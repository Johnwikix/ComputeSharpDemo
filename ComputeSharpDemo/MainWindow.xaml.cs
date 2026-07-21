using ComputeSharp;
using ComputeSharp.WinUI;
using ComputeSharpDemo.Shaders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using Windows.Foundation;

namespace ComputeSharpDemo;

public sealed partial class MainWindow : Window
{
    private readonly GraphicsDevice _device;
    private readonly ShaderFactory _factory;
    private readonly AnimatedComputeShaderPanel _shaderPanel;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private IShaderPass? _activePass;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        Title = "ComputeSharp Demo";

        // Create the GPU device and shader panel
        _device = GraphicsDevice.GetDefault();
        _factory = new ShaderFactory();
        _shaderPanel = new AnimatedComputeShaderPanel(_device)
        {
            IsDynamicResolutionEnabled = false,
            IsVerticalSyncEnabled = true,
        };

        Grid.SetRow(_shaderPanel, 1);
        RootGrid.Children.Add(_shaderPanel);

        // Populate shader selector
        ShaderSelector.ItemsSource = ShaderFactory.Catalog;
        ShaderSelector.SelectedIndex = 0;

        // Mouse tracking
        _shaderPanel.PointerMoved += OnPointerMoved;
        _shaderPanel.SizeChanged += OnShaderPanelSizeChanged;

        // Cleanup
        Closed += (_, _) => Dispose();
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
            Point pt = e.GetCurrentPoint(_shaderPanel).Position;
            _activePass.SetMouse(
                (float)pt.X,
                (float)pt.Y,
                (float)_shaderPanel.ActualWidth,
                (float)_shaderPanel.ActualHeight);
        }
    }

    private void OnShaderPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_activePass is null) return;

        var size = new Int2((int)e.NewSize.Width, (int)e.NewSize.Height);
        _activePass.OnResize(size);
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shaderPanel.Dispose();
        _factory.Dispose();
        _device.Dispose();
        _stopwatch.Stop();
    }
}