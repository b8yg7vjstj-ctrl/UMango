// SPDX-License-Identifier: Apache-2.0
//
// 动画值类型 —— 采用自 PCL-CE（PCL-Community/PCL-CE，Apache-2.0）。
// 原始文件：PCL.Core/UI/NColor.cs、PCL.Core/UI/NRotateTransform.cs、PCL.Core/UI/NScaleTransform.cs
//
// UMango 侧的改动（都是为了让类型脱离 WPF，好让 macOS 上的测试宿主也能跑）：
//   1. PCL 的 NColor 直接用 `System.Windows.Media.Color` / `SolidColorBrush` 做构造函数与隐式转换，
//      那是 WPF 类型 —— 一旦 Core 引用它，macOS 测试宿主就加载不了 PresentationCore。
//      这里改成**自包含的纯托管实现**（float RGBA），并另建 WPF 适配扩展（见 App 层）。
//   2. PCL 的 NRotateTransform / NScaleTransform 构造函数接受 WPF 的 RotateTransform / ScaleTransform，
//      并且隐式转换回 WPF 类型 —— 同样剥离到 App 层做。
//   3. PCL 的 NColor(string) 构造会去 `Lifecycle.CurrentApplication.FindResource(str)` 查资源，
//      那是应用层设施、Core 里根本没有 —— 剥离为纯字符串解析。
//   4. PCL 的 NColor 十六进制解析只处理 #RGB / #RGBA / #RRGGBB / #RRGGBBAA，
//      且**不检查长度以外的合法性**（例如 "#GGG" 会抛 FormatException 而不是可读错误）。
//      这里补了明确的中文错误信息。
//
// 另补了 PCL 没有的三个类型（PCL 的 Point / Thickness / Matrix 处理器直接用 WPF 类型，
// 无法在 Core 复用）：
//   NPoint、NThickness、NMatrix —— 与 WPF 的 Point / Thickness / Matrix 字段一一对应。

using System.Globalization;

namespace UMango.Core.Animation.Values;

