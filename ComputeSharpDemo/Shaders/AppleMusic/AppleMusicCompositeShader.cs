using ComputeSharp;

namespace ComputeSharpDemo.Shaders.AppleMusic;

/// <summary>
/// Final composite pass of the Apple Music inspired background, ported from the
/// <c>MaterialTreatedPixel</c> / <c>PinchVertex</c> / <c>PinchPixel</c> / <c>FinishMaterial</c>
/// stages of Lyricify-Backgrounds (Apache 2.0).
///
/// <para>
/// The original draws a fullscreen treated layer and then rasterizes an animated pinch
/// mesh (from/to control grids blended by a time-varying mix) on top with a vertex shader.
/// ComputeSharp has no rasterizer, so the mesh warp is inverted per pixel instead: a
/// Newton iteration solves <c>warp(uv) = pixel</c> against the bilinear blend of the
/// deformed control grid, and pixels whose solution lands inside [0,1]² (small residual)
/// are mesh-covered and sample the backdrop at the warped coordinate. This matches the
/// triangle rasterization for these near-identity warps.
/// </para>
/// <para>
/// The mesh positions arrive as two read-only buffers (row-major, v = 1 at row 0) blended
/// in-shader, so no per-frame uploads are needed. Output is encoded for the display
/// exactly like the other demo shaders: PQ (HDR10) or raw display-referred values (SDR).
/// </para>
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct AppleMusicCompositeShader(
    IReadWriteNormalizedTexture2D<Float4> backdrop,
    Float2 backdropSize,
    ReadOnlyBuffer<Float2> meshFrom,
    ReadOnlyBuffer<Float2> meshTo,
    int meshRows,
    int meshColumns,
    float pinchMix,
    Float2 resolution,
    float blackScrimAlpha,
    float ditherStrength,
    float pinchTextureScale,
    float pinchTextureOffset,
    bool isHdrEnabled,
    float sdrWhiteLevelInNits,
    float maxLuminanceInNits) : IComputeShader<Float4>
{
    /// <summary>NDC-space residual below which a pixel counts as mesh-covered (~1 px).</summary>
    private const float CoverageEpsilon = 0.002f;

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;
        if (xy.X >= resolution.X || xy.Y >= resolution.Y)
        {
            return Float4.Zero;
        }

        Float2 uv = new(
            (xy.X + 0.5f) / resolution.X,
            (xy.Y + 0.5f) / resolution.Y);

        // Treated fullscreen layer beneath the mesh (MaterialTreatedPixel): fills gaps
        // exposed by the moving mesh boundary.
        Float3 color = SampleTreatedMaterial(uv);

        // Pinch mesh (PinchVertex + PinchPixel), evaluated by inverting the warp.
        Float2 ndc = new(uv.X * 2f - 1f, 1f - uv.Y * 2f);
        Float4 solution = SolveMeshUv(ndc);

        if (solution.W > 0.5f)
        {
            Float2 meshUv = new(solution.X, solution.Y);
            Float2 textureCoordinate = new(
                meshUv.X * pinchTextureScale + pinchTextureOffset,
                meshUv.Y * pinchTextureScale + pinchTextureOffset);

            color = SampleTreatedMaterial(textureCoordinate);
        }

        color = FinishMaterial(color, new Float2(xy.X + 0.5f, xy.Y + 0.5f));

        if (isHdrEnabled)
        {
            Float3 nits = new(
                Hlsl.Min(color.X * sdrWhiteLevelInNits, maxLuminanceInNits),
                Hlsl.Min(color.Y * sdrWhiteLevelInNits, maxLuminanceInNits),
                Hlsl.Min(color.Z * sdrWhiteLevelInNits, maxLuminanceInNits));

            return new Float4(PqEncode(nits), 1f);
        }

        return new Float4(Hlsl.Saturate(color.X), Hlsl.Saturate(color.Y), Hlsl.Saturate(color.Z), 1f);
    }

    /// <summary>
    /// Newton-solves the mesh warp for one pixel. Returns (u, v, residual, covered) where
    /// the warp is the bilinear interpolation of the blend of the from/to grids.
    ///
    /// <para>
    /// The mesh presets fold in a few cells (the deformation is intentionally liquid), so
    /// the near-identity Newton iteration can oscillate there and fail to converge. Those
    /// pixels fall back to an exhaustive triangle scan that replicates the original
    /// rasterizer exactly (same draw order: the last covering triangle wins), so folded
    /// regions show the overlapping sheet instead of a fallback patch.
    /// </para>
    /// </summary>
    private Float4 SolveMeshUv(Float2 ndc)
    {
        // The grid is close to an identity warp: screen uv is a good starting guess.
        float u = 0.5f * (ndc.X + 1f);
        float v = 0.5f * (1f - ndc.Y);
        Float2 uv = new(u, v);

        float stepU = 0.25f / (meshColumns - 1);
        float stepV = 0.25f / (meshRows - 1);

        for (int iteration = 0; iteration < 5; iteration++)
        {
            Float2 position = Warp(uv);
            Float2 error = new(position.X - ndc.X, position.Y - ndc.Y);

            if (Hlsl.Abs(error.X) < 1e-5f && Hlsl.Abs(error.Y) < 1e-5f)
            {
                break;
            }

            // Numeric Jacobian of the piecewise-bilinear warp.
            Float2 duPosition = Warp(new Float2(uv.X + stepU, uv.Y));
            Float2 dvPosition = Warp(new Float2(uv.X, uv.Y + stepV));
            float jacUx = (duPosition.X - position.X) / stepU;
            float jacUy = (duPosition.Y - position.Y) / stepU;
            float jacVx = (dvPosition.X - position.X) / stepV;
            float jacVy = (dvPosition.Y - position.Y) / stepV;

            float determinant = jacUx * jacVy - jacUy * jacVx;

            if (Hlsl.Abs(determinant) < 1e-9f)
            {
                break;
            }

            // Solve [[jacU][jacV]] * delta = -error.
            float deltaX = (-jacVy * error.X + jacVx * error.Y) / determinant;
            float deltaY = (jacUy * error.X - jacUx * error.Y) / determinant;

            uv = new Float2(
                Hlsl.Clamp(uv.X + deltaX, -0.25f, 1.25f),
                Hlsl.Clamp(uv.Y + deltaY, -0.25f, 1.25f));
        }

        Float2 final = Warp(uv);
        float residual = Hlsl.Length(new Float2(final.X - ndc.X, final.Y - ndc.Y));

        if (residual < CoverageEpsilon)
        {
            return new Float4(uv.X, uv.Y, residual, 1f);
        }

        Float4 scan = ScanTriangles(ndc);

        return new Float4(scan.X, scan.Y, scan.Z, scan.W);
    }

    /// <summary>
    /// Exhaustive point-in-triangle scan over the mesh, mirroring the original index
    /// buffer order (row-major cells, two triangles each; the last covering triangle
    /// wins, matching rasterizer overdraw). Returns (u, v, 0, hit).
    /// </summary>
    private Float4 ScanTriangles(Float2 ndc)
    {
        Float2 bestUv = new(0f, 0f);
        bool hit = false;

        for (int row = 0; row < meshRows - 1; row++)
        {
            for (int column = 0; column < meshColumns - 1; column++)
            {
                int index = row * meshColumns + column;

                Float2 bottomLeft = BlendVertex(index);
                Float2 bottomRight = BlendVertex(index + 1);
                Float2 topLeft = BlendVertex(index + meshColumns);
                Float2 topRight = BlendVertex(index + meshColumns + 1);

                float u0 = column / (float)(meshColumns - 1);
                float u1 = (column + 1) / (float)(meshColumns - 1);
                float v0 = 1f - row / (float)(meshRows - 1);
                float v1 = 1f - (row + 1) / (float)(meshRows - 1);

                // Triangle 1: bottomLeft, topLeft, topRight.
                Float2 uv1 = BarycentricUv(
                    ndc, bottomLeft, topLeft, topRight,
                    new Float2(u0, v0), new Float2(u0, v1), new Float2(u1, v1));

                if (uv1.X >= 0f)
                {
                    bestUv = uv1;
                    hit = true;
                }

                // Triangle 2: topRight, bottomRight, bottomLeft.
                Float2 uv2 = BarycentricUv(
                    ndc, topRight, bottomRight, bottomLeft,
                    new Float2(u1, v1), new Float2(u1, v0), new Float2(u0, v0));

                if (uv2.X >= 0f)
                {
                    bestUv = uv2;
                    hit = true;
                }
            }
        }

        return new Float4(bestUv.X, bestUv.Y, 0f, hit ? 1f : 0f);
    }

    /// <summary>
    /// Barycentric uv of a point inside a triangle, or X = -1 when outside.
    /// </summary>
    private static Float2 BarycentricUv(
        Float2 point,
        Float2 a, Float2 b, Float2 c,
        Float2 uvA, Float2 uvB, Float2 uvC)
    {
        float denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);

        if (Hlsl.Abs(denominator) < 1e-12f)
        {
            return new Float2(-1f, 0f);
        }

        float weightA = ((b.Y - c.Y) * (point.X - c.X) + (c.X - b.X) * (point.Y - c.Y)) / denominator;
        float weightB = ((c.Y - a.Y) * (point.X - c.X) + (a.X - c.X) * (point.Y - c.Y)) / denominator;
        float weightC = 1f - weightA - weightB;

        if (weightA < -0.002f || weightB < -0.002f || weightC < -0.002f)
        {
            return new Float2(-1f, 0f);
        }

        return new Float2(
            weightA * uvA.X + weightB * uvB.X + weightC * uvC.X,
            weightA * uvA.Y + weightB * uvB.Y + weightC * uvC.Y);
    }

    /// <summary>
    /// Forward warp: bilinear interpolation of the per-vertex blend of the from/to grids
    /// (each vertex moves linearly between its two positions, as in <c>PinchVertex</c>).
    /// Positions are in clip space with v = 1 at row 0.
    /// </summary>
    private Float2 Warp(Float2 uv)
    {
        float gridX = uv.X * (meshColumns - 1);
        float gridY = (1f - uv.Y) * (meshRows - 1);
        int column = Hlsl.Clamp((int)gridX, 0, meshColumns - 2);
        int row = Hlsl.Clamp((int)gridY, 0, meshRows - 2);
        float fx = Hlsl.Clamp(gridX - column, 0f, 1f);
        float fy = Hlsl.Clamp(gridY - row, 0f, 1f);
        int index = row * meshColumns + column;

        Float2 p00 = BlendVertex(index);
        Float2 p10 = BlendVertex(index + 1);
        Float2 p01 = BlendVertex(index + meshColumns);
        Float2 p11 = BlendVertex(index + meshColumns + 1);

        Float2 top = new(
            Hlsl.Lerp(p00.X, p10.X, fx),
            Hlsl.Lerp(p00.Y, p10.Y, fx));

        Float2 bottom = new(
            Hlsl.Lerp(p01.X, p11.X, fx),
            Hlsl.Lerp(p01.Y, p11.Y, fx));

        return new Float2(
            Hlsl.Lerp(top.X, bottom.X, fy),
            Hlsl.Lerp(top.Y, bottom.Y, fy));
    }

    private Float2 BlendVertex(int index)
    {
        Float2 from = meshFrom[index];
        Float2 to = meshTo[index];

        return new Float2(
            Hlsl.Lerp(from.X, to.X, pinchMix),
            Hlsl.Lerp(from.Y, to.Y, pinchMix));
    }

    /// <summary>Clamp-addressed bilinear sample of the blurred backdrop.</summary>
    private Float4 SampleBackdrop(Float2 uv)
    {
        float x = uv.X * backdropSize.X - 0.5f;
        float y = uv.Y * backdropSize.Y - 0.5f;
        int x0 = (int)Hlsl.Floor(x);
        int y0 = (int)Hlsl.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        int x1 = x0 + 1;
        int y1 = y0 + 1;
        x0 = Hlsl.Clamp(x0, 0, (int)backdropSize.X - 1);
        x1 = Hlsl.Clamp(x1, 0, (int)backdropSize.X - 1);
        y0 = Hlsl.Clamp(y0, 0, (int)backdropSize.Y - 1);
        y1 = Hlsl.Clamp(y1, 0, (int)backdropSize.Y - 1);

        Float4 c00 = backdrop[x0, y0];
        Float4 c10 = backdrop[x1, y0];
        Float4 c01 = backdrop[x0, y1];
        Float4 c11 = backdrop[x1, y1];

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

    /// <summary>
    /// <c>SampleTreatedMaterial</c>: un-premultiply, then apply the material treatment.
    /// </summary>
    private Float3 SampleTreatedMaterial(Float2 uv)
    {
        Float4 sample = SampleBackdrop(uv);
        float alpha = Hlsl.Max(sample.W, 1f / 65535f);

        return ApplyTreatedMaterial(new Float3(sample.X / alpha, sample.Y / alpha, sample.Z / alpha));
    }

    /// <summary>Saturation matrix tuned for the background material.</summary>
    private static Float3 ApplySaturation(Float3 color, float saturation)
    {
        Float3 redColumn = new(
            0.2126f + 0.7873f * saturation,
            0.2126f - 0.2126f * saturation,
            0.2126f - 0.2126f * saturation);

        Float3 greenColumn = new(
            0.7152f - 0.7152f * saturation,
            0.7152f + 0.2848f * saturation,
            0.7152f - 0.7152f * saturation);

        Float3 blueColumn = new(
            0.0722f - 0.0722f * saturation,
            0.0722f - 0.0722f * saturation,
            0.0722f + 0.9278f * saturation);

        return new Float3(
            redColumn.X * color.X + greenColumn.X * color.Y + blueColumn.X * color.Z,
            redColumn.Y * color.X + greenColumn.Y * color.Y + blueColumn.Y * color.Z,
            redColumn.Z * color.X + greenColumn.Z * color.Y + blueColumn.Z * color.Z);
    }

    private Float3 ApplyTreatedMaterial(Float3 color)
    {
        // Reduce saturation before the final composition pass.
        color = ApplySaturation(color, 1.4f);
        color = new Float3(
            Hlsl.Clamp(color.X, -0.752941f, 1.25098f),
            Hlsl.Clamp(color.Y, -0.752941f, 1.25098f),
            Hlsl.Clamp(color.Z, -0.752941f, 1.25098f));
        color = ApplySaturation(color, 0.70f);

        float keep = 1f - blackScrimAlpha;

        return new Float3(color.X * keep, color.Y * keep, color.Z * keep);
    }

    /// <summary>
    /// <c>FinishMaterial</c>: half-LSB noise reduces banding when the result is quantized.
    /// </summary>
    private Float3 FinishMaterial(Float3 color, Float2 pixelPosition)
    {
        float dither = Hlsl.Frac(
            52.9829189f * Hlsl.Frac(Hlsl.Dot(pixelPosition, new Float2(0.06711056f, 0.00583715f)))) - 0.5f;

        float strength = ditherStrength / 255f;

        return new Float3(
            Hlsl.Clamp(color.X + dither * strength, 0.07f, 0.97f),
            Hlsl.Clamp(color.Y + dither * strength, 0.07f, 0.97f),
            Hlsl.Clamp(color.Z + dither * strength, 0.07f, 0.97f));
    }

    // ST 2084 (PQ) inverse EOTF, mapping linear luminance in nits to [0, 1] signal values.
    private static Float3 PqEncode(Float3 linearNits)
    {
        Float3 n = new(
            Hlsl.Max(linearNits.X, 0f) / 10000f,
            Hlsl.Max(linearNits.Y, 0f) / 10000f,
            Hlsl.Max(linearNits.Z, 0f) / 10000f);

        Float3 y = new(
            Hlsl.Pow(n.X, 0.1593017578125f),
            Hlsl.Pow(n.Y, 0.1593017578125f),
            Hlsl.Pow(n.Z, 0.1593017578125f));

        Float3 num = new(
            0.8359375f + 18.8515625f * y.X,
            0.8359375f + 18.8515625f * y.Y,
            0.8359375f + 18.8515625f * y.Z);

        Float3 den = new(
            1f + 18.6875f * y.X,
            1f + 18.6875f * y.Y,
            1f + 18.6875f * y.Z);

        return new Float3(
            Hlsl.Pow(num.X / den.X, 78.84375f),
            Hlsl.Pow(num.Y / den.Y, 78.84375f),
            Hlsl.Pow(num.Z / den.Z, 78.84375f));
    }
}
