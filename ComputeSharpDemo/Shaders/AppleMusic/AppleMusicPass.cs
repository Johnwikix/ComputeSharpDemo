using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ComputeSharp;
using ComputeSharpDemo.Hdr;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ComputeSharpDemo.Shaders.AppleMusic;

/// <summary>
/// <see cref="IShaderPass"/> wrapper for the Apple Music inspired background — a pure
/// compute-shader port of the D3D11 (vertex + pixel shader) renderer in
/// Lyricify-Backgrounds (Apache 2.0).
///
/// <para>
/// Per frame the following compute dispatches run in order, mirroring the original
/// <see cref="TryExecute"/> pipeline:
/// </para>
/// <list type="number">
/// <item><see cref="AppleMusicRotationShader"/> — the rotating artwork layers + aspect-fill
/// backing image, rendered per pixel by inverting the original quad transforms, into a
/// downsampled backdrop surface.</item>
/// <item><see cref="AppleMusicBlurShader"/> ×2 — separable 77-pair gaussian blur (horizontal,
/// then vertical with coverage normalization) on the backdrop.</item>
/// <item><see cref="AppleMusicCompositeShader"/> — treated material + per-pixel-inverted
/// pinch mesh, dithering and HDR/SDR display encoding, into the panel frame texture.</item>
/// </list>
/// <para>
/// The spectrum-driven image scaling and artwork crossfade of the original are omitted;
/// the mesh preset and blur configuration match the dark-theme lyrics look.
/// </para>
/// </summary>
public sealed class AppleMusicPass : IShaderPass
{
    private const int BlurSurfaceDownsample = 4;
    private const float GaussianKernelSigma = 42.5f;
    private const float LyricsBlurSigma = 42.5f;
    private const float OrdinaryBlurSigma = 80f;
    private const float DarkBehindLyricsBlackScrimAlpha = 0.4f;
    private const float PortraitTextureScale = 1f;
    private const float LandscapeTextureScale = 0.8f;
    private const float MeshWarpTimeScale = 5f;

    /// <summary>Image loaded when no other path is configured.</summary>
    public const string DefaultArtworkPath = @"C:\Users\90684\Pictures\SavedImage.jpg";

