using System;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using UMango.Shell.ViewModels;
using UMango.Core.PackageManagers;
using UMango.Core.Localization;
using UMango.App.Localization;
using UMango.Shell.Services;
namespace UMango.App.Views;


/// <summary>
/// 下载页：左栏包源分类（第一行=包管理器名、第二行=安装目录）+ 右栏搜索与结果卡片。
///
/// 页面职责边界（为什么这样切分）：
///   · 所有业务都在 <see cref="DownloadViewModel"/> 里（探测、搜索、安装、错误文案翻译），
///     页面只做三件 XAML 表达不了的事：
///       1. ItemsControl 没有"选中项"概念，点选要回写 ViewModel.SelectedCategory；
///       2. 空态/初始态需要跨多个属性判断（结果数 + 是否搜过 + 是否忙碌 + 是否有错误）；
///       3. 回车触发搜索、按钮点击时记录"已搜过"。
///   · 服务（包源注册表、安装执行器）由 ViewModel 通过 AppServices 单例获取，
///     页面不直接持有服务，避免出现"谁先构造谁生效"的实例不一致问题。
///
/// 线程约定：页面没有任何同步等待（.Result/.Wait()），
/// 首次加载与所有命令都是 await 到异步接口，CLI 跑到线程池上，UI 线程不会被卡住。
/// </summary>
public partial class DownloadView : UserControl
{
    /// <summary>
    /// 是否已经发起过一次搜索。
    /// 为什么需要：需求要求"结果为空**且已搜索过**"才显示空态 —— 一进页面就显示
    /// "没有找到匹配的包"会让人以为搜索失败了；这个状态只存在于界面层，故不放进 ViewModel。
    /// </summary>
    private bool _searchAttempted;

    /// <summary>本页自己的 ViewModel（外壳统一用 new DownloadView() 创建，故构造必须无参）。</summary>
    public DownloadViewModel ViewModel { get; }

    public DownloadView()
    {
        InitializeComponent();

        ViewModel = new DownloadViewModel();
        DataContext = ViewModel;

        // 空态判定跨了三个来源（结果集合、是否搜过、忙碌/错误状态），XAML 的触发器只能看单一属性，
        // 因此在这里合成；订阅通知而不是在每次操作后手动刷新，才不会漏掉 ViewModel 内部的变化。
        ViewModel.Results.CollectionChanged += OnResultsChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 外壳插入可视树后会立刻测量，先把提示摆成初始状态，避免出现一帧的错位。
        UpdateHintVisibility();

        // 首次进入才做包源探测（要起 npm/pnpm/git 进程），故在 Loaded 里 await。
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 只初始化一次：页面在外壳里可能被反复卸载/挂载，而"重新探测 CLI"不该每次切页都做。
        // ViewModel.InitializeAsync 本身也是幂等的，这里取消订阅只是省掉重复调用。
        Loaded -= OnLoaded;

        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception)
        {
            // InitializeAsync 内部已把失败写进 ErrorMessage（界面会显示错误条 + 建议）；
            // 这里是 async void 的兜底：异常直接冒到 Dispatcher 会终结整个应用。
        }

