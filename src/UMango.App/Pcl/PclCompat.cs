// ============================================================================
// PclCompat —— PCL-CE 动画模块在本项目的依赖桥。
// ModAnimation.cs 逐字采用自 PCL-CE（受限许可层），它调用的 ModBase 工具成员
// 在此提供。分两种：
//   · 【逐字】标注"取自 ModBase.cs"的成员：源码原文搬运（同一许可层，统一标注）；
//   · 【等价桥】其余成员：行为等价的 UMango 实现（平凡函数/接 UMango 日志），
//     逐条注明与原版的差异。
// 全部置于 namespace PCL，使 ModAnimation.cs 一行调用都无需改动。
// ============================================================================
namespace PCL;

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ICollection = System.Collections.ICollection;
using IList = System.Collections.IList;

/// <summary>ModBase 桥：仅含 ModAnimation.cs 实际引用的成员。</summary>
public static partial class ModBase
{
    /// <summary>调试开关（原版为 ModBase.cs:3296 的字段；UMango 接到自己的调试设置）。</summary>
    public static bool modeDebug = false;

    // ================= 以下成员逐字取自 PCL-CE ModBase.cs（见文件头许可标注） =================

    // ---- MyColor ----
    public class MyColor
    {
        public double a = 255d;
        public double b;
        public double g;
        public double r;

        // 构造函数
        public MyColor()
        {
        }

        public MyColor(Color col)
        {
            a = col.A;
            r = col.R;
            g = col.G;
            b = col.B;
        }

        public MyColor(string hexString)
        {
            var stringColor = (Color)ColorConverter.ConvertFromString(hexString);
            a = stringColor.A;
            r = stringColor.R;
            g = stringColor.G;
            b = stringColor.B;
        }

        public MyColor(double newA, MyColor col)
        {
            a = newA;
            r = col.r;
            g = col.g;
            b = col.b;
        }

        public MyColor(double newR, double newG, double newB)
        {
            a = 255d;
            r = newR;
            g = newG;
            b = newB;
        }

        public MyColor(double newA, double newR, double newG, double newB)
        {
            a = newA;
            r = newR;
            g = newG;
            b = newB;
        }

        public MyColor(Brush brush)
        {
            var color = ((SolidColorBrush)brush).Color;
            a = color.A;
            r = color.R;
            g = color.G;
            b = color.B;
        }

        public MyColor(SolidColorBrush brush)
        {
            var color = brush.Color;
            a = color.A;
            r = color.R;
            g = color.G;
            b = color.B;
        }

        public MyColor(object obj)
        {
            if (obj is null)
            {
                a = 255d;
                r = 255d;
                g = 255d;
                b = 255d;
            }
            else if (obj is SolidColorBrush)
            {
                // 避免反复获取 Color 对象造成性能下降
                var color = ((SolidColorBrush)obj).Color;
                a = color.A;
                r = color.R;
                g = color.G;
                b = color.B;
            }
            else
            {
                a = Convert.ToDouble(((dynamic)obj).A);
                r = Convert.ToDouble(((dynamic)obj).R);
                g = Convert.ToDouble(((dynamic)obj).G);
                b = Convert.ToDouble(((dynamic)obj).B);
            }
        }

        // 类型转换
        public static implicit operator MyColor(string str)
        {
            return new MyColor(str);
        }

        public static implicit operator MyColor(Color col)
        {
            return new MyColor(col);
        }

        public static implicit operator Color(MyColor conv)
        {
            return Color.FromArgb(MathByte(conv.a), MathByte(conv.r), MathByte(conv.g), MathByte(conv.b));
        }

        public static implicit operator System.Drawing.Color(MyColor conv)
        {
            return System.Drawing.Color.FromArgb(MathByte(conv.a), MathByte(conv.r), MathByte(conv.g),
                MathByte(conv.b));
        }

        public static implicit operator MyColor(SolidColorBrush bru)
        {
            return new MyColor(bru.Color);
        }

        public static implicit operator SolidColorBrush(MyColor conv)
        {
            return new SolidColorBrush(Color.FromArgb(MathByte(conv.a), MathByte(conv.r), MathByte(conv.g),
                MathByte(conv.b)));
        }

        public static implicit operator MyColor(Brush bru)
        {
            return new MyColor(bru);
        }

        public static implicit operator Brush(MyColor conv)
        {
            return new SolidColorBrush(Color.FromArgb(MathByte(conv.a), MathByte(conv.r), MathByte(conv.g),
                MathByte(conv.b)));
        }

        // 颜色运算
        public static MyColor operator +(MyColor a, MyColor b)
        {
            return new MyColor { a = a.a + b.a, b = a.b + b.b, g = a.g + b.g, r = a.r + b.r };
        }

