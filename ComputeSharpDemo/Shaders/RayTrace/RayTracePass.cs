using ComputeSharp;
using ComputeSharpDemo.Hdr;

namespace ComputeSharpDemo.Shaders.RayTrace;

public sealed class RayTracePass : IShaderPass
{
    private GraphicsDevice? _device;
    private Float2 _mouse;
    private float _dist = 6.5f;
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

    public void SetZoom(float delta)
    {
        _dist *= 1.0f - delta * 0.1f;
        _dist = float.Clamp(_dist, 1.0f, 50.0f);
        _frame = 0;
    }

    public void Initialize(GraphicsDevice device, Int2 initialSize)
    {
        _device = device;
    }

    public void OnResize(Int2 newSize) { }

    public bool TryExecute(
        ReadWriteTexture2D<Rgba64, Float4> texture,
        int width,
        int height,
        TimeSpan timespan,
        object? parameter)
    {
        if (_device is null)
            throw new InvalidOperationException("Initialize must be called before dispatch.");

        HdrRenderParameters hdr = parameter is HdrRenderParameters parameters ? parameters : HdrRenderParameters.Default;

        float time = (float)timespan.TotalSeconds;
        Float2 resolution = new(width, height);

        _device.ForEach(
            texture,
            new RayTraceShader(
                time,
                _mouse,
                resolution,
                _frame,
                texture,
                _dist,
                hdr.IsHdrEnabled,
                hdr.SdrWhiteLevelInNits,
                hdr.MaxLuminanceInNits));

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
