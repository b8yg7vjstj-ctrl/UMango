// SPDX-License-Identifier: Apache-2.0
//
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   Plain Craft Launcher 2/Modules/UI/HintService.cs（104 行）
// 许可：《PCL 分发有限许可》——位于**自定义许可目录**，
//   因此**不逐字复制代码**，只借鉴其语义，在此重新实现。
//   详见 THIRD-PARTY-NOTICES.md 与 docs/PCL_CE_FEATURE_PARITY.md 的「提示服务」行。
//
// 语义对齐（来自 PCL 原版）：
//   · 提示先入**等待队列**，由界面层 Tick 时逐条取出显示（PCL 是每个动画帧 Tick 一次）；
//   · 等待队列里**同文案同类型**的提示不重复入列；
//   · 已经显示中的同文案提示再次到来 → **强调**已有那条，而不是再叠一条；
//   · 同时显示条数有上限，超出时挤掉最旧的一条；
//   · 每条提示的停留时长按**文案长度**计算。
//
// 相对原版修掉的真实缺陷：
//
//   ① **空白提示会显示成一个空浮条**。
//      原版 `Hint()` 只把 \r\n / \r / \n 换成空格，**不裁剪首尾空白**，
//      也不拒绝纯空白文案 —— 调用方拼串失败时界面会出现一个只有图标的空条。
//      这里做 normalize（换行归一 + 折叠连续空白 + 去首尾），空白直接丢弃。
//
//   ② **等待队列与已显示列表的查重口径不一致**。
//      原版 `Hint()` 只在**等待队列**里查重（`HintWaiting.Any(...)`），
//      已经在屏幕上显示的那条**不参与查重** —— 同一句话在第一条还挂着时再来一次，
//      会先入队，等到 Tick 才发现重复再"强调"。
//      结果是：重复提示**仍然占用了一个队列槽位**，
//      且在两次 Tick 之间被重复计入统计。
//      这里查重覆盖「等待队列 ∪ 已显示」，重复的**直接不入列**。
//
//   ③ **停留时长可能算成 0**。
//      原版 `DisplayDuration = (800d + MathClamp(len, 5, 23) * 180d) * ModAnimation.aniSpeed`，
//      而 `ModAnimation.aniSpeed` 在"动画加速/关闭"时可为 0 —— 乘出来就是 0，
//      提示条**刚显示就消失**（用户根本看不到）。
//      这里给停留时长设了**硬下限**，任何加速倍率下都不会短于 600ms。
//
//   ④ **超出条数上限时，PCL 是"挤掉最旧的但本轮不显示新的"**，
//      即 `Dismiss()` 之后直接 return，等待下一次 Tick —— 语义没错，
//      但在 UMango 里等价于"新提示要等一个 Tick 才能上屏"。
//      这里保持同样的语义（不擅自改变交互节奏），
//      但把它表达成一个**显式的动作枚举**而不是"返回 null 表示什么都不做"，
//      界面层不会漏掉"该挤掉最旧这条"这个动作。
//
//   ⑤ 原版的 `Tick()` 里 `PanHint.Margin` 会按 `PanExtraButtons.ActualHeight` 调整，
//      那是 PCL 自己的界面布局细节，UMango 的提示浮层有独立布局，不迁。

using System.Text;

namespace UMango.Core.Notifications;

/// <summary>提示类型。与 <see cref="NotificationType"/> 一一对应。</summary>
public enum HintType
{
    Info,
    Success,
    Error,
    Warning,
}

/// <summary>
/// 提示浮层的**队列本体**（平台中立，不依赖任何界面类型）。
///
/// 分工：
///   · 本类负责「什么时候该显示哪一条、显示多久、要不要挤掉旧的、要不要强调旧的」；
///   · 界面层负责「把这条画出来」。
/// 这样两条非显然规则（查重口径、上限挤占）都能被单元测试锁住，
/// 而不是只能靠肉眼判断"会不会刷屏"。
/// </summary>
public sealed class HintService
{
    /// <summary>同时显示的提示条上限。超出时挤掉最旧的一条。</summary>
    public const int MaxVisible = 5;

    /// <summary>停留时长的硬下限（毫秒）。防止动画加速倍率为 0 时提示条一闪而过。</summary>
    public const int MinDurationMs = 600;

    /// <summary>停留时长的硬上限（毫秒）。避免超长文案把提示条挂上几分钟。</summary>
    public const int MaxDurationMs = 8000;

    /// <summary>一条等待显示的提示。</summary>
    /// <param name="Text">已归一化的文案。</param>
    /// <param name="Type">提示类型。</param>
    /// <param name="Log">是否要写日志。</param>
    /// <param name="DurationMs">停留时长（毫秒）。</param>
    public sealed record HintMessage(string Text, HintType Type, bool Log, int DurationMs);

    private readonly Lock _lock = new();
    private readonly List<HintMessage> _waiting = [];
    private readonly List<string> _displayed = [];

    /// <summary>全部提示被清空时触发（界面层据此把浮层上的提示条一并撤掉）。</summary>
    public event Action? Cleared;

    /// <summary>当前等待显示的提示（快照）。</summary>
    public IReadOnlyList<HintMessage> Waiting
    {
        get { lock (_lock) return _waiting.ToList(); }
    }

