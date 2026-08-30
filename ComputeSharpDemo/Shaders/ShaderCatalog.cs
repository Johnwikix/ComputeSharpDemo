using ComputeSharpDemo.Shaders.AppleMusic;
using ComputeSharpDemo.Shaders.ProteanClouds;
using ComputeSharpDemo.Shaders.RayTrace;

namespace ComputeSharpDemo.Shaders;

/// <summary>
/// Static catalog of every shader the demo knows about. Each entry is metadata
/// only — the actual <see cref="IShaderPass"/> instance is created lazily by
/// <see cref="ShaderFactory"/> only when the shader is selected, so we don't
/// pay the Initialize() cost for shaders the user never tries.
/// </summary>
public static class ShaderCatalog
{
    public static IReadOnlyList<ShaderAuthoringInfo> All { get; } = new[]
    {
        new ShaderAuthoringInfo(
            Id:           "protean-clouds",
            DisplayName:  "Protean Clouds",
            Description:  "Raymarched volumetric clouds.",
            Author:       new ShaderAuthor(
                              Name:    "nimitz",
                              Url:     "https://twitter.com/stormoid",
                              License: "CC BY-NC-SA 3.0"),
            Capabilities: ShaderCapabilities.UsesTime
                        | ShaderCapabilities.UsesMouse
                        | ShaderCapabilities.UsesResolution,
            OriginalUrl:  "https://www.shadertoy.com/view/3l23Rh",
            Factory:      static () => new ProteanCloudsPass()),
        new ShaderAuthoringInfo(
            Id:           "ray-trace",
            DisplayName:  "Ray Trace",
            Description:  "Monte Carlo path tracer with spheres.",
            Author:       new ShaderAuthor(
                              Name:    "RT Demo",
                              Url:     null,
                              License: "CC BY-NC-SA 3.0"),
            Capabilities: ShaderCapabilities.UsesTime
                        | ShaderCapabilities.UsesMouse
                        | ShaderCapabilities.UsesResolution,
            OriginalUrl:  null,
            Factory:      static () => new RayTracePass()),
        new ShaderAuthoringInfo(
            Id:           "apple-music-inspired",
            DisplayName:  "Apple Music Inspired",
            Description:  "Rotating artwork + pinch mesh background (pure compute port of Lyricify-Backgrounds).",
            Author:       new ShaderAuthor(
                              Name:    "Lyricify-Backgrounds",
                              Url:     null,
                              License: "Apache-2.0"),
            Capabilities: ShaderCapabilities.UsesTime
                        | ShaderCapabilities.UsesResolution,
            OriginalUrl:  null,
            Factory:      static () => new AppleMusicPass()),
        // To add a new shader: append one entry here.
        //   - Write a `XxxPass : IShaderPass` class.
        //   - Add an entry above with `Factory: static () => new XxxPass()`.
        // That's the whole integration cost — no MainWindow changes needed.
    };
}

/// <summary>
/// Lightweight description of a shader. Used by the dropdown and as a key
/// for the lazy factory cache.
/// </summary>
public sealed record ShaderAuthoringInfo(
    string Id,
    string DisplayName,
    string Description,
    ShaderAuthor Author,
    ShaderCapabilities Capabilities,
    string? OriginalUrl,
    Func<IShaderPass> Factory);