# PCL-CE 功能对照清单（功能清单 + 迁移状态映射）

> **目的**：为「后端完全剥离 Minecraft 逻辑」与「完整迁移 PCL-CE 全部功能」这两条需求之间的冲突，
> 提供**可核查的事实依据** —— 到底有哪些功能属于 MC 专属、哪些不是，各自体积多大，UMango 现在做到哪一步。
>
> **参照物**：`PCL-Community/PCL-CE`，分支 `dev`，commit `ab6e2f1`（`fix(setup-java): 修复 Java 选择页面刷新无效 (#3609)`）。
> 本地稀疏克隆位于 `/tmp/PCL-CE`，本次调研实测 **918 个文件**（不含 `.git`）、**803 个 `.cs` / 138,447 行**、**100 个 `.xaml` / 24,109 行**。
> 该 commit 与项目所有者已有的 `../PCL-CE-许可证与代码构成分析报告.md`（487 行）所分析的版本**完全一致**（同一 commit hash、同样 803 个 `.cs`），两份文档的数字可直接互引。
>
> **UMango 侧**：本次实际读取源码确认状态，**不采信文档自述**。凡文档与源码不一致处，以源码为准并单独注明。
>
> **调研日期**：基于本地工作副本的实测（`find` / `wc -l` / `grep -n`）。
> **重要限制**：`/tmp/PCL-CE` 是 `--depth 1` 的**部分克隆（partial clone，带 promisor remote）** ——
> `git log` 只有 1 个 commit，且 **`git status` / `git diff` 在离线时无法工作**（会尝试向 github.com 拉取缺失对象并超时）。
> 因此："上游继承 vs CE 新增"的判断只能依赖代码内容证据，不能靠 git blame；
> 而"PCL-CE 未被本次调研修改"这一点的证据是**文件数与行数与初始实测完全一致**（918 文件、138,447 行 `.cs`），
> 而不是 `git status` 的干净输出。

---


> **状态更新（第 11 轮起）**：本报告第 1 节的计数是产出时的快照。此后已完成：
> - **P0-3 日志系统**（`Core/Logging/` + 日志页 + 一键导出诊断包）
> - **P0-4 已安装列表接入界面**（下载页「搜索结果 ↔ 已安装」模式切换）
> - **P0-2 配置体系**：**已完成**（`Core/Configuration/`，描述符驱动 + 5 种存取后端 + 迁移引擎 + 事件总线）。
>   与 PCL 原版的**四处实质差异**：
>   ① **不迁源生成器**（`PCL.Core.SourceGenerators/` 5 个 2,039 行）—— UMango 用普通描述符对象表达同样的语义，
>      换来"能直接在 macOS 上单测"与"新增设置只改一处"。
>   ② `ConfigService` **不是静态单例**（PCL 的 `_Items` / `_KeySet` 是静态字段，测试之间互相污染）→ UMango 做成实例类。
>   ③ **配置读坏不让程序退出**（PCL 调 `Lifecycle.ForceShutdown(-2)`）→ UMango 把坏文件挪到 `.failbackup`、
>      用 `.bak` 恢复，然后继续启动。
>   ④ **迁移起点按路径最长选**（PCL 按迁移表倒序取第一个存在的来源，等于让书写顺序决定用哪个源文件）。
>   另修掉 PCL 三个真实缺陷：`JsonFileProvider.Get` 读失败时会把坏值改写成「true / 原文」并落盘（数据损失）、
>   `YamlFileProvider.Get` 键不存在时报 `ArgumentNullException`（指不到是哪个键不见了）、
>   `DynamicCacheConfigStorage.InvalidateCache` 忘记 `Stop()` 底层存储（资源泄漏）。
>> - 附加：`UMango.Core` 去掉残留 UseWPF 并多目标编译 `net10.0`（平台中立成为编译期证明）
>
> - **P0-1 持久化缓存**：**已完成**（用 JSON 实现，无 SQLite 依赖） —— 用 JSON 文件实现同一套
>   `IAvailabilityCache` 抽象（数据量极小：六个包源、每天一次），
>   因此**不必引入 `Microsoft.Data.Sqlite`**，那个依赖冲突自然消解。
>   将来若缓存内容变复杂，只需替换实现，接口已经隔离好。
>
> 本清单在第 11 轮交付它的当天就被当作工单使用：P0-3 → 日志系统与诊断包（`f2cc0b9`），
> P0-1 → 持久化缓存（本轮）。这正是"逐项实测清单"的价值所在。

## 0. 先说结论：冲突的真实形状

用户提出的两条需求，**表层冲突在"MC 功能要不要进 UMango"，但真正的硬约束在许可证**。事实如下（全部可核查）：

1. **PCL-CE 的许可证边界 ≈ 功能边界**（该结论由既有分析报告提出，本次复核成立）。
   - `PCL.Core/`：**Apache-2.0**，可自由采用、修改、闭源商用。
   - `Plain Craft Launcher 2/`：**《PCL 分发有限许可》**（自定义专有协议），**不授予修改后再分发的权利**。
   - 实测证据（`/tmp/PCL-CE/LICENSE` 尾部）：

     ```
     Copyright 2025 PCL Community

     SPECIAL DIRECTORY: Plain Craft Launcher 2/

     Licensed under some custom terms discribed in the 'Plain Craft Launcher 2/LICENCE' file.

     ALL THE OTHER DIRECTORIES

     Licensed under the Apache License, Version 2.0 ...
     ```

2. **而"启动 Minecraft"这一实质功能，恰好整个落在自定义许可目录里。**
   `Plain Craft Launcher 2/Modules/Minecraft/ModLaunch.cs`（2,807 行，`McLaunchStart` 在 `:233`）是真正的启动管线，
   它**不在** Apache-2.0 的 `PCL.Core` 里（`PCL.Core/Minecraft/Launch/` 只有 1 个文件 54 行，仅释放 log4j2 配置）。

3. **自定义许可自己点名了触发条件。** 实测 `Plain Craft Launcher 2/LICENCE` 中《PCL 存储库合理使用指南》原文：

   > 二、重度使用 …… 其常见情形例如：
   > · 基于本存储库进行修改。
   > · 参考存储库中较大量的内容，以制作或重新实现 PCL 的部分实质功能（**例如启动或下载 Java 版 Minecraft，下载或管理 Mod 等**）。

   即：**"迁移 MC 启动 / Mod 下载管理"这件事本身，就是许可证定义的重度使用**。
   一旦触发，五项强制义务里有两项与 UMango 的现有定位直接对撞：
   - **B6 命名**：名称必须以 `Plain Craft Launcher (PCL)` 开头 → 与「UMango」这个项目名冲突；
   - **B7(b) 开源**：必须公开源代码 → 与项目「非强制开源」的定位冲突。

4. **反过来，"后端不含 MC 逻辑"这条红线正是让项目留在「轻度使用」的那块基石。**
   THIRD-PARTY-NOTICES.md 的判定链是对的：软件与 PCL「实质功能零重叠」→ 只需署名 + 不混淆命名。
   **所以这条红线不是架构洁癖，它承担着合规功能。**

5. **还有第三条约束，比前两条更硬，而且它是"红线"的真正来源**：
   调研期间 UMango 把 `UMango.Core` 改为多目标 **`net10.0;net10.0-windows`**（commit `2f900b1`），
   而 `PCL.Core.csproj` 是 **`net10.0-windows` + `UseWPF=true`**。
   那个多出来的 `net10.0` 支**被刻意用作编译期闸门**——一旦有 Windows 专用 API 混进 Core，它立刻编译失败。
   按该 commit 原文，UMango 的目标是"**Avalonia 跨平台重建**"。
   **所以"后端剥离 MC 逻辑"实质上等于"后端保持跨平台"**：PCL-CE 是 Windows 专用 WPF 应用，
   把它的任何一层整体搬进来，都会让 UMango 失去跨平台能力。
   这意味着 **"部分 MC 功能可以合法迁移"（Apache-2.0）这条缝隙，在工程上多半也走不通** ——
   `PCL.Core/Minecraft/` 那 6,800 行虽然许可证允许，但连 `PCL.Core` 这个程序集都进不了 `net10.0`。
   详见 §4.5。

**因此本清单给出的结论是**：两条需求**不能同时按字面满足**。可行的做法是把 PCL-CE 功能切成三类：

| 类别 | 处置 | 依据 |
|------|------|------|
| **非 MC 功能** | **应当迁移**（这是"完整迁移"的可满足部分） | 多数位于 `PCL.Core/`（Apache-2.0），无许可负担 |
| **MC 专属功能** | **迁移即触发重度使用义务**（改名 + 开源） | 《合理使用指南》原文点名的情形 |
| **MC 专属但算法通用**（如崩溃分析、NBT、Java 扫描） | **可迁移，但只有剥离 MC 语义后才有意义** —— 剥离后就已不是"迁移该功能"，而是"借其思路另做一个" | 需逐项判定，见第 3 节 |

**给项目所有者的决策**：要么接受 B6+B7（改名 + 开源）**并放弃跨平台**换取全量迁移，
要么维持"非 MC + 跨平台"红线和把"完整迁移"重新定义为"**PCL-CE 全部非 MC 功能**"。
第 4 节按此举出了尚未迁移的非 MC 功能与优先级 —— 那是**唯一既不违法、也不破坏跨平台**的迁移范围。

---

## 1. 摘要

### 1.1 计数

下表数字**由脚本对第 2 节全部功能表逐行统计得出**（不是估算）：

| 指标 | 数量 | 口径 |
|------|------|------|
| **本清单收录功能条目总数** | **174** | 第 2.A–2.K 共 11 张功能表的全部数据行 |
| 其中 **MC 专属**（列值「是」） | **81** | 见第 3 节完整清单 |
| 其中 **"部分 MC"**（列值「部分」：机制通用、数据/语义是 MC 的） | **3** | `DownloadRequestFactory`、`LoaderDownload`、`ModBase` |
| 其中 **非 MC**（列值「否」） | **90** | ← 这是"完整迁移"需求的真实作用域 |
| — 已完成 | **20** | 全部在非 MC 侧 |
| — 部分完成 | **30** | 28 条非 MC + 2 条「部分 MC」 |
| — 未做 | **42** | 41 条非 MC + 1 条「部分 MC」 |
| — 不适用 | **82** | 81 条 MC 专属 + 1 条废弃死代码 |

> **计数口径与自纠**：
> 1. 「不适用」**专指**"按「后端剥离 MC」红线不迁移"，不是"做完了"也不是"做不了"。
>    MC 专属的 81 条 + 「部分 MC」的 3 条 = **84 条在红线之外**，其状态一律记「不适用」或按其机制记为未做/部分。
> 2. **本表是修正后的版本。** 本报告初稿曾写"总数 137 / MC 78 / 非 MC 55"，但 **78+55=133≠137**，
>    内部即不自洽 —— 那是手工累加出的错。现已改为**脚本统计**（`grep` + 逐行解析 Markdown 表），
>    并复核 `20+30+42+82 = 174`、`81+3+90 = 174` 两式均成立。
>    （注：本表的**每一个位移都发生在调研期间**，均有 commit 号可查：并发任务把「已安装包列表」由部分→已完成（`39f5259`）、
>    「日志系统」由未做→已完成（`f2cc0b9`）、「日志查看页」由未做→已完成（`f2cc0b9`）；
>    主控收尾复核时补入了初稿漏掉的非 MC 功能「启动器自定义主页」（见 2.A），并在整合 Modules 子任务完整报告时
>    把「动画引擎」由已完成下调为部分、补入「加载器/任务编排框架」「任务栏进度聚合」两行。故总数由首版的 171 变为 **174**。）
> 3. **"完整迁移 PCL-CE 全部功能"的可迁移作用域 = 90 条非 MC 条目**，
>    其中已完成 20、部分 28、未做 41、废弃死代码 1。

### 1.2 一句话概览

- UMango 已完成的 **20** 项，**全部集中在"包管理业务主干 + 外壳"**上：
  六个包源适配器、平台门控、搜索/版本/依赖树、安装执行、任务与子项进度、终端桥、操作记录、
  下载引擎、设置体系、主题系统、动画系统、合规声明页。全部有源码证据（见第 2 节各行）。
- **未做的 41 项里，体积最大、最通用的三块**是：**多语言 i18n**、**启动器自更新**、**HTTP 客户端扩展层与持久化缓存**。
  （另有 P0-1 持久化缓存这一项已由他人接手，见 §6.2）
- **MC 专属的 81 项**（+3 条部分 MC）占据了 PCL-CE 代码量的约一半（第 3 节），
  但按红线与许可证**都不应迁入** —— 且第 4.5 节给出了比"红线"更硬的技术理由（`net10.0` 编译期闸门）。

### 1.3 一个曾必须先纠正的文档问题（调研期间已被修复）

调研开始时，UMango 自身的 `README.md`、`docs/ARCHITECTURE.md`、`docs/ROADMAP.md`、`docs/PROGRESS.md`
**描述的仍是已废弃的 Flutter + Rust + C++ 三层架构**（阶段1 "202 项测试"），
而实际技术路线早在 commit `d01f071`（"技术路线切换：废弃 Flutter 版，改用 .NET 10 + WPF 并采用 PCL-CE 代码"）
就已改为 **.NET 10 + WPF**。`docs/DESIGN_UI_DOTNET.md` 写明：

> 已废弃的 Flutter 版在 `$P/ui`、`$P/core`、`$P/native`，**不要碰**

**⚠️ 该问题在本次调研期间已被并发任务修复**（commit `602f003`"修正文档债：README 重写，四份过期文档加作废横幅"）：

- `README.md` 已重写为 .NET/WPF 现状；
- `docs/ARCHITECTURE.md`、`docs/PROGRESS.md`、`docs/ROADMAP.md`（及 `docs/SCREENSHOT_CHECK.md`）顶部已加 **"⚠️ 本文档已作废"** 横幅。

**本清单的 UMango 状态一律以 `src/UMango.Core/`、`src/UMango.App/`（活跃 .NET 栈）为准**，
`ui/`（49 文件 / 16,979 行 Dart）、`core/`（17 文件 / 7,001 行 Rust）、`native/`（8 文件 / 1,895 行 C++）视为**废弃代码，不计入完成度**。

> 一个值得记录的巧合：重写后的 `README.md` 第 6-10 行**独立得出了与本报告 §0 相同的合规结论** ——
> "后端完全不含 Minecraft 逻辑。这不只是架构取向 —— 它同时承担合规功能……软件与 PCL「实质功能零重叠」
> 是项目留在「轻度使用」的基石"。两条独立路径得到同一结论，互为旁证。

---

## 2. 对照表（核心）

**状态口径**：

- **已完成** —— 有对应实现文件与行号（下方备注直接给出）。
- **部分** —— 已有部分实现或形态不同，但关键能力缺失。
- **未做** —— UMango 中查无此能力（本节所有"未做"均经 `grep -ril` 全库确认关键字命中数为 0 或仅为误命中）。
- **不适用** —— MC 专属，按「后端剥离 MC 逻辑」红线不迁移。

### 2.A 外壳与导航（PCL-CE：`FormMain` + 顶层 `Pages/`）

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| 主窗口外壳（850×500、圆角6、留白10、标题栏48） | `Plain Craft Launcher 2/FormMain.xaml:1-263` | 否 | **已完成** | `src/UMango.App/MainWindow.xaml` + `Themes/PclTokens.xaml`；几何经 `docs/SCREENSHOT_CHECK.md` 逐像素校验 |
| 主页面路由（启动/下载/设置/工具） | `FormMain.xaml.cs:1361-1421`（`enum PageType`：Launch=0, Download=1, Setup=2, Tools=3） | 否 | **已完成** | `MainWindow.xaml:143-151` 五个 banner 选项卡；`ViewModels/ShellViewModel.cs`（138 行）。UMango 多出「进度」页，PCL-CE 把任务管理放在副页面 |
| 二级页面栈（压栈/返回/标题刷新） | `FormMain.xaml.cs:1652`（`PageChange`）、`:1474`（`PageNameGet`）、`:1476-1505`（副页面标题） | 否 | **已完成** | ✅ `src/UMango.Core/UI/Navigation/NavigationService.cs`（通用页栈：压栈/返回/定位/参数/标题）+ `src/UMango.App/Services/NavigationHost.cs`（栈→副标题栏/返回按钮）+ `src/UMango.App/Services/NavStackMapping.cs`（左栏↔栈的映射规则，纯逻辑可测）。**与原版四处差异**：① 原版靠"标题为空"判断是不是主页面 → 空标题的次级页无法存在，UMango 用显式 `IsSecondary`；② 原版 `PageBack` 只弹栈、不统一更新左侧高亮 → UMango 把"当前页+栈"收敛到一处，订阅者拿到的快照永远自洽；③ 原版 `PageStackData` 塞一堆 nullable 字段（`additional.Value`）→ UMango 用 `object?` 参数 + 强类型取值；④ 原版栈与 Minecraft 页面类型强耦合 → UMango 的页栈与内容无关，页面标识用 `{选项卡}/{菜单键}` 两段式（返回时同时还原选项卡与左栏高亮） |
| banner 选项卡样式 | `FormMain.xaml`（`BannerTab` 样式） | 否 | **已完成** | `Themes/Styles.xaml` 的 `BannerTab` |
| 左栏列表项与分组标题（项高36、分组字号12/透明度0.6） | `Pages/*/Page*Left.xaml`（如 `PageSetupLeft.xaml:10-148`） | 否 | **部分** | UMango 左栏改用 `ListBox`（`MainWindow.xaml:183`）+ 「分类/包源」两段表格，**形态与 PCL-CE 的 `MyListItem` RadioBox 列表不同**；逐像素校验只覆盖了尺寸与配色 |
| 三大金刚键（最小化/最大化/关闭） | `FormMain.xaml`（28×28，右边距12、间距32） | 否 | **已完成** | `MainWindow.xaml:157-162`，`Click="BtnMin_Click"` 等 |
| 任务管理页面 | `FormMain.xaml.cs:1391`（`PageType.TaskManager = 6`） | 否 | **已完成** | `Views/ProgressView.xaml`（620 行）+ `ViewModels/ProgressViewModel.cs`（334 行）；能力**强于** PCL-CE（子项进度 + 内联终端） |
| **启动器自定义主页**（远程 XAML 渲染 + 文件热补丁 + 内置小提示） | `Pages/PageLaunch/PageLaunchRight.xaml.cs:461`（`LoadContent`）、`:534`（`XamlEventSanitizer`）、`:559-637-760-845`（`FileSystemWatcher` 热补丁链）、`:281`/`:325`（`GetRandomHint`/`_LoadEmbeddedHints`）；XAML `PageLaunchRight.xaml:7,8,19`（**898 行**） | **否** | ✅ **已完成**（安全再设计，见 P2-21/P2-22） | ⚠️ **本行是补充项**：功能普查初稿把它漏掉了（它挂在 `PageLaunch/` 目录下，容易被误当 MC 页面）。实测它是**非 MC** 的启动器主页机制 —— 从网络拉取 XAML 当主页渲染、用 `FileSystemWatcher` 做热补丁、远程 XAML 先过 `XamlEventSanitizer`。内置小提示的文案是 MC 冷知识，但**机制与 MC 无关**。
**⚠️ 若要迁移，须重新设计而非照搬**：在进程内加载并执行**远程 XAML** 是任意代码执行面，PCL-CE 用 `XamlEventSanitizer` 缓解，但这不是一个应当被复制的危险模式。 |
| 实时日志查看器（自动滚屏/字体缩放/导出/清空/结束进程/导出堆栈） | `Pages/PageLogLeft.xaml.cs:95,142,211`、`Pages/PageLogRight.xaml.cs:27,101,156,192`（共 521 行） | 否（**数据源是 MC 进程**） | **已完成** | ⚠️ **判定在调研期间由「未做」升为「已完成」**（commit `f2cc0b9`）。实测 `src/UMango.App/Views/LogView.xaml` + `.cs` + `ViewModels/LogViewModel.cs`（共 **404 行**）：级别筛选、刷新、清空显示、打开日志目录、**导出诊断包**；内存环形缓冲 3000 条 + 文件按日期分文件保留 7 天（见 `LogView.xaml:66`）。UMango 的差异：**数据源是自己的日志**，而非 MC 游戏进程 |
| 实例选择副页面 | `FormMain.xaml.cs:1386`（`InstanceSelect = 5`） | 是 | **不适用** | 选择 MC 实例 |
| 主页新闻（Minecraft 官方资讯流） | `Pages/PageHomepage/PageHomepageNewsView.xaml.cs` + `PCL.Core/Model/Homepage/News/`（3 文件 58 行）+ `PCL.Core/ViewModel/Homepage/NewsViewModel.cs`（114 行） | **是** | **不适用** | ⚠️ **本表首轮曾把它判为"非 MC"并说数据源是"PCL 官网 API"，两处都错**。实测 `NewsViewModel.cs:16`：`BaseApiUrl = "https://net-secondary.web.minecraft-services.net/api/v1.0/zh-cn/search"`，并在 `:20-22` 用 `minecraft.net` / `minecraft-services.net` / `microsoft.com` 做链接白名单 —— 它拉的是 **Minecraft 官方资讯**，**是 MC 专属** |
| 音乐播放器 | `Modules/ModMusic.cs:11`（472 行，NAudio `WaveOutEvent`） | 否 | **已完成** | **原版在受限许可目录 → 只借鉴语义、独立重写**。Core 层 `src/UMango.Core/Media/Music/`：`MusicLibrary.cs`（目录递归扫描 + 排除 `.ini/.jpg/.txt/.cfg/.lrc/.db/.png` + Fisher–Yates 洗牌 + `preventFirst` 防立刻重播 + 播放失败即从库中剔除）、`MusicPlaybackController.cs`（播放/暂停/继续/停止/重播/下一首的状态机 + 进度 + 音量 + 失败分类 `MusicFailureClassifier`：设备占用/设备变更/参数不支持/格式不支持/文件不存在/播放失败）。App 层 `src/UMango.App/Media/Music/NaudioMusicPlayer.cs`（NAudio 2.4.0，**唯一碰音频设备的一层**）+ `ViewModels/MusicViewModel.cs` + `Views/MusicView.xaml`（工具页左栏「音乐」二级页）。**NAudio 选型理由**：MIT 许可（与 PCL-CE 同库同许可），选 2.4.0 而非 3.x（3.x 拆包），FLAC 需外置 `NAudio.Flac`。**四处实质差异**：① 播放失败按 PCL 的分类给出**可读提示**（PCL 弹窗）；② 音乐目录**可配置**（`music.directory`），留空则用程序目录下 `Musics`；③ 音量/随机开关走**统一设置服务**（`music.volume`/`music.shuffle`）；④ 状态机在 Core 层 → 可在 macOS 上单测，App 层只做胶水。45 个 Core 测试 + 9 个 App 测试，注入矩阵 6 项（含 2 项最初不红 → 补「曲目数返回值」与「随机影响待播队列顺序」两处可观测缝）。 |
| 视频/动态背景 | `Modules/ModVideoBack.cs:3`（172 行） | 否 | **未做** | 依赖 WPF MediaElement + 游戏中状态判定（`IsGaming:7`） |
| 内嵌 WebServer | `Modules/ModWebServer.cs:10`（345 行） | 部分 | **已完成**（底层能力） | `StartWebServer:20` / `NaidCallbackServer:112`；是个通用 HTTP 服务器，但 PCL-CE 用它接 **MC 登录 OAuth 回调** → UMango 不做 MC 登录，但**底层能力**（通用本地 HTTP 服务端）已落地：`src/UMango.Core/Network/Http/Server/HttpServer.cs`（`HttpListener` 封装：路由注册 / 处理器异常统一转 500 / 多地址绑定与跳过 / 端口占用降级）+ `HttpRouteResponse.cs`（Text/Json/Redirect/各状态码便捷方法） |
| 全局提示/Hint 服务 | `Modules/UI/HintService.cs:21`（`Hint`）、`:99`（`HideAll`）+ `PCL.Core/UI/HintWrapper.cs:16` | 否 | **已完成** | **`HintWrapper.cs`（Apache-2.0）已逐字采用**进 `src/UMango.Core/Pcl/UI/HintWrapper.cs`；`App/Services/PclUiBridge.cs` 把 `OnShow` 汇入 `NotificationService`。**`HintService` 本体已重新实现**进 `src/UMango.Core/Notifications/HintService.cs`（PCL 原版在受限许可目录，只借鉴语义）：队列 / 去重 / `Tick` 出队 / `HideAll` / 按文案长度算停留时长 全部落地，并修掉原版四个真实缺陷（空白提示会显示成空浮条、查重口径只查等待队列不查已显示、停留时长可能被算成 0、淘汰语义靠返回 null 表达）。界面侧由 `App/Services/HintPump.cs` 翻译队列动作为「显示 / 撤掉」事件，`MainWindow.xaml.cs` 用 40ms 心跳驱动（对应 PCL 的每帧 Tick）渲染成右下角浮条。**策略单一来源**：原 App 层的 `ToastPolicy`（去重 + 上限 4）已删除 —— 规则只留在 Core 的 `HintService`（上限 5，与 PCL 一致），避免两处规则走偏。 |
| 系统 Toast 通知（WinRT） | `PCL.Core/UI/ToastNotification.cs:12`（`SendToast`）、`:39`（模板） | 否 | **已完成** | **`ToastNotification.cs` + `Utils/WinRT/**`（10 文件，全部 Apache-2.0）已逐字采用**进 `src/UMango.Core/UI/ToastNotification.cs`、`src/UMango.Core/Utils/WinRT/`，含 WinRT 裸 vtable 互操作。`Utils/OS/AumidHelper.cs` 改 Aumid 为 `UMango.UMango`、主色 `FF1370F3`。`App/Services/ToastBridge.cs` 订阅 `TaskCenter.Changed`，对**进入终态**（Success/Failed）的任务去重推送，Failed 附 `StateMessage` 原因。非 Windows 在发送处短路。**注意**：`UMango.Core.csproj` 按 TFM 条件编译把这三个目录排除出 `net10.0` 支，保持 Core 的跨平台可编译性 |
| 主题管理（明暗 + 色相） | `Modules/UI/Theme/ThemeManager.cs:9`（`ThemeRefresh:22`、`ThemeRefreshMain:31`） | 否 | **已完成** | `src/UMango.App/Themes/ThemeManager.cs:34`（`Apply:113`、`ToggleDark:167`、`ThemeChanged:68`），4 套完整字典 `Tokens.{Light,Dark}.{Pcl,Fluent10}.xaml`，**能力更强**（PCL-CE 是单套 + 明暗） |
| 多语言 i18n | `PCL.Core/App/Localization/Lang.cs` + `Languages/zh-CN.xaml`（**3,583 行**）×4 种实语言（zh-CN/zh-TW/en-US/en-GB）+ 3 个 12 行空壳 | 否 | ****已完成（本地化机制 + zh-CN/en-US 双语，392/391 键）** | UMango 版：`src/UMango.Core/Localization/`（Models/Service/ITextProvider）+ `src/UMango.App/Localization/`（AppLocalization + ViewText + Languages/zh-CN.xaml 392 键 / en-US.xaml 391 键）。资源字典分层（兜底 zh-CN 恒加载 + 叠加层只写差异键）、UI 语言与展示格式区域性分离、XAML 静态文案 + ViewModel + code-behind + ToolTip 全覆盖 |（纯 XAML 资源字典），**文本内容高度启动器专用**；`src/` 下 `Language` 关键字仅 1 处误命中（GitHub 搜索语法注释） |
| 动画引擎 | `PCL.Core/UI/Animation/`（`UI/` 共 129 文件 9,835 行，动画为主体：`Core/` 帧·关键帧·组合·时钟·值处理器，`Easings/` 39 个缓动类） | 否 | **已完成** | UMango `src/UMango.Core/Animation/`（**30+ 文件 / 3,000+ 行**）：`AnimationPlanner`、`Easing`（39 缓动枚举化）+ `Easing.Pcl.cs`（PCL 风格**档位缓动**类族：`FluentOutEasing`/`BackEaseWithPowerEasing` + `EasePower` 四档 Weak/Middle/Strong/ExtraStrong）、`AnimationSpec`、`AnimationSettings` + `App/Animation/AnimationPlayer.cs`；**帧动画/关键帧/顺序并行组/时钟抽象/`IAnimatable`/值处理器（含算术运算）/绝对相对值/`ActionAnimation`/复合缓动/按名取消**均已迁入；`UIAccessProvider`（`Animation/UIAccess/`）由 WPF 与 Avalonia 两壳共用 |
| 单实例运行 | `PCL.Core/App/Essentials/SingleInstanceService.cs` | 否 | **已完成** | 实现位置：Core/App/SingleInstance/SingleInstanceGuard.cs（命名互斥 Global\umango-single-instance-<hash> + 命名管道 activate）+ App/Services/SingleInstanceService.cs；App.xaml.cs 接线，OnExit 释放。未采用 PCL 原版的「独占打开文件 + FileShare.Read」——该组合只在 Windows 上互斥，Unix 上会静默失效（测试抓到）。 |0  实现位置：Core/App/SingleInstance/SingleInstanceGuard.cs（命名互斥 + 命名管道激活）+ App/Services/SingleInstanceService.cs；App.xaml.cs 接线，OnExit 释放 |
| 通知/对话框系统 | `PCL.Core/UI/MsgBoxWrapper.cs`、`SystemDialogs.cs`、`HintWrapper.cs` | 否 | **已完成** | **`MsgBoxWrapper.cs`（Apache-2.0）已逐字采用**进 `src/UMango.Core/Pcl/UI/MsgBoxWrapper.cs`（含 `MsgBoxButtonInfo`/`MsgBoxTheme`/`MsgBoxHandler` 全套契约，`Lang.Text` 由等价桥提供）；`App/Views/PclDialog.xaml` 是主题化对话框（圆角 4、随明暗主题、支持任意按钮集合 + `OnClick` 回调、后台线程编组 + 阻塞语义）。`SystemDialogs.cs` 未迁 |

