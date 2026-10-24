// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/Utils/Secret/EncryptHelper.cs 中的 EncryptionData 结构逐字采用。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 从 EncryptHelper 的嵌套结构提升为独立类型（UMango 的密钥层
//      跨平台，不需要 PCL 的 DPAPI 专属部分，故把纯格式的部分拆出来）；
//   2. 命名空间 PCL.Core.Utils.Secret → UMango.Core.Security；
//   3. 字段改成属性（保持 record struct 风格一致）。

using System.Buffers.Binary;

namespace UMango.Core.Security;

/// <summary>
/// 版本化密文容器。
///
/// 布局（大端）：
/// <code>
/// [0..4)   MagicNumber  0x454E4321 ("ENC!")
/// [4..8)   Version      算法版本
/// [8..12)  DataLength   密文长度
/// [12..)   Data         密文
/// </code>
///
/// **为什么要有容器**：算法会变（AES-CBC → ChaCha20 → AES-GCM）。
/// 没有版本号，旧密文就再也解不开了；有了版本号，将来可以逐条迁移。
/// </summary>
public readonly record struct EncryptionData(uint Version, byte[] Data)
{
    /// <summary>魔数。</summary>
    public const uint MagicNumber = 0x454E4321;

    /// <summary>头部长度：魔数 4 + 版本 4 + 长度 4。</summary>
    public const int HeaderSize = 12;

    /// <summary>从字节数组解析。</summary>
    public static EncryptionData FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
            throw new ArgumentException("No enough data for EncryptionData", nameof(bytes));

        if (BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]) != MagicNumber)
            throw new ArgumentException("Unknown data for EncryptionData", nameof(bytes));

        var dataLength = BinaryPrimitives.ReadInt32BigEndian(bytes[8..12]);
        if (dataLength < 0)
            throw new ArgumentException("Invalid data length for EncryptionData", nameof(bytes));
        if (dataLength > bytes.Length - HeaderSize)
            throw new ArgumentException("No enough data for EncryptionData", nameof(bytes));

        return new EncryptionData(
            BinaryPrimitives.ReadUInt32BigEndian(bytes[4..8]),
            bytes[HeaderSize..(HeaderSize + dataLength)].ToArray());
    }

    /// <summary>序列化为字节数组。</summary>
    public byte[] ToBytes()
    {
        var length = HeaderSize + Data.Length;
        var bytes = new byte[length];
        var bytesSpan = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(bytesSpan[..4], MagicNumber);
        BinaryPrimitives.WriteUInt32BigEndian(bytesSpan[4..8], Version);
        BinaryPrimitives.WriteInt32BigEndian(bytesSpan[8..12], Data.Length);
        Data.CopyTo(bytesSpan[HeaderSize..]);

        return bytes;
    }

    /// <summary>判断字节数组是不是本格式。</summary>
    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        try
        {
            return data.Length >= HeaderSize
                   && BinaryPrimitives.ReadUInt32BigEndian(data[..4]) == MagicNumber;
        }
        catch
        {
            return false;
        }
    }
}
