using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Network;
using Xunit;

namespace UMango.Core.Tests.Network;

/// <summary>
/// 加权竞速器测试。
///
/// 用可控延迟的假候选代替真实 DNS/HTTP：竞速器的价值全在"谁赢、权重怎么变、
/// 输家会不会被取消"这三点上，与传输层无关。
/// </summary>
public class WeightedRacerTests
{
    /// <summary>可控延迟的假候选：记录被调用次数与是否收到取消。</summary>
    private sealed class FakeCandidate(string name, int delayMs, bool fail = false, bool ignoreCancellation = false)
    {
        public string Name { get; } = name;
        public bool IgnoreCancellation { get; } = ignoreCancellation;
        public int DelayMs { get; } = delayMs;
        public bool Fail { get; set; } = fail;
        public int Calls { get; private set; }
        public bool WasCancelled { get; private set; }

        public async Task<string> RunAsync(CancellationToken token)
        {
            Calls++;
            try
            {
                // 无视取消：模拟不理会 CancellationToken 的原生回调型候选。
                await (IgnoreCancellation ? Task.Delay(DelayMs) : Task.Delay(DelayMs, token));
                if (Fail) throw new InvalidOperationException($"{Name} 本来就会失败");
                return Name;
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }

        public override string ToString() => Name;
    }

    private static WeightedRacer<FakeCandidate, string> Build(int concurrent, params FakeCandidate[] candidates)
        => new("测试竞速", concurrent, static (c, ct) => c.RunAsync(ct), candidates);

    [Fact]
    public async Task 最快成功者赢()
    {
        var slow = new FakeCandidate("慢", 300);
        var mid = new FakeCandidate("中", 120);
        var fast = new FakeCandidate("快", 20);
        using var racer = Build(3, slow, mid, fast);

        var result = await racer.RaceAsync();

        Assert.Equal("快", result.Value);
        Assert.Same(fast, result.Winner);
        // Losers 是"本轮参与但没赢"的全部候选（含被取消的），赢家必须在其中缺席。
        Assert.DoesNotContain(fast, result.Losers);
        Assert.Contains(slow, result.Losers);
    }

    [Fact]
    public async Task 输家会被取消而不是继续跑完()
    {
        var slow = new FakeCandidate("慢", 5000);
        var fast = new FakeCandidate("快", 20);
        using var racer = Build(2, slow, fast);

        await racer.RaceAsync();
        await WaitUntil(() => slow.WasCancelled, 2000);

        // 慢候选本会跑 5 秒；赢了以后必须收到取消，否则每个慢候选都会白占连接到超时。
        Assert.True(slow.WasCancelled, "败者应收到取消信号");
    }

    [Fact]
    public async Task 权重在赢家上加一()
    {
        var fast = new FakeCandidate("快", 20);
        var slow = new FakeCandidate("慢", 200);
        using var racer = Build(2, fast, slow);

        await racer.RaceAsync();

        Assert.Equal(1, racer.Weights.Single(w => ReferenceEquals(w.Client, fast)).Weight);
        Assert.Equal(0, racer.Weights.Single(w => ReferenceEquals(w.Client, slow)).Weight);
    }

    [Fact]
    public async Task 权重高的候选下一轮优先入选()
    {
        // 三个候选但每轮只发两个：权重决定谁被选中。
        var a = new FakeCandidate("A", 10);
        var b = new FakeCandidate("B", 10);
        var c = new FakeCandidate("C", 10);
        using var racer = Build(2, a, b, c);

        await racer.RaceAsync();   // A 或 B 先拿 1 分
        await racer.RaceAsync();

        var winners = racer.Weights.Where(w => w.Weight > 0).Select(w => w.Client.ToString()).ToArray();
        Assert.NotEmpty(winners);
        Assert.True(winners.Length <= 2);
    }

