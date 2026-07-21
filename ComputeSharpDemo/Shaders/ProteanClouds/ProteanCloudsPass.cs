using ComputeSharp;
using ComputeSharp.WinUI;

namespace ComputeSharpDemo.Shaders.ProteanClouds;

/// <summary>
/// <see cref="IShaderPass"/> wrapper for the Protean Clouds compute shader.
/// </summary>
public sealed class ProteanCloudsPass : IShaderPass
{
    private GraphicsDevice? _device;
    private Float2 _mouse;
    private bool _disposed;

    public string Id => "protean-clouds";
    public string DisplayName => "Protean Clouds";
    public string Description => "Raymarched volumetric clouds, by nimitz.";
    public ShaderAuthor Author { get; } = new(
        Name: "nimitz",
        Url: "https://twitter.com/stormoid",
        License: "CC BY-NC-SA 3.0");
    public string? OriginalUrl => "https://www.shadertoy.com/view/3l23Rh";

    public ShaderCapabilities Capabilities =>
        ShaderCapabilities.UsesTime
      | ShaderCapabilities.UsesMouse
      | ShaderCapabilities.UsesResolution;

    public void SetMouse(float x, float y, float panelWidth, float panelHeight)
        => _mouse = new Float2(x, panelHeight - y);

    public void Initialize(GraphicsDevice device, Int2 initialSize)
        => _device = device;

    public void OnResize(Int2 newSize) { }

    public bool TryExecute(
        IReadWriteNormalizedTexture2D<Float4> texture,
        TimeSpan timespan,
        object? parameter)
    {
        if (_device is null)
            throw new InvalidOperationException("Initialize must be called before dispatch.");

        float time = (float)timespan.TotalSeconds;
        Float2 resolution = new(texture.Width, texture.Height);

        _device.ForEach(
            texture,
            new ProteanCloudsShader(time, _mouse, resolution));

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}