    /// <summary>
    /// Set to <c>true</c> in DEBUG builds to make artwork-loading failures (missing file,
    /// decode error) throw instead of silently falling back to a 1×1 black texture. The
    /// silent fallback masks shader-path bugs and historically has been the entry point
    /// for races that tear down the D3D12 device and crash dwm.exe.
    /// </summary>
    public static bool ThrowOnArtworkFailure { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Largest artwork edge kept on the GPU (the backdrop samples it at ≤ 1/4 output size).</summary>
    private const uint MaximumArtworkDimension = 1024;

    // Normalized sigma-42.5 kernel with paired bilinear taps (Lyricify-Backgrounds).
    private static readonly float[] BlurOffsets =
    [
        1.499792388f, 3.499515571f, 5.499238755f, 7.498961939f,
        9.498685124f, 11.49840831f, 13.498131497f, 15.497854684f,
        17.497577874f, 19.497301064f, 21.497024257f, 23.496747451f,
        25.496470647f, 27.496193845f, 29.495917046f, 31.495640249f,
        33.495363455f, 35.495086663f, 37.494809875f, 39.49453309f,
        41.494256308f, 43.49397953f, 45.493702755f, 47.493425984f,
        49.493149218f, 51.492872455f, 53.492595697f, 55.492318943f,
        57.492042195f, 59.49176545f, 61.491488712f, 63.491211978f,
        65.490935249f, 67.490658527f, 69.490381809f, 71.490105098f,
        73.489828393f, 75.489551694f, 77.489275002f, 79.488998316f,
        81.488721637f, 83.488444964f, 85.488168299f, 87.487891641f,
        89.487614991f, 91.487338348f, 93.487061713f, 95.486785085f,
        97.486508466f, 99.486231855f, 101.485955253f, 103.485678659f,
        105.485402074f, 107.485125498f, 109.484848931f, 111.484572373f,
        113.484295824f, 115.484019286f, 117.483742757f, 119.483466238f,
        121.483189729f, 123.482913231f, 125.482636742f, 127.482360265f,
        129.482083798f, 131.481807343f, 133.481530898f, 135.481254465f,
        137.480978043f, 139.480701633f, 141.480425235f, 143.480148848f,
        145.479872474f, 147.479596112f, 149.479319763f, 151.479043426f,
        153.0f
    ];

    private static readonly float[] BlurWeights =
    [
        0.0187664737f, 0.01871460399f, 0.01862159953f, 0.01848807512f,
        0.01831490985f, 0.01810323743f, 0.01785443382f, 0.01757010239f,
        0.01725205665f, 0.01690230103f, 0.01652300989f, 0.01611650499f,
        0.01568523193f, 0.01523173574f, 0.01475863601f, 0.01426860189f,
        0.01376432738f, 0.01324850701f, 0.01272381248f, 0.01219287029f,
        0.0116582408f, 0.01112239879f, 0.01058771585f, 0.01005644461f,
        0.00953070502f, 0.00901247274f, 0.00850356966f, 0.0080056566f,
        0.00752022811f, 0.0070486094f, 0.00659195523f, 0.00615125069f,
        0.0057273138f, 0.00532079962f, 0.00493220595f, 0.0045618802f,
        0.0042100274f, 0.00387671917f, 0.00356190339f, 0.00326541439f,
        0.00298698364f, 0.0027262505f, 0.00248277319f, 0.00225603955f,
        0.00204547767f, 0.00185046618f, 0.00167034405f, 0.00150441998f,
        0.0013519811f, 0.00121230117f, 0.00108464796f, 0.00096829001f,
        0.00086250273f, 0.00076657363f, 0.00067980702f, 0.00060152792f,
        0.00053108534f, 0.00046785492f, 0.00041124106f, 0.00036067838f,
        0.0003156328f, 0.00027560209f, 0.00024011609f, 0.0002087365f,
        0.00018105641f, 0.00015669956f, 0.00013531939f, 0.00011659788f,
        0.0001002443f, 0.00008599378f, 0.00007360593f, 0.00006286326f,
        0.00005356972f, 0.00004554915f, 0.00003864377f, 0.00003271276f,
        0.00001440207f
    ];

    private GraphicsDevice? _device;
    private readonly int _presetSlot;
    private bool _disposed;

    // R16G16B16A16 surfaces (like the FP16 surfaces of the original renderer); all
    // intermediate values stay within [0, 1], so the normalized format is safe.
    private ReadWriteTexture2D<Rgba64, Float4>? _rotationSurface;
    private ReadWriteTexture2D<Rgba64, Float4>? _horizontalBlurSurface;
    private ReadWriteTexture2D<Rgba64, Float4>? _verticalBlurSurface;
    private int _surfaceWidth;
    private int _surfaceHeight;

    private ReadOnlyBuffer<float>? _blurOffsets;
    private ReadOnlyBuffer<float>? _blurWeights;

    private ReadOnlyBuffer<Float2>? _meshFrom;
    private ReadOnlyBuffer<Float2>? _meshTo;
    private int _meshRows;
    private int _meshColumns;
    private bool _meshIsPortrait;

    private ReadOnlyTexture2D<Rgba32, Float4>? _artwork;
    private Int2 _artworkSize;

    public AppleMusicPass()
    {
        _presetSlot = AppleMusicMesh.SelectPresetSlot();
    }

    public string Id => "apple-music-inspired";
    public string DisplayName => "Apple Music Inspired";
    public string Description => "Rotating artwork + pinch mesh background, pure compute port of Lyricify-Backgrounds.";
    public ShaderAuthor Author { get; } = new(
        Name: "Lyricify-Backgrounds",
        Url: null,
        License: "Apache-2.0");
    public string? OriginalUrl => null;

    public ShaderCapabilities Capabilities =>
        ShaderCapabilities.UsesTime
      | ShaderCapabilities.UsesResolution;

    public void Initialize(GraphicsDevice device, Int2 initialSize)
    {
        // Defer every GPU resource allocation to the render thread. ComputeSharp's
        // GraphicsDevice is shared between this call site (UI thread) and the
        // HdrSwapChainRenderer.RenderThreadMain loop (background thread): recording
        // D3D12 commands here while the render thread is mid-Dispatch tears the
        // device down and freezes dwm.exe.
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
        GraphicsDevice device = _device
            ?? throw new InvalidOperationException("Initialize must be called before dispatch.");

        EnsureKernel();
        EnsureArtwork();
        EnsureSurfaces(width, height);
        EnsureMesh(width, height);

        HdrRenderParameters hdr = parameter is HdrRenderParameters parameters ? parameters : HdrRenderParameters.Default;

        float time = (float)timespan.TotalSeconds;

        // PinchVertex: phase = acos(sin(Time * pi / 5)) / pi, mix = smoothstep(phase).
        float phase = MathF.Acos(MathF.Sin(time * MathF.PI / MeshWarpTimeScale)) / MathF.PI;
        float pinchMix = phase * phase * (3f - 2f * phase);

        float viewAspectRatio = width / (float)height;
        Vector2 viewScale = viewAspectRatio >= 1f
            ? new Vector2(1f, viewAspectRatio)
            : new Vector2(1f / viewAspectRatio, 1f);

        Float2 backdropSize = new(_surfaceWidth, _surfaceHeight);
        Float2 blurScale = GetBlurScale(LyricsBlurSigma, width, height);

        bool isPortrait = height > width;
        float pinchTextureScale = isPortrait ? PortraitTextureScale : LandscapeTextureScale;
        float pinchTextureOffset = (1f - pinchTextureScale) * 0.5f;

        // Pass 1 — rotating artwork layers into the downsampled backdrop.
        device.ForEach(
            _rotationSurface!,
            new AppleMusicRotationShader(
                _artwork!,
                _artworkSize,
                backdropSize,
                time,
                new Float2(viewScale.X, viewScale.Y),
                rotationScale: 1f,
                imageScale: 1f));

        // Passes 2-3 — separable gaussian blur; only the vertical pass normalizes
        // the zero-border coverage.
        device.ForEach(
            _horizontalBlurSurface!,
            new AppleMusicBlurShader(
                _rotationSurface!,
                backdropSize,
                _blurOffsets!,
                _blurWeights!,
                new Float2(blurScale.X / backdropSize.X, 0f),
                normalize: false));

        device.ForEach(
            _verticalBlurSurface!,
            new AppleMusicBlurShader(
                _horizontalBlurSurface!,
                backdropSize,
                _blurOffsets!,
                _blurWeights!,
                new Float2(0f, blurScale.Y / backdropSize.Y),
                normalize: true));

        // Pass 4 — treated material + pinch mesh into the panel frame texture.
        device.ForEach(
            texture,
            new AppleMusicCompositeShader(
                _verticalBlurSurface!,
                backdropSize,
                _meshFrom!,
                _meshTo!,
                _meshRows,
                _meshColumns,
                pinchMix,
                new Float2(width, height),
                DarkBehindLyricsBlackScrimAlpha,
                ditherStrength: 1f,
                pinchTextureScale,
                pinchTextureOffset,
                hdr.IsHdrEnabled,
                hdr.SdrWhiteLevelInNits,
                hdr.MaxLuminanceInNits));

        return true;
    }

    /// <summary>
    /// Port of <c>GetBlurScale</c>: converts the blur radius into the offset step used by
    /// the kernel on the downsampled backdrop surface.
    /// </summary>
    private Float2 GetBlurScale(float sigma, int outputWidth, int outputHeight)
    {
        double backdropDownsample = BlurSurfaceDownsample
            * Math.Max(1d, Math.Max(LyricsBlurSigma, OrdinaryBlurSigma) / GaussianKernelSigma);
        float rotationWidth = (float)Math.Max(1, Math.Floor(outputWidth / backdropDownsample));
        float rotationHeight = (float)Math.Max(1, Math.Floor(outputHeight / backdropDownsample));
        float targetOutputSigma = sigma * BlurSurfaceDownsample;

        return new Float2(
            targetOutputSigma * rotationWidth / (outputWidth * GaussianKernelSigma),
            targetOutputSigma * rotationHeight / (outputHeight * GaussianKernelSigma));
    }

    private void EnsureSurfaces(int width, int height)
    {
        if (_rotationSurface is not null && _surfaceWidth == width && _surfaceHeight == height)
        {
            return;
        }

        GraphicsDevice device = _device
            ?? throw new InvalidOperationException("Initialize must be called before dispatch.");

        double backdropDownsample = BlurSurfaceDownsample
            * Math.Max(1d, Math.Max(LyricsBlurSigma, OrdinaryBlurSigma) / GaussianKernelSigma);
        int backdropWidth = Math.Max(1, (int)Math.Floor(width / backdropDownsample));
        int backdropHeight = Math.Max(1, (int)Math.Floor(height / backdropDownsample));

        _rotationSurface?.Dispose();
        _horizontalBlurSurface?.Dispose();
        _verticalBlurSurface?.Dispose();

        _rotationSurface = device.AllocateReadWriteTexture2D<Rgba64, Float4>(backdropWidth, backdropHeight);
        _horizontalBlurSurface = device.AllocateReadWriteTexture2D<Rgba64, Float4>(backdropWidth, backdropHeight);
        _verticalBlurSurface = device.AllocateReadWriteTexture2D<Rgba64, Float4>(backdropWidth, backdropHeight);

        _surfaceWidth = backdropWidth;
        _surfaceHeight = backdropHeight;
    }

    /// <summary>
    /// Rebuilds the pinch mesh when the orientation changed. Portrait layouts use the
    /// (deduplicated) portrait presets, landscape slots map 1:1 like the original.
    /// </summary>
    private void EnsureMesh(int width, int height)
    {
        bool isPortrait = height > width;

        if (_meshFrom is not null && _meshIsPortrait == isPortrait)
        {
            return;
        }

        GraphicsDevice device = _device
            ?? throw new InvalidOperationException("Initialize must be called before dispatch.");

        int presetIndex = isPortrait
            ? AppleMusicMesh.ResolvePortraitPreset(_presetSlot)
            : Math.Clamp(_presetSlot, 0, AppleMusicMesh.PresetSlotCount - 1);

        AppleMusicMesh.MeshData mesh = AppleMusicMesh.Create(presetIndex, isPortrait);

        _meshFrom?.Dispose();
        _meshTo?.Dispose();

        _meshFrom = device.AllocateReadOnlyBuffer(ToFloat2Array(mesh.From));
        _meshTo = device.AllocateReadOnlyBuffer(ToFloat2Array(mesh.To));
        _meshRows = mesh.Rows;
        _meshColumns = mesh.Columns;
        _meshIsPortrait = isPortrait;
    }

    private static Float2[] ToFloat2Array(Vector2[] source)
    {
        var result = new Float2[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            result[i] = new Float2(source[i].X, source[i].Y);
        }

        return result;
    }

    private void EnsureKernel()
    {
        if (_blurOffsets is not null)
        {
            return;
        }

        GraphicsDevice device = _device
            ?? throw new InvalidOperationException("Initialize must be called before dispatch.");

        _blurOffsets = device.AllocateReadOnlyBuffer(BlurOffsets);
        _blurWeights = device.AllocateReadOnlyBuffer(BlurWeights);
    }

/// <summary>
    /// One-shot artwork loader that runs on the render thread the first time
    /// <see cref="TryExecute"/> is called. Must never be invoked from the UI thread:
    /// the underlying <see cref="GraphicsDevice"/> is shared with the render loop, and
    /// racing it with <see cref="GraphicsDevice.ForEach"/> tears the D3D12 device down.
    /// </summary>
    private void EnsureArtwork()
    {
        if (_artwork is not null)
        {
            return;
        }

        GraphicsDevice device = _device
            ?? throw new InvalidOperationException("Initialize must be called before dispatch.");

        byte[]? pixels = null;
        int width = 0;
        int height = 0;

        try
        {
            (pixels, width, height) = DecodeArtworkBlocking(DefaultArtworkPath);
        }
        catch (Exception ex)
        {
            // A missing artwork is the most common misconfiguration: the original
            // path on the dev machine has been hard-coded as the default. Rather than
            // silently uploading a 1×1 black texture on every retry (which masks the
            // real problem and historically pushed the run into a multi-threaded
            // device-record race that crashed dwm.exe), surface the failure.
            Debug.WriteLine(
                    $"[AppleMusic] artwork load failed for '{DefaultArtworkPath}': {ex.Message}. " +
                    "Set ComputeSharpDemo.Shaders.AppleMusic.AppleMusicPass.DefaultArtworkPath to a valid PNG.");

            if (ThrowOnArtworkFailure)
            {
                throw;
            }

            // Release-build fallback: upload a 1×1 black texture exactly once and
            // remember the size, so subsequent frames don't re-trigger the decode
            // (which would otherwise keep the render loop's exception handler busy).
            pixels = new byte[] { 0, 0, 0, 255 };
            width = 1;
            height = 1;
        }

        var rgba = new Rgba32[pixels.Length / 4];

        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = new Rgba32(
                pixels[i * 4],
                pixels[i * 4 + 1],
                pixels[i * 4 + 2],
                pixels[i * 4 + 3]);
        }

        _artwork = device.AllocateReadOnlyTexture2D<Rgba32, Float4>(rgba, width, height);
        _artworkSize = new Int2(width, height);
    }