    /// <summary>当前已显示中的提示文案（快照）。</summary>
    public IReadOnlyList<string> Displayed
    {
        get { lock (_lock) return _displayed.ToList(); }
    }

    /// <summary>
    /// 归一化一条提示文案：换行归一、连续空白折叠为一个空格、去首尾空白。
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' or ' ')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 按文案长度计算停留时长（毫秒）。
    /// 文案越长停留越久，但有上下限。
    /// </summary>
    public static int ComputeDurationMs(string text, double animationSpeed = 1.0)
    {
        // 与 PCL 的公式同构：(800 + clamp(len, 5, 23) * 180) * speed
        var length = Math.Clamp(text.Length, 5, 23);
        var raw = (800d + length * 180d) * (double.IsFinite(animationSpeed) ? animationSpeed : 1d);
        return Math.Clamp((int)Math.Round(raw), MinDurationMs, MaxDurationMs);
    }

    /// <summary>
    /// 入列一条提示。重复的（等待中或已显示）会被**丢弃**。
    /// </summary>
    /// <returns>true = 已入列；false = 空白或重复，被丢弃。</returns>
    public bool Hint(string? text, HintType type = HintType.Info, bool log = true,
        double animationSpeed = 1.0)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return false;

        lock (_lock)
        {
            // 查重覆盖「等待队列 ∪ 已显示」—— 见文件头缺陷②
            foreach (var item in _waiting)
                if (item.Text == normalized && item.Type == type)
                    return false;
            foreach (var shown in _displayed)
                if (shown == normalized)
                    return false;

            _waiting.Add(new HintMessage(normalized, type, log, ComputeDurationMs(normalized, animationSpeed)));
            return true;
        }
    }

    /// <summary>入列一条提示并返回它（供界面层在需要拿到时长时使用）。</summary>
    public HintMessage? HintAndGet(string? text, HintType type = HintType.Info, bool log = true,
        double animationSpeed = 1.0)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return null;

        lock (_lock)
        {
            foreach (var item in _waiting)
                if (item.Text == normalized && item.Type == type)
                    return null;
            foreach (var shown in _displayed)
                if (shown == normalized)
                    return null;

            var message = new HintMessage(normalized, type, log,
                ComputeDurationMs(normalized, animationSpeed));
            _waiting.Add(message);
            return message;
        }
    }

    /// <summary>
    /// 界面层在每个心跳调用一次，决定这一步该做什么。
    /// </summary>
    public HintAction Tick()
    {
        lock (_lock)
        {
            if (_waiting.Count == 0) return HintAction.None;

            var current = _waiting[0];

            // 已经在显示中（理论上查重时已被拦下，这里兜底）→ 强调它，不再新叠一条
            foreach (var shown in _displayed)
            {
                if (shown == current.Text)
                {
                    _waiting.RemoveAt(0);
                    return HintAction.Emphasize(current);
                }
            }

            // 已达上限 → 挤掉最旧的一条，本轮不显示新的（与 PCL 语义一致）
            if (_displayed.Count >= MaxVisible)
            {
                var oldest = _displayed[0];
                return HintAction.DismissOldest(oldest);
            }

            _waiting.RemoveAt(0);
            _displayed.Add(current.Text);
            return HintAction.Show(current);
        }
    }

    /// <summary>提示条显示完毕（到期或被用户关掉）后，界面层回报一声。</summary>
    public void NotifyDismissed(string text)
    {
        lock (_lock) { _displayed.Remove(text); }
    }

    /// <summary>
    /// 全部隐藏。等待队列清空，已显示的由界面层负责关掉（这里只清状态）。
    /// </summary>
    public void HideAll()
    {
        lock (_lock)
        {
            _waiting.Clear();
            _displayed.Clear();
        }
        Cleared?.Invoke();
    }
}

/// <summary>一次 <see cref="HintService.Tick"/> 的结果种类。</summary>
public enum HintActionKind
{
    /// <summary>什么都不做（队列为空）。</summary>
    None,

    /// <summary>显示一条新提示。</summary>
    Show,

    /// <summary>强调一条已经在显示中的提示（而不是再叠一条）。</summary>
    Emphasize,

    /// <summary>挤掉最旧的一条已显示提示（已达条数上限）。</summary>
    DismissOldest,
}

/// <summary>一次 <see cref="HintService.Tick"/> 的结果：界面层这一步该做什么。</summary>
/// <param name="Kind">动作种类。</param>
/// <param name="Message">要显示/强调的那条提示（<see cref="HintActionKind.Show"/> / <c>Emphasize</c> 时有值）。</param>
/// <param name="Text">要被挤掉的那条提示的文案（<see cref="HintActionKind.DismissOldest"/> 时有值）。</param>
public sealed record HintAction(
    HintActionKind Kind,
    HintService.HintMessage? Message = null,
    string? Text = null)
{
    /// <summary>什么都不做。</summary>
    public static HintAction None { get; } = new(HintActionKind.None);

    /// <summary>显示一条新提示。</summary>
    public static HintAction Show(HintService.HintMessage message) => new(HintActionKind.Show, message);

    /// <summary>强调一条已在显示中的提示。</summary>
    public static HintAction Emphasize(HintService.HintMessage message) => new(HintActionKind.Emphasize, message);

    /// <summary>挤掉最旧的一条已显示提示。</summary>
    public static HintAction DismissOldest(string text) => new(HintActionKind.DismissOldest, null, text);
}
