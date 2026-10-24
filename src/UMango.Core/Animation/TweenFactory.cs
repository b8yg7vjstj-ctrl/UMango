// SPDX-License-Identifier: Apache-2.0
//
// 补间工厂 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// 原始设计：PCL 用 `AniData` 描述一次补间（目标类型 + 子类型 + 数值 + 时长 + 延迟 +
// 缓动 + 是否排队），再由 `ModAnimation.AniRun` 反射式地把它落到 WPF 控件上。
//
// UMango 的差异（**刻意的架构改进**）：
//   1. PCL 的 `AniData.obj` 是 `object`，运行期靠 `dynamic` 反射读写 WPF 属性
//      （`((dynamic)obj).ActualWidth` 之类）—— 那让 PCL 的补间工厂**无法离开 WPF**。
//      这里把补间描述拆成两部分：
//        · Core 的 <see cref="TweenSpec"/>：**纯数据**，只说"要把哪个通道动到哪个值"；
//        · 宿主的绑定器（WPF 侧 <c>WpfTweenBinder</c>）：负责把通道映射到具体依赖属性。
//      于是同一套补间描述在 WPF 与 Avalonia 上都能用。
//   2. PCL 的 `timeFinished = -delay`（负数表示"延迟"）是个隐晦约定；
//      这里直接用 <see cref="TweenSpec.DelayMs"/>（正数 = 延迟多少毫秒）。
//   3. PCL 的 `isAfter`（等前面的动画跑完再跑）由动画引擎的队列实现；
//      这里用 <see cref="TweenSpec.Queued"/> 表达同样语义，由组合动画的顺序组落地。
//   4. PCL 的 `AaScale` 需要读控件的 ActualWidth/ActualHeight 算百分比 —
//      那是宿主能力，Core 侧改为要求调用方提供尺寸（见 <see cref="TweenSpec.ScaleBaseSize"/>）。
//
// 通道名（<see cref="TweenChannel"/>）与 PCL 的 `AniTypeSub` 一一对应，
// 便于对照与迁移。

using UMango.Core.Animation.Values;

namespace UMango.Core.Animation;

/// <summary>
/// 补间要动的通道（对应 PCL 的 <c>AniTypeSub</c>）。
/// </summary>
public enum TweenChannel
{
    /// <summary>X 坐标。</summary>
    X,
    /// <summary>Y 坐标。</summary>
    Y,
    /// <summary>宽度。</summary>
    Width,
    /// <summary>高度。</summary>
    Height,
    /// <summary>不透明度。</summary>
    Opacity,
    /// <summary>通用数值（对应 PCL 的 <c>AaValue</c>）。</summary>
    Value,
    /// <summary>圆角半径。</summary>
    Radius,
    /// <summary>边框厚度。</summary>
    BorderThickness,
    /// <summary>描边厚度。</summary>
    StrokeThickness,
    /// <summary>平移变换的 X。</summary>
    TranslateX,
    /// <summary>平移变换的 Y。</summary>
    TranslateY,
    /// <summary>任意依赖属性上的 double（对应 PCL 的 <c>AaDouble</c>）。</summary>
    Double,
    /// <summary>任意依赖属性上的 double（带参数版本，对应 PCL 的 <c>AaDouble(ParameterizedThreadStart, …)</c>）。</summary>
    DoubleParam,
    /// <summary>网格列宽（对应 PCL 的 <c>AaGridLengthWidth</c>）。</summary>
    GridLengthWidth,
    /// <summary>缩放（整体缩放控件大小）。</summary>
    Scale,
    /// <summary>颜色。</summary>
    Color,
    /// <summary>旋转变换。</summary>
    RotateTransform,
    /// <summary>缩放变换。</summary>
    ScaleTransform,
    /// <summary>文字逐字出现。</summary>
    TextAppear,
}

/// <summary>补间的种类（对应 PCL 的 <c>AniType</c>）。</summary>
public enum TweenKind
{
    /// <summary>单个数值。</summary>
    Number,
    /// <summary>颜色。</summary>
    Color,
    /// <summary>缩放（整体尺寸）。</summary>
    Scale,
    /// <summary>文字逐字出现。</summary>
    TextAppear,
    /// <summary>执行一段代码。</summary>
    Code,
    /// <summary>缩放变换。</summary>
    ScaleTransform,
    /// <summary>旋转变换。</summary>
    RotateTransform,
}

/// <summary>
/// 一次补间的**纯数据描述**。
///
/// 与 PCL 的 <c>AniData</c> 对应（借鉴设计）。它不引用任何界面框架类型，
/// 因此可以在 macOS 上直接构造与断言。
/// </summary>
public sealed record TweenSpec
{
    /// <summary>补间种类。</summary>
    public required TweenKind Kind { get; init; }

    /// <summary>要动的通道。</summary>
    public required TweenChannel Channel { get; init; }

    /// <summary>目标数值（double 通道用）。</summary>
    public double Value { get; init; }

    /// <summary>目标值（颜色通道用）。</summary>
    public NColor? Color { get; init; }

    /// <summary>目标缩放（<see cref="TweenKind.Scale"/> 用）。</summary>
    public double Scale { get; init; }

