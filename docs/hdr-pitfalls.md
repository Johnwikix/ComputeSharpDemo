# ComputeSharpDemo HDR 开发踩坑记录

> 给后来的人 / Agent 的实战笔记。记录了在 WinUI 3 + ComputeSharp + Vortice.Direct3D12
> 自定义 HDR swapchain 开发中遇到的全部坑：现象、根因、修复、验证方法。
>
> 环境：Windows 11 26200（Insider/26H2 系）、RTX 5070 Ti（驱动 32.0.16.1088）、
> 双显示器（主屏 HDR 2560×1440 + MuMu 虚拟 SDR 屏）、WinUI 3 / WindowsAppSDK 2.3.1、
> ComputeSharp 3.2.0、Vortice 3.6.2、.NET 10、150% DPI。
>
> 最终架构见文末"正确姿势"章节——遇到同类需求直接照抄。

---

## 目录

1. [DXGI / D3D12 层](#1-dxgi--d3d12-层)
2. [ComputeSharp 层](#2-computesharp-层)
3. [Vortice 层](#3-vortice-层)
4. [WinUI 3 / 显示器层](#4-winui-3--显示器层)
5. [多显示器 / HDR 检测层](#5-多显示器--hdr-检测层)
6. [渲染线程 / 生命周期层](#6-渲染线程--生命周期层)
7. [测试方法论（最容易骗过你的部分）](#7-测试方法论最容易骗过你的部分)
8. [工具链 / 工程流程](#8-工具链--工程流程)
9. [正确姿势（最终可用架构）](#9-正确姿势最终可用架构)

---

## 1. DXGI / D3D12 层

### 1.1 `SetColorSpace1` 在全新 composition swapchain 上抛 `E_INVALIDARG`

- **现象**：创建 swapchain 后立即调用 `SetColorSpace1(G22_P709)` → `0x80070057`。
- **根因**：全新（未初始化/未 present）的 composition swapchain 拒绝色彩空间操作；
  `CheckColorSpaceSupport` 在这个阶段也返回 `None`（空标志）。
- **修复**：色彩空间**延后到首次 present 之后**应用；不在构造期调用。
- **教训**：`CheckColorSpaceSupport` 返回 `None` 不能当作"不支持"，只能当作"现在问不出来"。
  不要用预检门禁决定是否调用 `SetColorSpace1`，直接 try/catch + 失败回退即可。

### 1.2 `ResizeBuffers` 返回 S_OK 但交换链尺寸纹丝不动（静默推迟）

- **现象**：`ResizeBuffers(2, w, h, ...)` 返回 S_OK，`IDXGISwapChain1.Description1` 仍是旧尺寸；
  渲染内容永远停在首尺寸——用户视角"最大输出尺寸被锁定"。
- **根因**：**应用持有后缓冲的 COM 包装器引用（`GetBuffer` 出来的对象）时，flip-model
  swapchain 会把 ResizeBuffers 静默推迟**。之前 `GetBuffer` 的包装器在 `ResizeBuffers`
  **之后**才释放，引用一直挂着。
- **修复**：**先释放全部后缓冲包装器，再调用 ResizeBuffers**（这是 ComputeSharp.WinUI
  包内的正确顺序）。
- **验证**：resize 后读 `Description1` 确认尺寸真的变了，而不是盲目信任 HRESULT。

### 1.3 连续两次 `ResizeBuffers` 之间必须有 Present

- **现象**：拖动窗口（高频 resize 事件）时偶发 `E_INVALIDARG`，进而渲染线程崩溃。
- **根因**：flip-model 要求相邻两次 `ResizeBuffers` 之间至少一次 `Present`；
  真实拖动的事件节奏（每秒 10-20 次）会让渲染线程连续两次 resize 而无 present。
- **修复**：
  1. resize **去抖**：尺寸稳定 ~30ms 后才应用（拖动期间持续渲染旧尺寸）；
  2. **硬性保证**：`_presentedSinceResize` 标志——resize 应用后必须先完成一次 present
     才允许下一次 resize。
- **教训**：自动测试用 `SetWindowPos` + 3-5 秒间隔永远复现不了（中间必有 present），
  必须用高频事件模拟真实拖动（见 §7）。

### 1.4 面板 attached 的 swapchain 上 `ResizeBuffers` 会永久冻结显示

- **现象**：修正 1.2 后 resize 生效（`Description1` 正确），但**面板画面变成静止帧**
  （渲染循环在 90fps 跑、present 正常，屏幕上却不动）。
- **根因**：这台机器（Win11 26200 + 该 GPU）上，对挂在 SwapChainPanel 上的 swapchain
  调用 `ResizeBuffers` 会破坏面板显示管线（呈现 1.2 节正确顺序后依然如此）。
- **修复**：**放弃 ResizeBuffers，resize 时整体重建 swapchain**（新链创建 → UI 线程
  `SetSwapChain` 换绑 → 旧链延迟释放）。
- **验证**：必须用"两帧画面是否变化"来验证（见 §7 PrintWindow），只查 desc 不够。

### 1.5 立即释放被面板引用过的旧 swapchain 会崩溃（`0xC0000005`）

- **现象**：重建 swapchain 后（第 2~4 次重建）进程随机崩溃，coreclr 访问违例。
- **根因**：合成线程可能仍引用刚换绑下来的旧链，立即 `Dispose()` 触发 use-after-dispose。
- **修复**：**会话期间不释放旧链**，把所有退休链存进列表，应用关闭（Dispose）时统一释放。
- **教训**：DXGI composition swapchain 与面板的生命周期耦合比想象的深；
  换绑后旧链多活一阵子没有任何坏处。

### 1.6 无超时的 UI 线程换绑握手

- **现象**：重建 swapchain 的 `done.Wait(2s)` 超时后，渲染线程继续用旧链，
  而 UI 线程稍后释放旧链 → 崩溃。
- **修复**：渲染线程在换绑期间**必须阻塞到换绑完成**，握手等待**不能有超时**
  （超时 = 放任 use-after-dispose）。UI 线程关窗时会拒绝入队，返回 false 走重试即可。

### 1.7 `R16G16B16A16_FLOAT` 交换链在此系统不支持任何色彩空间

- **现象**：16F swapchain 上 `CheckColorSpaceSupport` 全部返回 `None`、
  `SetColorSpace1`（无论 G22 还是 G2084）一律 `E_INVALIDARG`——D3D11/D3D12、
  HWND/Composition 全试过，裸设备也一样。
- **根因**：系统/驱动层对该格式的色彩空间支持损坏（疑似该 Win11 版本 + 新驱动组合）。
- **修复**：改用 **`R10G10B10A2_UNORM`**（标准 HDR10 格式，10-bit PQ），全部正常。
- **教训**：文档说的"16F 是 HDR 标准格式"在特定系统上可能不成立；`R10G10B10A2`
  是兼容性最好的 HDR 交换链格式。

### 1.8 创建 command list 后必须先 Close

- **现象**：首次 `commandAllocator.Reset()` 报 `E_FAIL`。
- **根因**：D3D12 command list 创建时处于 recording 状态，allocator 无法复用。
- **修复**：创建后立即 `Close()`（包也是这么做的）。

### 1.9 销毁 swapchain 会阻塞在合成器上（几秒的关闭卡顿）

- **现象**：点击关闭窗口后 UI 卡 1-2 秒才退出；窗口"停止响应"。
- **根因**：flip-model swapchain 的 `Dispose()` 会阻塞到**合成器退役其最后呈现的帧**
  （约 1 vsync/条）；每次 resize 退休一条链，关闭时逐条 Dispose → 多链累积 1-2 秒；
  当前链同理。此外设备释放（含 ComputeSharp `GraphicsDevice.Dispose()`）也会等 GPU 排空。
- **修复**：**销毁两阶段化**——UI 线程只做 `SetSwapChain(null)` 拆除（唯一需要
  UI 线程的部分），其余全部（Join 渲染线程、GPU 排空、逐条 Dispose 链、释放设备）
  丢到后台线程；窗口关闭瞬间返回，进程退出后驱动会回收残留资源。
- **教训**：凡是"关窗/退出"路径，一律假设 GPU/合成器可能慢，禁止阻塞 UI 线程。

---

## 2. ComputeSharp 层

### 2.1 ComputeSharp 的 `ID3D12Resource` IID 末字节是 `0FAD` 不是 `0F40`

- **现象**：`InteropServices.GetID3D12Resource(texture, IID_ID3D12Resource, ...)`
  返回 `E_NOINTERFACE`；用 `0FAD` 就成功。
- **根因**：ComputeSharp 的 Win32 互操作层自有一套 GUID（`...0F40` 是标准 d3d12.h 的，
  `...0FAD` 是 ComputeSharp 绑定里的）。它的纹理资源对象只认后者。
- **修复**：IID 用 `696442BE-A72E-4059-BC79-5B5C98040FAD`。
- **教训**：跨库 QI 时别背文档里的 IID，以实际绑定代码为准；失败时先怀疑 GUID。

### 2.2 `ForEach` 只支持 normalized 纹理；shader 无法写 raw float 纹理

- **现象**：想用 `ReadWriteTexture2D<Float4>` 做 HDR 线性帧缓冲，但：
  - 不存在 `IReadWriteBuffer<T>`/`IReadWriteTexture2D<Float4>` 这类 shader 接口；
  - `ReadWriteTexture2D<T, TPixel>` 要求 `T : IPixel<T, TPixel>`，Float4 不满足；
  - shader 可写纹理格式只有 `Bgra32/Rgba32/Rgba64`（8/16-bit UNORM）。
- **修复**：**编码下沉到 shader 内**——shader 直接把 PQ/sRGB 编码后的 [0,1] 值写入
  `ReadWriteTexture2D<Rgba64, Float4>`（16-bit UNORM 精度足够覆盖 10-bit PQ）；
  全屏 pass 只做格式搬运（UNORM16 → 后缓冲）。

### 2.3 `GraphicsDevice.ForEach` 是同步阻塞的（CPU 等 GPU 完成）

- **注意**：`device.ForEach(...)` 内部 signal + CPU 等待。跨队列（compute dispatch →
  渲染线程的 direct 队列 copy/present）无需额外 fence 等待——这是特性不是坑，
  但设计渲染循环时要知道它把 dispatch 时间算进了帧时间。

---

## 3. Vortice 层

### 3.1 Vortice 显式 SRV/UAV desc 会把设备搞死

- **现象**：`CreateShaderResourceView(resource, 显式desc, handle)`：
  - 无 debug layer 时 → 设备 `DXGI_ERROR_INVALID_CALL` 被移除（`0x887A0001`）；
  - 开 debug layer 时 → 原生访问违例。
- **根因**：Vortice 3.6.2 的 `ShaderResourceViewDescription`/`UnorderedAccessViewDescription`
  布局与原生不一致（union 字段问题），显式 desc 传进原生层即损坏。
- **修复**：**传 `null` desc**，让运行时从资源推断（Texture2D / 原生格式 / mip0），
  对全屏采样场景完全够用。
- **教训**：Vortice 这类生成的互操作层，个别 struct 可能有问题——能推断就推断。

### 3.2 默认 `BlendState`（default）等于"不写颜色" → 黑屏

- **现象**：全屏 pass 画了三角形，后缓冲全黑；改 PS 输出常量色也一样。
- **根因**：`BlendState = default` 时 `RenderTargetWriteMask = 0`（颜色写入被禁用）；
  D3D12 默认写掩码是 0xF，但 default struct 是 0。
- **修复**：`BlendState = BlendDescription.Opaque`（或显式写掩码 `ColorWriteEnable.All`）。
- **教训**：D3D12 的"default"≠零值 struct；顺手把 Rasterizer/DepthStencil 也显式设一遍。

### 3.3 `IDXGISwapChain3.GetDescription1()` 不存在

- **注意**：Vortice 里是**属性** `Description1`（不是方法 `GetDescription1()`）；
  `IDXGIOutput6` 同理是 `Description1` 属性。查 XML 文档别想当然。

---

## 4. WinUI 3 / 显示器层

### 4.1 WinUI bug #8219：SwapChainPanel 把缓冲像素当 DIP 显示

- **现象**：物理尺寸（DIP × 1.5）的后缓冲在 150% DPI 下要么溢出裁剪、要么大小错乱。
- **根因**：SwapChainPanel 呈现 swapchain 缓冲时按 1 buffer px = 1 DIP，
  而非 1 px = 1 物理 px。
- **修复（DXRDemo 验证过的姿势）**：
  1. 缓冲尺寸 = 布局 DIP × `XamlRoot.RasterizationScale`（物理像素）；
  2. **面板的 Width/Height 直接设成物理像素值**（让缓冲 1:1 显示、不裁剪）；
  3. 面板挂 `ScaleTransform(1/DpiScale)` 反缩放回布局槽；
  4. **不要用 `SetMatrixTransform`**（跟上面姿势冲突，且在该系统上表现不可靠）。
- **验证**：务必用 PrintWindow 直接抓窗口内容量尺寸（见 §7）。

### 4.2 DPI/跨屏变化用 `XamlRoot.Changed`，不要依赖 `SizeChanged`

- **现象**：把窗口拖到不同 DPI 的显示器，尺寸/画面不更新。
- **根因**：纯 DPI 变化不会触发元素 `SizeChanged`。
- **修复**：`XamlRoot.Changed`（UIElement.XamlRoot 的事件）负责 DPI-only 变化，
  `RootGrid.SizeChanged` 负责窗口尺寸变化。

### 4.3 事件处理器必须异常安全

- **现象**：UI 事件链里一个处理器抛异常会破坏后续事件处理。
- **修复**：所有事件处理器包 `SafeTry(() => ...)`（try/catch + Debug 日志），
  单个事件异常不断链。

---

## 5. 多显示器 / HDR 检测层

### 5.1 `DisplayInformation` 在 WinUI 3 桌面端不可靠

- **现象**：显示器处于 HDR 模式（DXGI 输出报告 G2084、1800 nits），
  `DisplayInformation.GetAdvancedColorInfo().CurrentAdvancedColorKind` 却报
  `StandardDynamicRange` → HDR 开关被禁用（灰色）。
- **根因**：WinUI 3 桌面下该 WinRT API 的已知可靠性问题（返回陈旧/错误数据）。
- **修复**：**检测主信号改用 DXGI 硬件查询**（见 5.2），WinRT 只作为附加信号。
  同时：检测推迟到窗口 `Activated` 之后（构造期更不可靠）。

### 5.2 Composition swapchain 不支持 `GetContainingOutput`

- **现象**：`IDXGISwapChain.GetContainingOutput()` → `DXGI_ERROR_UNSUPPORTED`。
- **修复**：改用**枚举 factory 的全部 adapter/output**，拿每个输出
  `IDXGIOutput6.Description1`（`ColorSpace`、`MaxLuminance`、`DesktopCoordinates`）。

### 5.3 多显示器混合 HDR/SDR：必须按"窗口当前所在输出"判定

- **坑**："任意输出支持 HDR"会让开关状态与真实渲染不符（拖到 SDR 屏时开关还亮着，
  且拖回 HDR 屏不会自动恢复）。
- **修复**：
  1. `SetWindowBounds(RectInt32)` 记录窗口屏幕坐标；
  2. `RecheckOutput()`：窗口矩形与各输出 `DesktopCoordinates` 求交、取面积最大者
     作为当前输出，读它的 `ColorSpace`/`MaxLuminance`；
  3. `AppWindow.Changed`（位置变化）+ 500ms 定时器兜底（显示器热插拔、Windows HDR 开关）
     触发重查；
  4. 变化时重应用色彩空间（`SetHdrMode` → `TryApplyColorSpace`，`_colorSpaceApplied`
     后每次都会真正调用，所以拖回 HDR 屏能自动恢复）。
- **教训**：检测信号必须有"当前显示器"语义；跨线程状态用 volatile + 握手保证顺序。

---

## 6. 渲染线程 / 生命周期层

### 6.1 单个瞬态异常把渲染线程打死（永久停摆）

- **现象**：一次 resize/present 异常 → 渲染线程顶层 catch → 事件把 `ShaderRunner`
  置空 → 渲染永久停止（切换 shader 才能恢复）。
- **修复**：
  1. 循环改**迭代级 try/catch**：单次失败只记录 + Sleep(250) 继续；
  2. `ApplyResize` 内部 try/catch：失败保留 resize 标志 + 500ms 退避重试；
  3. `OnRenderingFailed` **绝不置空 ShaderRunner**。

### 6.2 `WaitForSingleObjectEx(INFINITE)` 会饿死 resize

- **现象**：渲染线程阻塞在帧延迟 waitable 上，UI 侧 resize 永远不应用。
- **修复**：等待改**有界超时**（100ms），每轮都回到 resize 检查。

### 6.3 面板 `Unloaded` 时停渲染 = 永久停摆风险

- **现象**：跨屏/DPI 变化导致面板 Unloaded/Loaded 抖动，若 Unloaded 停循环而
  Loaded 未配对触发，渲染不再启动。
- **修复**：**Unloaded 不停渲染循环**（组合交换链脱离宿主后 Present 安全）；
  `Loaded` 时幂等重启 + 重新排队尺寸。

### 6.4 `_presentedSinceResize`：DXGI 的隐式约束要显式化

- 相邻 `ResizeBuffers` 必须隔一次 Present（§1.3）。把这条规则做成标志位
  （apply 后置 false、present 后置 true），而不是靠时序巧合。

### 6.5 UI 线程上 Join 渲染线程 = 关闭卡顿 + 潜在死锁

- **现象**：关窗时 UI 卡 1-2 秒（见 §1.9）；若关闭恰逢换绑，窗口可能永久挂起。
- **根因**：`StopRenderLoop()` 的 `Join()` 在 UI 线程上等渲染线程当前迭代结束——
  迭代里的 `device.ForEach` CPU 阻塞等 GPU（大窗口下可达数秒）；
  更糟的是渲染线程若卡在 `ReplaceSwapChain` 的 `done.Wait()`（等 UI 线程 lambda），
  UI 线程又卡在 `Join()` → **双向互等死锁**。
- **修复**：
  1. **销毁两阶段化**：UI 线程只 `SetSwapChain(null)`，`Join` + 全部资源销毁放后台线程；
  2. 换绑 lambda 加 `_swapChainPanelNative is null` 守卫（关闭期间排队的换绑
     直接失败返回，不碰悬垂指针）。
- **教训**：**任何 Join/GPU 等待都禁止出现在 UI 线程**；渲染线程与 UI 线程的
  握手（`done.Wait`）必须保证 UI 线程在等待期间能自由处理消息。

### 6.6 GraphicsDevice 的生命周期要跟着渲染线程走

- **坑**：`MainWindow.Dispose()` 里直接 `_device.Dispose()`（ComputeSharp）——
  它的队列/fence 拆除若与仍在运行的渲染线程 dispatch 竞争 → 崩溃或卡顿。
- **修复**：`GraphicsDevice` 的所有权移入渲染器的**后台销毁流程**：
  先 `Join` 渲染线程退出，再释放设备（本 Demo 中 `_d3D12Device` 包装
  与 `GraphicsDevice` 一并由渲染器释放，MainWindow 不再碰它）。

---

## 7. 测试方法论（最容易骗过你的部分）

### 7.1 `CopyFromScreen` 会抓到被遮挡的窗口 → 假"静止/冻结"

- **现象**：动画测试间歇性 0/250；重跑结果时好时坏；改代码后"变好了/变坏了"
  其实什么都没变。
- **根因**：前台窗口被测试脚本/控制台/其他窗口遮挡时，屏幕捕获抓到的是**别的窗口**。
- **修复**：用 **`PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`** 直接渲染窗口
  内容到 DC（不依赖前台/遮挡）。这是验证任何窗口内容的首选。

### 7.2 窗口尺寸超过屏幕 → 屏幕捕获结果无效

- **现象**：把窗口设成 2688/3494 宽（超过 2560 屏幕），"内容撑满"的测量是垃圾数据。
- **修复**：测试窗口尺寸**限制在屏幕内**；或者用 PrintWindow（抓窗口本体，
  与屏幕无关）。

### 7.3 真实拖动的节奏必须用高频事件模拟

- 单次 `SetWindowPos` + 长间隔测不出 resize 风暴类问题。用 **40-90ms 间隔、连续
  8 秒**的事件流模拟真实拖动，三类场景各测：resize 风暴、跨屏移动风暴、混合风暴。

### 7.4 只查 swapchain desc ≠ 画面真的对

- ResizeBuffers 生效（desc 正确）不代表面板显示了新帧（见 §1.4 冻结）。验证必须
  双管齐下：desc 尺寸 + PrintWindow 两帧 diff（动画）。

### 7.5 阈值型 diff 对慢帧敏感

- 超大分辨率下 shader 每帧耗时数秒，3 秒间隔的快照可能恰好同帧。验证动画时
  要么把窗口限制在合理尺寸，要么加大快照间隔。

### 7.6 关闭/退出类问题：测进程退出耗时，别肉眼看

- 关闭卡顿（1-2s）这类问题肉眼难以量化。用 `PostMessage(hwnd, WM_CLOSE)` 异步触发
  关闭，然后**轮询 `Process.HasExited` 计时**。三个必测场景：
  常规关闭、resize 风暴后立即关闭（覆盖换绑死锁）、大窗口（慢 dispatch）下关闭。
- 目标：全部 <100ms；任何场景超过 1s 都视为回归。

---

## 8. 工具链 / 工程流程

### 8.1 PowerShell 批量替换源码会毁文件

- **现象**：`-replace` 的替换串里写 `\r\n`（单引号字符串）→ 字面 `\r\n` 被插入源码；
  正则里的类名替换改了定义没改引用。
- **教训**：**绝不用 PowerShell 正则批量改源码**；要么用编辑工具精确替换，
  要么替换后立即构建 + grep 校验（`Select-String "\\r\\n"`）。

### 8.2 `git stash` 不包含未跟踪目录

- 用 stash 验证"原始代码"时，未跟踪的 `Hdr/` 目录还在，污染基线。
- **教训**：stash 前先移走未跟踪目录，或 `git stash -u`。

### 8.3 文件日志的乱序/丢失误导排查

- 多线程 `File.AppendAllText` 会乱序；进程崩溃时最后几条日志可能丢失。
- **教训**：关键时序日志加时间戳毫秒级 + 单线程写入；"日志里没这行"不等于
  "代码没执行"。

### 8.4 NuGet restore 网络错误 ≠ 代码问题

- `NU1301 / SSL EOF` 是网络瞬断；用 `dotnet build --no-restore` 用本地缓存继续，
  别改代码。

---

## 9. 正确姿势（最终可用架构）

一次 resize 的完整流程（渲染线程 + UI 线程协作）：

```
UI 线程                                 渲染线程
RootGrid.SizeChanged /
XamlRoot.Changed / AppWindow.Changed
   │
   ├─ SyncPanelAndBuffer()
   │    physical = DIP × RasterizationScale
   │    PanelHost/面板尺寸 = physical
   │    面板 ScaleTransform = 1/DpiScale
   │    QueueResize(physical)  ──────────►  _width/_height/_resizeQueuedAt/_isResizePending
   │
   │                                     循环：
   │                                       ├─ TryApplyPendingResize()
   │                                       │    门：_isResizePending
   │                                       │        && _presentedSinceResize
   │                                       │        && 尺寸稳定 ≥30ms
   │                                       │        && 退避期已过
   │                                       ├─ 释放旧 backbuffer 包装器
   │                                       ├─ SignalAndWait()  (GPU 空闲)
   │                                       ├─ ReplaceSwapChain(w,h)
   │                                       │    ├─ DispatcherQueue 入队换绑 lambda：
   │                                       │    │    建新链 → SetSwapChain(新) →
   │                                       │    │    旧链入退休列表(不释放) → done
   │                                       │    └─ done.Wait()   ← 无超时！
   │                                       ├─ 校验 Description1 == w,h
   │                                       ├─ 重建 backbuffer/RTV/帧纹理/SRV
   │                                       └─ _presentedSinceResize = false
   │                                      继续渲染（旧链尺寸照常画）
   │
   └─ ApplyHdrMode / SetHdrMode ──────────►  _hdrMode + TryApplyColorSpace
                                              （首帧 present 后由渲染线程应用）
```

要点清单：

- **swapchain**：`R10G10B10A2_UNORM` + `FLIP_SEQUENTIAL` + `FRAME_LATENCY_WAITABLE_OBJECT`；
  色彩空间首帧 present 后 `SetColorSpace1`（G2084=HDR10 / G22=SRD）。
- **面板**：物理尺寸 + `ScaleTransform(1/DpiScale)`；无 `SetMatrixTransform`。
- **resize**：重建 swapchain（新链先建、UI 线程换绑、旧链延迟释放），绝不 `ResizeBuffers`。
- **HDR 检测**：DXGI 输出枚举 + 窗口矩形求交（当前输出语义）；
  `AppWindow.Changed` + 500ms 定时器兜底；WinRT `DisplayInformation` 仅作附加信号。
- **帧缓冲**：`ReadWriteTexture2D<Rgba64, Float4>`；shader 内做 PQ/sRGB 编码；
  全屏 pass 只搬运（`null` SRV desc、`BlendDescription.Opaque`、command list 先 Close）。
- **渲染循环**：迭代级 try/catch；有界 waitable 等待；`_presentedSinceResize` 硬保证；
  失败退避重试；`RenderingFailed` 不杀 runner。
- **生命周期**：Unloaded 不停循环；事件处理器全部异常安全。
- **销毁**：两阶段——UI 线程只 `SetSwapChain(null)`；`Join` + GPU 排空 + 逐条
  Dispose 链 + 释放 `GraphicsDevice` 全部在后台线程；关闭路径禁止任何阻塞。