    /// <summary>
    /// Blocking decode of the artwork. WinRT continuations must marshal back to a
    /// dispatcher: this method is intentionally called from the render thread, which
    /// has no dispatcher, so the decode runs entirely on a thread-pool thread via
    /// <see cref="Task.Run"/> and is then awaited synchronously.
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) DecodeArtworkBlocking(string path)
    {
        // Task.Run keeps the WinRT continuations off the UI synchronization context,
        // so blocking here cannot deadlock even if the caller happens to be the UI thread.
        return Task.Run(() => DecodeArtworkAsync(path)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Decodes an image file to straight-alpha RGBA8 rows (top row first) via WIC,
    /// downscaling to at most <see cref="MaximumArtworkDimension"/> per edge.
    /// </summary>
    private static async Task<(byte[] Pixels, int Width, int Height)> DecodeArtworkAsync(string path)
    {
        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(path);
        }
        catch (System.IO.FileNotFoundException)
        {
            // Re-throw synchronously — StorageFile surfaces the file-missing condition
            // as a different exception type that callers may not recognise.
            throw;
        }

        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

        uint width = decoder.OrientedPixelWidth;
        uint height = decoder.OrientedPixelHeight;
        double scale = Math.Min(
            1d,
            MaximumArtworkDimension / (double)Math.Max(width, height));

        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1u, (uint)Math.Round(width * scale)),
            ScaledHeight = Math.Max(1u, (uint)Math.Round(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        PixelDataProvider provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        byte[] pixels = provider.DetachPixelData();

        return (pixels, (int)transform.ScaledWidth, (int)transform.ScaledHeight);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _rotationSurface?.Dispose();
        _horizontalBlurSurface?.Dispose();
        _verticalBlurSurface?.Dispose();
        _blurOffsets?.Dispose();
        _blurWeights?.Dispose();
        _meshFrom?.Dispose();
        _meshTo?.Dispose();
        _artwork?.Dispose();
    }
}
