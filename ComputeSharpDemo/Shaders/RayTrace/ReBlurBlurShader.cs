using ComputeSharp;

namespace ComputeSharpDemo.Shaders.RayTrace;

/// <summary>
/// ReBLUR-style hierarchical blur. Unlike the fixed-step A-trous of
/// <see cref="SpatialFilterShader"/>, the 5-tap binomial kernel has a per-pixel, continuous
/// radius that combines three ReBLUR signals (GTC 2020 "Fast Denoising with Self-Stabilizing
/// Recurrent Blurs"):
///   - variance: noisy regions blur fast, <c>r ~ sqrt(var)</c>,
///   - hierarchical history reconstruction: <c>mipLevel = 4 * (1 - normAccumFrames) *
///     roughness</c> — regions that lost their history (or have rough surfaces) reconstruct
///     from coarse mips, converged ones stay sharp,
///   - accumulated-frame damping: the radius shrinks as the history converges.
///
/// Color and hit distance are read from the mip level matching the radius (full-res for
/// level 0), with normal / hit-distance / roughness / sky edge stops on every tap.
///
/// The pass runs four times per frame (horizontal + vertical x two iterations, ping-ponging
/// through two buffers). The last one additionally performs the temporal stabilization:
/// the result is mixed with a small fraction of the current frame proportional to
/// <c>1 - historyLength</c> (no additional lag, TAA-style), written to the display texture
/// and stored as the next frame's history in the same dispatch.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ReBlurBlurShader(
    int passIndex,
    Float2 iResolution,
    IReadWriteNormalizedTexture2D<Float4> colorIn,
    IReadWriteNormalizedTexture2D<Float4> colorMip1,
    IReadWriteNormalizedTexture2D<Float4> colorMip2,
    IReadWriteNormalizedTexture2D<Float4> colorMip3,
    IReadWriteNormalizedTexture2D<Float4> colorMip4,
    IReadWriteNormalizedTexture2D<float> distMip1,
    IReadWriteNormalizedTexture2D<float> distMip2,
    IReadWriteNormalizedTexture2D<float> distMip3,
    IReadWriteNormalizedTexture2D<float> distMip4,
    IReadWriteNormalizedTexture2D<float> varianceIn,
    IReadWriteNormalizedTexture2D<float> historyLengthIn,
    IReadWriteNormalizedTexture2D<Float4> normalTexture,
    IReadWriteNormalizedTexture2D<Float4> signal,
    IReadWriteNormalizedTexture2D<Float4> historyOut) : IComputeShader<Float4>
{
    /// <summary>Maximum blur radius in pixels.</summary>
    private const float MaxBlurRadius = 16.0f;

    /// <summary>Scale of the variance-driven radius term (radius ~ sqrt(var) * scale).</summary>
    private const float VarianceRadiusScale = 16.0f;

    /// <summary>Variance radius clamp range (radius ~ sqrt(var) * scale).</summary>
    private const float VarianceRadiusMax = 8.0f;

    /// <summary>Standard deviation of the normal-difference Gaussian (1 - |dot(n0, n1)|).</summary>
    private const float NormalSigma = 0.15f;

    /// <summary>Base standard deviation of the hit-distance-difference weight.</summary>
    private const float DepthSigma = 0.7f;

    /// <summary>Maximum material-roughness delta for a tap to contribute.</summary>
    private const float RoughnessThreshold = 0.25f;

    /// <summary>Mix of the current (noisy) frame in the temporal stabilization.</summary>
    private const float StabilizationScale = 0.35f;

    /// <summary>Cap of the temporal stabilization mix.</summary>
    private const float StabilizationMax = 0.4f;

    private static Float3 Scale(Float3 v, float s)
    {
        return new Float3(v.X * s, v.Y * s, v.Z * s);
    }

    private static Float3 DecodeNormal(Float4 n)
    {
        return n.RGB * 2.0f - 1.0f;
    }

    private static bool IsSky(Float3 n)
    {
        return Hlsl.Length(n) < 0.5f;
    }

    // Inverse of the encoded hit distance; the sky (encoded 1) maps to a very large value.
    private static float ToMeters(float enc)
    {
        return enc >= 0.999f ? 1000000.0f : enc / (1.0f - enc);
    }

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;

        float histLen = historyLengthIn[xy];
        float normAcc = Hlsl.Min(histLen, 1.0f);

        float roughness = normalTexture[xy].W;
        float variance = varianceIn[xy];

        // Blur radius: max of the variance-driven and the hierarchical-history terms,
        // damped as the history converges; the second iteration uses a smaller footprint.
        float rVar = Hlsl.Clamp(Hlsl.Sqrt(variance) * VarianceRadiusScale, 1.0f, VarianceRadiusMax);
        float mipLevel = 4.0f * (1.0f - normAcc) * roughness;
        float rMip = Hlsl.Exp2(mipLevel);

        float iterScale = passIndex < 2 ? 1.0f : 0.6f;
        float radius = Hlsl.Clamp(
            Hlsl.Max(rVar, rMip) * Hlsl.Lerp(1.0f, 0.5f, normAcc),
            1.0f,
            MaxBlurRadius) * iterScale;

        int level = (int)Hlsl.Clamp(Hlsl.Floor(Hlsl.Log2(radius) + 0.5f), 0.0f, 4.0f);
        float depthSigma = DepthSigma * Hlsl.Pow(1.5f, level);

        bool horizontal = (passIndex & 1) == 0;

        Float3 n0 = DecodeNormal(normalTexture[xy]);

        // Center tap, read from the mip level matching the radius (level 0 = full-res).
        Float4 center;
        float dCenter;
        if (level == 0)
        {
            center = colorIn[xy];
            dCenter = center.W;
        }
        else if (level == 1)
        {
            Int2 q = xy >> 1;
            center = colorMip1[q];
            dCenter = distMip1[q];
        }
        else if (level == 2)
        {
            Int2 q = xy >> 2;
            center = colorMip2[q];
            dCenter = distMip2[q];
        }
        else if (level == 3)
        {
            Int2 q = xy >> 3;
            center = colorMip3[q];
            dCenter = distMip3[q];
        }
        else
        {
            Int2 q = xy >> 4;
            center = colorMip4[q];
            dCenter = distMip4[q];
        }

        float d0 = ToMeters(dCenter);
        bool sky0 = IsSky(n0);

        Float3 acc = center.RGB;
        float dAcc = d0;
        float wSum = 1.0f;

        for (int k = -2; k <= 2; k++)
        {
            if (k == 0)
            {
                continue;
            }

            int offset = (int)Hlsl.Round(radius * k);
            if (offset == 0)
            {
                continue;
            }

            Int2 p = horizontal
                ? new Int2(xy.X + offset, xy.Y)
                : new Int2(xy.X, xy.Y + offset);

            if (p.X < 0 || p.X >= iResolution.X || p.Y < 0 || p.Y >= iResolution.Y)
            {
                continue;
            }

            Float4 tap;
            float tapDist;
            if (level == 0)
            {
                tap = colorIn[p];
                tapDist = tap.W;
            }
            else if (level == 1)
            {
                Int2 q = p >> 1;
                tap = colorMip1[q];
                tapDist = distMip1[q];
            }
            else if (level == 2)
            {
                Int2 q = p >> 2;
                tap = colorMip2[q];
                tapDist = distMip2[q];
            }
            else if (level == 3)
            {
                Int2 q = p >> 3;
                tap = colorMip3[q];
                tapDist = distMip3[q];
            }
            else
            {
                Int2 q = p >> 4;
                tap = colorMip4[q];
                tapDist = distMip4[q];
            }

            float d1 = ToMeters(tapDist);
            Float4 tn = normalTexture[p];
            Float3 n1 = DecodeNormal(tn);

            float w = 0.0f;
            if (!(sky0 != IsSky(n1) || Hlsl.Abs(tn.W - roughness) > RoughnessThreshold))
            {
                float normalDiff = 1.0f - Hlsl.Dot(n0, n1);
                float wN = Hlsl.Exp(-(normalDiff * normalDiff) / (2.0f * NormalSigma * NormalSigma));
                float wD = Hlsl.Exp(-Hlsl.Abs(d1 - d0) / depthSigma);
                w = wN * wD;
            }

            float tapW = k == -2 || k == 2 ? 1.0f / 16.0f : 1.0f / 4.0f;

            acc += tap.RGB * (tapW * w);
            dAcc += d1 * (tapW * w);
            wSum += tapW * w;
        }

        Float3 filtered = acc / wSum;
        float distFiltered = dAcc / wSum;

        if (passIndex == 3)
        {
            // Temporal stabilization: keep a fraction of the current frame proportional to
            // the missing history (anti-lag), then store the result as the next history.
            float stab = Hlsl.Min(StabilizationScale * (1.0f - normAcc), StabilizationMax);

            Float4 result = new(Hlsl.Lerp(filtered, signal[xy].RGB, stab), distFiltered);

            historyOut[xy] = result;

            return result;
        }

        return new Float4(filtered, distFiltered);
    }
}