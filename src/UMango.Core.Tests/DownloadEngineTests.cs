using UMango.Core.Download;
using UMango.Core.Download.Engines;
using UMango.Core.Execution;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>下载层测试：协议识别、aria2 输出解析、引擎选择、许可约束。</summary>
[Trait("Category", "真机")]
public class DownloadEngineTests
{
    // ---------- 协议识别 ----------

    [Theory]
    [InlineData("https://example.com/a.tgz", DownloadProtocol.Https)]
    [InlineData("http://example.com/a.tgz", DownloadProtocol.Http)]
    [InlineData("ftp://example.com/a.zip", DownloadProtocol.Ftp)]
    [InlineData("sftp://example.com/a.zip", DownloadProtocol.Sftp)]
    [InlineData("magnet:?xt=urn:btih:abcdef", DownloadProtocol.BitTorrent)]
    [InlineData("https://example.com/a.torrent", DownloadProtocol.BitTorrent)]
    [InlineData("https://example.com/a.meta4", DownloadProtocol.Metalink)]
    [InlineData("ed2k://|file|x.iso|123|HASH|/", DownloadProtocol.Ed2k)]
    [InlineData("随便什么", DownloadProtocol.Unknown)]
    public void 能从链接识别协议(string url, DownloadProtocol expected)
        => Assert.Equal(expected, DownloadProtocolExtensions.Detect(url));

    // ---------- aria2 输出解析（格式取自真机实测） ----------

    [Fact]
    public void 解析aria2的真实进度读数行()
    {
        // 这一行是真实抓到的输出
        var p = Aria2OutputParser.TryParseProgress("[#a8c198 1.0MiB/5.4MiB(18%) CN:1 DL:519KiB ETA:8s]");

        Assert.NotNull(p);
        Assert.Equal(0.18, p!.Fraction!.Value, 3);
        Assert.Equal("a8c198", p.Gid);
        Assert.Equal("519KiB", p.SpeedText);
        Assert.Equal("8s", p.EtaText);
        Assert.Equal(1024L * 1024, p.BytesDone);
        Assert.NotNull(p.BytesTotal);
    }

    [Fact]
    public void 解析不含ETA的读数行_下载接近完成时会这样()
    {
        var p = Aria2OutputParser.TryParseProgress("[#a8c198 5.0MiB/5.4MiB(92%) CN:1 DL:855KiB]");
        Assert.NotNull(p);
        Assert.Equal(0.92, p!.Fraction!.Value, 3);
        Assert.Null(p.EtaText);
    }

    [Theory]
    [InlineData("普通日志行", null)]
    [InlineData("", null)]
    [InlineData("Download Results:", null)]
    [InlineData("b2f0cf|OK  |   7.4MiB/s|/tmp/x.bin", null)]
    public void 非进度行不会被误判(string line, double? _)
        => Assert.Null(Aria2OutputParser.TryParseProgress(line));

    [Fact]
    public void 能剥掉ANSI颜色码()
    {
        var line = "\u001b[1;32mNOTICE\u001b[0m Downloading 1 item(s)";
        var stripped = Aria2OutputParser.StripAnsi(line);

        // 用 char 版断言而不是 DoesNotContain(string,string)：
        // 后者在这里的重载解析会给出误导性的失败信息（xunit 把不可见的 ESC 渲染成空串）。
        Assert.False(stripped.Contains('\u001b'), $"仍含 ESC：{string.Join(",", stripped.Select(c => (int)c))}");
        Assert.Contains("NOTICE", stripped);
        Assert.Equal("NOTICE Downloading 1 item(s)", stripped);
    }

    [Fact]
    public void 能识别完成行_中英文都要支持()
    {
        // aria2 的消息随 locale 变化，中文环境实测输出「下载完成：」
        Assert.True(Aria2OutputParser.IsCompletion("[NOTICE] Download complete: /tmp/a.bin", out var p1));
        Assert.Equal("/tmp/a.bin", p1);
        Assert.True(Aria2OutputParser.IsCompletion("[NOTICE] 下载完成：/tmp/b.bin", out var p2));
        Assert.Equal("/tmp/b.bin", p2);
        Assert.False(Aria2OutputParser.IsCompletion("nothing here", out _));
    }

