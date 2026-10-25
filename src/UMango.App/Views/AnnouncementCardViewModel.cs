using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Localization;
using UMango.App.Services;
using UMango.Core.Announcements;
using UMango.Shell.Services;

namespace UMango.App.Views;

/// <summary>
/// 主页「公告」区的 ViewModel。
///
/// 与 <see cref="HomepageCardViewModel"/> 的差别：
///   · 主页清单是**用户自己放的文件**（本地声明式 JSON）；
///   · 公告是**远程内容**（设置页填的源），所以按钮必须走白名单，
///     而且这里**只展示**、不自动执行任何动作 —— 用户点一下才动。
/// </summary>
public partial class AnnouncementSectionViewModel : ObservableObject
{
    private readonly AnnouncementHost _host;

    internal AnnouncementSectionViewModel(AnnouncementHost host)
    {
        _host = host;
    }

    /// <summary>公告行。</summary>
    public ObservableCollection<AnnouncementRowViewModel> Items { get; } = [];

    /// <summary>状态/提示行。</summary>
    [ObservableProperty]
    private string _hint = "";

    /// <summary>是否有公告。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>抓取公告（没配源就直接给提示，不发请求）。</summary>
    [RelayCommand]
    private async Task Load()
    {
        try
        {
            var result = await _host.LoadAsync();
            Items.Clear();
            foreach (var a in result.Items)
                Items.Add(new AnnouncementRowViewModel(_host, a));

            Hint = result.Status switch
            {
                AnnouncementFetchStatus.NoSource => ViewText.L("Announcement.NoSource"),
                AnnouncementFetchStatus.Failed => ViewText.L("Announcement.Failed", result.Error ?? ""),
                _ when result.Items.Count == 0 => ViewText.L("Announcement.NoNew"),
                _ => ""
            };

            OnPropertyChanged(nameof(HasItems));
        }
        catch (Exception ex)
        {
            Hint = ViewText.L("Announcement.Failed", ex.Message);
        }
    }

    /// <summary>把当前这批公告标记为已展示（下次不再弹）。</summary>
    [RelayCommand]
    private void MarkAllShown()
    {
        _host.MarkShown(Items.Select(i => i.Announcement));
    }
}

/// <summary>公告列表里的一行。</summary>
public sealed partial class AnnouncementRowViewModel : ObservableObject
{
    private readonly AnnouncementHost _host;

    internal AnnouncementRowViewModel(AnnouncementHost host, Announcement announcement)
    {
        _host = host;
        Announcement = announcement;
        Title = announcement.Title;
        Detail = announcement.Detail;
        DateText = announcement.Date ?? "";
        // 按钮对象总是存在（空文案时 HasBtn 为 false，由界面隐藏）——
        // 用 null + 转换器会让 XAML 多一层"判空"逻辑，而界面本来就要判"要不要显示"。
        Btn1 = new AnnouncementButtonViewModel(announcement.Btn1);
        Btn2 = new AnnouncementButtonViewModel(announcement.Btn2);
    }

    /// <summary>原始公告（标记已展示时要用）。</summary>
    public Announcement Announcement { get; }

    public string Title { get; }
    public string Detail { get; }
    public string DateText { get; }
    public AnnouncementButtonViewModel Btn1 { get; }
    public AnnouncementButtonViewModel Btn2 { get; }

    /// <summary>是否要显示第一个按钮。</summary>
    public bool HasBtn1 => Btn1.HasButton;

    /// <summary>是否要显示第二个按钮。</summary>
    public bool HasBtn2 => Btn2.HasButton;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasDate => !string.IsNullOrWhiteSpace(DateText);

    /// <summary>点击按钮后的结果提示（未注册的动作会在这里说明）。</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>执行第一个按钮。</summary>
    [RelayCommand]
    private void RunBtn1() => Run(Btn1);

    /// <summary>执行第二个按钮。</summary>
    [RelayCommand]
    private void RunBtn2() => Run(Btn2);

    private void Run(AnnouncementButtonViewModel button)
    {
        if (!button.HasButton) return;
        var name = button.Command ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = ViewText.L("Announcement.NoAction");
            return;
        }
        Status = _host.InvokeAction(name, button.CommandParameter)
            ? ""
            : ViewText.L("Homepage.UnknownAction", name);
    }
}

/// <summary>公告上的一个按钮。</summary>
public sealed class AnnouncementButtonViewModel
{
    internal AnnouncementButtonViewModel(AnnouncementButton? button)
    {
        Command = button?.Command ?? "";
        CommandParameter = button?.CommandParameter ?? "";
        Text = button?.Text ?? "";
    }

    /// <summary>动作名（远程内容给的；白名单之外的会被拒绝）。</summary>
    public string Command { get; }

    /// <summary>动作参数（远程内容给的；只作为字符串传给已注册的动作）。</summary>
    public string CommandParameter { get; }

    public string Text { get; }

    /// <summary>是否真的要显示这个按钮（公告没给按钮，或只给了文案没给动作，都不显示）。</summary>
    public bool HasButton => !string.IsNullOrWhiteSpace(Text) && !string.IsNullOrWhiteSpace(Command);
}
