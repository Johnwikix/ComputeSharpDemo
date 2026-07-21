namespace ComputeSharpDemo.Shaders;

/// <summary>
/// Optional extension interface a shader can implement to expose
/// user-tweakable parameters (sliders, toggles, color pickers).
/// The host UI will automatically render a control panel when present.
/// </summary>
public interface IShaderPassWithParameters : IShaderPass
{
    IReadOnlyList<ShaderParameter> Parameters { get; }

    object? GetParameterValue(string id);

    bool TrySetParameterValue(string id, object value);
}

/// <summary>
/// Describes a single user-controllable shader parameter.
/// Implementations:
///   <see cref="Slider"/>     — continuous float in [Min, Max]
///   <see cref="Toggle"/>     — on / off boolean
///   <see cref="ColorPicker"/> — RGBA float4
/// </summary>
public abstract record ShaderParameter(string Id, string DisplayName)
{
    public sealed record Slider(
        string Id,
        string DisplayName,
        float Min,
        float Max,
        float Default,
        float Step) : ShaderParameter(Id, DisplayName);

    public sealed record Toggle(
        string Id,
        string DisplayName,
        bool Default) : ShaderParameter(Id, DisplayName);

    public sealed record ColorPicker(
        string Id,
        string DisplayName,
        float DefaultR,
        float DefaultG,
        float DefaultB) : ShaderParameter(Id, DisplayName);
}