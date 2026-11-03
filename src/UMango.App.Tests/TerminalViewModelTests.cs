using UMango.App.ViewModels;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 终端页 ViewModel 的测试：命令 -> 流式输出 -> 过滤/清空。
///
/// 只跑 <c>echo</c> 这类无副作用命令，确保用例不改变机器状态。
/// </summary>
[Collection("静态状态")]
public class TerminalViewModelTests
{
    [Fact]
    public async Task 执行命令会把回显与标准输出写进终端()
    {
        var vm = new TerminalViewModel();
        vm.Command = "echo UMango-Terminal-Test";

        await vm.RunCommand.ExecuteAsync(null);

        // 命令本身的回显（"$ …"）必须留一行，用户才知道这行输出是谁产生的
        Assert.Contains(vm.Lines, line => line.Kind == TerminalEntryKind.Command
                                          && line.Text.Contains("UMango-Terminal-Test", StringComparison.Ordinal));

        // 标准输出
        Assert.Contains(vm.Lines, line => line.Kind == TerminalEntryKind.Output
                                          && line.Text.Contains("UMango-Terminal-Test", StringComparison.Ordinal));

        Assert.False(vm.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public async Task 过滤只影响可见行_不丢失全量输出()
    {
        var vm = new TerminalViewModel();
        vm.Command = "echo alpha; echo beta";
        await vm.RunCommand.ExecuteAsync(null);

        vm.Filter = "beta";

        Assert.All(vm.VisibleLines, line =>
            Assert.Contains("beta", line.Text, StringComparison.OrdinalIgnoreCase));
        Assert.True(vm.Lines.Count > vm.VisibleLines.Count, "过滤后应少于全量行");

        vm.Filter = "";
        Assert.Equal(vm.Lines.Count, vm.VisibleLines.Count);
    }

    [Fact]
    public async Task 过滤大小写不敏感()
    {
        var vm = new TerminalViewModel();
        vm.Command = "echo MixedCaseMarker";
        await vm.RunCommand.ExecuteAsync(null);

        vm.Filter = "mixedcasemarker";

        Assert.Contains(vm.VisibleLines, line => line.Text.Contains("MixedCaseMarker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 清空会同时清掉全量与可见行()
    {
        var vm = new TerminalViewModel();
        vm.Command = "echo clear-me";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Lines);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Lines);
        Assert.Empty(vm.VisibleLines);
        Assert.Null(vm.StatusMessage);
    }
}