### 2.B PageLaunch（启动与登录）— 全部 MC 专属

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| MC 启动主页（实例选择 + 启动按钮 + 版本列表） | `Pages/PageLaunch/PageLaunchLeft.xaml.cs:1035` | 是 | **不适用** | `BtnLaunch`(高54)、`BtnInstance`、`BtnInstanceSetup` |
| 启动状态面板（当前步骤/下载库/登录方式/进度/速度） | `PageLaunchLeft.xaml:112-125` | 是 | **不适用** | `Launch.Status.*` 资源键 |
| 微软账号登录（OAuth 设备码） | `Pages/PageLaunch/PageLoginMs.xaml.cs:101` + `PCL.Core/Minecraft/Profile/Authentication/MicrosoftProvider.cs` | 是 | **不适用** | `IdentityModel/` 22 文件 1,613 行 |
| 离线登录 | `Pages/PageLaunch/PageLoginOffline.xaml.cs:92` | 是 | **不适用** | |
| 第三方验证登录（Authlib / Yggdrasil） | `Pages/PageLaunch/PageLoginAuth.xaml.cs:410` + `AuthlibProvider.cs` + `YggdrasilConnectProvider.cs` | 是 | **不适用** | |
| 账号档案管理（多档案、切换、失效） | `Pages/PageLaunch/PageLoginProfile.xaml.cs:198` + `PCL.Core/Minecraft/Profile/ProfileService.cs`、`ProfileManagement.cs` | 是 | **不适用** | `Profile/` 21 文件 1,563 行 |
| 皮肤查看与更换 | `Pages/PageLaunch/MySkin.xaml.cs:503`、`PageLoginProfileSkin.xaml.cs:168` + `Modules/Minecraft/ModSkin.cs` | 是 | **不适用** | |
| 登录消息/公告浮层 | `Pages/PageLaunch/MyMsgLogin.xaml.cs:281` | 是 | **不适用** | |
| 启动按钮的多态动作（安装/更新/启动） | `Pages/PageLaunch/PageLaunchLeft.xaml.cs:26`（`enum LaunchButtonAction`） | 是 | **不适用** | UMango 有同类多态按钮（安装/卸载/升级），但语义是包管理 |
| 启动前正版劝导（`McLaunchPrecheck`） | `Modules/Minecraft/ModLaunch.cs:34` | 是 | **不适用** | 该函数是许可证 B8 义务的实现载体 |

### 2.C PageDownload（下载中心）— 18 个副页面

`PageSubType` 枚举见 `FormMain.xaml.cs:1425-1446`；左栏菜单见 `PageDownloadLeft.xaml:11-277`；
**子页映射的权威依据**：`PageDownloadLeft.xaml.cs:238` 的 `PageGet(FormMain.PageSubType)` switch（case 自 `:244` 起 17 个）。

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| 游戏本体安装（版本列表 + 下载） | `Pages/PageDownload/PageDownloadInstall.xaml.cs` + `ModDownload.cs:23`（`DlClientJarGet`） | 是 | **不适用** | |
| Mod 搜索与下载 | `Pages/PageDownload/PageDownloadMod.xaml.cs` + `Comp/PageDownloadMod.xaml.cs` | 是 | **不适用** | 对应 UMango 的「搜索+卡片+版本列表」是**通用包搜索**，见 D01 |
| 整合包下载与安装 | `PageDownloadPack.xaml.cs` + `Modules/Minecraft/ModModpack.cs`（1,738 行） | 是 | **不适用** | |
| 数据包下载 | `PageDownloadDataPack.xaml.cs` | 是 | **不适用** | |
| 资源包下载 | `PageDownloadResourcePack.xaml.cs` | 是 | **不适用** | |
| 光影下载 | `PageDownloadShader.xaml.cs` | 是 | **不适用** | |
| 世界/地图下载 | `PageDownloadWorld.xaml.cs` | 是 | **不适用** | |
| 社区资源收藏夹 | `PageDownloadCompFavorites.xaml.cs` | 是 | **不适用** | |
| OptiFine 安装 | `PageDownloadOptiFine.xaml.cs` | 是 | **不适用** | |
| Forge 安装 | `PageDownloadForge.xaml.cs` | 是 | **不适用** | |
| NeoForge 安装 | `PageDownloadNeoForge.xaml.cs` | 是 | **不适用** | |
| Cleanroom 安装 | `PageDownloadCleanroom.xaml.cs` | 是 | **不适用** | |
| Fabric 安装 | `PageDownloadFabric.xaml.cs` | 是 | **不适用** | |
| LegacyFabric 安装 | `PageDownloadLegacyFabric.xaml.cs` | 是 | **不适用** | |
| LiteLoader 安装 | `PageDownloadLiteLoader.xaml.cs` | 是 | **不适用** | |
| LabyMod 安装 | `PageDownloadLabyMod.xaml.cs` | 是 | **不适用** | |
| 资源工程详情页（描述/文件列表/依赖/图集） | `Pages/PageDownload/Comp/PageDownloadCompDetail.xaml.cs`、`MyCompItem.xaml.cs` | 是 | **不适用** | 交互形态（卡片 + 详情页）UMango 已有，见 D01/C01 |
| 社区资源元数据与依赖解析 | `Modules/Minecraft/ModComp.cs`（3,823 行）、`ModCompDependency.cs` | 是 | **不适用** | 加载 `mcmod.buf` 索引，规模居全库第二 |
| Modrinth / CurseForge 双源 API | `PCL.Core/Minecraft/ResourceProject/`（15 文件 498 行） | 是 | **不适用** | |
| 整合包安装核心库 | `Pages/PageDownload/ModDownloadLib.cs`（**4,578 行**，全库最大文件） | 是 | **不适用** | 模块级代码混在页面目录 |

### 2.D PageInstance（实例管理）— 12 个副页面

`PageSubType.Version*` 见 `FormMain.xaml.cs:1462-1473`；左栏见 `PageInstanceLeft.xaml:8-166`；
**权威依据**：`PageInstanceLeft.xaml.cs:201` 的 `PageGet` switch（12 个 case 位于 `:207-:273`）；
存档子页见 `PageInstanceSaves/PageInstanceSavesLeft.xaml.cs:53`。

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| 实例总览（信息卡 + 快速动作） | `Pages/PageInstance/PageInstanceOverall.xaml.cs:843` | 是 | **不适用** | |
| 实例设置（内存/窗口/参数/Java） | `PageInstanceSetup.xaml.cs:1091` | 是 | **不适用** | |
| 实例导出/整合包打包 | `PageInstanceExport.xaml.cs:1226` | 是 | **不适用** | |
| 存档管理与存档详情 | `PageInstanceSaves.xaml.cs:573`、`Saves/PageInstanceSavesInfo.xaml.cs:356` | 是 | **不适用** | |
| 数据包管理（含版本筛选/排序） | `PageInstanceSaves/PageInstanceSavesDatapack.xaml.cs:1698`、`FilterType:842`、`SortMethod:907` | 是 | **不适用** | |
| 截图管理 | `PageInstanceScreenshot.xaml.cs:340` | 是 | **不适用** | 图片浏览机制通用，但目录/语义是 MC 的 |
| Mod 管理（启用/禁用/删除/搜索） | `PageInstanceCompResource.xaml.cs:2961`、`MyLocalModItem.xaml.cs:886` | 是 | **不适用** | |
| 已禁用 Mod 列表 | `PageInstanceModDisabled.xaml.cs:40` | 是 | **不适用** | |
| 资源包管理 | `PageInstanceCompResource.xaml.cs`（同上，`FilterType:1588`） | 是 | **不适用** | |
| 光影管理 | 同上 | 是 | **不适用** | |
| 原理图（Schematic）管理 | `FormMain.xaml.cs:1472`（`VersionSchematic = 9`） | 是 | **不适用** | |
| 服务器列表与服务器卡片 | `PageInstanceServer.xaml.cs:497`、`ServerCard.xaml.cs:238` | 是 | **不适用** | |
| 实例安装/重装（含 Loader 选择） | `PageInstanceInstall.xaml.cs:2553`、`InstallAction:18` | 是 | **不适用** | 安装编排机制与 UMango `InstallExecutor` 同类，但语义是 MC |
| 实例列表与卡片 | `Modules/Minecraft/ModInstanceList.cs:636`、`McInstanceCardType.cs`、`McInstance.cs:1062` | 是 | **不适用** | |

### 2.E PageSetup（设置）— 11 个子页

`PageSubType.Setup*` 见 `FormMain.xaml.cs:1448-1460`；左栏四分类（游戏/工具/启动器/关于）见 `PageSetupLeft.xaml:10-148`；
**权威依据**：`PageSetupLeft.xaml.cs:253` 的 `PageGet` switch（11 个子页）。

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| 游戏设置（启动参数/JVM/内存/窗口） | `Pages/PageSetup/PageSetupLaunch.xaml.cs:641` + `PageSetupLaunch.xaml`（326 行） | 是 | **不适用** | |
| Java 管理（扫描/选择/下载/校验） | `PageSetupJava.xaml.cs:224` + `Modules/Minecraft/ModJava.cs:15`（`JavaSelect:34`、`JavaDownloadConfirm:343`）+ `PCL.Core/Minecraft/Java/`（21 文件 986 行） | 是 | **不适用** | 含注册表扫描 `RegistryJavaScanner.cs`、Microsoft Store 扫描、PE 头解析 |
| 游戏管理（下载源/下载版本/线程数/限速/自动选实例/忽略 Quilt/自动装依赖/MC 中文名/剪贴板识别） | `PageSetupGameManage.xaml.cs:20,50,78,100,145`；`PageSetupGameManage.xaml:28,36,44,50,66,140,143,165,168` | 是（**但含通用参数**） | **不适用** | 备注：其中**下载源/线程数/限速**（`.xaml:28,44,50`）形式上与 MC 无关，但它们是**为 MC 资源下载服务的配置项**，脱离 MC 无意义 —— 故整条仍判 MC 专属。UMango 的对应能力是 `DownloadEngineRegistry`（按协议选引擎），**不经过这类设置项** |
| GameLink 联机设置（用户名/中继服务器/中继类型/协议优先/延迟优先/NAT 端口猜测/IPv6 + 网络测试面板） | `PageSetupGameLink.xaml.cs:27,42,100,113,120,142`；`PageSetupGameLink.xaml:39,45,54,59,65,71,103` | 是（**配置项本身通用**） | **不适用** | 备注：中继/协议/NAT/公网 IPv6 都是通用网络参数，但**服务对象是 MC 联机大厅**（见 2.F） |
| **界面设置/自定义** | `PageSetupUI.xaml.cs:957` + `PageSetupUI.xaml`（482 行） | **否** | **部分** | UMango `Views/SettingsView.xaml`(512) + `.xaml.cs`(390) + `ViewModels/SettingsViewModel.cs`(237)；已覆盖主题/动画/调试/行为，**缺** PCL-CE 的界面自定义项（背景图、圆角开关、字体等） |
| **启动器语言** | `PageSetupLauncherLanguage.xaml.cs:224` | **否** | 依赖 i18n（i18n 已完成，此项待启动器本体迁移后再定） | 见 2.A「多语言」 |
| **启动器杂项** | `PageSetupLauncherMisc.xaml.cs:322` | **否** | **部分** | ⚠️ 已逐项核对 9 组设置：**UMango 该有的 1 组已迁**（网络代理 → `src/UMango.Core/Network/ProxyManager.cs` + App 设置项），其余 8 组各有明确不迁理由（公告方案→已由 P3-1 覆盖 / 硬件加速→WPF 专属 / 动画 FPS→已有 `ui.animation.*` / 遥测→不做 / 主题→已完成 / 下载→UMango 有自己的下载器 / 联机→MC 专属 / 启动→MC 专属 / 界面→已完成） |
| **关于页（含第三方许可清单）** | `PageSetupAbout.xaml.cs:203` + `PageSetupAbout.xaml`（505 行） | **否** | **已完成** | UMango `Views/ToolsView.xaml:217-274`「合规与许可」+「第三方组件」+「关于」；且 UMango 的声明比 PCL-CE 更完整（见 `THIRD-PARTY-NOTICES.md`） |
| **启动器更新** | `PageSetupUpdate.xaml.cs:284` + `Modules/Updates/UpdateManager.cs:11`（`IsCurrentVersionBeta:26`、`GetVersionStatus:36`） | **否** | **已完成** | UMango 已实现完整自更新闭环：`Core/App/Essentials/UpdateHelper.cs`（PCL Apache-2.0 逐行移植）、`UpdateChecker.cs`（GitHub Releases Feed + SemVer 严格解析 + 预发布双通道判定）、`UpdateCommandLine.cs`（助手进程协议）、`App/Services/UpdateService.cs`、`ViewModels/UpdateViewModel.cs` + 设置页「检查更新」卡 + `App.Program.Main` 的助手分支。差异：UMango 只有 GitHub Releases 单源（PCL 的 Minio/MirrorChyan/随机/包装多源架构见本表第 305 行，未迁移） |
| **反馈** | `PageSetupFeedback.xaml.cs:195` + `TagId:12` | **否** | ✅ **已完成**（commit 见下） | `src/UMango.Core/Feedback/`（`FeedbackModels.cs` / `FeedbackCategory.cs` / `FeedbackService.cs`）+ `src/UMango.App/Views/FeedbackView.xaml` + `ViewModels/FeedbackViewModel.cs`。**与原版的差异**：① 分类从**硬编码的数字 label id** 改为**按 label 名字匹配**（大小写不敏感，且可注册自定义映射）—— 原版那些 id 只在 PCL 自己仓库里存在，换仓库即失效；② 源**可配置**（`feedback.source` / `feedback.submitUrl` 两个设置项），留空则不访问任何地址；③ 抓取失败只记状态提示，不抛异常；④ 反馈内容**只读展示**，其中的任何文本都不会被当作命令执行 |
| **日志设置/查看** | `PageSetupLog.xaml.cs:163` | **否** | **已完成** | 实现位置：App/Views/LogView.xaml（等级筛选 4 档、刷新、清空显示、打开日志目录、导出诊断包）+ ViewModels/LogViewModel.cs + Core/Logging/（FileLogSink/MemoryLogSink）。与 PCL 的 PageSetupLog 功能对齐。 |

### 2.F PageTools（工具）

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| 联机大厅（建房/加入/房间码/玩家列表） | `Pages/PageTools/PageToolsGameLink.xaml.cs`（`Subpages:987`）+ `Modules/ModLink.cs:1-398` + `PCL.Core/Link/`（59 文件 5,018 行） | 是 | **不适用** | `ModLink.cs` 导入 `PCL.Core.Minecraft.Profile`、`Link.McPing`；`ScaffoldingFactory` 用 MC 大厅码与 EasyTier。**基础设施（EasyTier P2P、TCP 转发 `IO/Net/SocketForward/`、Scaffolding 协议）本身通用，但语义是 MC 联机** |
| 工具/测试页 | `Pages/PageTools/PageToolsTest.xaml.cs` | 否 | **部分** | UMango `Views/ToolsView.xaml`（317 行）+ `ToolsView.xaml.cs`（493 行）提供系统诊断、CLI 探测明细、可复制诊断报告 —— **实质是 PCL-CE「测试页」的同类能力且更完善**；但 PCL-CE 该页另有快捷工具入口未迁移 |

