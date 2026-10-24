using UMango.App.Services;
using UMango.Core.Configuration;
using UMango.Core.Network;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 配置观察者（SetupObserver）的 App 层测试。
///
/// <para>观察者本身是纯逻辑（`src/UMango.App/Services/SetupObserver.cs`），
/// 它依赖的只有两个可替换的口子：<see cref="IWindowAppearanceTarget"/>（窗口外观）
/// 与 <see cref="ProxyManager"/>（代理）。所以这里能用假实现把
/// 「设置变了 → 观察者把效果应用出去」这条链路整条测掉，
/// 不需要真的开一个 WPF 窗口。</para>
///
/// <para>注意：<see cref="ProxyManager"/> 读系统代理是**运行时平台相关**的，
/// 但本文件只测「直连 / 自定义」两条不碰系统的路径。</para>
/// </summary>
[Collection("静态状态")]
public class SetupObserverTests
{
    /// <summary>记录窗口外观调用的假目标。</summary>
    private sealed class FakeWindow : IWindowAppearanceTarget
    {
        public double? Opacity { get; private set; }
        public bool? SizeLocked { get; private set; }
        public string? Font { get; private set; } = "<未设置>";
        public string? TitleBarText { get; private set; } = "<未设置>";
        public bool? TitleBarLeft { get; private set; }

        /// <summary>标题栏文字被应用的次数（用来观测"重复订阅"）。</summary>
        public int TitleBarTextApplyCount { get; private set; }

        public void ApplyWindowOpacity(double opacity) => Opacity = opacity;
        public void ApplyWindowSizeLock(bool locked) => SizeLocked = locked;
        public void ApplyUiFont(string? fontFamily) => Font = fontFamily;

        public void ApplyTitleBarText(string? text)
        {
            TitleBarTextApplyCount++;
            TitleBarText = text;
        }

        public void ApplyTitleBarAlignment(bool leftAligned) => TitleBarLeft = leftAligned;
    }

    /// <summary>在应用不透明度时抛异常的假窗口目标（模拟窗口已失效）。</summary>
    private sealed class ThrowingWindow : IWindowAppearanceTarget
    {
        public bool? TitleBarLeft { get; private set; }

        public void ApplyWindowOpacity(double opacity) =>
            throw new InvalidOperationException("窗口已关闭");

        public void ApplyWindowSizeLock(bool locked) { }
        public void ApplyUiFont(string? fontFamily) { }
        public void ApplyTitleBarText(string? text) { }
        public void ApplyTitleBarAlignment(bool leftAligned) => TitleBarLeft = leftAligned;
    }

    /// <summary>建一个带全部设置项的设置服务（内存存储，不碰磁盘）。</summary>
    private static SettingsService NewSettings()
    {
        var service = new SettingsService(new MemorySettingsStore());
        UiSettings.RegisterAll(service);
        return service;
    }

    [Fact]
    public void 启动时把所有登记项应用一遍()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        var observer = new SetupObserver(settings, window);

        var applied = observer.ApplyAll();