        public static MyColor operator -(MyColor a, MyColor b)
        {
            return new MyColor { a = a.a - b.a, b = a.b - b.b, g = a.g - b.g, r = a.r - b.r };
        }

        public static MyColor operator *(MyColor a, double b)
        {
            return new MyColor { a = a.a * b, b = a.b * b, g = a.g * b, r = a.r * b };
        }

        public static MyColor operator /(MyColor a, double b)
        {
            return new MyColor { a = a.a / b, b = a.b / b, g = a.g / b, r = a.r / b };
        }

        public static bool operator ==(MyColor a, MyColor b)
        {
            if (a is null && b is null)
                return true;
            if (a is null || b is null)
                return false;
            return a.a == b.a && a.r == b.r && a.g == b.g && a.b == b.b;
        }

        public static bool operator !=(MyColor a, MyColor b)
        {
            if (a is null && b is null)
                return false;
            if (a is null || b is null)
                return true;
            return !(a.a == b.a && a.r == b.r && a.g == b.g && a.b == b.b);
        }

        // HSL
        public double Hue(double v1, double v2, double vH)
        {
            if (vH < 0d)
                vH += 1d;
            if (vH > 1d)
                vH -= 1d;
            if (vH < 0.16667d)
                return v1 + (v2 - v1) * 6d * vH;
            if (vH < 0.5d)
                return v2;
            if (vH < 0.66667d)
                return v1 + (v2 - v1) * (4d - vH * 6d);
            return v1;
        }

        public MyColor FromHSL(double sH, double sS, double sL)
        {
            if (sS == 0d)
            {
                r = sL * 2.55d;
                g = r;
                b = r;
            }
            else
            {
                var h = sH / 360d;
                var s = sS / 100d;
                var l = sL / 100d;
                s = l < 0.5d ? s * l + l : s * (1.0d - l) + l;
                l = 2d * l - s;
                r = 255d * Hue(l, s, h + 1d / 3d);
                g = 255d * Hue(l, s, h);
                b = 255d * Hue(l, s, h - 1d / 3d);
            }

            a = 255d;
            return this;
        }

        public MyColor FromHSL2(double sH, double sS, double sL)
        {
            if (sS == 0d)
            {
                r = sL * 2.55d;
                g = r;
                b = r;
            }
            else
            {
                // 初始化
                sH = (sH + 3600000d) % 360d;
                var cent = new[]
                {
                    +0.1d, -0.06d, -0.3d, -0.19d, -0.15d, -0.24d, -0.32d, -0.09d, +0.18d, +0.05d, -0.12d, -0.02d, +0.1d,
                    -0.06d
                }; // 0, 30, 60
                // 90, 120, 150
                // 180, 210, 240
                // 270, 300, 330
                // 最后两位与前两位一致，加是变亮，减是变暗
                // 计算色调对应的亮度片区
                var center = sH / 30.0d;
                var intCenter = (int)Math.Round(Math.Floor(center)); // 亮度片区编号
                center = 50d -
                         ((1d - center + intCenter) * cent[intCenter] + (center - intCenter) * cent[intCenter + 1]) *
                         sS;
                // center = 50 + (cent(intCenter) + (center - intCenter) * (cent(intCenter + 1) - cent(intCenter))) * sS
                sL = (sL < center ? sL / center : 1d + (sL - center) / (100d - center)) * 50d;
                FromHSL(sH, sS, sL);
            }

            a = 255d;
            return this;
        }

        public MyColor Alpha(double sA)
        {
            a = sA;
            return this;
        }

        public override string ToString()
        {
            return "(" + a + "," + r + "," + g + "," + b + ")";
        }

        public override bool Equals(object obj)
        {
            return obj is MyColor other && a == other.a && r == other.r && g == other.g && b == other.b;
        }
    }

    // ---- MyRect ----
    public class MyRect
    {
        // 构造函数
        public MyRect()
        {
        }

        public MyRect(double left, double top, double width, double height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        // 属性
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
    }

    // ---- LogLevel ----
    public enum LogLevel
    {
        /// <summary>
        ///     不提示，只记录日志。
        /// </summary>
        Normal = 0,

        /// <summary>
        ///     只提示开发者。
        /// </summary>
        Developer = 1,

        /// <summary>
        ///     只提示开发者与调试模式用户。
        /// </summary>
        Debug = 2,

        /// <summary>
        ///     弹出提示所有用户。
        /// </summary>
        Hint = 3,

        /// <summary>
        ///     弹窗，不要求反馈。
        /// </summary>
        Msgbox = 4,

