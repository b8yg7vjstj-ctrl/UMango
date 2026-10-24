// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Hash/MD5Provider.cs / SHA1Provider.cs / SHA256Provider.cs / SHA512Provider.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Hash → UMango.Core.Hash。
//   2. `Length` 统一为**字节数**。原版 MD5Provider 写 32、SHA256Provider 写 32 ——
//      那是十六进制字符数，与 MurmurHash2Provider 的 8（字节数）自相矛盾；
//      UMango 统一成字节数，上层要字符数时自己乘 2。
//   3. 补 XML 文档。
//
// 注意：MD5/SHA1 在密码学上已不安全，仅用于**非敌对场景的文件校验**
// （与 PCL 原版用途一致）。需要抗碰撞时用 SHA256 以上。

using System.Security.Cryptography;
using System.Text;

namespace UMango.Core.Hash;

/// <summary>MD5（16 字节）。仅用于兼容性校验，不抗碰撞。</summary>
public sealed class MD5Provider : IHashProvider
{
    public static MD5Provider Instance { get; } = new();

    public int Length => 16;

    public byte[] ComputeHash(ReadOnlySpan<byte> input) => MD5.HashData(input);

    public byte[] ComputeHash(string input, Encoding? encoding = null)
        => ComputeHash(encoding is null ? Encoding.UTF8.GetBytes(input) : encoding.GetBytes(input));

    public byte[] ComputeHash(Stream input) => MD5.HashData(input);

    public async ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default)
        => await MD5.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
}

/// <summary>SHA-1（20 字节）。仅用于兼容性校验，不抗碰撞。</summary>
public sealed class SHA1Provider : IHashProvider
{
    public static SHA1Provider Instance { get; } = new();

    public int Length => 20;

    public byte[] ComputeHash(ReadOnlySpan<byte> input) => SHA1.HashData(input);

    public byte[] ComputeHash(string input, Encoding? encoding = null)
        => ComputeHash(encoding is null ? Encoding.UTF8.GetBytes(input) : encoding.GetBytes(input));

    public byte[] ComputeHash(Stream input) => SHA1.HashData(input);

    public async ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default)
        => await SHA1.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
}

/// <summary>SHA-256（32 字节）。</summary>
public sealed class SHA256Provider : IHashProvider
{
    public static SHA256Provider Instance { get; } = new();

    public int Length => 32;

    public byte[] ComputeHash(ReadOnlySpan<byte> input) => SHA256.HashData(input);

    public byte[] ComputeHash(string input, Encoding? encoding = null)
        => ComputeHash(encoding is null ? Encoding.UTF8.GetBytes(input) : encoding.GetBytes(input));

    public byte[] ComputeHash(Stream input) => SHA256.HashData(input);

    public async ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default)
        => await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
}

/// <summary>SHA-512（64 字节）。</summary>
public sealed class SHA512Provider : IHashProvider
{
    public static SHA512Provider Instance { get; } = new();

    public int Length => 64;

    public byte[] ComputeHash(ReadOnlySpan<byte> input) => SHA512.HashData(input);

    public byte[] ComputeHash(string input, Encoding? encoding = null)
        => ComputeHash(encoding is null ? Encoding.UTF8.GetBytes(input) : encoding.GetBytes(input));

    public byte[] ComputeHash(Stream input) => SHA512.HashData(input);

    public async ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default)
        => await SHA512.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
}
