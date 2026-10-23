using System;
using System.Globalization;
using System.Text.Json;
using System.Linq;
using System.Text.Json.Serialization;

namespace UMango.Core.UI;

/// <summary>
/// 灰度（哀悼日）配色的**音色描述** —— 语义取自 PCL-CE
/// <c>PCL.Core/UI/GrayProfile.cs</c>。
///
/// **为什么不是逐字采用**：核实 PCL-CE 全仓后发现，
/// <c>GrayProfile</c> 与 <c>GrayProfileConfig</c> **没有任何消费方** ——
/// <c>ThemeService.ApplyGrayResources()</c> 里的灰色来自 <c>ToneProfile.L1..L8</c>
/// 经 <c>LabColor.FromLch</c> 计算（<c>PCL.Core/UI/Theme/ThemeService.cs:182-184, 232</c>），
/// 与这两个 record 无关。所以照搬只会搬进两个没人用的类。
/// 故 UMango 只保留**语义**（音色档位、亮度调整、饱和度、若干层透明度），
/// 用一套可单测的纯函数实现；界面侧由 WPF <c>ColorMatrixEffect</c> 落地（见 <see cref="GrayColorMatrix"/>）。
/// </summary>
public record GrayProfile
{
    /// <summary>亮度档 L1..L8（0..100）。PCL 语义：L1 文字最深、L8 贴近底色。</summary>
    public int L1 { get; init; }

    public int L2 { get; init; }

    public int L3 { get; init; }

    public int L4 { get; init; }

    public int L5 { get; init; }

    public int L6 { get; init; }

    public int L7 { get; init; }

    public int L8 { get; init; }

    /// <summary>灰阶 G1..G3（0..100）。PCL 用 G 表示"无彩色"的三档中性灰。</summary>
    public int G1 { get; init; }

    public int G2 { get; init; }

    public int G3 { get; init; }

    /// <summary>亮部调整系数（乘性，默认 1 = 不动）。</summary>
    public double LaP { get; init; } = 1;

    /// <summary>暗部调整系数（乘性，默认 1 = 不动）。</summary>
    public double LaN { get; init; } = 1;

    /// <summary>色阶起点饱和度（0..1；0 = 完全无彩）。</summary>
    public double Sa0 { get; init; }

    /// <summary>色阶终点饱和度（0..1）。</summary>
    public double Sa1 { get; init; }

    /// <summary>窗口底色层透明度（0..1，PCL 原值 0xBE / 255）。</summary>
    public double AlphaBackground { get; init; } = 0xBE / 255d;

    /// <summary>半透明层透明度（PCL 原值 0x01 / 255，几乎全透）。</summary>
    public double AlphaSemiTransparent { get; init; } = 0x01 / 255d;

    /// <summary>透明层透明度（PCL 原值 0）。</summary>
    public double AlphaTransparent { get; init; }

    /// <summary>半透明白层透明度（PCL 原值 0x55 / 255）。</summary>
    public double AlphaHalfWhite { get; init; } = 0x55 / 255d;

    /// <summary>工具提示底色层透明度（PCL 原值 0xDB / 255）。</summary>
    public double AlphaTooltipBackground { get; init; } = 0xDB / 255d;

    /// <summary>侧栏底色层透明度（PCL 原值 0xD2 / 255）。</summary>
    public double AlphaSidebarBackground { get; init; } = 0xD2 / 255d;

    /// <summary>
    /// 把第 <paramref name="index"/> 档亮度（0 起）转成 0..255 的灰阶通道值。
    /// </summary>
    /// <param name="index">0..7 对应 L1..L8。</param>
    /// <exception cref="ArgumentOutOfRangeException">index 不在 0..7。</exception>
    public byte LevelChannel(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 7);
        var level = index switch
        {
            0 => L1, 1 => L2, 2 => L3, 3 => L4,
            4 => L5, 5 => L6, 6 => L7, _ => L8,
        };
        return GrayColorMath.LevelToChannel(level);
    }

    /// <summary>
    /// 第 <paramref name="index"/> 档灰阶的 0..255 通道值（0..2 对应 G1..G3）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">index 不在 0..2。</exception>
    public byte GrayChannel(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 2);
        var level = index switch { 0 => G1, 1 => G2, _ => G3 };
        return GrayColorMath.LevelToChannel(level);
    }
}