        /// <summary>
        ///     弹窗，要求反馈。
        /// </summary>
        Feedback = 5,

        /// <summary>
        ///     弹出 Windows 原生弹窗，要求反馈。在无法保证 WPF 窗口能正常运行时使用此级别。
        ///     在第二次触发后会直接结束程序。
        /// </summary>
        Critical = 6
    }

    // ---- MathPercent(double) ----
    public static double MathPercent(double valueA, double valueB, double percent)
    {
        return Math.Round(valueA * (1d - percent) + valueB * percent, 6); // 解决 Double 计算错误
    }

    // ---- MathPercent(MyColor) ----
    public static MyColor MathPercent(MyColor valueA, MyColor valueB, double percent)
    {
        return MathRound(valueA * (1d - percent) + valueB * percent, 6); // 解决Double计算错误
    }

    // ---- MathClamp ----
    public static double MathClamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    // ---- DeltaLeft ----
    public static void DeltaLeft(FrameworkElement control, double newValue)
    {
        // 安全性检查
        DebugAssert(!double.IsNaN(newValue));
        DebugAssert(!double.IsInfinity(newValue));

        if (control is Window)
            // 窗口改变
            ((Window)control).Left += newValue;
        else
            // 根据 HorizontalAlignment 改变数值
            switch (control.HorizontalAlignment)
            {
                case HorizontalAlignment.Left:
                case HorizontalAlignment.Stretch:
                {
                    control.Margin = new Thickness(control.Margin.Left + newValue, control.Margin.Top,
                        control.Margin.Right, control.Margin.Bottom);
                    break;
                }
                case HorizontalAlignment.Right:
                {
                    // control.Margin = New Thickness(control.Margin.Left, control.Margin.Top, CType(control.Parent, Object).ActualWidth - control.ActualWidth - newValue, control.Margin.Bottom)
                    control.Margin = new Thickness(control.Margin.Left, control.Margin.Top,
                        control.Margin.Right - newValue, control.Margin.Bottom);
                    break;
                }

                default:
                {
                    DebugAssert(false);
                    break;
                }
            }
    }

    // ---- DeltaTop ----
    public static void DeltaTop(FrameworkElement control, double newValue)
    {
        // 安全性检查
        DebugAssert(!double.IsNaN(newValue));
        DebugAssert(!double.IsInfinity(newValue));

        if (control is Window)
            // 窗口改变
            ((Window)control).Top += newValue;
        else
            // 根据 VerticalAlignment 改变数值
            switch (control.VerticalAlignment)
            {
                case VerticalAlignment.Top:
                {
                    control.Margin = new Thickness(control.Margin.Left, control.Margin.Top + newValue,
                        control.Margin.Right, control.Margin.Bottom);
                    break;
                }
                case VerticalAlignment.Bottom:
                {
                    // control.Margin = New Thickness(control.Margin.Left, control.Margin.Top, CType(control.Parent, Object).ActualWidth - control.ActualWidth - newValue, control.Margin.Bottom)
                    control.Margin = new Thickness(control.Margin.Left, control.Margin.Top, control.Margin.Right,
                        control.Margin.Bottom - newValue);
                    break;
                }

                default:
                {
                    DebugAssert(false);
                    break;
                }
            }
    }

    // ---- GetStringFromEnum ----
    public static string GetStringFromEnum(Enum enumData)
    {
        return Enum.GetName(enumData.GetType(), enumData);
    }

    // ---- GetUuid ----
    public static int GetUuid()
    {
        if (uuidLock is null)
            uuidLock = new object();
        lock (uuidLock)
        {
            uuid += 1;
            return uuid;
        }
    }

    // ---- DebugAssert ----
    public static void DebugAssert(bool exp)
    {
        if (!exp)
            throw new Exception("断言命中");
    }

    // ---- MathByte（逐字取自 ModBase.cs）----
    public static byte MathByte(double d)
    {
        if (d < 0d)
            d = 0d;
        if (d > 255d)
            d = 255d;
        return (byte)Math.Round(Math.Round(d));
    }

    // ---- GetFullList（逐字取自 ModBase.cs）----
    public static List<T> GetFullList<T>(System.Collections.IList data)
    {
        List<T> getFullListRet = default;
        getFullListRet = new List<T>();
        for (int i = 0, loopTo = data.Count - 1; i <= loopTo; i++)
            if (data[i] is ICollection)
                getFullListRet.AddRange((IEnumerable<T>)data[i]);
            else
                getFullListRet.Add((T)data[i]);

        return getFullListRet;
    }

