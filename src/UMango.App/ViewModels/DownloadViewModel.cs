using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Services;
using UMango.Core.Execution;
using UMango.Core.Install;
using UMango.Core.PackageManagers;

namespace UMango.App.ViewModels;

/// <summary>
/// 下载页：**包源即分类**（标题=包管理器名、子标题=安装目录）+ 搜索 + 结果卡片 + 安装。
///
/// 设计取舍：
///   · 分类来自 <see cref="PackageManagerRegistry.GetCategoriesAsync"/>，界面不再自己判断平台；
///   · 默认只显示"当前系统上真的能用"的包源（<see cref="VisibleCategories"/>），
///     winget/scoop 在 macOS 上属于"平台不支持"，默认藏起来免得用户白点；
///     需要排查时把 <see cref="ShowUnsupported"/> 打开即可看到全部六个及原因；
///   · 所有耗时动作（探测/搜索/安装）都是 async 命令，绝不用 .Result/.Wait()——
///     在 UI 线程上同步等待 CLI 会死锁。
/// </summary>
public partial class DownloadViewModel : ObservableObject
{
    /// <summary>
    /// 一次搜索取回的条数。CLI 的 search 结果常常几百条且带大量噪声，
    /// 界面又是一屏卡片列表，取一页（24）足够，也顺带把 CLI 的输出解析开销压下来。
    /// </summary>
    private const int SearchLimit = 24;

    /// <summary>包源分类（全部六个，含当前平台不支持的）。</summary>
    public ObservableCollection<PackageSourceCategory> Categories { get; } = [];

    [ObservableProperty]
    private PackageSourceCategory? _selectedCategory;

    [ObservableProperty]
    private string _query = "";

    /// <summary>搜索结果卡片数据源。</summary>
    public ObservableCollection<PackageInfo> Results { get; } = [];

    /// <summary>包源探测/搜索进行中。注意安装**不**置位它，见 Install 的说明。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>面向用户的一句话错误（中文，来自 Core 的消息或我们自己的文案）。</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>与错误配套的"该怎么办"建议；只显示异常消息对用户没有帮助，见 AdviceForCode。</summary>
    [ObservableProperty]
    private string? _errorAdvice;

    /// <summary>当前选中的结果卡片（也允许命令直接传 PackageInfo，两种绑定方式都支持）。</summary>
    [ObservableProperty]
    private PackageInfo? _selectedResult;

    /// <summary>中性提示（成功/无结果等），与红色的 ErrorMessage 分开，页面可分别着色。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    private bool _showUnsupported;
    private bool _initialized;
    private bool _initializing;

    /// <summary>
    /// 是否显示当前平台不支持的包源。
    /// 故意写成手写属性而不是 [ObservableProperty]：它一变，依赖它的
    /// <see cref="VisibleCategories"/> 就变了，必须顺带通知界面重新取列表。
    /// </summary>
    public bool ShowUnsupported
    {
        get => _showUnsupported;
        set
        {
            if (!SetProperty(ref _showUnsupported, value)) return;
            OnPropertyChanged(nameof(VisibleCategories));
        }
    }

    private bool _isInstalledMode;
    private bool _loadingInstalled;

    /// <summary>
    /// 已安装列表的**加载版本号**，用来丢弃过期的加载结果。
    ///
    /// 为什么必须有：切换模式（IsInstalledMode 的 setter）会触发一次加载，
    /// 用户点「重新读取」或切换包源又会触发一次 ——
    /// 两个加载并发时都会对 <see cref="Installed"/> 做 Clear/Add，
    /// 交错执行会导致集合在枚举中途被改写（真机上表现为列表内容错乱或直接抛异常，
    /// 单元测试里表现为 IndexOutOfRangeException）。
    /// 这是被测试真实暴露出来的，不是假想问题。
    /// </summary>
    private int _installedLoadVersion;

    public ObservableCollection<InstalledPackage> Installed { get; } = [];

    /// <summary>
    /// 右栏当前显示"已安装列表"还是"搜索结果"。
    ///
    /// 需求「应用列表读取参考 UnigetUI」在 Core 侧已实现且有测试
    /// （六个适配器的 ListInstalledAsync），但此前**没有界面消费方** ——
    /// 也就是那条需求只做了一半。这个模式开关就是它的落点。
    /// </summary>
    public bool IsInstalledMode
    {
        get => _isInstalledMode;
        set
        {
            if (!SetProperty(ref _isInstalledMode, value)) return;

            // 这几个都是受模式影响的派生状态，必须一起通知，
            // 否则会出现"列表已经切了、标题和空态还停在另一种模式"的错位。
            OnPropertyChanged(nameof(ModeTitle));
            OnPropertyChanged(nameof(ShowResultsPane));
            OnPropertyChanged(nameof(ShowInstalledPane));
            OnPropertyChanged(nameof(InstalledSummary));

            if (value) _ = LoadInstalledAsync();
        }
    }