    [Fact]
    public async Task 高权重候选一定排在低权重候选前面()
    {
        // 每轮只发两个、共三个候选：权重决定谁入选。
        // 用「A 先赢一次把权重拉起来」再验证 C 被挤出，而不是只断言"有赢家"，
        // 否则"完全不看权重"的实现也能通过。
        var a = new FakeCandidate("A", 5);
        var b = new FakeCandidate("B", 5000);
        var c = new FakeCandidate("C", 5000);
        using var racer = Build(2, a, b, c);

        Assert.Equal(2, racer.SelectCandidates().Count);
        await racer.RaceAsync();   // A 权重 1

        Assert.Equal(1, racer.Weights.Single(w => ReferenceEquals(w.Client, a)).Weight);
        var selected = racer.SelectCandidates();
        Assert.Contains(a, selected);
        // B 与 C 同为 0 分，只有其中一个会被选中
        Assert.Equal(1, selected.Count(s => ReferenceEquals(s, b) || ReferenceEquals(s, c)));

        await racer.RaceAsync();
        await racer.RaceAsync();
        // A 连赢三轮权重 3，B/C 仍是 0 —— 若不按权重排序，B 或 C 早晚会赢一次
        Assert.True(racer.Weights.Single(w => ReferenceEquals(w.Client, a)).Weight >= 2);
        Assert.Equal(0, racer.Weights.Single(w => ReferenceEquals(w.Client, b)).Weight);
        Assert.Equal(0, racer.Weights.Single(w => ReferenceEquals(w.Client, c)).Weight);
    }

