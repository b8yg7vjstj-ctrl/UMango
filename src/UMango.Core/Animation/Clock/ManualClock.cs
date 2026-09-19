namespace UMango.Core.Animation.Clock;

/// <summary>
/// 手动时钟：由调用方决定"何时推进一帧"。
///
/// **存在的唯一理由是让动画引擎可测**：真实时钟依赖墙上时间，
/// 测试里要么 sleep（慢且不稳），要么断言"大概到了"（不可靠）。
/// 用它能写出确定性的断言 —— 推 10 帧就是 10 帧，一毫秒都不会多。
/// </summary>
public sealed class ManualClock : IClock
{
    private long _frame = -1;

    public int Fps { get; set; } = 60;
    public bool IsRunning { get; private set; }

    public event EventHandler<long>? Tick;

    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;

    /// <summary>推进一帧并触发 Tick。返回新的帧序号。</summary>
    public long Advance()
    {
        _frame++;
        Tick?.Invoke(this, _frame);
        return _frame;
    }

    /// <summary>推进若干帧。</summary>
    public void Advance(int frames)
    {
        for (var i = 0; i < frames; i++) Advance();
    }
}