    /// <summary>搜索结果面板是否可见（与已安装列表互斥）</summary>
    public bool ShowResultsPane => !IsInstalledMode;

    /// <summary>已安装面板是否可见</summary>
    public bool ShowInstalledPane => IsInstalledMode;

    /// <summary>结果区标题（随模式变化）</summary>
    public string ModeTitle => IsInstalledMode ? "已安装" : "搜索结果";

    /// <summary>已安装列表的一句汇总。git 的"已安装"语义不同，要如实说明。</summary>
    public string InstalledSummary
    {
        get
        {
            if (_loadingInstalled) return "正在读取已安装列表…";
            var category = SelectedCategory;
            if (category is null) return "请先在左栏选择一个包源";

            var count = Installed.Count;
            var noun = category.Kind == PackageSourceKind.Git ? "个本地仓库" : "个已安装包";
            return count == 0
                ? $"{category.Title} 下没有已安装的{noun[1..]}"
                : $"{category.Title}：共 {count} {noun}";
        }
    }

    /// <summary>
    /// 左栏实际展示的分类：默认只给"可用"的（Available 含 Degraded），
    /// ShowUnsupported=true 时给全部。
    /// 这里物化成 List：绑定目标是一次性读取的 IEnumerable，
    /// 若沿用 Categories 上的惰性 Where，界面会在集合被刷新时枚举到一半的状态。
    /// </summary>
    public IEnumerable<PackageSourceCategory> VisibleCategories =>
        (ShowUnsupported ? Categories.AsEnumerable() : Categories.Where(c => c.Available)).ToList();