/// <summary>
/// 动画用的颜色值（RGBA，0~255 浮点）。
///
/// 与 PCL-CE 的 `PCL.Core/UI/NColor.cs` 对应（Apache-2.0）。
/// 语义保持一致：四个分量都是 float，默认不透明（alpha=255）。
/// </summary>
public readonly struct NColor : IEquatable<NColor>
{
    /// <summary>红色分量（0~255）。</summary>
    public float R { get; }

    /// <summary>绿色分量（0~255）。</summary>
    public float G { get; }

    /// <summary>蓝色分量（0~255）。</summary>
    public float B { get; }

    /// <summary>透明度（0~255）。</summary>
    public float A { get; }

    /// <summary>不透明黑色。</summary>
    public NColor(float r, float g, float b, float a = 255f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>不透明黑（alpha=255）。与 PCL 的无参构造一致。</summary>
    public NColor() : this(0f, 0f, 0f, 255f) { }

    /// <summary>用同一 RGB 造一个指定透明度的颜色。</summary>
    public NColor(float a, NColor color) : this(color.R, color.G, color.B, a) { }

    /// <summary>
    /// 从十六进制字符串解析。支持 <c>#RGB</c> / <c>#RGBA</c> / <c>#RRGGBB</c> / <c>#RRGGBBAA</c>。
    /// </summary>
    /// <exception cref="ArgumentException">字符串为空、不以 <c>#</c> 开头或长度非法。</exception>
    public NColor(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("颜色字符串不能为空。", nameof(text));

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('#'))
            throw new ArgumentException("颜色字符串必须以 '#' 开头。", nameof(text));

        var body = trimmed[1..];
        int r, g, b, a;
        switch (body.Length)
        {
            case 3: // #RGB
                r = Hex(body[0].ToString() + body[0]);
                g = Hex(body[1].ToString() + body[1]);
                b = Hex(body[2].ToString() + body[2]);
                a = 255;
                break;
            case 4: // #RGBA
                r = Hex(body[0].ToString() + body[0]);
                g = Hex(body[1].ToString() + body[1]);
                b = Hex(body[2].ToString() + body[2]);
                a = Hex(body[3].ToString() + body[3]);
                break;
            case 6: // #RRGGBB
                r = Hex(body[..2]);
                g = Hex(body[2..4]);
                b = Hex(body[4..6]);
                a = 255;
                break;
            case 8: // #RRGGBBAA
                r = Hex(body[..2]);
                g = Hex(body[2..4]);
                b = Hex(body[4..6]);
                a = Hex(body[6..8]);
                break;
            default:
                throw new ArgumentException($"无效的颜色字符串长度：{body.Length}。", nameof(text));
        }

        R = r;
        G = g;
        B = b;
        A = a;

        static int Hex(string s)
        {
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                throw new ArgumentException($"无效的十六进制数字：{s}。", nameof(s));
            return v;
        }
    }

    /// <summary>输出 <c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>（alpha 非 255 时带 alpha）。</summary>
    public string ToHexString()
    {
        static int Clamp255(float v) => (int)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0f, 255f);
        var hex = $"{Clamp255(R):X2}{Clamp255(G):X2}{Clamp255(B):X2}";
        return A >= 255f ? $"#{hex}" : $"#{hex}{Clamp255(A):X2}";
    }

    /// <summary>按 HSL 构造颜色（色相 0~360，饱和度/明度 0~100）。与 PCL 的 <c>FromHsl</c> 一致。</summary>
    public static NColor FromHsl(double hue, double saturation, double lightness)
    {
        var s = saturation / 100.0;
        var l = lightness / 100.0;

        if (Math.Abs(s) < 1e-9)
            return new NColor((float)(l * 255), (float)(l * 255), (float)(l * 255));

        var h = hue / 360.0;
        s = l < 0.5 ? s * l + l : s * (1.0 - l) + l;
        l = 2 * l - s;

        return new NColor(
            (float)(255 * Hue(l, s, h + 1.0 / 3.0)),
            (float)(255 * Hue(l, s, h)),
            (float)(255 * Hue(l, s, h - 1.0 / 3.0)));

        static double Hue(double l, double s, double h)
        {
            if (h < 0) h += 1;
            if (h > 1) h -= 1;
            if (h * 6 < 1) return l + (s - l) * h * 6;
            if (h * 2 < 1) return s;
            if (h * 3 < 2) return l + (s - l) * (2.0 / 3.0 - h) * 6;
            return l;
        }
    }

    public static NColor operator +(NColor a, NColor b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);

    public static NColor operator -(NColor a, NColor b) => new(a.R - b.R, a.G - b.G, a.B - b.B, a.A - b.A);

    public static NColor operator *(NColor a, float b) => new(a.R * b, a.G * b, a.B * b, a.A * b);

    public static NColor operator /(NColor a, float b) =>
        b == 0 ? throw new DivideByZeroException("除数不能为零。") : new NColor(a.R / b, a.G / b, a.B / b, a.A / b);

    public static bool operator ==(NColor a, NColor b) => a.Equals(b);

    public static bool operator !=(NColor a, NColor b) => !a.Equals(b);

    public bool Equals(NColor other) =>
        R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);

    public override bool Equals(object? obj) => obj is NColor c && Equals(c);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    public override string ToString() => ToHexString();
}

