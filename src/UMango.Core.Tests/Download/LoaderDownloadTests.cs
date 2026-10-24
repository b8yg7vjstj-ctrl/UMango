// SPDX-License-Identifier: Apache-2.0
//
// 下载加载器测试（对应 PCL-CE `Plain Craft Launcher 2/Modules/Network/Loaders/LoaderDownload.cs`
// 与 `LoaderDownloadUnc.cs`，两者都在 PCL 的**自定义许可目录** → UMango 只借鉴设计语义，
// 独立重写；本测试也独立编写，不复制 PCL 代码）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.App.Loaders;
using UMango.Core.Download;
using UMango.Core.Download.Loaders;
using Xunit;

namespace UMango.Core.Tests.Download;

/// <summary>
/// <see cref="LoaderDownload"/> / <see cref="LoaderUncDownload"/> 的行为测试。
/// </summary>
public class LoaderDownloadTests
{
    private sealed class FakeEngine(string id, bool available, params DownloadProtocol[] protocols) : IDownloadEngine
    {
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<DownloadProtocol> Protocols => protocols;
        public string? ExecutablePath => available ? "/fake/" + id : null;
        public bool IsAvailable => available;
        public string LicenseNote => $"{id}: 不链接也不分发";

        public List<DownloadRequest> Requests { get; } = [];

        public Func<DownloadRequest, DownloadResult>? Behavior { get; set; }

        /// <summary>异步行为（可挂起）。用于验证并发/顺序语义。</summary>
        public Func<DownloadRequest, Task<DownloadResult>>? AsyncBehavior { get; set; }

        public async Task<DownloadResult> DownloadAsync(DownloadRequest r, UMango.Core.Terminal.TerminalBridge? b,
            Action<DownloadProgress>? p, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(r);

            DownloadResult result;
            if (AsyncBehavior is not null)
            {
                result = await AsyncBehavior(r).ConfigureAwait(false);
            }
            else
            {
                // 同步路径：让 Task.Yield 之外的代码在返回前跑完。
                result = Behavior?.Invoke(r) ?? new DownloadResult(true, r.OutputFileName, null, null, 5);
            }

            p?.Invoke(new DownloadProgress(0.5, 50, 100, "1KiB", "1s", "gid1"));

            if (result.Success)
            {
                // 真实引擎会把文件落到目标目录，测试假引擎也得照做，
                // 否则"下载完成"之后本地文件不存在，大小与进度都无从谈起。
                var path = Path.Combine(r.OutputDirectory, r.OutputFileName ?? "out.bin");
                Directory.CreateDirectory(r.OutputDirectory);
                File.WriteAllText(path, "0123456789");
            }

            return result;
        }
    }

    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-loaderdl-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DownloadFileEntry Entry(string url, string target, string? sha = null)
        => new(new DownloadFile(url, target, sha));

    // ───────────────────────── 基础流程 ─────────────────────────

