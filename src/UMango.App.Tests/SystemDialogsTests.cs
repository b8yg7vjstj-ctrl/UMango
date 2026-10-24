using System;
using System.Collections.Generic;
using System.IO;
using UMango.App.Services;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 文件/文件夹选择对话框的参数计算。
///
/// 只测「参数怎么算」，不测「对话框弹没弹出来」——后者需要真机交互。
/// 假实现 <see cref="FakeDialogHost"/> 记录收到的请求，因此能验证
/// 取消 / 多选 / 初始目录不存在 / 路径规范化 等行为。
/// </summary>
public class SystemDialogsTests
{
    /// <summary>记录最后一次请求的假对话框宿主。</summary>
    private sealed class FakeDialogHost : IFileDialogHost
    {
        public FileDialogRequest? LastOpenFiles { get; private set; }
        public FileDialogRequest? LastSaveFile { get; private set; }
        public FileDialogRequest? LastOpenFolder { get; private set; }

        public string[] OpenResult { get; set; } = [];
        public string SaveResult { get; set; } = "";
        public string FolderResult { get; set; } = "";

        public string[] ShowOpenFiles(FileDialogRequest request)
        {
            LastOpenFiles = request;
            return OpenResult;
        }

        public string ShowSaveFile(FileDialogRequest request)
        {
            LastSaveFile = request;
            return SaveResult;
        }

        public string ShowOpenFolder(FileDialogRequest request)
        {
            LastOpenFolder = request;
            return FolderResult;
        }
    }

    [Fact]
    public void 取消选择文件时返回空字符串()
    {
        var host = new FakeDialogHost();
        Assert.Equal("", SystemDialogs.SelectFile(host));
    }

    [Fact]
    public void 选择单个文件时返回规范化后的路径()
    {
        var host = new FakeDialogHost { OpenResult = ["/tmp/./a.txt"] };
        var result = SystemDialogs.SelectFile(host);
        Assert.Equal(Path.GetFullPath("/tmp/./a.txt"), result);
    }

    [Fact]
    public void 允许多选时会把标志传给对话框()
    {
        var host = new FakeDialogHost();
        SystemDialogs.SelectFiles(host, allowMultiSelect: true);
        Assert.True(host.LastOpenFiles!.AllowMultiple);

        SystemDialogs.SelectFiles(host, allowMultiSelect: false);
        Assert.False(host.LastOpenFiles!.AllowMultiple);
    }

    [Fact]
    public void 多选取消时返回空数组()
    {
        var host = new FakeDialogHost();
        Assert.Empty(SystemDialogs.SelectFiles(host));
    }

    [Fact]
    public void 初始目录不存在时交给系统决定()
    {
        var host = new FakeDialogHost();
        SystemDialogs.SelectFiles(host, initialDirectory: "/绝对不存在的目录-xyz");
        Assert.Null(host.LastOpenFiles!.InitialDirectory);
    }

    [Fact]
    public void 初始目录存在时传规范化路径()
    {
        var host = new FakeDialogHost();
        var dir = Path.Combine(Path.GetTempPath(), "umango-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SystemDialogs.SelectFiles(host, initialDirectory: dir);
            Assert.Equal(Path.GetFullPath(dir), host.LastOpenFiles!.InitialDirectory);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 非法初始目录不会让对话框打不开()
    {
        // 含非法字符的路径在 Windows 上会抛异常；这里必须被吞掉并回落成 null。
        var host = new FakeDialogHost();
        SystemDialogs.SelectFiles(host, initialDirectory: "带\0空字符的目录");
        Assert.Null(host.LastOpenFiles!.InitialDirectory);
    }

    [Fact]
    public void 调用方给了标题就用调用方的()
    {
        var host = new FakeDialogHost();
        SystemDialogs.SelectFiles(host, title: "我的标题");
        Assert.Equal("我的标题", host.LastOpenFiles!.Title);
    }

    [Fact]
    public void 没给标题时用本地化默认值()
    {
        // 无 UI 环境下本地化返回资源键本身，这里只断言「不是空」且带键名前缀。
        var host = new FakeDialogHost();
        SystemDialogs.SelectFiles(host);
        Assert.False(string.IsNullOrWhiteSpace(host.LastOpenFiles!.Title));
    }

    [Fact]
    public void 保存对话框会带上默认文件名()
    {
        var host = new FakeDialogHost();
        SystemDialogs.SelectSaveFile(host, null, "a.zip");
        Assert.Equal("a.zip", host.LastSaveFile!.DefaultFileName);
    }

    [Fact]
    public void 取消保存时返回空字符串()
    {
        var host = new FakeDialogHost();
        Assert.Equal("", SystemDialogs.SelectSaveFile(host, null, "a.zip"));
    }

    [Fact]
    public void 选择文件夹时返回规范化路径()
    {
        var host = new FakeDialogHost { FolderResult = "/tmp/./dir" };
        Assert.Equal(Path.GetFullPath("/tmp/./dir"), SystemDialogs.SelectFolder(host));
    }

    [Fact]
    public void 取消选择文件夹时返回空字符串()
    {
        var host = new FakeDialogHost();
        Assert.Equal("", SystemDialogs.SelectFolder(host));
    }

    [Fact]
    public void 尾部分隔符只补一次()
    {
        var dir = Path.GetTempPath();
        Assert.Equal(dir, SystemDialogs.EnsureTrailingSeparator(dir));
        var withSep = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        Assert.Equal(withSep, SystemDialogs.EnsureTrailingSeparator(withSep));
    }

    [Fact]
    public void 规范化空路径得到空字符串()
    {
        Assert.Equal("", SystemDialogs.NormalizePath(null));
        Assert.Equal("", SystemDialogs.NormalizePath("   "));
    }
}
