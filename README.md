# UMango（Uneven Mango Application）

跨平台**包管理器图形界面**。用图形界面管理 `npm` / `pnpm` / `git` / `scoop` / `winget` / `Homebrew`。

交互设计对标 PCL-CE（仅观察其交互与设计数值），业务代码自研；
所复用的 PCL-CE 代码严格限于 **Apache-2.0 的 `PCL.Core/`**，逐文件标注来源与变更。

> **后端完全不含 Minecraft 逻辑。** 这不只是架构取向 —— 它同时承担合规功能：
> 《PCL 存储库合理使用指南》把"启动或下载 Java 版 Minecraft、下载或管理 Mod"
> 明确列为「重度使用」情形，而重度使用会强制要求**改名**（名称须以 `Plain Craft Launcher (PCL)` 开头）
> 与**公开源代码**。软件与 PCL「实质功能零重叠」是项目留在「轻度使用」的基石。
> 详见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

---

## 技术栈

**两个界面壳共享同一个业务与界面逻辑层**：
`UMango.Core`（业务）+ `UMango.Shell`（界面逻辑）是**平台中立**的（`net10.0`），
WPF 壳（`net10.0-windows`）与 Avalonia 壳（`net10.0`）都消费它们，
因此同一份实现只在 macOS/Linux 上也能跑起来（macOS 上原生的就是 Avalonia 壳）。

```
src/UMango.Core/            平台中立业务库（多目标 net10.0 + net10.0-windows，无 WPF 依赖）
  ├─ PackageManagers/       六个包源适配器 + 注册表 + 能力声明
  ├─ Download/              aria2 / eD2k / 内置 HttpClient 三引擎
  ├─ Install/               安装执行器：按下载方式拆子项、解析进度、写记录
  ├─ Terminal/              终端集成桥（原生回传，非黑底嵌入终端）
  ├─ Animation/             缓动表 + 方向矢量策略（镜像 PCL / PCL 兼容）
  ├─ Configuration/         描述符驱动的设置体系（含迁移与容错）
  ├─ Records/               管理记录与次数记录
  ├─ Platform/              系统判断（平台/架构/家目录集中判定）
  └─ Execution/             进程运行器（流式输出，终端桥的基础）

src/UMango.Shell/           两壳共用的界面逻辑层（net10.0，无任何 UI 框架依赖）
  ├─ ViewModels/            可移植 ViewModel（组合根、页面逻辑、诊断页）
  ├─ Localization/          界面文案真相源（纯文本 .txt）+ 语言目录 + 编排层
  └─ Services/              组合根（AppServices）与平台无关的服务

src/UMango.App/             WPF 界面（外壳 + 5 个页面 + 4 套主题字典 + 动画播放器）
src/UMango.Avalonia/        Avalonia 界面（macOS / Linux 上的原生壳）
src/UMango.Core.Tests/      Core 测试
src/UMango.Shell.Tests/     Shell 层测试（跨平台）
src/UMango.App.Tests/       WPF 壳测试
tools/XamlAudit/            XAML 静态审计工具（绑定/资源键/事件处理器/主题一致性）
tools/LocalizationAudit/    本地化占位符审计工具（语言间漂移 + 调用点实参数）
```

**`UMango.Core` 刻意不设 `UseWPF`，并多目标编译 `net10.0`。** 这不只是声明：
`net10.0` 那一支是"平台中立"的**编译期证明** —— 一旦有人往 Core 里塞 Windows 专用 API，
该目标立刻编译失败，而不是等到跨平台重构时才发现。

---

## 当前状态

### 已完成并验证

| 能力 | 实现位置 | 验证方式 |
|------|---------|---------|
| 六个包源适配器（含新增 Homebrew） | `Core/PackageManagers/Adapters/` | 单测 + npm/pnpm/brew **真机验证** |
| 平台门控（不支持的平台不执行 CLI） | `Core/PackageManagers/CliPackageManagerBase.cs` | 单测（macOS 上对 winget/scoop 断言） |
| 搜索 / 版本列表 / 依赖解析 | 各适配器 | 单测 + 真机 |
| **按下载方式列子项进度** | `Core/Install/InstallExecutor.cs` + `StepNames` | 单测（假 CLI 输出驱动全链路） |
| 安装执行（真跑 CLI） | `Core/Install/` | 单测 |
| **终端集成桥**（原生回传，非黑底终端） | `Core/Terminal/TerminalBridge.cs` | 单测 |
| 下载引擎：aria2 / BitTorrent / eD2k | `Core/Download/` | **真机 aria2 下载**单测 |
| 应用列表读取（参考 UniGetUI） | Core `ListInstalledAsync` + 下载页「已安装」 | 单测 + **真机读取本机已安装包** |
| 管理记录与次数记录 | `Core/Records/InstallRecordStore.cs` | 单测 |
| 系统判断 | `Core/Platform/SystemInfo.cs` | 单测 |
| 设置体系（描述符驱动、可迁移、容错） | `Core/Configuration/` | 单测 |
| 动画系统（33 种缓动 + 方向镜像 + 全局切换） | `Core/Animation/` | 单测（含与 PCL 公式的定点校验） |
| 主题：4 套 token 字典（PCL经典/Win10 Fluent × 浅/深） | `App/Themes/` | 审计工具校验四套 key 完全一致 |
| 顶部 5 个 banner 选项卡 + 左栏菜单 + 三大金刚键 | `App/MainWindow.xaml` | 审计 + ViewModel 单测 |
| **两壳共用的界面逻辑层**（设置/音乐/反馈/工具/下载页） | `Shell/ViewModels/` | Shell 单测 + Avalonia 壳在 macOS 上启动验证 |
| **界面文案真相源**（纯文本 `.txt`，两壳共用同一份） | `Shell/Localization/Languages/` | 单测 + 本地化静态审计 + `.txt↔.xaml` 一致性检查 |
| 合规声明 | 工具页 + `THIRD-PARTY-NOTICES.md` | — |