    // ---- MathRound(MyColor) / uuid 字段（逐字取自 ModBase.cs:581、2428-2429）----
    public static MyColor MathRound(MyColor col, int w = 0)
    {
        return new MyColor
            { a = Math.Round(col.a, w), r = Math.Round(col.r, w), g = Math.Round(col.g, w), b = Math.Round(col.b, w) };
    }
    private static int uuid = 1;
    private static object uuidLock;

    // ================= 等价桥（UMango 实现，非原版源码） =================

    /// <summary>文件大小文本（原版 GetString(long) 走 PCL.Core ByteStream；此处等价实现，
    /// 输出格式与 PCL 一致："1.28 M"）。</summary>
    public static string GetString(long fileSize)
    {
        double v = fileSize; var units = new[] { "B", "K", "M", "G", "T" };
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u += 1; }
        return $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {units[u]}";
    }

    /// <summary>日志桥（原版走 PCL.Core LogWrapper；此处接 UMango.Core 的 Log）。
    /// 级别映射：Normal→Info，Developer/Debug→Debug，Hint/Msgbox→Warn，
    /// Feedback→Error，Critical→Fatal。</summary>
    public static void Log(string text, LogLevel level = LogLevel.Normal,
        string? title = null, string? userSummary = null)
    {
        var line = (title is null ? "" : $"[{title}] ") + text;
        switch (level)
        {
            case LogLevel.Developer or LogLevel.Debug:
                UMango.Core.Logging.Log.Debug("PCL", line); break;
            case LogLevel.Feedback:
                UMango.Core.Logging.Log.Error("PCL", line); break;
            case LogLevel.Critical:
                UMango.Core.Logging.Log.Error("PCL", line); break;
            case LogLevel.Hint or LogLevel.Msgbox:
                UMango.Core.Logging.Log.Warn("PCL", line); break;
            default:
                UMango.Core.Logging.Log.Info("PCL", line); break;
        }
    }

    /// <summary>异常日志桥（原版签名一致；UMango 侧不带弹窗，只落日志）。</summary>
    public static void Log(Exception ex, string desc, LogLevel level = LogLevel.Debug,
        string? title = null, string? userSummary = null)
        => UMango.Core.Logging.Log.Error("PCL", $"{desc}: {ex.Message}");

    /// <summary>新线程（原版含取消协作等；此处等价实现，满足动画驱动线程的用法）。</summary>
    public static Thread RunInNewThread(Action action, string? name = null,
        ThreadPriority priority = ThreadPriority.Normal, bool onlyOne = false, bool reopen = true)
    {
        var t = new Thread(() => action()) { Name = name ?? "UMangoThread", Priority = priority, IsBackground = true };
        t.Start();
        return t;
    }

    /// <summary>在 UI 线程执行并等待（原版经 PCL.Core；此处直用 WPF Dispatcher）。</summary>
    public static void RunInUiWait(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }
}

/// <summary>配置桥：原版 Config.System.AnimationFpsLimit；UMango 侧读自己的动画设置。</summary>
public static class Config
{
    public static ConfigSystem System { get; } = new();
    public sealed class ConfigSystem
    {
        public int AnimationFpsLimit => UMango.App.Services.AppSettings.Animation.FpsLimit;
    }
}

/// <summary>本地化桥：UMango 无 PCL 本地化资源，Text 返回键名（仅用于日志文案）。</summary>
public static class Lang
{
    public static string Text(string key) => key;
    public static CultureInfo Culture => CultureInfo.CurrentCulture;
}

/// <summary>时间桥（原版 PCL.Core/Utils/TimeUtils.cs，Apache-2.0；此处等价实现）。</summary>
public static class TimeUtils
{
    public static long GetTimeTick() => Environment.TickCount64;
}

/// <summary>随机桥（原版 PCL.Core/Utils/RandomUtils.cs，Apache-2.0；此处等价实现）。</summary>
public static class RandomUtils
{
    public static int NextInt(int min, int max) => Random.Shared.Next(min, max);

    /// <summary>原版在 PCL.Core/Utils/RandomUtils.cs（Apache-2.0）；等价实现。</summary>
    public static T PickRandom<T>(ICollection<T> collection)
    {
        if (collection.Count == 0) throw new ArgumentException("集合不能为空", nameof(collection));
        var index = Random.Shared.Next(collection.Count);
        if (collection is IList<T> list) return list[index];
        return collection.Skip(index).First();
    }
}

/// <summary>网络统计桥（原版 ModNet.NetManager；UMango 不接 PCL 网络模块，恒零占位，
/// 仅影响动画线程里的 FPS 上报文案）。</summary>
public static class ModNet
{
    public static NetManagerClass NetManager { get; } = new();
    public sealed class NetManagerClass
    {
        public long FileRemain => 0;
        public long Speed => 0;
    }
}