    /// <summary>
    /// 首次进入下载页时调用（幂等）：加载分类。
    /// 刷新走 <see cref="RefreshCommand"/>（会强制重新探测 CLI），
    /// 首次加载用注册表缓存，避免每次切页都去起一遍 npm/pnpm/git 进程。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized || _initializing) return;
        _initializing = true;
        try
        {
            _initialized = await LoadCategoriesAsync(refresh: false);
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>重新探测包源（用户装了新 CLI 之后点它）。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            if (await LoadCategoriesAsync(refresh: true))
                StatusMessage = $"已重新探测 {Categories.Count} 个包源。";
        }
        catch (Exception ex)
        {
            // LoadCategoriesAsync 内部已兜底，这里再拦一层：命令的异常绝不能冒到 UI 线程。
            ApplyError(ex, SelectedCategory);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>切到已安装列表。</summary>
    [RelayCommand]
    private void ShowInstalled() => IsInstalledMode = true;

    /// <summary>切回搜索结果。</summary>
    [RelayCommand]
    private void ShowSearchResults() => IsInstalledMode = false;

    /// <summary>重新读取已安装列表（用户刚在外部装了东西时点它）。</summary>
    [RelayCommand]
    private Task ReloadInstalled() => LoadInstalledAsync();

    /// <summary>
    /// 读取当前包源的已安装列表。
    ///
    /// 各包管理器的"已安装"语义并不相同，这里如实区分（不统一措辞掩盖差异）：
    ///   · npm / pnpm：全局安装的包；
    ///   · Homebrew：已安装的 formula（同一 formula 的多个版本归到 OtherVersions）；
    ///   · scoop / winget：已安装的 app（本机无法验证，适配器里已如实标注）；
    ///   · **git：没有"已安装"概念**，读到的是本地克隆目录里的仓库。
    /// </summary>
    private async Task LoadInstalledAsync()
    {
        var category = SelectedCategory;
        if (category is null)
        {
            Installed.Clear();
            OnPropertyChanged(nameof(InstalledSummary));
            return;
        }

        if (!category.Available)
        {
            // 平台不支持或 CLI 未安装：给出可操作的说明，而不是空列表
            Installed.Clear();
            ErrorMessage = category.Detail ?? $"{category.Title} 当前不可用";
            ErrorAdvice = category.SupportedHere
                ? $"未检测到 {category.Title} 的命令行工具。{category.InstallHint}"
                : $"{category.Title} 只在其它操作系统上可用。";
            OnPropertyChanged(nameof(InstalledSummary));
            return;
        }

        var manager = AppServices.Registry.Find(category.Kind);
        if (manager is null)
        {
            Installed.Clear();
            OnPropertyChanged(nameof(InstalledSummary));
            return;
        }

        _loadingInstalled = true;
        OnPropertyChanged(nameof(InstalledSummary));
        IsBusy = true;
        ErrorMessage = null;
        ErrorAdvice = null;

        // 领一个版本号：本次加载只有在"期间没有发起更新的加载"时才允许写回结果。
        var version = ++_installedLoadVersion;

        try
        {
            var list = await manager.ListInstalledAsync();

            // 两道守卫：
            //   ① 包源没被切走（换了源的结果不该挂到新源名下）；
            //   ② 期间没有更新的加载发起（否则两个加载会交错改写 Installed）。
            if (!IsCurrentLoad(version, category)) return;

            Installed.Clear();
            foreach (var item in list) Installed.Add(item);
            StatusMessage = InstalledSummary;
        }
        catch (Exception ex)
        {
            if (IsCurrentLoad(version, category)) ApplyError(ex, category);
        }
        finally
        {
            // 只有最新那次加载才复位忙碌标记：否则先结束的旧加载会把新加载的忙碌态清掉
            if (version == _installedLoadVersion)
            {
                _loadingInstalled = false;
                IsBusy = false;
                OnPropertyChanged(nameof(InstalledSummary));
            }
        }
    }

    /// <summary>本次加载是否仍是最新的、且包源未被切走。</summary>
    private bool IsCurrentLoad(int version, PackageSourceCategory category)
        => version == _installedLoadVersion && ReferenceEquals(SelectedCategory, category);

    /// <summary>在选中的包源里搜索。</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        ClearMessages();

        var category = SelectedCategory;
        if (category is null)
        {
            ErrorMessage = "请先在左侧选择一个包源分类。";
            ErrorAdvice = "每个分类对应一个包管理器，安装目录显示在分类名下方。";
            return;
        }

        var keyword = Query.Trim();
        if (keyword.Length == 0)
        {
            ErrorMessage = "请输入要搜索的包名或关键词。";
            ErrorAdvice = $"例如在 {category.Title} 里搜索 express、tokio；Git 源可以直接填仓库地址。";
            return;
        }

        // 能力不足时提前收口：Core 会抛 unsupported_feature，但那样要白起一次进程，
        // 而且提示文案不如这里直接（用户想知道的是"这个源不能搜，换一个"）。
        if (!category.Capabilities.Search)
        {
            ErrorMessage = $"{category.Title} 不支持搜索。";
            ErrorAdvice = "该包源只能按已知名称安装，可切换到其它包源搜索。";
            return;
        }

        var manager = AppServices.Registry.Find(category.Kind);
        if (manager is null)
        {
            ErrorMessage = $"未注册 {category.Title} 的适配器。";
            ErrorAdvice = "请点「刷新包源」重新探测。";
            return;
        }

        IsBusy = true;
        try
        {
            var found = await manager.SearchAsync(keyword, SearchLimit);
            Results.Clear();
            foreach (var item in found) Results.Add(item);
            StatusMessage = found.Count == 0
                ? $"没有找到与「{keyword}」匹配的包。"
                : $"在 {category.Title} 中找到 {found.Count} 个结果。";
        }
        catch (Exception ex)
        {
            // 搜索失败必须清空旧结果：留着上一轮的结果会让人以为这次也搜到了东西。
            Results.Clear();
            ApplyError(ex, category);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 安装一个包。
    /// 参数可以直接传 PackageInfo（卡片上的「安装」按钮：CommandParameter="{Binding}"），
    /// 不传则用 <see cref="SelectedResult"/>。
    ///
    /// 为什么这里不置 IsBusy：安装可能跑几十分钟，置位会让整个搜索区在安装期间不可用。
    /// 同时 IAsyncRelayCommand 默认禁止并发执行，安装按钮会在运行期间自己置灰，
    /// 也不会被点两次；进度则由进度页的任务列表实时显示。
    /// </summary>
    [RelayCommand]
    private async Task Install(PackageInfo? package)
    {
        ClearMessages();

        var target = package ?? SelectedResult;
        if (target is null)
        {
            ErrorMessage = "请先选择要安装的包。";
            ErrorAdvice = "点选一张结果卡片，或直接点卡片上的「安装」。";
            return;
        }

        var category = Categories.FirstOrDefault(c => c.Kind == target.Source) ?? SelectedCategory;

        // 预检：CLI 缺失/平台不支持的包源直接给出可执行建议，不必白起一次注定失败的进程。
        if (category is not null && !category.SupportedHere)
        {
            ErrorMessage = $"{category.Title} 在当前系统上不可用。";
            ErrorAdvice = category.Detail ?? "该包源只支持其它操作系统。";
            return;
        }
        if (category is not null && !category.Available)
        {
            ErrorMessage = $"{category.Title} 当前不可用（{category.Detail ?? "未探测到可用的命令行工具"}）。";
            ErrorAdvice = AdviceForCode("cli_missing", category.InstallHint);
            return;
        }

        try
        {
            StatusMessage = $"正在安装 {target.Name}…子项进度与终端输出见「进度」页。";

            // 版本跟随搜索结果里展示的版本，保证"装的就是看到的"；
            // LatestVersion 为 null 时交给适配器自行解析最新版。
            var request = new InstallRequest(target.Source, target.Name, target.LatestVersion);
            var (_, result) = await AppServices.Executor.InstallAsync(request);

            if (result.Success)
            {
                StatusMessage = $"安装完成：{target.Name}（耗时 {result.DurationMs / 1000.0:0.0} 秒）。";
                return;
            }

            ErrorMessage = result.Message ?? $"安装 {target.Name} 失败。";
            ErrorAdvice = AdviceForCode(result.ErrorCode, category?.InstallHint);
        }
        catch (Exception ex)
        {
            ApplyError(ex, category);
        }
    }

    /// <summary>
    /// 切换包源分类时清空上一轮结果与提示。
    ///
    /// 已安装列表也在这里清：它是**按包源读取**的，
    /// 换源后不清就会把上一个包源的清单挂在当前源的名下（串台）。
    /// 若当前正处于"已安装"模式，则顺手为新源重新读一次 —— 否则界面会停在空列表上。
    /// </summary>
    partial void OnSelectedCategoryChanged(PackageSourceCategory? value)
    {
        Results.Clear();
        SelectedResult = null;
        ClearMessages();

        Installed.Clear();
        OnPropertyChanged(nameof(InstalledSummary));
        if (IsInstalledMode) _ = LoadInstalledAsync();
    }

    private async Task<bool> LoadCategoriesAsync(bool refresh)
    {
        ClearMessages();
        var keepKind = SelectedCategory?.Kind;

        try
        {
            var list = await AppServices.Registry.GetCategoriesAsync(refresh);

            Categories.Clear();
            foreach (var category in list) Categories.Add(category);

            // 刷新会产出**全新的分类实例**，选中项必须按 Kind 重新定位：
            // 直接保留旧实例会让 ComboBox 的选中项指向一个不在列表里的对象，表现为"选中丢失"。
            SelectedCategory = (keepKind is { } kind
                    ? Categories.FirstOrDefault(c => c.Kind == kind)
                    : null)
                ?? VisibleCategories.FirstOrDefault();

            OnPropertyChanged(nameof(VisibleCategories));
            return true;
        }
        catch (Exception ex)
        {
            ApplyError(ex, SelectedCategory);
            return false;
        }
    }

    private void ClearMessages()
    {
        ErrorMessage = null;
        ErrorAdvice = null;
        StatusMessage = null;
    }

    /// <summary>
    /// 把异常翻译成「一句话说清发生什么」+「一句话说清怎么办」。
    ///
    /// 为什么不直接显示 ex.Message：ProcessException 的消息是给日志看的
    /// （"npm search express 失败（退出码 1）"），用户既不知道原因也不知道下一步；
    /// 而 <see cref="ProcessException.Code"/> 是机器可读的，正好用来分支出可执行建议。
    /// </summary>
    private void ApplyError(Exception ex, PackageSourceCategory? category)
    {
        switch (ex)
        {
            case ProcessException pe:
                ErrorMessage = pe.Message;
                ErrorAdvice = AdviceForCode(pe.Code, category?.InstallHint);
                break;

            case OperationCanceledException:
                ErrorMessage = "操作已取消。";
                ErrorAdvice = "可以重新发起；已下载的内容由包管理器自行管理。";
                break;

            default:
                ErrorMessage = ex.Message;
                ErrorAdvice = "出现未预期的错误，可重试；若持续出现请到「工具」页查看系统诊断。";
                break;
        }
    }

    /// <summary>
    /// 错误码 → 兜底建议文案。这些文案直接决定用户下一步动作，所以宁可啰嗦也不留空。
    /// 错误码来自 Core（ProcessException.Code / InstallResult.ErrorCode）。
    /// </summary>
    private static string AdviceForCode(string? code, string? installHint) => code switch
    {
        "cli_missing" => string.IsNullOrWhiteSpace(installHint)
            ? "未找到该包源的命令行工具，请先安装它，然后回到本页点「刷新包源」。"
            : $"请先安装该命令行工具（{installHint}）",
        "unsupported_feature" => "该包源不支持搜索，可换一个包源，或直接按包名安装。",
        "unsupported_platform" => "该包源在当前系统上不可用，请换用跨平台的包源。",
        "timeout" => "网络或源不可达，可重试；若一直失败请检查代理设置后重试。",
        "network_error" => "网络或源不可达，可重试；若一直失败请检查代理设置后重试。",
        "package_not_found" => "没有找到该包，请确认包名拼写或换一个关键词。",
        "parse_error" => "命令行的输出无法解析（可能是 CLI 版本差异），可在进度页打开调试模式查看原始输出。",
        "command_failed" => "命令执行失败，可在进度页打开调试模式查看具体输出后再重试。",
        null => "操作未成功，可重试；若持续失败请查看进度页的终端输出。",
        _ => "操作未成功，可重试；若持续失败请查看进度页的终端输出。"
    };
}
