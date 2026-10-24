using UMango.Core.Cli;
using Xunit;

namespace UMango.Core.Tests;

public class CommandLineArgsTests
{
    [Fact]
    public void 空参数()
    {
        var args = CommandLineArgs.Parse([]);
        Assert.Empty(args.Positional);
        Assert.Empty(args.Flags);
    }

    [Fact]
    public void 标志参数()
    {
        var args = CommandLineArgs.Parse(["--debug", "--verbose"]);
        Assert.True(args.HasFlag("debug"));
        Assert.True(args.HasFlag("verbose"));
        Assert.False(args.HasFlag("missing"));
    }

    [Fact]
    public void 键值对参数()
    {
        var args = CommandLineArgs.Parse(["--source=npm"]);
        Assert.True(args.TryGetString("source", out var val));
        Assert.Equal("npm", val);
    }

    [Fact]
    public void 等号语法()
    {
        var args = CommandLineArgs.Parse(["--source=npm"]);
        Assert.True(args.TryGetString("source", out var val));
        Assert.Equal("npm", val);
    }

    [Fact]
    public void 位置参数()
    {
        var args = CommandLineArgs.Parse(["install", "express"]);
        Assert.Equal(2, args.Positional.Count);
        Assert.Equal("install", args.Positional[0]);
        Assert.Equal("express", args.Positional[1]);
    }

    [Fact]
    public void 混合参数()
    {
        var args = CommandLineArgs.Parse(["--debug", "install", "express", "--source=npm"]);
        Assert.True(args.HasFlag("debug"));
        Assert.Equal(2, args.Positional.Count);
        Assert.Equal("install", args.Positional[0]);
        Assert.Equal("express", args.Positional[1]);
        Assert.Equal("npm", args.TryGetString("source", out var v) ? v : "");
    }

    [Fact]
    public void 开关不会吞掉后面的位置参数()
    {
        // 旧版实现里 "--debug install" 是没问题的（它不认空格分隔的值），
        // 但如果照搬 PCL 的贪心规则，"--debug" 就会把 "install" 吃掉。
        // 纯参数模式因此**不贪心**：值一律用 --key=value 给。
        var args = CommandLineArgs.Parse(["--debug", "install", "express"]);
        Assert.True(args.HasFlag("debug"));
        Assert.Equal(["install", "express"], args.Positional);
    }

    [Fact]
    public void 显式为假的开关不算开启()
    {
        // 旧版无法表达这个：--cache false 里的 false 会被当成位置参数，
        // HasFlag("cache") 永远为真。
        var args = CommandLineArgs.Parse(["--cache=false"]);
        Assert.False(args.HasFlag("cache"));
    }

    [Fact]
    public void 键名区分大小写()
    {
        var args = CommandLineArgs.Parse(["--SOURCE=npm"]);
        Assert.False(args.TryGetString("source", out _));
        Assert.True(args.TryGetString("SOURCE", out var v));
        Assert.Equal("npm", v);
    }
}
