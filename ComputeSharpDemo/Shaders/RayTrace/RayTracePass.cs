using ComputeSharp;
using ComputeSharpDemo.Hdr;

namespace ComputeSharpDemo.Shaders.RayTrace;

/// <summary>
/// Selectable denoising modes for the ray traced pipeline.
/// </summary>
public enum RayTraceDenoiserMode
{
    /// <summary>No denoising: raw path tracer output is presented directly.</summary>
    None,

    /// <summary>Naive running mean of the last frames, no spatial filtering.</summary>
    TemporalOnly,

    /// <summary>SVGF/RELAX pipeline: variance-guided temporal accumulation + 5-level A-trous spatial filter.</summary>
    Relax,
}

/// <summary>
/// <see cref="IShaderPass"/> wrapper for the Ray Trace path tracer with an SVGF/RELAX-inspired
/// denoiser pipeline. Per frame the following compute dispatches run in order:
///
/// <list type="number">
/// <item><see cref="RayTraceShader"/> — 2 SPP path tracing: encoded radiance + hit distance,
/// plus a normal / material-id G-buffer.</item>
/// <item><see cref="TemporalAccumulationShader"/> — exponential history accumulation whose
/// length grows frame by frame up to a cap (bounded ghosting), with RELAX-style per-channel
/// AABB history clamping, NRD-style hit-distance confidence and a variance estimate written
/// into the history W channel; camera changes reset <c>frame</c> so stale history is discarded.</item>
/// <item>Five <see cref="SpatialFilterShader"/> levels (step 1, 2, 4, 8, 16 — SVGF 3x3 A-trous)
/// with normal / hit-distance / material edge stops and a variance-guided luminance weight,
/// ping-ponging through two buffers; the last level filters straight into the display texture.</item>
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
    private bool _disposed;

    private int _textureWidth;
    private int _textureHeight;

    private RayTraceDenoiserMode _denoiserMode = RayTraceDenoiserMode.Relax;
    private int _maxBounces = 10;
    private int _samples = 2;

    /// <summary>
    /// Gets or sets the denoiser mode. Changing it invalidates the temporal history.
    /// </summary>
    public RayTraceDenoiserMode DenoiserMode
    {
        get => _denoiserMode;
        set
        {
            if (_denoiserMode == value) return;
            _denoiserMode = value;
            _frame = 0;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of light bounces per path. Changing it changes the
    /// rendered image, so the temporal history is invalidated.
    /// </summary>
    public int MaxBounces
    {
        get => _maxBounces;
        set
        {
            value = int.Clamp(value, 1, 32);
            if (_maxBounces == value) return;
            _maxBounces = value;
            _frame = 0;
        }
    }

    /// <summary>
    /// Gets or sets the number of paths per pixel. Changing it invalidates the temporal history.
    /// </summary>
    public int Samples
    {
        get => _samples;
        set
        {
            value = int.Clamp(value, 1, 16);
            if (_samples == value) return;
            _samples = value;
            _frame = 0;
        }
    }

    /// <summary>Noisy encoded radiance (RGB) + normalized hit distance (W) written by the path tracer.</summary>
    private ReadWriteTexture2D<Rgba64, Float4>? _signal;

    /// <summary>Temporal history ping-pong buffers: accumulated color (RGB) + variance estimate (W).</summary>
    private ReadWriteTexture2D<Rgba64, Float4>? _historyA;
    private ReadWriteTexture2D<Rgba64, Float4>? _historyB;

    /// <summary>Accumulated hit distance ping-pong buffers, used by the temporal confidence test.</summary>
    private ReadWriteTexture2D<R16, float>? _momentA;
    private ReadWriteTexture2D<R16, float>? _momentB;

    /// <summary>Spatial filter ping-pong buffers.</summary>
    private ReadWriteTexture2D<Rgba64, Float4>? _filterA;
    private ReadWriteTexture2D<Rgba64, Float4>? _filterB;

    /// <summary>World-space normal (RGB) + material id (A) of the primary hit.</summary>
    private ReadWriteTexture2D<Rgba32, Float4>? _normal;

    public string Id => "ray-trace";
    public string DisplayName => "Ray Trace";
    public string Description => "MC path tracer with SVGF/RELAX-style denoiser pipeline.";
    public ShaderAuthor Author { get; } = new(
        Name: "RT Demo",
        Url: null,
        License: "CC BY-NC-SA 3.0");
    public string? OriginalUrl => null;

    public ShaderCapabilities Capabilities =>
        ShaderCapabilities.UsesTime
      | ShaderCapabilities.UsesMouse
      | ShaderCapabilities.UsesResolution;

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

        switch (_denoiserMode)
        {
            case RayTraceDenoiserMode.None:
                // Raw path tracer output, straight into the display texture.
                _device.ForEach(
                    texture,
                    new RayTraceShader(
                        time,
                        _mouse,
                        resolution,
                        _frame,
                        _dist,
                        hdr.IsHdrEnabled,
                        hdr.SdrWhiteLevelInNits,
                        hdr.MaxLuminanceInNits,
                        _maxBounces,
                        _samples,
                        _normal!));
                break;

            case RayTraceDenoiserMode.TemporalOnly:
                // Naive running mean: path trace into the signal, accumulate in place,
                // present the accumulated result (history is updated by the pass itself).
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
                        _maxBounces,
                        _samples,
                        _normal!));

                _device.ForEach(
                    texture,
                    new NaiveTemporalAccumulationShader(_frame, _signal!, _historyA!));
                break;

            case RayTraceDenoiserMode.Relax:
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
                        _maxBounces,
                        _samples,
                        _normal!));

                // Pass 2 — temporal accumulation onto the history pong buffer; the accumulated
                // hit distance is written into the moment pong buffer in the same dispatch.
                _device.ForEach(
                    _historyB!,
                    new TemporalAccumulationShader(_frame, resolution, _signal!, _historyA!, _momentA!, _momentB!));

                // Passes 3-7 — five SVGF-style A-trous levels (step 1, 2, 4, 8, 16); the last one
                // writes the final filtered, display-encoded result straight into the panel texture.
                _device.ForEach(
                    _filterA!,
                    new SpatialFilterShader(0, resolution, _historyB!, _signal!, _normal!));

                _device.ForEach(
                    _filterB!,
                    new SpatialFilterShader(1, resolution, _filterA!, _signal!, _normal!));

                _device.ForEach(
                    _filterA!,
                    new SpatialFilterShader(2, resolution, _filterB!, _signal!, _normal!));

                _device.ForEach(
                    _filterB!,
                    new SpatialFilterShader(3, resolution, _filterA!, _signal!, _normal!));

                _device.ForEach(
                    texture,
                    new SpatialFilterShader(4, resolution, _filterB!, _signal!, _normal!));

                (_historyA, _historyB) = (_historyB, _historyA);
                (_momentA, _momentB) = (_momentB, _momentA);
                break;
        }

        _frame++;

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
        _momentA?.Dispose();
        _momentB?.Dispose();
        _filterA?.Dispose();
        _filterB?.Dispose();
        _normal?.Dispose();

        _signal = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _historyA = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _historyB = device.AllocateReadWriteTexture2D<Rgba64, Float4>(width, height);
        _momentA = device.AllocateReadWriteTexture2D<R16, float>(width, height);
        _momentB = device.AllocateReadWriteTexture2D<R16, float>(width, height);
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
        _momentA?.Dispose();
        _momentB?.Dispose();
        _filterA?.Dispose();
        _filterB?.Dispose();
        _normal?.Dispose();
    }
}