/// <summary>灰度总开关与深浅两套音色。语义取自 PCL-CE <c>GrayProfileConfig.cs</c>。</summary>
public record GrayProfileConfig
{
    public static GrayProfileConfig Default { get; } = new();

    /// <summary>是否启用灰度（哀悼日）模式。</summary>
    public bool IsEnabled { get; init; }

    public GrayProfile Light { get; init; } = new()
    {
        L1 = 25, L2 = 45, L3 = 55, L4 = 65,
        L5 = 80, L6 = 91, L7 = 95, L8 = 97,
        G1 = 100, G2 = 98, G3 = 0,
        Sa0 = 1, Sa1 = 1, LaN = 0.5,
    };

    public GrayProfile Dark { get; init; } = new()
    {
        L1 = 96, L2 = 75, L3 = 60, L4 = 65,
        L5 = 45, L6 = 25, L7 = 22, L8 = 20,
        G1 = 15, G2 = 20, G3 = 100,
        Sa0 = 1, Sa1 = 0.4, LaP = 0.75, LaN = 0.75,
    };

    /// <summary>按当前深浅取生效音色。</summary>
    public GrayProfile CurrentProfile(bool isDarkMode) => isDarkMode ? Dark : Light;

    /// <summary>序列化（供设置持久化用）。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// 反序列化。解析失败返回 <paramref name="fallback"/> 而不是抛 ——
    /// 配置文件损坏不该让应用起不来。
    /// </summary>
    public static GrayProfileConfig FromJson(string? json, GrayProfileConfig? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback ?? Default;
        try
        {
            return JsonSerializer.Deserialize<GrayProfileConfig>(json, SerializerOptions) ?? fallback ?? Default;
        }
        catch (JsonException)
        {
            return fallback ?? Default;
        }
    }

    /// <summary>
    /// 小写驼峰 + 忽略未知键：配置文件由用户手改/跨版本增删字段，
    /// 遇到不认识的键必须容忍，遇到损坏的值必须降级。
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>灰度模式的纯数学。无 WPF 依赖，故可在 macOS 上直接单测。</summary>
public static class GrayColorMath
{
    /// <summary>亮度档（0..100）→ 0..255 通道值。越界值夹到 [0,100]。</summary>
    public static byte LevelToChannel(double level) => (byte)Math.Clamp(Math.Round(level * 255d / 100d), 0, 255);

    /// <summary>亮度档（0..100）→ 0.0..1.0 线性亮度。</summary>
    public static double LevelToUnit(double level) => Math.Clamp(level / 100d, 0, 1);

    /// <summary>
    /// 亮度调整系数：暗部（<50）走 <see cref="GrayProfile.LaN"/>，亮部（>=50）走
    /// <see cref="GrayProfile.LaP"/>。PCL 把两者分开是为了让"压暗背景"和"提亮文字"
    /// 能用不同力度 —— 整体反相会让文字变得比背景还不显眼。
    /// </summary>
    /// <param name="level">0..100 亮度档。</param>
    public static double AdjustLevel(double level, double positiveFactor, double negativeFactor)
    {
        var factor = level >= 50 ? positiveFactor : negativeFactor;
        return Math.Clamp(level * factor, 0, 100);
    }

    /// <summary>按档位在 <paramref name="sa0"/> 与 <paramref name="sa1"/> 之间线性插值饱和度。</summary>
    /// <param name="level">0..100 亮度档。</param>
    public static double SaturationAt(double level, double sa0, double sa1)
    {
        var t = LevelToUnit(level);
        return Math.Clamp(sa0 + (sa1 - sa0) * t, 0, 1);
    }

