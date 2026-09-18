# UMango UI 层规格（多 Agent 并行实现契约）

> 本文件是并行实现的**唯一契约**。所有 agent 先读它，严格按其中的命名与 API 写代码，
> 否则并行产出无法编译到一起。
>
> 技术栈：**.NET 10 + WPF**（`net10.0-windows`，`EnableWindowsTargeting` 使其可在 macOS 编译）。
> 本机无法原生运行 WPF（需 Windows 或 CrossOver），因此**不要以"跑起来看"为验证手段**，
> 以 `dotnet build` 与 `dotnet test` 为准。

---

## 0. 硬性约束

1. **禁止运行 `dotnet build` / `dotnet test` / `dotnet restore`**（多个 agent 并行会争 `obj/` 锁）；
   统一由最后的「集成验证」agent 构建与修错。
2. 只改分配给你的文件，**绝不碰别人的文件**。
3. 不得引入新的 NuGet 依赖。
4. 不得复制 PCL-CE 的 XAML/C# 代码；只使用我们自己的 token 与组件。
   （合规依据见 `THIRD-PARTY-NOTICES.md`：我们停留在「轻度使用」，只提取设计数值。）
5. 中文注释；解释「为什么」的注释要保留。不留 TODO、不留未定义引用。
6. 界面文案中文。

---

## 1. 现成的 Core API（已实现且有 98 项测试覆盖，直接用，不要重复实现）

命名空间 `UMango.Core.*`，工程 `src/UMango.Core/UMango.Core.csproj`。

### 1.1 包管理器

```csharp
public enum PackageSourceKind { Npm, Pnpm, Git, Scoop, Winget, Homebrew }

public sealed record SourceCapabilities(bool Search, bool ListVersions, bool ResolveDependencies,
    bool OfficialDownloadCounts, bool Install, bool Uninstall, bool Upgrade);

public enum AvailabilityState { Available, Degraded, UnsupportedPlatform, NotInstalled, Unreachable }

public sealed class SourceAvailability {
    PackageSourceKind Kind; AvailabilityState State; bool CliFound;
    string? CliPath; string? CliVersion; DateTimeOffset CheckedAt; string? Detail; string? VerificationNote;
    bool IsUsable;
}

public sealed class PackageInfo   { PackageSourceKind Source; string Name; string? Summary;
    string? LatestVersion; long? DownloadCount; string? DownloadCountSource;
    bool DownloadCountIsFallback; string? License; string? Homepage; }

public sealed class PackageVersion{ string Version; bool IsPrerelease; DateTimeOffset? PublishedAt;
    bool Deprecated; string? Note; }

public sealed record DependencyRef(string Name, string Requirement, bool IsDev = false);

public interface IPackageManager {
    PackageSourceKind Kind; string DisplayName; string InstallDirectory; string CliName;
    IReadOnlyList<PlatformKind> SupportedPlatforms; SourceCapabilities Capabilities;
    string InstallHint;
    Task<SourceAvailability> ProbeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(string name, string? version, CancellationToken ct = default);
    Task<IReadOnlyList<InstallStep>> PlanInstallAsync(string name, string? version, CancellationToken ct = default);
}

public sealed class PackageManagerRegistry {
    PackageManagerRegistry(IEnumerable<IPackageManager> managers);
    IReadOnlyList<IPackageManager> All;
    IPackageManager? Find(PackageSourceKind kind);
    IEnumerable<IPackageManager> UsableOnHost;
    Task<SourceAvailability> GetAvailabilityAsync(PackageSourceKind kind, bool refresh = false, CancellationToken ct = default);
    Task<IReadOnlyList<PackageSourceCategory>> GetCategoriesAsync(bool refresh = false, CancellationToken ct = default);
}

// ★ 界面左栏直接用这个：Title = 包管理器名，Subtitle = 安装目录（需求原文要求）
public sealed class PackageSourceCategory {
    PackageSourceKind Kind; string Title; string Subtitle;
    bool SupportedHere; bool Available; AvailabilityState AvailabilityState;
    string? VerificationNote; string? Detail; SourceCapabilities Capabilities; string InstallHint;
}

public static class PackageManagerFactory {
    public static PackageManagerRegistry CreateDefault();   // 已注册全部 6 个，不做平台过滤
}
```

