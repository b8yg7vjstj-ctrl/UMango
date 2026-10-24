using System;
using System.Collections.Generic;
using System.Net;
using UMango.Core.Network;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 代理管理器的行为。
///
/// 这些用例验证「模式选择」与「地址解析」——
/// 平台相关的系统代理读取被抽成 <see cref="ISystemProxyProbe"/>，因此可以完全离线测试。
/// </summary>
public class ProxyManagerTests
{
    /// <summary>可编程的系统代理读取器桩。</summary>
    private sealed class FakeProbe : ISystemProxyProbe
    {
        public bool Enabled { get; set; }
        public string? ProxyString { get; set; }
        public bool Available { get; set; } = true;

        public bool TryRead(out bool enabled, out string? proxyString)
        {
            enabled = Enabled;
            proxyString = ProxyString;
            return Available;
        }
    }

    [Fact]
    public void 默认跟随系统代理()
    {
        var manager = new ProxyManager();
        Assert.Equal(ProxyMode.SystemProxy, manager.Mode);
    }

    [Fact]
    public void 关闭代理时直连()
    {
        var manager = new ProxyManager { Mode = ProxyMode.NoProxy };
        Assert.Null(manager.GetProxy(new Uri("https://example.com/")));
        Assert.Null(manager.GetProxyUrl());
        Assert.True(manager.IsBypassed(new Uri("https://example.com/")));
    }

    [Fact]
    public void 自定义代理模式使用自定义地址()
    {
        var manager = new ProxyManager { Mode = ProxyMode.CustomProxy };
        manager.CustomProxyAddress = new Uri("http://127.0.0.1:7890");
        Assert.Equal("http://127.0.0.1:7890/", manager.GetProxyUrl());
    }

    [Fact]
    public void 系统代理启用且有地址时会被采用()
    {
        var probe = new FakeProbe { Enabled = true, ProxyString = "127.0.0.1:8080" };
        var manager = new ProxyManager(probe);
        Assert.True(manager.SystemProxyAvailable);
        Assert.Equal("http://127.0.0.1:8080/", manager.GetProxyUrl());
    }

    [Fact]
    public void 系统代理未启用时不被采用()
    {
        var probe = new FakeProbe { Enabled = false, ProxyString = "127.0.0.1:8080" };
        var manager = new ProxyManager(probe);
        Assert.False(manager.SystemProxyAvailable);
        Assert.Null(manager.GetProxyUrl());
    }

    [Fact]
    public void 系统代理里只有socks时不被采用()
    {
        // .NET 的 WebProxy 不支持 SOCKS，选它只会让请求直接失败。
        var probe = new FakeProbe { Enabled = true, ProxyString = "socks=127.0.0.1:1080" };
        var manager = new ProxyManager(probe);
        Assert.False(manager.SystemProxyAvailable);
    }

    [Fact]
    public void 读取系统代理失败时退回到不可用()
    {
        var probe = new FakeProbe { Available = false };
        var manager = new ProxyManager(probe);
        Assert.False(manager.SystemProxyAvailable);
        Assert.Null(manager.GetProxyUrl());
    }

    [Fact]
    public void 换掉读取器后立即刷新()
    {
        var manager = new ProxyManager();
        Assert.False(manager.SystemProxyAvailable);

        manager.SetProbe(new FakeProbe { Enabled = true, ProxyString = "10.0.0.1:3128" });
        Assert.True(manager.SystemProxyAvailable);
    }

    [Fact]
    public void 绕过本地地址默认开启()
    {
        var manager = new ProxyManager { Mode = ProxyMode.CustomProxy };
        manager.CustomProxyAddress = new Uri("http://127.0.0.1:7890");
        Assert.True(manager.BypassOnLocal);
    }

    [Fact]
    public void 只有自定义代理模式返回凭据()
    {
        var manager = new ProxyManager { Mode = ProxyMode.CustomProxy };
        manager.CustomProxyCredentials = new NetworkCredential("u", "p");
        Assert.NotNull(manager.Credentials);

        manager.Mode = ProxyMode.SystemProxy;
        Assert.Null(manager.Credentials);
    }
}

/// <summary>代理地址解析。</summary>
public class ProxyAddressParserTests
{
    [Fact]
    public void 空输入返回空列表()
    {
        Assert.Empty(ProxyAddressParser.Parse(null));
        Assert.Empty(ProxyAddressParser.Parse(""));
        Assert.Empty(ProxyAddressParser.Parse("   "));
    }

    [Fact]
    public void 解析带scheme的单地址()
    {
        var item = Assert.Single(ProxyAddressParser.Parse("http://127.0.0.1:1145"));
        Assert.Equal(ProxyProtocol.Http, item.Protocol);
        Assert.Equal("127.0.0.1:1145", item.Address);
    }

    [Fact]
    public void 解析纯host端口()
    {
        var item = Assert.Single(ProxyAddressParser.Parse("192.168.1.10:8080"));
        Assert.Equal(ProxyProtocol.Http, item.Protocol);
        Assert.Equal("192.168.1.10:8080", item.Address);
    }

    [Fact]
    public void 解析键值形式的多项()
    {
        var items = ProxyAddressParser.Parse("http=192.168.1.100:8080;socks=192.168.1.100:1080");
        Assert.Equal(2, items.Count);
        Assert.Equal(ProxyProtocol.Http, items[0].Protocol);
        Assert.Equal(ProxyProtocol.Socks, items[1].Protocol);
    }

    [Fact]
    public void 非法段被忽略()
    {
        var items = ProxyAddressParser.Parse("garbage;=value;http=127.0.0.1:1;");
        var item = Assert.Single(items);
        Assert.Equal("127.0.0.1:1", item.Address);
    }

    [Fact]
    public void socks5协议名被识别()
    {
        var item = Assert.Single(ProxyAddressParser.Parse("socks5=127.0.0.1:1080"));
        Assert.Equal(ProxyProtocol.Socks, item.Protocol);
    }

    [Fact]
    public void 未知协议名按http处理()
    {
        var item = Assert.Single(ProxyAddressParser.Parse("ftp=127.0.0.1:2121"));
        Assert.Equal(ProxyProtocol.Http, item.Protocol);
    }

    [Fact]
    public void 只挑选http代理()
    {
        var items = ProxyAddressParser.Parse("socks=127.0.0.1:1080;http=127.0.0.1:8080");
        var selected = ProxyAddressParser.SelectHttpProxy(items);
        Assert.NotNull(selected);
        Assert.Equal("127.0.0.1:8080", selected!.Value.Address);
    }

    [Fact]
    public void 没有http代理时挑选返回空()
    {
        var items = ProxyAddressParser.Parse("socks=127.0.0.1:1080");
        Assert.Null(ProxyAddressParser.SelectHttpProxy(items));
    }
}
