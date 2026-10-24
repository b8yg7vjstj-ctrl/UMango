using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Localization;
using UMango.App.Services;
using UMango.Core.Feedback;

namespace UMango.App.ViewModels;

/// <summary>反馈页 ViewModel：抓取反馈列表、按分类分组、拼装提交反馈的草稿。</summary>
public partial class FeedbackViewModel : ObservableObject
{
    private readonly FeedbackService _service;
    private readonly string? _sourceOverride;

    /// <summary>
    /// 构造 ViewModel。
    /// </summary>
    /// <param name="service">反馈服务（默认用组合根的 HTTP 与诊断信息）。</param>
    /// <param name="sourceOverride">
    /// 反馈源地址的**覆盖值**。给 null 时从设置里读。
    /// 之所以留这个口子，是因为源地址存在设置里，而测试环境没有设置文件 ——
    /// 没有它，ViewModel 的测试就只能覆盖「未配置源」这一条路径。
    /// </param>
    public FeedbackViewModel(FeedbackService? service = null, string? sourceOverride = null)
    {
        _sourceOverride = sourceOverride;
        _service = service ?? new FeedbackService(
            AppServices.Http,
            new FeedbackTagMap(),
            BuildDiagnostics);
    }

    /// <summary>分组后的反馈（每组一项分类）。</summary>
    public ObservableCollection<FeedbackGroupViewModel> Groups { get; } = [];

    /// <summary>状态提示（加载完成 / 失败原因）。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>错误提示（抓取失败时显示）。</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>是否正在加载。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>反馈源地址（留空 = 不拉取）。</summary>
    public string Source => _sourceOverride
        ?? AppSettings.Settings.Get<string>(Core.Configuration.UiSettings.FeedbackSourceKey);

    /// <summary>提交地址（留空 = 只导出草稿）。</summary>
    public string SubmitUrl => AppSettings.Settings.Get<string>(Core.Configuration.UiSettings.FeedbackSubmitUrlKey);

    /// <summary>是否配置了源（决定界面显示"未配置"还是列表）。</summary>
    public bool HasSource => !string.IsNullOrWhiteSpace(Source);

    /// <summary>预填的反馈正文（提交时用）。</summary>
    [ObservableProperty]
    private string _draftBody = "";

    /// <summary>预填的反馈标题。</summary>
    [ObservableProperty]
    private string _draftTitle = "";

    /// <summary>加载反馈列表。</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _service.FetchAsync(Source);

            Groups.Clear();
            switch (result.Status)
            {
                case FeedbackFetchStatus.NoSource:
                    StatusMessage = ViewText.L("Feedback.NoSource");
                    break;
                case FeedbackFetchStatus.Failed:
                    ErrorMessage = result.Error ?? ViewText.L("Feedback.LoadFailed");
                    StatusMessage = null;
                    break;
                default:
                    foreach (var group in _service.Group(result.Items))
                        Groups.Add(new FeedbackGroupViewModel(group));
                    StatusMessage = ViewText.L("Feedback.Loaded", result.Items.Count);
                    break;
            }

            ApplyDiagnostics();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasSource));
        }
    }

    /// <summary>
    /// 把草稿复制到剪贴板（提交地址为空时的兜底，也是用户想自己贴到任何地方的通用动作）。
    /// </summary>
    [RelayCommand]
    private void CopyDraft()
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(DraftTitle)
                ? DraftBody
                : $"{DraftTitle}\n\n{DraftBody}";
            Clipboard.SetText(text);
            StatusMessage = ViewText.L("Feedback.Copied");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>把诊断信息填进草稿（标题 + 正文）。</summary>
    [RelayCommand]
    private void FillDiagnostics() => ApplyDiagnostics();

    private void ApplyDiagnostics()
    {
        var info = _service.BuildDiagnosticInfo();
        DraftTitle = info.Title;
        DraftBody = info.Content;
    }

    /// <summary>拼装提交反馈时要预填的诊断信息。</summary>
    private static FeedbackDiagnosticInfo BuildDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ViewText.L("Feedback.DiagnosticHeader"));
        sb.AppendLine(ViewText.L("Feedback.DiagnosticVersion", VersionText()));
        sb.AppendLine(ViewText.L("Feedback.DiagnosticPlatform", Core.Platform.SystemInfo.Describe()));
        return new FeedbackDiagnosticInfo
        {
            Content = sb.ToString().TrimEnd()
        };
    }

    private static string VersionText()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly();
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? "unknown"
            : informational.Split('+')[0];
    }
}

/// <summary>反馈页里的一组（一个分类）。</summary>
public sealed class FeedbackGroupViewModel
{
    internal FeedbackGroupViewModel(FeedbackGroup group)
    {
        Category = group.Category;
        Title = ViewText.L($"Feedback.Category.{group.Category}");
        Items = group.Items.Select(i => new FeedbackItemViewModel(i)).ToList();
    }

    /// <summary>分类。</summary>
    public FeedbackCategory Category { get; }

    /// <summary>分类显示名。</summary>
    public string Title { get; }

    /// <summary>组内条目。</summary>
    public IReadOnlyList<FeedbackItemViewModel> Items { get; }
}

/// <summary>一条反馈。</summary>
public sealed class FeedbackItemViewModel
{
    internal FeedbackItemViewModel(FeedbackEntry entry)
    {
        Title = entry.Title;
        Content = entry.Content;
        Id = entry.Id;
        Author = entry.User?.Login ?? "";
        CreatedAt = entry.CreatedAt ?? "";
        Url = entry.Url ?? "";
    }

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>正文。</summary>
    public string Content { get; }

    /// <summary>编号。</summary>
    public string Id { get; }

    /// <summary>提交者。</summary>
    public string Author { get; }

    /// <summary>提交时间。</summary>
    public string CreatedAt { get; }

    /// <summary>网页链接。</summary>
    public string Url { get; }
}
