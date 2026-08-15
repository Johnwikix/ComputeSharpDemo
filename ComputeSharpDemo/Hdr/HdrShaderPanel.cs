using ComputeSharp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ComputeSharpDemo.Hdr;

/// <summary>
/// A <see cref="SwapChainPanel"/> that renders animated compute shader frames with
/// optional HDR10 output, powered by <see cref="HdrSwapChainRenderer"/>.
/// </summary>
public sealed class HdrShaderPanel : SwapChainPanel, IDisposable
{
    private readonly HdrSwapChainRenderer _renderer;
    private bool _isPaused;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="HdrShaderPanel"/> instance bound to the given GPU device.
    /// </summary>
    public HdrShaderPanel(GraphicsDevice device)
    {
        _renderer = new HdrSwapChainRenderer(this, device);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        CompositionScaleChanged += OnCompositionScaleChanged;
    }

    /// <summary>
    /// Raised (on the UI thread) whenever the render thread reports a failure.
    /// </summary>
    public event EventHandler<Exception>? RenderingFailed;

    /// <summary>
    /// Raised (on the UI thread) once the swap chain has queried the DXGI output capabilities
    /// (after the first present) — e.g. to enable the HDR toggle.
    /// </summary>
    public event EventHandler? OutputCapabilitiesChanged;

    /// <summary>
    /// Gets whether the DXGI output hosting the panel is HDR-capable
    /// (queried from the hardware after the first present).
    /// </summary>
    public bool IsOutputHdrCapable => _renderer.OutputHdrCapable;

    /// <summary>
    /// Gets the peak luminance (nits) reported by the DXGI output, if available.
    /// </summary>
    public float OutputMaxLuminanceInNits => _renderer.OutputMaxLuminanceInNits;

    /// <summary>
    /// Gets or sets the shader runner producing the frames to display.
    /// </summary>
    public IHdrShaderRunner? ShaderRunner
    {
        get => _renderer.ShaderRunner;
        set => _renderer.ShaderRunner = value;
    }

    /// <summary>
    /// Gets or sets whether frame production is paused.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set => _isPaused = value;
    }

    /// <summary>
    /// Enables or disables HDR10 output (ST 2084 / BT.2020 color space).
    /// </summary>
    public bool IsHdrEnabled
    {
        get => _renderer.IsHdrEnabled;
        set => _renderer.SetHdrMode(value);
    }

    /// <summary>
    /// Sets the luminance mapping used for the HDR10 signal.
    /// </summary>
    /// <param name="sdrWhiteLevelInNits">Luminance (nits) that SDR white (1.0) maps to.</param>
    /// <param name="maxLuminanceInNits">Peak luminance (nits) of the display.</param>
    public void SetHdrParameters(float sdrWhiteLevelInNits, float maxLuminanceInNits)
        => _renderer.SetHdrParameters(sdrWhiteLevelInNits, maxLuminanceInNits);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _renderer.QueueResize(ActualWidth, ActualHeight);
        _renderer.QueueCompositionScaleChange(CompositionScaleX, CompositionScaleY);
        _renderer.StartRenderLoop();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderer.StopRenderLoop();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _renderer.QueueResize(e.NewSize.Width, e.NewSize.Height);
    }

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        _renderer.QueueCompositionScaleChange(CompositionScaleX, CompositionScaleY);
    }

    /// <summary>
    /// Forwards a render failure from the render thread to the UI thread.
    /// </summary>
    internal void OnRenderingFailed(Exception exception)
    {
        RenderingFailed?.Invoke(this, exception);
    }

    /// <summary>
    /// Forwards the DXGI output capability query result to the UI thread.
    /// </summary>
    internal void OnOutputCapabilitiesChanged()
    {
        OutputCapabilitiesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        CompositionScaleChanged -= OnCompositionScaleChanged;

        _renderer.Dispose();
    }
}
