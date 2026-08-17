using ComputeSharp;

namespace ComputeSharpDemo.Shaders.RayTrace;

/// <summary>
/// NRD-style spatial filtering pass: one level of an A-trous wavelet filter with
/// cross-bilateral (edge-avoiding) weights. The kernel is dispatched twice with an
/// increasing <c>step</c> (1, then 2), ping-ponging between two buffers.
///
/// Edge stopping uses the NRD guide signals:
///   - world-space normals (stored in <paramref name="normalTexture"/>.RGB)
///   - normalized hit distance (signal W channel, decoded back to meters here)
///   - material id (stored in <paramref name="normalTexture"/>.W) as a hard edge stop
///
/// Sky pixels carry a zero normal + material id 3 and filter among themselves only.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct SpatialFilterShader(
    int step,
    Float2 iResolution,
    IReadWriteNormalizedTexture2D<Float4> signalIn,
    IReadWriteNormalizedTexture2D<Float4> normalTexture) : IComputeShader<Float4>
{
    /// <summary>Standard deviation of the normal-difference Gaussian (1 - |dot(n0, n1)|).</summary>
    private const float NormalSigma = 0.15f;

    /// <summary>Standard deviation of the hit-distance-difference Gaussian (relative distance).</summary>
    private const float DepthSigma = 0.7f;

    /// <summary>
    /// Strength of the NRD-style detail recovery: where the neighborhood disagrees (edges,
    /// high-frequency detail) the filtered mean is unreliable, so a fraction of the unfiltered
    /// center is blended back in to restore the sharpness lost to the blur.
    /// </summary>
    private const float DetailStrength = 0.45f;

    private static Float3 DecodeNormal(Float4 n)
    {
        return n.RGB * 2.0f - 1.0f;
    }

    private static bool IsSky(Float3 n)
    {
        return Hlsl.Length(n) < 0.5f;
    }

    private static float ToMeters(float enc)
    {
        return enc / (1.0f - enc);
    }

    private static float ComputeWeight(Float3 n0, float t0, float m0, Float3 n1, float t1, float m1)
    {
        bool sky0 = IsSky(n0);
        bool sky1 = IsSky(n1);

        if (sky0 && sky1)
        {
            return 1.0f;
        }

        if (sky0 != sky1 || m0 != m1)
        {
            return 0.0f;
        }

        float normalDiff = 1.0f - Hlsl.Dot(n0, n1);
        float depthDiff = Hlsl.Abs(t1 - t0) / Hlsl.Max(t0, 0.5f);

        float wN = Hlsl.Exp(-(normalDiff * normalDiff) / (2.0f * NormalSigma * NormalSigma));
        float wT = Hlsl.Exp(-(depthDiff * depthDiff) / (2.0f * DepthSigma * DepthSigma));

        return wN * wT;
    }

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;

        Float4 c = signalIn[xy];
        Float4 n4 = normalTexture[xy];

        Float3 n0 = DecodeNormal(n4);
        float t0 = ToMeters(c.W);
        float m0 = n4.W;

        Float3 acc = c.RGB;
        float wSum = 1.0f;

        // A-trous taps on the 4 diagonals — with two levels (step 1, 2) the combined
        // support covers a 5x5 neighborhood like NRD's early RELAX iterations.
        Int2 p1 = new Int2(xy.X + step, xy.Y + step);
        Int2 p2 = new Int2(xy.X + step, xy.Y - step);
        Int2 p3 = new Int2(xy.X - step, xy.Y + step);
        Int2 p4 = new Int2(xy.X - step, xy.Y - step);

        Float4 t;
        Float4 n;
        float w;
        Int2 p;

        p = p1;
        if (p.X >= 0 && p.X < iResolution.X && p.Y >= 0 && p.Y < iResolution.Y)
        {
            t = signalIn[p];
            n = normalTexture[p];
            w = ComputeWeight(n0, t0, m0, DecodeNormal(n), ToMeters(t.W), n.W);
            acc += t.RGB * w;
            wSum += w;
        }

        p = p2;
        if (p.X >= 0 && p.X < iResolution.X && p.Y >= 0 && p.Y < iResolution.Y)
        {
            t = signalIn[p];
            n = normalTexture[p];
            w = ComputeWeight(n0, t0, m0, DecodeNormal(n), ToMeters(t.W), n.W);
            acc += t.RGB * w;
            wSum += w;
        }

        p = p3;
        if (p.X >= 0 && p.X < iResolution.X && p.Y >= 0 && p.Y < iResolution.Y)
        {
            t = signalIn[p];
            n = normalTexture[p];
            w = ComputeWeight(n0, t0, m0, DecodeNormal(n), ToMeters(t.W), n.W);
            acc += t.RGB * w;
            wSum += w;
        }

        p = p4;
        if (p.X >= 0 && p.X < iResolution.X && p.Y >= 0 && p.Y < iResolution.Y)
        {
            t = signalIn[p];
            n = normalTexture[p];
            w = ComputeWeight(n0, t0, m0, DecodeNormal(n), ToMeters(t.W), n.W);
            acc += t.RGB * w;
            wSum += w;
        }

        Float3 mean = acc / wSum;

        // Detail recovery: in fully agreeing neighborhoods (flat areas, sky, whose taps always
        // carry weight 1) the filtered mean is kept as-is; where taps were rejected the detail
        // term rises and part of the unfiltered center is restored, recovering edge sharpness.
        float flat = Hlsl.Saturate((wSum - 1.0f) / 4.0f);
        float detail = (1.0f - flat) * (1.0f - flat) * DetailStrength;

        Float3 restored = mean + (c.RGB - mean) * detail;

        return new Float4(restored.X, restored.Y, restored.Z, c.W);
    }
}