// SPDX-License-Identifier: Apache-2.0
//
// 本文件是 UMango「启动器自定义主页」的**声明式**实现。
//
// 它替代的对象是 PCL-CE 的 `Pages/PageLaunch/PageLaunchRight.xaml.cs`（898 行，
// 位于 **受限许可目录** `Plain Craft Launcher 2/`，只能借鉴设计、不能复制代码）。
//
// ⚠️ **为什么不照搬 PCL 的做法**：
//   PCL 的自定义主页允许**加载并执行远程 XAML**。在进程内解析并实例化远程 XAML，
//   等于开放任意代码执行面（XAML 里可以写 `ObjectDataProvider`、事件处理器、
//   任意类型构造）。PCL 自己也知道这一点，所以配了 `XamlEventSanitizer` 做缓解，
//   但那是黑名单式清洗，不是能力式限制。
//
//   UMango 的做法是**能力式限制**：主页只由**一份 JSON 清单**描述，
//   清单里能表达的东西是**封闭集合**——
//     · 卡片（标题、副标题、正文、图标、链接、标签）
//     · 快捷操作（已注册的操作名 + 参数）
//   解析器**只认这些字段**，没有 XAML、没有类型名、没有委托、没有脚本，
//   因此**无论清单来自哪里都不可能执行代码**。
//
// 这一点是 P2-22「安全再设计」的结论：功能（用户能自定义主页）保留，
// 危险面（远程 XAML 执行）不引入。

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UMango.Core.UI.Homepage;

/// <summary>主页卡片。</summary>
public sealed class HomepageCard
{
    /// <summary>标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>副标题（可空）。</summary>
    public string? Subtitle { get; set; }

    /// <summary>正文（可空）。</summary>
    public string? Body { get; set; }

    /// <summary>图标名（走既有图标表，不接受路径或 URL —— 避免任意文件读取）。</summary>
    public string? Icon { get; set; }

    /// <summary>链接（只允许 http/https）。</summary>
    public string? Link { get; set; }

    /// <summary>标签（纯文本，用于分类显示）。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>快捷操作名（必须是 <see cref="HomepageActionRegistry"/> 里已注册的名字）。</summary>
    public string? Action { get; set; }

    /// <summary>快捷操作的参数（纯文本，不解析为路径或命令）。</summary>
    public string? ActionArgument { get; set; }
}

/// <summary>主页清单。</summary>
public sealed class HomepageManifest
{
    /// <summary>清单标题（显示在页头）。</summary>
    public string Title { get; set; } = "UMango";

    /// <summary>卡片列表。</summary>
    public List<HomepageCard> Cards { get; set; } = [];
}

/// <summary>清单解析结果。</summary>
public sealed class HomepageParseResult
{
    /// <summary>解析成功时是清单。</summary>
    public HomepageManifest? Manifest { get; init; }

    /// <summary>错误信息（解析失败时非空）。</summary>
    public string? Error { get; init; }

    /// <summary>被丢弃的卡片数（解析成功时可能 &gt; 0 —— 无效卡片被跳过而不是整体失败）。</summary>
    public int DroppedCards { get; init; }

    /// <summary>是否成功。</summary>
    public bool Success => Manifest is not null;
}

/// <summary>
/// 主页清单的**安全**解析器。
///
/// 能力边界（这是安全模型本身，不是实现细节）：
///   · 只接受 JSON；不接受 XAML、不接受任何可执行内容；
///   · 字段是封闭集合 —— 未知字段**报错**（而不是忽略），这样清单作者
///     不会误以为某个字段生效了；
///   · 链接只允许 http/https（堵住 file:// / javascript: / data:）；
///   · 图标只接受**名字**（不接受路径 / URL），避免变成任意文件读取器；
///   · 动作只接受**已注册的名字**（白名单），不接受任意命令；
///   · 字符串长度、数量都有上限，防止清单把 UI 撑爆。
/// </summary>
public static class HomepageManifestParser
{
    /// <summary>允许的最大卡片数。</summary>
    public const int MaxCards = 200;

    /// <summary>允许的最大标题长度。</summary>
    public const int MaxTitleLength = 200;

    /// <summary>允许的最大正文字数。</summary>
    public const int MaxBodyLength = 4000;

    /// <summary>允许的最大标签数。</summary>
    public const int MaxTags = 20;

    /// <summary>允许的最大链接长度。</summary>
    public const int MaxLinkLength = 2048;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析清单文本。</summary>
    /// <param name="json">清单 JSON。</param>
    /// <param name="isKnownAction">判断动作名是否已注册（白名单）。</param>
    public static HomepageParseResult Parse(string? json, Func<string, bool>? isKnownAction = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new HomepageParseResult { Error = "清单为空" };

        HomepageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<HomepageManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            return new HomepageParseResult { Error = "清单不是合法 JSON：" + ex.Message };
        }