### 2.G Modules/Base、Event、UI、Updates（业务基础设施）

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| 上游基础设施与转换器（`ModBase.cs` **3,759 行**） | `Modules/Base/ModBase.cs` | 否（**大部分通用**） | **部分** | UMango 未移植 `ModBase`，其中通用的 `SafeList`(2250)、校验、路径工具由自研 `src/UMango.Core/Utils/` 部分覆盖。实测其中**零 MC 依赖的通用件**包括：文本扩展方法集 `:1624-2020`（`BeforeFirst/AfterLast/Between/RegexReplace` 等 14+ 个，是全项目字符串解析基座）、模糊搜索引擎 `SearchEntry<T>:2108`/`SearchSource:2135`/`Search<T>:2163`、文件校验器 `FileChecker:1198`（`:1232 Check`）、哈希族 `GetFileMD5:1054`…`GetAuthSHA1:1182`、压缩与目录递归 `ExtractFile:1372`/`CopyDirectory:1499`/`MoveDirectory:1540`、数学工具 `:517-615`（RadixConvert/MathBezier/MathClamp）、颜色模型 `MyColor:126`（9 构造 + 8 隐式转换）、线程与 UI 调度 `RunInNewThread:2614`/`RunInUi:2641`、系统集成 `OpenWebsite:2818`/`OpenExplorer:2848`。⚠️ **该文件在自定义许可目录下**，只能借鉴设计、不能复制代码；且含 Windows 特有件（见 4.5） |
| 加载器/任务编排框架 | `Modules/Base/ModLoader.cs`（**1,032 行**：`LoaderBase:182`、状态机 `State:281`、加权进度 `:332`/`:366`、父子关系 `RealParent:244`/`InitParent:386`、`Abort:415`、`WaitForExit:420`/`:453`、超时重载 `:508`；`LoaderTask:493`、`LoaderCombo:707`/`:1005`） | 否 | **部分** | UMango 有自研的 `UMango.Core/App/Tasks/`（8 文件 228 行，采用自 PCL.Core），能表达**任务 + 子项 + 加权进度**；但**没有** PCL-CE 这套 Loader 的 `realParent` 父子树、`Abort` 级联、`WaitForExit`/超时、以及 **`LoaderFolderRun:122` 目录变更驱动的缓存失效**（`:164` 递归取最大写入时间）。⚠️ 在**自定义许可目录**下，只能借鉴设计 |
| 任务栏进度聚合（Windows） | `ModLoader.cs:30`（`LoaderTaskbarAdd`）、`:38`、`:92`；`TaskbarItemProgressState:25` | 否 | **已完成** | 实现位置：Core/App/Tasks/TaskProgressAggregator.cs（Summarize 聚合 + MapToProgress 纯映射 + TaskProgressWatcher 订阅 CollectionChanged/Changed/PropertyChanged）+ App/Services/TaskbarProgressBridge.cs（WPF TaskbarItemInfo，回调已做 Dispatcher 编组）；MainWindow.xaml 挂 TaskbarItemInfo，Closed 时释放。 |
| 启动初始化流程与配置观察者 | `Modules/Base/ModSetup.cs`（`ApplyAll:109` + **实测 31 个** `public static void` 应用方法，如 `ToolDownloadThread:196`、`SystemHttpProxy:607`、`VersionRamType:654`） | 部分 | **部分** | UMango `Services/AppServices.cs:32`（`Initialize:69`）+ `SettingsService.Changed` 事件做等价的事；PCL-CE 的 31 个观察者里下载线程/限速/主题/字体/代理是通用的，实例内存方案/登录方案是 MC 专属 |
| 窗口动画引擎（补间/缓动/分组） | `Modules/Base/ModAnimation.cs`（**1,517 行**：`AniStart:28`、`AniTimer:104`、20+ 个 `Aa*` 补间工厂 `:642-1112`、14 种缓动 `:1233-1452`；`AniData` 结构 `:488`） | 否 | **已完成** | ① **PCL 原版已逐字采用**：`src/UMango.App/Pcl/ModAnimation.cs`（1,537 行，含 5 处有据可查的改动），PCL 主题下走的正是这套 API；② **可跨框架的等价层**（借鉴设计、独立重写，因原文件在**受限许可目录**）：`src/UMango.Core/Animation/TweenFactory.cs`（补间工厂：`AaOpacity`/`AaTranslate`/`AaScale`/`AaRotate`/`AaColor`/`AaWidth`/`AaHeight`/`AaTextAppear` 等）、`TweenPlanner.cs`（编排）、`TweenChannelAnimation.cs`（**同通道多段并行叠加**，PCL 的 `AaTranslateY(+5)` 与 `AaTranslateY(+11)` 就是这么叠的）、`DelayedAnimation.cs`（延迟）、`OffsetChainAnimation.cs`（多段偏移链）、`StaggerPlanner.cs`（**子项级联**：PCL `MyPageLeft`/`MyPageRight` 的 25ms 步进与 15ms 退场步进、`max(15-序号,7)×2` 递减延迟）、`AnimTreeCollector.cs`（**控件树收集规则**，PCL `_GetAllAnimControls` 的三分支语义）、`PclTweenPresets.cs`（PCL 原版数值）、`Easing.Pcl.cs`（14 种档位缓动）；③ **App 侧宿主**：`App/Animation/WpfTweenPlayer.cs`、`WpfTweenBinder.cs`、`WpfAnimPolicy.cs`、`WpfStaggerHost.cs`；④ **已接真机路径**：`MainWindow.xaml.cs` 的 `PlayContentAnimation` 走「整页（PCL 原版 ModAnimation API）+ 子项级联（StaggerPlanner）」两层叠加 |
| 位图/图像处理（`MyBitmap.cs`） | `Modules/Base/MyBitmap.cs` | 否 | **已完成**（等价实现） | `src/UMango.Core/UI/Media/ImageProbe.cs` 字节级探测层（commit 406bee2）。⚠️ **未整体迁受限层的 `MyBitmap.cs`**（它是 MC 皮肤渲染，与包管理器无关） |
| 自定义事件总线 | `Modules/Event/`（6 文件 741 行：`CustomEventService.cs`、`EventType.cs`、`XamlEventSanitizer.cs`） | 否 | **已完成** | ⚠️ `Modules/Event` 是 CE 新增能力（见既有分析报告 §2.2）。UMango 用 MVVM 属性通知，无事件总线  实现位置：Core/Events/ |
| 主题管理器（业务侧） | `Modules/UI/Theme/ThemeManager.cs:9`（**131 行**） | 否 | **已完成** | 见 2.A「主题管理」。⚠️ 本次首轮统计曾把该文件漏算（用了 `-maxdepth 1`），已修正：`Modules/UI` 实为 **3 文件 / 244 行** |
| 提示服务 | `Modules/UI/HintService.cs:21` | 否 | **已完成** | `src/UMango.Core/Notifications/HintService.cs` —— 队列 / 去重 / `Tick` 出队 / `HideAll` 全部迁入（重新实现，见 2.A「全局提示/Hint 服务」）；`App/Services/HintPump.cs` 负责把队列动作翻译成界面浮条事件，`App/ToastPolicy.cs` 及其 7 个测试已删除（规则统一到 Core） |
| 启动器更新（多通道：Minio/MirrorChyan/随机/包装） | `Modules/Updates/`（11 文件 1,020 行：`UpdateManager.cs:11`、`UpdatesMinioModel.cs`、`UpdatesMirrorChyanModel.cs`、`UpdatesRandomModel.cs`、`UpdatesWrapperModel.cs`、`IUpdateSource.cs`） | 否 | **已完成**（多通道抽象已迁） | `src/UMango.Core/App/Essentials/UpdateChannels.cs`（commit 2153c75）—— 多源抽象 + 包装降级已迁；⚠️ 仅**借鉴架构**，未复制受限许可目录的代码 |
| 公告/Announcement | `Modules/Updates/AnnouncementService.cs`、`VersionAnnouncementDataModel.cs` | 否 | **已完成** | ✅ `src/UMango.Core/Announcements/`（抓取 / 已读记录 / **动作白名单**）+ App 层 `AnnouncementHost`。**与原版差异**：源可配置（PCL 绑定自家官网 API）/ 动作走白名单而非黑名单 / 抓取失败只记日志不打断启动 / 设置键在 Core 描述符里注册（`announcement.showed` / `announcement.source`） |

### 2.H Modules/Network（网络与下载）

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| **多线程下载管理器（分片/并发/限速/进度聚合）** | `Modules/Network/Management/NetManager.cs`（`ThreadCount:44`、`Speed:35`、`FileRemain:12`、`DownloadDone:21`） | **否** | **部分** | 模块总计 12 文件 1,137 行。UMango `src/UMango.Core/Download/`（10 文件 1,638 行）走**另一种路线**：`DownloadEngineRegistry.Select:34` 按协议选引擎（`Aria2Engine`、`BuiltinHttpEngine`、`Ed2kEngine`）。**自身也做分片并发** —— `RangeDownloader.cs`（570 行）实现自适应分段下载：先发 `bytes=0-0` 探测服务器是否支持 Range（支持则**跳过探测**、直接用已知大小），按 8 MB 目标段大小切段、每段独立重试（上限 2 次）、段慢于其他活跃连接时重启该段（PCL 的"相对慢"与"绝对慢"两条判定都补齐了：相对慢 = 自身速率 < 中位速率/4 且中位 ≥ 明显快阈值；绝对慢 = 自身速率 < 绝对慢阈值**且**存在明显更快的段）、Content-Range 一致性校验、段提前结束即判失败。`BandwidthThrottle.cs`（78 行）做整连接限速。⚠️ **与 PCL 原版的三处差异**：① PCL 的 `AdaptiveRangeDownloader` 位于**自定义许可目录**（`Plain Craft Launcher 2/Modules/Network/Downloader/`），**只能借鉴设计、不能复制代码** —— UMango 是独立实现；② PCL 用 `SafeFileHandle` + `MemoryMappedFile` 写段，UMango 用 `FileStream.Seek` + 写偏移（更简单、跨平台，代价是大文件随机写不如 mmap）；③ PCL 在 `ModNet.NetTaskSpeedLimitHigh > 0` 时**关闭慢段重启**，UMango 对应的是 `RangeDownloaderOptions.Throttle != null`（整连接限速时同样不重启慢段）。**因此不是"未做"而是"用不同架构覆盖"** |
| 下载任务门面与重试/镜像源 | `Modules/Network/Facade/ModNet.cs:6`（`NetGetCodeByRequestRetry:16`、`NetDownloadByLoader:107`、`NetTaskSpeedLimit*:9-13`） | **否** | **已完成** | ✅ `src/UMango.Core/Download/DownloadTaskFacade.cs`（统一重试 + 镜像降级）+ `DownloadSources.cs`（`DownloadSource` / `DownloadRetryPolicy` / `DownloadAttempt` / `DownloadTaskResult`）+ `MirrorUrls.cs`（镜像模板派生）。⚠️ **与原版的四处差异**：① 原版重试与换源塞在一个双层循环里，**换源后每个源都重试满次数**（主源 3 次 + 备用源 3 次 = 6 次请求）→ 这里**总量封顶**（`MaxTotalAttempts`，默认 6）；② 原版只支持**一个** `FallbackUrl` → 这里支持**任意多个**镜像源；③ 原版是同步接口（`.GetAwaiter().GetResult()` 阻塞 UI）→ 全异步；④ 每次尝试都如实记进 `DownloadAttempt` 列表，界面能显示"主源失败 → 正在试镜像"。 |
| 单文件下载器（URL→本地文件） | `Modules/Network/Downloader/FileDownloader.cs:9`（`DownloadAsync:11/19`、`DownloadByLoader:27`） | **否** | **已完成** | UMango `Download/Engines/BuiltinHttpEngine.cs:14` + `DownloadModels.cs:41`（`DownloadRequest`） |
| HTTP 请求层（JSON/字符串/超时/UA） | `Modules/Network/Http/Requester.cs:13`（`FetchJsonAsync:40/45`、`FetchStringAsync:21`） | **否** | **已完成** | ✅ `src/UMango.Core/Network/Http/`（P1-2 已迁 PCL `IO/Net/Http/` Apache-2.0 层：请求构建 / 头 / 内容 / Cookie / 认证、响应读取、克隆、**带退避的重试**、代理管理、**HTTP 缓存处理器**）+ `src/UMango.Core/Utils/JsonCompat.cs`（宽松 JSON）。⚠️ 相对原版的**实质差异**：**不引入 Polly**（PCL 用它做重试/熔断）→ 用等义的轻量退避重试，不额外引入依赖 |
| 请求签名 | `Modules/Network/Http/RequestSigning.cs` | 否 | **已完成**（等价实现） | CE 新增；与 PCL 官方服务配套（内置他人 API Key、强制 HTTP/2）→ **不逐字复制**，按「调用方自带密钥 + 算法可插拔」重写：`src/UMango.Core/Network/Http/RequestSigning.cs`（`IRequestSigner` 可插拔 + `HmacRequestSigner`：签名串构造 / 时间戳与 nonce 头 / 签名头注入，**密钥由调用方传入**）+ `RequestSigning` 静态辅助（CDN 标记展开 / 空格转义 / 默认头 / 统一签名入口） |
| 下载载荷构造与 MC 镜像源 | `Modules/Network/Downloader/DownloadRequestFactory.cs`、`Models/FetchParam.cs`、`NetworkEnums.cs` | 是 | **不适用** | 内含 MC 资源站镜像源与目录约定 |
| 下载文件模型与去重 | `Modules/Network/Models/DownloadFile.cs` | 否 | **已完成** | ✅ `src/UMango.Core/Download/DownloadFile.cs`（URL + 目标路径 + 期望 SHA-256，`IsComplete()` 判定「存在且非空」或「存在且哈希匹配」）+ `DownloadRequest.SkipExisting` 开关（默认关 —— 显式要求才跳过，避免用户点"重新下载"被悄悄跳过）。⚠️ **顺带修掉一个真实缺陷**：原 `BuiltinHttpEngine` 直接 `File.Create(target)` 写目标路径 → 下载中途失败/取消会把原来完好的文件毁成半个；现改为**先写 `.part` 临时文件、校验通过再落位**（失败时原文件完好，且不留 `.part` 残留） |
| Loader 驱动下载（与加载器管线绑定） | `Modules/Network/Loaders/LoaderDownload.cs`、`LoaderDownloadUnc.cs` | 部分 | **部分** | UMango `Install/InstallExecutor.cs:70`（`InstallAsync`）+ `InstallProgressParser.cs:27/53` 是等价能力（把进程输出解析成进度），**已迁移思路但非同一实现** |

### 2.I Modules/Minecraft（MC 业务核心）— 全部 MC 专属

| 功能 | PCL-CE 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| **MC 启动管线（classpath/JVM 参数/游戏参数/native/鉴权注入）** | `Modules/Minecraft/ModLaunch.cs:28`（`McLaunchStart:233`，2,807 行） | 是 | **不适用** | **许可证重度使用的核心触发点** |
| 资源下载编排 | `Modules/Minecraft/ModDownload.cs:15`（2,502 行） | 是 | **不适用** | |
| 社区资源元数据 | `Modules/Minecraft/ModComp.cs`（3,823 行） | 是 | **不适用** | |
| 本地资源管理 | `ModLocalComp.cs`（2,493 行） | 是 | **不适用** | |
| 整合包 | `ModModpack.cs:1738` | 是 | **不适用** | |
| 资源库依赖下载 | `ModLibrary.cs:691` | 是 | **不适用** | |
| 实例模型与状态 | `McInstance.cs:1062`、`McInstanceInfo.cs`、`McInstanceState.cs` | 是 | **不适用** | |
| 实例列表 | `ModInstanceList.cs:636` | 是 | **不适用** | |
| Java 管理（业务侧） | `ModJava.cs:15`（`Javas:27`、`GetInstanceJavaPreference:209`、`IsGameSet64BitJava:270`） | 是 | **不适用** | |
| 皮肤 | `ModSkin.cs` | 是 | **不适用** | |
| 进程监控（游戏启动/退出检测） | `ModWatcher.cs:12`（`hasRunningMinecraft:17`、824 行） | 是 | **不适用** | |
| Jar-in-Jar 解析 | `ModJarInJar.cs` | 是 | **不适用** | |
| 资源文件/图标 | `ModAssets.cs`、`ModStyle.cs` | 是 | **不适用** | |
| 游戏日志过滤 | `McLogFilter.cs` | 是 | **不适用** | |
| 版本比较（MC 版本号） | `McVersionComparer.cs` | 是 | **不适用** | ⚠️ UMango 的 `SemVer` 比较在**适配器内部自建**（`git` 宽松版本比较注释见 `GitPackageManager.cs`），与本文件无关 |
| Profile UI | `ProfileUi.cs:518` | 是 | **不适用** | |
| **崩溃分析子系统** | `Modules/Minecraft/CrashAnalysis/`（**28 文件 / 3,874 行**：`CrashAnalyzer.cs:59`、`Detection/CrashRuleCatalog.cs`、`Detection/CrashStackAnalyzer.cs`、`Export/CrashReportExporter.cs`、`Logs/CrashLogCollector.cs`、`Presentation/CrashDialogPresenter.cs`） | 是 | **不适用** | 规则目录针对 MC Java 堆栈与 mod 列表；**日志收集与报告导出部分思路可借鉴，但需重写** |

### 2.J PCL.Core / Minecraft（核心库的 MC 部分）

| 功能 | PCL.Core 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| MC 账号身份体系（MS OAuth / 离线 / Authlib / Yggdrasil Connect） | `PCL.Core/Minecraft/IdentityModel/`（22 文件 1,613 行）、`Profile/Authentication/` | 是 | **不适用** | |
| MC 档案服务 | `Profile/ProfileService.cs`、`Models/McProfile.cs`（21 文件 1,563 行） | 是 | **不适用** | |
| Java 运行时扫描与管理 | `Minecraft/Java/`（21 文件 986 行，含 4 种 Scanner + PE 头解析） | 是 | **不适用** | 其中 `PeHeaderParser.cs`、`Utils/PEHeaderReader.cs` **算法通用**（PE 格式），但服务对象是 JVM |
| MC 存档解析与编辑（NBT，跨版本） | `Minecraft/Saves/`（20 文件 1,144 行：`Parsing/Internal/` 6 个版本化解析器、`Editing/`、`SaveManager.cs`） | 是 | **不适用** | NBT 是 MC 专有格式；**版本化解析器架构是很好的可借鉴范式** |
| MC 资源工程（Modrinth/CurseForge） | `ResourceProject/`（15 文件 498 行） | 是 | **不适用** | |
| 依赖解析器 | `ResourceProject/ModDependencyResolver.cs` | 是 | **不适用** | 语义是 mod 依赖 |
| Maven 坐标与版本范围 | `McVersionClassifier.cs`、`MavenArtifact.cs`、`Utils/VersionRange.cs` | 是 | **不适用** | Maven 机制通用，但用于 MC 库坐标 |
| NBT 文件读写 | `NbtFileHandler.cs`、`Utils/VarIntHelper.cs` | 是 | **不适用** | |
| 服务器地址解析 / MC Ping / MOTD | `ServerAddressResolver.cs`、`Link/McPing/`（9 文件）、`UI/Controls/MotdRenderer.xaml` | 是 | **不适用** | |
| 游戏核心常量/格式化 | `GameCore.cs`、`McFormatter.cs`、`Yggdrasil/ApiLocation.cs` | 是 | **不适用** | |

### 2.K PCL.Core 通用能力（非 MC）

