// SPDX-License-Identifier: Apache-2.0
//
// DNS 线格式与 DoH 解析器的测试。
//
// 对应 PCL-CE `PCL.Core/IO/Net/Dns/`（Apache-2.0）：UMango 用独立实现替代
// 第三方 `Ae.Dns.Protocol` 依赖，因此这里对着**协议规范**测，不对着 PCL 原版测。

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Network.Dns;
using Xunit;

namespace UMango.Core.Tests.Network;

public sealed class DnsWireTests
{
    private static byte[] U16(ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    [Fact]
    public void 构造查询_头部字段正确()
    {
        var query = DnsWire.BuildQuery("example.com", DnsRecordType.A, 0x1234);

        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2, 2))); // RD 位
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)));      // QDCOUNT
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(6, 2)));      // ANCOUNT
    }

    [Fact]
    public void 构造查询_域名按标签编码()
    {
        var query = DnsWire.BuildQuery("example.com", DnsRecordType.A);

        // 12 字节头之后：7 "example" 3 "com" 0
        Assert.Equal(7, query[12]);
        Assert.Equal((byte)'e', query[13]);
        var comOffset = 12 + 1 + 7;
        Assert.Equal(3, query[comOffset]);
        Assert.Equal((byte)'c', query[comOffset + 1]);
        // 根标签（0）在 "com" 之后；其后才是 QTYPE/QCLASS 四字节
        Assert.Equal(0, query[comOffset + 4]);
        Assert.Equal(0, query[comOffset + 5]);
    }

    [Fact]
    public void 构造查询_末尾是类型与类()
    {
        var query = DnsWire.BuildQuery("a.com", DnsRecordType.Aaaa);

        var tail = query.Length - 4;
        Assert.Equal(28, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(tail, 2))); // AAAA
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(tail + 2, 2))); // IN
    }

    [Fact]
    public void 构造查询_带尾点也能编码()
    {
        var query = DnsWire.BuildQuery("example.com.", DnsRecordType.A);

        Assert.Equal(7, query[12]);
    }

    [Fact]
    public void 构造查询_空域名抛异常()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery("", DnsRecordType.A));
    }

    [Fact]
    public void 解析应答_取出A记录()
    {
        var response = BuildResponse(("a.com", DnsRecordType.A, new byte[] { 1, 2, 3, 4 }, 300));

        var records = DnsWire.ParseAnswers(response);

        var record = Assert.Single(records);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), record.Address);
        Assert.Equal(DnsRecordType.A, record.Type);
        Assert.Equal(300u, record.TtlSeconds);
    }

    [Fact]
    public void 解析应答_取出AAAA记录()
    {
        var ipv6 = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        var response = BuildResponse(("a.com", DnsRecordType.Aaaa, ipv6, 60));

        var records = DnsWire.ParseAnswers(response);

        var record = Assert.Single(records);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), record.Address);
        Assert.Equal(DnsRecordType.Aaaa, record.Type);
    }

    [Fact]
    public void 解析应答_跳过不认识的类型()
    {
        var response = BuildResponse(
            ("a.com", DnsRecordType.Cname, new byte[] { 1, 2, 3 }, 60),
            ("a.com", DnsRecordType.A, new byte[] { 5, 6, 7, 8 }, 60));

        var records = DnsWire.ParseAnswers(response);

        var record = Assert.Single(records);
        Assert.Equal(IPAddress.Parse("5.6.7.8"), record.Address);
    }

    [Fact]
    public void 解析应答_多条记录都取出来()
    {
        var response = BuildResponse(
            ("a.com", DnsRecordType.A, new byte[] { 1, 1, 1, 1 }, 60),
            ("a.com", DnsRecordType.A, new byte[] { 2, 2, 2, 2 }, 60));

        var records = DnsWire.ParseAnswers(response);

        Assert.Equal(2, records.Count);
    }

    [Fact]
    public void 解析应答_过短返回空()
    {
        Assert.Empty(DnsWire.ParseAnswers(new byte[5]));
    }

    [Fact]
    public void 解析应答_空应答段返回空()
    {
        var response = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), 1); // QDCOUNT=1 但没有问题段
        Assert.Empty(DnsWire.ParseAnswers(response));
    }

    [Fact]
    public void 取应答码_成功是零()
    {
        var response = new byte[12];
        Assert.Equal(0, DnsWire.GetResponseCode(response));
    }

    [Fact]
    public void 取应答码_失败是三()
    {
        var response = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 3);
        Assert.Equal(3, DnsWire.GetResponseCode(response));
    }

    [Fact]
    public void 取应答码_报文过短返回负一()
    {
        Assert.Equal(-1, DnsWire.GetResponseCode(new byte[3]));
    }

    /// <summary>造一条带压缩指针的应答报文。</summary>
    private static byte[] BuildResponse(
        params (string Name, DnsRecordType Type, byte[] Data, uint Ttl)[] answers)
    {
        var message = new List<byte>();
        message.AddRange(new byte[12]);

        // 放一个问题段
        message.AddRange(new byte[] { 1, (byte)'a' });
        message.Add(0); // 根标签
        message.AddRange(new byte[] { 0, 1, 0, 1 }); // QTYPE=IN? 用 A/1, QCLASS=1

        foreach (var (name, type, data, ttl) in answers)
        {
            // 名字用压缩指针指向问题段的名字（offset 12）
            message.AddRange(new byte[] { 0xC0, 0x0C });
            message.AddRange(U16((ushort)type));
            message.AddRange(U16(1)); // CLASS=IN
            message.AddRange(U32(ttl));
            message.AddRange(U16((ushort)data.Length));
            message.AddRange(data);
        }

        var result = message.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), 1);                       // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6, 2), (ushort)answers.Length); // ANCOUNT
        return result;
    }
}