    [Fact]
    public void 能识别错误行与汇总表数据行()
    {
        Assert.True(Aria2OutputParser.IsError("[ERROR] Something failed"));
        Assert.False(Aria2OutputParser.IsError("[NOTICE] fine"));

        var row = Aria2OutputParser.TryParseSummaryRow("b2f0cf|OK  |   7.4MiB/s|/tmp/aria_big.bin");
        Assert.NotNull(row);
        Assert.Equal("b2f0cf", row!.Value.Gid);
        Assert.Equal("OK", row.Value.Stat);
        Assert.Equal("/tmp/aria_big.bin", row.Value.Path);

        Assert.Null(Aria2OutputParser.TryParseSummaryRow("gid   |stat|avg speed  |path/URI"));
    }

    [Theory]
    [InlineData("1.0", "MiB", 1048576L)]
    [InlineData("1.0", "KiB", 1024L)]
    [InlineData("2.5", "GiB", 2684354560L)]
    [InlineData("512", "B", 512L)]
    public void 容量单位换算正确(string n, string unit, long expected)
        => Assert.Equal(expected, Aria2OutputParser.ToBytes(n, unit));

    // ---------- aria2 命令行拼装（含真机踩出来的参数） ----------

    [Fact]
    public void 种子链接必须禁止做种否则进程不退出()
    {
        var args = Aria2Engine.BuildArgs(new DownloadRequest("magnet:?xt=urn:btih:abc", "/tmp"));
        Assert.Contains("--seed-time=0", args);
        // 种子文件名由内容决定，不能传 -o
        Assert.DoesNotContain("-o", args);
    }

    [Fact]
    public void 普通链接带输出文件名与断点续传()
    {
        var args = Aria2Engine.BuildArgs(new DownloadRequest(
            "https://example.com/a.tgz", "/tmp", "a.tgz"));
        Assert.Contains("--continue=true", args);
        Assert.Contains("-o", args);
        Assert.Contains("a.tgz", args);
        Assert.Contains("--enable-color=false", args);
        Assert.Contains("--summary-interval=1", args);
    }

    [Fact]
    public void 校验值被传给aria2而不是自己算()
    {
        var args = Aria2Engine.BuildArgs(new DownloadRequest(
            "https://example.com/a.tgz", "/tmp", "a.tgz", ExpectedSha256: "deadbeef"));
        Assert.Contains("--checksum=sha-256=deadbeef", args);
    }

    [Fact]
    public void 限速参数只在指定时出现()
    {
        var without = Aria2Engine.BuildArgs(new DownloadRequest("https://e.com/a", "/tmp"));
        Assert.DoesNotContain(without, a => a.StartsWith("--max-download-limit"));

        var with = Aria2Engine.BuildArgs(new DownloadRequest("https://e.com/a", "/tmp", MaxBytesPerSecond: 1024));
        Assert.Contains("--max-download-limit=1024", with);
    }

    // ---------- 引擎选择与许可约束 ----------

    [Fact]
    public void 协议到引擎的选择符合优先级()
    {
        var registry = new DownloadEngineRegistry();
        var http = registry.Select("https://example.com/a.tgz");
        Assert.NotNull(http);
        // 本机装了 aria2c，因此 HTTP 应优先选 aria2；没装则退回内置引擎
        var aria = new Aria2Engine();
        Assert.Equal(aria.IsAvailable ? "aria2" : "builtin-http", http!.Id);
    }

