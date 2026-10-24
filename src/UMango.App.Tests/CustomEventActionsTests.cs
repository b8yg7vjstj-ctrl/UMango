using System;
using System.Collections.Generic;
using UMango.App.Services;
using UMango.Core.Customization;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 自定义事件动作的行为。
///
/// WPF 能力（剪贴板、进程启动、对话框）都在 <see cref="IShellCapabilities"/> 后面，
/// 所以这些用例能在 macOS 上跑。
/// </summary>
public class CustomEventActionsTests
{
    /// <summary>记录调用的假系统能力。</summary>
    private sealed class FakeShell : IShellCapabilities
    {
        public List<string> Opened { get; } = [];
        public List<string> Copied { get; } = [];
        public List<(string Title, string Content, string[] Buttons)> Messages { get; } = [];
        public List<(string Message, CustomHintType Type)> Hints { get; } = [];
        public int RefreshCount { get; private set; }
        public List<(string Url, string SavePath, string? FileName)> Downloads { get; } = [];
        public List<(string Key, string Value)> Settings { get; } = [];

        public void Open(string target) => Opened.Add(target);
        public void CopyToClipboard(string text) => Copied.Add(text);

        public int ShowMessage(string title, string content, string[] buttons)
        {
            Messages.Add((title, content, buttons));
            return 0;
        }

        public void ShowHint(string message, CustomHintType type) => Hints.Add((message, type));
        public void RefreshPage() => RefreshCount++;

        public void DownloadFile(string url, string savePath, string? fileName)
            => Downloads.Add((url, savePath, fileName));

        public void WriteSetting(string key, string value) => Settings.Add((key, value));
    }

    [Fact]
    public void 打开链接会把反斜杠换成正斜杠()
    {
        var shell = new FakeShell();
        new OpenUrlEventAction(shell).Execute(["https:\\example.com"], _ => true);
        Assert.Equal(["https:/example.com"], shell.Opened);
    }

    [Fact]
    public void 打开链接缺参数时报参数错误()
    {
        var result = new OpenUrlEventAction(new FakeShell()).Execute([], _ => true);
        Assert.Equal(CustomEventOutcome.InvalidArguments, result.Outcome);
    }

    [Fact]
    public void 打开文件前会请求确认()
    {
        var shell = new FakeShell();
        var confirmed = false;
        var result = new OpenFileEventAction(shell).Execute(["a.txt"], _ => { confirmed = true; return false; });
        Assert.True(confirmed);
        Assert.Equal(CustomEventOutcome.CancelledByUser, result.Outcome);
        Assert.Empty(shell.Opened);
    }

    [Fact]
    public void 确认后打开文件()
    {
        var shell = new FakeShell();
        new OpenFileEventAction(shell).Execute(["a.txt"], _ => true);
        Assert.Equal(["a.txt"], shell.Opened);
    }

    [Fact]
    public void 复制文本()
    {
        var shell = new FakeShell();
        new CopyTextEventAction(shell).Execute(["你好"], _ => true);
        Assert.Equal(["你好"], shell.Copied);
    }

    [Fact]
    public void 复制空文本也不会崩()
    {
        var shell = new FakeShell();
        new CopyTextEventAction(shell).Execute([], _ => true);
        Assert.Equal([""], shell.Copied);
    }

    [Fact]
    public void 刷新页面会调用刷新()
    {
        var shell = new FakeShell();
        new RefreshPageEventAction(shell).Execute([], _ => true);
        Assert.Equal(1, shell.RefreshCount);
    }

    [Fact]
    public void 弹出消息框会把换行转成真实换行()
    {
        var shell = new FakeShell();
        new ShowDialogEventAction(shell).Execute(["标题", "第一行\\n第二行"], _ => true);
        var message = Assert.Single(shell.Messages);
        Assert.Equal("标题", message.Title);
        Assert.Equal("第一行\n第二行", message.Content);
    }

    [Fact]
    public void 弹出消息框缺参数时报错()
    {
        var result = new ShowDialogEventAction(new FakeShell()).Execute(["只有标题"], _ => true);
        Assert.Equal(CustomEventOutcome.InvalidArguments, result.Outcome);
    }

    [Fact]
    public void 弹出消息框没给按钮文字时用确定()
    {
        var shell = new FakeShell();
        new ShowDialogEventAction(shell).Execute(["标题", "正文"], _ => true);
        Assert.Equal(["确定"], Assert.Single(shell.Messages).Buttons);
    }

    [Theory]
    [InlineData("success", CustomHintType.Success)]
    [InlineData("Finish", CustomHintType.Success)]
    [InlineData("error", CustomHintType.Error)]
    [InlineData("critical", CustomHintType.Error)]
    [InlineData("info", CustomHintType.Info)]
    [InlineData("不认识的", CustomHintType.Info)]
    public void 提示条类型能识别旧名字(string text, CustomHintType expected)
    {
        var shell = new FakeShell();
        new ShowHintEventAction(shell).Execute(["正文", text], _ => true);
        Assert.Equal(expected, Assert.Single(shell.Hints).Type);
    }

    [Fact]
    public void 提示条缺内容时报错()
    {
        var result = new ShowHintEventAction(new FakeShell()).Execute([], _ => true);
        Assert.Equal(CustomEventOutcome.InvalidArguments, result.Outcome);
    }

    [Fact]
    public void 下载文件前会请求确认()
    {
        var shell = new FakeShell();
        var result = new DownloadFileEventAction(shell).Execute(["https://x/y.zip"], _ => false);
        Assert.Equal(CustomEventOutcome.CancelledByUser, result.Outcome);
        Assert.Empty(shell.Downloads);
    }

    [Fact]
    public void 下载文件会带上路径与文件名()
    {
        var shell = new FakeShell();
        new DownloadFileEventAction(shell).Execute(["https://x/y.zip", "/tmp", "y.zip"], _ => true);
        var download = Assert.Single(shell.Downloads);
        Assert.Equal("https://x/y.zip", download.Url);
        Assert.Equal("/tmp", download.SavePath);
        Assert.Equal("y.zip", download.FileName);
    }

    [Fact]
    public void 写入设置缺参数时报错()
    {
        var result = new WriteSettingEventAction(new FakeShell()).Execute(["只有键"], _ => true);
        Assert.Equal(CustomEventOutcome.InvalidArguments, result.Outcome);
    }

    [Fact]
    public void 写入设置会带上键与值()
    {
        var shell = new FakeShell();
        new WriteSettingEventAction(shell).Execute(["ui.theme", "dark"], _ => true);
        Assert.Equal(("ui.theme", "dark"), Assert.Single(shell.Settings));
    }
}
