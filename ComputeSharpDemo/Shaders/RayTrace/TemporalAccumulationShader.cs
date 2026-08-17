using ComputeSharp;

namespace ComputeSharpDemo.Shaders.RayTrace;

/// <summary>
/// NRD-style temporal accumulation pass. Combines the current (noisy, 2 SPP) frame with
/// the accumulated history using an exponential moving average whose strength depends on
/// a per-pixel confidence derived from the hit distance ratio (NRD "history confidence"
/// analogue — no motion vectors are needed since the scene is static and the host resets
/// <c>frame</c> to 0 whenever the camera changes).
///
/// The signal is stored in display-encoded space (PQ or sRGB, see <see cref="RayTraceShader"/>);
/// RGB holds the radiance, W holds the normalized hit distance <c>t / (t + 1)</c> which is
/// used both for the confidence test and as the spatial filter's edge signal.
///
/// Also performs a 3x3 neighborhood luminance clamp (anti-firefly) before accumulation,
/// mirroring NRD's firefly filtering to keep single-pixel outliers from polluting history.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct TemporalAccumulationShader(
    int frame,
    Float2 iResolution,
    IReadWriteNormalizedTexture2D<Float4> signal,
    IReadWriteNormalizedTexture2D<Float4> history) : IComputeShader<Float4>
{
    /// <summary>Maximum number of frames the history is allowed to accumulate.</summary>
    private const int MaxHistoryFrames = 32;

    /// <summary>Hit distance ratio tolerance before confidence collapses (NRD-style log2 distance).</summary>
    private const float ConfidenceSigma = 5.0f;

    private static float Luminance(Float3 c)
    {
        return 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    }

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;

        Float4 cur = signal[xy];

        // Anti-firefly: clamp the pixel luminance to the min/max of its 3x3 neighborhood,
        // so isolated high-energy outliers cannot accumulate into the history.
        float minL = Luminance(cur.RGB);
        float maxL = minL;

        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                Int2 p = new(xy.X + ox, xy.Y + oy);

                if (p.X < 0 || p.X >= iResolution.X || p.Y < 0 || p.Y >= iResolution.Y)
                {
                    continue;
                }

                float tapLum = Luminance(signal[p].RGB);
                minL = Hlsl.Min(minL, tapLum);
                maxL = Hlsl.Max(maxL, tapLum);
            }
        }

        float lum = Luminance(cur.RGB);
        cur.RGB *= Hlsl.Clamp(lum, minL, maxL) / (lum + 1e-4f);

        // History accumulation with hit-distance confidence. The confidence is 1 while the
        // current hit distance ratio is within a small band around 1, and drops to 0 at a
        // 2x discrepancy (disocclusion / different surface).
        Float4 hist = history[xy];

        float ratio = cur.W / (hist.W + 1e-5f);
        float confidence = Hlsl.Saturate(1.0f - Hlsl.Abs(Hlsl.Log2(ratio)) * ConfidenceSigma);

        bool accept = frame > 0 && confidence > 0.0f;
        float strength = accept ? Hlsl.Min((float)frame, (float)MaxHistoryFrames) * confidence : 0.0f;

        Float3 rgb = accept ? (cur.RGB + hist.RGB * strength) / (1.0f + strength) : cur.RGB;
        float encDist = accept ? (cur.W + hist.W * strength) / (1.0f + strength) : cur.W;

        return new Float4(rgb, encDist);
    }
}