| 功能 | PCL.Core 位置（文件:行） | 是否 MC 专属 | UMango 状态 | 备注 |
|------|----------------------|------------|-----------|------|
| **配置体系**（多存储后端 + 迁移 + 观察者 + 源生成器） | `App/Configuration/`（**28 文件 / 2,478 行**：`ConfigService.cs:19`、`Storage/` 5 种后端 `JsonFileProvider:32`/`YamlFileProvider:45`/`CatIniFileProvider:16`/`EncryptedFileConfigStorage:12`/`DynamicCacheConfigStorage:7`、`ConfigStorage.cs:24`、`ConfigMigration.cs` + `PCL.Core.SourceGenerators/` 5 生成器） | 否 | **部分** | UMango `src/UMango.Core/Configuration/`（6 文件 653 行）：`SettingsService.cs:15`（`SchemaVersion:18`、`Descriptors:39`、`Sections:47`）+ `SettingDescriptor.cs:25` + `JsonFileSettingsStore.cs:13`（含 `LoadRawWithMigration:79`）+ `UiSettings.cs`（10 个描述符，4 分区）。**描述符驱动的思路已具备**，但**只支持 JSON 一种后端**，无源生成器、无加密存储、无配置事件注册表 |
| **任务中心** | `App/Tasks/`（8 文件） | 否 | **已完成** | UMango `App/Tasks/` 8 文件，**已按 Apache-2.0 逐文件采用并标注**（见 `THIRD-PARTY-NOTICES.md` §1 表格：`TaskState`/`ITask`/`ITaskProgressive`/`ITaskCancelable`/`ITaskPausable`/`ITaskGroup`/`TaskModel`/`TaskCenter`）；`TaskModel` 新增 `IsGroup`/`Children` |
| 日志系统（分级/包装/适配器） | `Logging/`（10 文件 849 行：`Logger.cs`、`LogService.cs`、`LogLevel.cs`、`LoggerAdapter.cs`、`LoggerFactoryAdapter.cs`） | 否 | **已完成** | ⚠️ **判定在调研期间变化了两次**：初判「未做」（当时 `grep -ril Logging src/` = 0）→ 中间改为「部分（进行中）」→ 现已**已完成**（commit `f2cc0b9`）。实测 `src/UMango.Core/Logging/`（5 文件 **429 行**：`Log.cs`、`ILogSink.cs`、`LogModels.cs`、`MemoryLogSink.cs`、`FileLogSink.cs`）+ 已接入 `Execution/ProcessRunner.cs`。
⚠️ **该 commit 的信息里直接写着「功能对照表 P0-3」** —— 即本报告的条目编号被当作工单使用了 |
| 内部事件总线 | `App/EventBus/`（`EventBusService.cs`、`EventDataBase.cs`、`IEventHandler.cs`、`IResponsibleEventHandler.cs`） | 否 | **已完成** |  实现位置：Core/Events/ |
| IoC / 生命周期 | `App/IoC/`（`LifecycleService.cs`、`DependencyGroup.cs`、`LifecycleFlow.cs`、`PropertyAccessor.cs` 等） | 否 | **已完成**（生命周期编排部分） | `src/UMango.Core/App/Lifecycle/LifecycleManager.cs`（commit 3036f7b）—— 生命周期阶段编排已迁；⚠️ **未迁依赖注入容器本体**（UMango 仍用 `AppServices` 的 `Lazy<T>` 静态服务定位） |
| 本地化服务 | `App/Localization/`（`Lang.cs`、`LocalizationService.cs`、`LocalizationFontService.cs` + 7 个语言 XAML） | 否 | ****已完成（本地化机制 + zh-CN/en-US 双语，392/391 键）** | 见 2.A；字体配置（LocalizationFontService）未做 —— UMango 用同一套 SystemFont，无逐语言字体需求 | |
| 路径与环境 | `App/Paths.cs`、`App/Basics.cs`、`App/Metadata.cs` | 否 | **部分** | UMango `Platform/SystemInfo.cs`（`PlatformKind.cs`）、`AppServices.cs:138`（`SettingsPath`） |
| 密钥与凭据保护 | `App/Secrets.cs`、`Utils/Secret/`（`EncryptHelper.cs`、`Identify.cs`）、`Utils/Encryption/`（AesGcm/AesCbc/ChaCha20 ×2 + 软件实现） | 否 | **已完成**（等价实现） | `src/UMango.Core/Security/`（commit b4e14e8）—— 软件 provider + 跨平台密钥保护抽象；⚠️ DPAPI 只在 Windows 分支，未引入 `PlainToolkit.CngProtectedData` |
| 单实例 / 启动校验 / 崩溃上报 | `App/Essentials/SingleInstanceService.cs`、`StartupValidation.cs`、`TelemetryService.cs`（Sentry） | 否 | **部分** | **单实例已完成**（commit 9715e65）：改用命名互斥锁（.NET 跨进程原语）而非 PCL 的 FileShare 锁 —— 后者在 macOS/Linux 上是咨询锁，不互斥（测试抓到的真实缺陷）；激活通道用命名管道。崩溃上报（Sentry）未做（需外部服务，且 UMango 有本地日志+诊断包导出作为替代） |
| RPC 服务 | `App/Essentials/RpcService.cs`、`RpcFunction.cs`、`RpcProperty.cs`、`RpcResponse.cs` | 否 | **已完成** | `src/UMango.Core/Ipc/`（`PipeServer.cs` + `Rpc/RpcService.cs`/`RpcProtocol.cs`/`RpcClient.cs`）+ `src/UMango.Core/App/RpcCli.cs` + `src/UMango.App/Services/RpcHost.cs`。**与原版的实质差异**：① 原版把 RPC 挂在自己的 IoC 上，UMango 改为**独立命名管道 + 显式注册表**；② 原版默认放行，UMango **默认全部拒绝** —— 命名管道没有鉴权，开放能力必须「显式注册 + 显式放行」两道手续都做；③ 管道名写在独立的 `rpc.pipe` 文件里（**不是**单实例的 `instance.lock` —— 那个存的是激活通道的管道名，协议不同）；④ 命令行侧 `umango rpc REQ/GET/SET <名称>` 调运行中的实例。**修掉的真实缺陷**：协议曾用 `Encoding.UTF8`（带 BOM），响应首行变成 `\uFEFFSUCCESS`，按名字解析状态必然失败 |
| 应用/窗口服务 | `App/Essentials/ApplicationService.cs`、`MainWindowService.cs`、`PromoteService.cs` | 否 | **部分** | UMango 在 `MainWindow.xaml.cs` 内直接实现 |
| 命令行参数解析 | `App/Cli/`（`CommandLine.cs`、`TextArgument.cs`、`DecimalArgument.cs`、`BoolArgument.cs`、`SubcommandDefinition.cs`） | 否 | **已完成** |  实现位置：Core/Utils/（--flag / --key=value） |
| 数据库服务 | `App/Database/`（`DatabaseService.cs`、`DatabaseEntry.cs` + README） | 否 | **已完成** | **`src/UMango.Core/Database/DatabaseService.cs` / `DatabaseEntry.cs`**（Apache-2.0 层）+ **LiteDB 5.0.21（MIT，无传染性）**。PCL 原版 48 行代码依赖其自有 IoC 生命周期体系（`GeneralService`/`LifecycleServiceAttribute`，13 文件），为 48 行引入整个框架不成比例，故**只保留语义、重写实现**（连接按路径缓存、空路径即抛、退出时统一释放）。`DatabaseEntry` 保留 PCL 的结构与命名，仅把幂等闸门从 `Dispose(bool)` 移到非虚的 `Dispose()` —— 原写法下子类 override 会绕过闸门，清理逻辑重复执行（13 测试守着） |
| HTTP 客户端扩展（代理/缓存/路由/内容处理） | `IO/Net/Http/`（`HttpRequest.cs`、`HttpProxyManager.cs`、`HttpCacheHandler.cs`、`HttpRoute.cs`、`HttpServer.cs`、`HttpBasicExtension.cs` 等） | 否 | **已完成** | 体量大且通用；UMango 仅 `HttpClient` 裸用  实现位置：Core/Network/HttpService.cs + IHttpService |
| DNS 查询与加权竞速 | `IO/Net/Dns/`（`DnsQuery.cs`、`WeightedDnsRacerClient.cs`、`DnsSrvResource.cs`） | 否 | **已完成**（等价实现） | 竞速算法已迁 `src/UMango.Core/Network/WeightedRacer.cs`（commit 6a5e9b4）；⚠️ **未引入 `Ae.Dns.Protocol`**，DNS 协议栈本身未迁 |
| TCP 端口转发 | `IO/Net/SocketForward/`（`TcpForwardBuilder.cs`、`TcpForwardConfig.cs`、`TcpForwardWorker.cs`） | 否 | **已完成** | `src/UMango.Core/Network/SocketForward/`（Apache-2.0，逐字/等义实现 + 修 2 个真实缺陷）：① **端口用 int 而不是 ushort** —— 原版 `SetRemote(IPEndPoint)` 写 `(ushort) remote.Port`，端口号超范围时**静默截断**成完全不同的端口，转发会连到错误目标；这里直接用 int 并在构造期校验。② **`Start` 改为可等待的 `StartAsync`** —— 原版 `Start()` 是 fire-and-forget，`LocalEndPoint` 要等后台跑到 `Bind()` 之后才赋值，调用方紧接着读几乎必然拿到 `null`（随机端口时完全没法用）；这里 await 到监听就绪再返回端点。另有 3 处差异：`Stop()` 后台收尾时不再 `Dispose` 信号量（避免运行中的连接 `Release()` 打到已释放对象，原版靠 catch `ObjectDisposedException` 掩盖）；`AcceptAsync` 遇 `ObjectDisposedException` 直接收工（原版当普通错误处理，会空转 500ms 重试）；新增 `WaitForConnectionsAsync` 便于优雅退出与测试。测试用**真实回环 socket 做端到端转发**（13 个用例）。 |
| 缓存服务（SQLite + 内容寻址） | `IO/Storage/Cache/`（12 文件 **1,839 行** + `Model/` 3 文件 154 行：`CacheService.cs:23`、`SqliteCacheStorage.cs:9`、`CacheEvictionService.cs`、`CachePolicy.cs`、`SchemaManager.cs` 209 行） | 否（**但含 MC 行模型**） | **已完成** | UMango 只有 `PackageManagerRegistry` 内一个**内存字典** `_cache`，**重启即失效**。⚠️ **迁移注意**：这套通用缓存服务上挂着**两张 MC 专用的行模型** —— `Model/ComponentCacheRow.cs:8`（`CompType // mods/rp/shader/saves/datapack`）、`:22`（`ModLoader`）、`Model/InstanceCacheRow.cs:19`（`VanillaVersion`）、`:28`（`LoaderJson`）。迁移时应**只取 CacheService/SqliteCacheStorage/SchemaManager 等 12 个通用文件，丢掉 `Model/` 的这两张表**  实现位置：Core/Caching/（LruCache + AvailabilityCache，JSON 实现） |
| 哈希存储 | `IO/Storage/HashStorage.cs` | 否 | **已完成** | `src/UMango.Core/Storage/HashStorage.cs`（Apache-2.0 逐字采用 + 修 2 个真实缺陷）：① 原版 `PutAsync(Stream)` 算完哈希后流停在末尾 → 随后的拷贝**写 0 字节，库里存出的永远是空文件**；这里改为一次读入内存再算再写。② 原版校验 `hash.Length != hashProvider.Length` —— 拿**十六进制串长**和**字节数**比（MD5 串长 32 / 字节数 16）→ 调用方永远传不进 hash；这里按串长（= 字节数 × 2）校验。另有 5 处差异（路径统一用绝对路径、删空前缀目录、存在性缓存、返回值语义统一、注释说明压缩流生命周期）见源文件头。 |
| 文件/目录抽象与字节流 | `IO/Files.cs`、`Directories.cs`、`ByteStream.cs` | 否 | **已完成** | `src/UMango.Core/IO/`（`Files.cs` 868 行 / `Directories.cs` / `ByteStream.cs`，均 Apache-2.0 逐字采用 + 修真实缺陷）。⚠️ 相对原版的**实质差异**：① **原版的 `Directories.CheckPermissionAsync` 是 Windows-only** —— 它用 `DirectoryInfo.GetAccessControl()` + `WindowsIdentity.GetCurrent()`，这两个 API 在 net10.0 支根本编译不过，所以原版事实上把整个文件绑死在 Windows 上；UMango 改成「**真去写一个临时文件**」来判定权限（跨平台，且比读 ACL 更贴近真实可用性：ACL 允许但磁盘只读/配额满/沙箱限制都会体现为写失败），并**修掉原版一个真实缺陷**：原版用 `principal.IsInRole(rule.IdentityReference.Value)` 判组权限，而 `WindowsPrincipal.IsInRole(string)` 期望的是**角色名**，对 "BUILTIN\\Users" 这类组恒返回 false → **普通用户永远被判成「无写权限」**。② 原版 `CopyDirectoryAsync` 用 `file.FullName[fromPath.Length..]` 切相对路径，源路径末尾没分隔符时会切错；改用 `Path.GetRelativePath`。③ 原版 `DeleteDirectoryAsync` 递归删完子目录后又对子目录 `Delete(recursive: true)`，若子目录在两次删除之间被重建会连带删掉新内容；改为一律只删空目录。④ **原版 gz 解压把输出文件名 `ToLower()`** —— "Plain.txt.gz" 解压出来变成 "plain.txt"，文件名被改写；UMango 保留原始大小写。⑤ 原版 tar 解压用单遍 `TarReader`，而进度需要总大小 → UMango 改为**先缓冲整包再两遍读取**（单遍读取会写出 0 字节文件）。⑥ 原版 `CreateShortcut` 依赖 COM `WScript.Shell`（仅 Windows）、`PasteFromClipboardAsync` 依赖 WPF `Clipboard` → **均不迁**（前者是 Windows 专属行为，后者由 App 层的 `CustomEventActions.CopyToClipboard` 承担）。⑦ 原版 `ExportAsZipArchiveAsync` 里夹了 `SystemDialogs.SelectSaveFile`（WPF 对话框）→ Core 层只做打包，选路径交给 App 层。
| 进程间管道通信 | `IO/PipeComm.cs` | 否 | **已完成** | ⚠️ `src/UMango.Core/Ipc/PipeServer.cs`（commit c7cf255） |
| 下载抽象（NDlFactory 等） | `IO/Download/`（**6 文件**：`NDlFactory.cs`、`IDlConnection.cs`、`IDlResourceMapping.cs`、`IDlWriter.cs`、`NDlConnectionInfo.cs`、`NDlSourceReport.cs`） | 否 | **不适用（死代码）** | ⚠️ **本表首轮把它列为可迁移项，是错的**。实测：`NDlFactory`/`IDlConnection`/`IDlWriter`/`IDlResourceMapping`/`NDlSourceReport` 在 `IO/Download/` 之外的引用数为 **0** —— **没有任何实现、没有任何调用点**，是废弃骨架。**不应列为迁移项** |
| 验证器族 | `Utils/Validate/`（12 个 Validator **457 行** + FluentValidation） | 否（**有一处 MC 开关**） | **已完成** | `src/UMango.Core/Validation/`（`Validators.cs` + `FileSystemValidator.cs`）+ `FluentValidation` 12.1.1（**Apache-2.0**，零传递依赖）。⚠️ 相对原版的三处**实质差异**：① 校验消息不再硬编码中文，改为 `ValidationText` 里的**资源键**，经 Core 的 `LocalizationService` 查（无 UI 环境回落为键本身，测试可直接断言键）；② `useMinecraftCharCheck` **默认关闭** —— Minecraft 不许 `!`，但 git 仓库名、npm 包名里它合法；③ **修掉原版两个真实缺陷**：`StringLengthValidator` 首条规则条件写反（长度恰好等于 `Max` 时被拒）、Minecraft 非法字符传的是单串 `"!;"` 而非两个字符（`"wow!"` 命不中）。另：原版每次校验重建规则（`PreValidate` + `_BuildRules`），UMango 改为构造时建一次 |
| 哈希算法族 | `Utils/Hash/`（MD5/SHA1/SHA256/SHA512/MurmurHash2 + `HashCache.cs`） | 否 | **已完成** | UMango `Hash` 关键字命中仅 4 处 `HashSet`（**误命中**）  实现位置：Core/Hash/HashService.cs（MD5/SHA1/SHA256/SHA512） |
| 加密算法族 | `Utils/Encryption/`（AES-CBC/GCM、ChaCha20-Poly1305 + 纯软件实现） | 否 | **已完成** | 实现位置：Core/Crypto/CryptoService.cs（AES-GCM / ChaCha20-Poly1305）+ **Core/Security/**（`IEncryptionProvider` + AES-GCM / ChaCha20-Poly1305 / 纯软件 ChaCha20 三个 provider、版本化密文容器 `EncryptionData`、跨平台密钥保护 `IKeyProtector`（Windows DPAPI / 其它平台文件权限）、`SecretService` 密钥生命周期、`AtomicFile` 原子写） |
| 二进制差分（BsDiff/ItemDiff） | `Utils/Diff/`（`BsDiff.cs`、`IBinaryDiff.cs`、`ItemDiffResult.cs`） | 否 | **已完成** | 用于增量更新  实现位置：Core/Diff/BinaryDiff.cs |
| 编码检测与转换 | `Utils/Codecs/`（`EncodingDetector.cs`、`EncodingUtils.cs`、`Encodings.cs`） | 否 | **已完成** |  实现位置：Core/Utils/（UTF-8/GBK/Latin1 检测） |
| 线程/并发原语 | `Utils/Threading/`（`AsyncDebounce.cs`、`AsyncManualResetEvent.cs`、`AsyncCountResetEvent.cs`、`DualThreadPool.cs`、`LimitedTaskPool.cs`、`LimitedConcurrencyLevelTaskScheduler.cs`） | 否 | **已完成** | UMango `Threading` 命中 52 处**全是 `using System.Threading`（误命中）**  实现位置：Core/Threading/（AsyncDebounce / Throttle / LimitedConcurrencyTaskScheduler） |
| 字符串/集合/任务扩展 | `Utils/Exts/`（8 文件：`StringExtension.cs`、`ListExtension.cs`、`TaskExtension.cs`、`UiExtension.cs`、`ConcurrentDictionaryExtension.cs`、`AsyncEnumerableExtensions.cs`、`ByteExtension.cs`、`HttpRequestExtension.cs`） | 否 | **已完成** |  实现位置：Core/Utils/ |
| 语义化版本与版本范围 | `Utils/SemVer.cs`、`Utils/VersionRange.cs` | 否 | **已完成** | `src/UMango.Core/Utils/SemVer.cs` + `VersionRange.cs`（commit c45c21d）—— 公共类型已迁；⚠️ 预发布比较按 semver.org 规范修正，与原版行为不同 |
| 相似度搜索 | `Utils/SimilaritySearch.cs` | 否 | **已完成** | PCL 用于模糊匹配 MC 版本名  实现位置：Core/Utils/（Levenshtein + 子串包含） |
| 文本/正则/随机工具 | `Utils/RegexPatterns.cs`（**246 行**）、`RandomUtils.cs`、`TimeUtils.cs`、`Result.cs`（149 行）、`JsonCompat.cs`（236 行）、`StringStream.cs`、`ExpandoObjectConverter.cs` | 否（**`RegexPatterns` 大部分是 MC 正则**） | **部分** | UMango 各页自建少量工具。⚠️ **`RegexPatterns.cs` 不宜整文件迁移**：实测 `:78`/`:85`/`:101-122`/`:160`/`:215`/`:219` 是 MC 颜色代码、MOTD 格式码、MC 版本号（正式/快照/Indev/Infdev）、MC 实例解析等**MC 专属正则**；而 `Result`/`JsonCompat`/`SemVer`/`SimilaritySearch` 才是干净可用的。**已采纳的部分**：`src/UMango.Core/Utils/RegexPatterns.cs` 取其 `NewLine`/`HttpUri`/`FullHttpUri`/`UncPath`/`Ntfs83FileName`/`SemVer`/`HasChineseChar` 七个**非 MC** 模式（保留 `[GeneratedRegex]` 源生成写法），MC 专属模式全部剔除；35 个测试钉住边界 |
| 系统信息与硬件 | `Utils/OS/`（`SystemInfo.cs`、`HardwareInfo.cs`、`SystemPaths.cs`、`SystemTheme.cs`、`EnvironmentInterop.cs`、`ProcessInterop.cs`、`WindowInterop.cs`、`KernelInterop.cs`、`NtInterop.cs`、`AumidHelper.cs`、`ClipboardUtils.cs`、`DragHelper.cs`、`RegistryChangeMonitor.cs`、`NetworkUtils.cs`） | 否 | **已完成**（跨平台部分） | `src/UMango.Core/Utils/OS/` 4 个跨平台文件（commit 96b0e8d）+ 既有 `Platform/SystemInfo.cs`；⚠️ 注册表 / `NtInterop` / `KernelInterop` / `WindowInterop` 等 Win32 专属部分未迁 |
| 主题/配色（NColor、色调档、系统主题跟随） | `UI/Theme/`（`ThemeService.cs`、`SystemThemeHelper.cs`、`CatColorResource.cs`、`LabColor.cs`、`ToneProfile.cs`）、`UI/NColor.cs` | 否 | **已完成**（等价实现） | 实现位置：`src/UMango.Core/UI/Theme/`（`OklabColor.cs` 色彩空间 + `PaletteGenerator.cs` 调色板生成 + `ToneProfile.cs` 色调档 + `SystemThemePreference.cs` 跟随策略解析）+ `src/UMango.App/Themes/SystemThemeProbe.cs`（跨平台探测）+ `src/UMango.App/Themes/ThemeManager.ApplyFollowMode`（接线）。UMango 有 4 套 token 字典 + `ThemeManager`；**无** LAB 色彩空间、色调档、系统主题跟随 —— **已补齐**。⚠️ 相对原版的**实质差异**：① 原版用 `Wacton.Unicolour` 这个 NuGet 包做 Oklab/Oklch，UMango **在 Core 里直接实现 Oklab 变换矩阵**（不引依赖，且可在 macOS 上测试）；② 原版 `SystemThemeHelper` 只读 Windows 注册表，UMango 改为按平台分派（Windows 注册表 / macOS `defaults read -g AppleInterfaceStyle` / Linux `gsettings color-scheme`），纯逻辑「策略 → 深浅色」下沉 Core 可测；③ 原版 `NColor` 是基于 `Vector4` 的 scRGB 结构体，UMango 未逐字采用（其使用面只在 PCL 内部），改为 `OklabColor`（L/C/H + 8 位 ARGB 互转）覆盖调色板生成的实际需要。 |
| 动画系统（完整） | `UI/Animation/`（**87 文件 / 2,742 行**：`Animatable/` 4、`Clock/` 5、`Core/` 21、`Easings/` 39、`ValueProcessor/` 9、`UIAccessProvider/` 2、根 7） | 否 | **已完成** | 见 2.A「动画引擎」与 §4.6「第 2.5 层」。**仅 14/87 文件触及 WPF，且零使用 WPF 的 `DoubleAnimation`/`Storyboard`/`Timeline`** —— 自研帧驱动引擎，可移植性远高于初判。**已补齐**：值处理器算术运算（Add/Subtract/Scale/Equal/Default）、绝对/相对值动画（`AnimationValueType`）、`ActionAnimation`、复合缓动（`CombinedEasing`/`CompositeEasing`/`BackEaseWithPower`）、`EmptyAnimatable`/`ClrAnimatable<TOwner,T>`、按名取消；**另已补齐 PCL 控件层的三块动效语义**（受限于许可目录，只借鉴设计与公开数值、独立重写）：`AnimTreeCollector`（控件树收集）、`StaggerPlanner`（子项级联）、`TweenChannelAnimation`（同通道并行叠加） |
| WPF 控件库 | `UI/Controls/SvgIcon/`（10 文件，自研 SVG 渲染）、`BlurBorder.cs`、`Tooltip.cs`、`WaterfallPanel.cs`、`MotdRenderer.xaml` | 否 | **部分** | UMango 用 lucide（ISC）图标 + 自绘样式；**未移植 SvgIcon 运行时解析器**（`SvgPathGeometryParser.cs` 等） |
| 模糊/特效 | `UI/Effects/`（`AdaptiveBlurEffect.cs`、`EnhancedBlurEffect.cs`、`OptimizedBlurEffect.cs`、`SamplingBlurProcessor.cs`） | 否 | **已完成**（部分） | 实现位置：`src/UMango.Core/UI/Blur/`（`BlurKernel.cs` 纯算法层 + `SamplingBlur.cs` 泊松采样层）+ `src/UMango.App/Effects/AdaptiveBlurEffect.cs`（WPF `ShaderEffect` 适配层）+ 着色器资源 `Assets/Shaders/AdaptiveBlur.{ps,hlsl}`。⚠️ **相对原版的实质差异**：① 原版 `OptimizedBlurEffect` / `EnhancedBlurEffect` 是 `Freezable` 而非 `Effect`，其 `GetEffectInstance()` **无论采样率都直接返回原生 `BlurEffect`** —— 原版那 590 行的 `SamplingBlurProcessor` 在 PCL 自己的界面路径上**从未真正执行**，只有 `AdaptiveBlurEffect`（`ShaderEffect` + 预编译 `.ps`）是真实生效的特效，故 UMango 同样只落地这一条真实路径；② 原版泊松采样的最小间距 0.7 在单位圆内**几何上不可行**（最多放 6 个点，32 个点放不下），UMango 改用 Vogel 螺旋（黄金角分布，实际最小间距 0.2733）；③ 原版高斯核在半径为 0 时算出 NaN，UMango 特判为单点核；④ 原版卷积结果**截断**取整，常量图经两次卷积后整体暗两档，UMango 改四舍五入；⑤ 原版 `TextureSize` 固定为 1920×1080，UMango 保持一致但注明为已知限制。**不引入** `EnhancedBlurEffect` / `OptimizedBlurEffect`（在原版中即死代码）。 |
| 图像加载与转换 | `UI/ImageLoaderHelper.cs`、`UI/Media/ImageConverter.cs`、`Utils/IconHelper.cs` | 否 | **已完成**（等价实现） | `src/UMango.Core/UI/Media/ImageProbe.cs` 字节级探测层（commit 406bee2）；⚠️ **未迁 WPF/WIC 专属的解码与 WebP 转换** |
| 灰度/无障碍 | `UI/GrayProfile.cs`、`GrayProfileConfig.cs` | 否 | **已完成** | 哀悼日灰度模式：Core `UI/GrayProfile.cs` 迁语义（PCL 原版零消费方）+ `GrayColorPlanner<TKey>` 记账；App `Themes/GrayModeController.cs` 改写资源字典里的画笔；设置项 `ui.appearance.grayMode`。**注意 WPF 没有 ColorMatrixEffect**（只在 GDI+/WinForms 里），所以只能换画笔而非挂 Effect |
| 后台展示器 | `UI/BackgroundPresenter.cs` | 否 | **未做** | 与视频背景配套（视频背景本身按 P3-3 判为「与包管理器无关」） |
| 图标管理 | `UI/IconManager.cs`、`IconModel.cs` | 否 | **部分** | UMango 用 lucide + simple-icons（见 `THIRD-PARTY-NOTICES.md` §2） |
| 联机基础设施（P2P / 协议） | `Link/Scaffolding/`、`Link/EasyTier/`、`Link/Lobby/`、`Link/BroadcastLocal.cs`（59 文件 5,018 行） | 是 | **不适用** | 语义是 MC 联机（见 2.F） |
| Natayark 账号 | `Link/Natayark/NatayarkProfileManager.cs` | 是 | **不适用** | 第三方 MC 联机平台 |
| **下载引擎（UMango 独有）** | —（PCL-CE 无对应） | **否** | **已完成** | UMango `Download/` 10 文件 1,638 行；`Aria2Engine.cs:24`（覆盖 HTTP/HTTPS/FTP/SFTP/BitTorrent/Metalink）、`Ed2kEngine.cs:22`、`BuiltinHttpEngine.cs:14`；协议推断 `DownloadModels.cs:9`（`Detect`）；自适应分段下载 `RangeDownloader.cs`（570 行，独立实现，PCL 原版在受限许可目录内） |
| **安装执行器（UMango 独有）** | —（PCL-CE 无对应） | **否** | **已完成** | `Install/InstallExecutor.cs:44`（`PlanAsync:61`、`InstallAsync:70`）、`InstallCommandBuilder.cs:11`（`BuildInstall:14`/`BuildUninstall:57`）、`InstallProgressParser.cs:16`、`StepNames.cs:10` |
| **操作记录（UMango 独有）** | —（PCL-CE 无对应） | **否** | **已完成** | `Records/InstallRecord.cs:8`、`InstallRecordStore.cs:16`（`Load:94`、`Save:115`、`CountBySource:66`） |
| **终端集成桥（UMango 独有）** | —（PCL-CE 无对应） | **否** | **已完成** | `Terminal/TerminalBridge.cs:22`（`For:53`、`LinesOfStep:114`、`DebugMode:50`）、`TerminalLine.cs:4` |
| **平台能力门控（UMango 独有）** | —（PCL-CE 无对应抽象） | **否** | **已完成** | `PackageManagers/PackageModels.cs:36`（`SupportedPlatforms`）、`:44`（`Supports`）、`Platform/PlatformKind.cs`、`PlatformSupportTests.cs` |
| **包源适配器 ×6（UMango 独有）** | —（PCL-CE 无对应） | **否** | **已完成** | `Adapters/`：`NpmPackageManager.cs`(191)、`PnpmPackageManager.cs`(501)、`GitPackageManager.cs`(839)、`HomebrewPackageManager.cs`(734)、`ScoopPackageManager.cs`(706)、`WingetPackageManager.cs`(481) |
| **已安装包列表（UMango 独有）** | — | **否** | **已完成** | ⚠️ **判定在调研期间由「部分」升级为「已完成」**：初判时 Core 的 6 个适配器均已实现 `ListInstalledAsync`（见 `InstalledListTests.cs`）但界面无消费方；随后并发任务提交 `39f5259`（已安装包列表接入界面）补齐了另一半 —— 实测 `ViewModels/DownloadViewModel.cs:84`（`Installed` 集合）与 `:245`（调用 `ListInstalledAsync()`） |
| **XAML 静态审计工具（UMango 独有）** | — | **否** | **已完成** | `tools/XamlAudit/`（5 文件 711 行）+ `scripts/xaml-audit.sh`；`docs/XAML_AUDIT.md` 记录了 4 类注入缺陷的验证 |

---

### 2.L 已修 PCL 原版缺陷（迁移时发现并修掉的真实 bug）

迁移 Apache-2.0 层不是逐字抄就完事 —— 实测原版里有几个**真实缺陷**，照抄会把 bug 一起搬过来。已修的：

| # | 缺陷 | 位置 | 症状 | UMango 的修法 |
|---|---|---|---|---|
| 1 | `StringLengthValidator` 首条规则条件写反 | `Utils/Validate/` | 长度**恰好等于** `Max` 时被判失败（`"abcd"` 在 `Max=4` 下无效） | 条件改为 `Min == Max ? Length == Min : Length >= Min && Length <= Max` |
| 2 | Minecraft 非法字符传的是单串 `"!;"` | `Utils/Validate/` | `input.Contains("!;")` 只在**同时出现** `!` 和 `;` 时才命中 → `"wow!"` 合法 | 改为两个独立字符 `["!", ";"]` |
| 3 | 保留名比较大小写敏感 | `Utils/Validate/` | `"con"` 通过（Windows 上 `con` 同样非法） | 改用 `StringComparison.OrdinalIgnoreCase` |
| 4 | `WithHeader` 同名头静默失败 | `IO/Net/Http/` | 先 `WithContent`（已带 `Content-Type: text/plain`）再 `WithHeader("Content-Type", "application/json")` **不生效** —— `TryAddWithoutValidation` 在头已存在时返回 false 且不抛，看起来成功了其实没改 | 先 `Remove` 再 `Add` |
| 5 | 缓存命中时丢掉状态码 | `IO/Net/Http/Cache/` | 反序列化响应一律返回 200，调用方无法知道原响应是 404 | 状态码与 ETag/Last-Modified 一起缓存并还原 |

每一项都有**对应的回归测试**（`src/UMango.Core.Tests/Validation/ValidatorTests.cs` 44 个、`src/UMango.Core.Tests/Network/HttpExtensionTests.cs` 57 个），并用缺陷注入验证过测试有牙。

## 3. MC 专属功能的完整清单（供决策"是否迁移"）

**合计：81 个 MC 专属条目（另 3 条「部分 MC」）**。按 PCL-CE 目录边界统计其**实际体积**如下表。

### 3.1 MC 专属代码的体积（实测）

| 归属 | 文件数 | 代码行数 | 许可 | 是否 MC 专属 |
|------|-------:|--------:|------|------------|
| `Plain Craft Launcher 2/Modules/Minecraft/`（含 `CrashAnalysis/`） | 50 | 23,352 | 自定义专有 | ✅ 全部 MC |
| `Plain Craft Launcher 2/Pages/PageDownload/` | 43 | 12,690 | 自定义专有 | ✅ 全部 MC |
| `Plain Craft Launcher 2/Pages/PageInstance/` | 30 | 16,029 | 自定义专有 | ✅ 全部 MC |
| `Plain Craft Launcher 2/Pages/PageLaunch/` | 18 | 3,686 | 自定义专有 | ✅ 全部 MC |
| `Plain Craft Launcher 2/Modules/ModLink.cs` | 1 | 398 | 自定义专有 | ✅ MC 联机 |
| `PCL.Core/Minecraft/` | 108 | 6,800 | **Apache-2.0** | ✅ 全部 MC |
| `PCL.Core/Link/`（联机/大厅/McPing/EasyTier/Natayark） | 59 | 5,018 | **Apache-2.0** | ✅ 语义是 MC 联机 |
| `Plain Craft Launcher 2/Pages/PageSetup/`（Java/游戏/GameLink 三页，MC 部分） | 约 5 | 约 1,100 | 自定义专有 | ⚠️ 该目录 12 页**混合**，需拆分（见 3.2） |
| **合计（估）** | **约 314** | **约 69,100** | — | — |

> 说明：上表按"整个目录属 MC"的口径汇总，因此**高估了 PageSetup 的 MC 部分**（该目录含界面/语言/关于/更新/反馈/日志等非 MC 页）。
> 更保守的纯 MC 估算：**Modules/Minecraft 23,352 + PageDownload 12,690 + PageInstance 16,029 + PageLaunch 3,686 + ModLink 398 + PCL.Core/Minecraft 6,800 + PCL.Core/Link 5,018 ≈ 67,973 行**。

### 3.2 关键观察

1. **MC 专属代码约占 PCL-CE 全库的 50%**（67,973 ÷ 138,447 ≈ 49%）。
   即：即使把"全部非 MC 功能"都迁移完，也**只是迁移了 PCL-CE 的一半代码量**。

2. **"迁移 MC 功能"与"后端剥离 MC 逻辑"物理上不可兼得**：
   本清单中 MC 侧条目（81 专属 + 3 部分）**84/171 = 49%**，且这些功能**没有"去掉 MC 部分"的版本** ——
   例如"MC 启动管线"剥离 MC 后不剩任何东西；"Java 扫描"剥离 MC 后只是"扫描 PE 文件"，
   那已不是同一个功能。

3. **许可层面的红线比架构红线更硬**：
   架构红线（后端不含 MC 逻辑）是项目自定的、可以改；
   《PCL 存储库合理使用指南》的 B6（改名）与 B7b（开源）是**外部约束**，
   且 `Plain Craft Launcher 2/` 目录**根本不是 Apache-2.0**（见 `/tmp/PCL-CE/LICENSE` 尾部与 `Plain Craft Launcher 2/LICENCE` 全文）。
   **特别注意**：即便选择"改名为 PCL XXX 并开源"这条路，
   `PCL.Core/Minecraft/`（6,800 行）与 `PCL.Core/Link/`（5,018 行）**是 Apache-2.0**，
   采用它们**不触发**自定义许可义务 —— 这是唯一可以"部分迁移 MC 能力"的合法缝隙。

4. **3 个"部分 MC"条目**（机制通用、数据是 MC 的），它们**不是**可以简单迁移的：
   `Modules/Network/Downloader/DownloadRequestFactory.cs`、`Modules/Network/Loaders/LoaderDownload.cs`、
   `Modules/Base/ModBase.cs`。
   迁移它们需要重写其中的 MC 镜像源与目录约定。
   （初稿曾把 `Modules/ModWebServer.cs` 也算进来，共 4 个；复核后改判为**否** ——
   它是通用 HTTP 服务器，MC 登录回调只是其用法之一，见 2.A 表。）

### 3.3 明确建议：**不迁移**这 84 项（81 条 MC 专属 + 3 条部分 MC）

理由按强度排序：

1. **法律**：迁移即触发重度使用，强制改名（B6）+ 开源（B7b）。
2. **架构**：与「后端完全剥离 Minecraft 逻辑」红线直接矛盾。
3. **产品**：UMango 是包管理器 GUI，MC 启动/Mod 管理与其定位无关（THIRD-PARTY-NOTICES.md 的"实质功能零重叠"判定正是建立在此）。

### 3.4 逐目录的 MC 占比实测（功能条目加权）

为了不靠印象，对 `Pages/` 的 135 个文件逐个按功能条目判定归属，结果如下：

| 分组 | 纯 MC | 部分 MC | 通用 | MC 占比 |
|------|------:|-------:|-----:|-------:|
| `Pages/PageDownload` | 全部 | — | — | **100%** |
| `Pages/PageInstance` | 全部 | — | — | **100%** |
| `Pages/PageLaunch` | 多数 | — | — | **85%** |
| `Pages/PageTools` | — | — | 多数 | **46%** |
| `Pages/PageSetup` | — | — | 多数 | **29%** |
| **`Pages/` 全体（按功能条目加权）** | 48 | 11 | 14 | **约 73%** |

**`PageSetup` 只有 29% 是 MC 的** —— 该目录 12 个子页里 **7 个是纯启动器功能**
（界面 `SetupUI`、语言 `SetupLauncherLanguage`、杂项 `SetupLauncherMisc`、
关于 `SetupAbout`、反馈 `SetupFeedback`、更新 `SetupUpdate`、日志 `SetupLog`），
这与第 4 节把它们列为可迁移项一致。

**功能归属的锚点**：每个 `*Left.xaml.cs` 的 `PageGet(FormMain.PageSubType)` switch 是权威映射，
例如 `Pages/PageInstance/PageInstanceLeft.xaml.cs:201` 起，12 个子页的 case 位于 `:207-:273`。
按此锚点可复核 2.D 表的每一行。

### 3.5 "可复用"部分其实很薄 —— 以崩溃分析为例

崩溃分析（`CrashAnalysis/`，28 文件 / 3,874 行）常被当成"可以剥离 MC 后复用"的候选。
实测**逐层拆开后，真正可直接复用的只有 198 行**：

| 层 | 文件 | 体积 | 可否复用 | 证据 |
|----|------|-----:|---------|------|
| 文本工具 | `CrashText.cs` | 74 | ✅ 纯逻辑 | MC token 计数 0 |
| 正则工具 | `CrashRegex.cs` | 59 | ✅ 纯逻辑 | 同上 |
| 文件 IO | `CrashFileIo.cs` | 65 | ✅ 纯逻辑 | 同上 |
| 日志采集 | `Logs/CrashLogCollector.cs` | — | ❌ | `:35` 硬编码 `Path.Combine(versionPathIndie, "crash-reports")` |
| 报告导出 | `Export/CrashReportExporter.cs` | — | ❌ | `:17-21` 硬编码 6 个 MC 报告文件名（`启动脚本.bat`、`游戏崩溃前的输出.txt`、`PCL CE 启动器日志.txt`、`环境与启动信息.txt`、`模组列表.txt`、`Report`） |
| 规则判定 | `Detection/CrashRuleCatalog.cs` | — | ❌ | 26 条硬编码规则 |
| 证据提取 | `Detection/CrashEvidenceExtractor.cs` | 713 | ❌ | 42 个 `_Extract*` 方法，全针对 MC 崩溃特征 |
| 结果格式化 | `Presentation/CrashResultFormatter.cs` | 524 | ❌ | 面向 MC 崩溃输出 |

**结论**：判定层（规则目录 + 证据提取 + 格式化）占该子系统绝大部分体积，且**完全 MC 专属**。
即便迁移，得到的也只是 198 行文本/正则工具 —— 不值得为它承担"迁移 MC 功能"的定性与许可风险。

### 3.6 反向证据：哪些"看起来 MC"的东西其实是通用的

本次调研刻意做了反向验证，避免把通用能力误判成 MC 专属而漏掉可迁移项。**最典型的是下载引擎**：

对整个 `Modules/Network/`（12 文件 / 1,137 行）做 MC 域名普查，**只有 2 个文件命中**：

| 文件:行 | 命中内容 |
|---------|---------|
| `Downloader/FileDownloader.cs:216` | `parsedUri.Host is "edge.forgecdn.net" or "mediafilez.forgecdn.net" or "forgecdn.net" or "api.curseforge.com"` → 走专用 HttpClient |
| `Http/RequestSigning.cs:25-28` | 对 `api.curseforge.com` / `edge.forgecdn.net` / `mediafilez.forgecdn.net` 注入 `x-api-key: Secrets.CurseForgeAPIKey` |

其余 **10 个文件（`NetManager`、`Requester`、`LoaderDownload*`、`ModNet`、全部 Models）零 MC 域名命中** ——
分片、重试、镜像轮换、限速、进度聚合**全部与 MC 无关**。

**这正是 2.H 表把这几项标为"否（非 MC）"的实测依据**，
也说明"下载引擎要迁移"是成立的 —— 只需把上面那 2 处 CurseForge 特判去掉。

### 3.7 唯一值得单独讨论的例外

`PCL.Core/Minecraft/` 与 `PCL.Core/Link/` 是 **Apache-2.0**，
其中 `Utils/PEHeaderReader.cs`-类的算法（PE 解析）、`Saves/Parsing/` 的**版本化解析器架构**、
`IdentityModel/` 的 **OAuth PKCE / OpenID / JWT 实现**在技术上是通用的。
但这些属于"借鉴设计"而非"迁移功能"，应作为独立需求另行评估，**不要挂在"功能对照迁移"名下** ——
否则会把许可边界搞模糊。

---

## 4. 非 MC 但尚未迁移的功能（按建议优先级排序）

**作用域**：第 1 节统计的 **90 条非 MC 功能**中，**未做 41 条 + 部分 28 条**（部分完成的也列出，但语义是"补齐"而非"新建"）。
> ⚠️⚠️ **读本节前必读：许可证决定了"能迁移"与"只能借鉴"是两件事。**
>
> 下表"涉及 PCL.Core 模块"一列里，**凡是标注 `Modules/...` 的条目，其代码都位于
> `Plain Craft Launcher 2/` 目录下 —— 那是《PCL 分发有限许可》（自定义专有协议），不是 Apache-2.0**
> （实测根 `LICENSE`：`SPECIAL DIRECTORY: Plain Craft Launcher 2/` → 自定义条款；
> `ALL THE OTHER DIRECTORIES` → Apache-2.0）。
> 因此这些条目的正确做法是**只借鉴设计、不复制代码**；直接复制会立即触发 §0 的重度使用义务（改名 + 开源）。
> 真正可以自由采用代码的，只有 `PCL.Core/**`。
>
> 本报告初稿在 P1/P2 表里列了 `Modules/Updates/`、`Modules/Event/`、`Modules/Base/MyBitmap.cs` 等条目，
> 却只在其中一条注明了许可问题 —— **这个遗漏已在此集中更正**。

本节把它们归并成 **42 个可执行条目**（P0 四项 / P1 七项 / P2 二十二项 / P3 九项；
其中 P0-3 与 P3-8 已由并发任务完成）—— 少于 68，因为多条功能被合并为一项
（例如整个 `Utils/Threading/` 的 6 个文件算一项，`Utils/Hash/` 的 7 个文件算一项）。

### 4.1 P0 —— 高价值、低风险、UMango 已具备落点

| # | 功能 | 现状 | 迁移成本 | 涉及 PCL.Core 模块 | Windows 依赖 |
|---|------|------|---------|------------------|-------------|
| P0-1 | **持久化缓存服务**（SQLite + 内容寻址 + 淘汰策略） | **已完成**（等价实现） | **中** | `IO/Storage/Cache/`（13 文件，`CacheService.cs`、`SqliteCacheStorage.cs`、`CacheEvictionService.cs`、`CachePolicy.cs`、`SchemaManager.cs`） | 否（纯 .NET）—— **不引入 `Microsoft.Data.Sqlite`**：`src/UMango.Core/Caching/AvailabilityCache.cs` 用 JSON 文件实现了同一套 `IAvailabilityCache` 抽象（数据量极小：六个包源、每天一次），依赖冲突自然消解；将来内容变复杂时替换实现即可，接口已隔离。另：本轮**数据库服务**（`App/Database/`，对应 LiteDB）已单独完成，见主表「数据库服务」行 |
| P0-2 | **配置体系补齐**（多后端 + 源生成器 + 事件） | ✅ **已完成**（Core `Configuration/`：5 种存取后端 + 迁移引擎 + 事件总线 + 门面） | — | `App/Configuration/`（**28 文件 / 2,478 行**：`ConfigService.cs:19`、`Storage/` 5 种后端（JSON/YAML/CatIni/加密/动态缓存）、`ConfigStorage.cs:24`、`ConfigMigration.cs`）—— **不迁源生成器**（PCL.Core.SourceGenerators 5 个 2,039 行） | 部分（`EncryptedFileConfigStorage` → `Utils/Secret/EncryptHelper` 走 Windows DPAPI/CNG；其余零 Windows 依赖） |
| P0-3 | **日志系统** | ✅ **已完成**（commit `f2cc0b9`，Core 5 文件 429 行 + LogView 404 行） | — | 已由并发任务实现，**该 commit 明确引用本报告的条目编号「P0-3」** | 否 |
| P0-4 | **已安装包列表接入界面** | **已完成**（commit `39f5259`，`DownloadViewModel.cs:84,245`） | — | 无需迁移 —— 是 UMango 自己的能力 | 否 |

### 4.2 P1 —— 高价值、中等成本

| # | 功能 | 现状 | 迁移成本 | 涉及 PCL.Core 模块 | Windows 依赖 |
|---|------|------|---------|------------------|-------------|
| P1-1 | **启动器自更新**（多通道抽象 + 镜像降级） | **已完成** | **中** | `Modules/Updates/`（11 文件 1,020 行）—— ⚠️ **位于自定义许可目录**，只能借鉴架构、不能复制代码；或另找 Apache-2.0 的替代实现 | 否 —— `src/UMango.Core/App/Essentials/UpdateChannels.cs`（`IUpdateSource` / `UpdateChannelKind` / `UpdateSourceResult`）+ `UpdateChannelChain.cs`（`UpdateChannelChain` 降级链 / `HttpUpdateSource` / `StaticManifestUpdateSource`）；App 层 `src/UMango.App/Services/UpdateService.cs` 按「官方源 → 静态清单备用源」串成链。⚠️ 相对原版的**实质差异**：① PCL 的 `IUpdateSource` 是**同步**接口（内部 `.Result` 阻塞），这里全异步 —— 阻塞式网络调用是 PCL 老版本的著名卡顿源；② PCL 的源各自定义 `VersionDataModel`，这里统一返回 `UpdateInfo`；③ **PCL 的通道联动逻辑不迁**：它会在「当前是 beta 但用户选了 stable」时拿 beta 通道比较，对 UMango 而言是自找麻烦，这里只有显式的 stable / prerelease 两档；④ PCL 的 Minio / MirrorChyan 具体源不迁（与其官方服务配套），UMango 用 GitHub Releases + 静态清单两个源。 |
| P1-2 | **HTTP 客户端扩展层**（代理/缓存/路由/内容处理） | **已完成** | **中** | `IO/Net/Http/`（约 18 文件） | 否 —— `src/UMango.Core/Network/Http/`（请求构建 / 头 / 内容 / Cookie / 认证、响应读取、克隆、带退避的重试、代理管理、HTTP 缓存处理器）+ `src/UMango.Core/Utils/JsonCompat.cs`（宽松 JSON）。⚠️ 相对原版的**实质差异**：① **不引入 Polly** —— 原版用 Polly 做重试，退避算法本身十几行，自己写更可控也更好测；② **去掉 Windows 注册表**（原版监听 `HKCU\...\Internet Settings` 变更来刷新系统代理）—— 那会破坏 Core 的 net10.0 支（跨平台可编译的证明），改为按需读 `WebRequest.DefaultWebProxy`；③ **去掉 PCL 专属元数据头**（`PCL-Community/PCL2-CE` UA 与 pcl2.server Referer）—— UMango 不该向 PCL 官方服务器表明身份；④ **修掉原版一个真实缺陷**：`WithHeader` 用 `TryAddWithoutValidation`，同名头已存在时会**静默失败** —— 先 `WithContent` 再 `WithHeader("Content-Type", …)` 实际根本没生效；UMango 改为先 `Remove` 再 `Add`（见 §2.K「已修 PCL 原版缺陷」说明）|
| P1-3 | **DNS 查询 + 加权竞速** | **部分** | **低** | `IO/Net/Dns/`（3 文件） | 否 —— **加权竞速算法已迁**：`src/UMango.Core/Network/WeightedRacer.cs`（`WeightedRacer<TClient,TResult>` + `WeightedCandidate` + `RaceOutcome`）。**不引入 `Ae.Dns.Client`** —— 那个 NuGet 包只服务于 PCL 的「按 DNS SRV 记录选 IP」用法，UMango 把它用到**包源镜像竞速**上（同一包在多个镜像源比谁先回），因此不需要 DNS 协议查询本身。⚠️ 相对原版的**实质差异**：① 原版 `WeightedDnsRacerClient` 把「按权重排序 → 逐个试 → 记录输家」硬编码在 DNS 客户端里，UMango 拆成**与协议无关的泛型竞速器**（候选 + 权重 + 结果），DNS/HTTP 都能套；② 修掉原版**取消竞态**：`WeightedDnsRacerClient` 在胜者产生后取消输家，但取消与「输家恰好拿到许可并开始执行」之间没有闸门，会出现**输家在取消生效后仍然执行**；UMango 用 `SemaphoreSlim` + 取消令牌做闸门，保证「未开始的候选绝不会被执行」；③ 修掉原版**权重为 0 的候选仍会被尝试**的缺陷。 |
| P1-4 | **线程/并发原语**（防抖、手动复位事件、限流任务池） | **已完成** | **低** | `Utils/Threading/`（6 文件） | 否 —— `src/UMango.Core/Threading/`：`AsyncDebounce.cs`（防抖）、`Throttle.cs`（节流）、`LimitedConcurrencyTaskScheduler.cs`（限流调度）、`AsyncManualResetEvent.cs`、`AsyncCountResetEvent.cs`、`LimitedTaskPool.cs` + `DualThreadPool.cs`。⚠️ 相对原版的**实质差异**：① 原版 `AsyncManualResetEvent` 同时维护 TCS 与 `ManualResetEventSlim` 两套状态（两处不同步就会出现「异步能过、同步卡死」的鬼状态），UMango 改为**单一 TCS 真相来源**；② 原版 `LimitedConcurrencyLevelTaskScheduler` 的 `MaxConcurrency` 返回的是**剩余可用数**而非上限、`Running` 用 `Release(0)` 反查会**多放行**一个任务，UMango 改为记录配置值；③ 限流调度器在取得槽位后**补了一次取消检查** —— `SemaphoreSlim.WaitAsync(ct)` 在「许可被释放」与「取消生效」同时到达时会授予许可（实测同时到达时约 82% 会授予），不补这一刀会出现「点了取消，包管理器还是跑起来了」 |
| P1-5 | **哈希算法族** | **已完成** | **低** | `Utils/Hash/`（7 文件） | 否 —— `src/UMango.Core/Hash/IHashProvider.cs` + `CryptographicHashProviders.cs`（MD5/SHA1/SHA256/SHA512）+ `MurmurHash2Provider.cs` + `HashCache.cs`。⚠️ 相对原版的**实质差异**：① **缓存后端从 Microsoft.Data.Sqlite 换成 LiteDB 5.0.21（MIT）** —— UMango 已因数据库服务引入 LiteDB，少引一个原生 SQLite 依赖；② **修掉原版跨实例串扰**：原版把"正在计算的哈希"挂在**静态**字典上、key 不含数据库路径，不同 dbPath 的实例会共用同一次计算、结果只写进先到的那个库，导致第二个实例的缓存永远不命中（UMango 改为实例字段）；③ **修掉原版 MurmurHash2 长度声明与实际输出对不上**（声明 8 字节、实际只算 32 位变体输出 4 字节），UMango 改用 64 位变体，输出 8 字节与声明一致；④ 原版 `ComputeHash(Stream)` 把整个文件读进内存，UMango 改为分块流式计算，内存占用恒定；⑤ 原版各 Provider 的 `Length` 语义自相矛盾（MD5 写 32 是十六进制字符数、MurmurHash2 写 8 是字节数），UMango 统一为**字节数** |
| P1-6 | **编码检测与转换** | **已完成** | **低** | `Utils/Codecs/`（3 文件） | 否 —— `src/UMango.Core/Utils/Codecs/{Encodings,EncodingDetector,EncodingUtils}.cs`；旧的 `src/UMango.Core/Utils/EncodingHelper.cs` 改为转发。⚠️ 相对原版的**实质差异**：① **修掉原版的致命缺陷**：`_IsValidUtf8` 从没真的从流里读数据 —— 它解码的是**没被填过的全零 buffer**，于是**任何文件都被判成合法 UTF-8**，GBK/Shift-JIS 全部误判（原版表现为"打开 GBK 文件全是乱码且查不出原因"）；② **修掉原版 UTF-32LE 被误判成 UTF-16LE**（FF FE 00 00 以 FF FE 开头，判断顺序错了）；③ **修掉原版的运行期崩溃**：原版 `Encoding.GetEncoding("GB18030")` 是静态字段初始化，.NET Core 默认不提供该代码页，PCL 靠启动流程里 `Encoding.RegisterProvider` 才不炸 —— 意味着"启动前"任何一处访问都会抛 `ArgumentException`。UMango 改成惰性注册 + 失败时退回 Latin1（不丢字节）；④ BOM 处理：原版解码 UTF-8 时会留下 U+FEFF 零宽字符，UMango 显式跳过 BOM 字节。 |
| P1-7 | **验证器族** | **已完成** | **低** | `Utils/Validate/`（12 文件，基于 FluentValidation） | 否 —— `FluentValidation` 12.1.1 是 Apache-2.0，零传递依赖。迁移时把硬编码中文换成资源键、MC 字符开关默认关 |

### 4.3 P2 —— 中价值或形态需重新设计

| # | 功能 | 现状 | 迁移成本 | 涉及 PCL.Core 模块 | Windows 依赖 |
|---|------|------|---------|------------------|-------------|
| P2-1 | **多语言 i18n** | **已完成（本地化机制 + zh-CN/en-US 双语，392/391 键）** | **高** | `App/Localization/`（`Lang.cs` + 服务 + 字体配置 + 7 语言 XAML，实语言各 3,583 行） | 否（用 WPF 资源字典，与 PCL 同路线） |
| P2-2 | **动画系统补齐**（帧动画/关键帧/组合/时钟抽象/`IAnimatable`） | **已完成**（核心能力齐备） | **中**（⚠️ 由「高」下调） | `UI/Animation/`（**87 文件 / 2,742 行**） | **部分**（仅 14/87 文件触及 WPF；UMango 的 WPF 侧与 Avalonia 侧各提供一份 `IUIAccessProvider` 实现，Core 引擎对平台无知）。**已迁入 Core（净10.0）**：帧驱动器 + 关键帧 + 顺序/并行组 + 时钟抽象 + `IAnimatable` + 值处理器（含算术运算）+ 绝对/相对值 + `ActionAnimation` + 复合缓动 + 按名取消。**已补迁**：`UIAccessProvider`（`src/UMango.Core/Animation/UIAccess/`：`IUIAccessProvider` + `ImmediateUIAccessProvider`，**WPF 与 Avalonia 两个外壳共用**）、帧抽象（`src/UMango.Core/Animation/Frames/`：`IAnimationFrame` + `ActionAnimationFrame` + `FromToAnimationFrame<T>`）、`IUIClock` 标记接口（`src/UMango.Core/Animation/Clock/IUIClock.cs`）。**仍未迁**：`WinMMClock`（Windows 专有时钟，用 `StopwatchClock` 替代）、`Matrix/NColor/NRotateTransform/NScaleTransform/Point/Thickness` 六种专用 FromTo 动画（UMango 用通用 `FromToAnimation` + 值处理器覆盖同类需求）。**另已补齐 PCL 控件层的动效语义**（受限许可目录，只借鉴设计与公开数值、独立重写）：`TweenFactory`/`TweenPlanner`/`TweenChannelAnimation`（同通道并行叠加）/`DelayedAnimation`/`OffsetChainAnimation`/`StaggerPlanner`（子项级联）/`AnimTreeCollector`（控件树收集）/`PclTweenPresets`（PCL 原版数值）；App 侧 `WpfTweenPlayer`/`WpfTweenBinder`/`WpfAnimPolicy`/`WpfStaggerHost` 已接进 `MainWindow.PlayContentAnimation`（整页走 PCL 原版 ModAnimation API，子项走 StaggerPlanner 级联） |
| P2-3 | **系统信息与硬件** | **已完成**（跨平台部分） | **中** | `Utils/OS/`（14 文件） | **部分是**（注册表、`NtInterop`、`KernelInterop`、`WindowInterop`、`AumidHelper`）—— 只迁了 4 个真正跨平台的文件（`SystemInfoEx` / `SystemPaths` / `NetworkUtils` / `HardwareInfo`），其余 10 个标注「对 UMango 无落点」。`HardwareInfo` 的 WMI 部分重写为 `/proc/cpuinfo`（Linux）+ `sysctl`（macOS），并把原版「静态可变全局字段」改成不可变 `HardwareSnapshot` + 显式 `Collect()`（原版在类型首次访问时抓硬件，多线程下可能读到半初始化值） |
| P2-4 | IoC / 生命周期 | **已完成**（生命周期编排部分） | **中** | `App/IoC/`（14 文件） | 否 —— `src/UMango.Core/App/Lifecycle/LifecycleManager.cs` 迁入**生命周期编排**这一层：阶段状态机（`LifecycleState` 8 个通用阶段，PCL 的 Minecraft 专属阶段已剔除）、同阶段按优先级降序启动、**同步服务先于异步服务启动**、停止时按**启动顺序的逆序**、停止失败不阻断其余服务释放、`LifecycleContext.RequestExit()` 退出请求。**不迁 PCL 的依赖注入部分**（`[DependencyInjectionPoint]` + 源生成器 + 反射扫描程序集）—— UMango 已有手写的 `AppServices` 静态门面在做按需创建，DI 容器会与之重复；且反射扫描在单测里会捞到测试程序集的类型，改为**显式注册**。15 个测试，注入矩阵 4 项全红后还原。 |
| P2-5 | 内部事件总线 | **已完成** | **低** | `App/EventBus/`（4 文件 290 行） | 否 —— `src/UMango.Core/Events/EventBus.cs` 补齐 PCL 的**通道名路由**（`AddChannel`/`RemoveChannel`/`Channels`）、**异步发布**（`PublishAsync(channel, data)`）、**可赋值性匹配**（订阅 `object` 能收到 `Foo`）、**弱引用持有者**（`IEventHandler<T>`，持有者被回收后不阻止 GC 且订阅条目在下次发布时清理）。⚠️ 相对 PCL 的**实质差异**：保留**实例**语义而非静态单例（静态总线会让测试之间互相污染）。向不存在的通道发布**抛 `InvalidOperationException`**（PCL 一致：频道写错应当被看见而不是静默吞掉） |
| P2-6 | 单实例运行 | **已完成**（commit `9715e65`，命名互斥锁 + 命名管道激活） | **低** | `App/Essentials/SingleInstanceService.cs` | **是**（命名互斥体/Win32） |
| P2-7 | 命令行参数解析 | **已完成**（`src/UMango.Core/Cli/` 迁入 PCL `App/Cli/` 7 文件 502 行 + `src/UMango.Core/App/UmangoCommandLine.cs` 子命令表 + `CliCommandService` 六个子命令 install/uninstall/upgrade/search/list/source/help） | **低** | `App/Cli/`（7 文件 502 行） | 否 —— ⚠️ 相对原版的**实质差异**：① **保留词分流**：`update` / `update_finished` / `update_failed` 是自更新助手协议，`UmangoCommandLine.TryParse` 一律返回 false 让给 `UpdateCommandLine`（助手协议的位置参数里含字面量 `source`，与 UMango 自己的 `source` 子命令同名，不在入口分流会被吃成子命令）；② 参数类型按值文本推断 Bool/Decimal/Text 三类（PCL 原版行为）；③ **无位置参数语义**的裸 token 归入 `Positional` 列表供各子命令自行取用；④ `Program.Main` 在 `new App()` 之前分流：带子命令时**只打印结果、不开主窗口、不抢单实例锁** |
| P2-8 | 字符串/集合/任务扩展方法集 | **已完成**（`Utils/Extensions.cs` 11 个扩展：IsBlank/Truncate/SafeSubstring/Between/AfterFirst/BeforeLast/AtOrDefault/ToHashSet/IgnoreResult/WithTimeout） | **低** | `Utils/Exts/`（8 文件） | 否 |
| P2-9 | 二进制差分（增量更新） | **已完成** —— `src/UMango.Core/Diff/BinaryDelta.cs`（UMD2 容器：公共前缀/后缀引用旧文件 + 差分块 + 额外块 + seek 指令），`BinaryDiff` 门面改为走增量引擎并兼容旧 deflate 补丁 | **中** | `Utils/Diff/`（4 文件） | 否 —— ⚠️ **相对原版的实质差异**：PCL 的 `BsDiff` **只有应用侧**（`MakeAsync` 直接抛 `NotImplementedException`，原文件第 165 行），且其 bsdiff 三元组指令集无法引用旧文件本身，「文件尾部完全相同」这种最常见的更新场景也得把整段尾巴塞进 extra 块；UMango 补齐了生成侧并扩展指令集 |
| P2-10 | 加密算法族 + 密钥保护 | **已完成** | **中** | `Utils/Encryption/`（5 文件）、`Utils/Secret/`（3 文件）、`App/Secrets.cs` | **部分是**（DPAPI/CNG） |
| P2-11 | 主题体系补齐（LAB 色彩、色调档、系统主题跟随） | **已完成**（等价实现，见主表「主题/配色」行的差异说明） | **中** | `UI/Theme/`（5 文件）、`UI/NColor.cs` | **部分**（原版 `SystemThemeHelper` 依赖注册表，UMango 改为跨平台探测，注册表读取只在 Windows 分支） |
| P2-12 | 统一对话框/提示封装 | **已完成** | **低** | `UI/MsgBoxWrapper.cs`、`SystemDialogs.cs`、`UI/HintWrapper.cs` | 否 —— 三件套全部落地：`src/UMango.Core/Pcl/UI/HintWrapper.cs`（提示条）+ `src/UMango.Core/Pcl/UI/MsgBoxWrapper.cs`（消息框，Apache-2.0 逐字采用）+ `src/UMango.App/Services/SystemDialogs.cs`（文件/文件夹选择，参数计算为纯函数、显示走 `IFileDialogHost` 抽象以便单测）。下载器页「保存到」新增「浏览…」按钮（`DownloaderView.xaml.cs`）调用系统文件夹选择对话框 |
| P2-13 | 系统 Toast 通知 | **已完成** | **中** | `src/UMango.Core/UI/ToastNotification.cs` + `Utils/WinRT/**`（10 文件）+ `Utils/OS/AumidHelper.cs`（Apache-2.0 逐字采用）+ `src/UMango.App/Services/ToastBridge.cs`（自写桥接，7 测试） | **是**（WinRT 专用，net10.0-windows 支） |
| P2-14 | SVG 图标运行时渲染 | **已完成** | **中** | `UI/Controls/SvgIcon/`（11 文件 1,445 行） | **是**（WPF 几何）—— `src/UMango.Core/UI/Svg/`（7 文件）做**平台中立的解析与路径模型**（`SvgSourceParser`/`SvgDocumentParser`/`SvgPathTokenizer`/`SvgPathBuilder`/`SvgStyle`/`SvgIconKey`/`SvgPathModel`），App 层 `src/UMango.App/Controls/SvgIcon.cs` 负责渲染。⚠️ 相对原版的**实质差异**：① **内联 `style` 属性优先于直接属性**（PCL 原版反过来，与 CSS 层叠相反）；② 图标标识**显式拒绝绝对路径与空图标名**（原版 `Trim('/')` 会把 `material/` 静默当成 `material`，写错标识不报错而是悄悄显示成另一个图标）；③ 原版 `ScRgbMapping` 半成品不迁（UMango 用标准 sRGB） |
| P2-15 | 模糊/特效 | **已完成**（部分，见主表「模糊/特效」行的差异说明） | **中** | `UI/Effects/`（4 文件） | **是**（`ShaderEffect` + 预编译 `.ps` 着色器，**不需要 P/Invoke**）—— 着色器需在 Windows 上用 FXC 预编译（PCL 原版附 `CompileShader.bat`），但**已编译产物可直接复用**，构建链无需 FXC |
| P2-16 | 灰度（哀悼日）模式 | **已完成** | 低 | `UI/GrayProfile.cs`、`GrayProfileConfig.cs` | **是**（WPF 画笔改写，非 Effect） |
| P2-17 | 位图/图像处理 | **已完成** | **低** | `Modules/Base/MyBitmap.cs`、`UI/ImageLoaderHelper.cs`、`UI/Media/ImageConverter.cs` | 部分 —— ⚠️ **两个原版文件都不整体迁**：`ImageLoaderHelper.cs` 直接操作 WPF 的 `Image` 控件、`ImageConverter.cs` 靠 WIC 解码 WebP（Windows 独有）。UMango 改为在 Core 实现**与平台无关的字节级探测**（`src/UMango.Core/Utils/ImageData.cs`）：识别 PNG/JPEG/GIF/BMP/WebP/ICO/SVG、读出内在尺寸、识别动图、base64 与 data URI 解码（含缺失填充与折行容忍）。**为什么选探测而不是转换**：包管理器搜索结果带的图标是远端不可信数据，先判断「是不是能显示的图」比「先解码再说」安全；且转换需要 WIC，macOS/Linux 上没有对应能力。`MyBitmap.cs` 位于受限许可目录且只服务 MC 皮肤渲染，**不迁**（见主表「位图/图像处理」行的说明） |
| P2-18 | 语义化版本公共类型 | **已完成** | **低** | `Utils/SemVer.cs`、`Utils/VersionRange.cs` | 否 —— ⚠️ 相对原版的**实质差异**：① **预发布标识符按 semver.org 规范逐段比较**（PCL 原版用整串字典序，会把 `1.0.0-alpha.10` 判成小于 `1.0.0-alpha.2`）；② 纯数字标识符按数值比较且**优先级低于**非数字标识符；③ `VersionRange` 的 caret 范围在**主版本为 0** 时按 npm 约定收紧（`^0.2.3` → `<0.3.0`，`^0.0.3` → `<0.0.4`），PCL/初版用 `<(1+1).0.0` 会把整个 0.x 放行；④ 通配符范围**只约束通配符之前的段**（初版只比主版本，`1.2.x` 会放行 `1.3.9`）；⑤ 精确匹配按三段号+预发布比较，**忽略构建元数据** |
| P2-19 | 相似度搜索 | **已完成** —— `src/UMango.Core/Utils/SimilaritySearch.cs`（多字段加权）+ `src/UMango.Core/PackageManagers/PackageSearchRanking.cs`（包域映射） | **低** | `Utils/SimilaritySearch.cs` | 否 —— 与 PCL 的**实质差异**：① 分数归一化到 **0~1**（PCL 原版无上界，`1.4^(3+L)-3.6` 指数加权可到 10+），PCL 的 0.1/0.2/0.35 阈值因此不能照搬；② 只对**有命中的**字段求加权平均（PCL 把不命中字段也算进分母，「描述命中、名字不中」会被稀释到 0.09）；③ 位置奖励**不进分母**（PCL 用「查询对自己」当分母，那一对必拿满分位置奖励，真实匹配被平白除以约 6.7）；④ 源长度惩罚用 0.25 次幂而非平方根（平方根把长描述压到 0.56，与短包名不可比）；⑤ 归一化用 `ToLowerInvariant` + 去**所有**空白（PCL 只去 ASCII 空格且 `ToLower()` 区域敏感，土耳其语环境下 "I" 匹配不上 "i"）；⑥ 空白查询返回空列表（PCL 返回全部条目）；⑦ 补 null 防御。`DownloadViewModel` 的搜索结果排序已由「只看包名 + Levenshtein≥0.6」改用它 |
| P2-20 | 自定义事件总线（XAML 附加事件） | **已完成**（安全再设计） | **中** | `Modules/Event/`（6 文件 836 行） | 否 —— ⚠️ 原版位于**自定义许可目录**（`Plain Craft Launcher 2/`），**不逐字复制**，只借鉴架构。`src/UMango.Core/Customization/CustomEventService.cs` 做平台中立的「类型 → 动作」分发（白名单注册表、`ICustomEventPolicy` 安全策略、`HttpUrlOnlyPolicy` 只放行 http/https）；`src/UMango.App/Services/CustomEventActions.cs` 提供 7 个动作实现，WPF 能力（进程/剪贴板/对话框）经 `IShellCapabilities` 注入因而可测。**与原版三处实质差异**：① **不含 XAML 附加属性**（原版 `CustomEventService` 用 `RegisterAttached` 把事件绑到任意 FrameworkElement，依赖远程 XAML 渲染 —— 见 P2-22 结论，UMango 不加载远程 XAML，故不需要）；② 10 个 MC 专属类型（LaunchGame/ImportModpack/InstallModpack/DailyFortune/ClearTrash 等）不迁；③ 安全模型更严：原版只有「高危设置黑名单 + 执行前确认框」，UMango 改为**白名单注册表 + 可插拔策略 + 协议校验**。测试：Core 30 + App 21。 |
| P2-21 | **启动器自定义主页**（远程 XAML 渲染 + 热补丁 + 内置小提示） | **已完成**（安全再设计，见 P2-22） | **中** | `Pages/PageLaunch/PageLaunchRight.xaml.cs`（898 行，⚠️ **位于自定义许可目录**，只能借鉴设计、不能复制代码） | **是**（WPF `LoadContent`） —— ✅ `src/UMango.Core/UI/Homepage/`（`HomepageManifest.cs` 解析器 + `HomepageService.cs` 存储/热更新；`src/UMango.App/Services/HomepageHost.cs` 动作白名单 + 跳转；`src/UMango.App/Views/HomepageCardViewModel.cs` 卡片渲染）。**安全再设计**：不加载/执行远程 XAML，改为**声明式 JSON 清单**（标题/副标题/正文/图标名/http(s) 链接/白名单动作名/标签）；链接只放行 http/https，动作名必须**事先注册**（解析阶段就丢弃未注册动作），远程内容无法引入新能力。清单落盘在用户配置目录 `UMango/homepage.json`，`FileSystemWatcher` 热更新。 |
| P2-22 | ↳ 该功能的**安全再设计**（**不建议照搬**） | **已完成**（改为声明式 JSON 清单，不加载远程 XAML） | **中** | 在进程内加载并执行**远程 XAML** 等于开放任意代码执行面；PCL-CE 用 `XamlEventSanitizer` 缓解，但这是危险模式。若要"可自定义主页"，应改为**受限数据格式**（JSON 描述 + 内置模板）而非可执行 XAML | **是** —— ✅ `src/UMango.Core/UI/Homepage/`（`HomepageManifest.cs` 解析器 + `HomepageService.cs` 存储/热更新；`src/UMango.App/Services/HomepageHost.cs` 动作白名单 + 跳转；`src/UMango.App/Views/HomepageCardViewModel.cs` 卡片渲染）。**安全再设计**：不加载/执行远程 XAML，改为**声明式 JSON 清单**（标题/副标题/正文/图标名/http(s) 链接/白名单动作名/标签）；链接只放行 http/https，动作名必须**事先注册**（解析阶段就丢弃未注册动作），远程内容无法引入新能力。清单落盘在用户配置目录 `UMango/homepage.json`，`FileSystemWatcher` 热更新。 |

### 4.4 P3 —— 低价值 / 对 UMango 无意义

| # | 功能 | 现状 | 理由 |
|---|------|------|------|
| P3-1 | 主页新闻（官网公告） | **已完成** | 数据源是 **PCL 官网 API**，UMango 用不上（需换源，等于新做）—— 机制照搬、源可配置：`src/UMango.Core/Announcements/`（抓取 / 已读记录 / 动作白名单）+ `src/UMango.App/Services/AnnouncementHost.cs` + 主页「公告」区。**四处实质差异**：① 源可配置，留空即不抓取（PCL 写死自家 API）；② 按钮动作走**白名单**（PCL 靠 `XamlEventSanitizer` 黑名单过滤远程 XAML —— 白名单更可靠，且 PCL 那是远程 XAML，UMango 不执行远程 XAML）；③ 抓取失败只记日志不弹窗（公告不是关键路径）；④ 动作实现挂在 Core 的描述符上，注册表是唯一真相 |
| P3-2 | 音乐播放器 | **已完成** | 见主表「音乐播放器」行 |
| P3-3 | 视频/动态背景 | 未做 | 与包管理器无关 |
| P3-4 | 内嵌 WebServer | **已完成** | PCL-CE 的 `Modules/ModWebServer.cs`(345) 在**受限许可目录**，且它本质是 MC 登录 OAuth 回调服务器 —— UMango 不做 MC 登录。但**底层能力**（通用本地 HTTP 服务端）在 Apache-2.0 层：`PCL.Core/IO/Net/Http/HttpServer.cs` + `HttpRouteResponse.cs` → `src/UMango.Core/Network/Http/Server/`（路由表 = 方法+路径→处理器、精确匹配优先于通配、异常统一转 500、响应结构含状态码/MIME/字符集/重定向/Cookie/流）。**七处修掉的真实缺陷**（原版在 macOS 上一个地址都起不来）：① IPv6 前缀写成 `http://::1:8080/`（非法 URI）；② 即使加方括号，macOS 上 HttpListener 仍绑不上 IPv6 回环 —— 改为**逐地址尝试、绑不上的跳过**并暴露 `SkippedAddresses`；③ 请求处理挂取消令牌 → 停止时请求被静默丢弃、客户端等到超时；④ 端口"先占后放"选出来有竞态；⑤ 重复 `Start()` 炸且 `Init()` 会重跑（路由表为空才调的判断）；⑥ `Stop()` 后无法重启（HttpListener 停了就废）；⑦ 处理器抛异常时先写 `StatusCode=500` 再 `Pour()` → **被 Pour 的默认 200 覆盖**，客户端收到 200 而非 500。另修 `ContentType: "application/json, charset=utf-8"` —— HttpListener 解析带逗号的 ContentType 失败，结果**响应里没有 Content-Type 头**；字符集改由 `ContentEncoding` 单独声明。16 个测试，注入矩阵 3 项全红后还原。 |
| P3-5 | 请求签名 | **已完成** | PCL-CE 的 `Modules/Network/Http/RequestSigning.cs`(37 行) 在**受限许可目录**且与 CurseForge 官方服务绑定（内置他人 API Key、强制 HTTP/2）→ **不逐字复制**，按同一语义重新实现：`src/UMango.Core/Network/Http/RequestSigning.cs` —— `IRequestSigner` 可插拔签名器接口 + `HmacRequestSigner`（HMAC-SHA256，签名串 = 方法\n路径+查询\n时间戳\n随机串\nsha256(请求体)，头名 `X-UMango-Timestamp/Nonce/Signature`，**UMango 自定、不与任何厂商私有协议冲突**）+ `RequestSigning` 静态工具（CDN 标记展开、默认头注入、签名头写回）。**修掉的原版缺陷**：① 原版 `EndsWithF("{CDN}")` 只在标记位于**结尾**时处理，中间的标记会原样发给对端 —— 改为任意位置展开；② 原版 `Headers.Add` 在请求已带同名头时**抛异常** —— 改为幂等覆盖；③ 原版 `TryAddWithoutValidation` 允许**多值**，重复调用会攒出多个 User-Agent；④ 原版强制 `HttpVersion.Version20` + `RequestVersionOrLower`（针对特定 CDN 的兼容 hack，会让所有请求降级）—— 改为不动版本策略；⑤ 原版把 CurseForge API Key 写死在配置里 —— UMango 不内置任何厂商密钥，厂商鉴权一律走可插拔签名器。22 个测试，注入矩阵 4 项全红后还原。 |
| P3-6 | 进程间管道通信 | **已完成** | `PCL.Core/IO/PipeComm.cs`（Apache-2.0，170 行）—— `src/UMango.Core/Ipc/PipeServer.cs`：行协议（UTF-8，每行一条消息）+ 客户端自报进程号 + 会话计数 + 运行期日志 + 出错不杀服务端。**三处实质差异**：① 原版用 `KernelInterop.GetNamedPipeClientProcessId` 读管道句柄拿客户端进程号（Win32 专属），改为**客户端在协议里自报**（自报可伪造，只作日志与弱校验）；② 原版跑在专用线程上，改为 **async**；③ 原版的私有终止符协议（ESC）改为**行协议**。另加 `PipeServer.BuildPipeName`：**macOS/Linux 上 .NET 把命名管道映射成 Unix domain socket，完整路径必须 ≤ 104 字符**，超长名字自动用哈希压缩（原版无此概念，是跨平台移植时新发现的约束）。13 个测试，注入矩阵 4 项全红后还原。 |
| P3-7 | 反馈页（标签化提交） | **已完成** | — |
| P3-8 | 日志查看页 | ✅ **已完成**（同上 commit `f2cc0b9`，`Views/LogView.xaml`） | 与 P0-3 同批交付，并附「导出诊断包」 |
| P3-9 | 启动器杂项设置 | **部分**（已逐项确认，见下） | PCL 的 `PageSetupLauncherMisc.xaml.cs`（322 行）共 9 组设置，逐项核对后：**UMango 该有的 1 组已迁（网络代理）**，其余 8 组各有明确不迁理由 |

### 4.5 Windows 依赖实测汇总（迁移成本的硬约束）

上表"Windows 依赖"列的依据如下（均为实测命中，非推断）：

| 范围 | 实测结果 | 证据 |
|------|---------|------|
| **`Plain Craft Launcher 2/Pages/`（135 文件）** | **无注册表 / 无 WinRT / 无 P/Invoke** | 三关键字全目录 grep 命中数为 0 |
| ├ 自删逻辑 | 调 `cmd.exe` + `rmdir /s /q` | `PageSetupLauncherMisc.xaml.cs:291-298`（`FileName = "cmd.exe"`, `CreateNoWindow = true`） |
| ├ 创建快捷方式 | 生成 `.lnk` | `PageToolsTest.xaml.cs:471-481` |
| └ 位图合成（皮肤） | 依赖 `System.Drawing` | `MySkin.xaml.cs:1-3` |
| **`Plain Craft Launcher 2/Modules/`（92 文件）** | 实测 **11 处 `DllImport`** + **3 处 `Registry.`** | `DllImport` 分布：`ModMain.cs` 3 处（`:889 FindWindowA`、`:892 SetForegroundWindow`、`:895 PostMessageA`）、`Minecraft/ModWatcher.cs` 6 处（`:682 EnumWindows`、`:685`、`:688`、`:691 SetWindowText`、`:694 ShowWindow`、`:697`）、`ModLink.cs` 1 处（`:127 iphlpapi GetExtendedTcpTable`，PID 反查 TCP 端口）、`Base/ModBase.cs` 1 处（`:1557 kernel32 GetShortPathNameA`，启动 MC 前转 8.3 短路径）。`Registry.` **仅 3 处且全在** `ModMain.cs:912/923/930`（写 `HKCU` 的 GPU 高性能首选项）。另有 `cmd.exe`（`ModLaunch.cs:2441`/`:2475` 自定义命令、`ModBase.cs:1562` 取短路径）、`taskkill`（`ModWatcher.cs:771`）、`javaw`（`ModLaunch.cs:2511`）、GDI+ 取 DPI（`ModBase.cs:3146 Graphics.FromHwnd`）、`explorer.exe`（`ModBase.cs:2848`） |
| **`PCL.Core/`（544 文件）** | `UI/` 129 文件中 40 个用 WPF 类型；`Utils/` 92 文件中 6 个 `DllImport` + 3 个 Win32/WinRT | 见 5.3；`Minecraft/` 108 文件**零 WPF 命中**（纯逻辑，但 TFM 仍是 Windows） |

**⚠️ 一个决定性的发现（比"Windows 依赖"更硬）**：调查过程中 UMango 落地了 commit `2f900b1`
（"Core 去掉残留的 UseWPF 并改为多目标：平台中立从'声明'变成'编译期证明'"），把 `UMango.Core` 改成：

```xml
<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>   <!-- 刻意不设 UseWPF -->
```

而 `PCL.Core.csproj` 是：

```xml
<TargetFramework>net10.0-windows</TargetFramework>
<UseWPF>true</UseWPF>
```

**两者不再"TFM 一致"，而是被一道编译期闸门隔开**：

- `net10.0` 这一支的存在，按该 commit 的原文，是"**一旦有人往 Core 里塞 `System.Windows` / `Registry` /
  `DllImport(win32)` 之类，net10.0 这一支立刻编译失败**"——即它是一道**主动设置的反退化闸门**。
- 因此 **PCL.Core 的代码无法进入 `UMango.Core` 的 `net10.0` 分支**：
  即便某个文件本身"零 WPF 命中"（如 `Minecraft/` 108 文件），只要它 `using System.Windows` 就编不过；
  而 `PCL.Core` 是**程序集级** `UseWPF=true` + `net10.0-windows`，直接 `ProjectReference` 会传递
  `Microsoft.WindowsDesktop.App` 引用，把 Core 重新绑回 Windows。
- **可行的方式只有"逐文件复制、不要工程引用"，且复制进来的文件必须真的零 Windows 依赖。**
  这正是"迁移成本"里最容易被低估的一项：**它不是抄代码，而是逐文件做平台中立性审查。**

**这同时回答了"红线为什么存在"**：`README.md` 第 3 行写"**跨平台**包管理器图形界面"，
commit `2f900b1` 提到目标是"**Avalonia 跨平台重建**"。
所以「后端完全剥离 MC 逻辑」的真实动因**不只是产品定位，更是跨平台** ——
PCL-CE 是 **Windows 专用 WPF** 应用（`net10.0-windows` + `UseWPF`），
把它的任何 UI/MC 层搬进来，都会把 UMango 从"将来能跑在 macOS/Linux"退回到"只能跑 Windows"。

**NuGet 依赖的二次冲突**：PCL.Core 拖进的包很重（`Sentry`、`LiteDB`、`Microsoft.Data.Sqlite`、
`SQLitePCLRaw.bundle_e_sqlite3`、`fNbt`、`Stun.Net`、`Polly`、`FluentValidation`、`SharpZipLib`、
`YamlDotNet`、`Ae.Dns.Client`、`PlainToolkit.CngProtectedData` 等），
与 `docs/DESIGN_UI_DOTNET.md` 第 0 节第 3 条「**不得引入新的 NuGet 依赖**」冲突。
UMango.Core 目前**只有一个依赖**（`CommunityToolkit.Mvvm 8.4.2`）。
**采用 PCL.Core 的任何一块，都要先由项目所有者重新裁决这条约束，并逐个检查该依赖是否支持 `net10.0`（非 Windows）。**

### 4.6 PCL.Core 的可移植性分层（决定"能不能进 Core"）

在 4.5 的闸门之下，逐目录核查 `PCL.Core` 的平台耦合，得到三层：

**第 1 层：可进 `UMango.Core`（`net10.0` 支）—— 目录内零 WPF 命中**

| 目录 | 说明 |
|------|------|
| `App/EventBus/` | 内部事件总线（4 文件） |
| `App/Tasks/` | 任务中心 —— **UMango 已采用**（Apache-2.0，逐文件标注） |
| `App/Cli/` | 命令行参数解析（7 文件） |
| `App/Configuration/`（**28 文件 / 2,478 行**：根 14 / 1,283 + `Storage/` 14 / 1,195） | **整个目录零 WPF 命中**（对 `System.Windows`/`Microsoft.Win32`/`DllImport`/`System.Drawing`/`IsWindows` 全 grep 均零命中）—— 最值得优先迁移的一块。唯一要注意：`EncryptedFileConfigStorage` 的加密后端走 `Utils/Secret/EncryptHelper`（Windows DPAPI/CNG），跨平台需替换 |
| `App/IoC/` | 13 个文件中仅 1 个触及 WPF |
| `Utils/Threading/`、`Utils/Encryption/`、`Utils/Hash/`、`Utils/Codecs/`、`Utils/Diff/` | 纯算法工具 |
| `IO/Net/Http/` | 15 个文件中 13 个无 WPF 命中 |
| `IO/Storage/Cache/` | SQLite 缓存（取决于 NuGet 裁决） |
| `UI/Animation/Easings/` | **39 文件 / 660 行、0 个 `System.Windows` 命中** —— 纯数学，最干净的一块 |
| `UI/Animation/` 的**其余 73 个文件** | ⚠️ **本项是复核后的重要上调**：整个 `UI/Animation/`（87 文件 / 2,742 行）**只有 14 个文件触及 `System.Windows`**，且实测 `DoubleAnimation` / `System.Windows.Media.Animation` / `Storyboard` / `Timeline` **命中数全为 0** —— 它是**自研帧驱动引擎**（`IAnimation.ComputeNextFrame` + `IClock.Tick`），**没有用 WPF 的动画体系**。抽象层显式为跨框架设计：`IAnimatable.cs:5`（`object? GetValue()` / `void SetValue(object)`，零框架依赖）、`IClock.cs`、`IUIAccessProvider.cs`（注释原文「跨框架的 UI 线程访问接口」）。13 个 WPF 触点**全部是实现类而非抽象层**（`WpfAnimatable`、`WpfCompositionTargetRenderingClock`、`WpfUIAccessProvider`、`AnimationBase : DependencyObject`、`RunAnimationAction : TriggerAction<DependencyObject>`、`AnimationGroup` / `AnimationExtensions` / `IAnimationFrame`、`Matrix`/`Point`/`Thickness` 三组值处理器与动画类型）。**因为 UMango 也是 WPF，这些实现类可直接工作** |

**第 2 层：可借鉴设计，但不宜整体迁移**

- `Minecraft/IdentityModel/`（22 文件 1,613 行）里约 **990 行**是通用的 OAuth2 / OpenID / PKCE / JWT 实现。
  技术上通用，但**位于 MC 命名空间下**，且 4.5 的闸门要求逐文件审查（`JsonWebToken.cs`、`OpenIdClient.cs` 等确实不依赖 WPF）。
  按 3.7 的结论：应当作独立需求评估，**不要挂在"功能对照迁移"名下**。

**第 2.5 层：`UI/Animation/` 的 14 个 WPF 实现类 —— 不能进 Core 的 `net10.0` 支，但因 UMango 是 WPF，可用于 App**

实测触及 `System.Windows` 的 14 个文件（可逐一对照）：`Animatable/WpfAnimatable.cs`、`Clock/WpfCompositionTargetRenderingClock.cs`、`Core/{AnimationBase,AnimationExtensions,AnimationGroup,IAnimationFrame,RunAnimation}.cs`、`MatrixFromToAnimation.cs`、`PointFromToAnimation.cs`、`ThicknessFromToAnimation.cs`、`UIAccessProvider/WpfUIAccessProvider.cs`、`ValueProcessor/{Matrix,Point,Thickness}ValueProcessor.cs`。
另 `Clock/WinMMClock.cs` 走 `winmm.dll`（Windows 专属；跨平台应换用 `StopwatchClock.cs`，后者纯 BCL）。

**结论**：动画系统可整体迁入 `UMango.App`（WPF 侧），而**其抽象层与 Easings 还能进 `UMango.Core` 的 `net10.0` 支** ——
这正是 P2-2 成本由「高」下调为「中」的依据。

**第 3 层：硬阻塞项 —— 明确不能进 Core**

| 目录 / 文件 | 阻塞原因（实测） |
|------------|----------------|
| `App/Localization/` | 用 XAML `ResourceDictionary` + `Application.TryFindResource` → **WPF 专用** |
| `Utils/OS/` | **25 处 `DllImport`**（全库 33 处的 76%）：`KernelInterop` 8、`DragHelper` 8、`WindowInterop` 5、`RegistryChangeMonitor` 3、`NtInterop` 1 |
| `Utils/WinRT/` | 6 处 `DllImport` + WinRT 互操作 |
| `Utils/Secret/` | DPAPI + WMI |
| `IO/Net/Http/HttpProxyManager.cs` | 读注册表 |
| `IO/Net/Http/HttpServer.cs` | 用 `HttpListener` |
| `App/Essentials/TelemetryService.cs` | 读注册表 + Sentry |
| **整体统计** | **544 个 `.cs` 中 59 个引用 `System.Windows` 家族** |

> ⚠️ **对 `App/Localization/` 的判定需要说明**：4.2 表把它列为"硬阻塞"，
> 而 2.A 表把它列为"非 MC / 未做"。两者不矛盾 —— **它是非 MC 的功能，但它的实现方式是 WPF 专用的**。
> 若 UMango 要做 i18n，**不应移植它**，而应另选跨平台方案（如 resx 或自建 JSON 字典）。
> 这正是"某功能非 MC"≠"可以直接迁移"的具体例子。

> 注：PCL.Core 子任务的原始报告称"全库 37 处 P/Invoke、`Utils/OS/` 占 30 处"，
> 主控复核实测为 **33 处**（`Utils/OS/` 25 处），本报告采用实测值。

---

### 4.7 把 §4 的候选按许可证二分（决定"抄代码"还是"只抄想法"）

这是本节最有操作性的一张表 —— 因为它直接决定每一项**能不能把代码拿过来**：

| 类别 | §4 中的条目 | 可否复制代码 | 依据 |
|------|------------|------------|------|
| **A. 可复制（Apache-2.0）** | P0-1 持久化缓存（`IO/Storage/Cache/`）、P0-2 配置体系（`App/Configuration/`）、P1-2 HTTP 扩展层（`IO/Net/Http/`）、P1-3 DNS（`IO/Net/Dns/`）、P1-4 线程原语（`Utils/Threading/`）、P1-5 哈希（`Utils/Hash/`）、P1-6 编码（`Utils/Codecs/`）、P1-7 验证器（`Utils/Validate/`）、P2-3 系统信息（`Utils/OS/` 部分）、P2-4 IoC、P2-5 事件总线（`App/EventBus/`）、P2-6 单实例、P2-7 CLI（`App/Cli/`）、P2-8 扩展方法（`Utils/Exts/`）、P2-9 差分（`Utils/Diff/`）、P2-10 加密（`Utils/Encryption/`）、P2-11 主题（`UI/Theme/`）、P2-12 对话框（`UI/MsgBoxWrapper.cs`）、P2-14 SvgIcon、P2-15 特效、P2-18 SemVer、P2-19 相似度（`Utils/SimilaritySearch.cs`） | ✅ **可以**（保留声明 + 标注变更） | 根 `LICENSE`：`ALL THE OTHER DIRECTORIES` → Apache-2.0 |
| **B. 只能借鉴设计（自定义专有）** | P1-1 启动器自更新（`Modules/Updates/`）、P2-17 位图（`Modules/Base/MyBitmap.cs`）、P2-20 自定义事件总线（`Modules/Event/`）、P2-21/22 自定义主页（`Pages/PageLaunch/PageLaunchRight`）、**新增：`Modules/Base/ModBase.cs` 的通用工具集、`Modules/Base/ModLoader.cs` 的加载器框架、`Modules/Base/ModAnimation.cs` 的补间引擎、`Modules/Network/` 的下载引擎主体** | ❌ **不可以** —— 直接复制即触发重度使用（改名 + 开源） | 根 `LICENSE`：`SPECIAL DIRECTORY: Plain Craft Launcher 2/` → 自定义条款 |

**B 类里最值得注意的一项**：`Modules/Network/`（12 文件 1,137 行）经实测**只有 2 处 MC 域名硬编码**
（`FileDownloader.cs:216`、`RequestSigning.cs:25-28`），本质上是个通用下载引擎 ——
但**它整块在自定义许可目录下**，所以"下载引擎很通用"这个直觉**不能推出"可以搬过来"**。
UMango 现有的 `Download/`（10 文件 1,638 行，aria2/内置 HTTP/eD2k 三引擎 + 自适应分段下载 `RangeDownloader.cs`）是自主设计，**这条路本来就是对的**。

**一个可复用的安全设计参考（B 类，但值得单独记一笔）**：`Modules/Event/XamlEventSanitizer.cs`（用 8 条
`[GeneratedRegex]` 剥离 XAML 中的事件声明，返回含 `UnsupportedTypesFound` 的 `SanitizeResult`）。
它与 2.A 表里"远程 XAML 是代码执行面"的问题是配套的 —— 若将来真要支持可自定义界面，
**"加载前先净化 + 明确拒绝未知事件类型"这个设计思路值得借鉴**，但不要连代码一起搬。

## 5. 指标

### 5.1 总量对比

| 指标 | PCL-CE（commit `ab6e2f1`） | UMango（活跃 .NET 栈，commit `602f003`） | 比值 |
|------|--------------------------:|-------------------------------------:|-----:|
| 文件总数（不含 `.git`） | 918 | 约 **136** | 6.8× |
| `.cs` 文件数 / 行数 | **803 / 138,447** | **113 / 18,331** | 7.6× |
| `.xaml` 文件数 / 行数 | **100 / 24,109** | **15 / 3,565** | 6.8× |
| C# + XAML 行数合计 | **162,556** | **21,896** | **7.4×** |
| 测试用例数 | `PCL.Core.Test`：46 文件 / 5,280 行 / 177 个 `[TestMethod]` | 静态计数 **278**（228 `[Fact]` + 50 条 `[InlineData]`）；commit `f2cc0b9` 自述 **277** | — |

> **UMango 文件数口径**（逐目录实测，`-not -path '*/obj/*' -not -path '*/bin/*'`）：
> `.cs` 113 = Core 57 + App 25（新增 `LogView.xaml.cs`、`LogViewModel.cs` 等）+ Core.Tests 22 + App.Tests 9 + XamlAudit 4；（口径为 commit `f2cc0b9`）
> `.xaml` 14 全部在 `App`；另有 5 个 `.csproj` + 1 个 `Directory.Build.props` + 1 个 `UMango.slnx` + `app.manifest` 等工程文件。
>
> ⚠️ **本表是"某一时刻"的快照**：本次调研期间有另一个任务在**并发提交**（详见 6.2），
> 数字取 commit `f2cc0b9` 时刻。若与本文件之外的数字不一致，以更新时刻为准。
>
> ⚠️ **测试用例数是静态计数**（`grep -c '\[Fact\]'` / `\[InlineData\]`），
> 不是运行结果。commit `2f900b1` 自述"测试 245/245"、`39f5259` 自述"App 测试 46 → 52"，
> 与静态计数**完全吻合**（154+45=199；47+5=52），但按任务硬性要求本次**未运行 `dotnet test`**，
> 因此"全部通过"未经本次独立复核。

### 5.2 UMango 活跃栈内部构成（实测）

| 模块 | 文件数 | 行数 | 说明 |
|------|-------:|-----:|------|
| `src/UMango.Core/PackageManagers` | 13 | 4,385 | 6 适配器 + 基类 + 注册表 + 模型（**最大模块，占 Core 56%**） |
| `src/UMango.Core/Configuration` | 6 | 703 | 描述符驱动设置 |
| `src/UMango.Core/Download` | 14 | 2,041 | 3 引擎 + 注册表 + 会话 + 模型 |
| `src/UMango.Core/Install` | 4 | 460 | 执行器 + 命令构造 + 进度解析 + 步骤名 |
| `src/UMango.Core/Animation` | 27 | 2,151 | 规划器 + 缓动 + 规格 + 设置 + **帧抽象** + **UI 线程访问抽象** + `IUIClock` |
| `src/UMango.Core/App/Tasks` | 9 | 434 | **采用自 PCL.Core（Apache-2.0）** |
| `src/UMango.Core/Execution` | 3 | 212 | 进程执行 3 件套（已接入日志） |
| `src/UMango.Core/Records` | 2 | 160 | 操作记录 + 存储 |
| `src/UMango.Core/Terminal` | 2 | 132 | 终端桥 + 行模型 |
| `src/UMango.Core/Platform` | 2 | 67 | 平台/系统信息 |
| `src/UMango.Core/Logging` | 5 | 429 | 调研期间新增并已提交（`f2cc0b9`）：`Log.cs`/`ILogSink.cs`/`LogModels.cs`/`MemoryLogSink.cs`/`FileLogSink.cs` |
| **`src/UMango.Core` 合计** | **224** | **30,628** | |
| `src/UMango.App`（Views + ViewModels + Themes + Animation + Services） | **55** | **12,793** | 含新增 `LogView`（404 行）；`Views/` + `ViewModels/` 共 21 文件 |
| `src/UMango.Core.Tests` | 102 | 19,518 | |
| `src/UMango.App.Tests` | 18 | 2,824 | |
| `tools/XamlAudit` | 5 | 711 | XAML 静态审计 |

### 5.3 PCL-CE 侧按模块（实测）

| 模块 | `.cs` 文件 / 行 | `.xaml` 文件 / 行 | 是否 MC 专属 |
|------|---------------:|-----------------:|------------|
| `PCL.Core/App` | 90 / 9,055 | 7 / 14,363（语言字典） | 否（i18n 文本为启动器专用） |
| `PCL.Core/IO` | 51 / 6,044 | 0 | 否 |
| `PCL.Core/Link` | 59 / 5,018 | 0 | **是**（MC 联机） |
| `PCL.Core/Logging` | 10 / 849 | 0 | 否 |
| `PCL.Core/Minecraft` | 108 / 6,800 | 0 | **是** |
| `PCL.Core/Model` | 3 / 58 | 0 | 否 |
| `PCL.Core/UI` | 129 / 9,835 | 1 / 7（`MotdRenderer`，MC 专属） | 否（除 MotdRenderer） |
| `PCL.Core/Utils` | 92 / 7,219 | 0 | 否（除 `VarIntHelper`/`PEHeaderReader` 服务 MC） |
| `PCL.Core/ViewModel` | 1 / 114 | 0 | 否 |
| **`PCL.Core` 合计** | **544 / 45,005** | **8 / 14,375** | |
| `Plain Craft Launcher 2/Modules` | 92 / 36,231 | 0 | **大部分是**（见 5.4） |
| `Plain Craft Launcher 2/Pages` | 135 / 44,708 | — | **大部分是** |
| `Plain Craft Launcher 2/Controls` | 64 / 11,342 | — | 否（通用控件） |
| **`Plain Craft Launcher 2` 合计** | **205 / 86,123** | **92 / 9,734** | |
| `PCL.Core.Test` | 46 / 5,280 | 0 | 否 |
| `PCL.Core.SourceGenerators` | 8 / 2,039 | 0 | 否 |

### 5.4 `Plain Craft Launcher 2/Modules` 细分（92 文件 / 36,231 行）

| 子目录 | 文件数 | 行数 | MC 归属 |
|--------|-------:|-----:|--------|
| `Modules/Minecraft`（含 `CrashAnalysis/`） | 50 | 23,352 | **全部 MC** |
| `Modules/Base` | 5 | 7,216 | 混合（`ModBase.cs` 3,759 行为上游通用基础设施；`ModAnimation`/`MyBitmap`/`ModLoader`/`ModSetup` 通用） |
| `Modules/Network` | 12 | 1,137 | **大部分通用**（仅 `DownloadRequestFactory` 含 MC 镜像源） |
| `Modules/Updates` | 11 | 1,020 | 通用 |
| `Modules/Event` | 6 | 741 | 通用（依赖 WPF） |
| `Modules/UI` | 3 | 244 | 通用（`HintService`/`HintType`/`Theme/ThemeManager`） |
| `Modules/` 根（`ModMain` 1134 / `ModMusic` 472 / `ModLink` 398 / `ModWebServer` 345 / `ModVideoBack` 172） | 5 | 2,521 | `ModLink` 是 MC；其余通用 |
| **其中 `CrashAnalysis/`** | **28** | **3,874** | **全部 MC** |

### 5.5 一个刺眼的对比

| | 数值 |
|---|---:|
| PCL-CE 的 **MC 专属**代码（第 3.1 节保守估） | **≈ 67,973 行** |
| UMango **全部**活跃代码（C# + XAML） | **≈ 21,896 行** |
| 比值 | **3.1×** |

即：**"完整迁移 PCL-CE 全部功能"意味着项目规模要再放大 3 倍以上，而其中约一半代码落在 UMango 的红线之外** ——
且那部分代码与项目既定的跨平台方向直接冲突（§4.5）。

---

## 6. 不确定处与未能确定的项

以下各项**本次未能确定**，列出原因，避免误导决策：

1. ~~**`PageSetupLauncherMisc.xaml.cs`（322 行）的具体设置项未逐项展开**~~ —— **已逐项确认**（见 §4.5 之后的 P3-9 逐项核对表）：
   9 组设置中，**网络代理**（自定义地址 / 自定义用户名 / 自定义密码 / 代理类型）已迁入
   （`src/UMango.Core/Network/ProxyManager.cs` + `src/UMango.App/Network/PlatformSystemProxyProbe.cs` + 设置页「①.9 网络代理」卡）；
   其余 8 组不迁的理由分别是：公告方案（PCL 官网专属）、禁用硬件加速（WPF 渲染层内部开关，UMango 暂无软渲染回退路径）、
   动画 FPS 上限（UMango 已有 `ui.animation.fpsLimit` 等价项）、最大日志行数（UMango 已有日志缓冲上限）、
   遥测（UMango 不采集遥测）、DoH（UMango 的 DNS 走系统，不自带解析器）、调试动画速度（UMango 的动画速度因子已独立）、
   调试跳过复制与调试随机延时（PCL 的 MC 安装流程专用）、以及「停止使用 PCL CE / 导入导出 PCL 配置」（PCL 专属）。
2. **`Modules/Base/ModBase.cs`（3,759 行）内部的通用/专用比例未逐行分类**。
   该文件是上游继承的基础设施大杂烩，本次只确认了它的体积与角色，未做行级归属统计。
3. **PCL-CE 的 `Controls/`（64 文件 11,342 行）未纳入功能清单**。
   本次按任务给定的范围（Pages / Modules / PCL.Core）调研；`Controls/` 是被 Pages 引用的通用控件库，
   其中 `MyListItem`、`MyCompItem`、`MyButton` 等的**设计数值已被 UMango 提取**（见 `docs/DESIGN_PCL_CE.md`），
   但**功能级对照未做**。
4. **PCL-CE 的 `Resources/` 目录（1 文件 151 行）与 `Images/`、捆绑 jar/exe 未调研**。
   既有分析报告指出该目录含内置二进制（含确证为 Microsoft 专有的 `linkd.exe`），但对"功能对照"无影响。
5. **`PCL.Core/Utils/OS/`（14 文件）与 `PCL.Core/UI/`（129 文件）的内部文件级分类未逐项完成**。
   只做了目录级与关键字级（WPF / P/Invoke / Win32）的统计，未逐个文件判定 —— 因此 5.3 表中这两行的"否"是**目录级判断**。
6. **UMango 的 `git status` 显示两项未提交改动**（`src/UMango.App/ViewModels/DownloaderViewModel.cs` 已修改、
   `src/UMango.App.Tests/` 未跟踪且只有 csproj）。**这两个可能是其他并行任务正在进行的工作**，
   本清单按"当前工作副本可见状态"判定，未跟踪的测试工程**不计入完成度**。
7. **`ui/`、`core/`、`native/`（Flutter 废弃栈，共 74 文件 / 25,875 行）的归属**：
   `docs/DESIGN_UI_DOTNET.md` 明确写"已废弃……不要碰"，故本次不计入 UMango 完成度，
   但它们**仍在仓库中且 `README.md` 仍以它们为主体**，是否需要清理/归档应由项目所有者决定。
8. **PCL-CE 的自定义许可义务判定依赖"善意合理使用"**，指南自述"本指南仅作为说明，不是法律文件或协议"。
   本清单引用其原文作为**事实依据**，但**不构成法律意见**；最终是否触发 B6/B7 应咨询专业人士。
9. **"部分 MC"的 4 个条目归入 MC 侧计数**（第 1 节已声明口径），若按"机制通用即可迁移"的另一种口径，
   非 MC 侧会 +4 条。两种口径的差异已在此显式说明。
10. **UMango 的 154 个 `[Fact]` / 9 个 `[Theory]`（45 条 `[InlineData]`）是静态计数**，
    不是测试运行结果 —— 按任务硬性要求，本次**未运行 `dotnet test`**（避免与主控进程争锁）。
    因此"测试通过"这一结论**未经本次复核**，仅有源码与文档自述。
11. **`Plain Craft Launcher 2/Pages/PageHomepage/PageHomepageNewsView` 的逻辑部分在 `PCL.Core` 内**，
    本目录只有视图；其完整行为未在本次确认（不影响"未做"的判定）。
12. **`Pages/PageTools/PageToolsTest.xaml` 引用的服务器查询控件在本目录无 code-behind**，
    该控件的实现位置未确认。
13. **`Modules/Network/Facade/ModNet.cs` 的 `NetTaskSpeedLimitLow/High` 在 `Modules/` 范围内无消费点**，
    推测由 `PCL.Core` 的下载实现读取；本次未追到消费方。
14. **`Modules/Minecraft/CrashAnalysis/Detection/CrashStackAnalyzer.cs` 是 `partial` 类**，
    其另一半不在 `Modules/` 范围内，本次未定位（因此 3.5 表对该文件的行数记为"—"）。
16. **`Modules/Updates/UpdatesMinioModel.cs` 的增量补丁应用逻辑未逐行确认** —— 仅确认数据模型内存在 `Patches` 字段（`:263`），其应用（打补丁）流程未验证；这会影响 P1-1 的成本估算（若含增量更新则成本更高）。
18. **`PCL.Core/Model/Homepage/News/` 的命名空间与目录不一致** —— 目录是 `Model/Homepage/News/`，而 `ApiResponse.cs:3`/`NewsItem.cs:4`/`Result.cs:4` 声明的是 `PCL.Core.Model.Tools.News`。原因未查（不影响 MC 归属判定）。
19. **`IO/Download/` 的文件数**：本报告实测 **6 文件 / 163 行**；PCL.Core 子任务报告称 7 文件。以实测 6 为准（`ls` 可直接核对，见 4.5 的脚注与 2.K 表）。
20. **"约 6 成代码可移植"是一个粗估**：我按目录逐一核查了 WPF/Windows 耦合（§4.6 三层分类），但**未做行级精确统计**，因此 §4.6 的"第 1 层"清单是**文件级判断**，不是按行加权得出的百分比。若需要精确比例，应按每个文件是否有 WPF/Windows 命中来逐行加总（本次未做）。
17. **`Modules/Network/Facade/ModNet.cs` 的 `NetTaskSpeedLimitLow`/`Left` 在 `Modules/` 内无消费点**（见第 13 条），其实际使用可能在 `PCL.Core/`；本次未追到，故 2.H 表中该条目的"部分"判定依据是**架构对照**而非调用链证据。
15. **PCL-CE 侧无法用 git 交叉核验**：该克隆是**部分克隆（promisor remote）**，
    `git status` / `git diff` 在离线时不可用（会向 github.com 拉取缺失对象并超时 75 秒）。
    因此"PCL-CE 未被本次调研修改"的证据是文件数/行数与初始实测一致，而非 git 的干净输出。
    同理，`--depth 1` 使 `git blame` 不可用，故 2.x 各表的"CE 新增 vs 上游继承"未做逐文件归属判定 ——
    本报告只做**功能与 MC 归属**判定，不做**代码来源归属**判定（后者见既有分析报告）。

### 6.1 关于本次调研方法的说明

`Pages/`、`Modules/`、`PCL.Core/` 三个范围的逐文件普查由三个并行子任务完成，
主控（本报告作者）独立完成了：UMango 侧全部源码判定、两棵源码树的规模实测、
导航枚举（`PageType`/`PageSubType`）的权威索引提取、许可证原文核验，以及 §3.4–3.6 的交叉验证。

**子任务结论的抽检**：主控对子任务给出的 6 处关键断言做了原始文件复核（`sed -n` 打印上下文），
**全部命中、无一虚构**：
`PageInstanceLeft.xaml.cs:201` 的 `PageGet` switch、
`PageSetupLauncherMisc.xaml.cs:291-298` 的 `cmd.exe` 自删、
`FileDownloader.cs:216` 的 CurseForge host 特判、
`RequestSigning.cs:25-28` 的 API Key 注入、
`ModMain.cs:912/923/930` 的注册表写入、
`CrashLogCollector.cs:35` 与 `CrashReportExporter.cs:17-21` 的 MC 硬编码。

**主控对子任务结论做了 3 处改判（子任务原始结论 → 复核后）**：

| 项 | 子任务/初稿结论 | 复核后结论 | 证据 |
|----|--------------|-----------|------|
| `Model/Homepage/News` | 非 MC（"PCL 官网新闻"） | **是 MC** | `NewsViewModel.cs:16` 打 `net-secondary.web.minecraft-services.net`，`:20-22` 白名单为 minecraft.net / minecraft-services.net / microsoft.com |
| `IO/Download/`（NDlFactory 等） | 可迁移的下载抽象 | **废弃死代码，不适用** | 该目录 6 个文件在目录外的引用数 = **0**，无实现、无调用点 |
| P/Invoke 数量 | 37 处（`Utils/OS/` 30 处） | **33 处**（`Utils/OS/` 25 处） | `grep -rc "DllImport\|LibraryImport"` 逐文件统计 |
| `Modules/` 的 P/Invoke 数量 | 13 处 | **11 处** | `grep -rn DllImport Modules/` → ModMain 3 + ModWatcher 6 + ModLink 1 + ModBase 1 |
| 动画引擎状态 | 「已完成」 | **下调为「部分」** | 初稿只比对了 `AnimationPlayer`，忽略了 `ModAnimation.cs` 是 **1,517 行**的完整补间引擎（20+ 属性工厂 + 14 缓动 + 帧率控制） |
| 动画系统的**可移植性** | 归入「高成本、WPF 绑死」（P2-2 成本＝高） | **上调为「成本＝中」** | 实测 `UI/Animation/` 87 文件仅 14 个触 WPF，`DoubleAnimation`/`System.Windows.Media.Animation`/`Storyboard`/`Timeline` 命中**全为 0**；UMango 也是 WPF，故实现类可直接用（详见 §4.6 第 2.5 层） |
| `App/Configuration/` 体积 | 「30+ 文件」 | **28 文件 / 2,478 行** | 实测根 14 / 1,283 + `Storage/` 14 / 1,195 |
| `PCL.Core/README.md` 的定位 | 任务书假设其「被设计成可复用库」 | **README 无此声明** | 该文件仅 6 行，原文只说「为 PCL CE 版本特别开发的核心库」；真正的定位证据是 `PCL.Core.csproj` 的 `net10.0-windows` + `UseWPF=true`（本报告 §4.5 已按此判定） |
| §4 候选的许可证标注 | P1/P2 表未逐项标注许可来源 | **已集中更正**（新增 §4.7 二分表 + §4 开头警示） | 初稿把 `Modules/Updates/`、`Modules/Event/`、`Modules/Base/MyBitmap.cs` 列为迁移候选，却只标注了其中一条的许可问题；`Modules/` 整体在自定义许可目录下 |
| 功能普查完整性 | 漏掉「启动器自定义主页」（初稿未收录） | **已补入**（`PageLaunchRight.xaml.cs`，898 行，**非 MC**） | 子任务完整报告收尾送达后比对发现；该功能挂在 `PageLaunch/` 目录下，容易被误当 MC 页面而跳过 |

**主控自身的两处错误已修正**：

1. 首轮统计 `Modules/UI` 时用了 `-maxdepth 1`，漏掉子目录 `Theme/ThemeManager.cs`，
   得到"2 文件 / 113 行"；实测应为 **3 文件 / 244 行**（`ThemeManager.cs` 131 行）。
2. 初稿的**功能条目计数内部不自洽**（写 137 = MC 78 + 非 MC 55，但 78+55=133）。
   已改为脚本逐行统计；经后续多轮补充/下调，**当前值为 174 = MC 81 + 部分 MC 3 + 非 MC 90**，
   且 `20+30+42+82 = 174` 亦成立（见 §1.1）。

### 6.2 ⚠️ 重要：本报告是"某一时刻"的快照

**调研期间，工作区被另一个任务并发修改并提交。** 实测到的变化：

| 时刻 | 状态 |
|------|------|
| 调研开始 | `git log -1` = `f060293`（"新增 XAML 静态审计工具"） |
| 调研中 | `git status` 出现 `?? src/UMango.App.Tests/`（未跟踪，**只有 1 个 csproj**） |
| 调研中 | `src/UMango.App.Tests/` 陆续出现 4 个测试文件（`DownloaderViewModelTests`、`OtherViewModelTests`、`SharedSettingConsistencyTests`、`ShellViewModelTests`，共 604 行），并被提交 |
| 调研后期 | `2f900b1`（"Core 去掉残留的 UseWPF 并改为多目标"）—— `UMango.Core` 改为 `net10.0;net10.0-windows` |
| 调研末段 | `39f5259`（"已安装包列表接入界面"，App 测试 46 → 52）—— **本报告 P0-4 由「部分」变「已完成」** |
| 调研末段 | `602f003`（"修正文档债：README 重写，四份过期文档加作废横幅"）—— **本报告 §1.3 指出的过期文档问题被修复**；本报告文件本身也被该 commit 一并提交 |
| 核对结束时 | 工作区出现未跟踪的 `src/UMango.Core/Logging/`（5 文件 426 行）与 `ProcessRunner.cs` 改动 —— **本报告 P0-3 由「未做」变「部分（进行中）」** |
| 收尾复核时 | **`f2cc0b9`（"日志系统 + 日志页 + 一键导出诊断包（功能对照表 P0-3，测试 251 → 277）"）** —— 该 commit 的信息里**直接引用了本报告的条目编号「P0-3」**，说明这份清单已被当作工单使用；
**P0-3（日志系统）与 P3-8（日志查看页）双双由未做/部分变为已完成**；测试数 251 → 277 |

**影响**：

1. 第 5 节的规模数字是 `2f900b1` 时刻的实测值。并发继续提交后，这些数字会变。
2. 初稿曾把 `src/UMango.App.Tests/` 记为"未跟踪、尚未编写测试" —— **该结论已被后续提交推翻**，现已改正为 4 文件 46 个用例。
3. `git status` 现在只显示 `M docs/PCL_CE_FEATURE_PARITY.md`（**本文件本身**），
   说明其它改动都已由那个任务提交；本报告未修改任何其他文件。
4. **`UMango.Core` 的 `net10.0` 多目标这一变化，直接改变了本报告的迁移成本结论**（§4.5），
   它是本次调研中影响最大的发现 —— 若非并发提交，本报告会漏掉这个决定性约束。
5. **有三项判定在调研期间发生了变化**（已安装包列表 → 已完成；日志系统 → 部分进行中；过期文档 → 已修复），
   报告已逐处标注「⚠️ 判定在调研期间发生变化」并给出新证据。
   **这恰恰说明"功能对照清单"这类文档必须带时间戳与 commit 号** —— 否则一周后它会静默失真。

> **本报告在本轮调查期间已被下游消费**：`f2cc0b9` 的提交信息引用「功能对照表 P0-3」，并按该条目实现了日志系统与日志页。这验证了清单的可用性，但也意味着 **§1.1 的数字是流动的** —— 引用本报告的任何结论时，请一并引用它所属的 commit（当前：`f2cc0b9`）。

### 6.3 一条方法学教训

初稿把"MC 专属 vs 非 MC"的分类做了手工累加，结果**内部不自洽**（78+55≠137），
而单看每一行都"看起来对"。**多分类的清单必须用脚本统计，不能手工数** ——
这与本项目 `docs/XAML_AUDIT.md` 里那条教训同源："一个 100% 误报的检查比没有检查更糟"，
反过来也成立：**一个手工维护的计数，比没有计数更容易骗过自己。**

---

## 附录 A：状态判定所依据的 UMango 源码证据索引

| 能力 | UMango 证据文件（行号） |
|------|----------------------|
| 包源适配器 ×6 | `src/UMango.Core/PackageManagers/Adapters/{Npm,Pnpm,Git,Homebrew,Scoop,Winget}PackageManager.cs` |
| 包源注册与可用性缓存 | `src/UMango.Core/PackageManagers/PackageManagerRegistry.cs`、`PackageManagerFactory.cs` |
| 平台门控（唯一事实来源） | `src/UMango.Core/PackageManagers/PackageModels.cs:36,44` |
| 设置体系 | `src/UMango.Core/Configuration/SettingsService.cs:15`、`SettingDescriptor.cs:25`、`UiSettings.cs:13` |
| 主题运行时切换 | `src/UMango.App/Themes/ThemeManager.cs:34,113,167` |
| 动画 | `src/UMango.Core/Animation/AnimationPlanner.cs:19`、`Easing.cs:10`、`src/UMango.App/Animation/AnimationPlayer.cs:16` |
| 任务中心（采用自 PCL.Core） | `src/UMango.Core/App/Tasks/TaskCenter.cs:1`（头部 SPDX 声明） |
| 安装执行 | `src/UMango.Core/Install/InstallExecutor.cs:44`、`InstallCommandBuilder.cs:11` |
| 终端桥 | `src/UMango.Core/Terminal/TerminalBridge.cs:22` |
| 操作记录 | `src/UMango.Core/Records/InstallRecordStore.cs:16,94,115` |
| 下载引擎 | `src/UMango.Core/Download/Engines/{Aria2,BuiltinHttp,Ed2k}Engine.cs`、`DownloadEngineRegistry.cs:17` |
| 进程执行 | `src/UMango.Core/Execution/ProcessRunner.cs`、`ProcessSpec.cs` |
| 界面外壳 | `src/UMango.App/MainWindow.xaml:143-209`、`ViewModels/ShellViewModel.cs` |
| 六个视图 | `src/UMango.App/Views/{Home,Download,Progress,Settings,Tools,Downloader}View.xaml` |
| 服务定位 | `src/UMango.App/Services/AppServices.cs:32` |
| 合规声明 | `src/UMango.App/Views/ToolsView.xaml:217-274` + `THIRD-PARTY-NOTICES.md` |
| XAML 审计 | `tools/XamlAudit/` + `docs/XAML_AUDIT.md` |

## 附录 B：调研方法

- **PCL-CE 侧**：以其自身的枚举定义作为权威索引 —— `FormMain.xaml.cs:1361`（`PageType`）、
  `:1425`（`PageSubType`），辅以各 `Page*Left.xaml` 的菜单项与 `Lang` 资源键，
  再用 `find` / `wc -l` / `grep -n` 取实测数字。这样比"逐个文件猜功能"更完备、更少遗漏。
- **UMango 侧**：**只读源码**判定，不采信文档自述。凡文档与源码不符，以源码为准并把不符点写进报告（见 1.3）。
- **反向验证**：对 8 个容易误判的关键字（`Update`/`Hash`/`Language`/`Java`/`Cache`/`Proxy`/`SemVer`/`Validate`），
  逐个打印命中上下文，确认其中多数是 `UpdateSourceTrigger`、`HashSet`、`JavaScriptEncoder`、`System.Threading` 之类的**误命中**，
  避免把"没做过"写成"做过了"。
- **硬性约束遵守**：本次**未运行** `dotnet build` / `dotnet test` / `cargo` / `cmake`；**未修改** `/tmp/PCL-CE` 任何文件；仅创建本文件。
