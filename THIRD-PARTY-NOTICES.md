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
