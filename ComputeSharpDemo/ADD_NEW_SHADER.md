# 添加新 Shader 指南

## 项目结构概览

为方便未来扩展，项目采用三层架构：

| 层 | 组件 | 作用 |
|---|---|---|
| **Shader 核** | `XxxShader.cs` | HLSL 计算着色器（`IComputeShader<Float4>`） |
| **Pass 包装** | `XxxPass.cs` | 状态管理 + 每帧调度（`IShaderPass`） |
| **注册** | `ShaderCatalog.cs` | 一条元数据记录，使 shader 出现在下拉框 |

添加一个新 shader 只需**改/建 3 个文件**（其中两个是新建的），其余框架代码完全不动。

---

## 步骤

### 1. 创建着色器文件 `Shaders/Xxx/XxxShader.cs`

继承 `IComputeShader<Float4>`，**必须**添加两个 Attribute：

```csharp
using ComputeSharp;

namespace ComputeSharpDemo.Shaders.Xxx;

/// <summary>
/// 你的 shader 说明。
/// 原始来源：https://www.shadertoy.com/view/xxxxxx
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct XxxShader(
    float iTime,
    Float2 iMouse,
    Float2 iResolution) : IComputeShader<Float4>
{
    // 静态常量 / 工具函数
    private static readonly Float3x3 M3 = new(
        0.33338f * 1.93f, -0.87887f * 1.93f, 0.15162f * 1.93f,
        0.56034f * 1.93f, 0.32651f * 1.93f, 0.69596f * 1.93f,
        -0.71817f * 1.93f, -0.15323f * 1.93f, 0.61339f * 1.93f);

    /// <summary>
    /// 每个像素调用一次。返回该像素的颜色。
    /// </summary>
    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;

        // Y 翻转：Shadertoy 左下角为原点，DirectX 左上角
        Float2 fragCoord = new(xy.X + 0.5f, iResolution.Y - (xy.Y + 0.5f));

        // ---- 你的着色逻辑 ----

        Float3 col = new(1, 0, 1); // 品红色占位

        return new Float4(Hlsl.Saturate(col), 1.0f);
    }

    // ---- 辅助函数 ----
    private static Float2 Scale(Float2 v, float s) => new(v.X * s, v.Y * s);
    private static Float3 Scale(Float3 v, float s) => new(v.X * s, v.Y * s, v.Z * s);
    private static Float4 Scale(Float4 v, float s) => new(v.X * s, v.Y * s, v.Z * s, v.W * s);
}
```

#### 规则速查

> **GLSL `mat2(c, s, -s, c)`** → HLSL row-major `Float2x2(c, -s, s, c)`

> **GLSL `vec2 * float`** → `Scale(v, s)`（ComputeSharp 的 C# 包装不暴露 `Float2 * float` 运算符）

> **GLSL `gl_FragCoord`** → `fragCoord = new(xy.X + 0.5, iResolution.Y - (xy.Y + 0.5))`（Y 翻转）

> **GLSL `iResolution.xy`** → `iResolution`

> **GLSL `vec3(x,y,z)`** → `new Float3(x, y, z)`

> **GLSL `mat3 * vec3`** → `Hlsl.Mul(M3, p)`

> **GLSL swizzle `.zxy`** → `.ZXY`

> **HLSL 数学函数** `sin, cos, abs, normalize, cross, dot, lerp, clamp, saturate, pow, exp, smoothstep` → `Hlsl.Sin(), Hlsl.Cos(), …`

> **不支持 `Float3 * float`** — 用 `Scale(float3, float)`

### 2. 创建 Pass 文件 `Shaders/Xxx/XxxPass.cs`

```csharp
using ComputeSharp;
using ComputeSharp.WinUI;

namespace ComputeSharpDemo.Shaders.Xxx;

/// <summary>
/// <see cref="IShaderPass"/> 包装。
/// </summary>
public sealed class XxxPass : IShaderPass
{
    private GraphicsDevice? _device;
    private Float2 _mouse;
    private bool _disposed;

    // ── 身份 ──────────────────────────────────
    public string Id => "xxx";
    public string DisplayName => "你的 Shader";
    public string Description => "一句话描述。";
    public ShaderAuthor Author { get; } = new(
        Name: "作者名",
        Url: "https://twitter.com/xxx",
        License: "CC BY-NC-SA 3.0");
    public string? OriginalUrl => "https://www.shadertoy.com/view/xxxxxx";

    // ── 能力 ──────────────────────────────────
    public ShaderCapabilities Capabilities =>
        ShaderCapabilities.UsesTime     // shader 需要 iTime
      | ShaderCapabilities.UsesMouse    // shader 需要 iMouse（鼠标跟踪）
      | ShaderCapabilities.UsesResolution; // shader 需要 iResolution

    // ── 鼠标 ──────────────────────────────────
    public void SetMouse(float x, float y, float panelWidth, float panelHeight)
        => _mouse = new Float2(x, panelHeight - y);

    // ── 生命周期 ──────────────────────────────
    public void Initialize(GraphicsDevice device, Int2 initialSize)
        => _device = device;

    public void OnResize(Int2 newSize) { /* 如果不需要维护中间纹理，留空 */ }

    public bool TryExecute(
        IReadWriteNormalizedTexture2D<Float4> texture,
        TimeSpan timespan,
        object? parameter)
    {
        if (_device is null)
            throw new InvalidOperationException(
                $"{nameof(XxxPass)}.{nameof(Initialize)} must be called first.");

        float time = (float)timespan.TotalSeconds;
        Float2 resolution = new(texture.Width, texture.Height);

        _device.ForEach(
            texture,
            new XxxShader(time, _mouse, resolution));

        return true; // true = 显示这一帧
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 如果有 native 资源，在此释放
    }
}
```