        // 登记了 5 个窗口项 + 3 个下载项 + 4 个代理项 = 12
        Assert.Equal(12, applied);
        Assert.Equal(12, observer.Keys.Count);
    }

    [Fact]
    public void 窗口不透明度按千分比换算()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        var observer = new SetupObserver(settings, window);

        settings.Set(UiSettings.WindowOpacityKey, 500);
        observer.Apply(UiSettings.WindowOpacityKey);

        Assert.Equal(0.5, window.Opacity);
    }

    [Fact]
    public void 窗口不透明度低于两成被夹住_否则窗口点不中()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        var observer = new SetupObserver(settings, window);

        settings.Set(UiSettings.WindowOpacityKey, 10);
        observer.Apply(UiSettings.WindowOpacityKey);

        Assert.Equal(0.2, window.Opacity);
    }

    [Fact]
    public void 字体留空时传空_表示恢复主题默认()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        var observer = new SetupObserver(settings, window);

        settings.Set(UiSettings.FontKey, "");
        observer.Apply(UiSettings.FontKey);

        Assert.Null(window.Font);
    }

    [Fact]
    public void 标题栏左对齐会被传给窗口()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        var observer = new SetupObserver(settings, window);

        settings.Set(UiSettings.TitleBarLeftAlignKey, true);
        observer.Apply(UiSettings.TitleBarLeftAlignKey);

        Assert.True(window.TitleBarLeft);
    }

    [Fact]
    public void 设置变了观察者会自动重应用_不用手动调Apply()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        using var observer = new SetupObserver(settings, window);
        observer.Subscribe();

        settings.Set(UiSettings.TitleBarTextKey, "我的UMango");
        // 设置服务的 Changed 事件是同步触发的
        Assert.Equal("我的UMango", window.TitleBarText);
    }

    [Fact]
    public void 没被登记的键不触发任何动作()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        using var observer = new SetupObserver(settings, window);
        observer.Subscribe();

        settings.Set(UiSettings.FontKey, "Consolas");
        Assert.Equal("Consolas", window.Font);

        // 一个没登记的键：不应把 Font 改回去
        settings.Set(UiSettings.ProxyModeKey, "custom");
        Assert.Equal("Consolas", window.Font);
    }

    [Fact]
    public void 重复订阅不会导致重复应用()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        using var observer = new SetupObserver(settings, window);

        observer.Subscribe();
        observer.Subscribe();

        // 重复订阅时同一个值会被应用两次 —— 用"应用次数"观测，
        // 因为"最终值"在重复应用下长得一样（看不出差别）。
        settings.Set(UiSettings.TitleBarTextKey, "X");
        Assert.Equal(1, window.TitleBarTextApplyCount);
        Assert.Equal("X", window.TitleBarText);
    }

    [Fact]
    public void 某个键的应用动作抛异常不会中断其它键()
    {
        var settings = NewSettings();
        // 窗口目标在处理不透明度时抛异常：这是最贴近真实的失败源
        // （真实情况是窗口已被关闭、句柄失效等）。
        var window = new ThrowingWindow();
        var errors = new List<string>();
        var observer = new SetupObserver(settings, window, onError: errors.Add);

        var applied = observer.ApplyAll();

        // 抛异常的那一项算失败，其余照常应用
        Assert.Equal(11, applied);
        Assert.Single(errors);
        Assert.Contains("ui.appearance.windowOpacity", errors[0]);
        // 抛异常之后，后面的键仍然应用了
        Assert.True(window.TitleBarLeft is not null);
    }

    [Fact]
    public void 代理模式字符串解析正确()
    {
        Assert.Equal(ProxyMode.NoProxy, SetupObserver.ParseProxyMode(null));
        Assert.Equal(ProxyMode.NoProxy, SetupObserver.ParseProxyMode(""));
        Assert.Equal(ProxyMode.NoProxy, SetupObserver.ParseProxyMode("乱写"));
        Assert.Equal(ProxyMode.SystemProxy, SetupObserver.ParseProxyMode("system"));
        Assert.Equal(ProxyMode.SystemProxy, SetupObserver.ParseProxyMode("  System "));
        Assert.Equal(ProxyMode.CustomProxy, SetupObserver.ParseProxyMode("custom"));
    }

    [Fact]
    public void 代理模式变化会落到代理管理器上()
    {
        var settings = NewSettings();
        var proxy = new ProxyManager();
        using var observer = new SetupObserver(settings, window: null, proxyManager: proxy);

        settings.Set(UiSettings.ProxyModeKey, "custom");
        observer.Apply(UiSettings.ProxyModeKey);

        Assert.Equal(ProxyMode.CustomProxy, proxy.Mode);
    }

    [Fact]
    public void 代理地址为空时清掉自定义地址()
    {
        var settings = NewSettings();
        var proxy = new ProxyManager { CustomProxyAddress = new Uri("http://127.0.0.1:8080") };
        using var observer = new SetupObserver(settings, window: null, proxyManager: proxy);

        settings.Set(UiSettings.ProxyAddressKey, "");
        observer.Apply(UiSettings.ProxyAddressKey);

        Assert.Null(proxy.CustomProxyAddress);
    }

    [Fact]
    public void 用户名变化会重建凭据()
    {
        var settings = NewSettings();
        var proxy = new ProxyManager();
        using var observer = new SetupObserver(settings, window: null, proxyManager: proxy);

        settings.Set(UiSettings.ProxyUsernameKey, "bob");
        settings.Set(UiSettings.ProxyPasswordKey, "secret");
        observer.Apply(UiSettings.ProxyUsernameKey);

        Assert.NotNull(proxy.CustomProxyCredentials);
        Assert.Equal("bob", ((System.Net.NetworkCredential)proxy.CustomProxyCredentials!).UserName);
    }

    [Fact]
    public void 用户名为空则不需要凭据()
    {
        var settings = NewSettings();
        var proxy = new ProxyManager();
        using var observer = new SetupObserver(settings, window: null, proxyManager: proxy);

        settings.Set(UiSettings.ProxyUsernameKey, "");
        observer.Apply(UiSettings.ProxyUsernameKey);

        Assert.Null(proxy.CustomProxyCredentials);
    }

    [Fact]
    public void 窗口目标为null时也不抛异常()
    {
        var settings = NewSettings();
        using var observer = new SetupObserver(settings, window: null);

        settings.Set(UiSettings.WindowOpacityKey, 800);
        var ok = observer.Apply(UiSettings.WindowOpacityKey);

        Assert.True(ok);
    }

    [Fact]
    public void 释放后不再响应设置变更()
    {
        var settings = NewSettings();
        var window = new FakeWindow();
        var observer = new SetupObserver(settings, window);
        observer.Subscribe();
        observer.Dispose();

        settings.Set(UiSettings.TitleBarTextKey, "释放后");
        Assert.Equal("<未设置>", window.TitleBarText);
    }
}
