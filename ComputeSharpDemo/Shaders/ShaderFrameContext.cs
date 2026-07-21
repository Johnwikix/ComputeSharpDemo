using ComputeSharp;

namespace ComputeSharpDemo.Shaders;

/// <summary>
/// Common per-frame input bundle passed to every <see cref="IShaderPass"/>.
/// All fields are already pre-processed into the shader's coordinate convention
/// (mouse Y is flipped, mouse is resolution-relative, etc.) so shader code can
/// use them directly as iMouse / iResolution / iTime.
/// </summary>
public readonly record struct ShaderFrameContext(
    float Time,
    float DeltaTime,
    Float2 Mouse,
    Float2 Resolution);