    [Fact]
    public async Task 单个文件下载成功后加载器成功且进度为1()
    {
        var dir = TempDir("one");
        try
        {
            var engine = new FakeEngine("fake", true, DownloadProtocol.Https);
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]))
            {
                DelayProvider = (_, _) => TimeSpan.Zero,
            };
            var target = Path.Combine(dir, "a.bin");
            var loader = new LoaderDownload("测试", [Entry("https://x/a.bin", target)], facade);

            loader.Start();
            await loader.WaitForExitAsync();

            Assert.Equal(LoaderState.Succeeded, loader.State);
            Assert.Equal(1d, loader.AggregatedProgress);
            Assert.Equal(0, loader.FailCount);
            Assert.Equal(DownloadFileState.Finished, loader.Files[0].State);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 目标已存在且非空时跳过下载()
    {
        var dir = TempDir("skip");
        try
        {
            var target = Path.Combine(dir, "exists.bin");
            await File.WriteAllTextAsync(target, "hello");

            var engine = new FakeEngine("fake", true, DownloadProtocol.Https);
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]));
            var loader = new LoaderDownload("测试", [Entry("https://x/e.bin", target)], facade);

            loader.Start();
            await loader.WaitForExitAsync();

            Assert.Equal(LoaderState.Succeeded, loader.State);
            Assert.True(loader.Files[0].Skipped);
            Assert.Empty(engine.Requests);   // 根本没去请求
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 目标存在但为空文件时不跳过()
    {
        var dir = TempDir("empty");
        try
        {
            var target = Path.Combine(dir, "zero.bin");
            await File.WriteAllTextAsync(target, "");

            var engine = new FakeEngine("fake", true, DownloadProtocol.Https);
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]));
            var loader = new LoaderDownload("测试", [Entry("https://x/z.bin", target)], facade);

            loader.Start();
            await loader.WaitForExitAsync();

            Assert.False(loader.Files[0].Skipped);
            Assert.NotEmpty(engine.Requests);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 空文件列表直接成功()
    {
        var engine = new FakeEngine("fake", true, DownloadProtocol.Https);
        var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]));
        var loader = new LoaderDownload("空", [], facade);

        loader.Start();
        await loader.WaitForExitAsync();

        Assert.Equal(LoaderState.Succeeded, loader.State);
        Assert.Equal(1d, loader.AggregatedProgress);
    }

    // ───────────────────────── 失败处理 ─────────────────────────

    [Fact]
    public async Task 单个文件失败时加载器失败且失败计数为1()
    {
        var dir = TempDir("fail");
        try
        {
            var engine = new FakeEngine("fake", true, DownloadProtocol.Https)
            {
                Behavior = _ => new DownloadResult(false, null, "http_500", "服务器错误", 3),
            };
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]))
            {
                MaxTotalAttempts = 1,
                DelayProvider = (_, _) => TimeSpan.Zero,
            };
            var loader = new LoaderDownload("测试", [Entry("https://x/f.bin", Path.Combine(dir, "f.bin"))], facade);

            loader.Start();
            // 失败时 WaitForExitAsync 会把 Error 抛出来 —— 这正是我们要验证的。
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.WaitForExitAsync());

            Assert.Equal(LoaderState.Failed, loader.State);
            Assert.Equal(1, loader.FailCount);
            Assert.Equal(DownloadFileState.Interrupted, loader.Files[0].State);
            Assert.False(string.IsNullOrEmpty(loader.Files[0].ErrorMessage));
            Assert.NotNull(loader.Error);
            Assert.Contains("1 个文件下载失败", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 默认不因单个文件失败放弃整批()
    {
        var dir = TempDir("nofailfast");
        try
        {
            var engine = new FakeEngine("fake", true, DownloadProtocol.Https)
            {
                Behavior = r => r.OutputFileName == "bad.bin"
                    ? new DownloadResult(false, null, "http_500", "服务器错误", 3)
                    : new DownloadResult(true, r.OutputFileName, null, null, 3),
            };
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]))
            {
                MaxTotalAttempts = 1,
                DelayProvider = (_, _) => TimeSpan.Zero,
            };
            var loader = new LoaderDownload("测试",
            [
                Entry("https://x/bad.bin", Path.Combine(dir, "bad.bin")),
                Entry("https://x/good.bin", Path.Combine(dir, "good.bin")),
            ], facade);

            loader.Start();
            await Assert.ThrowsAsync<InvalidOperationException>(() => loader.WaitForExitAsync());

            Assert.Equal(LoaderState.Failed, loader.State);
            Assert.Equal(1, loader.FailCount);
            // 另一个文件仍然下载了
            Assert.Equal(2, engine.Requests.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task FailFast开启时一个文件失败即放弃整批()
    {
        var dir = TempDir("failfast");
        try
        {
            var engine = new FakeEngine("fake", true, DownloadProtocol.Https)
            {
                Behavior = _ => new DownloadResult(false, null, "http_500", "服务器错误", 3),
            };
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]))
            {
                MaxTotalAttempts = 1,
                DelayProvider = (_, _) => TimeSpan.Zero,
            };
            var loader = new LoaderDownload("测试",
            [
                Entry("https://x/a.bin", Path.Combine(dir, "a.bin")),
                Entry("https://x/b.bin", Path.Combine(dir, "b.bin")),
                Entry("https://x/c.bin", Path.Combine(dir, "c.bin")),
            ], facade, failFast: true);

            loader.Start();
            await Assert.ThrowsAsync<InvalidOperationException>(() => loader.WaitForExitAsync());

            Assert.Equal(LoaderState.Failed, loader.State);
            // FailFast 的关键语义：**后面的文件根本没去下载**（只发了一次请求）。
            Assert.Single(engine.Requests);
            Assert.All(loader.Files.Skip(1),
                f => Assert.Equal(DownloadFileState.Interrupted, f.State));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 引擎抛异常时该文件算失败且整批失败()
    {
        var dir = TempDir("throw");
        try
        {
            var engine = new FakeEngine("fake", true, DownloadProtocol.Https)
            {
                AsyncBehavior = _ => throw new InvalidOperationException("引擎炸了"),
            };
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]))
            {
                MaxTotalAttempts = 1,
                DelayProvider = (_, _) => TimeSpan.Zero,
            };
            var loader = new LoaderDownload("测试", [Entry("https://x/a.bin", Path.Combine(dir, "a.bin"))], facade);

            loader.Start();
            await Assert.ThrowsAsync<InvalidOperationException>(() => loader.WaitForExitAsync());

            Assert.Equal(LoaderState.Failed, loader.State);
            Assert.Equal(1, loader.FailCount);
            Assert.Equal(DownloadFileState.Interrupted, loader.Files[0].State);
            Assert.NotNull(loader.Error);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task FailFast在真异步引擎下也不并发开跑后续文件()
    {
        var dir = TempDir("failfast-async");
        try
        {
            var engine = new FakeEngine("fake", true, DownloadProtocol.Https)
            {
                AsyncBehavior = async r =>
                {
                    if (r.OutputFileName == "a.bin")
                    {
                        // 第一个文件失败前先挂起，让后续文件有机会"抢先开跑"——
                        // 如果实现是并行的，后续文件会真的发出请求。
                        await Task.Delay(30).ConfigureAwait(false);
                        return new DownloadResult(false, null, "http_500", "服务器错误", 1);
                    }

                    return new DownloadResult(true, r.OutputFileName, null, null, 1);
                },
            };
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]))
            {
                MaxTotalAttempts = 1,
                DelayProvider = (_, _) => TimeSpan.Zero,
            };
            var loader = new LoaderDownload("测试",
            [
                Entry("https://x/a.bin", Path.Combine(dir, "a.bin")),
                Entry("https://x/b.bin", Path.Combine(dir, "b.bin")),
                Entry("https://x/c.bin", Path.Combine(dir, "c.bin")),
            ], facade, failFast: true);

            loader.Start();
            await Assert.ThrowsAsync<InvalidOperationException>(() => loader.WaitForExitAsync());

            // FailFast 是**顺序**语义：第一个失败后，后面的根本没被请求。
            Assert.Single(engine.Requests);
            Assert.Equal("a.bin", engine.Requests[0].OutputFileName);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ───────────────────────── 中止 ─────────────────────────

    [Fact]
    public void 等待态也能中止()
    {
        var engine = new FakeEngine("fake", true, DownloadProtocol.Https);
        var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]));
        var loader = new LoaderDownload("测试", [], facade);

        loader.Abort();

        Assert.Equal(LoaderState.Aborted, loader.State);
    }

    [Fact]
    public async Task 运行中中止后状态为已中止()
    {
        var dir = TempDir("abort");
        try
        {
            var engine = new FakeEngine("fake", true, DownloadProtocol.Https)
            {
                Behavior = _ => new DownloadResult(false, null, "canceled", "已取消", 1),
            };
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]))
            {
                MaxTotalAttempts = 1,
                DelayProvider = (_, _) => TimeSpan.Zero,
            };
            var loader = new LoaderDownload("测试", [Entry("https://x/a.bin", Path.Combine(dir, "a.bin"))], facade);
            loader.Start();
            loader.Abort();
            await Task.Delay(50);

            Assert.Equal(LoaderState.Aborted, loader.State);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ───────────────────────── 进度汇总 ─────────────────────────

    [Fact]
    public async Task 算术平均进度按文件个数汇总()
    {
        var dir = TempDir("avg");
        try
        {
            var engine = new FakeEngine("fake", true, DownloadProtocol.Https);
            var facade = new DownloadTaskFacade(new DownloadEngineRegistry([engine]))
            {
                DelayProvider = (_, _) => TimeSpan.Zero,
            };
            var loader = new LoaderDownload("测试",
            [
                Entry("https://x/a.bin", Path.Combine(dir, "a.bin")),
                Entry("https://x/b.bin", Path.Combine(dir, "b.bin")),
            ], facade);

            loader.Start();
            await loader.WaitForExitAsync();

            // 两个文件都完成 → 平均 1
            Assert.Equal(1d, loader.AggregatedProgress);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 条目的进度在总量未知时为null()
    {
        var entry = Entry("https://x/a.bin", Path.Combine(TempDir("p"), "a.bin"));
        Assert.Null(entry.Progress);
    }

    // ───────────────────────── UNC 加载器 ─────────────────────────

    [Fact]
    public async Task Unc加载器复制文件成功()
    {
        var dir = TempDir("unc");
        try
        {
            var src = Path.Combine(dir, "src.txt");
            await File.WriteAllTextAsync(src, "content");
            var dst = Path.Combine(dir, "sub", "dst.txt");

            var loader = new LoaderUncDownload("unc", src, dst);
            loader.Start();
            await loader.WaitForExitAsync();

            Assert.Equal(LoaderState.Succeeded, loader.State);
            Assert.True(File.Exists(dst));
            Assert.Equal("content", await File.ReadAllTextAsync(dst));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Unc加载器源不存在时失败()
    {
        var dir = TempDir("unc-fail");
        try
        {
            var loader = new LoaderUncDownload("unc", Path.Combine(dir, "nope.txt"), Path.Combine(dir, "out.txt"));
            loader.Start();
            await Assert.ThrowsAsync<IOException>(() => loader.WaitForExitAsync());

            Assert.Equal(LoaderState.Failed, loader.State);
            Assert.NotNull(loader.Error);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Unc加载器等待态可中止()
    {
        var loader = new LoaderUncDownload("unc", "/a", "/b");
        loader.Abort();
        Assert.Equal(LoaderState.Aborted, loader.State);
    }
}
