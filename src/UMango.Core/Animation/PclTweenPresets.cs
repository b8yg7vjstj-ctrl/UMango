// SPDX-License-Identifier: Apache-2.0
//
// PCL 页面动效预设 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Controls/MyPageRight.cs:552-600（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义与公开的数值**，
// 不复制其代码实现；本文件为独立重写。
//
// PCL 的 `MyPageRight.TriggerEnterAnimation` 对每个可动画控件做三件事：
//
//   Control.Opacity = 0d;
//   Control.RenderTransform = new TranslateTransform(0d, -16);
//   aniList.Add(AaOpacity(Control, 1d,  100, delay, new AniEaseOutFluent(Weak)));
//   aniList.Add(AaTranslateY(Control,  5d, 250, delay, new AniEaseOutFluent()));
//   aniList.Add(AaTranslateY(Control, 11d, 350, delay, new AniEaseOutBack()));
//   delay += 25;                                  // ← 子项级联
//
// 关键语义（与 PCL 一字不差的三条）：
//   1. 补间值是**增量**。`AaOpacity(1d)` 是"不透明度 +1"（从 0 到 1），
//      `AaTranslateY(5d)` 是"往下挪 5 像素"（从 -16 到 -11）。
//   2. 两条 `AaTranslateY` **并行推进**，不是先后。
//      PCL 的 `AniRun` 每帧对每条 AniData 各算自己的增量，再累加到目标当前值上。
//      所以任意时刻 Y = -16 + 5·f₁(t) + 11·f₂(t)，终态 = 0。
//   3. 三条补间**共享同一个 delay**（该控件的级联延迟），不是各自独立。
//
// UMango 与 PCL 的差异：
//   · PCL 直接用 `dynamic` 反射改 WPF 属性；这里产出**纯数据的 TweenSpec**，
//     由宿主（WPF / Avalonia）把通道接到自己的属性上 —— 同一套数值可跨框架复用。
//   · PCL 的滚动条动画（`AaTranslateX(-X, 350, 0, FluentOut)`，初值 X=10）
//     也一并建模为 <see cref="ScrollBarSlide"/>。

namespace UMango.Core.Animation;

/// <summary>
/// PCL 的页面进入 / 退出动效预设（纯数据）。
/// </summary>
public static class PclTweenPresets
{
    /// <summary>PCL 的淡入时长（ms）。</summary>
    public const int FadeInMs = 100;

    /// <summary>PCL 的中段位移时长（ms）。</summary>
    public const int EnterMidMs = 250;

    /// <summary>PCL 的末段位移时长（ms）。</summary>
    public const int EnterToMs = 350;

    /// <summary>PCL 的子项级联步进（ms）。</summary>
    public const int StaggerMs = 25;

    /// <summary>PCL 的入场起始 Y 偏移。</summary>
    public const double EnterFromY = -16d;

    /// <summary>PCL 的中段位移量（增量）。</summary>
    public const double EnterMidDelta = 5d;

    /// <summary>PCL 的末段位移量（增量）。</summary>
    public const double EnterToDelta = 11d;

    /// <summary>PCL 的滚动条入场起始 X 偏移。</summary>
    public const double ScrollBarFromX = 10d;

    /// <summary>PCL 的滚动条入场时长（ms）。</summary>
    public const int ScrollBarMs = 350;

    /// <summary>
    /// PCL 的入场缓动（淡入用 <c>AniEaseOutFluent(Weak)</c> = <c>1-(1-t)^2</c>）。
    /// </summary>
    public static Easing FadeInEase => new FluentOutEasing(EasePower.Weak);

    /// <summary>PCL 的中段缓动（<c>AniEaseOutFluent()</c> = <c>1-(1-t)^3</c>）。</summary>
    public static Easing EnterMidEase => new FluentOutEasing(EasePower.Middle);

    /// <summary>PCL 的末段缓动（<c>AniEaseOutBack</c>，指数随档位变化）。</summary>
    public static Easing EnterToEase => new BackEaseWithPowerEasing(EasePower.Middle);

    /// <summary>
    /// 生成一个控件的 PCL 入场补间组。
    /// </summary>
    /// <param name="delayMs">该控件的级联延迟（三条补间共享）。</param>
    /// <returns>三条补间：不透明度 ×1、中段位移 +5、末段位移 +11。</returns>
    public static IReadOnlyList<TweenSpec> PageEnter(int delayMs = 0)
    {
        var delay = Math.Max(0, delayMs);
        return
        [
            TweenFactory.AaOpacity(1d, FadeInMs, delay, FadeInEase),
            TweenFactory.AaTranslateY(EnterMidDelta, EnterMidMs, delay, EnterMidEase),
            TweenFactory.AaTranslateY(EnterToDelta, EnterToMs, delay, EnterToEase),
        ];
    }

    /// <summary>
    /// 生成一组子项的 PCL 入场补间（级联）。
    /// </summary>
    /// <param name="count">子项数量。</param>
    /// <param name="staggerMs">级联步进（PCL 用 25ms）。</param>
    /// <returns>每个子项一组补间，延迟依次递增。</returns>
    public static IReadOnlyList<IReadOnlyList<TweenSpec>> PageEnterStagger(int count, int staggerMs = StaggerMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var step = Math.Max(0, staggerMs);
        var result = new List<IReadOnlyList<TweenSpec>>(count);
        for (var i = 0; i < count; i++)
            result.Add(PageEnter(i * step));
        return result;
    }

    /// <summary>
    /// PCL 的滚动条入场：初值 X=10，用 350ms 滑回 0。
    ///
    /// PCL 的写法是 <c>AaTranslateX(scroll, -X, 350, 0, FluentOut)</c> ——
    /// 增量等于"初值的相反数"，所以终值恰好是 0。
    /// </summary>
    public static TweenSpec ScrollBarSlide(double fromX = ScrollBarFromX, int delayMs = 0)
        => TweenFactory.AaTranslateX(-fromX, ScrollBarMs, delayMs, new FluentOutEasing(EasePower.Middle));

    /// <summary>
    /// PCL 的页面退出动效。
    ///
    /// PCL 的 `TriggerExitAnimation`（MyPageRight.cs:602-）用 70ms 的短动画
    /// 把内容向上收走（Y 终态 -6），并给每个控件加 15ms 的级联延迟。
    /// </summary>
    public static IReadOnlyList<TweenSpec> PageExit(int delayMs = 0)
    {
        var delay = Math.Max(0, delayMs);
        return
        [
            TweenFactory.AaOpacity(-1d, ExitMs, delay, new FluentOutEasing(EasePower.Weak)),
            TweenFactory.AaTranslateY(ExitToY, ExitMs, delay, new FluentOutEasing(EasePower.Middle)),
        ];
    }

    /// <summary>PCL 的退场时长（ms）。</summary>
    public const int ExitMs = 70;

    /// <summary>PCL 的退场 Y 终态。</summary>
    public const double ExitToY = -6d;

    /// <summary>PCL 的退场级联步进（ms）。</summary>
    public const int ExitStaggerMs = 15;
}
