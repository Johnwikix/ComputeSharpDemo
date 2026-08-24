using System.Diagnostics;
using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.Interop;
using Microsoft.UI.Dispatching;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Windows.Graphics;
using static Vortice.DXGI.ColorSpaceType;

namespace ComputeSharpDemo.Hdr;

/// <summary>
/// Renders frames produced by an <see cref="IHdrShaderRunner"/> onto a <see cref="HdrShaderPanel"/>
/// through a custom DXGI composition swap chain.
///
/// <para>
/// The swap chain uses an <c>R10G10B10A2_UNORM</c> back buffer (the standard HDR10 format) and can
/// switch its color space between sRGB (SDR) and ST 2084 / BT.2020 (HDR10). Shaders render into a
/// 16-bit frame texture, encoding their output with the PQ curve (HDR) or an sRGB gamma (SDR) before
/// writing it; a fullscreen pass then converts the frame texture into the swap chain back buffer.
/// </para>
/// <para>
/// Note: <c>R16G16B16A16_FLOAT</c> swap chains are rejected by <c>SetColorSpace1</c>
/// (E_INVALIDARG) on some Windows 11 builds with NVIDIA drivers, while R10G10B10A2 works everywhere.
/// </para>
/// </summary>
internal sealed unsafe class HdrSwapChainRenderer : IDisposable
{
    /// <summary>All subresources constant for resource barriers.</summary>
    private const uint AllSubresources = 0xFFFFFFFF;

    /// <summary>Infinite wait constant for <see cref="WaitForSingleObjectEx"/>.</summary>
    private const uint InfiniteWait = 0xFFFFFFFF;

    /// <summary>
    /// Maximum wait for the frame-latency waitable object before re-checking for a pending
    /// resize (bounds the wait so the render loop always stays responsive to UI changes).
    /// </summary>
    private const uint ResizePollIntervalMs = 100;

    /// <summary>
    /// Backoff between resize retries after a transient failure.
    /// </summary>
    private const long ResizeRetryIntervalMs = 500;

    /// <summary>IID of <c>ID3D12Device</c>.</summary>
    private static readonly Guid IID_ID3D12Device = new("189819F1-1DB6-4B57-BE54-1821339B85F7");

    /// <summary>IID of <c>ID3D12Resource</c> as defined by ComputeSharp's interop bindings.</summary>
    private static readonly Guid IID_ID3D12Resource = new("696442BE-A72E-4059-BC79-5B5C98040FAD");

    private readonly HdrShaderPanel _owner;
    private readonly GraphicsDevice _device;
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly SemaphoreSlim _setupSemaphore = new(1, 1);

    private ID3D12Device _d3D12Device = null!;
    private ID3D12CommandQueue _commandQueue = null!;
    private ID3D12Fence _fence = null!;
    private ID3D12CommandAllocator _commandAllocator = null!;
    private ID3D12GraphicsCommandList _commandList = null!;
    private IDXGISwapChain3 _swapChain = null!;
    private IDXGIFactory6 _dxgiFactory = null!;
    private IntPtr _frameLatencyWaitableObject;
    private IntPtr _fenceEvent;
    private ISwapChainPanelNative* _swapChainPanelNative;

    /// <summary>
    /// Swap chains retired by previous replacements, kept alive until the renderer is disposed
    /// (releasing them earlier freezes the panel's display on this system).
    /// </summary>
    private readonly List<IDXGISwapChain3> _retiredSwapChains = [];

    /// <summary>
    /// Signals that the panel has been detached from the swap chain (phase 1 of disposal).
    /// </summary>
    private readonly ManualResetEventSlim _detachDone = new(false);

    private HdrFullScreenPass _fullScreenPass = null!;
    private ID3D12DescriptorHeap _rtvHeap = null!;
    private ID3D12DescriptorHeap _srvHeap = null!;
    private int _rtvIncrementSize;
    private readonly ID3D12Resource[] _backBuffers = new ID3D12Resource[2];

    private ReadWriteTexture2D<Rgba64, Float4>? _frameBuffer;
    private ID3D12Resource? _frameResource;

    private volatile bool _isResizePending = true;
    private volatile float _width = 1;
    private volatile float _height = 1;
    private volatile bool _presentedSinceResize = true;
    private long _resizeRetryAt;
    private volatile bool _hdrMode;
    private volatile bool _colorSpaceApplied;
    private volatile float _sdrWhiteLevelInNits = 200;
    private volatile float _maxLuminanceInNits = 1000;

    private bool _currentOutputHdrCapable;
    private float _currentOutputMaxLuminanceInNits;
    private RectInt32 _windowBounds;

