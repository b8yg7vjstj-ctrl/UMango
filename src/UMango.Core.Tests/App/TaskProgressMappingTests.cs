using System;
using UMango.Core.App.Tasks;
using Xunit;

namespace UMango.Core.Tests.App;

/// <summary>
/// 「汇总 → 进度条状态」映射的测试。
///
/// 映射放在 Core 而不是 App：这是"有任务在跑该显示什么"的业务判定，与平台无关；
/// 且 WPF 的 <c>TaskbarItemProgressState</c> 在非 Windows 上加载即崩，
/// 放 App.Tests 会让整个测试宿主起不来（踩过一次：MsgBox 测试 FileNotFoundException）。
/// </summary>
public class TaskProgressMappingTests
{
    private static TaskProgressSummary Summary(bool hasActive, double? fraction, int active = 1, int total = 1)
        => new(hasActive, fraction, active, total);

    [Fact]
    public void 没有活跃任务时不显示进度条()
    {
        var (state, value) = TaskProgressAggregator.MapToProgress(Summary(false, null));
        Assert.Equal(TaskbarProgressState.None, state);
        Assert.Equal(0.0, value);
    }

    [Fact]
    public void 进度确定时用Normal态并带上取值()
    {
        var (state, value) = TaskProgressAggregator.MapToProgress(Summary(true, 0.42));
        Assert.Equal(TaskbarProgressState.Normal, state);
        Assert.Equal(0.42, value, precision: 3);
    }

    [Fact]
    public void 进度不确定时用Indeterminate态()
    {
        var (state, _) = TaskProgressAggregator.MapToProgress(Summary(true, null));
        Assert.Equal(TaskbarProgressState.Indeterminate, state);
    }

    [Fact]
    public void 进度取值为1时是完成前一格()
    {
        var (state, value) = TaskProgressAggregator.MapToProgress(Summary(true, 1.0));
        Assert.Equal(TaskbarProgressState.Normal, state);
        Assert.Equal(1.0, value);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.7)]
    public void 越界进度被夹到0到1(double raw)
    {
        var (state, value) = TaskProgressAggregator.MapToProgress(Summary(true, raw));
        Assert.Equal(TaskbarProgressState.Normal, state);
        Assert.InRange(value, 0.0, 1.0);
    }

    [Fact]
    public void 有活跃但进度为0时仍显示Normal态而非None()
    {
        // 0% 也要显示进度条：用户需要看到"任务已开始、正在下载"
        var (state, value) = TaskProgressAggregator.MapToProgress(Summary(true, 0.0));
        Assert.Equal(TaskbarProgressState.Normal, state);
        Assert.Equal(0.0, value);
    }

    [Fact]
    public void 活跃数为0时不显示进度()
    {
        var (state, _) = TaskProgressAggregator.MapToProgress(Summary(false, null, active: 0, total: 5));
        Assert.Equal(TaskbarProgressState.None, state);
    }
}
