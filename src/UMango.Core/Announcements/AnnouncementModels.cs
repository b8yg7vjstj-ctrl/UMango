using System.Text.Json.Serialization;

namespace UMango.Core.Announcements;

/// <summary>
/// 一条公告。
///
/// 对应 PCL 的 `VersionAnnouncementContentModel`（Apache-2.0 语义，但 PCL 那份在受限许可目录，
/// 这里按同样的 JSON 形状重新定义）。
/// </summary>
public sealed class Announcement
{
    /// <summary>公告标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>公告正文。</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    /// <summary>唯一标识 —— 用来判断"这条是不是已经展示过了"。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>发布日期（原样保留字符串，不做解析 —— 不同源的格式不统一）。</summary>
    [JsonPropertyName("date")]
    public string? Date { get; set; }

    /// <summary>第一个按钮。</summary>
    [JsonPropertyName("btn1")]
    public AnnouncementButton? Btn1 { get; set; }

    /// <summary>第二个按钮。</summary>
    [JsonPropertyName("btn2")]
    public AnnouncementButton? Btn2 { get; set; }
}

/// <summary>公告上的一个按钮。</summary>
public sealed class AnnouncementButton
{
    /// <summary>按钮文字。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>点击后要执行的动作名。</summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>动作的参数。</summary>
    [JsonPropertyName("command_paramter")]
    public string? CommandParameter { get; set; }
}

/// <summary>公告列表的 JSON 根节点。</summary>
public sealed class AnnouncementList
{
    /// <summary>公告条目。</summary>
    [JsonPropertyName("content")]
    public List<Announcement> Content { get; set; } = [];
}
