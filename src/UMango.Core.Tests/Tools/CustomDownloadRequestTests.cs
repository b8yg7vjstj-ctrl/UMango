// SPDX-License-Identifier: Apache-2.0
using System;
using UMango.Core.Tools;
using Xunit;

namespace UMango.Core.Tests.Tools;

public sealed class CustomDownloadRequestTests
{
    [Fact]
    public void 合法请求_通过()
    {
        var result = CustomDownloadRequest.Validate(
            "https://example.com/pkg/file.zip", "file.zip", "/tmp/out");

        Assert.True(result.IsValid);
        Assert.Equal("https://example.com/pkg/file.zip", result.Url);
        Assert.Equal("file.zip", result.FileName);
        Assert.Null(result.ErrorKey);
    }

    [Fact]
    public void 空URL_失败_键为Empty()
    {
        var result = CustomDownloadRequest.Validate("   ", "a.zip", "/tmp");
        Assert.False(result.IsValid);
        Assert.Equal("Validation.Empty", result.ErrorKey);
    }

    [Fact]
    public void null_URL_失败()
    {
        var result = CustomDownloadRequest.Validate(null, "a.zip", "/tmp");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void 非法URL_失败_键为InvalidUrl()
    {
        var result = CustomDownloadRequest.Validate("not a url", "a.zip", "/tmp");
        Assert.False(result.IsValid);
        Assert.Equal("Validation.InvalidUrl", result.ErrorKey);
    }

    [Fact]
    public void 非允许协议_失败()
    {
        var result = CustomDownloadRequest.Validate("javascript:alert(1)", "a.zip", "/tmp");
        Assert.False(result.IsValid);
        Assert.Equal("Validation.InvalidUrl", result.ErrorKey);
    }

    [Theory]
    [InlineData("http://example.com/a.zip", true)]
    [InlineData("https://example.com/a.zip", true)]
    [InlineData("ftp://example.com/a.zip", true)]
    [InlineData("file:///tmp/a.zip", true)]
    [InlineData("magnet:?xt=urn:btih:abc", false)]
    public void 协议白名单(string url, bool expected)
    {
        var result = CustomDownloadRequest.Validate(url, "a.zip", "/tmp");
        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void 文件名为空_从URL推断()
    {
        var result = CustomDownloadRequest.Validate("https://example.com/pkg/app-1.2.3.zip", "", "/tmp");
        Assert.True(result.IsValid);
        Assert.Equal("app-1.2.3.zip", result.FileName);
    }

    [Fact]
    public void URL无路径段_用主机名做文件名()
    {
        var result = CustomDownloadRequest.Validate("https://example.com", "", "/tmp");
        Assert.True(result.IsValid);
        Assert.Equal("example.com", result.FileName);
    }

    [Fact]
    public void 文件名含路径_只取最后一段()
    {
        var result = CustomDownloadRequest.Validate("https://example.com/a.zip", @"C:\temp\x\file.bin", "/tmp");
        Assert.True(result.IsValid);
        Assert.Equal("file.bin", result.FileName);
    }

    [Fact]
    public void 文件名含正斜杠_只取最后一段()
    {
        var result = CustomDownloadRequest.Validate("https://example.com/a.zip", "dir/file.bin", "/tmp");
        Assert.True(result.IsValid);
        Assert.Equal("file.bin", result.FileName);
    }

    [Fact]
    public void 文件名为点或双点_失败()
    {
        Assert.False(CustomDownloadRequest.Validate("https://e.com/a", ".", "/tmp").IsValid);
        Assert.False(CustomDownloadRequest.Validate("https://e.com/a", "..", "/tmp").IsValid);
    }

    [Fact]
    public void 文件名含非法字符_失败()
    {
        foreach (var bad in new[] { "a<b.zip", "a>b.zip", "a:b.zip", "a\"b.zip", "a|b.zip", "a?b.zip", "a*b.zip" })
            Assert.False(CustomDownloadRequest.Validate("https://e.com/a", bad, "/tmp").IsValid, bad);
    }

    [Fact]
    public void 文件名含控制字符_失败()
    {
        Assert.False(CustomDownloadRequest.Validate("https://e.com/a", "a\u0001b.zip", "/tmp").IsValid);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void Windows保留设备名_失败(string name)
    {
        Assert.False(CustomDownloadRequest.Validate("https://e.com/a", name, "/tmp").IsValid);
    }

    [Fact]
    public void 目录为空_用默认目录()
    {
        var result = CustomDownloadRequest.Validate("https://e.com/a.zip", "a.zip", "", "/tmp/default");
        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath("/tmp/default"), result.Directory);
    }

    [Fact]
    public void 目录为空且无默认_失败()
    {
        var result = CustomDownloadRequest.Validate("https://e.com/a.zip", "a.zip", "");
        Assert.False(result.IsValid);
        Assert.Equal("Validation.Empty", result.ErrorKey);
    }

    [Fact]
    public void 目录被规范化成绝对路径()
    {
        var result = CustomDownloadRequest.Validate("https://e.com/a.zip", "a.zip", "relative/dir");
        Assert.True(result.IsValid);
        Assert.True(Path.IsPathRooted(result.Directory!));
    }

    [Fact]
    public void 推断文件名_也走规范化()
    {
        var result = CustomDownloadRequest.Validate("https://e.com/", "", "/tmp");
        Assert.True(result.IsValid);
        Assert.Equal("e.com", result.FileName);
    }
}
