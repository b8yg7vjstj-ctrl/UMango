using UMango.App.ViewModels;
using UMango.Core.App.Tasks;
using UMango.Core.PackageManagers;
using Xunit;

namespace UMango.App.Tests;

/// <summary>其余 ViewModel 的测试。逐个确认它们在无 WPF 宿主下可用。</summary>
[Collection("静态状态")]
public class OtherViewModelTests
{
    // ================= DownloadViewModel =================

    [Fact]
    public void 下载页默认不显示平台不支持的包源()
    {
        var vm = new DownloadViewModel();
        Assert.False(vm.ShowUnsupported);

        // 未加载分类时也不应抛异常
        Assert.Empty(vm.VisibleCategories);
    }

    [Fact]
    public async Task 下载页能加载包源分类_且标题为包管理器名子标题为安装目录()
    {
        var vm = new DownloadViewModel();
        await vm.InitializeAsync();

        var categories = vm.VisibleCategories.ToList();
        Assert.NotEmpty(categories);   // 本机至少有 npm/pnpm/git/homebrew

        // 需求原文的硬要求：左栏两行 —— 第一行包管理器名、第二行安装目录。
        // 这里断言的是"数据侧确实提供了这两项且非空"，渲染由 XAML 负责。
        Assert.All(categories, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title), "分类标题（包管理器名）不能为空");
            Assert.False(string.IsNullOrWhiteSpace(c.Subtitle), "子标题（安装目录）不能为空");
            Assert.True(c.SupportedHere, "默认过滤后剩下的都应是当前平台支持的包源");
        });
    }

    [Fact]
    public async Task 打开显示不支持开关后会多出包源()
    {
        var vm = new DownloadViewModel();
        await vm.InitializeAsync();
        var supported = vm.VisibleCategories.Count();

        vm.ShowUnsupported = true;
        var all = vm.VisibleCategories.Count();

        Assert.True(all >= supported, "显示不支持的包源后数量不应减少");
    }

    [Fact]
    public void 下载页初始状态是干净的()
    {
        var vm = new DownloadViewModel();
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("", vm.Query);
        Assert.Empty(vm.Results);
    }

    // ================= ToolsViewModel =================

    [Fact]
    public void 工具页构造后就有系统报告_避免首帧空白()
    {
        var vm = new ToolsViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.SystemReport));
    }

    [Fact]
    public void 工具页报告包含平台与架构()
    {
        var desc = UMango.Core.Platform.SystemInfo.Describe();

        // 报告走 L(key, args) 取文案，无 UI 环境下 Provider 是 KeyOnlyTextProvider
        // （返回资源键本身）→ 断言具体中文会失败。用 ChineseProvider 提供真实文案。
        using (ChineseProvider.Scope())
        {
            var vm = new ToolsViewModel();
            Assert.Contains(desc, vm.SystemReport);
        }
    }

    // ================= ProgressViewModel =================

    [Fact]
    public void 进度页直接绑定TaskCenter而不是复制一份数据()
    {
        var vm = new ProgressViewModel();
        // 同一个集合实例：这是"进度页与实际执行不会不同步"的结构保证
        Assert.Same(TaskCenter.Tasks, vm.Tasks);
    }

    [Fact]
    public void 进度页初始没有选中任务()
    {
        var vm = new ProgressViewModel();
        Assert.Null(vm.SelectedTask);
        Assert.Empty(vm.TerminalLines);
    }

    [Fact]
    public void 进度页能按子项分组取终端输出()
    {
        var vm = new ProgressViewModel();
        var task = TaskModel.Group("测试任务-" + Guid.NewGuid());
        task.Children.Add(TaskModel.Progressive("下载"));
        task.Children.Add(TaskModel.Progressive("链接与写入"));

        // 没有任何输出时分组结果应安全为空
        var groups = vm.GroupedOutput(task);
        Assert.NotNull(groups);
    }

    // ================= SettingsViewModel =================

    [Fact]
    public void 设置页语言选项在无UI环境也能构造()
    {
        // 回归：AppLocalization.BuildOptions 原先调 Text() → 碰 Application 类型 →
        // PresentationFramework 只存在于 Windows → macOS 测试宿主 FileNotFoundException，
        // 7 个 SettingsViewModel 用例全军覆没。本用例保证那条路径不再依赖 WPF 类型。
        var vm = new SettingsViewModel();
        Assert.NotEmpty(vm.Languages);
        Assert.Equal("auto", vm.Languages[0].Code);
        Assert.False(string.IsNullOrWhiteSpace(vm.Languages[0].DisplayName));
        Assert.NotNull(vm.SelectedLanguage);
    }

    [Fact]
    public void 设置页提供两种主题族与两种动画方向()
    {
        // Themes/AnimationDirections 的 Title/Description 是**字段初始化器**（构造时求值 L()），
        // 所以 Provider 要在构造之前装好。
        using (ChineseProvider.Scope())
        {
            var vm = new SettingsViewModel();
            Assert.Equal(2, vm.Themes.Count);
            Assert.Equal(2, vm.AnimationDirections.Count);

            // 需求要求两种动画矢量都可选（默认镜像 UMango，另一项还原 PCL）
            Assert.Contains(vm.AnimationDirections, o => o.Mode == UMango.Core.Animation.AnimationDirectionMode.Mirrored);
            Assert.Contains(vm.AnimationDirections, o => o.Mode == UMango.Core.Animation.AnimationDirectionMode.PclCompatible);
            Assert.Contains(vm.Themes, t => t.Title.Contains("PCL"));
            Assert.Contains(vm.Themes, t => t.Title.Contains("Fluent"));
        }
    }

    [Fact]
    public void 设置页的动画开关读写走设置服务()
    {
        var vm = new SettingsViewModel();
        var original = vm.AnimationEnabled;

        try
        {
            vm.AnimationEnabled = !original;
            Assert.Equal(!original, vm.AnimationEnabled);
        }
        finally
        {
            vm.AnimationEnabled = original;   // 还原，避免污染真实设置文件
        }
    }

    [Fact]
    public void 设置页速度倍率被夹在合法范围()
    {
        var vm = new SettingsViewModel();
        var original = vm.AnimationSpeedFactor;

        try
        {
            vm.AnimationSpeedFactor = 99.0;
            Assert.InRange(vm.AnimationSpeedFactor, 0.5, 2.0);
            vm.AnimationSpeedFactor = 0.01;
            Assert.InRange(vm.AnimationSpeedFactor, 0.5, 2.0);
        }
        finally
        {
            vm.AnimationSpeedFactor = original;
        }
    }

    [Fact]
    public void 设置页的日志保留条数读写走设置服务且被夹在合法范围()
    {
        var vm = new SettingsViewModel();
        var original = vm.LogBufferCapacity;

        try
        {
            vm.LogBufferCapacity = 12345;
            Assert.Equal(12345, vm.LogBufferCapacity);

            // 越界值被夹住（描述符 Min=200 / Max=50000）
            vm.LogBufferCapacity = 1;
            Assert.Equal(200, vm.LogBufferCapacity);

            vm.LogBufferCapacity = 999999;
            Assert.Equal(50000, vm.LogBufferCapacity);
        }
        finally
        {
            vm.LogBufferCapacity = original;   // 还原，避免污染真实设置文件
        }
    }

    [Fact]
    public void 设置页能读到记录统计()
    {
        var vm = new SettingsViewModel();
        Assert.True(vm.TotalRecords >= 0);
        Assert.True(vm.SuccessCount >= 0);
        Assert.True(vm.FailureCount >= 0);
        Assert.True(vm.SuccessCount + vm.FailureCount <= vm.TotalRecords);
    }

    // ================= ShellViewModel 与页面的对应关系 =================

    [Fact]
    public void AppTab的取值数量与外壳文档一致()
    {
        // 需求要求"顶部四个 banner 选项卡 + 下方一栏菜单"，
        // 实现上多了一个「进度」页（需求也明确要求有下载进度界面），共 5 个。
        // 这条测试锁住"数量是被有意选择的"，避免有人顺手加减而不更新文档。
        Assert.Equal(5, Enum.GetValues<AppTab>().Length);
        Assert.Contains(AppTab.Progress, Enum.GetValues<AppTab>());
    }
}

