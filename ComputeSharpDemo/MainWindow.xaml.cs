using ComputeSharp;
using ComputeSharpDemo.Hdr;
using ComputeSharpDemo.Shaders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using Windows.Foundation;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;

namespace ComputeSharpDemo;

public sealed partial class MainWindow : Window
{
    private GraphicsDevice _device = null!;
    private ShaderFactory _factory = null!;
    private HdrShaderPanel _shaderPanel = null!;
    private HdrDisplayInfoTracker? _hdrTracker;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _fpsBaseline;
    private IShaderPass? _activePass;
    private bool _hdrAutoEnabled;
    private bool _hdrDetectionInitialized;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        Title = "ComputeSharp Demo";

        // Create the GPU device and shader panel
        _device = GraphicsDevice.GetDefault();
        _factory = new ShaderFactory();

        _shaderPanel = new HdrShaderPanel(_device);

        Grid.SetRow(_shaderPanel, 1);
        RootGrid.Children.Add(_shaderPanel);

        // Populate shader selector
        ShaderSelector.ItemsSource = ShaderFactory.Catalog;
        ShaderSelector.SelectedIndex = 0;

        // Mouse tracking
        _shaderPanel.PointerMoved += OnPointerMoved;
        _shaderPanel.PointerWheelChanged += OnPointerWheelChanged;
        _shaderPanel.SizeChanged += OnShaderPanelSizeChanged;
        _shaderPanel.RenderingFailed += OnRenderingFailed;
        _shaderPanel.OutputCapabilitiesChanged += OnOutputCapabilitiesChanged;

        // FPS counter (poll pass frame count every 500ms)
        var fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        fpsTimer.Tick += (_, _) =>
        {
            int total = _activePass?.TotalFrames ?? 0;
            int fps = (int)((total - _fpsBaseline) / 0.5);
            FrameCountText.Text = $"FPS: {fps}";
            _fpsBaseline = total;
        };
        fpsTimer.Start();

        // HDR detection is deferred until the window is activated: DisplayInformation
        // is not reliably available while the window is still being constructed.
        Activated += OnWindowActivated;

        // Initial toolbar state (refreshed once detection + the DXGI output query complete)
        HdrStatusText.Text = "HDR: 检测中…";
        HdrToggle.IsEnabled = false;
        ApplyHdrMode();

        // Cleanup
        Closed += (_, _) => Dispose();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_hdrDetectionInitialized) return;
        _hdrDetectionInitialized = true;

        // Detect HDR support and keep tracking display changes (monitor switch, Windows HDR toggle...)
        try
        {
            _hdrTracker = HdrDisplayInfoTracker.Create();
            _hdrTracker.Changed += OnHdrStateChanged;
        }
        catch (Exception ex)
        {
            _hdrTracker = null;

            Debug.WriteLine($"[HDR] DisplayInformation unavailable: {ex.Message}");
        }

        UpdateHdrUi();
    }

    private void OnShaderSelected(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is ShaderAuthoringInfo info)
        {
            SwitchShader(info);
        }
    }

    private void SwitchShader(ShaderAuthoringInfo info)
    {
        // Dispose the old pass entirely (destroys its GPU resources).
        _activePass?.Dispose();

        var pass = _factory.GetOrCreate(info.Id);
        Int2 size = default;

        if (_shaderPanel.ActualWidth > 0 && _shaderPanel.ActualHeight > 0)
        {
            size = new Int2((int)_shaderPanel.ActualWidth, (int)_shaderPanel.ActualHeight);
        }

        pass.Initialize(_device, size);
        pass.OnResize(size);

        _shaderPanel.ShaderRunner = pass;
        _shaderPanel.IsPaused = false;
        _activePass = pass;

        AuthorText.Text = info.DisplayName;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activePass?.Capabilities.HasFlag(ShaderCapabilities.UsesMouse) == true)
        {
            var pointerPoint = e.GetCurrentPoint(_shaderPanel);
            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                _activePass.SetMouse(
                    (float)pointerPoint.Position.X,
                    (float)pointerPoint.Position.Y,
                    (float)_shaderPanel.ActualWidth,
                    (float)_shaderPanel.ActualHeight);
            }
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_activePass is not null)
        {
            var pointerPoint = e.GetCurrentPoint(_shaderPanel);
            float delta = (float)pointerPoint.Properties.MouseWheelDelta / 120.0f;
            _activePass.SetZoom(delta);
        }
    }

    private void OnShaderPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_activePass is null) return;

        var size = new Int2((int)e.NewSize.Width, (int)e.NewSize.Height);
        _activePass.OnResize(size);
    }

    // Re-evaluates HDR whenever the display configuration changes (HDR toggle in
    // Windows settings, monitor switch, ...).
    private void OnHdrStateChanged(object? sender, HdrDisplayInfo info)
    {
        UpdateHdrUi();
    }

    private void OnOutputCapabilitiesChanged(object? sender, EventArgs e)
    {
        UpdateHdrUi();
    }

    private void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        ApplyHdrMode();
    }

    // Merges the WinRT display detection and the DXGI hardware query into a single
    // effective HDR capability snapshot.
    private HdrDisplayInfo GetEffectiveHdrInfo()
    {
        HdrDisplayInfo info = _hdrTracker?.Current ?? HdrDisplayInfoTracker.Unsupported;

        return new HdrDisplayInfo(
            Kind: info.Kind,
            IsSupported: info.IsSupported || _shaderPanel.IsOutputHdrCapable,
            MaxLuminanceInNits: info.MaxLuminanceInNits > 0 ? info.MaxLuminanceInNits : _shaderPanel.OutputMaxLuminanceInNits,
            MinLuminanceInNits: info.MinLuminanceInNits,
            SdrWhiteLevelInNits: info.SdrWhiteLevelInNits);
    }

    // Refreshes the toolbar state and applies the HDR mode.
    private void UpdateHdrUi()
    {
        if (_shaderPanel is null) return;

        HdrDisplayInfo effective = GetEffectiveHdrInfo();

        HdrStatusText.Text = effective.StatusText;
        HdrToggle.IsEnabled = effective.IsSupported;

        // Auto-enable HDR the first time it becomes available; the user can still
        // toggle it afterwards.
        if (effective.IsSupported && !_hdrAutoEnabled)
        {
            _hdrAutoEnabled = true;
            HdrToggle.IsOn = true;
        }

        ApplyHdrMode();
    }

    // Applies the current toggle + detection state to the rendering pipeline.
    private void ApplyHdrMode()
    {
        if (_shaderPanel is null) return;

        HdrDisplayInfo effective = GetEffectiveHdrInfo();
        bool enabled = HdrToggle.IsOn && effective.IsSupported;

        _shaderPanel.SetHdrParameters(
            effective.SdrWhiteLevelInNits > 0 ? effective.SdrWhiteLevelInNits : 200,
            effective.MaxLuminanceInNits > 0 ? effective.MaxLuminanceInNits : 1000);

        _shaderPanel.IsHdrEnabled = enabled;
    }

    private void OnRenderingFailed(object? sender, Exception e)
    {
        FrameCountText.Text = "渲染错误";
        _shaderPanel.ShaderRunner = null;

        Debug.WriteLine($"Rendering failed: {e}");
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shaderPanel.Dispose();
        _factory.Dispose();
        _hdrTracker?.Dispose();
        _device.Dispose();
        _stopwatch.Stop();
    }
}
