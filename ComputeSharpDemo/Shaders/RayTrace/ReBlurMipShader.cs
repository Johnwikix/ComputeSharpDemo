using ComputeSharp;

namespace ComputeSharpDemo.Shaders.RayTrace;

/// <summary>
/// 2x2 box-filter downsampling pass for the ReBLUR hierarchical history: one mip level is
/// produced per dispatch, from the previous level (or, for level 1, from the full-resolution
/// denoised hit distance written by <see cref="ReBlurAccumulateShader"/>). Edge texels clamp
/// to the last valid sample. The resulting pyramid lets the hierarchical blur read accurate
/// hit distance at coarse scales (ReBLUR / GTC 2020).
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ReBlurDistMipShader(
    Float2 inputResolution,
    IReadWriteNormalizedTexture2D<float> inputMip) : IComputeShader<float>
{
    public float Execute()
    {
        Int2 xy = ThreadIds.XY;

        Int2 a = new(xy.X * 2, xy.Y * 2);
        Int2 b = new(Hlsl.Min(a.X + 1, (int)inputResolution.X - 1), Hlsl.Min(a.Y + 1, (int)inputResolution.Y - 1));

        return (inputMip[a]
            + inputMip[new Int2(b.X, a.Y)]
            + inputMip[new Int2(a.X, b.Y)]
            + inputMip[b]) * 0.25f;
    }
}

/// <summary>
/// Same 2x2 box-filter downsampling as <see cref="ReBlurDistMipShader"/>, for the display-
/// encoded accumulated color (RGB + W hit distance). Mip texels average encoded values, which
/// is consistent with the rest of the pipeline running in display-encoded space.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ReBlurColorMipShader(
    Float2 inputResolution,
    IReadWriteNormalizedTexture2D<Float4> inputMip) : IComputeShader<Float4>
{
    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;

        Int2 a = new(xy.X * 2, xy.Y * 2);
        Int2 b = new(Hlsl.Min(a.X + 1, (int)inputResolution.X - 1), Hlsl.Min(a.Y + 1, (int)inputResolution.Y - 1));

        return (inputMip[a]
            + inputMip[new Int2(b.X, a.Y)]
            + inputMip[new Int2(a.X, b.Y)]
            + inputMip[b]) * 0.25f;
    }
}