using System;
using System.Collections.Generic;
using UMango.Core.Customization;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 自定义事件服务的行为。
///
/// 这些用例不碰 WPF：事件的「类型 → 动作」映射、参数解析、安全策略
/// 都是平台中立的纯逻辑，因此可以在 macOS 上被完整覆盖。
/// </summary>
public class CustomEventServiceTests
{
    /// <summary>记录调用参数的动作桩。</summary>
    private sealed class RecordingAction : ICustomEventAction
    {
        public List<string> Received { get; } = [];
        public string? ConfirmMessage { get; private set; }
        public bool ConfirmResult { get; set; } = true;
        public Exception? Throw { get; set; }

        public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
        {
            Received.Add(string.Join("|", arguments));
            ConfirmMessage = "要执行吗？";
            if (!confirm(ConfirmMessage))
                return new CustomEventResult(CustomEventOutcome.CancelledByUser, "用户取消");
            if (Throw is not null)
                throw Throw;
            return new CustomEventResult(CustomEventOutcome.Success);
        }
    }

    [Fact]
    public void 未注册的类型会被拒绝()
    {
        var service = new CustomEventService();
        var result = service.Raise(CustomEventType.OpenUrl, "https://example.com");
        Assert.Equal(CustomEventOutcome.UnsupportedType, result.Outcome);
        Assert.False(result.Ok);
    }

    [Fact]
    public void None类型永远不被执行()
    {
        var service = new CustomEventService();
        // 注册 None 本身就会被拒绝（见下方用例），所以这里只能直接触发。
        Assert.Equal(CustomEventOutcome.UnsupportedType, service.Raise(CustomEventType.None, "x").Outcome);
    }

    [Fact]
    public void 注册后能执行并拿到参数()
    {
        var service = new CustomEventService();
        var action = new RecordingAction();
        service.Register(CustomEventType.ShowDialog, action);

        var result = service.Raise(CustomEventType.ShowDialog, "标题|正文|按钮");

        Assert.True(result.Ok);
        Assert.Equal(["标题|正文|按钮"], action.Received);
    }

    [Fact]
    public void 同一类型重复注册时后者覆盖前者()
    {
        var service = new CustomEventService();
        var first = new RecordingAction();
        var second = new RecordingAction();
        service.Register(CustomEventType.CopyText, first);
        service.Register(CustomEventType.CopyText, second);

        service.Raise(CustomEventType.CopyText, "x");

        Assert.Empty(first.Received);
        Assert.Single(second.Received);
    }

    [Fact]
    public void 移除动作后不再执行()
    {
        var service = new CustomEventService();
        service.Register(CustomEventType.CopyText, new RecordingAction());
        Assert.True(service.Unregister(CustomEventType.CopyText));
        Assert.False(service.Unregister(CustomEventType.CopyText));
        Assert.Equal(CustomEventOutcome.UnsupportedType, service.Raise(CustomEventType.CopyText, "x").Outcome);
    }

    [Fact]
    public void 参数按竖线分割并保留空片段()
    {
        Assert.Equal(["a", "", "b"], CustomEventService.SplitArguments("a||b"));
        Assert.Equal(["a"], CustomEventService.SplitArguments("a"));
    }

    [Fact]
    public void 空参数串返回空数组()
    {
        Assert.Empty(CustomEventService.SplitArguments(null));
        Assert.Empty(CustomEventService.SplitArguments(""));
    }

    [Fact]
    public void 用户拒绝确认时动作不执行()
    {
        var service = new CustomEventService();
        var action = new RecordingAction { ConfirmResult = false };
        service.Register(CustomEventType.OpenFile, action);

        var result = service.Raise(CustomEventType.OpenFile, "a.txt", _ => action.ConfirmResult);

        Assert.Equal(CustomEventOutcome.CancelledByUser, result.Outcome);
    }

    [Fact]
    public void 用户确认后动作执行()
    {
        var service = new CustomEventService();
        var action = new RecordingAction();
        service.Register(CustomEventType.OpenFile, action);

        var result = service.Raise(CustomEventType.OpenFile, "a.txt", _ => true);

        Assert.True(result.Ok);
    }