        UpdateHintVisibility();
    }

    // ===== 界面事件的三个收口 =====

    /// <summary>点选左栏的包源分类。</summary>
    private void SourceCategory_Click(object sender, MouseButtonEventArgs e)
    {
        // ItemsControl 没有 SelectedItem，选中状态由 ViewModel 持有（刷新后按 Kind 重新定位，见 VM）。
        if (sender is FrameworkElement { DataContext: PackageSourceCategory category })
        {
            ViewModel.SelectedCategory = category;
        }
    }

    /// <summary>搜索框回车 = 点「搜索」。</summary>
    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // 标记为已处理：避免回车继续冒泡触发别的默认行为（例如把焦点交给某个按钮）。
        e.Handled = true;

        _searchAttempted = true;
        UpdateHintVisibility();

        // 命令自身带并发保护（执行期间 CanExecute 为 false），这里再判一次免得白跑。
        if (ViewModel.SearchCommand.CanExecute(null))
        {
            ViewModel.SearchCommand.Execute(null);
        }
    }

    /// <summary>
    /// 「搜索」与错误条上的「重试」共用。
    /// 命令绑定在 XAML 上（Button.Command），这里只负责记录"用户搜过了"，
    /// 让空态在搜索结束（结果与状态通知到达）时正确显示。
    /// </summary>
    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _searchAttempted = true;
        UpdateHintVisibility();
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateHintVisibility();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 换包源 = 上一轮的"搜过"作废：ViewModel 切换分类时会清空 Results，
        // 若不清掉这个标记，右栏会显示"没有找到匹配的包" —— 而实际上在新包源里一次都还没搜过。
        if (e.PropertyName == nameof(DownloadViewModel.SelectedCategory))
        {
            _searchAttempted = false;
        }

        UpdateHintVisibility();
    }

    /// <summary>
    /// 合成"空态"与"初始提示"的显隐。二者互斥：
    ///   · 搜过 + 无结果 + 不在忙碌 + 没有错误 → 空态文案；
    ///   · 没搜过 + 无结果                     → 初始提示；
    ///   · 忙碌（正在搜索）或出错               → 两者都不显示（由进度条/错误条接管）。
    /// </summary>
    private void UpdateHintVisibility()
    {
        var noResults = ViewModel.Results.Count == 0;

        // 这两个空态只属于"搜索结果"模式：切到已安装列表后必须收起，
        // 否则会出现"已安装列表是空的，却叠着『没有找到匹配的包』"的错位提示。
        var searchMode = !ViewModel.IsInstalledMode;

        EmptyState.Visibility = searchMode
                                && noResults
                                && _searchAttempted
                                && !ViewModel.IsBusy
                                && string.IsNullOrWhiteSpace(ViewModel.ErrorMessage)
            ? Visibility.Visible
            : Visibility.Collapsed;

        IdleHint.Visibility = searchMode && noResults && !_searchAttempted
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}

// ============================================================================
//  转换器
// ----------------------------------------------------------------------------
//  全部放在本文件（页面只允许新增这一个 .xaml.cs），因此不新建 Converters 目录。
//  它们只做展示层映射，不含业务规则；转换异常一律不抛（绑定失败会让整页显示异常）。
// ============================================================================

/// <summary>
/// 左栏选中判定：把「当前分类项」与「ViewModel.SelectedCategory」做**引用比较**。
///
/// 为什么需要它：规格要求左栏用 ItemsControl（它没有 SelectedItem/IsSelected 这类选中概念），
/// 而 WPF 里把"两个属性是否相等"喂给 Style 触发器的唯一办法就是 MultiBinding + 转换器。
/// 只比引用即可：PackageSourceCategory 是 Core 产出的视图对象（不可变、按 Kind 单实例），
/// 刷新会产出**全新实例**，引用不等就代表"不是当前选中项"。
/// </summary>
public sealed class CategorySelectedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length >= 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("选中状态由 ViewModel 单向驱动，不支持从界面反写。");
}

/// <summary>非空字符串 → Visible；空串/null → Collapsed（用于可选文案行）。</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// 非空 → Visible。
    ///
    /// **同时支持字符串与集合**：原先只判 `value as string`，于是绑到集合（例如
    /// Homebrew 同一 formula 的其它版本 OtherVersions）时**永远返回 Collapsed** ——
    /// 表现为"那段信息静默不显示"，既不报错也看不出来。这是扩展的原因。
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return Visibility.Collapsed;
            case string text:
                return string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            case System.Collections.IEnumerable items:
                // 只判断"有没有元素"，不物化整个序列（可能很大）
                return items.GetEnumerator().MoveNext() ? Visibility.Visible : Visibility.Collapsed;
            default:
                return Visibility.Visible;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("只用于单向显示。");
}

