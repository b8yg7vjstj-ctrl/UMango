// SPDX-License-Identifier: Apache-2.0
//
// DNS 线格式编解码（RFC 1035 消息格式 + RFC 3597 未知类型处理）。
//
// **为什么自己写**：PCL-CE 的 `PCL.Core/IO/Net/Dns/DnsQuery.cs` 依赖第三方包
// `Ae.Dns.Protocol`（`DnsQueryFactory` / `DnsCachingClient` / `DnsHttpClient`）。
// UMango 不为 DNS 引入额外依赖，因此用纯托管代码实现所需的最小子集：
//   · 构造 A / AAAA 查询报文；
//   · 解析应答里的 A / AAAA 记录；
//   · 处理名字压缩指针（RFC 1035 §4.1.4）。
//
// **范围**：只解析 A(1) / AAAA(28) 两条记录。其它类型一律跳过（用 RDLENGTH 定位），
// 不做 CNAME 链跟随 —— 上层拿到 IP 就够用，跟随 CNAME 交给系统解析器更可靠。
//
// 参考：PCL-CE `PCL.Core/IO/Net/Dns/DnsQuery.cs`（Apache-2.0，只借鉴语义）。
// 本文件为独立实现，未复制原版代码。

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace UMango.Core.Network.Dns;

/// <summary>DNS 记录类型。</summary>
public enum DnsRecordType : ushort
{
    /// <summary>IPv4 地址记录。</summary>
    A = 1,

    /// <summary>IPv6 地址记录。</summary>
    Aaaa = 28,

    /// <summary>规范名字记录。</summary>
    Cname = 5,
}

/// <summary>一条已解析出来的地址记录。</summary>
/// <param name="Address">地址。</param>
/// <param name="Type">记录类型（A 或 AAAA）。</param>
/// <param name="TtlSeconds">生存时间（秒）。</param>
public readonly record struct DnsAddressRecord(IPAddress Address, DnsRecordType Type, uint TtlSeconds);

/// <summary>DNS 线格式编解码。</summary>
public static class DnsWire
{
    private const int HeaderLength = 12;

    /// <summary>构造一个 A / AAAA 查询报文。</summary>
    /// <param name="host">要查询的主机名（不带点结尾）。</param>
    /// <param name="type">记录类型。</param>
    /// <param name="transactionId">事务 ID（0~65535）。</param>
    public static byte[] BuildQuery(string host, DnsRecordType type, ushort transactionId = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);

        var name = EncodeName(host);
        var message = new byte[HeaderLength + name.Length + 4];

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0, 2), transactionId);
        // 标志位：0x0100 = 标准查询（RD=1，Recursion Desired）
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4, 2), 1); // QDCOUNT
        // ANCOUNT / NSCOUNT / ARCOUNT 保持 0

        name.CopyTo(message, HeaderLength);
        var tail = HeaderLength + name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(tail, 2), (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(tail + 2, 2), 1); // QCLASS = IN

        return message;
    }

    /// <summary>
    /// 从应答报文里取出 A / AAAA 记录。
    /// </summary>
    /// <param name="response">应答报文。</param>
    /// <returns>解析出的地址记录；报文不合法时返回空列表（不抛异常）。</returns>
    public static IReadOnlyList<DnsAddressRecord> ParseAnswers(ReadOnlySpan<byte> response)
    {
        var results = new List<DnsAddressRecord>();

        if (response.Length < HeaderLength)
        {
            return results;
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..8]);

        var offset = HeaderLength;

        // 跳过问题段
        for (var i = 0; i < questionCount; i++)
        {
            if (!SkipName(response, ref offset) || offset + 4 > response.Length)
            {
                return results;
            }

            offset += 4; // QTYPE + QCLASS
        }

        // 解析答案段
        for (var i = 0; i < answerCount; i++)
        {
            if (!SkipName(response, ref offset) || offset + 10 > response.Length)
            {
                return results;
            }

            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(response[(offset + 4)..]);
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
            offset += 10;

            if (offset + rdLength > response.Length)
            {
                return results;
            }

            if (type == DnsRecordType.A && rdLength == 4)
            {
                var bytes = response.Slice(offset, 4);
                results.Add(new DnsAddressRecord(new IPAddress(bytes.ToArray()), type, ttl));
            }
            else if (type == DnsRecordType.Aaaa && rdLength == 16)
            {
                var bytes = response.Slice(offset, 16);
                results.Add(new DnsAddressRecord(new IPAddress(bytes.ToArray()), type, ttl));
            }

            offset += rdLength;
        }

        return results;
    }

    /// <summary>
    /// 解析应答报文里的应答码（RCODE），低 4 位。
    /// </summary>
    public static int GetResponseCode(ReadOnlySpan<byte> response)
    {
        if (response.Length < HeaderLength)
        {
            return -1;
        }

        return BinaryPrimitives.ReadUInt16BigEndian(response[2..4]) & 0x000F;
    }

    /// <summary>把域名编码成带长度前缀的标签序列（末尾补 0）。</summary>
    private static byte[] EncodeName(string host)
    {
        var labels = host.TrimEnd('.').Split('.');
        var total = 1; // 结尾的 0 长度标签

        foreach (var label in labels)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException($"域名标签长度非法：{label}", nameof(host));
            }

            total += 1 + bytes.Length;
        }

        var result = new byte[total];
        var pos = 0;

        foreach (var label in labels)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            result[pos++] = (byte)bytes.Length;
            bytes.CopyTo(result, pos);
            pos += bytes.Length;
        }

        result[pos] = 0;
        return result;
    }

    /// <summary>
    /// 跳过一段域名（可能是压缩指针），把 <paramref name="offset"/> 移到域名之后。
    /// </summary>
    /// <returns>报文结构是否合法。</returns>
    private static bool SkipName(ReadOnlySpan<byte> message, ref int offset)
    {
        while (true)
        {
            if (offset >= message.Length)
            {
                return false;
            }

            var length = message[offset];

            if ((length & 0xC0) == 0xC0)
            {
                // 压缩指针：占 2 字节（高 2 位 11 + 14 位偏移），指针之后就是下一个字段。
                // 注意：**必须把 offset 推进 2 字节**，否则解析器会从指针字节继续往下读，
                // 把类型/长度字段当成域名标签解析（表现为"一条记录都取不到"）。
                if (offset + 1 >= message.Length)
                {
                    return false;
                }

                offset += 2;
                return true;
            }

            if ((length & 0xC0) != 0)
            {
                // 保留位非零：报文非法
                return false;
            }

            offset++;

            if (length == 0)
            {
                return true;
            }

            offset += length;
        }
    }
}