    [Fact]
    public void 没有提供确认回调时视为同意()
    {
        var service = new CustomEventService();
        var action = new RecordingAction();
        service.Register(CustomEventType.OpenFile, action);

        Assert.True(service.Raise(CustomEventType.OpenFile, "a.txt").Ok);
    }

    [Fact]
    public void 动作抛异常时结果里带原因()
    {
        var service = new CustomEventService();
        service.Register(CustomEventType.CopyText, new RecordingAction { Throw = new InvalidOperationException("炸了") });

        var result = service.Raise(CustomEventType.CopyText, "x");

        Assert.Equal(CustomEventOutcome.Failed, result.Outcome);
        Assert.Contains("炸了", result.Detail);
    }

    [Fact]
    public void 策略拒绝时动作根本不会被调用()
    {
        var service = new CustomEventService();
        var action = new RecordingAction();
        service.Register(CustomEventType.OpenUrl, action);
        service.AddPolicy(HttpUrlOnlyPolicy.Instance);

        var result = service.Raise(CustomEventType.OpenUrl, "file:///etc/passwd");

        Assert.Equal(CustomEventOutcome.RejectedByPolicy, result.Outcome);
        Assert.Empty(action.Received);
    }

    [Fact]
    public void 策略放行https链接()
    {
        var service = new CustomEventService();
        var action = new RecordingAction();
        service.Register(CustomEventType.OpenUrl, action);
        service.AddPolicy(HttpUrlOnlyPolicy.Instance);

        Assert.True(service.Raise(CustomEventType.OpenUrl, "https://example.com").Ok);
    }

    [Fact]
    public void 策略也管下载事件()
    {
        var service = new CustomEventService();
        service.Register(CustomEventType.DownloadFile, new RecordingAction());
        service.AddPolicy(HttpUrlOnlyPolicy.Instance);

        Assert.Equal(CustomEventOutcome.RejectedByPolicy,
            service.Raise(CustomEventType.DownloadFile, "javascript:alert(1)").Outcome);
    }

    [Fact]
    public void 策略不管复制文本()
    {
        var service = new CustomEventService();
        service.Register(CustomEventType.CopyText, new RecordingAction());
        service.AddPolicy(HttpUrlOnlyPolicy.Instance);

        Assert.True(service.Raise(CustomEventType.CopyText, "随便什么").Ok);
    }

    [Theory]
    [InlineData("OpenUrl", CustomEventType.OpenUrl)]
    [InlineData("openurl", CustomEventType.OpenUrl)]
    [InlineData("打开网页", CustomEventType.OpenUrl)]
    [InlineData("刷新主页", CustomEventType.RefreshPage)]
    [InlineData("刷新页面", CustomEventType.RefreshPage)]
    [InlineData("写入设置", CustomEventType.WriteSetting)]
    public void 能解析英文名与中文别名(string text, CustomEventType expected)
    {
        Assert.True(CustomEventService.TryParseType(text, out var type));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("None")]
    [InlineData("不存在的类型")]
    [InlineData("内存优化")]
    public void 无法解析的类型返回false(string text)
    {
        Assert.False(CustomEventService.TryParseType(text, out var type));
        Assert.Equal(CustomEventType.None, type);
    }

    [Fact]
    public void 显式注册None会被拒绝()
    {
        var service = new CustomEventService();
        Assert.Throws<ArgumentException>(() => service.Register(CustomEventType.None, new RecordingAction()));
    }

    [Fact]
    public void 注册空动作会被拒绝()
    {
        var service = new CustomEventService();
        Assert.Throws<ArgumentNullException>(() => service.Register(CustomEventType.OpenUrl, null!));
    }

    [Fact]
    public void 能列出已注册的类型()
    {
        var service = new CustomEventService();
        service.Register(CustomEventType.OpenUrl, new RecordingAction());
        service.Register(CustomEventType.CopyText, new RecordingAction());
        Assert.Equal(2, service.RegisteredTypes.Count);
        Assert.Contains(CustomEventType.OpenUrl, service.RegisteredTypes);
    }

    [Fact]
    public void 不支持类型名单保持与PCL一致()
    {
        Assert.Contains("内存优化", CustomEventService.UnsupportedTypeNames);
        Assert.Contains("检查更新", CustomEventService.UnsupportedTypeNames);
    }
}
