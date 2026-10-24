using UMango.App.ViewModels;
using UMango.Core.UI.Navigation;

namespace UMango.App.Services;

/// <summary>
/// 左栏菜单 ↔ 导航栈 的**映射规则**（纯逻辑，可测）。
///
/// 为什么单独抽出来：这套规则住在 <c>MainWindow</c> 里时无法在 macOS 上测试
/// （MainWindow 是 WPF 类型）。抽成纯函数后，"点返回能不能正确回到上一层"
/// 这种最容易出错的逻辑就有了回归测试。
///
/// 页面标识的格式约定：<c>{选项卡}/{菜单键}</c>，例如 <c>Tools/logs</c>。
/// 两段都要，因为"返回"时必须同时还原**选项卡**和**左栏高亮项** ——
/// 只还原一个的话，回到上一层就会看到选项卡和内容对不上。
/// </summary>
public static class NavStackMapping
{
    /// <summary>分隔符。</summary>
    public const char Separator = '/';

    /// <summary>组合出一个页面标识。</summary>
    public static string PageId(AppTab tab, string navKey) => $"{tab}{Separator}{navKey}";

    /// <summary>
    /// 拆一个页面标识。格式不对时返回 false。
    /// </summary>
    public static bool TryParse(string? pageId, out AppTab tab, out string navKey)
    {
        tab = default;
        navKey = string.Empty;

        if (string.IsNullOrWhiteSpace(pageId)) return false;

        var slash = pageId.IndexOf(Separator);
        if (slash <= 0 || slash >= pageId.Length - 1) return false;

        if (!Enum.TryParse(pageId[..slash], out tab)) return false;

        navKey = pageId[(slash + 1)..];
        return navKey.Length > 0;
    }

    /// <summary>
    /// 根据左栏选中项，决定导航栈该怎么变。
    ///
    /// 返回 null 表示"不需要动栈"（例如重复点选同一项）。
    /// </summary>
    /// <param name="nav">当前选中的菜单项。</param>
    /// <param name="tab">当前选项卡。</param>
    /// <param name="currentPageId">当前已在栈里的页面标识（可能为空）。</param>
    public static NavigationEntry? Resolve(NavItem? nav, AppTab tab, string? currentPageId)
    {
        if (nav is null) return null;

        var pageId = PageId(tab, nav.Key);

        if (nav.IsSecondary)
        {
            // 已经在这一页 → 不要再压一层（否则连点两次会压出两层，返回要多点一次）。
            if (string.Equals(currentPageId, pageId, StringComparison.Ordinal)) return null;

            return new NavigationEntry(new NavigationPageId(pageId), nav.Title, true, nav.Key);
        }

        // 主视图：清栈回根。
        return new NavigationEntry(new NavigationPageId(pageId), nav.Title, false, nav.Key);
    }
}
