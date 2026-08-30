# ComputeSharpDemo

> 基于 WinUI 3 + ComputeSharp 的 GPU 计算着色器实时渲染示例。
>
> 仓库地址：<https://github.com/Johnwikix/ComputeSharpDemo>

一个轻量的 Windows 桌面演示工程，把 [ComputeSharp](https://github.com/Sergio0694/ComputeSharp) 的 HLSL 源生成能力塞进一个 WinUI 3 窗口里，所有画面都由 D3D12 计算着色器产出。支持 HDR/SDR 切换、DPI 适配、多显示器感知，并提供了一个插件式的 Shader 目录，便于扩展。

## 核心特性

- **GPU 计算着色器实时渲染**：ComputeSharp 3.2.0 + D3D12，HLSL 以源生成方式集成到 C# `IComputeShader<T>` 中
- **HDR / SDR 切换**：融合 WinRT `DisplayInformation` 与 DXGI 输出能力查询；窗口跨显示器移动或 Windows HDR 开关变化时自动重检测
- **DPI 自适应**：规避 WinUI `#8219`（`SwapChainPanel` 把 buffer 像素当 DIP 渲染），采用"宿主按物理像素缩放 + Panel 反向缩放"的双套尺寸方案
- **插件式 Shader 目录**：通过 `ShaderCatalog` 元数据 + `ShaderFactory` 懒加载缓存，新增 shader 只需改 3 个文件，主窗口零改动
- **Mica 背景 + 自定义标题栏**：使用 WinUIEx 扩展
- **三架构 RID**：`win-x86` / `win-x64` / `win-arm64`

## 预览

> 截图待补充。建议放入：
>
> - 窗口默认效果（含 Mica 背景）
> - Ray Trace 开启 HDR
> - Protean Clouds 动画
> - Ray Trace + RELAX 降噪器对比
>
> 图片存放目录：[`docs/screenshots/`](docs/screenshots/)。占位说明见该目录下的 `PLACEHOLDER.md`。

## 技术栈

| 组件 | 版本 / 说明 |
| --- | --- |
| 桌面框架 | WinUI 3 / Windows App SDK 2.3.1 |
| 运行时 | .NET 10（`net10.0-windows10.0.22621.0`） |
| GPU 计算 | ComputeSharp 3.2.0（HLSL 源生成） |
| DirectX 绑定 | Vortice.Direct3D12 / Vortice.D3DCompiler / Vortice.DXGI 3.6.2 |
| 窗口扩展 | WinUIEx 2.9.2 |
| 目标平台 | `win-x86` / `win-x64` / `win-arm64` |
| 发布策略 | Release：`PublishReadyToRun=true` + `PublishTrimmed=true` |
| 打包 | MSIX（`ComputeSharpDemo (Package).wapproj`） |

## 运行环境与前提条件

- **操作系统**：Windows 10 1809（10.0.17763）及以上
- **.NET SDK**：.NET 10 SDK
- **显卡**：DirectX 12 兼容显卡（WARP 软件渲染器**不**支持本工程使用的 D3D12 计算路径）
- **HDR（可选）**：HDR 显示器，用于验证 HDR 路径；Windows 系统的"使用 HDR"开关需提前开启
- **MSIX 部署（可选）**：Windows 10 1809+；需有效代码签名证书

> 实操命令（build / run / publish）请参考 Visual Studio F5、`dotnet build` / `dotnet publish` 或仓库内部 wiki；本 README 不在此处罗列命令。

## 项目结构

```
ComputeSharpDemo/
├─ ComputeSharpDemo/                  主应用项目
│  ├─ App.xaml(.cs)                   程序入口
│  ├─ MainWindow.xaml(.cs)            主窗口 + 着色器面板容器 + 工具栏
│  ├─ Shaders/
│  │  ├─ ProteanClouds/               nimitz 体积云 shader（自带 pass）
│  │  ├─ RayTrace/                    蒙特卡洛路径追踪 + 降噪
│  │  ├─ AppleMusic/                  Apple Music 风格背景（纯计算着色器移植）
│  │  ├─ ShaderCatalog.cs             元数据列表 + 懒加载工厂
│  │  ├─ ShaderFactory.cs             IShaderPass 实例缓存
│  │  ├─ IShaderPass.cs               Pass 抽象（Initialize / OnResize / SetMouse ...）
│  │  ├─ IShaderPassWithParameters.cs 带运行时参数的 Pass 扩展
│  │  └─ ShaderCapabilities.cs        能力位（UsesTime / UsesMouse / UsesResolution）
│  ├─ Hdr/                            HDR 检测 + 交换链渲染
│  │  ├─ HdrDisplayInfoTracker.cs     DisplayInformation 包装
│  │  ├─ HdrShaderPanel.cs            宿主 SwapChainPanel 控件
│  │  ├─ HdrSwapChainRenderer.cs      D3D12 渲染线程
│  │  ├─ HdrFullScreenPass.cs         HDR 全屏 shader
│  │  └─ SwapChainPanelNativeInterop.cs  WinRT / DXGI 互操作
│  └─ ADD_NEW_SHADER.md               新增 shader 的完整流程
├─ ComputeSharpDemo (Package)/        MSIX 打包项目
│  ├─ Package.appxmanifest            应用清单（声明 runFullTrust / systemAIModels）
│  └─ Images/                         图标资源
├─ docs/
│  ├─ hdr-pitfalls.md                 HDR 实现踩坑笔记
│  └─ screenshots/                    截图占位（待补充）
├─ ComputeSharpDemo.slnx              Solution
├─ LICENSE                            MIT 协议全文
└─ README.md                          本文件
```

## 内置 Shader

### Protean Clouds

- **类型**：Ray-march 体积云
- **作者**：[nimitz](https://twitter.com/stormoid)
- **原始来源**：[ShaderToy #3l23Rh](https://www.shadertoy.com/view/3l23Rh)
- **协议**：CC BY-NC-SA 3.0
- **能力**：`UsesTime | UsesMouse | UsesResolution`

### Ray Trace

- **类型**：蒙特卡洛路径追踪（球体场景）
- **作者**：本仓库内置
- **可调参数**：
  - `MaxBounces`：光线反弹次数（1–32）
  - `Samples`：每像素采样数（1–16）
  - **降噪器**：
    - `无`
    - `时域均值累加`
    - `RELAX`
- **能力**：`UsesTime | UsesMouse | UsesResolution`

### Apple Music Inspired

- **类型**：旋转专辑图层 + pinch 液态网格变形背景（Apple Music iOS 16.3 风格）
- **来源**：移植自 [Lyricify-Backgrounds](https://github.com/WXRIW/Lyricify-Backgrounds) 的 D3D11 顶点+像素着色器实现（Apache-2.0）
- **协议**：Apache-2.0
- **能力**：`UsesTime | UsesResolution`
- **实现要点**：原始实现依赖顶点着色器光栅化（3 个旋转实例四边形 + Catmull-Clark 细分的 pinch 网格）。ComputeSharp 只有计算着色器，因此整个移植是"逐像素反解"：
  - 旋转图层：每像素逆向执行原 `RotationVertex` 的仿射变换链（变换可逆，覆盖判定为 [-1,1] 方块测试），按原绘制顺序自顶向下求值；
  - pinch 网格：每像素用牛顿迭代反解 `warp(uv) = pixel`（对网格做双线性插值），不收敛的折叠像素回退到与原始光栅化器等价的三角形穷举扫描（保持索引缓冲的绘制顺序语义）；
  - 高斯模糊：77 对双线性采样的可分离核，手动实现零边框采样并按 alpha 覆盖率归一化（替代 `LinearZeroBorderSampler`）；
  - 网格数据存放在两个只读结构化缓冲中，在着色器内按时间混合，无需每帧上传。
- **默认图片**：`C:\Users\90684\Pictures\3ce2647bd143f6d49cf58a483e6c9aa8.png`（见 `AppleMusicPass.DefaultArtworkPath`，经 WIC 解码、最长边 ≤1024 后上传）
- **省略项**：频谱驱动的缩放脉冲与专辑切换交叉淡入淡出（原实现依赖音频捕获，Demo 不采集音频）

## HDR 支持

本工程同时走两条 HDR 探测路径，并合并成一份"有效"能力快照：

1. **WinRT 路径**：`HdrDisplayInfoTracker` 包装 `DisplayInformation`，跟随 Windows HDR 开关 / 显示器切换事件
2. **DXGI 路径**：`HdrShaderPanel` 通过 DXGI 查询当前输出是否支持 HDR，以及峰值亮度

首次检测到 HDR 可用时，HDR 自动开启一次；之后由工具栏右侧的 ToggleSwitch 完全由用户控制。窗口移动到另一台显示器时，会重新评估输出能力（多显示器混合 HDR/SDR 场景）。

更详细的实现难点与踩坑记录见 [`docs/hdr-pitfalls.md`](docs/hdr-pitfalls.md)。

## 新增 Shader

新增一个 shader 通常需要：

1. 编写 `Shaders/Xxx/XxxShader.cs` —— 实现 `IComputeShader<Float4>`，并用 `[ThreadGroupSize]` / `[GeneratedComputeShaderDescriptor]` 标注
2. 编写 `Shaders/Xxx/XxxPass.cs` —— 继承 `IShaderPass`，负责状态管理与 dispatch
3. 在 `Shaders/ShaderCatalog.cs` 的 `All` 列表里追加一条 `ShaderAuthoringInfo`

主窗口（`MainWindow.xaml.cs`）**无需任何修改**。完整步骤见 [`ComputeSharpDemo/ADD_NEW_SHADER.md`](ComputeSharpDemo/ADD_NEW_SHADER.md)。

## MSIX 部署小贴士

- `Package.appxmanifest` 中声明了 `runFullTrust`（桌面全信任）与 `systemAIModels`（系统 AI 模型）受限能力
- 最低运行时版本：Windows 10 1809（10.0.17763）
- 已测最高版本：Windows 11 26226
- 开发部署需要有效的代码签名证书；侧载或 Store 分发按需配置

## 已知问题与备注

- **WinUI `#8219`**：`SwapChainPanel` 把 swapchain buffer 像素当作 DIP 渲染，导致高 DPI 屏幕上图像被裁切。本工程通过把宿主 `Canvas` 设为物理像素尺寸，再把 panel 反向缩放回 DIP 槽位解决，详见 `MainWindow.xaml.cs` 的注释与 `TrySyncPanelAndBuffer()`
- **WARP 软件渲染器**：不支持本工程使用的 D3D12 计算着色器路径，需要真 GPU
- **多显示器 + DPI**：仅靠 `SizeChanged` 无法覆盖 DPI-only 变更；本工程额外监听 `XamlRoot.Changed` 与 `AppWindow.Changed` 事件，并在 500 ms 节流的 `DispatcherTimer` 上做兜底重检测

## 许可与致谢

- **本项目代码**：[MIT](LICENSE) © 2026 Johnwikix
- **Protean Clouds Shader**：CC BY-NC-SA 3.0 © nimitz
- **Ray Trace Shader**：本仓库内置
- **Apple Music Inspired Shader**：移植自 [Lyricify-Backgrounds](https://github.com/WXRIW/Lyricify-Backgrounds)（Apache-2.0 © XY Wang），效果逆向自 Apple Music（iOS 16.3）
- 依赖致谢：[ComputeSharp](https://github.com/Sergio0694/ComputeSharp)、[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)、[Windows App SDK](https://github.com/microsoft/WindowsAppSDK)、[WinUIEx](https://github.com/dotMorten/WinUIEx)