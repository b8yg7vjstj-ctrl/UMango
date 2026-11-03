// SPDX-License-Identifier: Apache-2.0
//
// 左栏导航 Key → SVG 图标标识 的映射表。
//
// 为什么放在 Shell（而不是某个壳）：
//   · 图标资源本身已上移到 UMango.Shell（两个壳共用一份）；
//   · 映射关系是纯数据，与 UI 框架无关 —— 放在壳里意味着两个壳各维护一份，
//     加一个导航项时必然有一边忘记补图标；
//   · NavItem.Icon 直接由 Key 推导，两个壳的模板都只绑一个属性即可。
//
// 图标标识写法是「包名/图标名」，例如 lucide/home（包见 Assets/IconPacks/）。

namespace UMango.Shell.Icons;

/// <summary>
/// 左栏导航 Key → SVG 图标标识 的映射表。
/// </summary>
public static class NavIconMap
{
    /// <summary>图标包名（对应 <c>Assets/IconPacks/lucide</c> 目录）。</summary>
    public const string Pack = "lucide";

    /// <summary>
    /// Key → 图标名（不含包名）。Key 与 ShellViewModel 里 MenuFor 的 Key 一一对应。
    ///
    /// 图标名的选取原则：与 PCL-CE 顶部 banner / 左栏的语义对齐，
    /// 且全部取自 lucide 里语义最接近的那一个（不另造图标）。
    /// </summary>
    private static readonly Dictionary<string, string> Icons = new(StringComparer.Ordinal)
    {
        ["overview"] = "home",          // 概览 → 主页
        ["records"] = "list-checks",    // 记录 → 清单
        ["sources"] = "database",       // 包源 → 数据库
        ["results"] = "list",           // 搜索结果 → 列表
        ["favorites"] = "star",         // 收藏 → 星标
        ["running"] = "loader",         // 进行中 → 加载中
        ["finished"] = "circle-check",  // 已完成 → 对勾
        ["theme"] = "palette",          // 主题 → 调色板
        ["debug"] = "bug",              // 调试 → 缺陷
        ["recordManage"] = "scale",     // 记录管理 → 标尺
        ["downloader"] = "download",    // 下载器 → 下载
        ["logs"] = "scroll-text",       // 日志 → 文本
        ["diagnostics"] = "activity",   // 诊断 → 活动曲线
        ["licenses"] = "package-search",// 许可 → 开源包
        ["feedback"] = "message-square",// 反馈 → 留言
        ["music"] = "music",            // 音乐
        ["about"] = "info",             // 关于
    };

    /// <summary>
    /// 取导航 Key 对应的图标标识（「包名/图标名」）。没有对应图标时返回空串。
    /// </summary>
    public static string Resolve(string? navKey)
    {
        if (string.IsNullOrEmpty(navKey))
            return string.Empty;
        return Icons.TryGetValue(navKey, out var name) ? $"{Pack}/{name}" : string.Empty;
    }
}
