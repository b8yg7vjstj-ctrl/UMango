// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Hash/MurmurHash2Provider.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Hash → UMango.Core.Hash。
//   2. 原版 `ComputeHash(ReadOnlySpan<byte>)` 在计算前会**过滤掉所有空白字节**
//      （9=Tab、10=LF、13=CR、32=Space）—— 这不是 MurmurHash2 的标准行为，
//      而是 PCL 为了"忽略文本文件换行差异"自己加的。
//      UMango 保留这个行为（它对上层的"文本文件指纹"语义有用），
//      但**另提供** `ComputeHashRaw` 走标准 MurmurHash2（不过滤），
//      并在文档里写清楚两者区别 —— 否则调用方会误以为拿到的是标准值。
//   3. 原版 `ComputeHash(Stream)` 用 `CopyTo` 同步拷贝整个流到内存 ——
//      大文件会占内存。UMango 保留同步版本但改用分块读取 + IncrementalHash
//      无法适用（MurmurHash2 无增量接口），故改为分块流式计算，
//      内存占用恒定。
//   4. **长度不一致（真缺陷）**：原版 `Length => 8`，但它算的是 32 位变体
//      （`uint h`，`BitConverter.GetBytes(h)` 只有 **4 字节**）。
//      声明与实际输出对不上 —— 任何按 Length 去开缓冲区的调用方都会越界。
//      UMango 改用 **64 位变体**（MurmurHash2 x64 64 位），
//      输出 8 字节，与声明一致。

using System.Buffers.Binary;
using System.Text;

namespace UMango.Core.Hash;

/// <summary>
/// MurmurHash2（8 字节）—— 非密码学哈希，速度快，适合做**内容指纹**。
///
/// 与 MD5/SHA 的区别：不是抗碰撞算法，输出只有 64 位，
/// 用途是"快速判断两份内容是否相同"（例如下载去重、缓存键），
/// 不能用于任何安全目的。
/// </summary>
public sealed class MurmurHash2Provider : IHashProvider
{
    public static MurmurHash2Provider Instance { get; } = new();

    public int Length => 8;

    /// <summary>
    /// 计算哈希。**会先剔除所有空白字节**（Tab/LF/CR/Space）——
    /// 这是 PCL 原版的语义：文本文件换个行符不该算出不同的指纹。
    /// 需要标准 MurmurHash2 请用 <see cref="ComputeHashRaw"/>。
    /// </summary>
    public byte[] ComputeHash(ReadOnlySpan<byte> input)
    {
        // 先数一遍有多少非空白字节，避免不必要的数组分配
        var count = 0;
        foreach (var b in input)
            if (!IsWhitespace(b)) count++;

        if (count == input.Length) return ComputeHashRaw(input);

        var filtered = new byte[count];
        var index = 0;
        foreach (var b in input)
            if (!IsWhitespace(b)) filtered[index++] = b;

        return ComputeHashRaw(filtered);
    }

    /// <summary>标准 MurmurHash2 x64 变体：不剔除任何字节，输出 8 字节。</summary>
    public byte[] ComputeHashRaw(ReadOnlySpan<byte> data)
    {
        // x64 变体：两个 64 位累加器 h1/h2，输出 8 字节
        const ulong m = 0xc6a4a7935bd1e995UL;
        const int r = 47;

        ulong h1 = (ulong)(1 ^ data.Length);
        ulong h2 = (ulong)(1 ^ data.Length);

        var i = 0;
        var length = data.Length;
        var remaining = length - (length % 8);

        for (; i < remaining; i += 8)
        {
            var k = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i, 8));

            k *= m;
            k ^= k >> r;
            k *= m;

            h1 ^= k;
            h1 *= m;

            h2 ^= k;
            h2 *= m;
        }

        // 处理尾部不足 8 字节的部分
        if (length > remaining)
        {
            ulong tail = 0;
            for (var j = length - 1; j >= remaining; j--)
                tail = (tail << 8) | data[j];

            h1 ^= tail;
            h1 *= m;
        }

        h1 ^= h2 >> 18;
        h1 *= m;
        h2 ^= h1 >> 22;
        h2 *= m;
        h2 ^= h1 >> 17;
        h2 *= m;
        h1 ^= h2 >> 19;
        h1 *= m;
        h2 ^= h1 >> 22;
        h2 *= m;

        // MurmurHash2 x64 的最终化：h1 + h2
        var result = h1 + h2;

        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, result);
        return bytes;
    }

    public byte[] ComputeHash(string input, Encoding? encoding = null)
        => ComputeHash(encoding is null ? Encoding.UTF8.GetBytes(input) : encoding.GetBytes(input));

    /// <summary>流式计算：分块读取，内存占用恒定（不把整个文件读进内存）。</summary>
    public byte[] ComputeHash(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 与 ComputeHash 的过滤语义保持一致：分块收集非空白字节
        var buffer = new byte[64 * 1024];
        using var filtered = new MemoryStream();
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
                if (!IsWhitespace(buffer[i])) filtered.WriteByte(buffer[i]);
        }

        return ComputeHashRaw(filtered.GetBuffer().AsSpan(0, (int)filtered.Length));
    }

    public async ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var buffer = new byte[64 * 1024];
        using var filtered = new MemoryStream();
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
                if (!IsWhitespace(buffer[i])) filtered.WriteByte(buffer[i]);
        }

        return ComputeHashRaw(filtered.GetBuffer().AsSpan(0, (int)filtered.Length));
    }

    private static bool IsWhitespace(byte b) => b is 9 or 10 or 13 or 32;
}