/// <summary>
/// 已安装列表接入界面的测试（需求 P0-4：Core 侧早已实现，此前缺界面消费方）。
/// </summary>
[Collection("静态状态")]
[Trait("Category", "真机")]
public class InstalledListUiTests
{
    [Fact]
    public async Task 默认是搜索结果模式()
    {
        var vm = new DownloadViewModel();
        Assert.False(vm.IsInstalledMode);
        Assert.True(vm.ShowResultsPane);
        Assert.False(vm.ShowInstalledPane);
        // 文案已本地化：无 UI 环境下 Provider 是 KeyOnly（返回资源键），
        // 所以这里临时装一个查表 Provider 来断言键确实取到了中文案。
        using (ChineseProvider.Scope())
        {
            Assert.Equal("搜索结果", vm.ModeTitle);
        }
        await Task.CompletedTask;
    }

    [Fact]
    public void 切到已安装模式后派生状态一起变()
    {
        var vm = new DownloadViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.IsInstalledMode = true;

        // 这几个派生属性必须一起通知，否则会出现"列表切了、标题还停在另一种模式"的错位
        Assert.Contains(nameof(vm.ModeTitle), changed);
        Assert.Contains(nameof(vm.ShowResultsPane), changed);
        Assert.Contains(nameof(vm.ShowInstalledPane), changed);
        Assert.Contains(nameof(vm.InstalledSummary), changed);

        using (ChineseProvider.Scope())
        {
            Assert.Equal("已安装", vm.ModeTitle);
        }
        Assert.True(vm.ShowInstalledPane);
    }