### 自动化验证

```bash
bash scripts/xaml-audit.sh          # XAML 静态审计：绑定路径/资源键/事件处理器/四套主题一致性
bash scripts/localization-audit.sh  # 本地化审计：语言间占位符漂移 + 调用点实参数
dotnet test src/UMango.slnx         # 2585 项（Core 2261 + Shell 213 + App 111）
```

| 手段 | 覆盖 |
|------|------|
| Core 单测 2261 项 | 业务逻辑、适配器、下载引擎（含真机 aria2 下载） |
| Shell 单测 213 项 | 可移植 ViewModel、语言目录、文案模板契约、外壳导航 |
| App 单测 111 项 | ViewModel 逻辑、跨页面设置一致性、外壳导航、已安装列表 |
| XAML 静态审计 | 800+ 处绑定/资源引用、四套主题键一致性、事件处理器存在性 |
| 本地化静态审计 | 329 处文案调用点的实参数、语言间占位符漂移 |
| **真机渲染** | ⚠️ Avalonia 壳已能在 macOS 上启动（进程级验证）；像素级渲染与交互仍待补 |

### 尚未完成

- **真机渲染与交互验证**：本项目在 macOS 上开发，WPF 只能在 Windows 上运行。
  Avalonia 壳（`src/UMango.Avalonia`）已在 macOS 上**进程级**验证启动不崩，
  但布局观感、动画手感的**像素级**验证尚未做（开发机无显示器/录屏权限）。
- **macOS/Linux 的系统原生窗口风格**：由 Avalonia 壳承担，窗口已能创建，
  但各页面的实际观感仍待真机确认。
- 功能对照清单中尚未迁移的非 MC 项，见下节。

---

## 与 PCL-CE 的功能对照

[`docs/PCL_CE_FEATURE_PARITY.md`](docs/PCL_CE_FEATURE_PARITY.md) 是逐项实测的对照清单（137 项功能）：

| 类别 | 数量 | 处置 |
|------|-----:|------|
| 非 MC 功能 | 55 | **应当迁移**（多数位于 Apache-2.0 的 `PCL.Core/`） |
| — 已完成 | 24 | 见上表 |
| — 部分完成 | 16 | |
| — 未做 | 15 | |
| **MC 专属** | **78** | **不迁移**：迁移即触发重度使用义务（改名 + 开源），且与「剥离 MC」红线矛盾 |

未迁移的非 MC 项按优先级见该文档第 4 节。

---

## 开发

```bash
# 路径注意：OneDrive 挂载名会变（OneDrive-个人 ↔ OneDrivePersonal），务必动态解析
P=$(ls -d /Users/jonathan/Library/CloudStorage/*/"Deepseek Harness"/UMango | head -1)

export PATH="$HOME/.dotnet:$PATH"          # .NET SDK 装在 ~/.dotnet（无需 sudo）

dotnet build "$P/src/UMango.slnx"           # 交叉编译：macOS 上可编译 WPF（EnableWindowsTargeting）
dotnet test  "$P/src/UMango.slnx"
bash "$P/scripts/xaml-audit.sh"
bash "$P/scripts/localization-audit.sh"

# 跑 Avalonia 壳（macOS / Linux 原生）
dotnet run --project "$P/src/UMango.Avalonia"
```

### 已知环境约束

- **WPF 运行时在 macOS 上不存在**。因此：
  - 测试工程的 `runtimeconfig` 不能要求 `Microsoft.WindowsDesktop.App`，
    否则测试宿主直接起不来 → 用 `<DisableTransitiveFrameworkReferences>` 关掉传递框架引用；
  - App 层代码**不能有编译期的 WPF 类型引用**（哪怕包在 try/catch 里 ——
    JIT 失败发生在方法能执行之前），需要访问 WPF 时走反射（见 `Services/AppServices.cs` 的 `UiDispatch`）。
- `dotnet build` 产出的 `bin/`、`obj/` 已由 `.gitignore` 全局忽略（`**/bin`、`**/obj`）。

---

## 文档

| 文档 | 内容 |
|------|------|
| [`docs/PCL_CE_FEATURE_PARITY.md`](docs/PCL_CE_FEATURE_PARITY.md) | PCL-CE 功能逐项对照与迁移状态（137 项，实测） |
| [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) | 许可证与合规（含重度使用义务 B5–B9） |
| [`docs/DESIGN_UI_DOTNET.md`](docs/DESIGN_UI_DOTNET.md) | UI 层规格（多 Agent 并行的契约） |
| [`docs/XAML_AUDIT.md`](docs/XAML_AUDIT.md) | XAML 审计工具的原理、能力与不覆盖范围 |
| [`docs/ANIMATION.md`](docs/ANIMATION.md) | 动画设计 |

### 已废弃文档

`docs/ARCHITECTURE.md`、`docs/ROADMAP.md`、`docs/PROGRESS.md`、`docs/SCREENSHOT_CHECK.md`
描述的是**已废弃的第一版（Flutter + Rust + C++）**，保留仅作历史参考。
相关代码在 `ui/`、`core/`、`native/`，**不要在其上继续开发**。