/// <summary>动画用的点（与 WPF 的 <c>Point</c> 字段一致）。</summary>
public readonly struct NPoint : IEquatable<NPoint>
{
    public double X { get; }
    public double Y { get; }

    public NPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static NPoint operator +(NPoint a, NPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static NPoint operator -(NPoint a, NPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static NPoint operator *(NPoint a, double b) => new(a.X * b, a.Y * b);
    public static bool operator ==(NPoint a, NPoint b) => a.Equals(b);
    public static bool operator !=(NPoint a, NPoint b) => !a.Equals(b);

    public bool Equals(NPoint other) => X.Equals(other.X) && Y.Equals(other.Y);
    public override bool Equals(object? obj) => obj is NPoint p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>动画用的四边厚度（与 WPF 的 <c>Thickness</c> 字段一致）。</summary>
public readonly struct NThickness : IEquatable<NThickness>
{
    public double Left { get; }
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }

    public NThickness(double left, double top, double right, double bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    /// <summary>四边同值。</summary>
    public NThickness(double uniform) : this(uniform, uniform, uniform, uniform) { }

    public static NThickness operator +(NThickness a, NThickness b) =>
        new(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);

    public static NThickness operator -(NThickness a, NThickness b) =>
        new(a.Left - b.Left, a.Top - b.Top, a.Right - b.Right, a.Bottom - b.Bottom);

    public static NThickness operator *(NThickness a, double b) =>
        new(a.Left * b, a.Top * b, a.Right * b, a.Bottom * b);

    public static bool operator ==(NThickness a, NThickness b) => a.Equals(b);
    public static bool operator !=(NThickness a, NThickness b) => !a.Equals(b);

    public bool Equals(NThickness other) =>
        Left.Equals(other.Left) && Top.Equals(other.Top) &&
        Right.Equals(other.Right) && Bottom.Equals(other.Bottom);

    public override bool Equals(object? obj) => obj is NThickness t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    public override string ToString() => $"[{Left}, {Top}, {Right}, {Bottom}]";
}

/// <summary>动画用的 2D 仿射矩阵（与 WPF 的 <c>Matrix</c> 字段一致）。</summary>
public readonly struct NMatrix : IEquatable<NMatrix>
{
    public double M11 { get; }
    public double M12 { get; }
    public double M21 { get; }
    public double M22 { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }

    public NMatrix(double m11, double m12, double m21, double m22, double offsetX, double offsetY)
    {
        M11 = m11;
        M12 = m12;
        M21 = m21;
        M22 = m22;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    /// <summary>单位矩阵。</summary>
    public NMatrix() : this(1, 0, 0, 1, 0, 0) { }

    public static NMatrix operator +(NMatrix a, NMatrix b) =>
        new(a.M11 + b.M11, a.M12 + b.M12, a.M21 + b.M21, a.M22 + b.M22, a.OffsetX + b.OffsetX, a.OffsetY + b.OffsetY);

    public static NMatrix operator -(NMatrix a, NMatrix b) =>
        new(a.M11 - b.M11, a.M12 - b.M12, a.M21 - b.M21, a.M22 - b.M22, a.OffsetX - b.OffsetX, a.OffsetY - b.OffsetY);

    public static NMatrix operator *(NMatrix a, double b) =>
        new(a.M11 * b, a.M12 * b, a.M21 * b, a.M22 * b, a.OffsetX * b, a.OffsetY * b);

    public static bool operator ==(NMatrix a, NMatrix b) => a.Equals(b);
    public static bool operator !=(NMatrix a, NMatrix b) => !a.Equals(b);

    public bool Equals(NMatrix other) =>
        M11.Equals(other.M11) && M12.Equals(other.M12) && M21.Equals(other.M21) &&
        M22.Equals(other.M22) && OffsetX.Equals(other.OffsetX) && OffsetY.Equals(other.OffsetY);

    public override bool Equals(object? obj) => obj is NMatrix m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(M11, M12, M21, M22, OffsetX, OffsetY);
    public override string ToString() => $"[{M11},{M12};{M21},{M22}]";
}

/// <summary>
/// 动画用的缩放变换（与 PCL-CE 的 `PCL.Core/UI/NScaleTransform.cs` 对应，Apache-2.0）。
/// </summary>
public readonly struct NScaleTransform : IEquatable<NScaleTransform>
{
    public float ScaleX { get; }
    public float ScaleY { get; }
    public float CenterX { get; }
    public float CenterY { get; }

    public NScaleTransform(float scaleX, float scaleY, float centerX = 0f, float centerY = 0f)
    {
        ScaleX = scaleX;
        ScaleY = scaleY;
        CenterX = centerX;
        CenterY = centerY;
    }

    public static NScaleTransform operator +(NScaleTransform a, NScaleTransform b) =>
        new(a.ScaleX + b.ScaleX, a.ScaleY + b.ScaleY, a.CenterX + b.CenterX, a.CenterY + b.CenterY);

    public static NScaleTransform operator -(NScaleTransform a, NScaleTransform b) =>
        new(a.ScaleX - b.ScaleX, a.ScaleY - b.ScaleY, a.CenterX - b.CenterX, a.CenterY - b.CenterY);

    public static NScaleTransform operator *(NScaleTransform a, float b) =>
        new(a.ScaleX * b, a.ScaleY * b, a.CenterX * b, a.CenterY * b);

    public static NScaleTransform operator /(NScaleTransform a, float b) =>
        b == 0 ? throw new DivideByZeroException("除数不能为零。")
               : new NScaleTransform(a.ScaleX / b, a.ScaleY / b, a.CenterX / b, a.CenterY / b);

    public static bool operator ==(NScaleTransform a, NScaleTransform b) => a.Equals(b);
    public static bool operator !=(NScaleTransform a, NScaleTransform b) => !a.Equals(b);

    public bool Equals(NScaleTransform other) =>
        ScaleX.Equals(other.ScaleX) && ScaleY.Equals(other.ScaleY) &&
        CenterX.Equals(other.CenterX) && CenterY.Equals(other.CenterY);

    public override bool Equals(object? obj) => obj is NScaleTransform s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(ScaleX, ScaleY, CenterX, CenterY);
    public override string ToString() => $"scale({ScaleX}, {ScaleY}) @({CenterX}, {CenterY})";
}

/// <summary>
/// 动画用的旋转变换（与 PCL-CE 的 `PCL.Core/UI/NRotateTransform.cs` 对应，Apache-2.0）。
/// </summary>
public readonly struct NRotateTransform : IEquatable<NRotateTransform>
{
    /// <summary>旋转角度（度）。</summary>
    public float Angle { get; }

    public float CenterX { get; }
    public float CenterY { get; }

    public NRotateTransform(float angle, float centerX = 0f, float centerY = 0f)
    {
        Angle = angle;
        CenterX = centerX;
        CenterY = centerY;
    }

    public static NRotateTransform operator +(NRotateTransform a, NRotateTransform b) =>
        new(a.Angle + b.Angle, a.CenterX + b.CenterX, a.CenterY + b.CenterY);

    public static NRotateTransform operator -(NRotateTransform a, NRotateTransform b) =>
        new(a.Angle - b.Angle, a.CenterX - b.CenterX, a.CenterY - b.CenterY);

    public static NRotateTransform operator *(NRotateTransform a, float b) =>
        new(a.Angle * b, a.CenterX * b, a.CenterY * b);

    public static NRotateTransform operator /(NRotateTransform a, float b) =>
        b == 0 ? throw new DivideByZeroException("除数不能为零。")
               : new NRotateTransform(a.Angle / b, a.CenterX / b, a.CenterY / b);

    public static bool operator ==(NRotateTransform a, NRotateTransform b) => a.Equals(b);
    public static bool operator !=(NRotateTransform a, NRotateTransform b) => !a.Equals(b);

    public bool Equals(NRotateTransform other) =>
        Angle.Equals(other.Angle) && CenterX.Equals(other.CenterX) && CenterY.Equals(other.CenterY);

    public override bool Equals(object? obj) => obj is NRotateTransform r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Angle, CenterX, CenterY);
    public override string ToString() => $"rotate({Angle}°) @({CenterX}, {CenterY})";
}