### 1.2 任务与进度（子项进度的数据源）

```csharp
public enum TaskState { Waiting, Running, Success, Canceled, Failed }

public partial class TaskModel : ObservableObject {
    string Title; bool SupportProgress;
    TaskState State; string StateMessage; double Progress;      // 均为可观察属性
    bool IsGroup; ObservableCollection<TaskModel> Children;      // ★ 子项（按下载方式划分）
    RelayCommand Cancel; RelayCommand Pause;
    static TaskModel Simple(string title, Action? onCancel = null, Action? onPause = null);
    static TaskModel Progressive(string title, Action? onCancel = null, Action? onPause = null);
    static TaskModel Group(string title, Action? onCancel = null, Action? onPause = null);
}

public static class TaskCenter {
    ObservableCollection<TaskModel> Tasks;      // ★ 界面直接绑定
    event Action? Changed;
    int CompletedCount; int FailedCount;
    TaskModel Register(TaskModel task);
    void Complete(TaskModel task, bool success);
    void Remove(TaskModel task);
}
```

### 1.3 安装执行

```csharp
public sealed record InstallRequest(PackageSourceKind Source, string Name,
    string? Version = null, bool Global = true, string? WorkingDirectory = null);
public sealed record InstallResult(bool Success, string? ErrorCode, string? Message, long DurationMs);

public sealed class InstallExecutor {
    InstallExecutor(PackageManagerRegistry registry, InstallRecordStore records,
                    Func<ProcessSpec, ProcessRunner.OutputLineHandler?, CancellationToken, Task<ProcessOutput>>? runner = null);
    Task<IReadOnlyList<InstallStep>> PlanAsync(PackageSourceKind kind, string name, string? version, CancellationToken ct = default);
    Task<(TaskModel Task, InstallResult Result)> InstallAsync(InstallRequest request, bool dryRun = false, CancellationToken ct = default);
    static string MethodToStepName(DownloadMethod method);
}
```

### 1.4 终端集成桥

```csharp
public sealed class TerminalLine { DateTimeOffset At; bool IsStdErr; string Text; string? Step; }

public sealed class TerminalBridge {
    static bool DebugMode { get; set; }                 // ★ 设置页的「调试模式」开关
    static TerminalBridge For(string channelId);
    static void Remove(string channelId);
    static IReadOnlyCollection<string> ChannelNames;
    IReadOnlyList<TerminalLine> Lines; int Count; string? CurrentStep;
    event Action<TerminalLine>? LineAppended;
    event Action? Cleared;
    void BeginStep(string? step);
    void Write(bool isStdErr, string text);
    IReadOnlyList<TerminalLine> LinesOfStep(string step);   // ★ 按子项分组渲染
    void Clear();
}
```

### 1.5 记录与系统判断

```csharp
public sealed class InstallRecord {
    PackageSourceKind Source; string PackageName; string? Version; string Action;   // install/uninstall/upgrade
    DateTimeOffset OccurredAt; bool Success; string? InstallDirectory; long DurationMs;
    string? ErrorCode; string? Detail;
}
public sealed class InstallRecordStore {
    InstallRecordStore(string? storePath = null);
    IReadOnlyList<InstallRecord> Records; int TotalCount; int SuccessCount; int FailureCount;
    IReadOnlyDictionary<PackageSourceKind, int> CountBySource;
    int CountOf(PackageSourceKind kind);
    void Add(InstallRecord record); void Load(); void Save(); void Clear();
}

public static class SystemInfo {
    PlatformKind Platform; string Architecture; bool IsWindows; string HomeDirectory; string Describe();
}
```

---

## 2. 主题系统（需求：Windows10 Fluent Design + 深色 + 多主题可复用）

### 2.1 现状

