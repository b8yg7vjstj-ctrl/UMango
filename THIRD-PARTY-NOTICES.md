# 第三方组件与许可证声明

UMango 是**独立开发的第三方软件**，与 PCL / PCL-CE 及其开发者**无任何关联**，
未获其授权、赞助或认可。

---

## 1. 采用自 PCL-CE 的代码

仓库：`PCL-Community/PCL-CE`

| 来源目录 | 许可证 | 使用方式 |
|---------|--------|---------|
| `PCL.Core/` | **Apache License 2.0** | ✅ **直接采用并修改**（允许，需保留声明与变更说明） |
| `PCL.Core.SourceGenerators/`、`PCL.Core.Test/` 等其余非 UI 目录 | Apache License 2.0 | 同上 |
| `Plain Craft Launcher 2/`（WPF 界面） | **《PCL 分发有限许可》** + **《PCL 存储库合理使用指南》** | ⚠️ 见下节 |

### 已采用的 PCL-CE 代码（Apache-2.0）

| UMango 文件 | 来源 | 变更说明 |
|------------|------|---------|
| `src/UMango.Core/App/Tasks/TaskState.cs` | `PCL.Core/App/Tasks/TaskState.cs` | 改命名空间为 `UMango.Core`，补充中文注释 |
| `src/UMango.Core/App/Tasks/ITask.cs` | 同上目录 `ITask.cs` | 同上 |
| `src/UMango.Core/App/Tasks/ITaskProgressive.cs` | 同上 | 同上 |
| `src/UMango.Core/App/Tasks/ITaskCancelable.cs` | 同上 | 同上 |
| `src/UMango.Core/App/Tasks/ITaskPausable.cs` | 同上 | 同上 |
| `src/UMango.Core/App/Tasks/ITaskGroup.cs` | 同上 | 同上 |
| `src/UMango.Core/App/Tasks/TaskModel.cs` | 同上 | 新增 `IsGroup` / `Children` 以承载子项进度 |
| `src/UMango.Core/App/Tasks/TaskCenter.cs` | 同上 | 改为不依赖 WPF Dispatcher；新增完成/失败次数统计 |

每个被采用的文件头部都带 `SPDX-License-Identifier: Apache-2.0` 与来源说明。

Apache-2.0 全文见：https://www.apache.org/licenses/LICENSE-2.0

**采用路径的合规性依据**：报告 §6 对"`PCL.Core` 是否真是独立重写"做了实证检验
（引用上游问题号次数为 0、源码中文注释重合度仅 **0.51%** 且全部是通用 XML 文档模板、
项目自述"由社区开发与维护"、其内 3 处第三方许可头均不指向上游 PCL），
结论为"证据一致指向 `PCL.Core` 是 CE 社区新写的库"。
我们采用的 8 个文件全部位于 `PCL.Core/App/Tasks/`，因此这些文件置于 Apache-2.0 之下成立。
（报告同时如实标注该检验基于注释文本、属**启发式而非证明**，这一点也保留在案。）

### 关于 `Plain Craft Launcher 2/`（界面目录）

该目录**不是** Apache-2.0，而适用《PCL 分发有限许可》与《PCL 存储库合理使用指南》：

- 《分发有限许可》授予的是**原样副本**的存储/镜像/下载/再分发权，**不包含**修改后再分发；
- 《合理使用指南》规定：**重度使用**（基于本仓库修改，或参考较大量的内容以重新实现 PCL 的部分实质功能）
  必须**明确声明这是第三方基于 PCL 独立进行的二次创作**。

**UMango 的合规做法**：
1. 本文件与本软件界面内（工具页「合规声明」区块）均做上述声明；
2. **不复制**该目录下的图片资源（logo、图标、背景图一律不使用）；
3. 该目录下的 XAML/C# 代码**不以原样或改写形式并入** UMango；
   仅提取**设计数值**（颜色、尺寸、字号、圆角）用于还原视觉，
   这些数值属于设计参数而非表达性代码；
4. 界面图标改用 **lucide**（ISC 许可）自行集成。

---

## 2. 其他第三方资源

