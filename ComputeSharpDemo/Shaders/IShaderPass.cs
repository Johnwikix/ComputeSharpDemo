using ComputeSharp;

namespace ComputeSharpDemo.Shaders;

/// <summary>
/// Contract every selectable shader implements. One instance per active shader —
/// see <see cref="ShaderFactory"/> for lifetime management.
///
/// Implementations are also <see cref="ComputeSharp.WinUI.IShaderRunner"/>s,
/// which is the interface <see cref="ComputeSharp.WinUI.AnimatedComputeShaderPanel"/>
/// uses to dispatch frames to its GPU texture each tick.
/// </summary>
public interface IShaderPass
    : IDisposable,
      ComputeSharp.WinUI.IShaderRunner
{
    /// <summary>Stable identifier (used as a key in the catalog &amp; settings).</summary>
    string Id { get; }

    /// <summary>Display name shown in the dropdown.</summary>
    string DisplayName { get; }

    /// <summary>One-line description / subtitle.</summary>
    string Description { get; }

    /// <summary>Original author (kept for attribution).</summary>
    ShaderAuthor Author { get; }

    /// <summary>Optional URL of the source shader (e.g. shadertoy link).</summary>
    string? OriginalUrl { get; }

    /// <summary>Capabilities that drive UI behaviour.</summary>
    ShaderCapabilities Capabilities { get; }

    /// <summary>
    /// Called once after the D3D12 device is ready, before the first dispatch.
    /// </summary>
    void Initialize(GraphicsDevice device, Int2 initialSize);

    /// <summary>
    /// Called whenever the swap chain is resized.
    /// </summary>
    void OnResize(Int2 newSize);
}