public sealed class DohResolverTests
{
    private static byte[] U16(ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    [Fact]
    public async Task 没有上游_不查询直接回落系统()
    {
        var transport = new FakeTransport();
        var resolver = new DohResolver(transport) { Upstreams = Array.Empty<string>() };

        var result = await resolver.ResolveAsync("localhost");

        Assert.Equal(0, transport.CallCount);
        Assert.Equal(DnsResolveSource.System, result.Source);
    }

    [Fact]
    public async Task 上游返回地址_来源是DoH()
    {
        var transport = new FakeTransport();
        transport.Respond = query => QueryTypeOf(query) == DnsRecordType.Aaaa
            ? BuildAaaaResponse()
            : BuildAResponse();

        var resolver = new DohResolver(transport) { Upstreams = new[] { "https://doh.test/dns-query" } };

        var result = await resolver.ResolveAsync("example.com");

        Assert.Equal(DnsResolveSource.Doh, result.Source);
        Assert.Contains(IPAddress.Parse("1.2.3.4"), result.Addresses);
    }

    [Fact]
    public async Task 上游失败_回落到系统解析器()
    {
        var transport = new FakeTransport { Fail = true };
        var resolver = new DohResolver(transport) { Upstreams = new[] { "https://doh.test/dns-query" } };

        var result = await resolver.ResolveAsync("localhost");

        Assert.Equal(DnsResolveSource.System, result.Source);
        Assert.True(result.HasAddresses);
    }

    [Fact]
    public async Task 上游失败且不回落_返回失败结果()
    {
        var transport = new FakeTransport { Fail = true };
        var resolver = new DohResolver(transport)
        {
            Upstreams = new[] { "https://doh.test/dns-query" },
            FallBackToSystem = false,
        };

        var result = await resolver.ResolveAsync("example.com");

        Assert.Equal(DnsResolveSource.None, result.Source);
        Assert.False(result.HasAddresses);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task 第二次解析_命中缓存不再请求()
    {
        var transport = new FakeTransport();
        transport.Respond = query => QueryTypeOf(query) == DnsRecordType.Aaaa
            ? BuildAaaaResponse()
            : BuildAResponse();

        var resolver = new DohResolver(transport) { Upstreams = new[] { "https://doh.test/dns-query" } };

        var first = await resolver.ResolveAsync("example.com");
        var callsAfterFirst = transport.CallCount;
        var second = await resolver.ResolveAsync("example.com");

        Assert.Equal(callsAfterFirst, transport.CallCount);
        Assert.Equal(1, resolver.CacheHitCount);
        Assert.Equal(first.Addresses.Count, second.Addresses.Count);
    }

    [Fact]
    public async Task 缓存过期后_重新请求()
    {
        var transport = new FakeTransport();
        transport.Respond = query => QueryTypeOf(query) == DnsRecordType.Aaaa
            ? BuildAaaaResponse()
            : BuildAResponse();

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var resolver = new DohResolver(transport)
        {
            Upstreams = new[] { "https://doh.test/dns-query" },
            CacheTtlSeconds = 60,
            Clock = () => now,
        };

        await resolver.ResolveAsync("example.com");
        var before = transport.CallCount;

        // 时间没动 → 仍命中缓存
        await resolver.ResolveAsync("example.com");
        Assert.Equal(before, transport.CallCount);
        Assert.Equal(1, resolver.CacheHitCount);

        // 时间越过 TTL → 重新请求
        now = now.AddSeconds(61);
        await resolver.ResolveAsync("example.com");
        Assert.True(transport.CallCount > before);
    }

    [Fact]
    public async Task 清空缓存后_重新请求()
    {
        var transport = new FakeTransport();
        transport.Respond = query => QueryTypeOf(query) == DnsRecordType.Aaaa
            ? BuildAaaaResponse()
            : BuildAResponse();

        var resolver = new DohResolver(transport) { Upstreams = new[] { "https://doh.test/dns-query" } };

        await resolver.ResolveAsync("example.com");
        var before = transport.CallCount;
        resolver.ClearCache();
        await resolver.ResolveAsync("example.com");

        Assert.True(transport.CallCount > before);
    }

    [Fact]
    public async Task 多个上游_合并去重()
    {
        var transport = new FakeTransport();
        transport.Respond = query => QueryTypeOf(query) == DnsRecordType.Aaaa
            ? BuildAaaaResponse()
            : BuildAResponse();

        var resolver = new DohResolver(transport)
        {
            Upstreams = new[] { "https://a.test/dns-query", "https://b.test/dns-query" },
        };

        var result = await resolver.ResolveAsync("example.com");

        // 两个上游给的是同一组地址 → 合并去重后仍是 1 个 IPv4 + 1 个 IPv6
        Assert.Equal(2, result.Addresses.Count);
        Assert.Equal(result.Addresses.Distinct().Count(), result.Addresses.Count);
    }

    [Fact]
    public async Task IPv4排在IPv6前面()
    {
        var transport = new FakeTransport();
        transport.Respond = query => QueryTypeOf(query) == DnsRecordType.Aaaa
            ? BuildAaaaResponse()
            : BuildAResponse();

        var resolver = new DohResolver(transport) { Upstreams = new[] { "https://doh.test/dns-query" } };

        var result = await resolver.ResolveAsync("example.com");

        Assert.Equal(2, result.Addresses.Count);
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, result.Addresses[0].AddressFamily);
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetworkV6, result.Addresses[1].AddressFamily);
    }

