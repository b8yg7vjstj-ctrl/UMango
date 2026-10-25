using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using UMango.Core.Configuration;
using UMango.Core.Logging;
using UMango.Core.UI;

namespace UMango.App.Themes;

/// <summary>
/// 灰度（哀悼日）模式的运行时开关 —— WPF 落地层。
///
/// **为什么改 token 字典而不是给窗口挂 Effect**：
/// WPF 里根本没有 <c>ColorMatrixEffect</c> —— 它只存在于 System.Drawing（GDI+），
/// WPF 的 <see cref="System.Windows.Media.Effects.Effect"/> 派生类里没有色彩矩阵效果。
/// （PCL-CE 那套 <c>GrayProfile</c> 零消费方，多半也卡在这里：它定义了音色，
/// 却没有任何能把它落到界面上的方式。）
/// 能用的只有两条路：① 自己写 <see cref="System.Windows.Media.Effects.ShaderEffect"/>（重），
/// ② 把资源字典里的画笔换成脱色后的值（轻）。这里选 ②。
///
/// **为什么逐个替换画笔而不重建整个字典**：重建要重新解析四套 Tokens.*.xaml，
/// 还得保证与原字典完全同 key；替换只改现有 <see cref="SolidColorBrush"/> 的
/// <see cref="SolidColorBrush.Color"/>，token 集合天然不变，DynamicResource 照旧生效。
///
/// **为什么算色不在本类里**：见 <see cref="GrayColorPlanner{TKey}"/> ——
/// 记账与还原逻辑必须能在 macOS 测试宿主上验证，故下沉到 Core。
/// </summary>
public sealed class GrayModeController : IDisposable
{
    /// <summary>
    /// 设置项键（与 <see cref="UiSettings"/> 的 token 命名一致）。
    ///
    /// **刻意转发到 <see cref="UiSettings.GrayModeKey"/>**：这个键必须在 Core 的
    /// <see cref="UiSettings.All"/> 里注册 —— <c>SettingsService.Get</c> 对未注册的键
    /// 抛 <c>KeyNotFoundException</c>，而读它的地方在启动路径上
    /// （<c>App.OnStartup → ApplyFromSettings</c>）。本类自己再抄一份字面量，
    /// 就多了一个可能与 Core 悄悄分叉的副本。
    /// </summary>
    public const string GrayModeKey = UiSettings.GrayModeKey;

    private readonly GrayColorPlanner<SolidColorBrush> _planner = new();

    /// <summary>当前是否处于灰度模式。</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>音色配置。测试可替换以验证深浅两套的选择。</summary>
    public GrayProfileConfig Config { get; set; } = GrayProfileConfig.Default;

    /// <summary>已改写的画笔数量。</summary>
    public int RewrittenCount => _planner.TrackedCount;

    /// <summary>
    /// 应用灰度变换。<paramref name="resources"/> 通常是 <c>Application.Current.Resources</c>。
    /// </summary>
    /// <param name="resources">
    /// 为 <c>null</c>（无 UI 环境的单元测试）时只更新状态，不碰任何画笔。
    /// </param>
    public void Apply(ResourceDictionary? resources)
    {
        IsEnabled = Config.IsEnabled;
        if (!IsEnabled)
        {
            Restore();
            return;
        }

        var profile = Config.CurrentProfile(ThemeManager.IsDark);
        var saturation = profile.Sa1;
        var brightness = profile.LaP;

        if (resources is null)
        {
            Log.Info("Gray", $"灰度模式已启用（饱和度 {saturation:0.###}，亮度 {brightness:0.###}），但没有资源字典可改");
            return;
        }

        var changed = 0;
        foreach (var brush in FlattenSolidBrushes(resources))
        {
            // 只脱色「有颜色」的画笔：透明画笔脱色后看不出差别。
            if (brush.Color.A == 0) continue;

            brush.Color = FromArgb(_planner.Plan(brush, ToArgb(brush.Color), saturation, brightness));
            changed++;
        }

        Log.Info("Gray", $"灰度模式已启用：改写 {changed} 个画笔（饱和度 {saturation:0.###}，亮度 {brightness:0.###}）");
    }

