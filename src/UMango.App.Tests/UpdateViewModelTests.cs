using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UMango.App.ViewModels;
using UMango.Core.App.Essentials;
using UMango.Core.Network;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 自更新流程的测试。
///
/// **为什么在 App.Tests 而不是 Core.Tests**：被测对象是
/// <see cref="UpdateViewModel"/>，它引用 <c>AppServices.Update</c>（App 层）。
/// Core 层的 <c>UpdateChecker</c> 已在 <c>UMango.Core.Tests/App/UpdateTests.cs</c>
/// 用假 HttpService 验过纯逻辑，这里只测「状态机按预期推进」。
/// </summary>
[Collection("静态状态")]
public class UpdateViewModelTests
{
    /// <summary>
    /// 可复现的假 Feed：按 URL 里的版本号决定返回哪一版，
    /// 这样用例之间互不影响，也不依赖全局状态。
    /// </summary>
    private sealed class FakeHttp : IHttpService
    {
        private readonly string _json;

        public FakeHttp(string json) => _json = json;

        public string? ProxyUrl { get; set; }
        public int TimeoutMs { get; set; }
        public string UserAgent { get; set; } = "test";

        public Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(new HttpResponse<string> { IsSuccess = true, Value = _json, StatusCode = 200 });
    }

    private const string NewerFeed = """
        {
          "tag_name": "v99.0.0",
          "prerelease": false,
          "assets": [
            { "name": "UMango-win-x64.zip", "size": 100, "browser_download_url": "https://example.test/win.zip" },
            { "name": "UMango-osx-arm64.zip", "size": 200, "browser_download_url": "https://example.test/osx.zip" },
            { "name": "UMango-linux-x64.zip", "size": 300, "browser_download_url": "https://example.test/linux.zip" }
          ]
        }
        """;

    private const string LatestFeed = """
        {
          "tag_name": "v0.0.1",
          "prerelease": false,
          "assets": [
            { "name": "UMango-win-x64.zip", "size": 100, "browser_download_url": "https://example.test/win.zip" },
            { "name": "UMango-osx-arm64.zip", "size": 200, "browser_download_url": "https://example.test/osx.zip" },
            { "name": "UMango-linux-x64.zip", "size": 300, "browser_download_url": "https://example.test/linux.zip" }
          ]
        }
        """;

    /// <summary>
    /// 检查成功（发现新版）后必须：置 HasUpdate、给出含版本号的状态、解除忙碌态。
    /// </summary>
    [Fact]
    public async Task 发现新版后置位HasUpdate并显示版本号()
    {
        // Provider 必须在 new VM 之前装好：构造期不求值，但 CheckCommand 一跑就会
        // 把 L() 的结果写进 StatusMessage —— 装晚了读到的就是资源键。
        using (ChineseProvider.Scope())
        {
            var vm = new UpdateViewModel();
            Services.AppServices.Update = new Services.UpdateService(new FakeHttp(NewerFeed));

            await vm.CheckCommand.ExecuteAsync(null);

            Assert.True(vm.HasUpdate, vm.StatusMessage);
            Assert.False(vm.IsBusy);
            Assert.Contains("99.0.0", vm.StatusMessage);
        }
    }

    /// <summary>
    /// 已是最新时不置 HasUpdate —— 否则「立即更新」按钮会亮着，
    /// 点了却什么也不做（这正是本用例要防的回归）。
    /// </summary>
    [Fact]
    public async Task 已是最新时不置位HasUpdate()
    {
        var vm = new UpdateViewModel();
        Services.AppServices.Update = new Services.UpdateService(new FakeHttp(LatestFeed));

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.False(vm.HasUpdate);
        Assert.False(vm.IsBusy);
    }

    /// <summary>
    /// 检查失败（网络错误）时必须解除忙碌态并给出原因，
    /// 否则按钮会永久禁用，用户再也点不了「检查更新」。
    /// </summary>
    [Fact]
    public async Task 检查失败后解除忙碌态并显示原因()
    {
        using (ChineseProvider.Scope())
        {
            var vm = new UpdateViewModel();
            Services.AppServices.Update = new Services.UpdateService(new ThrowingHttp());

            await vm.CheckCommand.ExecuteAsync(null);

            Assert.False(vm.HasUpdate);
            Assert.False(vm.IsBusy);
            // 失败原因要透出（状态行是唯一的反馈渠道 —— 这里刻意不弹窗）
            Assert.Contains("offline", vm.StatusMessage);
        }
    }

    /// <summary>
    /// 没有发现新版本时点「立即更新」必须是无害空操作
    /// （不能崩、不能把状态行写成空）。
    /// </summary>
    [Fact]
    public async Task 无待装更新时应用命令是空操作()
    {
        var vm = new UpdateViewModel();   // _pending 为 null

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(vm.HasUpdate);
        Assert.False(vm.IsBusy);
    }

    /// <summary>
    /// 当前版本号来自程序集，构造后即可见（不能是空串 ——
    /// 设置页要显示「当前版本：x.y.z」）。
    /// </summary>
    [Fact]
    public void 当前版本号非空()
    {
        var vm = new UpdateViewModel();

        Assert.False(string.IsNullOrWhiteSpace(vm.CurrentVersionText));
    }

    private sealed class ThrowingHttp : IHttpService
    {
        public string? ProxyUrl { get; set; }
        public int TimeoutMs { get; set; }
        public string UserAgent { get; set; } = "test";

        public Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default) =>
            throw new System.Net.Http.HttpRequestException("offline");
    }
}