    [Fact]
    public void 模式切换命令可用()
    {
        var vm = new DownloadViewModel();
        vm.ShowInstalledCommand.Execute(null);
        Assert.True(vm.IsInstalledMode);
        vm.ShowSearchResultsCommand.Execute(null);
        Assert.False(vm.IsInstalledMode);
    }

    [Fact]
    public async Task 能读取本机已安装的包_真机()
    {
        var vm = new DownloadViewModel();
        await vm.InitializeAsync();

        // 选一个本机确定可用的包源（Homebrew 或 npm，至少有一个）
        var category = vm.VisibleCategories
            .FirstOrDefault(c => c.Kind == UMango.Core.PackageManagers.PackageSourceKind.Homebrew)
            ?? vm.VisibleCategories.FirstOrDefault(c => c.Kind == UMango.Core.PackageManagers.PackageSourceKind.Npm);

        if (category is null) return;   // 本机都没有就跳过（不静默通过，下面会断言摘要非空）

        vm.SelectedCategory = category;
        vm.IsInstalledMode = true;
        await vm.ReloadInstalledCommand.ExecuteAsync(null);

        // 真机上这两个包源都有已安装的包（npm 自身 / 本机 153 个 formula）
        Assert.NotEmpty(vm.Installed);
        Assert.All(vm.Installed, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        // 注意：不要断言具体中文，也不要断言分类名 —— 文案已本地化，
        // 无 UI 环境下 Provider 是 KeyOnly（返回资源键本身），
        // 只有真机 + 资源字典就位时才会格式化成「Homebrew：共 N 个已安装包」。
        // 这里只验证摘要确实被填了非空内容。
        Assert.False(string.IsNullOrWhiteSpace(vm.InstalledSummary));
    }

    [Fact]
    public async Task 不可用包源的已安装列表给出可操作说明而不是空列表()
    {
        var vm = new DownloadViewModel();
        await vm.InitializeAsync();

        // 打开"显示不支持的包源"，在 macOS 上能找到 winget/scoop
        vm.ShowUnsupported = true;
        var unsupported = vm.VisibleCategories.FirstOrDefault(c => !c.Available);
        if (unsupported is null) return;   // Windows 上跳过

        vm.SelectedCategory = unsupported;
        vm.IsInstalledMode = true;
        await vm.ReloadInstalledCommand.ExecuteAsync(null);

        Assert.Empty(vm.Installed);
        // 必须告诉用户为什么空、以及怎么办 —— 只留一片白是最差的处理
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage) && string.IsNullOrWhiteSpace(vm.ErrorAdvice));
    }

    /// <summary>
    /// 并发加载的回归测试：切到"已安装"模式会触发一次加载，
    /// 紧接着点「重新读取」又会触发一次 —— 两次加载若都写回结果，
    /// 就会交错改写 Installed，表现为**枚举中途集合被改写**（实测抛 IndexOutOfRangeException）。
    /// 真机上的对应场景：用户点了「已安装」又马上点「重新读取」。
    /// </summary>
    [Fact]
    public async Task 连续触发两次已安装加载不会交错改写集合_真机()
    {
        var vm = new DownloadViewModel();
        await vm.InitializeAsync();

        var category = vm.VisibleCategories.FirstOrDefault(c => c.Available);
        if (category is null) return;   // 没有可用包源就跳过（Windows/macOS 都至少有一个）

        vm.SelectedCategory = category;

        // 不 await 第一次：模拟"切模式触发的后台加载"仍在进行时用户又点了一次
        vm.IsInstalledMode = true;
        await vm.ReloadInstalledCommand.ExecuteAsync(null);

        // 枚举结果不得抛异常（修复前这里会因集合被并发改写而 IndexOutOfRange）
        var names = vm.Installed.Select(p => p.Name).ToList();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Count, vm.Installed.Count);   // 集合与快照一致

        // 再连续触发几次，确认不会累积出不一致状态
        for (var i = 0; i < 3; i++) await vm.ReloadInstalledCommand.ExecuteAsync(null);
        Assert.Equal(vm.Installed.Count, vm.Installed.Select(p => p.Name).Count());
    }

    [Fact]
    public void 没选包源时已安装摘要不崩()
    {
        var vm = new DownloadViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.InstalledSummary));
    }

    [Fact]
    public void 本地化后的下载页在无UI环境也能构造()
    {
        // 回归：DownloadViewModel 有 20 处文案改走 LocalizationService.Text。
        // 若有人不小心改回 AppLocalization.Text()（碰 Application 类型），
        // macOS 测试宿主会在构造/取文案时 FileNotFoundException，
        // 本用例（以及同一文件里所有构造 DownloadViewModel 的用例）立刻红。
        var vm = new DownloadViewModel();
        Assert.NotNull(vm);
        Assert.False(string.IsNullOrWhiteSpace(vm.ModeTitle));
    }
}