    /// <summary>关闭灰度并把所有画笔恢复原值。</summary>
    public void Restore()
    {
        foreach (var (brush, argb) in _planner.Drain()) brush.Color = FromArgb(argb);
        var count = RewrittenCount;
        IsEnabled = false;
        if (count > 0) Log.Info("Gray", $"灰度模式已关闭：还原 {count} 个画笔");
    }

    /// <summary>深度遍历资源字典（含合并字典与子字典）取出全部纯色画刷。</summary>
    /// <param name="resources">资源字典。</param>
    public static IEnumerable<SolidColorBrush> FlattenSolidBrushes(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var seen = new HashSet<SolidColorBrush>();
        var stack = new Stack<ResourceDictionary>();
        stack.Push(resources);

        while (stack.Count > 0)
        {
            var dict = stack.Pop();
            foreach (var value in dict.Values)
            {
                switch (value)
                {
                    case SolidColorBrush brush:
                        seen.Add(brush);
                        break;
                    case ResourceDictionary child:
                        stack.Push(child);
                        break;
                }
            }
        }

        foreach (var merged in resources.MergedDictionaries)
        {
            foreach (var brush in FlattenSolidBrushes(merged)) seen.Add(brush);
        }

        return seen;
    }

    /// <summary>WPF 的 <see cref="Color"/> 没有 <c>ToArgb()</c>（那是 WinForms 的），自己打包。</summary>
    /// <param name="color">WPF 颜色。</param>
    public static int ToArgb(Color color)
        => unchecked((int)(((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));

    /// <summary>WPF 的 <see cref="Color"/> 没有单参数的 <c>FromArgb(int)</c>，自己拆包。</summary>
    /// <param name="argb">0xAARRGGBB。</param>
    public static Color FromArgb(int argb)
        => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

    /// <summary>从设置项读取开关并施加。</summary>
    /// <param name="resources">目标资源字典，通常是 <c>Application.Current.Resources</c>。</param>
    public void ApplyFromSettings(ResourceDictionary? resources)
    {
        // 读设置项有可能失败（键没注册、配置文件损坏、值类型对不上）。
        // 这里是**启动路径**：一次读取失败不该让整个应用起不来 ——
        // 灰度模式只是个装饰性开关，读不到就当作关闭，继续启动。
        // 失败必须留下痕迹（否则「灰度模式怎么不生效」会变成查不到原因的问题）。
        bool enabled;
        try
        {
            enabled = UMango.Shell.Services.AppSettings.Settings.Get<bool>(GrayModeKey);
        }
        catch (Exception ex)
        {
            UMango.Core.Logging.Log.Warn("GrayMode", $"读取灰度模式设置失败，按关闭处理：{ex.Message}");
            enabled = false;
        }

        Config = Config with { IsEnabled = enabled };
        Apply(resources);
    }

    /// <summary>写回设置项并立即施加。</summary>
    /// <param name="enabled">是否启用灰度模式。</param>
    /// <param name="resources">目标资源字典。</param>
    public void SetEnabled(bool enabled, ResourceDictionary? resources)
    {
        Config = Config with { IsEnabled = enabled };
        try
        {
            UMango.Shell.Services.AppSettings.Settings.Set(GrayModeKey, enabled);
        }
        catch (Exception ex)
        {
            // 写不进去也不能让界面崩 —— 内存里的状态已经是新值了，界面照样立刻生效。
            UMango.Core.Logging.Log.Warn("GrayMode", $"写入灰度模式设置失败：{ex.Message}");
        }
        Apply(resources);
    }

    /// <summary>
    /// 主题深浅切换后要重算：深浅两套音色不同（PCL 的 <c>GrayProfileConfig</c> 也是分开配的）。
    /// </summary>
    /// <param name="resources">目标资源字典。</param>
    public void OnThemeChanged(ResourceDictionary? resources)
    {
        // 换主题 = 换了另一批画刷实例；先还原旧的（按引用记账的键已失效），
        // 再对新的一批记账，否则原色表里会堆一堆再也用不到的旧画刷。
        Restore();
        Apply(resources);
    }

    public void Dispose() => Restore();
}