    private volatile IHdrShaderRunner? _shaderRunner;
    private volatile bool _isPaused;
    private CancellationTokenSource? _renderCancellationTokenSource;
    private Thread? _renderThread;
    private ulong _nextFenceValue;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="HdrSwapChainRenderer"/> instance.
    /// Must be called on the UI thread.
    /// </summary>
    public HdrSwapChainRenderer(HdrShaderPanel owner, GraphicsDevice device)
    {
        _owner = owner;
        _device = device;

        InitializeD3D12();
        InitializeSwapChain();
    }

    /// <summary>
    /// Gets or sets the shader runner used to produce frames.
    /// </summary>
    public IHdrShaderRunner? ShaderRunner
    {
        get => _shaderRunner;
        set => _shaderRunner = value;
    }

    /// <summary>
    /// Gets or sets whether the render loop should pause producing frames.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set => _isPaused = value;
    }

    /// <summary>
    /// Gets whether HDR10 output is currently active (may be rolled back if the display rejects it).
    /// </summary>
    public bool IsHdrEnabled => _hdrMode;

    /// <summary>
    /// Gets whether the DXGI output currently hosting the window is HDR-capable
    /// (queried from the hardware, independent of WinRT detection).
    /// </summary>
    public bool CurrentOutputHdrCapable => _currentOutputHdrCapable;

    /// <summary>
    /// Gets the peak luminance (nits) of the current output, if the query succeeded.
    /// </summary>
    public float CurrentOutputMaxLuminanceInNits => _currentOutputMaxLuminanceInNits;

    /// <summary>
    /// Switches the swap chain color space between SDR and HDR10 (ST 2084 / BT.2020).
    /// Can be called at any time, from any thread. Before the first present the color
    /// space is only recorded; the render thread applies it as soon as the swap chain
    /// is live (fresh composition swap chains reject <c>SetColorSpace1</c> calls).
    /// </summary>
    public void SetHdrMode(bool isHdrEnabled)
    {
        _hdrMode = isHdrEnabled;

        if (!_colorSpaceApplied)
        {
            return;
        }

        TryApplyColorSpace(isHdrEnabled);
    }

    /// <summary>
    /// Applies the color space matching <see cref="_hdrMode"/>, falling back to SDR if
    /// the display rejects HDR10. Never throws.
    /// </summary>
    private void TryApplyColorSpace(bool hdrEnabled)
    {
        ColorSpaceType colorSpace = hdrEnabled
            ? RgbFullG2084NoneP2020
            : RgbFullG22NoneP709;

        try
        {
            _swapChain.SetColorSpace1(colorSpace);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[HDR] SetColorSpace1({colorSpace}) failed: {e.Message}");

            // The display rejected the requested color space (e.g. HDR not actually available,
            // or the output changed mid-run): fall back to SDR.
            if (hdrEnabled)
            {
                _hdrMode = false;

                try
                {
                    _swapChain.SetColorSpace1(RgbFullG22NoneP709);
                }
                catch
                {
                    // Nothing else we can do - the previous color space remains in effect.
                }
            }
        }
    }

    /// <summary>
    /// Applies the pending color space after the first present. Runs on the render thread.
    /// </summary>
    private void EnsureColorSpaceApplied()
    {
        if (_colorSpaceApplied) return;

        TryApplyColorSpace(_hdrMode);

        // Even if both attempts failed (rare), stop retrying every frame.
        _colorSpaceApplied = true;
    }

    /// <summary>
    /// Records the window bounds (screen coordinates) used to determine which DXGI output
    /// currently hosts the window. Call whenever the window moves or resizes.
    /// </summary>
    public void SetWindowBounds(RectInt32 bounds)
    {
        _windowBounds = bounds;
    }

    /// <summary>
    /// Re-queries the DXGI outputs and updates the HDR state of the output currently hosting
    /// the window (the output whose <c>DesktopCoordinates</c> intersects the window bounds the
    /// most). If no window bounds are available yet, falls back to "any output is HDR" so that
    /// single-monitor HDR setups work from the first frame. Raises
    /// <see cref="HdrShaderPanel.OutputCapabilitiesChanged"/> when the state changes.
    /// Thread-safe (DXGI factory calls are safe from any thread).
    /// </summary>
    public void RecheckOutput()
    {
        try
        {
            RectInt32 bounds = _windowBounds;
            bool hasBounds = bounds.Width > 0 && bounds.Height > 0;

            bool anyHdr = false;
            bool currentHdr = false;
            float currentMaxLuminance = 0;
            float bestIntersection = -1;

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                Result adapterResult = _dxgiFactory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter);

                if (adapterResult.Failure)
                {
                    break; // DXGI_ERROR_NOT_FOUND: no more adapters
                }

                using (adapter)
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        Result outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput output);

                        if (outputResult.Failure)
                        {
                            break; // DXGI_ERROR_NOT_FOUND: no more outputs
                        }

                        using (output)
                        {
                            try
                            {
                                using IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>();
                                OutputDescription1 description = output6.Description1;
                                RawRect desktop = description.DesktopCoordinates;

                                bool isHdr = description.ColorSpace == RgbFullG2084NoneP2020;
                                anyHdr |= isHdr;

                                if (hasBounds)
                                {
                                    int interW = Math.Min(bounds.X + bounds.Width, desktop.Right)
                                               - Math.Max(bounds.X, desktop.Left);
                                    int interH = Math.Min(bounds.Y + bounds.Height, desktop.Bottom)
                                               - Math.Max(bounds.Y, desktop.Top);

                                    if (interW > 0 && interH > 0)
                                    {
                                        float intersection = (float)interW * interH;

                                        if (intersection > bestIntersection)
                                        {
                                            bestIntersection = intersection;
                                            currentHdr = isHdr;
                                            currentMaxLuminance = description.MaxLuminance;
                                        }
                                    }
                                }
                                else if (isHdr && description.MaxLuminance > currentMaxLuminance)
                                {
                                    currentMaxLuminance = description.MaxLuminance;
                                }

                                Debug.WriteLine(
                                    $"[HDR] output {adapterIndex}.{outputIndex}: " +
                                    $"colorSpace={description.ColorSpace} maxLum={description.MaxLuminance:0} " +
                                    $"desktop=({desktop.Left},{desktop.Top},{desktop.Right},{desktop.Bottom})");
                            }
                            catch (Exception e)
                            {
                                Debug.WriteLine($"[HDR] output {adapterIndex}.{outputIndex} query failed: {e.Message}");
                            }
                        }
                    }
                }
            }

            bool foundOutput = hasBounds && bestIntersection >= 0;
            bool newHdr;
            float newLuminance;

            if (foundOutput)
            {
                newHdr = currentHdr;
                newLuminance = currentMaxLuminance;
            }
            else if (!hasBounds)
            {
                // No window bounds yet (first frames): fall back to "any output is HDR".
                newHdr = anyHdr;
                newLuminance = Math.Max(currentMaxLuminance, _currentOutputMaxLuminanceInNits);
            }
            else
            {
                // The window is not on any enumerated output (e.g. minimized or off-screen):
                // keep the previous state.
                newHdr = _currentOutputHdrCapable;
                newLuminance = _currentOutputMaxLuminanceInNits;
            }

            bool changed = newHdr != _currentOutputHdrCapable || newLuminance != _currentOutputMaxLuminanceInNits;

            _currentOutputHdrCapable = newHdr;
            if (newLuminance > 0) _currentOutputMaxLuminanceInNits = newLuminance;

            Debug.WriteLine($"[HDR] current output: hdr={_currentOutputHdrCapable} maxLum={_currentOutputMaxLuminanceInNits:0} (bounds={hasBounds})");

            if (changed)
            {
                _ = _dispatcherQueue.TryEnqueue(() => _owner.OnOutputCapabilitiesChanged());
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[HDR] DXGI output enumeration failed: {e.Message}");
        }
    }

    /// <summary>
    /// Sets the luminance parameters used to map linear frame values into the HDR10 signal.
    /// </summary>
    /// <param name="sdrWhiteLevelInNits">Luminance (nits) that SDR white (1.0) maps to.</param>
    /// <param name="maxLuminanceInNits">Peak luminance (nits) of the display.</param>
    public void SetHdrParameters(float sdrWhiteLevelInNits, float maxLuminanceInNits)
    {
        if (sdrWhiteLevelInNits > 0) _sdrWhiteLevelInNits = sdrWhiteLevelInNits;
        if (maxLuminanceInNits > 0) _maxLuminanceInNits = maxLuminanceInNits;
    }

    /// <summary>
    /// Queues a resize of the render surface. Applies on the render thread at the next
    /// present opportunity (see <see cref="TryApplyPendingResize"/>).
    /// </summary>
    public void QueueResize(double width, double height)
    {
        _width = (float)Math.Max(width, 1);
        _height = (float)Math.Max(height, 1);
        _isResizePending = true;
    }

    /// <summary>
    /// Starts the render thread. Must be called on the UI thread.
    /// </summary>
    public void StartRenderLoop()
    {
        _setupSemaphore.Wait();

        try
        {
            if (_renderThread is not null)
            {
                return;
            }

            _renderCancellationTokenSource = new CancellationTokenSource();
            _renderThread = new Thread(static state => ((HdrSwapChainRenderer)state!).RenderThreadMain())
            {
                IsBackground = true,
                Name = "HdrSwapChainRenderer",
            };

            _renderThread.Start(this);
        }
        finally
        {
            _setupSemaphore.Release();
        }
    }

    /// <summary>
    /// Stops the render thread and waits for it to exit. Must be called on the UI thread.
    /// </summary>
    public void StopRenderLoop()
    {
        _setupSemaphore.Wait();

        try
        {
            if (_renderThread is null)
            {
                return;
            }

            _renderCancellationTokenSource!.Cancel();
            _renderThread.Join();
            _renderThread = null;
        }
        finally
        {
            _setupSemaphore.Release();
        }
    }

    /// <summary>
    /// The core render loop, running on a dedicated background thread.
    /// Every iteration is individually protected so that a transient failure in any
    /// single step (resize, dispatch or present) never stops rendering permanently.
    /// </summary>
    private void RenderThreadMain()
    {
        try
        {
            CancellationToken cancellationToken = _renderCancellationTokenSource!.Token;
            Stopwatch stopwatch = Stopwatch.StartNew();

            while (!cancellationToken.IsCancellationRequested)
            {

                try
                {
                    if (TryApplyPendingResize())
                    {
                        continue;
                    }

                    IHdrShaderRunner? runner = _shaderRunner;
                    ReadWriteTexture2D<Rgba64, Float4>? frameBuffer = _frameBuffer;

                    if (runner is null || frameBuffer is null || _isPaused)
                    {
                        Thread.Sleep(16);
                        continue;
                    }

                // Wait for the previous present to complete before touching the frame buffer again.
                // This also paces the render loop to the display refresh rate. The wait is bounded
                // so that a resize requested while waiting is still applied promptly even if the
                // waitable object misbehaves (returns on signal, otherwise after the timeout).
                WaitForSingleObjectEx(_frameLatencyWaitableObject, ResizePollIntervalMs, true);

                if (TryApplyPendingResize())
                {
                    continue;
                }

                HdrRenderParameters parameters = new(
                    IsHdrEnabled: _hdrMode,
                    SdrWhiteLevelInNits: _sdrWhiteLevelInNits,
                    MaxLuminanceInNits: _maxLuminanceInNits);

                if (!runner.TryExecute(frameBuffer, frameBuffer.Width, frameBuffer.Height, stopwatch.Elapsed, parameters))
                {
                    continue;
                }

                PresentFrame(frameBuffer);
            }
            catch (Exception e)
            {
                // A single failed iteration must never kill the render loop.
                Debug.WriteLine($"[HDR] render iteration failed: {e}");

                Thread.Sleep(250);
            }
            }
        }
        catch (Exception e)
        {
            _ = _dispatcherQueue.TryEnqueue(() => _owner.OnRenderingFailed(e));
        }
    }

    /// <summary>
    /// Applies a pending resize at the next present opportunity. The only gating constraint
    /// is the DXGI flip-model requirement that consecutive <c>ResizeBuffers</c> calls are
    /// separated by at least one present; rapid resize events simply update the pending size,
    /// which coalesces naturally into one apply per present.
    /// </summary>
    /// <returns>Whether a resize was applied by this call.</returns>
    private bool TryApplyPendingResize()
    {
        if (!_isResizePending)
        {
            return false;
        }

        string? blocked = null;

        // DXGI requires at least one Present between two consecutive ResizeBuffers calls.
        if (!_presentedSinceResize)
        {
            blocked = "no-present-since-resize";
        }

        // Back off after a previous failure.
        if (blocked is null && Environment.TickCount64 < _resizeRetryAt)
        {
            blocked = "backoff";
        }

        if (blocked is not null)
        {
            return false;
        }

        return ApplyResize();
    }

    /// <summary>
    /// Creates the D3D12 device wrapper, command queue, fence, command list and the fullscreen pass.
    /// </summary>
    private void InitializeD3D12()
    {
        // Wrap the ID3D12Device that ComputeSharp is already using, so that the frame texture
        // allocated by ComputeSharp and our swap chain/pipeline live on the same device.
        Guid deviceIid = IID_ID3D12Device;
        IntPtr devicePointer = IntPtr.Zero;

        InteropServices.GetID3D12Device(_device, &deviceIid, (void**)&devicePointer);

        _d3D12Device = new ID3D12Device(devicePointer);

        _commandQueue = _d3D12Device.CreateCommandQueue(
            new CommandQueueDescription(CommandListType.Direct, CommandQueuePriority.Normal, CommandQueueFlags.None, 0));

        _fence = _d3D12Device.CreateFence(0, FenceFlags.None);
        _commandAllocator = _d3D12Device.CreateCommandAllocator(CommandListType.Direct);
        _commandList = _d3D12Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _commandAllocator, null);

        // Command lists are created in the "recording" state: close it so that the
        // allocator can be reset on the first present.
        _commandList.Close();

        _fullScreenPass = new HdrFullScreenPass(_d3D12Device);

        _rtvHeap = _d3D12Device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2, DescriptorHeapFlags.None, 0));

        _srvHeap = _d3D12Device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible, 0));

        _rtvIncrementSize = (int)_d3D12Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        // Event used by SignalAndWait() to actually block until the GPU is idle.
        _fenceEvent = CreateEventW(IntPtr.Zero, false, false, null);
    }

    /// <summary>
    /// Creates the DXGI factory and the composition swap chain, then attaches it to the panel.
    /// </summary>
    private void InitializeSwapChain()
    {
        _dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(debug: false);

        _swapChainPanelNative = SwapChainPanelNativeMarshaller.GetNativeObject(_owner);

        _swapChain = CreateSwapChain(width: 1, height: 1);
        _frameLatencyWaitableObject = _swapChain.FrameLatencyWaitableObject;

        // Attach the swap chain to the hosting panel.
        // Note: the color space is intentionally NOT set here — a freshly created composition
        // swap chain rejects SetColorSpace1 (E_INVALIDARG). It is applied by the render thread
        // right after the first present, when the swap chain is fully initialized.
        _swapChainPanelNative->SetSwapChain((void*)_swapChain.NativePointer).ThrowIfFailed();
    }

    /// <summary>
    /// Creates a composition swap chain with the given buffer size.
    /// </summary>
    private IDXGISwapChain3 CreateSwapChain(uint width, uint height)
    {
        SwapChainDescription1 description = new(
            width: width,
            height: height,
            format: Format.R10G10B10A2_UNorm,
            stereo: false,
            bufferUsage: Usage.RenderTargetOutput,
            bufferCount: 2,
            scaling: Scaling.Stretch,
            swapEffect: SwapEffect.FlipSequential,
            alphaMode: AlphaMode.Ignore,
            flags: SwapChainFlags.FrameLatencyWaitableObject);

        using IDXGISwapChain1 swapChain1 = _dxgiFactory.CreateSwapChainForComposition(_commandQueue, description, null);

        return swapChain1.QueryInterface<IDXGISwapChain3>();
    }

    /// <summary>
    /// Replaces the current swap chain with a new one at the given size. Must be called from
    /// the render thread; the panel binding is performed on the UI thread via the dispatcher
    /// (the new chain is created and bound first, then the old one is released).
    ///
    /// <para>
    /// Recreating the swap chain is required because <c>ResizeBuffers</c> on a swap chain
    /// attached to a <see cref="SwapChainPanel"/> either silently defers on this system
    /// (output stays locked at the previous size) or freezes the panel's display entirely.
    /// </para>
    /// </summary>
    /// <returns>Whether the swap chain was replaced.</returns>
    private bool ReplaceSwapChain(uint width, uint height)
    {
        using ManualResetEventSlim done = new(false);

        Exception? swapError = null;
        bool swapped = false;

        bool enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            IDXGISwapChain3? newSwapChain = null;

            try
            {
                newSwapChain = CreateSwapChain(width, height);

                // The panel may already be detached while the app is closing; the swap chain
                // binding is gone, so there is nothing to rebind and the old chain must stay
                // with the renderer (it is released by the background disposal).
                if (_swapChainPanelNative is null)
                {
                    throw new InvalidOperationException("Swap chain panel is already detached.");
                }

                // Rebind the panel to the new chain before releasing the old one.
                _swapChainPanelNative->SetSwapChain((void*)newSwapChain.NativePointer).ThrowIfFailed();

                // Defer the old chain's disposal: releasing a swap chain that was attached to
                // the panel freezes the panel's display on this system, so retired chains are
                // kept alive (and released together with the renderer).
                _retiredSwapChains.Add(_swapChain);

                // Ownership of the new chain transfers to the renderer.
                _swapChain = newSwapChain;
                newSwapChain = null;

                _frameLatencyWaitableObject = _swapChain.FrameLatencyWaitableObject;

                // Fill the first back buffer with the last rendered frame and present it once,
                // so the compositor samples real content instead of the new chain's undefined
                // back buffers (which would show as a black flash) before the render thread's
                // first present.
                FillNewChainWithLastFrame((int)width, (int)height);

                // The new chain starts in SDR: the color space is re-applied after its first present.
                _colorSpaceApplied = false;

                swapped = true;
            }
            catch (Exception e)
            {
                swapError = e;
            }
            finally
            {
                newSwapChain?.Dispose();

                done.Set();
            }
        });

        // The render thread must not touch the swap chain until the replacement is complete,
        // so the wait is unbounded: a timed-out wait would let the render thread present on
        // a chain the UI thread is about to dispose (access violation).
        if (!enqueued)
        {
            return false;
        }

        done.Wait();

        if (swapError is not null)
        {
            throw swapError;
        }

        return swapped;
    }

    /// <summary>
    /// Blits the last rendered frame into buffer 0 of the (already bound) new swap chain and
    /// presents it once, so the compositor samples real content instead of the new chain's
    /// undefined back buffers before the render thread issues its first present. Runs on the
    /// UI thread inside <see cref="ReplaceSwapChain"/> while the render thread is blocked in
    /// <c>done.Wait()</c>, so the command list is not contended and the GPU is idle
    /// (<see cref="ApplyResize"/> drains it before replacing the chain). On failure the chain
    /// stays bound and the previous behavior (brief undefined content) applies.
    /// </summary>
    private void FillNewChainWithLastFrame(int width, int height)
    {
        // Nothing to blit on the very first resize (no frame has been rendered yet).
        if (_frameResource is null)
        {
            return;
        }

        ID3D12Resource? backBuffer = null;

        try
        {
            backBuffer = _swapChain.GetBuffer<ID3D12Resource>(0);

            // Heap slot 0 still references the retired chain's (disposed) back buffer: recreate
            // the RTV for the new chain. ApplyResize recreates both slots right afterwards.
            CpuDescriptorHandle rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
            _d3D12Device.CreateRenderTargetView(backBuffer, null, rtvHandle);

            _commandAllocator.Reset();
            _commandList.Reset(_commandAllocator, null);

            _commandList.ResourceBarrierTransition(
                _frameResource, ResourceStates.UnorderedAccess, ResourceStates.PixelShaderResource, AllSubresources, ResourceBarrierFlags.None);

            _commandList.ResourceBarrierTransition(
                backBuffer, ResourceStates.Common, ResourceStates.RenderTarget, AllSubresources, ResourceBarrierFlags.None);

            _commandList.SetDescriptorHeaps(_srvHeap);
            _commandList.SetGraphicsRootSignature(_fullScreenPass.RootSignature);
            _commandList.SetGraphicsRootDescriptorTable(0, _srvHeap.GetGPUDescriptorHandleForHeapStart());
            _commandList.OMSetRenderTargets(rtvHandle, null);
            _commandList.RSSetViewport(0, 0, width, height, 0, 1);
            _commandList.RSSetScissorRect(width, height);
            _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _commandList.SetPipelineState(_fullScreenPass.PipelineState);
            _commandList.DrawInstanced(3, 1, 0, 0);

            _commandList.ResourceBarrierTransition(
                _frameResource, ResourceStates.PixelShaderResource, ResourceStates.UnorderedAccess, AllSubresources, ResourceBarrierFlags.None);

            _commandList.ResourceBarrierTransition(
                backBuffer, ResourceStates.RenderTarget, ResourceStates.Common, AllSubresources, ResourceBarrierFlags.None);

            _commandList.Close();

            _commandQueue.ExecuteCommandLists(new ID3D12CommandList[] { _commandList });

            _swapChain.Present(0, PresentFlags.None);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[HDR] First-frame fill failed: {e}");
        }
        finally
        {
            backBuffer?.Dispose();
        }
    }

    /// <summary>
    /// Applies a pending resize: replaces the swap chain, recreates the render targets and the frame texture.
    /// Runs on the render thread. On failure the resize stays pending and is retried after
    /// <see cref="ResizeRetryIntervalMs"/> — a transient DXGI error must never kill rendering.
    /// </summary>
    /// <returns>Whether the resize was applied successfully.</returns>
    private bool ApplyResize()
    {
        _isResizePending = false;

        // The size is already in physical pixels (the UI sizes the panel to the physical
        // pixel size, see the bug #8219 workaround in MainWindow).
        int width = (int)Math.Min(Math.Max(Math.Ceiling(_width), 1), 16384);
        int height = (int)Math.Min(Math.Max(Math.Ceiling(_height), 1), 16384);

        try
        {
            // DXGI flip-model back buffer objects must not outlive their swap chain: release
            // them before the chain is replaced below.
            for (int i = 0; i < _backBuffers.Length; i++)
            {
                _backBuffers[i]?.Dispose();
                _backBuffers[i] = null;
            }

            // Make sure no pending GPU work references the buffers we're about to recreate.
            SignalAndWait();

            // ResizeBuffers is unreliable on a swap chain attached to a SwapChainPanel on
            // this system (it silently defers or freezes the panel's display). Recreate the
            // whole swap chain instead — the panel binding happens on the UI thread; the new
            // chain is created and bound before the old one is released.
            if (!ReplaceSwapChain((uint)width, (uint)height))
            {
                throw new InvalidOperationException("Failed to replace the swap chain (dispatcher unavailable).");
            }

            SwapChainDescription1 swapChainDesc = _swapChain.Description1;

            if (swapChainDesc.Width != (uint)width || swapChainDesc.Height != (uint)height)
            {
                throw new InvalidOperationException(
                    $"Swap chain replace failed: requested {width}x{height}, got {swapChainDesc.Width}x{swapChainDesc.Height}.");
            }

            // No matrix transform is applied: the panel presents the swapchain buffer at
            // 1 buffer pixel = 1 DIP (WinUI bug #8219), and the panel itself is sized to
            // the physical pixel size, so the buffer maps 1:1 onto the panel.

            // Resizing may reset the color space on some drivers: re-apply it once the swap
            // chain has already been initialized (the first application happens after the
            // first present, never on a fresh swap chain).
            if (_colorSpaceApplied)
            {
                TryApplyColorSpace(_hdrMode);
            }

            CpuDescriptorHandle rtvHeapStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();

            for (int i = 0; i < _backBuffers.Length; i++)
            {
                _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);

                _d3D12Device.CreateRenderTargetView(_backBuffers[i], null, rtvHeapStart + i * _rtvIncrementSize);
            }

            // Recreate the frame texture and its shader resource view.
            _frameResource?.Dispose();
            _frameBuffer?.Dispose();

            _frameBuffer = _device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);

            Guid resourceIid = IID_ID3D12Resource;
            IntPtr resourcePointer = IntPtr.Zero;

            InteropServices.GetID3D12Resource(_frameBuffer, &resourceIid, (void**)&resourcePointer);

            _frameResource = new ID3D12Resource(resourcePointer);

            // Use a null description so the runtime infers it from the resource
            // (Texture2D, R16G16B16A16_UNORM, mip 0) — explicit Vortice descriptions
            // have a layout that crashes the native call.
            _d3D12Device.CreateShaderResourceView(_frameResource, null, _srvHeap.GetCPUDescriptorHandleForHeapStart());

            _presentedSinceResize = false;

            return true;
        }
        catch (Exception e)
        {
            // Keep the resize pending and back off: the render loop keeps rendering at the
            // last good size in the meantime. If the frame texture was already replaced,
            // the next retry recreates the whole chain again.
            Debug.WriteLine($"[HDR] ApplyResize {width}x{height} failed: {e}");

            _isResizePending = true;
            _resizeRetryAt = Environment.TickCount64 + ResizeRetryIntervalMs;

            return false;
        }
    }
    /// <summary>
    /// Runs the fullscreen conversion pass and presents the frame.
    /// </summary>
    private void PresentFrame(ReadWriteTexture2D<Rgba64, Float4> frameBuffer)
    {
        ID3D12Resource backBuffer = _backBuffers[_swapChain.CurrentBackBufferIndex];

        _commandAllocator.Reset();
        _commandList.Reset(_commandAllocator, null);

        // Transition the frame texture and the back buffer for the fullscreen pass
        _commandList.ResourceBarrierTransition(
            _frameResource!, ResourceStates.UnorderedAccess, ResourceStates.PixelShaderResource, AllSubresources, ResourceBarrierFlags.None);

        _commandList.ResourceBarrierTransition(
            backBuffer, ResourceStates.Common, ResourceStates.RenderTarget, AllSubresources, ResourceBarrierFlags.None);

        // Bind the frame texture SRV
        _commandList.SetDescriptorHeaps(_srvHeap);
        _commandList.SetGraphicsRootSignature(_fullScreenPass.RootSignature);
        _commandList.SetGraphicsRootDescriptorTable(0, _srvHeap.GetGPUDescriptorHandleForHeapStart());

        _commandList.OMSetRenderTargets(_rtvHeap.GetCPUDescriptorHandleForHeapStart() + (int)_swapChain.CurrentBackBufferIndex * _rtvIncrementSize, null);
        _commandList.RSSetViewport(0, 0, frameBuffer.Width, frameBuffer.Height, 0, 1);
        _commandList.RSSetScissorRect(frameBuffer.Width, frameBuffer.Height);
        _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _commandList.SetPipelineState(_fullScreenPass.PipelineState);
        _commandList.DrawInstanced(3, 1, 0, 0);

        // Transition both resources back
        _commandList.ResourceBarrierTransition(
            _frameResource!, ResourceStates.PixelShaderResource, ResourceStates.UnorderedAccess, AllSubresources, ResourceBarrierFlags.None);

        _commandList.ResourceBarrierTransition(
            backBuffer, ResourceStates.RenderTarget, ResourceStates.Common, AllSubresources, ResourceBarrierFlags.None);

        _commandList.Close();

        _commandQueue.ExecuteCommandLists(new ID3D12CommandList[] { _commandList });

        _swapChain.Present(0, PresentFlags.None);

        // Retired swap chains are NOT disposed here: on this system releasing a swap chain
        // that was attached to the panel (even long after the swap) freezes the panel's
        // display. They are kept alive until the renderer is disposed.

        // A present has now been issued since the last resize (if any).
        _presentedSinceResize = true;

        // After the first present the swap chain is fully initialized: apply the pending
        // color space (HDR toggle or detection result) and re-query the output capabilities.
        if (!_colorSpaceApplied)
        {
            EnsureColorSpaceApplied();
            RecheckOutput();
        }
    }

    /// <summary>
    /// Signals the render queue fence and blocks the calling thread until the GPU is idle.
    /// </summary>
    private void SignalAndWait()
    {
        ulong value = ++_nextFenceValue;

        _commandQueue.Signal(_fence, value);

        if (_fence.CompletedValue < value)
        {
            // A NULL event handle makes SetEventOnCompletion fail silently (the HRESULT used
            // to be ignored), so this call returned without waiting and teardown could dispose
            // GPU objects while work was still in flight. Also crashes dwm.exe, which keeps
            // compositing the last-presented swap chain buffers of a torn-down device.
            _fence.SetEventOnCompletion(value, _fenceEvent).CheckError();

            WaitForSingleObjectEx(_fenceEvent, InfiniteWait, true);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(null);
    }

    /// <summary>
    /// Disposes the renderer.
    /// </summary>
    /// <param name="beforeDeviceDispose">
    /// Optional callback invoked on the background teardown thread after the render thread has
    /// stopped and the GPU is idle, but before the underlying D3D12 device is disposed (used
    /// to release runner resources that depend on the device).
    /// </param>
    public void Dispose(Action? beforeDeviceDispose)
    {
        if (_disposed) return;
        _disposed = true;

        // Phase 1 (UI thread, caller): release the panel's reference to the swap chain.
        // This is the only part that requires the UI thread and must complete before
        // the swap chains are released below.
        if (_dispatcherQueue.HasThreadAccess)
        {
            DetachSwapChain();
            _detachDone.Set();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    DetachSwapChain();
                }
                finally
                {
                    _detachDone.Set();
                }
            });
        }

        // Phase 2 (background thread): all blocking teardown — render thread join, GPU
        // drain and swap chain disposal can take seconds (compositor retirement, large
        // shader dispatches), so closing the window must not block on them.
        ThreadPool.QueueUserWorkItem(_ => DisposeResources(beforeDeviceDispose));
    }

    /// <summary>
    /// Performs the blocking part of the disposal on a background thread.
    /// </summary>
    /// <param name="beforeDeviceDispose">
    /// Optional callback invoked after the render thread has stopped and the GPU is idle,
    /// but before the underlying D3D12 device is disposed (used to release resources that
    /// depend on the device, such as the pass textures).
    /// </param>
    private void DisposeResources(Action? beforeDeviceDispose)
    {
        try
        {
            // Best effort wait for the UI-thread detach (in practice it already ran).
            _detachDone.Wait(TimeSpan.FromSeconds(2));

            StopRenderLoop();

            SignalAndWait();

            // Drain the composition pipeline: wait for DWM to retire the last-presented back
            // buffers before they and the swap chains are released below. Releasing them while
            // the compositor still references them tears down the device under dwm.exe.
            for (int i = 0; i < 2 && _frameLatencyWaitableObject != IntPtr.Zero; i++)
            {
                WaitForSingleObjectEx(_frameLatencyWaitableObject, 1000, true);
            }

            for (int i = 0; i < _backBuffers.Length; i++)
            {
                _backBuffers[i]?.Dispose();
            }

            _frameResource?.Dispose();
            _frameBuffer?.Dispose();
            _srvHeap.Dispose();
            _rtvHeap.Dispose();
            _fullScreenPass.Dispose();
            _commandList.Dispose();
            _commandAllocator.Dispose();
            _fence.Dispose();
            _commandQueue.Dispose();
            foreach (IDXGISwapChain3 retired in _retiredSwapChains)
            {
                retired.Dispose();
            }
            _retiredSwapChains.Clear();
            _swapChain.Dispose();
            _dxgiFactory.Dispose();

            // Release the runner-owned resources (pass textures) while the render thread is
            // stopped and the GPU is idle, but before the D3D12 device is torn down.
            beforeDeviceDispose?.Invoke();

            // Release the reference we obtained on the underlying D3D12 device. The device
            // itself is owned by the ComputeSharp GraphicsDevice instance, which is disposed
            // here as well — it must happen after the render thread has exited so its queues
            // and fences are not torn down while a dispatch could still be in flight.
            _d3D12Device.Dispose();
            _device.Dispose();

            if (_fenceEvent != IntPtr.Zero)
            {
                CloseHandle(_fenceEvent);
                _fenceEvent = IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[HDR] resource disposal failed: {e}");
        }
    }

    /// <summary>
    /// Detaches the swap chain from the panel and releases the panel native interface.
    /// </summary>
    private void DetachSwapChain()
    {
        if (_swapChainPanelNative is null) return;

        _swapChainPanelNative->SetSwapChain(null).ThrowIfFailed();
        _swapChainPanelNative->Release();
        _swapChainPanelNative = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObjectEx(IntPtr hObject, uint dwMilliseconds, bool bAlertable);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateEventW")]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Extensions for raw <c>HRESULT</c> values returned by our interop declarations.
/// </summary>
internal static class HresultExtensions
{
    /// <summary>
    /// Throws if the input <c>HRESULT</c> indicates a failure.
    /// </summary>
    public static void ThrowIfFailed(this int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }
}