/// <summary>
/// 字符串集合 → 一行文本（用于"同包的其它版本"这类展示）。
///
/// 存在的理由：Homebrew 实测可能同时装了多个版本（<c>brew list --versions</c> 输出
/// <c>brotli 1.1.0 1.2.0</c>），Core 侧把它们收进 OtherVersions，
/// 界面需要把这一串渲染出来而不是只显示第一个。
/// 前缀用"其它版本："而不是裸列表 —— 否则界面上会出现两个相邻的、语义不明的版本号。
/// </summary>
public sealed class JoinToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not System.Collections.IEnumerable items || value is string)
            return "";

        var parts = new List<string>();
        foreach (var item in items)
        {
            if (item?.ToString() is { Length: > 0 } text) parts.Add(text);
        }

        return parts.Count == 0 ? "" : ViewText.L("Download.OtherVersions") + string.Join(" / ", parts);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("只用于单向显示。");
}

/// <summary>
/// 空集合 → Visible（反之 Collapsed）。用于"一个包源都没探测到"的左栏提示：
/// 若不提示，左栏会是一整片空白，用户会以为界面坏了。
/// </summary>
public sealed class EmptyCollectionToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Visible;

        // 字符串按"空内容"判断（与集合语义区分开，避免误用时空列表和空串表现不一致）
        if (value is string text)
            return string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

        if (value is IEnumerable items)
        {
            // 只判断"有没有元素"：取值不算开销（VisibleCategories 已在 ViewModel 里物化成 List）。
            foreach (var _ in items) return Visibility.Collapsed;
            return Visibility.Visible;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("只用于单向显示。");
}

/// <summary>版本号文案；为空时明确写"未知"而不是留白（留白会让人以为界面少渲染了东西）。</summary>
public sealed class VersionLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string version && !string.IsNullOrWhiteSpace(version) ? ViewText.L("Download.Version", version) : ViewText.L("Download.VersionUnknown");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("只用于单向显示。");
}

/// <summary>许可证文案；为空时写"未知"。</summary>
public sealed class LicenseLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string license && !string.IsNullOrWhiteSpace(license) ? ViewText.L("Download.License", license) : ViewText.L("Download.LicenseUnknown");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("只用于单向显示。");
}

/// <summary>
/// 下载量文案。
/// 需求硬要求：<see cref="PackageInfo.DownloadCount"/> 为空时显示 **"暂无数据"**，
/// 不能显示 0 或空白（0 会被误读成"确实没人下载"）。
/// 至于"这个数字其实是 GitHub Stars 兜底来的"，由卡片上的角标单独标注
/// （DownloadCountIsFallback），不能只靠这里的文案 —— 那是之前修过的真实缺陷。
/// </summary>
public sealed class DownloadCountLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long? count = value switch
        {
            long l => l,
            int i => i,
            _ => null
        };

        return count is > 0
            ? ViewText.L("Download.Downloads", count.Value.ToString("N0", CultureInfo.InvariantCulture))
            : ViewText.L("Download.DownloadsNone");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("只用于单向显示。");
}

/// <summary>
/// 包名缩写：给结果卡片左侧的 40x40 方块用。
/// 为什么自绘文字而不是图标：合规要求不得使用 PCL-CE 的图片资源（见 THIRD-PARTY-NOTICES.md），
/// 而按包名现取缩写既不需要网络也不需要资源文件。
/// 规则：作用域包与仓库地址先取最后一段，再取首字母 + 第一个分隔符后的首字母（如 react-dom → RD）。
/// </summary>
public sealed class PackageInitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name) return "?";

        var text = name.Trim().TrimStart('@');
        var slash = text.LastIndexOf('/');
        if (slash >= 0 && slash < text.Length - 1) text = text[(slash + 1)..];

        var builder = new StringBuilder(2);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (!char.IsLetterOrDigit(ch)) continue;

            builder.Append(char.ToUpperInvariant(ch));

            // 首字母之后，再补"第一个分隔符后面的首字母"，形成两字缩写；没有分隔符就只有首字母。
            var rest = text[(i + 1)..];
            var separator = rest.IndexOfAny(new[] { '-', '_', '.' });
            if (separator >= 0)
            {
                foreach (var candidate in rest[(separator + 1)..])
                {
                    if (!char.IsLetterOrDigit(candidate)) continue;
                    builder.Append(char.ToUpperInvariant(candidate));
                    break;
                }
            }
            break;
        }

        return builder.Length > 0 ? builder.ToString() : "?";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("只用于单向显示。");
}
