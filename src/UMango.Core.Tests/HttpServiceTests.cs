using UMango.Core.Network;
using Xunit;

namespace UMango.Core.Tests;

public class HttpServiceTests
{
    [Fact]
    public void 代理变更会重建客户端() { var http = new HttpService(); http.ProxyUrl = "http://127.0.0.1:7890"; http.ProxyUrl = null; }
    [Fact]
    public void UA可设置() { var http = new HttpService { UserAgent = "TestAgent/1.0" }; Assert.Equal("TestAgent/1.0", http.UserAgent); }
    [Fact]
    public void 超时默认值合理() { var http = new HttpService(); Assert.InRange(http.TimeoutMs, 1000, 300000); }
}