    /// <summary>
    /// 缩放的基准尺寸（<see cref="TweenKind.Scale"/> 用）。
    /// PCL 在运行期读控件的 ActualWidth/ActualHeight；Core 侧不能读宿主对象，
    /// 因此由调用方提供。宽或高为 0 时按 1 处理（避免除零）。
    /// </summary>
    public (double Width, double Height) ScaleBaseSize { get; init; } = (1, 1);

    /// <summary>时长（毫秒）。</summary>
    public int DurationMs { get; init; } = 400;

    /// <summary>延迟（毫秒）。</summary>
    public int DelayMs { get; init; }

    /// <summary>缓动。默认线性（与 PCL 一致：PCL 的 `ease ?? new AniEaseLinear()`）。</summary>
    public Easing? Easing { get; init; }

    /// <summary>是否排队（等前面的动画跑完再跑）。对应 PCL 的 <c>isAfter</c>。</summary>
    public bool Queued { get; init; }

    /// <summary>
    /// 文字内容（<see cref="TweenKind.TextAppear"/> 用）。
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// 文字是逐字**消失**而不是出现（<see cref="TweenKind.TextAppear"/> 用）。
    /// </summary>
    public bool TextHide { get; init; }

    /// <summary>
    /// 每个字占用多少毫秒（<see cref="TweenKind.TextAppear"/> 用）。
    /// 为 0 表示整段文本用固定时长。
    /// </summary>
    public int TimePerTextMs { get; init; }

    /// <summary>要执行的代码（<see cref="TweenKind.Code"/> 用）。</summary>
    public Action? Code { get; init; }

    /// <summary>
    /// 依赖属性的键（<see cref="TweenChannel.Double"/> / <see cref="TweenChannel.DoubleParam"/> 用）。
    ///
    /// Core 侧用字符串键而不是框架的 <c>DependencyProperty</c> ——
    /// 后者是 WPF 类型。宿主绑定器负责把键映射到真正的依赖属性。
    /// </summary>
    public string? PropertyKey { get; init; }

    /// <summary>实际时长（含延迟之外的部分）。</summary>
    public int EffectiveDurationMs => Math.Max(0, DurationMs);
}

/// <summary>
/// 补间工厂：PCL 的 <c>ModAnimation.Aa*</c> 系列在此以**纯数据**形式重建。
///
/// 与 PCL 的对应关系：
/// <list type="table">
///   <item><term>PCL</term><description><c>AaX</c> / <c>AaY</c> / <c>AaWidth</c> / <c>AaHeight</c> /
///   <c>AaOpacity</c> / <c>AaValue</c> / <c>AaRadius</c> / <c>AaBorderThickness</c> /
///   <c>AaStrokeThickness</c> / <c>AaGridLengthWidth</c> / <c>AaDouble</c> /
///   <c>AaScale</c> / <c>AaScaleTransform</c> / <c>AaRotateTransform</c> /
///   <c>AaTranslateX</c> / <c>AaTranslateY</c> / <c>AaTextAppear</c> / <c>AaCode</c> /
///   <c>AaColor</c></description></item>
///   <item><term>UMango</term><description>本类的同名方法</description></item>
/// </list>
///
/// 参数语义与 PCL 一致：<c>time</c> = 时长（毫秒，默认 400）、<c>delay</c> = 延迟（默认 0）、
/// <c>ease</c> = 缓动（默认线性）、<c>after</c> = 是否排队。
/// </summary>
public static class TweenFactory
{
    private static TweenSpec Number(TweenChannel channel, double value, int time, int delay, Easing? ease, bool after) =>
        new()
        {
            Kind = TweenKind.Number,
            Channel = channel,
            Value = value,
            DurationMs = time,
            DelayMs = delay,
            Easing = ease,
            Queued = after,
        };

    /// <summary>移动 X 轴。</summary>
    public static TweenSpec AaX(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.X, value, time, delay, ease, after);

    /// <summary>移动 Y 轴。</summary>
    public static TweenSpec AaY(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.Y, value, time, delay, ease, after);

    /// <summary>改变宽度。</summary>
    public static TweenSpec AaWidth(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.Width, value, time, delay, ease, after);

    /// <summary>改变高度。</summary>
    public static TweenSpec AaHeight(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.Height, value, time, delay, ease, after);

    /// <summary>改变不透明度。</summary>
    public static TweenSpec AaOpacity(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.Opacity, value, time, delay, ease, after);

    /// <summary>改变通用数值。</summary>
    public static TweenSpec AaValue(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.Value, value, time, delay, ease, after);

    /// <summary>改变圆角半径。</summary>
    public static TweenSpec AaRadius(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.Radius, value, time, delay, ease, after);

    /// <summary>改变边框厚度。</summary>
    public static TweenSpec AaBorderThickness(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.BorderThickness, value, time, delay, ease, after);

    /// <summary>改变描边厚度。</summary>
    public static TweenSpec AaStrokeThickness(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.StrokeThickness, value, time, delay, ease, after);

    /// <summary>改变网格列宽。</summary>
    public static TweenSpec AaGridLengthWidth(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.GridLengthWidth, value, time, delay, ease, after);

