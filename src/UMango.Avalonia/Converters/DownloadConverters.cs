// SPDX-License-Identifier: Apache-2.0
//
// 下载页专用的值转换器 —— 与 WPF 侧 `src/UMango.App/Views/DownloadView.xaml.cs`
// 末尾那批页面内转换器同构（WPF 把它们塞在页面文件里，是因为那边约定"一页只加一个 .xaml.cs"；
// Avalonia 侧沿用本壳既有做法，统一放在 Converters/ 下）。
//
// 这里只做**展示层映射**，不含业务规则；任何情况下都不抛异常 ——
// 绑定求值抛出去会让整行渲染失败（表现为一片空白，比显示"未知"糟得多）。

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using UMango.Core.Localization;
using UMango.Core.PackageManagers;

namespace UMango.Avalonia.Converters;

/// <summary>版本号文案；为空时明确写「未知」而不是留白（留白会让人以为界面少渲染了东西）。</summary>
public sealed class VersionLabelConverter : IValueConverter
{
    public static VersionLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string version && !string.IsNullOrWhiteSpace(version)
            ? LocalizationService.Text("Download.Version", version)
            : LocalizationService.Text("Download.VersionUnknown");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>许可证文案；为空时写「未知」。</summary>
public sealed class LicenseLabelConverter : IValueConverter
{
    public static LicenseLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string license && !string.IsNullOrWhiteSpace(license)
            ? LocalizationService.Text("Download.License", license)
            : LocalizationService.Text("Download.LicenseUnknown");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 下载量文案。
///
/// <para>
/// 需求硬要求：<see cref="PackageInfo.DownloadCount"/> 为空时显示 **「暂无数据」**，
/// 不能显示 0 或空白 —— 0 会被误读成"确实没人下载"。
/// </para>
/// <para>
/// 至于"这个数字其实是 GitHub Stars 兜底来的"，由卡片上的角标单独标注
/// （绑 <c>DownloadCountIsFallback</c>），不能只靠这里的文案 —— 那是之前修过的真实缺陷。
/// </para>
/// </summary>
public sealed class DownloadCountLabelConverter : IValueConverter
{
    public static DownloadCountLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long? count = value switch
        {
            long l => l,
            int i => i,
            _ => null,
        };

        return count is > 0
            ? LocalizationService.Text("Download.Downloads", count.Value.ToString("N0", CultureInfo.InvariantCulture))
            : LocalizationService.Text("Download.DownloadsNone");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 左栏包源的状态圆点：<see cref="AvailabilityState"/> → 画刷。
///
/// <para>
/// WPF 版用 4 个 DataTrigger 切 <c>Fill</c>；Avalonia 没有 DataTrigger，
/// 只能把"状态 → 画刷"搬进转换器。两边口径必须一致（同一枚举、同一 token）：
/// 可用=主题蓝、降级=琥珀、未安装/不可达=红、平台不支持/其它=中性灰。
/// </para>
/// </summary>
public sealed class AvailabilityStateToBrushConverter : IValueConverter
{
    public static AvailabilityStateToBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is AvailabilityState state
            ? state switch
            {
                AvailabilityState.Available => "BrC3",
                AvailabilityState.Degraded => "BrWarnAmber",
                AvailabilityState.NotInstalled => "BrRed",
                AvailabilityState.Unreachable => "BrRed",
                _ => "BrG4",
            }
            : "BrG4";

        var app = global::Avalonia.Application.Current;
        if (app is not null && app.TryGetResource(key, null, out var found) && found is IBrush brush)
            return brush;

        return new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
