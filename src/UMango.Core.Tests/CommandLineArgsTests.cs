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
}