#### 能力位选择

| 标志 | 功能 | 主窗口响应 |
|---|---|---|
| `UsesTime` | 需要 iTime | 自动传入 |
| `UsesMouse` | 需要 iMouse | 开启 PointerMoved 跟踪 |
| `UsesResolution` | 需要 iResolution | 自动传入 |
| `SupportsPause` | 可暂停 | 预留（当前未接 UI） |
| `SupportsCustomParameters` | 额外滑块/开关 | 预留（见 `IShaderPassWithParameters`） |

### 3. 注册到 `Shaders/ShaderCatalog.cs`

在 `All` 数组末尾加一条：

```csharp
public static IReadOnlyList<ShaderAuthoringInfo> All { get; } = new[]
{
    // … 已有的 shader …
    new ShaderAuthoringInfo(
        Id:           "xxx",
        DisplayName:  "你的 Shader",
        Description:  "一句话描述。",
        Author:       new ShaderAuthor(
                          Name:    "作者名",
                          Url:     "https://twitter.com/xxx",
                          License: "CC BY-NC-SA 3.0"),
        Capabilities: ShaderCapabilities.UsesTime
                    | ShaderCapabilities.UsesMouse
                    | ShaderCapabilities.UsesResolution,
        OriginalUrl:  "https://www.shadertoy.com/view/xxxxxx",
        Factory:      static () => new XxxPass()),
};
```

完成。重新构建后，shader 会自动出现在下拉框中。

---

## 从 Shadertoy 移植速查表

假源 Shadertoy 代码：

```glsl
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // fragCoord 是像素坐标，原点在左下角
    // iTime 是秒
    // iMouse 是像素坐标
    // iResolution 是视口尺寸
}
```

对应 ComputeSharp 入口：

```csharp
public Float4 Execute()
{
    // ThreadIds.XY 是像素坐标（左上角原点）
    // 用 Y 翻转：new(xy.X + 0.5, iResolution.Y - (xy.Y + 0.5))
    // iTime, iMouse, iResolution 已通过主构造器传入
}
```

### 常用 GLSL → HLSL 转换

| GLSL | ComputeSharp (HLSL) |
|---|---|
| `vec2(x, y)` | `new Float2(x, y)` |
| `vec3(x, y, z)` | `new Float3(x, y, z)` |
| `vec4(x, y, z, w)` | `new Float4(x, y, z, w)` |
| `mat2(c, s, -s, c)` | `new Float2x2(c, -s, s, c)` |
| `v *= mat` | `v = Hlsl.Mul(v, mat)` |
| `v.x, v.y, v.z` | `v.X, v.Y, v.Z` |
| `v.xy, v.zyx` | `v.XY, v.ZYX` |
| `mix(a, b, t)` | `Hlsl.Lerp(a, b, t)` |
| `clamp(v, lo, hi)` | `Hlsl.Clamp(v, lo, hi)` |
| `saturate(v)` | `Hlsl.Saturate(v)` |
| `smoothstep(e0, e1, v)` | `Hlsl.SmoothStep(e0, e1, v)` |
| `dot(a, b)` | `Hlsl.Dot(a, b)` |
| `cross(a, b)` | `Hlsl.Cross(a, b)` |
| `normalize(v)` | `Hlsl.Normalize(v)` |
| `length(v)` | `Hlsl.Length(v)` |
| `pow(v, e)` | `Hlsl.Pow(v, e)` |
| `exp(v)` | `Hlsl.Exp(v)` |
| `sin(v)` | `Hlsl.Sin(v)` |
| `cos(v)` | `Hlsl.Cos(v)` |
| `abs(v)` | `Hlsl.Abs(v)` |
| `min(v1, v2)` | `Hlsl.Min(v1, v2)` |
| `max(v1, v2)` | `Hlsl.Max(v1, v2)` |

---

## 额外参数（进阶）

如果 shader 需要用户调节的滑块/开关（如色调、强度、颜色），可实现 `IShaderPassWithParameters` 接口。框架已预留接口定义和 UI 绑定点，当前未渲染 UI。（实现后下拉框下方会自动显示参数面板。）

```csharp
public sealed class XxxPass : IShaderPassWithParameters
{
    public IReadOnlyList<ShaderParameter> Parameters { get; } = new ShaderParameter[]
    {
        new ShaderParameter.Slider("intensity", "强度", 0f, 5f, 1f, 0.1f),
        new ShaderParameter.Toggle("invert", "反色", false),
    };

    public object? GetParameterValue(string id) => ...;
    public bool TrySetParameterValue(string id, object value) { ...; return true; }
}
```

---

## QA

**Q: 找不到 `IComputeShader<Float4>`？**  
A: 确保 `csproj` 里有 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`。

**Q: `Float2 * float` 编译报错？**  
A: ComputeSharp 的 C# 包装不暴露标量-向量乘法运算符。用 `Scale(v, s)` 代替。

**Q: 如何确认 source generator 跑起来了？**  
A: 检查 `obj/Generated` 目录，应有 `ComputeSharp.SourceGenerators` 生成的 `.cs` 文件。如果为空，确认 `.csproj` 中 `AllowUnsafeBlocks` 为 `true`。
