using System.Text.Json.Serialization;

namespace UMango.Core.Feedback;

/// <summary>
/// 一条反馈（issue）。
///
/// 对应 PCL 的 <c>PageSetupFeedback.Feedback</c>（`Plain Craft Launcher 2/Pages/PageSetup/PageSetupFeedback.xaml.cs`，
/// 位于**受限许可目录** → 只借鉴 JSON 形状与分类语义，重新实现）。
///
/// PCL 那边的 JSON 来自 GitHub Issues API（<c>title</c> / <c>body</c> / <c>created_at</c> /
/// <c>html_url</c> / <c>user.login</c> / <c>labels[].id</c>）。
/// UMango 的源可配置，但**形状保持一致** —— 这样任何兼容 GitHub Issues 形状的源都能直接用。
/// </summary>
public sealed class FeedbackEntry
{
    /// <summary>标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>正文。</summary>
    [JsonPropertyName("body")]
    public string Content { get; set; } = "";

    /// <summary>编号 / 唯一标识（PCL 用 issue 的 number）。</summary>
    [JsonPropertyName("number")]
    public string Id { get; set; } = "";

    /// <summary>提交者（PCL 用 user.login）。</summary>
    [JsonPropertyName("user")]
    public FeedbackUser? User { get; set; }

    /// <summary>提交时间（ISO 8601 字符串）。</summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>网页链接。</summary>
    [JsonPropertyName("html_url")]
    public string? Url { get; set; }

    /// <summary>标签数组（PCL 用 label 的 id 字符串）。</summary>
    [JsonPropertyName("labels")]
    public List<FeedbackLabel> Labels { get; set; } = [];

    /// <summary>
    /// 分类名。PCL 从 <c>type.name</c> 取，UMango 兼容两种形状：
    /// 顶层 <c>type</c> 字段，或某个 label 的 <c>name</c>。
    /// </summary>
    [JsonPropertyName("type")]
    public FeedbackType? Type { get; set; }
}

/// <summary>提交者信息。</summary>
public sealed class FeedbackUser
{
    /// <summary>用户名（GitHub 是 <c>login</c>）。</summary>
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";
}

/// <summary>标签。</summary>
public sealed class FeedbackLabel
{
    /// <summary>标签名。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>标签 id（PCL 用它做分类键）。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

/// <summary>反馈分类。</summary>
public sealed class FeedbackType
{
    /// <summary>分类名。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>反馈列表的 JSON 根节点（GitHub Issues API 的形状）。</summary>
public sealed class FeedbackList
{
    /// <summary>反馈条目。</summary>
    [JsonPropertyName("items")]
    public List<FeedbackEntry> Items { get; set; } = [];
}
