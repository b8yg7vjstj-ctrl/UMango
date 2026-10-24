// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Hash/IHashProvider.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Hash → UMango.Core.Hash。
//   2. 原版接口没有为"流式读取"之外的场景提供"边读边算"的能力，
//      这里额外加了 ComputeHashAsync(Stream) 的取消支持（原版已有）。
//   3. 补 XML 文档，明确 Length 指的是**字节数**（原版 MD5Provider 写 32、
//      SHA256Provider 写 32 —— 那是十六进制字符数，不是字节数；
//      MurmurHash2Provider 写 8 才是字节数。原版自相矛盾，
//      UMango 统一为字节数，见各 Provider 的说明）。

using System.Text;

namespace UMango.Core.Hash;

/// <summary>
/// 哈希算法提供者的统一契约。
///
/// 为什么要有这层抽象：包管理器要校验下载文件的完整性，
/// 不同算法（MD5 / SHA256 / MurmurHash2）的调用方式应该一致，
/// 上层（缓存、校验）不该为每个算法写一遍。
/// </summary>
public interface IHashProvider
{
    /// <summary>
    /// 哈希结果的长度，单位是**字节**。
    /// （MD5/SHA1/SHA256/SHA512 均为 16/20/32/64 字节，
    ///   MurmurHash2 为 8 字节。）
    /// </summary>
    int Length { get; }

    /// <summary>对一段字节做哈希。</summary>
    byte[] ComputeHash(ReadOnlySpan<byte> input);

    /// <summary>对一段文本做哈希（默认 UTF-8）。</summary>
    byte[] ComputeHash(string input, Encoding? encoding = null);

    /// <summary>对整个流做哈希。</summary>
    byte[] ComputeHash(Stream input);

    /// <summary>异步地对整个流做哈希。</summary>
    ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default);
}