    /// <summary>
    /// 供 WPF <c>ColorMatrixEffect</c> 使用的 20 个矩阵值。
    /// </summary>
    /// <param name="saturation">0 = 完全灰度，1 = 原色，小于 1 = 保留一点色彩。</param>
    /// <param name="brightnessFactor">整体亮度乘性系数。</param>
    /// <remarks>
    /// 矩阵按 WPF 的行主序线性色彩空间声明；因此灰度化后所有通道相等，
    /// 视觉亮度会略高于 sRGB 亮度（0.299/0.587/0.114 是 sRGB 系数，
    /// WPF 的矩阵作用于线性空间）。这是与 PCL 使用 <c>ColorMatrix</c> 时一致的取舍：
    /// 保持"代码即所见"，而不是再补一层 gamma 校正。
    /// </remarks>
    public static double[] ColorMatrix(double saturation = 0, double brightnessFactor = 1)
    {
        var s = Math.Clamp(saturation, 0, 1);
        var b = brightnessFactor;

        // 标准饱和度矩阵：
        //   R' = s·R + (1-s)·(ir·R + ig·G + ib·B)
        // 展开后 R 的系数是 s·1 + (1-s)·ir，**不能漏掉这个 s·1** ——
        // 漏掉时 s=1 会让三行全归零，原本彩色的一律变纯黑（实测抓到）。
        var ir = (1 - s) * 0.299d;
        var ig = (1 - s) * 0.587d;
        var ib = (1 - s) * 0.114d;
        var rr = s + ir;
        var gg = s + ig;
        var bb = s + ib;

        return
        [
            rr * b, ig * b, ib * b, 0, 0,
            ir * b, gg * b, ib * b, 0, 0,
            ir * b, ig * b, bb * b, 0, 0,
            0, 0, 0, b, 0,
        ];
    }

    /// <summary>
    /// 纯数学的色相消解：对 0xAARRGGBB 的颜色做去饱和，**保持相对亮度不变**。
    /// </summary>
    /// <param name="argb">0xAARRGGBB。</param>
    /// <param name="saturation">0 = 完全灰度，1 = 原色。</param>
    /// <returns>去饱和后的 0xAARRGGBB。</returns>
    /// <remarks>
    /// 为什么保留亮度而不是直接取灰度均值：token 里的每一对前景/背景是按对比度挑的，
    /// 等权重灰度化会让暗色系的字落到与背景相同的亮度上（WCAG AA 直接不过）。
    /// 按 sRGB 亮度加权混合则保持「谁亮谁暗」的相对关系，灰度后仍然可读。
    /// </remarks>
    public static int Desaturate(int argb, double saturation)
    {
        var s = Math.Clamp(saturation, 0, 1);
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;

        var luminance = (0.299d * r) + (0.587d * g) + (0.114d * b);
        // s=0 全灰、s=1 原色，所以要从"全灰"朝原色插值：lum + (c - lum) * s
        var nr = (int)Math.Round(luminance + (r - luminance) * s);
        var ng = (int)Math.Round(luminance + (g - luminance) * s);
        var nb = (int)Math.Round(luminance + (b - luminance) * s);
        return (a << 24) | (nr << 16) | (ng << 8) | nb;
    }

