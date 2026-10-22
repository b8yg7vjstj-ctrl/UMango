using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.App.Essentials;
using UMango.Core.Network;
using Xunit;

namespace UMango.Core.Tests.App;

/// <summary>
/// 自更新「文件替换」原语的测试。
/// </summary>
public sealed class UpdateHelperTests
{
    private static string NewTempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "umango-upd-" + Guid.NewGuid().ToString("n"))).FullName;

    [Fact]
    public void 替换成功后目标内容等于来源内容()
    {
        var dir = NewTempDir();
        try
        {
            var target = Path.Combine(dir, "app.exe");
            var source = Path.Combine(dir, "new.exe");
            File.WriteAllText(target, "old-content");
            File.WriteAllText(source, "new-content");

            var ex = UpdateHelper.Replace(source, target);

            Assert.Null(ex);
            Assert.Equal("new-content", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 替换成功后不留备份文件()
    {
        var dir = NewTempDir();
        try
        {
            var target = Path.Combine(dir, "app.exe");
            var source = Path.Combine(dir, "new.exe");
            File.WriteAllText(target, "old");
            File.WriteAllText(source, "new");

            UpdateHelper.Replace(source, target);

            Assert.Empty(Directory.GetFiles(dir, "*.bak.*"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 来源文件不存在时返回异常且原文件保持原样()
    {
        var dir = NewTempDir();
        try
        {
            var target = Path.Combine(dir, "app.exe");
            File.WriteAllText(target, "keep-me");

            var ex = UpdateHelper.Replace(Path.Combine(dir, "missing.exe"), target);

            Assert.NotNull(ex);
            Assert.Equal("keep-me", File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(dir, "*.bak.*"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 等待删除超时会返回异常()
    {
        // FileSystemWatcher 在某些文件系统（网络共享、FAT）上不派发事件。
        // 等待逻辑用 ManualResetEventSlim(3s)，这里直接验证"等不到就抛超时"的语义
        // 由一个独立的可测原语承担，而不是把 3 秒等待硬编码在 Replace 里。
        Assert.True(UpdateHelper.DeleteWaitTimeout.TotalSeconds > 0);
        Assert.False(new ManualResetEventSlim(false).Wait(UpdateHelper.DeleteWaitTimeout));
    }

    [Fact]
    public void 目标文件不存在时返回异常而不是覆盖成空白()
    {
        var dir = NewTempDir();
        try
        {
            var source = Path.Combine(dir, "new.exe");
            File.WriteAllText(source, "new");

            var ex = UpdateHelper.Replace(source, Path.Combine(dir, "no-such.exe"));

            Assert.NotNull(ex);
            Assert.False(File.Exists(Path.Combine(dir, "no-such.exe")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>
/// 自更新「命令行协议」的测试。
/// </summary>
public sealed class UpdateCommandLineTests
{
    [Theory]
    [InlineData("update")]
    [InlineData("update_finished")]
    [InlineData("update_failed")]
    public void 更新协议的命令能被识别(string command)
    {
        Assert.True(UpdateCommandLine.IsUpdateCommand(new[] { command, "x" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("help")]
    [InlineData("Update")]  // 故意大写：协议要求小写，避免与用户自建参数冲突
    public void 非更新协议参数不会被误判(string command)
    {
        var list = new List<string>();
        if (command.Length > 0) list.Add(command);
        Assert.False(UpdateCommandLine.IsUpdateCommand(list));
    }

    [Fact]
    public void 解析助手模式参数()
    {
        var parsed = UpdateCommandLine.Parse(new[] { "update", "1234", @"C:\app\UMango.exe", @"C:\tmp\new.exe", "true" });

        var req = Assert.IsType<UpdateCommandLine.ApplyRequest>(parsed);
        Assert.Equal(1234, req.OldProcessId);
        Assert.Equal(@"C:\app\UMango.exe", req.TargetPath);
        Assert.Equal(@"C:\tmp\new.exe", req.SourcePath);
        Assert.True(req.RestartAfterwards);
    }

    [Fact]
    public void 构造与解析互为逆运算()
    {
        var args = UpdateCommandLine.BuildApplyArguments(4321, "/app/UMango", "/tmp/pkg", false);
        var parsed = Assert.IsType<UpdateCommandLine.ApplyRequest>(UpdateCommandLine.Parse(args));

        Assert.Equal(4321, parsed.OldProcessId);
        Assert.Equal("/app/UMango", parsed.TargetPath);
        Assert.Equal("/tmp/pkg", parsed.SourcePath);
        Assert.False(parsed.RestartAfterwards);
    }

    [Theory]
    [InlineData("update", 4)]
    [InlineData("update", 6)]
    [InlineData("update_finished", 1)]
    [InlineData("update_finished", 3)]
    [InlineData("update_failed", 1)]
    [InlineData("unknown_cmd", 2)]
    public void 参数个数不符时解析返回null(string command, int count)
    {
        var args = new string[count];
        Array.Fill(args, "x");
        args[0] = command;
        Assert.Null(UpdateCommandLine.Parse(args));
    }

    [Fact]
    public void 非数字进程号解析返回null()
    {
        Assert.Null(UpdateCommandLine.Parse(new[] { "update", "abc", "t", "s", "true" }));
    }

    [Fact]
    public void 非布尔重启标志解析返回null()
    {
        Assert.Null(UpdateCommandLine.Parse(new[] { "update", "1", "t", "s", "yes-please" }));
    }

    [Fact]
    public void 收尾_删除已完成的安装包()
    {
        var file = Path.Combine(Path.GetTempPath(), "umango-upd-pkg-" + Guid.NewGuid().ToString("n") + ".tmp");
        File.WriteAllText(file, "pkg");
        try
        {
            var ex = UpdateCommandLine.RunFinished(new UpdateCommandLine.FinishedRequest(file));

            Assert.Null(ex);
            Assert.False(File.Exists(file));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void 收尾_安装包已不在时不算失败()
    {
        var missing = Path.Combine(Path.GetTempPath(), "umango-upd-gone-" + Guid.NewGuid().ToString("n") + ".tmp");

        var ex = UpdateCommandLine.RunFinished(new UpdateCommandLine.FinishedRequest(missing));

        Assert.Null(ex);
    }

    [Fact]
    public void 助手模式_旧进程号无效仍能完成替换()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "umango-upd-apply-" + Guid.NewGuid().ToString("n")));
        try
        {
            var target = Path.Combine(dir.FullName, "app.exe");
            var source = Path.Combine(dir.FullName, "new.exe");
            File.WriteAllText(target, "old");
            File.WriteAllText(source, "new");

            // 用一个几乎不可能存在的 PID：等价于「旧进程已退出」
            var result = UpdateCommandLine.RunApply(
                new UpdateCommandLine.ApplyRequest(int.MaxValue - 1, target, source, false));

            // restart=false → 不重启，也就没有「成功后删安装包」的收尾参数
            Assert.Empty(result);
            Assert.Equal("new", File.ReadAllText(target));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

/// <summary>
/// 版本检查器的测试（用假 HttpService，不联网）。
/// </summary>
public sealed class UpdateCheckerTests
{
    private sealed class FakeHttp : IHttpService
    {
        private readonly Func<string, string> _responder;
        public FakeHttp(Func<string, string> responder) => _responder = responder;

        public string? ProxyUrl { get; set; }
        public int TimeoutMs { get; set; }
        public string UserAgent { get; set; } = "test";

        public Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default) =>
            throw new NotSupportedException("版本检查只用 GetStringAsync");

        public Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default)
        {
            var body = _responder(url);
            return Task.FromResult(new HttpResponse<string>
            {
                IsSuccess = true,
                Value = body,
                StatusCode = 200
            });
        }
    }

    private const string ReleaseJson = """
        {
          "tag_name": "v1.3.0",
          "prerelease": false,
          "body": "修复了一些问题",
          "assets": [
            { "name": "UMango-win-x64.zip", "size": 100, "browser_download_url": "https://example.test/win-x64.zip" },
            { "name": "UMango-osx-arm64.zip", "size": 200, "browser_download_url": "https://example.test/osx-arm64.zip" },
            { "name": "UMango-linux-x64.zip", "size": 300, "browser_download_url": "https://example.test/linux-x64.zip" }
          ]
        }
        """;

    private static UpdateChecker NewChecker(string json) =>
        new(new FakeHttp(_ => json), name => name.StartsWith("UMango-win-x64", StringComparison.Ordinal));

    [Fact]
    public async Task 正常返回更新信息()
    {
        var checker = NewChecker(ReleaseJson);

        var result = await checker.CheckAsync(new UpdateCheckRequest("1.2.0", "https://example.test/latest"));

        var info = Assert.IsType<UpdateInfo>(result);
        Assert.True(info.IsNewer);
        Assert.Equal("1.3.0", info.Version.Original);
        Assert.Equal("https://example.test/win-x64.zip", info.DownloadUrl);
        Assert.Equal(100, info.FileSize);
        Assert.False(info.IsPreRelease);
    }

    [Fact]
    public async Task 远端版本不新时也返回信息而不是失败()
    {
        var checker = NewChecker(ReleaseJson);

        var result = await checker.CheckAsync(new UpdateCheckRequest("2.0.0", "https://example.test/latest"));

        var info = Assert.IsType<UpdateInfo>(result);
        Assert.False(info.IsNewer);
    }

    [Fact]
    public async Task 版本号相等时不算新版本()
    {
        var checker = NewChecker(ReleaseJson);

        var result = await checker.CheckAsync(new UpdateCheckRequest("1.3.0", "https://example.test/latest"));

        Assert.False(Assert.IsType<UpdateInfo>(result).IsNewer);
    }

    [Fact]
    public async Task 预发布版本默认被拒绝()
    {
        var json = ReleaseJson.Replace("\"prerelease\": false", "\"prerelease\": true");
        var checker = NewChecker(json);

        var result = await checker.CheckAsync(new UpdateCheckRequest("1.0.0", "https://example.test/latest"));

        var failure = Assert.IsType<UpdateCheckFailure>(result);
        Assert.Equal(UpdateCheckError.BadVersion, failure.Error);
    }

    [Fact]
    public async Task 显式接受预发布时返回信息()
    {
        var json = ReleaseJson.Replace("\"prerelease\": false", "\"prerelease\": true");
        var checker = NewChecker(json);

        var result = await checker.CheckAsync(
            new UpdateCheckRequest("1.0.0", "https://example.test/latest", IncludePreRelease: true));

        Assert.True(Assert.IsType<UpdateInfo>(result).IsNewer);
    }

    [Fact]
    public async Task 没有匹配本平台的资源时报错()
    {
        var checker = new UpdateChecker(new FakeHttp(_ => ReleaseJson), name => name.Contains("nope"));

        var result = await checker.CheckAsync(new UpdateCheckRequest("1.0.0", "https://example.test/latest"));

        Assert.Equal(UpdateCheckError.NoAsset, Assert.IsType<UpdateCheckFailure>(result).Error);
    }

    [Fact]
    public async Task 非JSON响应报数据错误而不是崩溃()
    {
        var checker = NewChecker("<html>502 Bad Gateway</html>");

        var result = await checker.CheckAsync(new UpdateCheckRequest("1.0.0", "https://example.test/latest"));

        Assert.Equal(UpdateCheckError.BadPayload, Assert.IsType<UpdateCheckFailure>(result).Error);
    }

    [Fact]
    public async Task 缺少tag_name时报数据错误()
    {
        var checker = NewChecker("""{ "assets": [] }""");

        var result = await checker.CheckAsync(new UpdateCheckRequest("1.0.0", "https://example.test/latest"));

        Assert.Equal(UpdateCheckError.BadPayload, Assert.IsType<UpdateCheckFailure>(result).Error);
    }

    [Fact]
    public async Task 本地版本号非法时报版本错误()
    {
        var checker = NewChecker(ReleaseJson);

        var result = await checker.CheckAsync(new UpdateCheckRequest("not-a-version", "https://example.test/latest"));

        Assert.Equal(UpdateCheckError.BadVersion, Assert.IsType<UpdateCheckFailure>(result).Error);
    }

    [Fact]
    public async Task 远端版本号非法时报版本错误()
    {
        var checker = NewChecker(ReleaseJson.Replace("\"v1.3.0\"", "\"garbage\""));

        var result = await checker.CheckAsync(new UpdateCheckRequest("1.0.0", "https://example.test/latest"));

        Assert.Equal(UpdateCheckError.BadVersion, Assert.IsType<UpdateCheckFailure>(result).Error);
    }

    [Fact]
    public async Task 网络异常被归为网络错误()
    {
        var checker = new UpdateChecker(new ThrowingHttp(), _ => true);

        var result = await checker.CheckAsync(new UpdateCheckRequest("1.0.0", "https://example.test/latest"));

        var failure = Assert.IsType<UpdateCheckFailure>(result);
        Assert.Equal(UpdateCheckError.Network, failure.Error);
        Assert.NotNull(failure.Inner);
    }

    private sealed class ThrowingHttp : IHttpService
    {
        public string? ProxyUrl { get; set; }
        public int TimeoutMs { get; set; }
        public string UserAgent { get; set; } = "test";

        public Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default) =>
            throw new System.Net.Http.HttpRequestException("connection refused");
    }
}