`src/UMango.App/Themes/PclTokens.xaml` 是**固定浅色**的 PCL 设计 token（颜色/尺寸/字体），
`Themes/Styles.xaml` 是控件样式（banner 选项卡、三大金刚键、任务进度模板）。

### 2.2 要做的

**目标**：把颜色换成「**可整套替换的主题字典**」，并新增两套：

| 主题 | 要求 |
|------|------|
| **PCL 经典（浅）** | 保持现有 token 数值不变（这是 1:1 复刻的依据，**不得改动数值**） |
| **Windows10 Fluent（浅）** | 还原 **Windows 10 设置**的风格。⚠️ **注意：Fluent Design ≠ WinUI3**。Win10 的 Fluent 特征：方正（圆角极小或无）、**纯色标题栏**、控件紧凑、强调色为系统蓝、无 Win11 的 Mica/大圆角/居中标题。不要去模仿 Win11。 |
| **深色** | 上述两套各自的深色版本 |

**实现方式（必须按此结构，页面才不会写死颜色）**：

```
Themes/
  Tokens.Light.Pcl.xaml      ← 浅色 PCL 经典（现有数值原样搬过来）
  Tokens.Dark.Pcl.xaml       ← 深色 PCL 经典
  Tokens.Light.Fluent10.xaml ← Win10 Fluent 浅色
  Tokens.Dark.Fluent10.xaml  ← Win10 Fluent 深色
  ThemeManager.cs            ← 运行时切换：合并对应字典、替换旧字典
  Styles.xaml                ← 控件样式（只引用 DynamicResource，不写死颜色）
```

**关键要求**：
- 所有 token 的 **key 名必须完全一致**（两套主题可互换），例如 `BrC1` / `BrC3` / `BrBg` / `BrTitleBar` / `BrG3` 等；
- `Styles.xaml` 与页面一律使用 `{DynamicResource Xxx}` 而**不是** `{StaticResource Xxx}`，
  否则运行时切换主题不会生效（这是 WPF 的常见坑，请在注释里写明）；
- `ThemeManager` 提供：`Apply(ThemeKind kind, bool dark)`、`Current`、`event Action? ThemeChanged`；
- **PCL 经典浅色那套的数值一个字都不许改**（有 1:1 复刻的校验依据）。

---

## 3. 页面清单与文件所有权

| 页面 | 文件 | 说明 |
|------|------|------|
| 外壳 | `MainWindow.xaml` / `MainWindow.xaml.cs` | 顶部四个 banner 选项卡 + 左侧对应菜单 + 三大金刚键（已有，需接线） |
| 主页 | `Views/HomeView.xaml(.cs)` | 概览：系统判断、各包源状态、次数记录 |
| 下载 | `Views/DownloadView.xaml(.cs)` | ★ 左栏包源分类（Title=名/Subtitle=安装目录）+ 搜索 + 卡片列表 |
| 进度 | `Views/ProgressView.xaml(.cs)` | ★ 任务列表：**父任务 + 按下载方式的子项进度** + **内联终端输出** |
| 设置 | `Views/SettingsView.xaml(.cs)` | 主题选择（PCL经典/Win10 Fluent × 浅/深）、调试模式开关、记录管理 |
| 工具 | `Views/ToolsView.xaml(.cs)` | 诊断、合规声明、第三方许可 |

---

## 4. 环境要点（每个 agent 都要知道）

- 项目根动态解析，**绝不写死**：
  ```bash
  P=$(ls -d /Users/jonathan/Library/CloudStorage/*/"Deepseek Harness"/UMango | head -1)
  ```
  （OneDrive 会不定时改名挂载目录 `OneDrive-个人` ↔ `OneDrivePersonal`）
- .NET SDK 在 `$HOME/.dotnet`：`export PATH="$HOME/.dotnet:$PATH"`
- 解决方案 `$P/src/UMango.slnx`；App 工程 `$P/src/UMango.App/UMango.App.csproj`
- 访问 GitHub 需代理前缀 `http://gh-proxy.org/`
- 已废弃的 Flutter 版在 `$P/ui`、`$P/core`、`$P/native`，**不要碰**
