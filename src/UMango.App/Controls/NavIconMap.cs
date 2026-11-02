// SPDX-License-Identifier: Apache-2.0
//
// 左栏导航图标映射 —— 只属于 WPF 壳这一层。
//
// 为什么放在 App 层而不是 Shell/Core：
//   · Shell 的 <c>NavItem</c>（src/UMango.Shell/ViewModels/ShellViewModel.cs）只有
//     Key / Title / Subtitle / IsSecondary / GroupTitle 五个字段，**没有图标字段**；
//     给它加字段属于共享层改动（会牵动 Avalonia 与 Core 的公共契约），不在本次范围内。
//   · Core 的 <c>IconRegistry</c> 是「图标管理」用的登记/选择机制
//     （对应 PCL 的 IconManager），不是「某个导航项配哪个图标」的映射表，用它不合适。
//   所以：Shell 继续只产出 Key，图标由本文件在 WPF 侧按 Key 查表得到。
//
// 图标资源在 <c>Assets/IconPacks/lucide/</c>（lucide 图标集，ISC 许可，
// 见 THIRD-PARTY-NOTICES.md §2）。标识写法是「包名/图标名」，例如 <c>lucide/home</c>。

using System.Globalization;
using System.Windows.Data;

namespace UMango.App.Controls;

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

/// <summary>
/// 把导航 Key 转成 SVG 图标标识的转换器（供 XAML 绑定用）。
/// </summary>
public sealed class NavIconConverter : IValueConverter
{
    /// <summary>单例（转换器无状态）。</summary>
    public static readonly NavIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => NavIconMap.Resolve(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