    [Fact]
    public async Task 败者权重会被扣下来()
    {
        // 补上「失败不扣分」注入留下的盲区：需要一个"先赢后输"的候选。
        var flip = new FakeCandidate("翻转", 5);
        using var racer = Build(1, flip);
        await racer.RaceAsync();                       // 赢 → 1 分
        Assert.Equal(1, racer.Weights[0].Weight);

        flip.Fail = true;                              // 变成坏节点
        // 同样套看门狗：若"全军覆没"时竞速器等在一个永不完成的候选上，
        // 这里应当看到超时而不是整个测试运行挂起。
        var losing = racer.RaceAsync();
        var settled = await Task.WhenAny(losing, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(ReferenceEquals(settled, losing), "唯一候选失败后竞速器应立刻收手，而不是继续等");
        await Assert.ThrowsAsync<InvalidOperationException>(() => losing);
        Assert.Equal(0, racer.Weights[0].Weight);       // 输 → 扣回 0
    }

    [Fact]
    public async Task 只发一个名额时选中的正是权重最高的候选()
    {
        // 权重刻意分散，且把 0 分的两个排在数组最前 ——
        // 不看权重的实现会选到它们，从而被这条抓住。
        var a = new FakeCandidate("A", 5);
        var b = new FakeCandidate("B", 5);
        var star = new FakeCandidate("明星", 5);
        using var racer = Build(1, a, b, star);

        Assert.Single(racer.SelectCandidates());

        await racer.RaceAsync();               // 三者同分，赢的那个升到 1
        var top = racer.Weights.Max(w => w.Weight);
        Assert.True(top > 0, "赢一轮后应有人拿到最高分");

        var selected = racer.SelectCandidates();
        Assert.Single(selected);
        Assert.Contains(selected[0], racer.Weights.Where(w => w.Weight == top).Select(w => w.Client));
    }

    [Fact]
    public async Task 候选完全无视取消信号时外部取消依然能及时收手()
    {
        // 有的候选实现（比如原生下载器回调）不理会 CancellationToken。
        // 若竞速器只等 WhenAny 而不查 token，就会被这种候选拖死。
        var stubborn = new FakeCandidate("不听话", int.MaxValue / 2, ignoreCancellation: true);
        using var cts = new CancellationTokenSource();
        using var racer = Build(1, stubborn);

        // 给被测调用套一层看门狗：若竞速器真的被无视取消的候选拖死，
        // 这里会得到超时异常而不是让整个测试运行挂住（挂住会让 CI 变成"无结论"）。
        var task = racer.RaceAsync(cts.Token);
        await Task.Delay(50);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await cts.CancelAsync();

        // 看门狗给 10 秒：这里的语义是「取消必须被尊重」，不是「取消必须多快」。
        // 3 秒在并行跑整套测试时会被机器负载拖爆（曾经真的随机红过一次）。
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        sw.Stop();
        Assert.True(ReferenceEquals(finished, task),
            $"收手耗时 {sw.ElapsedMilliseconds}ms：竞速器被无视取消的候选拖死了");
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public void 候选数不足时构造就抛()
        => Assert.Throws<ArgumentException>(() => Build(2, new FakeCandidate("独苗", 10)));

    [Fact]
    public void 并发数为零时构造就抛()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Build(0, new FakeCandidate("x", 1)));

    [Fact]
    public void 候选数为零时构造就抛()
        => Assert.Throws<ArgumentException>(() => Build(1));

    [Fact]
    public async Task 全部候选失败时抛明确异常()
    {
        using var racer = Build(2, new FakeCandidate("坏1", 10, fail: true), new FakeCandidate("坏2", 10, fail: true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => racer.RaceAsync());
        Assert.Contains("全部候选均失败", ex.Message);
    }

    [Fact]
    public async Task 部分失败时仍能拿到成功者()
    {
        using var racer = Build(2, new FakeCandidate("坏", 10, fail: true), new FakeCandidate("好", 30));

        Assert.Equal("好", (await racer.RaceAsync()).Value);
    }

    [Fact]
    public async Task 外部取消会让本轮以失败告终()
    {
        using var cts = new CancellationTokenSource();
        var slow = new FakeCandidate("慢1", 3000);
        using var racer = Build(2, slow, new FakeCandidate("慢2", 3000));

        var task = racer.RaceAsync(cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => task);
        await WaitUntil(() => slow.WasCancelled, 2000);
        Assert.True(slow.WasCancelled, "外部取消应传导到所有候选");
    }

    [Fact]
    public async Task 失败候选会被扣权重()
    {
        var ok = new FakeCandidate("好", 20);
        var bad = new FakeCandidate("坏", 10, fail: true);
        using var racer = Build(2, ok, bad);

        await racer.RaceAsync();   // 好赢 +1
        await racer.RaceAsync();   // 坏失败 -1（已到 0 不再降），好再赢 +1

        Assert.Equal(0, racer.Weights.Single(w => ReferenceEquals(w.Client, bad)).Weight);
        Assert.True(racer.Weights.Single(w => ReferenceEquals(w.Client, ok)).Weight >= 1);
    }

    [Fact]
    public async Task 权重封顶后停止更新()
    {
        using var racer = Build(1, new FakeCandidate("热", 5));

        for (var i = 0; i < 260; i++) await racer.RaceAsync();

        // 封顶是刻意设计：让长期领先的候选稳定占优，别被偶发快一次打断。
        Assert.Equal(byte.MaxValue, racer.Weights[0].Weight);
    }

    [Fact]
    public async Task 释放后拒绝再跑()
    {
        var racer = Build(1, new FakeCandidate("x", 5));
        racer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => racer.RaceAsync());
    }

    [Fact]
    public async Task 竞速总耗时贴近最快者而不是最慢者()
    {
        var sw = Stopwatch.StartNew();
        using var racer = Build(3, new FakeCandidate("慢", 3000), new FakeCandidate("中", 1000), new FakeCandidate("快", 50));

        await racer.RaceAsync();
        sw.Stop();

        // 若没有取消输家，这里会 ≥3000ms。
        // 上限给 2000ms：要卡住的是「不能拖到 3000ms」，而不是精确计时；
        // 1000ms 在并行负载下会随机红。
        Assert.True(sw.ElapsedMilliseconds < 2000, $"耗时 {sw.ElapsedMilliseconds}ms，应贴近最快者 50ms");
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(10);
    }
}
