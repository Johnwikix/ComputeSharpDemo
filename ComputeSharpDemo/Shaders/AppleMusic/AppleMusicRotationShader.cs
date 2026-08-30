using ComputeSharp;

namespace ComputeSharpDemo.Shaders.AppleMusic;

/// <summary>
/// Backdrop pass of the Apple Music inspired background, ported from the
/// <c>RotationVertex</c> / <c>ArtworkFillVertex</c> / <c>RotationPixel</c> pair of
/// Lyricify-Backgrounds (Apache 2.0).
///
/// <para>
/// The original pass rasterizes four quads on the D3D11 graphics pipeline: one aspect-fill
/// quad underneath and three instanced rotating artwork quads on top (iOS 16.3
/// RotatingArtworkRenderer: model 0 scale 1.4 at the origin, models 1/2 scale 0.7 offset,
/// model 2 parented to model 0's rotation). ComputeSharp has no vertex shaders, so this
/// compute shader runs the transform chain backwards per pixel: each rotating quad's
/// transform is affine, hence inverting it is exact and coverage is a simple [-1,1] square
/// test. Layers are evaluated topmost-first (instance 2, 1, 0), matching the original
/// draw order, with the aspect-fill layer as fallback.
/// </para>
/// <para>
/// Dispatched at 1/4 output resolution (the surface the gaussian blur consumes). Artwork
/// is sampled with manual clamp bilinear filtering to avoid relying on ComputeSharp's
/// static sampler (which uses mirror addressing).
/// </para>
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct AppleMusicRotationShader(
    IReadOnlyNormalizedTexture2D<Float4> artwork,
    Int2 artworkSize,
    Float2 backdropSize,
    float time,
    Float2 viewScale,
    float rotationScale,
    float imageScale) : IComputeShader<Float4>
{
    private const float TwoPi = 6.2831853071795864769f;

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;
        if (xy.X >= backdropSize.X || xy.Y >= backdropSize.Y)
        {
            return Float4.Zero;
        }

        float u = (xy.X + 0.5f) / backdropSize.X;
        float v = (xy.Y + 0.5f) / backdropSize.Y;

        // Screen uv (0,0 = top left) to clip space (NDC +Y is up in D3D).
        Float2 ndc = new(u * 2f - 1f, 1f - v * 2f);

        // Topmost first, matching the original draw order (instances 0,1,2 drawn in
        // order, so 2 wins). A miss is signaled through the alpha channel.
        Float4 top = SampleRotatingInstance(2, ndc);

        if (top.W > 0f)
        {
            return top;
        }

        Float4 middle = SampleRotatingInstance(1, ndc);

        if (middle.W > 0f)
        {
            return middle;
        }

        Float4 bottom = SampleRotatingInstance(0, ndc);

        if (bottom.W > 0f)
        {
            return bottom;
        }

        // Aspect-fill copy underneath the moving layers (ArtworkFillVertex): the fill
        // quad's texture coordinate expands beyond [0,1] by the view scale, so the
        // artwork covers the whole surface regardless of aspect ratio.
        Float2 fillUv = new(
            (u - 0.5f) / viewScale.X + 0.5f,
            (v - 0.5f) / viewScale.Y + 0.5f);

        return new Float4(SampleArtwork(fillUv).XYZ, 1f);
    }

    /// <summary>
    /// Inverts <c>RotationVertex</c> for one instance. Returns the sampled color with
    /// alpha 1 on hit, or alpha 0 when the pixel is outside the rotated quad.
    /// </summary>
    private Float4 SampleRotatingInstance(int instanceId, Float2 ndc)
    {
        // Forward chain (RotationVertex): rotate by local angle, scale by the model
        // matrix, translate, scale by the view aspect, apply the parent rotation for
        // model 2, then scale the material as a whole. Inverted below in reverse order.
        Float2 position = new(ndc.X / imageScale, ndc.Y / imageScale);

        if (instanceId == 2)
        {
            float parentAngle = time * rotationScale * TwoPi / RotationTimeScale(0);
            position = RotateCounterClockwise(position, -parentAngle);
        }

        position = new Float2(position.X / viewScale.X, position.Y / viewScale.Y);

        Float2 translation = ModelTranslation(instanceId);
        position = new Float2(position.X - translation.X, position.Y - translation.Y);

        float modelScale = ModelScale(instanceId);
        position = new Float2(position.X / modelScale, position.Y / modelScale);

        float angle = time * rotationScale * TwoPi / RotationTimeScale(instanceId);
        position = RotateCounterClockwise(position, -angle);

        if (Hlsl.Abs(position.X) > 1f || Hlsl.Abs(position.Y) > 1f)
        {
            return Float4.Zero;
        }

        // Quad corners map texture coordinates ((local.x+1)/2, (1-local.y)/2), so the
        // artwork stays upright (local y +1 = screen top = texture row 0).
        Float2 uv = new((position.X + 1f) * 0.5f, (1f - position.Y) * 0.5f);

        return new Float4(SampleArtwork(uv).XYZ, 1f);
    }

    private static Float2 RotateCounterClockwise(Float2 value, float angle)
    {
        float sine = Hlsl.Sin(angle);
        float cosine = Hlsl.Cos(angle);

        return new Float2(
            cosine * value.X - sine * value.Y,
            sine * value.X + cosine * value.Y);
    }

    // iOS 16.3 constructor: model 0 = scale 1.4, models 1 and 2 = scale 0.7.
    private static float ModelScale(int instanceId)
    {
        return instanceId == 0 ? 1.4f : 0.7f;
    }

    private static Float2 ModelTranslation(int instanceId)
    {
        if (instanceId == 1)
        {
            return new Float2(-0.25f, 0.15f);
        }

        if (instanceId == 2)
        {
            return new Float2(0.7f, 0.7f);
        }

        return new Float2(0f, 0f);
    }

    // iOS 16.3 RotatingArtworkRenderer: model 0 = 120 s, model 1 = 70 s, model 2 = 90 s.
    private static float RotationTimeScale(int instanceId)
    {
        if (instanceId == 1)
        {
            return 70f;
        }

        if (instanceId == 2)
        {
            return 90f;
        }

        return 120f;
    }

    /// <summary>
    /// Clamp-addressed bilinear sample (replaces the original LinearClampSampler).
    /// </summary>
    private Float4 SampleArtwork(Float2 uv)
    {
        float x = uv.X * artworkSize.X - 0.5f;
        float y = uv.Y * artworkSize.Y - 0.5f;
        int x0 = (int)Hlsl.Floor(x);
        int y0 = (int)Hlsl.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        int x1 = x0 + 1;
        int y1 = y0 + 1;
        x0 = Hlsl.Clamp(x0, 0, artworkSize.X - 1);
        x1 = Hlsl.Clamp(x1, 0, artworkSize.X - 1);
        y0 = Hlsl.Clamp(y0, 0, artworkSize.Y - 1);
        y1 = Hlsl.Clamp(y1, 0, artworkSize.Y - 1);

        Float4 c00 = artwork[x0, y0];
        Float4 c10 = artwork[x1, y0];
        Float4 c01 = artwork[x0, y1];
        Float4 c11 = artwork[x1, y1];

        Float4 top = new(
            Hlsl.Lerp(c00.X, c10.X, fx),
            Hlsl.Lerp(c00.Y, c10.Y, fx),
            Hlsl.Lerp(c00.Z, c10.Z, fx),
            Hlsl.Lerp(c00.W, c10.W, fx));

        Float4 bottom = new(
            Hlsl.Lerp(c01.X, c11.X, fx),
            Hlsl.Lerp(c01.Y, c11.Y, fx),
            Hlsl.Lerp(c01.Z, c11.Z, fx),
            Hlsl.Lerp(c01.W, c11.W, fx));

        return new Float4(
            Hlsl.Lerp(top.X, bottom.X, fy),
            Hlsl.Lerp(top.Y, bottom.Y, fy),
            Hlsl.Lerp(top.Z, bottom.Z, fy),
            Hlsl.Lerp(top.W, bottom.W, fy));
    }
}