    /// <summary>改变任意依赖属性上的 double。</summary>
    public static TweenSpec AaDouble(string propertyKey, double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.Double, value, time, delay, ease, after) with { PropertyKey = propertyKey };

    /// <summary>改变平移变换的 X。</summary>
    public static TweenSpec AaTranslateX(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.TranslateX, value, time, delay, ease, after);

    /// <summary>改变平移变换的 Y。</summary>
    public static TweenSpec AaTranslateY(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false)
        => Number(TweenChannel.TranslateY, value, time, delay, ease, after);

    /// <summary>
    /// 改变平移变换的位移量（按通道区分 X / Y）。
    ///
    /// PCL 的入场位移是**两段并行叠加**：初值 Y=-16，
    /// 段1 <c>AaTranslateY(+5, 250ms)</c>、段2 <c>AaTranslateY(+11, 350ms)</c>，
    /// 终态 = -16 + 5 + 11 = 0。两段**同时推进**，不是先后。
    /// </summary>
    public static TweenSpec AaTranslate(TweenChannel channel, double value, int time = 400, int delay = 0,
        Easing? ease = null, bool after = false)
    {
        if (channel is not (TweenChannel.TranslateX or TweenChannel.TranslateY))
            throw new ArgumentException("平移补间只能用于 TranslateX / TranslateY 通道。", nameof(channel));
        return Number(channel, value, time, delay, ease, after);
    }

    /// <summary>改变颜色。</summary>
    public static TweenSpec AaColor(NColor value, int time = 400, int delay = 0, Easing? ease = null, bool after = false) =>
        new()
        {
            Kind = TweenKind.Color,
            Channel = TweenChannel.Color,
            Color = value,
            DurationMs = time,
            DelayMs = delay,
            Easing = ease,
            Queued = after,
        };

    /// <summary>
    /// 改变缩放变换。
    ///
    /// PCL 的 <c>AaScaleTransform</c> 走 WPF 的 <c>ScaleTransform</c>；
    /// 这里产出 <see cref="TweenKind.ScaleTransform"/>，由宿主绑定到自己的变换对象。
    /// </summary>
    public static TweenSpec AaScaleTransform(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false) =>
        new()
        {
            Kind = TweenKind.ScaleTransform,
            Channel = TweenChannel.ScaleTransform,
            Value = value,
            DurationMs = time,
            DelayMs = delay,
            Easing = ease,
            Queued = after,
        };

    /// <summary>改变旋转变换（角度，度）。</summary>
    public static TweenSpec AaRotateTransform(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false) =>
        new()
        {
            Kind = TweenKind.RotateTransform,
            Channel = TweenChannel.RotateTransform,
            Value = value,
            DurationMs = time,
            DelayMs = delay,
            Easing = ease,
            Queued = after,
        };

    /// <summary>
    /// 整体缩放控件。
    ///
    /// PCL 的 <c>AaScale</c> 有 <c>absolute</c> 参数：<c>true</c> 表示"绝对像素"，
    /// <c>false</c> 表示"相对当前尺寸的倍数"。这里用 <see cref="TweenSpec.ScaleBaseSize"/>
    /// 显式给出基准尺寸，倍数值放在 <see cref="TweenSpec.Scale"/>。
    /// </summary>
    public static TweenSpec AaScale(double value, int time = 400, int delay = 0, Easing? ease = null, bool after = false,
        (double Width, double Height)? baseSize = null) =>
        new()
        {
            Kind = TweenKind.Scale,
            Channel = TweenChannel.Scale,
            Scale = value,
            ScaleBaseSize = baseSize ?? (1, 1),
            DurationMs = time,
            DelayMs = delay,
            Easing = ease,
            Queued = after,
        };

    /// <summary>
    /// 让一段文字逐字出现（或逐字消失）。
    ///
    /// PCL 的 <c>AaTextAppear</c> 在 <c>timePerText=true</c> 时，总时长 = 每字时长 × 字数。
    /// 这里用 <see cref="TweenSpec.TimePerTextMs"/> 表达：大于 0 时总时长 = 每字时长 × 字数。
    /// </summary>
    public static TweenSpec AaTextAppear(string text, bool hide = false, int timePerTextMs = 70, int time = 70,
        int delay = 0, Easing? ease = null, bool after = false) =>
        new()
        {
            Kind = TweenKind.TextAppear,
            Channel = TweenChannel.TextAppear,
            Text = text,
            TextHide = hide,
            TimePerTextMs = timePerTextMs,
            DurationMs = timePerTextMs > 0 ? timePerTextMs * (text?.Length ?? 0) : time,
            DelayMs = delay,
            Easing = ease,
            Queued = after,
        };

    /// <summary>执行一段代码（对应 PCL 的 <c>AaCode</c>）。</summary>
    public static TweenSpec AaCode(Action code, int delay = 0, bool after = false) =>
        new()
        {
            Kind = TweenKind.Code,
            Channel = TweenChannel.Value,
            Code = code,
            DurationMs = 1,
            DelayMs = delay,
            Queued = after,
        };
}
