using ComputeSharp;
using ComputeSharpDemo.Hdr;

namespace ComputeSharpDemo.Shaders.RayTrace;

/// <summary>
/// <see cref="IShaderPass"/> wrapper for the Ray Trace path tracer with an NRD-inspired
/// denoiser pipeline. Per frame the following compute dispatches run in order:
///
/// <list type="number">
/// <item><see cref="RayTraceShader"/> — 2 SPP path tracing: encoded radiance + hit distance,
/// plus a normal / material-id G-buffer.</item>
/// <item><see cref="TemporalAccumulationShader"/> — history accumulation driven by a
/// hit-distance confidence; camera changes reset <c>frame</c> so stale history is discarded.</item>
/// <item><see cref="SpatialFilterShader"/> — two A-trous levels (step 1, 2) with
/// normal / hit-distance / material edge stops, the last level filtering straight into
/// the display texture.</item>
/// </list>
///
/// All intermediate textures are recreated lazily on the render thread whenever the
/// render size changes (the swap chain renderer resizes independently of the UI thread).
/// </summary>
public sealed class RayTracePass : IShaderPass
{
    private GraphicsDevice? _device;
    private Float2 _mouse;
    private float _dist = 6.5f;
    private int _frame;
    private int _totalFrames;
    private bool _disposed;

    private int _textureWidth;
    private int _textureHeight;

    /// <summary>Noisy radiance (RGB) + normalized hit distance (W) written by the path tracer.</summary>
    private ReadWriteTexture2D<Rgba64, Float4>? _signal;

    /// <summary>Temporal history ping-pong buffers.</summary>
    private ReadWriteTexture2D<Rgba64, Float4>? _historyA;
    private ReadWriteTexture2D<Rgba64, Float4>? _historyB;

    /// <summary>Spatial filter ping-pong buffers.</summary>
    private ReadWriteTexture2D<Rgba64, Float4>? _filterA;
    private ReadWriteTexture2D<Rgba64, Float4>? _filterB;

    /// <summary>World-space normal (RGB) + material id (A) of the primary hit.</summary>
    private ReadWriteTexture2D<Rgba32, Float4>? _normal;

    public string Id => "ray-trace";
    public string DisplayName => "Ray Trace";
    public string Description => "MC path tracer with NRD-style denoiser pipeline.";
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

        EnsureTextures(width, height);

        HdrRenderParameters hdr = parameter is HdrRenderParameters parameters ? parameters : HdrRenderParameters.Default;

        float time = (float)timespan.TotalSeconds;
        Float2 resolution = new(width, height);

        // Pass 1 — path trace into the noisy signal + normal G-buffer.
        _device.ForEach(
            _signal!,
            new RayTraceShader(
                time,
                _mouse,
                resolution,
                _frame,
                _dist,
                hdr.IsHdrEnabled,
                hdr.SdrWhiteLevelInNits,
                hdr.MaxLuminanceInNits,
                _normal!));

        // Pass 2 — temporal accumulation onto the history pong buffer.
        _device.ForEach(
            _historyB!,
            new TemporalAccumulationShader(_frame, resolution, _signal!, _historyA!));

        // Passes 3 & 4 — two A-trous spatial filtering levels; the last one writes the
        // final filtered, already display-encoded result straight into the panel texture.
        _device.ForEach(
            _filterA!,
            new SpatialFilterShader(1, resolution, _historyB!, _normal!));

        _device.ForEach(
            texture,
            new SpatialFilterShader(2, resolution, _filterA!, _normal!));

        (_historyA, _historyB) = (_historyB, _historyA);

        _frame++;
        _totalFrames++;

        return true;
    }

    private void EnsureTextures(int width, int height)
    {
        if (_signal is not null && _textureWidth == width && _textureHeight == height)
        {
            return;
        }

        GraphicsDevice device = _device
            ?? throw new InvalidOperationException("Initialize must be called before dispatch.");

        _signal?.Dispose();
        _historyA?.Dispose();
        _historyB?.Dispose();
        _filterA?.Dispose();
        _filterB?.Dispose();
        _normal?.Dispose();

        _signal = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _historyA = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _historyB = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _filterA = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _filterB = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _normal = device.AllocateReadWriteTexture2D<Rgba32, Float4>(width, height);

        _textureWidth = width;
        _textureHeight = height;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _signal?.Dispose();
        _historyA?.Dispose();
        _historyB?.Dispose();
        _filterA?.Dispose();
        _filterB?.Dispose();
        _normal?.Dispose();
    }
}