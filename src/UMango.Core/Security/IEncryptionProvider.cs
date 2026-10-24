// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/Utils/Encryption/IEncryptionProvider.cs 逐字采用。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.Utils.Encryption → UMango.Core.Security；
//   2. 接口成员上的 public 修饰符去掉（接口成员隐式 public，写出来是冗余的）。

namespace UMango.Core.Security;

/// <summary>
/// 加密提供器。
///
/// 每种提供器自己负责 nonce / tag / salt 的布局，
/// 调用方只管「把字节数组变成字节数组」。
/// </summary>
public interface IEncryptionProvider
{
    /// <summary>加密。</summary>
    byte[] Encrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key);

    /// <summary>解密。</summary>
    byte[] Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key);

    /// <summary>当前平台是否支持该算法。</summary>
    bool IsSupported { get; }
}
