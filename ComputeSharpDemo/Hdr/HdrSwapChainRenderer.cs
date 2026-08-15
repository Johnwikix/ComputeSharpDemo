using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharp.Interop;
using Microsoft.UI.Dispatching;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
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

    /// <summary>IID of <c>ID3D12Device</c>.</summary>
    private static readonly Guid IID_ID3D12Device = new("189819F1-1DB6-4B57-BE54-1821339B85F7");

    /// <summary>IID of <c>ID3D12Resource</c> as defined by ComputeSharp's interop bindings.</summary>
    private static readonly Guid IID_ID3D12Resource = new("696442BE-A72E-4059-BC79-5B5C98040FAD");

    private static readonly Matrix3x2 IdentityTransform = new(1, 0, 0, 1, 0, 0);

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
    private ISwapChainPanelNative* _swapChainPanelNative;

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
    private volatile float _compositionScaleX = 1;
    private volatile float _compositionScaleY = 1;
    private volatile bool _hdrMode;
    private volatile bool _colorSpaceApplied;
    private volatile float _sdrWhiteLevelInNits = 200;
    private volatile float _maxLuminanceInNits = 1000;

    private volatile bool _outputHdrCapable;
    private volatile float _outputMaxLuminanceInNits;

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
    /// Gets whether the DXGI output hosting the swap chain is currently HDR-capable
    /// (queried from the hardware after the first present, independent of WinRT detection).
    /// </summary>
    public bool OutputHdrCapable => _outputHdrCapable;

    /// <summary>
    /// Gets the peak luminance (nits) of the DXGI output, if the query succeeded.
    /// </summary>
    public float OutputMaxLuminanceInNits => _outputMaxLuminanceInNits;

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
    /// Queries the DXGI outputs of all adapters to detect HDR capability and luminance data
    /// directly from the hardware (composition swap chains do not support
    /// <c>IDXGISwapChain::GetContainingOutput</c>, so all outputs are enumerated instead).
    /// Runs on the render thread after the first present.
    /// </summary>
    private void QueryOutputCapabilities()
    {
        try
        {
            bool anyHdr = false;
            float maxLuminance = 0;

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

                                if (description.ColorSpace == RgbFullG2084NoneP2020)
                                {
                                    anyHdr = true;
                                }

                                if (description.MaxLuminance > maxLuminance)
                                {
                                    maxLuminance = description.MaxLuminance;
                                }

                                Debug.WriteLine(
                                    $"[HDR] DXGI output {adapterIndex}.{outputIndex}: " +
                                    $"colorSpace={description.ColorSpace} maxLum={description.MaxLuminance:0} " +
                                    $"bitsPerColor={description.BitsPerColor}");
                            }
                            catch (Exception e)
                            {
                                Debug.WriteLine($"[HDR] DXGI output {adapterIndex}.{outputIndex} query failed: {e.Message}");
                            }
                        }
                    }
                }
            }

            _outputHdrCapable = anyHdr;
            if (maxLuminance > 0) _outputMaxLuminanceInNits = maxLuminance;

            Debug.WriteLine($"[HDR] DXGI outputs: hdr={anyHdr} maxLum={maxLuminance:0}");

            _ = _dispatcherQueue.TryEnqueue(() => _owner.OnOutputCapabilitiesChanged());
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
    /// Queues a resize of the render surface. Applies on the render thread.
    /// </summary>
    public void QueueResize(double width, double height)
    {
        _width = (float)Math.Max(width, 1);
        _height = (float)Math.Max(height, 1);
        _isResizePending = true;
    }

    /// <summary>
    /// Queues a change of the composition scale factors.
    /// </summary>
    public void QueueCompositionScaleChange(double scaleX, double scaleY)
    {
        _compositionScaleX = (float)scaleX;
        _compositionScaleY = (float)scaleY;
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
    /// </summary>
    private void RenderThreadMain()
    {
        try
        {
            CancellationToken cancellationToken = _renderCancellationTokenSource!.Token;
            Stopwatch stopwatch = Stopwatch.StartNew();

            while (!cancellationToken.IsCancellationRequested)
            {
                if (_isResizePending)
                {
                    ApplyResize();
                }

                IHdrShaderRunner? runner = _shaderRunner;
                ReadWriteTexture2D<Rgba64, Float4>? frameBuffer = _frameBuffer;

                if (runner is null || frameBuffer is null || _isPaused)
                {
                    Thread.Sleep(16);
                    continue;
                }

                // Wait for the previous present to complete before touching the frame buffer again.
                // This also paces the render loop to the display refresh rate.
                WaitForSingleObjectEx(_frameLatencyWaitableObject, InfiniteWait, true);

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
        }
        catch (Exception e)
        {
            _ = _dispatcherQueue.TryEnqueue(() => _owner.OnRenderingFailed(e));
        }
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
    }

    /// <summary>
    /// Creates the DXGI factory and the composition swap chain, then attaches it to the panel.
    /// </summary>
    private void InitializeSwapChain()
    {
        _dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(debug: false);

        SwapChainDescription1 description = new(
            width: 1,
            height: 1,
            format: Format.R10G10B10A2_UNorm,
            stereo: false,
            bufferUsage: Usage.RenderTargetOutput,
            bufferCount: 2,
            scaling: Scaling.Stretch,
            swapEffect: SwapEffect.FlipSequential,
            alphaMode: AlphaMode.Ignore,
            flags: SwapChainFlags.FrameLatencyWaitableObject);

        using IDXGISwapChain1 swapChain1 = _dxgiFactory.CreateSwapChainForComposition(_commandQueue, description, null);

        _swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();

        // Start in SDR mode; SetHdrMode is called again once HDR detection completes.
        SetHdrMode(isHdrEnabled: false);

        _frameLatencyWaitableObject = _swapChain.FrameLatencyWaitableObject;

        // Attach the swap chain to the hosting panel.
        // Note: the color space is intentionally NOT set here 鈥?a freshly created composition
        // swap chain rejects SetColorSpace1 (E_INVALIDARG). It is applied by the render thread
        // right after the first present, when the swap chain is fully initialized.
        _swapChainPanelNative = SwapChainPanelNativeMarshaller.GetNativeObject(_owner);

        _swapChainPanelNative->SetSwapChain((void*)_swapChain.NativePointer).ThrowIfFailed();
    }

    /// <summary>
    /// Applies a pending resize: resizes the swap chain, recreates the render targets and the frame texture.
    /// Runs on the render thread.
    /// </summary>
    private void ApplyResize()
    {
        _isResizePending = false;

        int width = (int)Math.Min(Math.Max(Math.Ceiling(_width * _compositionScaleX), 1), 16384);
        int height = (int)Math.Min(Math.Max(Math.Ceiling(_height * _compositionScaleY), 1), 16384);

        // Make sure no pending GPU work references the buffers we're about to recreate.
        SignalAndWait();

        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.R10G10B10A2_UNorm, SwapChainFlags.FrameLatencyWaitableObject);

        float inverseScaleX = _compositionScaleX != 0 ? 1 / _compositionScaleX : 1;
        float inverseScaleY = _compositionScaleY != 0 ? 1 / _compositionScaleY : 1;

        _swapChain.MatrixTransform = inverseScaleX == 1 && inverseScaleY == 1
            ? IdentityTransform
            : new Matrix3x2(inverseScaleX, 0, 0, inverseScaleY, 0, 0);

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
            _backBuffers[i]?.Dispose();

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
        // (Texture2D, R16G16B16A16_UNORM, mip 0) 鈥?explicit Vortice descriptions
        // have a layout that crashes the native call.
        _d3D12Device.CreateShaderResourceView(_frameResource, null, _srvHeap.GetCPUDescriptorHandleForHeapStart());
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

        // After the first present the swap chain is fully initialized: apply the pending
        // color space (HDR toggle or detection result) and query the output capabilities.
        if (!_colorSpaceApplied)
        {
            EnsureColorSpaceApplied();
            QueryOutputCapabilities();
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
            _fence.SetEventOnCompletion(value, IntPtr.Zero);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopRenderLoop();

        // Must run on the UI thread: releases the swap chain the panel is holding onto.
        if (_dispatcherQueue.HasThreadAccess)
        {
            DetachSwapChain();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(DetachSwapChain);
        }

        SignalAndWait();

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
        _swapChain.Dispose();
        _dxgiFactory.Dispose();

        // Release the reference we obtained on the underlying D3D12 device. The device itself
        // is still owned by the ComputeSharp GraphicsDevice instance.
        _d3D12Device.Dispose();
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