    private static DnsRecordType QueryTypeOf(byte[] query)
    {
        var tail = query.Length - 4;
        return (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(tail, 2));
    }

    private static byte[] BuildAResponse() => BuildResponse(DnsRecordType.A, new byte[] { 1, 2, 3, 4 });

    private static byte[] BuildAaaaResponse() =>
        BuildResponse(DnsRecordType.Aaaa, IPAddress.Parse("2001:db8::1").GetAddressBytes());

    private static byte[] BuildResponse(DnsRecordType type, byte[] data)
    {
        var message = new List<byte>();
        message.AddRange(new byte[12]);
        message.AddRange(new byte[] { 1, (byte)'a', 0 });
        message.AddRange(new byte[] { 0, 1, 0, 1 });
        message.AddRange(new byte[] { 0xC0, 0x0C });
        message.AddRange(U16((ushort)type));
        message.AddRange(U16(1));
        message.AddRange(U32(60u));
        message.AddRange(U16((ushort)data.Length));
        message.AddRange(data);

        var result = message.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6, 2), 1);
        return result;
    }

    private sealed class FakeTransport : IDohTransport
    {
        public int CallCount { get; private set; }

        public bool Fail { get; set; }

        public Func<byte[], byte[]?> Respond { get; set; } = _ => null;

        public Task<byte[]?> QueryAsync(string url, byte[] query, CancellationToken ct)
        {
            CallCount++;
            if (Fail)
            {
                return Task.FromResult<byte[]?>(null);
            }

            return Task.FromResult(Respond(query));
        }
    }
}
