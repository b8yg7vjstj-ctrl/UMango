using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using UMango.Core.Localization;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Services;
using UMango.Core.Logging;
using UMango.Core.Platform;

namespace UMango.App.ViewModels;

/// <summary>日志页里一个级别筛选项。</summary>
public sealed record LogLevelOption(LogLevel? Level, string Title);

/// <summary>
/// 日志页。
///
/// **它存在的实际理由**：应用主要在 Windows 上运行，而开发发生在 macOS ——
/// 真机出问题时，开发侧唯一能拿到的材料就是用户发回来的一份日志。
/// 因此这里的"导出诊断包"不是锦上添花：它把日志、设置、记录、系统信息
/// 打成一个压缩包，用户点一下就能整份发出。
///
/// 刻意不依赖任何 WPF 类型（与其它 ViewModel 一致），因此能在 macOS 上被单测覆盖。
/// </summary>
public partial class LogViewModel : ObservableObject
{
    private readonly MemoryLogSink? _memory;

    private static string L(string key, params object?[] args) => LocalizationService.Text(key, args);

    public LogViewModel(MemoryLogSink? memory = null)
    {
        _memory = memory ?? AppServices.Logs;
        if (_memory is not null) _memory.Appended += OnAppended;

        Levels =
        [
            new LogLevelOption(null, "全部"),
            new LogLevelOption(LogLevel.Info, "信息及以上"),
            new LogLevelOption(LogLevel.Warn, "警告及以上"),
            new LogLevelOption(LogLevel.Error, "仅错误"),
        ];
        _selectedLevel = Levels[0];

        RefreshCommand = new RelayCommand(Reload);
        ClearCommand = new RelayCommand(Clear);
        ExportCommand = new RelayCommand(() => ExportDiagnosticBundle());
        OpenFolderCommand = new RelayCommand(OpenLogFolder);

        Reload();
    }

    public IReadOnlyList<LogLevelOption> Levels { get; }

    [ObservableProperty]
    private LogLevelOption _selectedLevel;

    /// <summary>按级别过滤后的日志行（最新的在前 —— 排查问题时最关心的总是刚发生的事）</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasMore;

    /// <summary>文件日志所在目录（"打开日志目录"用）</summary>
    public string LogDirectory => AppServices.LogDirectory;

    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenFolderCommand { get; }

    partial void OnSelectedLevelChanged(LogLevelOption value) => Reload();

    private void OnAppended(LogEntry entry)
    {
        // 新日志到达：只在不违反当前过滤条件时插入
        if (!Passes(entry)) return;
        Entries.Insert(0, entry);
    }

    private bool Passes(LogEntry entry)
    {
        var min = SelectedLevel?.Level;
        return min is null || entry.Level >= min;
    }

    /// <summary>重新读取内存日志。</summary>
    public void Reload()
    {
        try
        {
            Entries.Clear();
            if (_memory is null)
            {
                StatusMessage = L("Log.NotInitialized");
                return;
            }

            // 倒序：最新的在最上面
            foreach (var entry in _memory.Snapshot().Reverse().Where(Passes))
                Entries.Add(entry);

            var dropped = _memory.DroppedCount;
            HasMore = dropped > 0;
            StatusMessage = dropped > 0
                ? $"显示 {Entries.Count} 条；因超出缓冲上限已丢弃 {dropped} 条较早日志（完整内容在日志文件里）。"
                : $"共 {Entries.Count} 条日志。";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void Clear()
    {
        try
        {
            _memory?.Clear();
            Entries.Clear();
            StatusMessage = L("Log.Cleared");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// 导出诊断包：把日志、设置、记录与系统信息打成一个 zip，放在用户桌面上。
    /// 「一个文件、点一下、直接发回来」—— 这是让真机问题可定位的关键。
    /// </summary>
    /// <param name="outputDirectory">
    /// 输出目录。默认放桌面；**可注入是为了单元测试** ——
    /// 否则测试会往用户桌面扔 zip 文件。
    /// </param>
    public string? ExportDiagnosticBundle(string? outputDirectory = null)
    {
        try
        {
            ErrorMessage = null;
            var desktop = outputDirectory;
            if (string.IsNullOrWhiteSpace(desktop))
            {
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                    desktop = Path.GetTempPath();
            }
            Directory.CreateDirectory(desktop);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zipPath = Path.Combine(desktop, $"UMango-诊断包-{stamp}.zip");

            // 先把内存日志也落盘：用户可能很早就发现问题，
            // 而内存里那份包含了比文件更晚的信息（或者文件根本写不进去）。
            var summary = new System.Text.StringBuilder();
            summary.AppendLine("=== UMango 诊断摘要 ===");
            summary.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            summary.AppendLine($"平台：{SystemInfo.Describe()}");
            summary.AppendLine($"应用目录：{AppContext.BaseDirectory}");
            summary.AppendLine($"日志目录：{AppServices.LogDirectory}");
            summary.AppendLine($"内存日志条数：{_memory?.Count ?? 0}（丢弃 {_memory?.DroppedCount ?? 0}）");
            summary.AppendLine($"文件写入失败次数：{AppServices.LogFile?.FailedWriteCount ?? 0}");
            summary.AppendLine();
            summary.AppendLine("=== 内存中的日志（最近 3000 条） ===");
            if (_memory is not null)
            {
                foreach (var e in _memory.Snapshot()) summary.AppendLine(e.ToLine());
            }

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddText(zip, "摘要与内存日志.txt", summary.ToString());

                // 日志文件
                foreach (var file in AppServices.LogFile?.ListLogFiles() ?? [])
                {
                    try { zip.CreateEntryFromFile(file, "logs/" + Path.GetFileName(file)); }
                    catch (Exception) { /* 单个文件读不到就跳过，不影响整包 */ }
                }

                // 设置与记录（不含任何凭据：这两份文件里本来就没有密码之类的字段）
                AddFileIfExists(zip, AppSettings.SettingsPath, "settings.json");
                AddFileIfExists(zip,
                    Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath) ?? ".", "install-records.json"),
                    "install-records.json");
            }

            StatusMessage = $"诊断包已导出到桌面：{Path.GetFileName(zipPath)}";
            Log.Info("App", $"已导出诊断包：{zipPath}");
            return zipPath;
        }
        catch (Exception ex)
        {
            Log.ErrorAt("App", ex, member: "ExportDiagnosticBundle");
            ErrorMessage = $"导出失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>在资源管理器/Finder 里打开日志目录。</summary>
    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var file = AppServices.LogFile?.CurrentFilePath;

            // 优先选中当天那个文件（"打开文件夹并高亮该文件"比只开目录更省事）
            var psi = OperatingSystem.IsWindows()
                ? (file is not null
                    ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"")
                    : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{LogDirectory}\""))
                : OperatingSystem.IsMacOS()
                    ? new System.Diagnostics.ProcessStartInfo("open", file is not null ? $"-R \"{file}\"" : $"\"{LogDirectory}\"")
                    : new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{LogDirectory}\"");

            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
            StatusMessage = L("Log.OpenedFolder");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法打开日志目录：{ex.Message}";
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
        writer.Write(content);
    }

    private static void AddFileIfExists(ZipArchive zip, string path, string entryName)
    {
        try
        {
            if (File.Exists(path)) zip.CreateEntryFromFile(path, entryName);
        }
        catch (Exception)
        {
            // 读不到就跳过：诊断包应该是"尽力而为"，不能因为缺一个文件就整包失败
        }
    }
}
