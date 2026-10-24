// SPDX-License-Identifier: Apache-2.0
//
// UMango 自有实现（未取自 PCL-CE）。
//
// 把「Core 的提示队列」与「界面上的浮条」之间的翻译层单独拎出来，
// 目的是让它**可在无界面环境测试**（UMango.App.Tests 目标框架是 net10.0，
// 不能引用 WPF 类型，所以这里一律不出现 Brush / Dispatcher / Control）。
//
// 界面层（WPF 壳）只负责：拿到 <see cref="HintPumpEvent"/> → 画成右下角浮条。

using UMango.Core.Notifications;

namespace UMango.App.Services;

/// <summary>一次提示泵动作。</summary>
public enum HintPumpEventKind
{
    /// <summary>显示一条新提示。</summary>
    Show,

    /// <summary>撤掉某条已显示的提示（被更新的挤掉，或全部清空）。</summary>
    Dismiss,
}

/// <summary>提示泵的一次动作。</summary>
/// <param name="Kind">动作种类。</param>
/// <param name="Text">提示文案。</param>
/// <param name="Type">提示类型（仅 <see cref="HintPumpEventKind.Show"/> 有意义）。</param>
/// <param name="DurationMs">停留时长（仅 <see cref="HintPumpEventKind.Show"/> 有意义）。</param>
public sealed record HintPumpEvent(
    HintPumpEventKind Kind,
    string Text,
    HintType Type = HintType.Info,
    int DurationMs = 0);

/// <summary>
/// 提示泵：把 <see cref="HintService"/> 的队列动作翻译成一串「显示 / 撤掉」事件。
///
/// 为什么需要它（而不是在 MainWindow 里直接写循环）：
///   · 队列的**出队节奏**与**淘汰联动**是两件事，界面层只该被动执行；
///   · 淘汰时界面层必须回报「这条已经撤了」，否则 Core 以为位置还被占着，
///     后面每拍都只会返回"挤掉最旧的"，新提示永远上不了屏。
///     这一条**曾经真的写错过一次**（漏了回报），把它挪到可测的类里才有回归护栏。
/// </summary>
public sealed class HintPump
{
    private readonly HintService _hints;

    /// <summary>一次心跳最多产生多少个动作（防止界面卡住，剩下的下一拍继续）。</summary>
    public const int MaxActionsPerTick = 8;

    /// <summary>构造。绑定一个提示队列。</summary>
    public HintPump(HintService hints)
    {
        _hints = hints ?? throw new ArgumentNullException(nameof(hints));
    }

    /// <summary>被绑定的提示队列。</summary>
    public HintService Hints => _hints;

    /// <summary>
    /// 跑一拍心跳，返回本拍要做的事。
    /// 队列为空时返回空列表。
    /// </summary>
    public IReadOnlyList<HintPumpEvent> Pump()
    {
        var events = new List<HintPumpEvent>();

        // 上限按**产出的动作数**算，而不是循环次数 ——
        // 因为一轮循环可能只产出一个动作（淘汰时），也可能产出两个（淘汰 + 紧接着显示新的）。
        // 循环次数也留一道闸：万一某轮只产出一个动作又继续循环，也不至于空转。
        for (var i = 0; i < MaxActionsPerTick * 2 && events.Count < MaxActionsPerTick; i++)
        {
            var action = _hints.Tick();
            switch (action.Kind)
            {
                case HintActionKind.None:
                    return events;

                case HintActionKind.Show:
                    if (action.Message is { } message)
                    {
                        events.Add(new HintPumpEvent(HintPumpEventKind.Show, message.Text, message.Type,
                            message.DurationMs));
                    }
                    break;

                case HintActionKind.Emphasize:
                    // 同文案已经在显示中 → 不叠新条（PCL 是把它提到最前，这里保持原位）
                    break;

                case HintActionKind.DismissOldest:
                    if (action.Text is { } text)
                    {
                        events.Add(new HintPumpEvent(HintPumpEventKind.Dismiss, text));
                        // **关键**：界面马上就把这条撤了 → 立刻回报 Core，
                        // 否则"已显示"列表永远满着，新提示再也上不了屏。
                        _hints.NotifyDismissed(text);
                    }
                    break;
            }
        }

        return events;
    }
}