        if (manifest is null)
            return new HomepageParseResult { Error = "清单内容为空" };

        if (manifest.Title.Length > MaxTitleLength)
            return new HomepageParseResult { Error = $"标题超过 {MaxTitleLength} 字符" };

        if (manifest.Cards.Count > MaxCards)
            return new HomepageParseResult { Error = $"卡片数超过 {MaxCards}" };

        var valid = new List<HomepageCard>();
        var dropped = 0;
        foreach (var card in manifest.Cards)
        {
            if (Validate(card, isKnownAction) is { } error)
            {
                dropped++;
                continue;
            }
            valid.Add(card);
        }

        manifest.Cards = valid;
        return new HomepageParseResult { Manifest = manifest, DroppedCards = dropped };
    }

    /// <summary>校验单张卡片；返回 null 表示合法，否则是错误原因。</summary>
    private static string? Validate(HomepageCard card, Func<string, bool>? isKnownAction)
    {
        if (string.IsNullOrWhiteSpace(card.Title))
            return "标题为空";
        if (card.Title.Length > MaxTitleLength)
            return $"标题超过 {MaxTitleLength} 字符";

        if (card.Subtitle is { Length: > MaxTitleLength })
            return "副标题过长";
        if (card.Body is { Length: > MaxBodyLength })
            return $"正文超过 {MaxBodyLength} 字";

        if (card.Tags.Count > MaxTags)
            return $"标签数超过 {MaxTags}";
        foreach (var tag in card.Tags)
        {
            if (tag.Length > MaxTitleLength)
                return "标签过长";
        }

        if (card.Link is { Length: > 0 } link)
        {
            if (link.Length > MaxLinkLength)
                return $"链接超过 {MaxLinkLength} 字符";
            if (!IsAllowedLink(link))
                return "链接只允许 http/https";
        }

        if (card.Icon is { Length: > 0 } icon)
        {
            if (icon.Length > 64)
                return "图标名过长";
            // 图标只允许**名字**：不允许路径分隔符与 URL 字符。
            if (icon.Contains('/') || icon.Contains('\\') || icon.Contains(':') || icon.Contains('.'))
                return "图标只接受名字，不接受路径";
        }

        if (card.Action is { Length: > 0 } action)
        {
            if (action.Length > 64)
                return "动作名过长";
            if (isKnownAction is not null && !isKnownAction(action))
                return "未注册的动作：" + action;
        }

        if (card.ActionArgument is { Length: > MaxBodyLength })
            return "动作参数过长";

        return null;
    }

    /// <summary>链接是否只允许 http/https。</summary>
    public static bool IsAllowedLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme is "http" or "https";
    }
}

/// <summary>已注册的主页动作。</summary>
public sealed class HomepageActionDescriptor
{
    /// <summary>创建描述符。</summary>
    public HomepageActionDescriptor(string name, string description, bool requiresConfirmation = false)
    {
        Name = name;
        Description = description;
        RequiresConfirmation = requiresConfirmation;
    }

    /// <summary>动作名（清单里写的就是它）。</summary>
    public string Name { get; }

    /// <summary>说明（显示在主页上）。</summary>
    public string Description { get; }

    /// <summary>执行前是否需要用户确认。</summary>
    public bool RequiresConfirmation { get; }
}

/// <summary>主页动作注册表（白名单）。</summary>
public sealed class HomepageActionRegistry
{
    private readonly Dictionary<string, HomepageActionDescriptor> _actions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册一个动作。</summary>
    /// <exception cref="ArgumentException">动作名为空或全是空白。</exception>
    public HomepageActionRegistry Register(HomepageActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // 空名字必须拒掉：一旦注册了空名，`IsRegistered("")` 就是 true，
        // 清单里写 `"action": ""` 的卡片会被当成「动作已注册」而保留 ——
        // 白名单就漏了一个洞。
        if (string.IsNullOrWhiteSpace(descriptor.Name))
            throw new ArgumentException("动作名不能为空。", nameof(descriptor));

        _actions[descriptor.Name] = descriptor;
        return this;
    }

    /// <summary>是否已注册。</summary>
    public bool IsRegistered(string name) => _actions.ContainsKey(name);

    /// <summary>取描述符。</summary>
    public HomepageActionDescriptor? Find(string name) =>
        _actions.TryGetValue(name, out var d) ? d : null;

    /// <summary>全部已注册动作。</summary>
    public IReadOnlyCollection<HomepageActionDescriptor> All => _actions.Values;
}
