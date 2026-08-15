using ComputeSharp;
using ComputeSharpDemo.Hdr;
using ComputeSharpDemo.Shaders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;
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
    private bool _layoutInitialized;
    private bool _disposed;

    // XAML sizes are in DIPs; the swap chain / render target must be physical pixels.
    private double DpiScale => RootGrid.XamlRoot?.RasterizationScale ?? 1.0;

    public MainWindow()
    {
        InitializeComponent();
        Title = "ComputeSharp Demo";

        // Create the GPU device and shader panel
        _device = GraphicsDevice.GetDefault();
        _factory = new ShaderFactory();

        _shaderPanel = new HdrShaderPanel(_device);
        PanelHost.Children.Add(_shaderPanel);

        // Populate shader selector
        ShaderSelector.ItemsSource = ShaderFactory.Catalog;
        ShaderSelector.SelectedIndex = 0;

        // Mouse tracking
        _shaderPanel.PointerMoved += OnPointerMoved;
        _shaderPanel.PointerWheelChanged += OnPointerWheelChanged;
        _shaderPanel.SizeChanged += OnShaderPanelSizeChanged;
        _shaderPanel.RenderingFailed += OnRenderingFailed;
        _shaderPanel.OutputCapabilitiesChanged += OnOutputCapabilitiesChanged;

        // Track window moves/resizes so the HDR state of the current display stays accurate
        // (multi-monitor setups with mixed HDR/SDR outputs).
        AppWindow.Changed += OnAppWindowChanged;

        // FPS counter (poll pass frame count every 500ms) + safety-net output recheck
        var fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        fpsTimer.Tick += (_, _) =>
        {
            int total = _activePass?.TotalFrames ?? 0;
            int fps = (int)((total - _fpsBaseline) / 0.5);
            FrameCountText.Text = $"FPS: {fps}";
            _fpsBaseline = total;

            UpdateWindowBoundsAndRecheckOutput();
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

        // Track DPI/monitor changes (SizeChanged does not fire for DPI-only changes)
        if (RootGrid.XamlRoot is XamlRoot xamlRoot)
        {
            xamlRoot.Changed += OnXamlRootChanged;
        }

        TrySyncPanelAndBuffer();
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

            // The panel is sized in physical pixels, so its coordinates are already
            // buffer pixels — no DPI scaling needed.
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

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SafeTry(() => TrySyncPanelAndBuffer());
    }

    // Fires on DPI/monitor changes (moving across monitors with different scaling):
    // re-derive the physical buffer size and re-evaluate the HDR state of the new display.
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        SafeTry(() =>
        {
            TrySyncPanelAndBuffer();
            UpdateWindowBoundsAndRecheckOutput();
        });
    }

    // Fires on window position/size changes: keep the current-output HDR state accurate.
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        SafeTry(() =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                UpdateWindowBoundsAndRecheckOutput();
            }

            if (args.DidSizeChange)
            {
                TrySyncPanelAndBuffer();
            }
        });
    }

    // Event handlers must never break the event chain on a single exception.
    private void SafeTry(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[HDR] UI handler failed: {e.Message}");
        }
    }

    // Merges the WinRT display detection and the DXGI hardware query into a single
    // effective HDR capability snapshot.
    private HdrDisplayInfo GetEffectiveHdrInfo()
    {
        HdrDisplayInfo info = _hdrTracker?.Current ?? HdrDisplayInfoTracker.Unsupported;

        return new HdrDisplayInfo(
            Kind: info.Kind,
            IsSupported: info.IsSupported || _shaderPanel.IsCurrentOutputHdrCapable,
            MaxLuminanceInNits: info.MaxLuminanceInNits > 0 ? info.MaxLuminanceInNits : _shaderPanel.CurrentOutputMaxLuminanceInNits,
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

    // SwapChainPanel displays swapchain buffer pixels as DIPs (WinUI bug #8219): the panel
    // host is sized to the physical pixel size (1 buffer px = 1 px, no crop), and the panel
    // is counter-scaled by 1/DpiScale so it fits its layout slot again.
    private void TrySyncPanelAndBuffer()
    {
        SafeTry(() =>
        {
            if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
            {
                return;
            }

            double dpiScale = DpiScale;
            double contentWidth = RootGrid.ActualWidth;
            double contentHeight = Math.Max(1, RootGrid.ActualHeight - ToolbarHeight);

            double w = Math.Round(contentWidth * dpiScale);
            double h = Math.Round(contentHeight * dpiScale);
            if (w <= 0 || h <= 0)
            {
                return;
            }

            bool dirty = double.IsNaN(PanelHost.Width) || double.IsNaN(PanelHost.Height)
                || Math.Abs(PanelHost.Width - w) > 0.5 || Math.Abs(PanelHost.Height - h) > 0.5;

            if (dirty)
            {
                PanelHost.Width = w;
                PanelHost.Height = h;
            }

            if (double.IsNaN(_shaderPanel.Width) || Math.Abs(_shaderPanel.Width - w) > 0.5)
            {
                _shaderPanel.Width = w;
            }

            if (double.IsNaN(_shaderPanel.Height) || Math.Abs(_shaderPanel.Height - h) > 0.5)
            {
                _shaderPanel.Height = h;
            }

            _shaderPanel.SetDpiScale(dpiScale);
            _shaderPanel.QueueResize(w, h);

            _layoutInitialized = true;
        });
    }

    private double ToolbarHeight => RootGrid.RowDefinitions.Count > 0
        ? RootGrid.RowDefinitions[0].ActualHeight
        : 0;

    // Keeps the current-output HDR state in sync with the window position (multi-monitor).
    private void UpdateWindowBoundsAndRecheckOutput()
    {
        SafeTry(() =>
        {
            if (!_layoutInitialized) return;

            PointInt32 position = AppWindow.Position;
            SizeInt32 size = AppWindow.Size;

            if (size.Width > 0 && size.Height > 0)
            {
                _shaderPanel.SetWindowBounds(new RectInt32(position.X, position.Y, size.Width, size.Height));
            }

            _shaderPanel.RecheckOutput();
        });
    }

    private void OnRenderingFailed(object? sender, Exception e)
    {
        // Do NOT clear the shader runner: the render loop is resilient to transient
        // errors (resizes, presents), so a single failure must not stop rendering.
        FrameCountText.Text = "渲染错误";

        Debug.WriteLine($"Rendering failed: {e}");
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (RootGrid.XamlRoot is XamlRoot xamlRoot)
        {
            xamlRoot.Changed -= OnXamlRootChanged;
        }

        AppWindow.Changed -= OnAppWindowChanged;

        _shaderPanel.Dispose();
        _factory.Dispose();
        _hdrTracker?.Dispose();
        _device.Dispose();
        _stopwatch.Stop();
    }
}