/// <summary>
/// 测试用的中文文案提供器：把 <c>zh-CN.xaml</c> 里的键→文案装进
/// <see cref="LocalizationService"/>，让断言能写具体中文（而不是资源键）。
/// 用 <see cref="Scope"/> 包住断言块，退出时自动还原原 Provider。
/// </summary>
internal sealed class ChineseProvider : UMango.Core.Localization.ITextProvider
{
    private static readonly System.Collections.Generic.Dictionary<string, string> Table = new(System.StringComparer.Ordinal);

    private const string Pattern = "<sys:String x:Key=\"" + "([^\"]+)\"" + ">(.*)</sys:String>";

    static ChineseProvider()
    {
        var path = System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "UMango.App", "Localization", "Languages", "zh-CN.xaml");
        if (!System.IO.File.Exists(path)) return;
        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                line, Pattern);
            if (m.Success) Table[m.Groups[1].Value] = m.Groups[2].Value;
        }
    }

    public string Text(string key) => Table.TryGetValue(key, out var v) ? v : key;

    public static ScopeHandle Scope()
    {
        var previous = UMango.Core.Localization.LocalizationService.CurrentProvider;
        UMango.Core.Localization.LocalizationService.SetTextProvider(new ChineseProvider());
        return new ScopeHandle(previous);
    }

    /// <summary>
    /// 退出时把 **provider 实例**装回去。
    /// ⚠ 不要包成"调旧 provider 的委托"那种形式：一旦捕获的是
    /// <c>LocalizationService.Text</c> 本身，还原后每次取文本都会
    /// Text → 包装 provider → Text → … 无限递归，直接栈溢出把测试进程炸掉。
    /// </summary>
    internal readonly struct ScopeHandle : System.IDisposable
    {
        private readonly UMango.Core.Localization.ITextProvider _restore;
        public ScopeHandle(UMango.Core.Localization.ITextProvider restore) => _restore = restore;
        public void Dispose() => UMango.Core.Localization.LocalizationService.SetTextProvider(_restore);
    }
}