    [Fact]
    public void eD2k链接只会选eD2k引擎而不会误用aria2()
    {
        var registry = new DownloadEngineRegistry();
        var selected = registry.Select("ed2k://|file|x.iso|123|HASH|/");
        // aria2 不支持 eD2k：要么选到 eD2k 引擎，要么没有可用引擎
        Assert.True(selected is null || selected.Id == "ed2k-client");
    }

    [Fact]
    public void 未安装eD2k客户端时如实报不可用并给出安装指引()
    {
        var engine = new Ed2kEngine(overrideCli: null);
        if (engine.IsAvailable) return;   // 装了客户端的机器跳过

        Assert.False(engine.IsAvailable);
        Assert.Contains("brew install amule", engine.LicenseNote);
        Assert.Contains("GPL-2.0", engine.LicenseNote);
    }

    [Fact]
    public void 所有引擎都必须声明许可说明且不链接第三方代码()
    {
        var registry = new DownloadEngineRegistry();
        foreach (var e in registry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.LicenseNote), $"{e.Id} 缺少许可说明");
        }
        // GPL 系引擎的说明里必须写明"不链接也不分发"
        var aria = registry.All.First(e => e.Id == "aria2");
        Assert.Contains("GPL-2.0", aria.LicenseNote);
        Assert.Contains("不链接", aria.LicenseNote);
    }

    [Fact]
    public void 内置引擎永远可用且无外部依赖()
    {
        var e = new BuiltinHttpEngine();
        Assert.True(e.IsAvailable);
        Assert.Null(e.ExecutablePath);
        Assert.Contains("HttpClient", e.LicenseNote);
    }

    // ---------- 真机端到端：真的用 aria2 下一个文件 ----------

    /// <summary>
    /// 真机测试：用 aria2 实际下载一个小文件，验证「拼参数 → 起进程 → 解析输出 → 拿到文件」全链路。
    /// aria2 未安装时跳过（并打印原因，不静默通过）。
    /// </summary>
    [Fact]
    public async Task 真机用aria2下载文件并解析出进度()
    {
        var aria = new Aria2Engine();
        if (!aria.IsAvailable)
        {
            Console.WriteLine("跳过：本机未安装 aria2c");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"umango-dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var progresses = new List<DownloadProgress>();
            var result = await aria.DownloadAsync(
                new DownloadRequest(
                    "https://registry.npmmirror.com/is-number/-/is-number-7.0.0.tgz",
                    dir, "is-number.tgz", MaxBytesPerSecond: 32 * 1024),
                bridge: null,
                onProgress: p => progresses.Add(p));

            Assert.True(result.Success, $"下载失败：{result.ErrorCode} {result.Message}");
            Assert.NotNull(result.OutputPath);
            Assert.True(File.Exists(result.OutputPath!), $"文件不存在：{result.OutputPath}");
            Assert.True(new FileInfo(result.OutputPath!).Length > 1000, "下载的文件太小，可疑");

            // 限速 32KB/s 下载 ~4KB 的小包可能来不及产生进度读数，
            // 所以这里不断言"一定有进度"，但一旦有就必须是合法值
            foreach (var p in progresses)
            {
                Assert.True(p.Fraction is null || (p.Fraction >= 0 && p.Fraction <= 1));
                Assert.True(p.BytesDone is null or >= 0);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败无所谓 */ }
        }
    }

    /// <summary>真机测试：内置引擎也能真的下载（不依赖任何外部程序）。</summary>
    [Fact]
    public async Task 真机用内置引擎下载文件并上报进度()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"umango-dl2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var engine = new BuiltinHttpEngine();
            var progresses = new List<DownloadProgress>();
            var result = await engine.DownloadAsync(
                new DownloadRequest("https://registry.npmmirror.com/is-number/-/is-number-7.0.0.tgz", dir, "x.tgz"),
                bridge: null,
                onProgress: p => progresses.Add(p));

            Assert.True(result.Success, $"下载失败：{result.ErrorCode} {result.Message}");
            Assert.True(File.Exists(result.OutputPath!));
            Assert.NotEmpty(progresses);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
