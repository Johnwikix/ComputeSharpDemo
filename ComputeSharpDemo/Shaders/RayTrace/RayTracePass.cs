using ComputeSharp;
using ComputeSharp.WinUI;

namespace ComputeSharpDemo.Shaders.RayTrace;

public sealed class RayTracePass : IShaderPass
{
    private GraphicsDevice? _device;
    private Float2 _mouse;
    private int _frame;
    private int _totalFrames;
    private bool _disposed;

    public string Id => "ray-trace";
    public string DisplayName => "Ray Trace";
    public string Description => "Monte Carlo path tracer with sphere scene.";
    public ShaderAuthor Author { get; } = new(
        Name: "RT Demo",
        Url: null,
        License: "CC BY-NC-SA 3.0");
    public string? OriginalUrl => null;

    public ShaderCapabilities Capabilities =>
        ShaderCapabilities.UsesTime
      | ShaderCapabilities.UsesMouse
      | ShaderCapabilities.UsesResolution;

    public int TotalFrames => _totalFrames;

    public void SetMouse(float x, float y, float panelWidth, float panelHeight)
    {
        _mouse = new Float2(x, panelHeight - y);
        _frame = 0;
    }

    public void Initialize(GraphicsDevice device, Int2 initialSize)
    {
        _device = device;
    }

    public void OnResize(Int2 newSize) { }

    public bool TryExecute(
        IReadWriteNormalizedTexture2D<Float4> texture,
        TimeSpan timespan,
        object? parameter)
    {
        if (_device is null)
            throw new InvalidOperationException("Initialize must be called before dispatch.");

        float time = (float)timespan.TotalSeconds;
        Float2 resolution = new(texture.Width, texture.Height);

        _device.ForEach(
            texture,
            new RayTraceShader(time, _mouse, resolution, _frame, texture));

        _frame++;
        _totalFrames++;

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
