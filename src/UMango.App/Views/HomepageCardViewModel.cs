using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Localization;
using UMango.App.Services;
using UMango.Core.UI.Homepage;

namespace UMango.App.Views;

/// <summary>
/// 主页「自定义卡片」区的 ViewModel。
///
/// 对应 PCL-CE 的 `Pages/PageLaunch/PageLaunchRight.xaml.cs`（898 行）——
/// 但 PCL 那一版加载并执行**远程 XAML**，UMango 换成**声明式 JSON 清单**
/// （安全结论见 <see cref="HomepageManifest"/>）。
///
/// 这一层只做三件事：
///   1. 把清单卡片转成可绑定的行对象；
///   2. 点链接时用系统默认浏览器打开（只允许 http/https —— Core 已校验过，这里再兜一次）；
///   3. 点动作时转交 <see cref="HomepageHost"/>（动作名必须已注册）。
/// </summary>
public partial class HomepageCardViewModel : ObservableObject
{
    private readonly HomepageHost _host;

    internal HomepageCardViewModel(HomepageHost host, HomepageCard card)
    {
        _host = host;
        Title = card.Title;
        Subtitle = card.Subtitle ?? "";
        Body = card.Body ?? "";
        IconName = card.Icon ?? "";
        Link = card.Link ?? "";
        ActionName = card.Action ?? "";
        ActionArgument = card.ActionArgument ?? "";
        Tags = new ObservableCollection<string>(card.Tags);
    }

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>副标题（无则空串 —— 绑 TextBlock 时空串比 null 更省心）。</summary>
    public string Subtitle { get; }

    /// <summary>正文。</summary>
    public string Body { get; }

    /// <summary>图标名（空则不显示）。</summary>
    public string IconName { get; }

    /// <summary>链接（空则不显示）。</summary>
    public string Link { get; }

    /// <summary>动作名（空则不显示按钮）。</summary>
    public string ActionName { get; }

    /// <summary>动作参数。</summary>
    public string ActionArgument { get; }

    /// <summary>标签。</summary>
    public ObservableCollection<string> Tags { get; }

    /// <summary>是否有副标题。</summary>
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    /// <summary>是否有正文。</summary>
    public bool HasBody => !string.IsNullOrEmpty(Body);

    /// <summary>是否有链接。</summary>
    public bool HasLink => !string.IsNullOrEmpty(Link);

    /// <summary>是否有动作按钮。</summary>
    public bool HasAction => !string.IsNullOrEmpty(ActionName);

    /// <summary>是否有标签。</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>打开链接（只允许 http/https）。</summary>
    [RelayCommand]
    private void OpenLink()
    {
        if (string.IsNullOrEmpty(Link)) return;

        // Core 已经在解析阶段校验过协议；这里再兜一次 —— 界面层的防线不能只建在一层上。
        if (!HomepageManifestParser.IsAllowedLink(Link))
        {
            Status = ViewText.L("Homepage.LinkRejected");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(Link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = string.Format(ViewText.L("Homepage.OpenLinkFailed"), ex.Message);
        }
    }

    /// <summary>状态行（错误提示）。</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>执行动作。</summary>
    [RelayCommand]
    private void RunAction()
    {
        if (string.IsNullOrEmpty(ActionName)) return;
        if (!_host.InvokeAction(ActionName, ActionArgument))
            Status = string.Format(ViewText.L("Homepage.UnknownAction"), ActionName);
    }
}

/// <summary>主页「自定义卡片」区。</summary>
public partial class HomepageSectionViewModel : ObservableObject
{
    private readonly HomepageHost _host;

    internal HomepageSectionViewModel(HomepageHost host)
    {
        _host = host;
        host.ManifestChanged += OnManifestChanged;
    }

    /// <summary>卡片行。</summary>
    public ObservableCollection<HomepageCardViewModel> Cards { get; } = [];

    /// <summary>清单路径（显示给用户看「清单放在哪儿」）。</summary>
    public string ManifestPath => _host.ManifestPath;

    /// <summary>提示行（清单不存在 / 有卡片被丢弃 / 解析失败）。</summary>
    [ObservableProperty]
    private string _hint = "";

    /// <summary>是否有卡片。</summary>
    public bool HasCards => Cards.Count > 0;

    /// <summary>加载清单。</summary>
    public void Load()
    {
        var result = _host.Load();
        Apply(result);
    }

    private void OnManifestChanged(HomepageParseResult result) => Apply(result);

    private void Apply(HomepageParseResult result)
    {
        Cards.Clear();
        if (result.Manifest is not null)
        {
            foreach (var card in result.Manifest.Cards)
                Cards.Add(new HomepageCardViewModel(_host, card));
        }

        Hint = result.Error is { } error
            ? error
            : result.DroppedCards > 0
                ? string.Format(ViewText.L("Homepage.DroppedCards"), result.DroppedCards)
                : result.Manifest is { Cards.Count: 0 }
                    ? ViewText.L("Homepage.NoManifest")
                    : "";

        OnPropertyChanged(nameof(HasCards));
    }
}
