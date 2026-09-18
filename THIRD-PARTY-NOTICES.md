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

winget 与 scoop 在 simple-icons 中无对应图标，界面使用**自绘字母徽章**，
未使用任何来源不明的商标图形。

---

## 3. 本项目自身

UMango 自身代码的许可证由项目所有者决定（原始项目说明中「非强制开源」）。
无论选择何种许可证，**上述第三方组件的许可条款独立生效且必须继续遵守**。

---

## 4. ⚠️ 重度使用的 5 项强制义务（来自《PCL 存储库合理使用指南》）

> 完整规范表见同目录外的分析报告：`../PCL-CE-许可证与代码构成分析报告.md`（487 行，逐条绑定原文）。
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