    /// <summary>亮度调整：按比例整体缩放 RGB（保留 alpha）。</summary>
    /// <param name="argb">0xAARRGGBB。</param>
    /// <param name="factor">乘性系数，1 = 不动。</param>
    public static int ScaleBrightness(int argb, double factor)
    {
        var a = (argb >> 24) & 0xFF;
        var r = Scale((argb >> 16) & 0xFF, factor);
        var g = Scale((argb >> 8) & 0xFF, factor);
        var b = Scale(argb & 0xFF, factor);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static int Scale(int channel, double factor)
        => (int)Math.Clamp(Math.Round(channel * factor), 0, 255);

    /// <summary>一次性完成去饱和 + 亮度调整，得到灰度模式下的最终颜色。</summary>
    /// <param name="argb">0xAARRGGBB。</param>
    public static int Apply(int argb, double saturation, double brightnessFactor)
        => ScaleBrightness(Desaturate(argb, saturation), brightnessFactor);

    /// <summary>把矩阵格式化成 <c>Invariant</c> 小数字符串，便于写日志或展示。</summary>
    public static string FormatMatrix(double saturation = 0, double brightnessFactor = 1)
    {
        var parts = new string[20];
        var m = ColorMatrix(saturation, brightnessFactor);
        for (var i = 0; i < 20; i++) parts[i] = m[i].ToString("0.####", CultureInfo.InvariantCulture);
        return string.Join(' ', parts);
    }
}
/// <summary>
/// 灰度改写的记账与算色 —— <b>与 WPF 无关的纯逻辑</b>。
///
/// 为什么要有这一层：真正落地要遍历 <c>ResourceDictionary</c> 里的
/// <c>SolidColorBrush</c>，那是 WPF 类型，在 macOS 测试宿主上根本加载不了
/// （App.Tests 用了 <c>DisableTransitiveFrameworkReferences</c>，编译期可见、
/// 运行期抛 <c>FileNotFoundException: PresentationFramework</c>）。
/// 所以「记下原值 / 算新值 / 还原」这些真正会出错的部分放在这里，
/// WPF 层只负责把画笔的颜色取出来、写回去。
/// </summary>
/// <typeparam name="TKey">画笔的标识（App 层传画笔实例本身，按引用比较）。</typeparam>
public sealed class GrayColorPlanner<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, int> _originalColors = [];

    /// <summary>已记账的画笔数量。</summary>
    public int TrackedCount => _originalColors.Count;

    /// <summary>
    /// 计算某个画笔的新颜色，并首次遇到时记下原值。
    /// </summary>
    /// <param name="key">画笔标识。</param>
    /// <param name="currentColor">当前颜色（0xAARRGGBB）。</param>
    /// <param name="saturation">去饱和强度。</param>
    /// <param name="brightnessFactor">亮度系数。</param>
    /// <returns>应写入的新颜色。</returns>
    /// <remarks>
    /// 两处关键，都由 <c>GrayProfileTests.反复规划时原色不会被脱色值顶掉</c> 守着：
    /// <list type="number">
    /// <item><b>从记账的原色算，不从传入的当前色算</b>。否则重复调用会一层层褪色
    /// （传入的已经是上一次的脱色值）。</item>
    /// <item><b>只在第一次遇到时记原值</b>。否则每轮都会把脱色值当成原值记回去，
    /// 还原时再也回不到真正的原始颜色。</item>
    /// </list>
    /// 两者合起来才让本方法<b>幂等</b>：同样参数调用多少次都是同一个结果。
    /// </remarks>
    public int Plan(TKey key, int currentColor, double saturation, double brightnessFactor)
    {
        if (!_originalColors.TryGetValue(key, out var original)) original = currentColor;
        _originalColors[key] = original;
        return GrayColorMath.Apply(original, saturation, brightnessFactor);
    }

    /// <summary>
    /// 某个画笔的原色；没记账过返回 <paramref name="fallback"/>。
    /// </summary>
    /// <param name="key">画笔标识。</param>
    /// <param name="fallback">未记账时的返回值。</param>
    public int OriginalColor(TKey key, int fallback)
        => _originalColors.TryGetValue(key, out var color) ? color : fallback;

    /// <summary>把某个画笔移出记账（对应 App 层的 <c>Dispose</c> 单点还原）。</summary>
    /// <param name="key">画笔标识。</param>
    /// <returns>曾记账过则返回 true。</returns>
    public bool Forget(TKey key) => _originalColors.Remove(key);

    /// <summary>清空全部记账并交还原色清单。</summary>
    public IReadOnlyList<(TKey Key, int Color)> Drain()
    {
        var result = _originalColors.Select(kv => (kv.Key, kv.Value)).ToList();
        _originalColors.Clear();
        return result;
    }
}
