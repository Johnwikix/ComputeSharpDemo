using ComputeSharp;

namespace ComputeSharpDemo.Shaders.AppleMusic;

/// <summary>
/// Separable gaussian blur pass, ported from the <c>GaussianBlur</c> /
/// <c>BlurHorizontalPixel</c> / <c>BlurVerticalPixel</c> functions of
/// Lyricify-Backgrounds (Apache 2.0).
///
/// <para>
/// Normalized sigma-42.5 kernel with 77 paired bilinear taps, dispatched horizontally and
/// then vertically. The original samples through a zero-border sampler and normalizes the
/// vertical result by the accumulated alpha coverage so the blur fades to black at the
/// edges instead of smearing clamped edge texels. ComputeSharp only exposes a static
/// linear/mirror sampler, so the zero-border bilinear fetch is emulated per tap here.
/// </para>
/// <para>
/// Dispatched twice per frame with <c>direction = (blurScale.x / width, 0)</c> and
/// <c>(0, blurScale.y / height)</c>; <paramref name="normalize"/> must only be set on the
/// vertical pass.
/// </para>
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct AppleMusicBlurShader(
    IReadWriteNormalizedTexture2D<Float4> source,
    Float2 sourceSize,
    ReadOnlyBuffer<float> offsets,
    ReadOnlyBuffer<float> weights,
    Float2 direction,
    bool normalize) : IComputeShader<Float4>
{
    private const float CenterWeight = 0.009389731878f;
    private const int TapCount = 77;

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;
        if (xy.X >= sourceSize.X || xy.Y >= sourceSize.Y)
        {
            return Float4.Zero;
        }

        Float2 uv = new(
            (xy.X + 0.5f) / sourceSize.X,
            (xy.Y + 0.5f) / sourceSize.Y);

        Float4 color = SampleZeroBorder(uv);
        color = new Float4(color.X * CenterWeight, color.Y * CenterWeight, color.Z * CenterWeight, color.W * CenterWeight);

        for (int i = 0; i < TapCount; i++)
        {
            float offset = offsets[i];
            float weight = weights[i];
            Float2 stepForward = new(uv.X + direction.X * offset, uv.Y + direction.Y * offset);
            Float2 stepBackward = new(uv.X - direction.X * offset, uv.Y - direction.Y * offset);
            Float4 forward = SampleZeroBorder(stepForward);
            Float4 backward = SampleZeroBorder(stepBackward);

            color = new Float4(
                color.X + (forward.X + backward.X) * weight,
                color.Y + (forward.Y + backward.Y) * weight,
                color.Z + (forward.Z + backward.Z) * weight,
                color.W + (forward.W + backward.W) * weight);
        }

        if (normalize)
        {
            // Normalize zero-border samples by their accumulated coverage.
            float coverage = Hlsl.Max(color.W, 1f / 65535f);

            color = new Float4(color.X / coverage, color.Y / coverage, color.Z / coverage, 1f);
        }

        return color;
    }

    /// <summary>
    /// Bilinear sample with transparent-black border (replaces the original
    /// LinearZeroBorderSampler): texels outside the surface read as zero.
    /// </summary>
    private Float4 SampleZeroBorder(Float2 uv)
    {
        float x = uv.X * sourceSize.X - 0.5f;
        float y = uv.Y * sourceSize.Y - 0.5f;
        int x0 = (int)Hlsl.Floor(x);
        int y0 = (int)Hlsl.Floor(y);
        float fx = x - x0;
        float fy = y - y0;

        Float4 c00 = Fetch(x0, y0);
        Float4 c10 = Fetch(x0 + 1, y0);
        Float4 c01 = Fetch(x0, y0 + 1);
        Float4 c11 = Fetch(x0 + 1, y0 + 1);

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

    private Float4 Fetch(int x, int y)
    {
        if (x < 0 || y < 0 || x >= sourceSize.X || y >= sourceSize.Y)
        {
            return Float4.Zero;
        }

        return source[x, y];
    }
}
