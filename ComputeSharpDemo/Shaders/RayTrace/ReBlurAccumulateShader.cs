using ComputeSharp;

namespace ComputeSharpDemo.Shaders.RayTrace;

/// <summary>
/// ReBLUR-style temporal accumulation with hierarchical (camera) reprojection. The pass
/// reconstructs the world-space position of the current hit — from the camera basis and the
/// encoded hit distance written by <see cref="RayTraceShader"/> — and reprojects it into the
/// previous frame's camera, sampling the accumulated history with a manual bilinear filter.
///
/// A hit-distance disocclusion test gates the blend: confident pixels accumulate toward a
/// clean history, disoccluded ones adopt the current frame and let their history length
/// decay. The per-pixel history length is tracked in a dedicated buffer and drives the
/// hierarchical blur radius in <see cref="ReBlurBlurShader"/>, so regions that just lost
/// their history are reconstructed from coarse mips first (hierarchical history
/// reconstruction, ReBLUR / GTC 2020 "Fast Denoising with Self-Stabilizing Recurrent Blurs").
///
/// Outputs (single dispatch):
///   - target (Rgba64): accumulated color (RGB, display-encoded) + denoised hit distance (W),
///   - variance out (R16): EMA of the squared luminance deviation, floored by the 3x3
///     spatial variance of the current frame,
///   - history length out (R16): grows with confident accumulation, decays on disocclusion,
///   - moment out (R16): the denoised hit distance, source for the distance mip chain.
///
/// The camera frame follows the same convention as <see cref="RayTraceShader"/>: the
/// unnormalized view ray is <c>u * halfWidth * ndcX + v * halfHeight * ndcY - w</c> with
/// ndc computed from the pixel center, and W holds the encoded distance <c>t / (t + 1)</c>
/// (1 for the sky).
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ReBlurAccumulateShader(
    Float2 iResolution,
    float halfWidth,
    float halfHeight,
    Float3 camOrigin,
    Float3 camU,
    Float3 camV,
    Float3 camW,
    Float3 prevOrigin,
    Float3 prevU,
    Float3 prevV,
    Float3 prevW,
    IReadWriteNormalizedTexture2D<Float4> signal,
    IReadWriteNormalizedTexture2D<Float4> historyIn,
    IReadWriteNormalizedTexture2D<float> varianceIn,
    IReadWriteNormalizedTexture2D<float> historyLengthIn,
    IReadWriteNormalizedTexture2D<float> varianceOut,
    IReadWriteNormalizedTexture2D<float> historyLengthOut,
    IReadWriteNormalizedTexture2D<float> momentOut) : IComputeShader<Float4>
{
    /// <summary>
    /// Relative hit-distance tolerance of the disocclusion test. Confidence is 1 while the
    /// reprojected history distance is within a small band around the current distance and
    /// collapses beyond it (NRD-style confidence).
    /// </summary>
    private const float ConfidenceDistSigma = 0.08f;

    /// <summary>
    /// Variance normalization constant of the accumulation weight: pixels whose variance is
    /// large relative to this accumulate fast, converged ones keep most of the history.
    /// </summary>
    private const float VarianceBase = 0.01f;

    private static float Luminance(Float3 c)
    {
        return 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    }

    private static Float3 Scale(Float3 v, float s)
    {
        return new Float3(v.X * s, v.Y * s, v.Z * s);
    }

    // Inverse of the encoded hit distance; the sky (encoded 1) maps to a very large value
    // so sky pixels reproject "at infinity" and sky-vs-sky deltas stay ~0.
    private static float ToMeters(float enc)
    {
        return enc >= 0.999f ? 1000000.0f : enc / (1.0f - enc);
    }

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;

        Float4 cur = signal[xy];

        // Single 3x3 scan over the current frame: firefly clamp range, per-channel AABB for
        // the history clamp, and the spatial luminance variance (fallback for the variance
        // estimate when no usable history exists).
        Float3 minC = cur.RGB;
        Float3 maxC = cur.RGB;
        float minL = Luminance(cur.RGB);
        float maxL = minL;
        float lumMean = 0.0f;
        float lumMeanSq = 0.0f;

        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                Int2 p = new(xy.X + ox, xy.Y + oy);

                if (p.X < 0 || p.X >= iResolution.X || p.Y < 0 || p.Y >= iResolution.Y)
                {
                    continue;
                }

                Float3 tap = signal[p].RGB;
                float tapL = Luminance(tap);

                minC = Hlsl.Min(minC, tap);
                maxC = Hlsl.Max(maxC, tap);
                minL = Hlsl.Min(minL, tapL);
                maxL = Hlsl.Max(maxL, tapL);
                lumMean += tapL;
                lumMeanSq += tapL * tapL;
            }
        }

        float spatialVar = Hlsl.Max(lumMeanSq / 9.0f - (lumMean / 9.0f) * (lumMean / 9.0f), 0.0f);

        // Anti-firefly: clamp the pixel luminance to the neighborhood min/max so isolated
        // high-energy outliers cannot accumulate into the history.
        float lum = Luminance(cur.RGB);
        cur.RGB *= Hlsl.Clamp(lum, minL, maxL) / (lum + 1e-4f);

        // Current hit in world space: the ray is u*halfWidth*ndcX + v*halfHeight*ndcY - w and
        // W holds the true distance, so the hit is origin + dir * (dist / |dir|).
        float ndcX = (xy.X + 0.5f) / iResolution.X;
        float ndcY = (iResolution.Y - (xy.Y + 0.5f)) / iResolution.Y;

        Float3 dir = Scale(camU, halfWidth * ndcX) + Scale(camV, halfHeight * ndcY) - camW;
        float dirLen = Hlsl.Sqrt(1.0f
            + halfWidth * halfWidth * ndcX * ndcX
            + halfHeight * halfHeight * ndcY * ndcY);

        float distCur = ToMeters(cur.W);
        Float3 worldPos = camOrigin + dir * (distCur / dirLen);

        // Reproject into the previous camera frame.
        Float3 d = worldPos - prevOrigin;
        float zPrev = -Hlsl.Dot(d, prevW);
        float ndcXp = Hlsl.Dot(d, prevU) / (halfWidth * zPrev);
        float ndcYp = Hlsl.Dot(d, prevV) / (halfHeight * zPrev);

        Float3 histColor = cur.RGB;
        float histDist = cur.W;
        float histVar = 0.0f;
        float histLen = 0.0f;
        float confidence = 0.0f;

        bool reprojValid = zPrev > 0.01f
            && ndcXp > -0.25f && ndcXp < 1.25f
            && ndcYp > -0.25f && ndcYp < 1.25f;

        if (reprojValid)
        {
            // Manual bilinear sample of the history at the reprojected location.
            float fx = ndcXp * iResolution.X - 0.5f;
            float fy = (1.0f - ndcYp) * iResolution.Y - 0.5f;

            float cx0 = Hlsl.Clamp(Hlsl.Floor(fx), 0.0f, iResolution.X - 2.0f);
            float cy0 = Hlsl.Clamp(Hlsl.Floor(fy), 0.0f, iResolution.Y - 2.0f);
            float tx = Hlsl.Saturate(fx - cx0);
            float ty = Hlsl.Saturate(fy - cy0);

            Int2 p00 = new((int)cx0, (int)cy0);
            Int2 p10 = new((int)(cx0 + 1.0f), (int)cy0);
            Int2 p01 = new((int)cx0, (int)(cy0 + 1.0f));
            Int2 p11 = new((int)(cx0 + 1.0f), (int)(cy0 + 1.0f));

            float w00 = (1.0f - tx) * (1.0f - ty);
            float w10 = tx * (1.0f - ty);
            float w01 = (1.0f - tx) * ty;
            float w11 = tx * ty;

            Float4 h00 = historyIn[p00];
            Float4 h10 = historyIn[p10];
            Float4 h01 = historyIn[p01];
            Float4 h11 = historyIn[p11];

            histColor = h00.RGB * w00 + h10.RGB * w10 + h01.RGB * w01 + h11.RGB * w11;
            histDist = h00.W * w00 + h10.W * w10 + h01.W * w01 + h11.W * w11;
            histVar = varianceIn[p00] * w00 + varianceIn[p10] * w10 + varianceIn[p01] * w01 + varianceIn[p11] * w11;
            histLen = historyLengthIn[p00] * w00 + historyLengthIn[p10] * w10 + historyLengthIn[p01] * w01 + historyLengthIn[p11] * w11;

            // Disocclusion confidence from the hit-distance delta at the reprojected location.
            float distPrev = ToMeters(histDist);
            confidence = Hlsl.Exp(-Hlsl.Abs(distCur - distPrev) / (ConfidenceDistSigma * Hlsl.Max(distCur, 0.05f)));
            confidence = Hlsl.Saturate(confidence);
        }

        Float3 newRgb;
        float newDist;
        float newVar;
        float newLen;

        if (confidence < 0.5f)
        {
            // Disoccluded (or no history): adopt the current frame and seed the variance
            // estimate with the neighborhood variance; the history length drops to zero so
            // the hierarchical blur reconstructs this pixel from coarse mips.
            newRgb = cur.RGB;
            newDist = cur.W;
            newVar = spatialVar;
            newLen = 0.0f;
        }
        else
        {
            // Clamp the reprojected history to the current frame's per-channel AABB, then
            // blend with a variance-driven weight: noisy pixels accumulate fast, converged
            // ones keep most of the history.
            Float3 clamped = Hlsl.Clamp(histColor, minC, maxC);

            float varN = histVar / (histVar + VarianceBase);
            float alpha = Hlsl.Clamp(confidence * (0.3f + 0.7f * (1.0f - varN)), 0.05f, 1.0f);

            newRgb = Hlsl.Lerp(clamped, cur.RGB, alpha);
            newDist = Hlsl.Lerp(histDist, cur.W, alpha);

            // Variance estimate: EMA of the squared luminance deviation, floored by the
            // spatial variance; drives both the accumulation weight and the blur radius.
            float dev = lum - Luminance(histColor);
            newVar = Hlsl.Max(Hlsl.Lerp(histVar, dev * dev, alpha), spatialVar);

            // History length: grows with confident accumulation, halves on a weak confidence.
            newLen = Hlsl.Clamp(histLen + (1.0f - histLen) * alpha * confidence, 0.0f, 1.0f)
                * Hlsl.Lerp(0.5f, 1.0f, confidence);
        }

        varianceOut[xy] = newVar;
        historyLengthOut[xy] = newLen;
        momentOut[xy] = newDist;

        return new Float4(newRgb, newDist);
    }
}