| 资源 | 许可证 | 用途 |
|------|--------|------|
| [lucide](https://lucide.dev/) 图标 | **ISC** | 界面全部矢量图标（PCL-CE 亦使用同一图标集） |
| [simple-icons](https://github.com/simple-icons/simple-icons) | **CC0 1.0** | npm / pnpm / git / Homebrew 品牌图标 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | MVVM 基础设施（`ObservableObject` / `RelayCommand`） |
| [LiteDB](https://github.com/mbdavid/LiteDB) 5.0.21 | **MIT** | 内嵌文档数据库（`src/UMango.Core/Database/`，对应 PCL-CE 的 `DatabaseService` / `DatabaseEntry`） |
| [YamlDotNet](https://github.com/aanpierdahl/YamlDotNet) 18.1.0 | **MIT** | YAML 配置后端（`src/UMango.Core/Configuration/Storage/YamlFileProvider.cs`，对应 PCL-CE 的同名后端） |
| [FluentValidation](https://github.com/FluentValidation/FluentValidation) 12.1.1 | **Apache-2.0** | 验证器族（`src/UMango.Core/Validation/`，对应 PCL-CE 的 `Utils/Validate/`；零传递依赖） |

winget 与 scoop 在 simple-icons 中无对应图标，界面使用**自绘字母徽章**，
未使用任何来源不明的商标图形。

---

## 3. 本项目自身

UMango 自身代码的许可证由项目所有者决定（原始项目说明中「非强制开源」）。
无论选择何种许可证，**上述第三方组件的许可条款独立生效且必须继续遵守**。

---

## 4. ⚠️ 重度使用的 5 项强制义务（来自《PCL 存储库合理使用指南》）

> 完整规范表见 [`docs/licensing/PCL-CE-许可证与代码构成分析报告.md`](docs/licensing/PCL-CE-许可证与代码构成分析报告.md)
> （487 行，逐条绑定原文；已纳入版本控制，见下）。
> 下面是**与本项目直接相关**的部分，以及我们对每一项的处置。

判定逻辑（原文）：
- **轻度使用**：仅参考极小部分内容，**或**「虽参考了较大量的内容，但制作的软件与 PCL 的**实质功能不存在任何重叠**」
  → 义务只有两项：① 适当形式署名；② 不得混淆或暗示与 PCL 有关。
- **重度使用**：「基于本存储库进行修改」**或**「参考大量内容以重新实现 PCL 的部分实质功能」
  → 触发下面全部五项义务。

| # | 义务 | 原文要求 | UMango 的处置 |
|---|------|---------|--------------|
| B5 | 二次创作声明 | 明确表示是第三方基于 PCL 独立二次创作的产物 | ✅ 已做（见第 6 节与应用内声明） |
| **B6** | **命名规则** | **名称必须以 `Plain Craft Launcher (PCL)` 开头**，其后添加足以表现"由第三方修改"的后缀 | ❌ **UMango 不满足**。这是「复制并修改界面代码」路径下**最硬的阻塞项** |
| **B7** | 署名 + **开源** + 指南延续 | (a) 关于页**首位**署名龙腾猫跃及赞助链接；(b) **公开源代码**；(c) 继续使用该指南文件 | ⚠️ (a) 可做；(b) **与本项目「非强制开源」的定位冲突** |
| B8 | 正版/赞助劝导 | 「软件**若有 Minecraft 启动功能**」，应实现不弱于 `McLaunchPrecheck` 的劝导 | ➖ **不适用**：UMango 无 MC 启动功能（原文以「若有」为前提） |
| **B9** | 禁赞助解锁式体验 | 不得有与赞助解锁类似的功能；原文举例：**界面主要色相必须为蓝色** | ✅ 已满足：UMango 主色相沿用 PCL 的蓝色 `#1370F3` |


### 4.2 命名规则的正反示例（原文列举，逐条照录）

B6 是这个项目**最容易被忽略、后果又最直接**的一条，因此把原文的正反示例一并收在这里：

| | 示例 | 说明 |
|---|------|------|
| ✅ 允许 | `PCL 某服务器魔改版` | 前缀合规 + 后缀表明"第三方修改" |
| ✅ 允许 | `PCL PR Collection` | 同上 |
| ✅ 允许 | `PCL (XX Bug Fix)` | 同上 |
| ✅ 允许 | `PCL XXX修改版` | 同上 |
| ❌ 禁止 | `PCL 某服务器特供版` | **暗示官方提供** |
| ❌ 禁止 | `PCL 3` | 像官方后续作品 |
| ❌ 禁止 | `PCL 手机版` | 像官方版本 |
| ❌ 禁止 | `PCL++` | 像官方后续作品 |
| ❌ 禁止 | `PCL Pro` | 像官方后续作品 |

判定要点：**前缀必须是 `Plain Craft Launcher (PCL)`，后缀必须表明"这是第三方改的"**。
"魔改 / 修改版 / Bug Fix"这类表述可以；"特供版 / 3 / Pro / ++"这类会被读成官方作品，不行。

### 4.3 轻度使用的两条义务（B3 / B4）

若落在「轻度使用」（UMango 当前所处区间），义务只有两条 —— 仍是**必须**履行：

| # | 义务 | 我们怎么做 |
|---|------|-----------|
| **B3** | 以适当形式**署名** | 本文件 + 应用内工具页的合规声明区块 |
| **B4** | **不得混淆**或暗示与 PCL 有关 | 名称用 `UMango`（不含 PCL / Plain Craft Launcher 及近似词）；界面无 PCL 标识；不使用其图片资源 |

### 4.4 一条原文自述的边界（B0）

指南**自称不是法律文件**（"本指南仅作为说明，不是法律文件或协议"），
但开发者承诺"在遵守本指南，且**善意、合理使用**的情况下，**不会追究责任**"。
因此按它执行是**降低实际风险**的做法，而不是履行一项法律义务 ——
这一点我们如实标注，不夸大成"合同约束"。

### 4.1 由此得出的关键结论（必须先决定，再动手）

**UMango 与 PCL 的实质功能零重叠**（一个是包管理器 GUI，一个是 Minecraft 启动器），
因此**只要不「基于本存储库进行修改」（即不复制并改写其代码），就落在「轻度使用」**，
义务仅两项：署名 + 不混淆命名。

但一旦**复制并修改 `Plain Craft Launcher 2/` 下的代码**，立刻构成「重度使用」，将同时被要求：
1. **把名称改成以 `Plain Craft Launcher (PCL)` 开头**（B6）——与「UMango」这个项目名直接冲突；
2. **公开源代码**（B7b）——与项目「非强制开源」的定位冲突。

**当前项目的实际做法（合规）**：
- 业务层只采用 `PCL.Core/`（Apache-2.0），逐文件标注来源与变更 —— 不触发自定义许可；
- 界面层**仅提取设计数值**（颜色/尺寸/字号/圆角），**未复制任何 XAML/C# 代码**，未使用其图片资源；
- 因此目前停留在「轻度使用」，只需署名 + 不使用易混淆命名。

> **如果后续决定改为「直接复制并修改 PCL-CE 的界面代码」**，则必须先接受 B6（改名）与 B7（开源）
> 两条义务，或者改为在独立进程中以「聚合」方式使用、不并入本项目代码。

---

## 5. 下载引擎的第三方工具（GPL 系，**只以独立进程调用**）

需求要求「弃用 PCL2 网页下载代码，重新集成 aria2、bittorrent、ed2k 等多功能下载工具，注意开源许可证限制」。
本项目的处理方式如下 —— **这不是"尽量注意"，而是写进接口约束的硬规则**：

| 工具 | 许可证 | 提供的能力 | 集成方式 |
|------|--------|-----------|---------|
| **aria2**（`aria2c`） | **GPL-2.0** | HTTP / HTTPS / FTP / SFTP / **BitTorrent** / Metalink | 独立进程调用（聚合） |
| **aMule**（`amule`/`amuled`） | **GPL-2.0** | **eD2k** | 独立进程调用（聚合） |
| **mldonkey**（`mlnet`） | **GPL-2.0** | **eD2k** | 独立进程调用（聚合） |
| .NET `HttpClient` | MIT（.NET 自身） | HTTP / HTTPS 兜底 | 直接使用（无许可负担） |

### 5.1 硬性约束（已落实在代码里）

`UMango.Core/Download/IDownloadEngine.cs` 的接口注释中写明：

> 引擎**只能以「独立进程 + 命令行」的方式调用它们**（法律上属"聚合"），
> **不得把它们的代码静态或动态链接进本程序**，也不得随包分发其二进制。

因此：
- 引擎只持有**可执行文件路径**，通过 `ProcessRunner` 起子进程；
- 项目**不引用**任何 aria2/aMule 的 .NET 绑定或库；
- 项目**不随包分发** `aria2c.exe` 等二进制，用户需自行安装；
- 每个引擎都通过 `LicenseNote` 属性把上述事实**展示给用户**（设置页/诊断页会显示）；
- 有一条单元测试专门断言「所有引擎都必须声明许可说明，且 GPL 系引擎的说明里必须含『不链接』」。

### 5.2 为什么 BitTorrent 不需要单独的工具

**aria2 原生支持 BitTorrent**（含磁力链接与 `.torrent`），因此需求里的
「bittorrent」不需要再引入第二个 GPL 工具 —— 同一份 aria2 即可覆盖。
这一点有实测依据：本机 aria2c 1.37.0 的 `-h` 中 BitTorrent 相关选项完整。

### 5.3 eD2k 的现状（如实说明）

aria2 **不支持** eD2k，必须依赖专用 P2P 客户端；而这类客户端通常以**常驻守护进程**运行，
本身维护下载目录与队列，命令行只能把链接"投递"过去。

- 本机**未安装**任何 eD2k 客户端，因此该引擎当前**不可用**；
- 「未安装时如实报不可用并给出安装指引」这条路径**有单元测试覆盖**；
- 而**投递逻辑本身未经真机验证** —— 与 winget/scoop 适配器同样如实标注，
  代码注释中写明了这一点。

---

## 7. 本文件的依据来源

本文件的条款判定依据是 **[`docs/licensing/PCL-CE-许可证与代码构成分析报告.md`](docs/licensing/PCL-CE-许可证与代码构成分析报告.md)**
（487 行，逐条绑定 PCL-CE 原文行号）。

该报告原本只存在于项目工作区之外，**一度丢失**（第 5 轮读过、之后发现已不在原位置），
现由项目所有者重新提供并纳入版本控制 —— 这样它不会再丢，
且本文件的每一条判定都能被追溯到具体依据。

配套的逐项功能对照清单见 [`docs/PCL_CE_FEATURE_PARITY.md`](docs/PCL_CE_FEATURE_PARITY.md)（172 项，实测）。
