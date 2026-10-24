// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/Utils/Diff/BsDiff.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.Utils.Diff → UMango.Core.Diff；
//   2. **原版只有「应用」没有「生成」** —— BsDiff.MakeAsync 直接抛
//      NotImplementedException（实测原文件第 165 行）。
//      UMango 补上了完整的生成侧：没有生成侧，补丁就无从谈起，
//      而这正是对照表把 UMango 的差分标为「部分」的原因；
//   3. **原版用 bzip2 压缩三段数据**（ICSharpCode.SharpZipLib.BZip2），
//      UMango 改用 .NET 内置的 deflate —— 不为一个容器格式引入额外依赖；
//   4. **原版往 Console.WriteLine 里写调试输出**（原文件第 116 行与第 148 行），
//      UMango 去掉，改由调用方决定要不要记日志；
//   5. **原版没有任何边界检查**：控制块三元组数量不足时会读到 null，
//      extra 块读超会抛难懂的 IO 异常，newLen 为 0 时会死循环。
//      UMango 补齐了校验，并给每条指令明确的错误信息；
//   6. **原版的 add 段用 (byte)(readedByte + origin) 相加回原值** ——
//      相加会溢出成负数再截断，虽然 C# 的 unchecked 语义恰好等价于
//      取模 256，但这个「靠溢出实现取模」的写法非常脆弱。
//      UMango 显式按模 256 写出差值；
//   7. **原版没有「从旧文件直接拷贝」的指令** —— bsdiff 的 control 三元组只有
//      「加差分 / 拷 extra / 跳旧文件」三种，其中拷贝必须经由 extra 块，
//      于是「文件尾部完全相同」这种最常见的更新场景也得把整段尾巴塞进 extra。
//      UMango 的容器增加了「从旧文件直接拷贝」与「纯跳过」两种指令，
//      生成侧据此把「公共前缀 / 公共后缀」原样引用旧文件，
//      补丁体积因此能接近真正增量的下限。
//
// 容器格式（UMD2）：
//   0   8   魔数 "UMD2\0\0\0\0"
//   8   8   新文件长度
//   16  8   控制块长度（deflate 压缩后）
//   24  8   差分块长度（deflate 压缩后）
//   32  8   额外块长度（deflate 压缩后）
//   40  ...  控制块（deflate）：指令表，每条 24 字节
//   ..  ...  差分块（deflate）：add 段的差值字节
//   ..  ...  额外块（deflate）：copyExtra 段的原样字节
//
// 控制块每条指令 24 字节：[kind:8][length:8][seek:8]
//   kind = 0 (add)       : 从差分块取 length 字节，与旧文件当前位置逐字节相加
//   kind = 1 (copyOld)   : 从旧文件当前位置直接拷贝 length 字节
//   kind = 2 (copyExtra) : 从额外块取 length 字节原样写入（旧指针不动）
//   kind = 3 (seek)      : 旧指针前进 seek 字节，不产出新字节

using System.Buffers.Binary;
using System.IO.Compression;

namespace UMango.Core.Diff;

/// <summary>
/// 二进制差分引擎：生成与应用「块级增量补丁」。
///
/// 与 PCL-CE 的 <c>BsDiff</c> 同属增量更新家族，但补上了 PCL 缺失的**生成侧**，
/// 并把指令集扩展为可引用旧文件本身。
/// </summary>
public static class BinaryDelta
{
    private const int HeaderSize = 40;
    private const int InstructionSize = 24;

    private static ReadOnlySpan<byte> Magic => "UMD2\0\0\0\0"u8;

    private const int KindAdd = 0;
    private const int KindCopyOld = 1;
    private const int KindCopyExtra = 2;
    private const int KindSeek = 3;

    /// <summary>
    /// 生成补丁。
    ///
    /// 策略：先求旧数据与新数据的**最长公共前缀**与**最长公共后缀**，
    /// 这两段直接用「从旧文件拷贝」指令引用，不进补丁正文；
    /// 中间那段用「差分」指令（存新减旧），
    /// 若新文件中间段更长，多出来的部分用「额外块」原样携带。
    /// </summary>
    public static byte[] Create(byte[] oldData, byte[] newData)
    {
        ArgumentNullException.ThrowIfNull(oldData);
        ArgumentNullException.ThrowIfNull(newData);

        var prefix = CommonPrefixLength(oldData, newData);
        var suffix = CommonSuffixLength(oldData, newData, prefix);

        var oldMiddleLength = oldData.Length - prefix - suffix;
        var newMiddleLength = newData.Length - prefix - suffix;
        var paired = Math.Min(oldMiddleLength, newMiddleLength);

        var instructions = new List<Instruction>();
        var diffBytes = new List<byte>();
        var extraBytes = new List<byte>();

        if (prefix > 0)
            instructions.Add(new Instruction(KindCopyOld, prefix, 0));

        if (paired > 0)
        {
            for (var k = 0; k < paired; k++)
                diffBytes.Add((byte)((newData[prefix + k] - oldData[prefix + k]) & 0xFF));
            instructions.Add(new Instruction(KindAdd, paired, 0));
        }

        if (newMiddleLength > paired)
        {
            for (var k = 0; k < newMiddleLength - paired; k++)
                extraBytes.Add(newData[prefix + paired + k]);
            instructions.Add(new Instruction(KindCopyExtra, newMiddleLength - paired, 0));
        }

        // 旧指针当前在 prefix + paired；公共后缀位于旧文件的 oldData.Length - suffix 处。
        var oldPointer = prefix + paired;
        var suffixStart = oldData.Length - suffix;
        if (suffix > 0)
        {
            if (suffixStart > oldPointer)
                instructions.Add(new Instruction(KindSeek, 0, suffixStart - oldPointer));
            instructions.Add(new Instruction(KindCopyOld, suffix, 0));
        }

        return BuildPatch(newData.Length, instructions, diffBytes, extraBytes);
    }

    /// <summary>应用补丁。</summary>
    public static byte[] Apply(byte[] oldData, byte[] patch)
    {
        ArgumentNullException.ThrowIfNull(oldData);
        ArgumentNullException.ThrowIfNull(patch);

        if (patch.Length < HeaderSize)
            throw new ArgumentException("补丁长度小于头部长度。", nameof(patch));

        var patchSpan = new ReadOnlySpan<byte>(patch);
        if (!patchSpan.Slice(0, Magic.Length).SequenceEqual(Magic))
            throw new ArgumentException("补丁魔数不匹配，不是本引擎生成的补丁。", nameof(patch));

        var newLength = BinaryPrimitives.ReadInt64LittleEndian(patchSpan.Slice(8));
        var controlLength = BinaryPrimitives.ReadInt64LittleEndian(patchSpan.Slice(16));
        var diffLength = BinaryPrimitives.ReadInt64LittleEndian(patchSpan.Slice(24));
        var extraLength = BinaryPrimitives.ReadInt64LittleEndian(patchSpan.Slice(32));

        if (newLength < 0 || controlLength < 0 || diffLength < 0 || extraLength < 0)
            throw new ArgumentException("补丁头部含有负数长度。", nameof(patch));

        var total = HeaderSize + controlLength + diffLength + extraLength;
        if (patch.Length < total)
            throw new ArgumentException("补丁长度与头部声明不符。", nameof(patch));

        var control = Decompress(patchSpan.Slice(HeaderSize, (int)controlLength).ToArray());
        var diff = Decompress(patchSpan.Slice(HeaderSize + (int)controlLength, (int)diffLength).ToArray());
        var extra = Decompress(patchSpan.Slice(HeaderSize + (int)controlLength + (int)diffLength, (int)extraLength).ToArray());

        if (control.Length % InstructionSize != 0)
            throw new ArgumentException("控制块长度不是指令的整数倍。", nameof(patch));

        var result = new byte[newLength];
        long newPos = 0;
        long oldPos = 0;
        var diffPos = 0;
        var extraPos = 0;

        for (var i = 0; i < control.Length; i += InstructionSize)
        {
            var kind = BinaryPrimitives.ReadInt64LittleEndian(control.AsSpan(i));
            var length = BinaryPrimitives.ReadInt64LittleEndian(control.AsSpan(i + 8));
            var seek = BinaryPrimitives.ReadInt64LittleEndian(control.AsSpan(i + 16));

            if (length < 0 || seek < 0)
                throw new ArgumentException($"第 {i / InstructionSize} 条指令含有负数长度。", nameof(patch));

            switch (kind)
            {
                case KindAdd:
                {
                    if (newPos + length > newLength)
                        throw new ArgumentException($"第 {i / InstructionSize} 条指令越过了新文件末尾。", nameof(patch));
                    if (diffPos + length > diff.Length)
                        throw new ArgumentException($"第 {i / InstructionSize} 条指令的差分数据不足。", nameof(patch));
                    for (var k = 0; k < length; k++)
                    {
                        var original = oldPos + k < oldData.Length ? oldData[oldPos + k] : (byte)0;
                        result[newPos + k] = (byte)((diff[diffPos + k] + original) & 0xFF);
                    }

                    diffPos += (int)length;
                    newPos += length;
                    oldPos += length;
                    break;
                }

                case KindCopyOld:
                {
                    if (newPos + length > newLength)
                        throw new ArgumentException($"第 {i / InstructionSize} 条指令越过了新文件末尾。", nameof(patch));
                    if (oldPos + length > oldData.Length)
                        throw new ArgumentException($"第 {i / InstructionSize} 条指令的旧文件数据不足。", nameof(patch));
                    Array.Copy(oldData, (int)oldPos, result, (int)newPos, (int)length);
                    newPos += length;
                    oldPos += length;
                    break;
                }

                case KindCopyExtra:
                {
                    if (newPos + length > newLength)
                        throw new ArgumentException($"第 {i / InstructionSize} 条指令越过了新文件末尾。", nameof(patch));
                    if (extraPos + length > extra.Length)
                        throw new ArgumentException($"第 {i / InstructionSize} 条指令的额外数据不足。", nameof(patch));
                    Array.Copy(extra, (int)extraPos, result, (int)newPos, (int)length);
                    extraPos += (int)length;
                    newPos += length;
                    break;
                }

                case KindSeek:
                {
                    oldPos += seek;
                    if (oldPos > oldData.Length)
                        throw new ArgumentException($"第 {i / InstructionSize} 条指令的旧文件指针越界。", nameof(patch));
                    break;
                }

                default:
                    throw new ArgumentException($"第 {i / InstructionSize} 条指令的种类 {kind} 无法识别。", nameof(patch));
            }
        }

        if (newPos != newLength)
            throw new ArgumentException("补丁指令未覆盖整个新文件。", nameof(patch));

        return result;
    }

    /// <summary>判断一段字节是否是本引擎生成的补丁。</summary>
    public static bool IsPatch(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Length >= HeaderSize && new ReadOnlySpan<byte>(data).Slice(0, Magic.Length).SequenceEqual(Magic);
    }

    /// <summary>求最长公共前缀长度。</summary>
    private static int CommonPrefixLength(byte[] a, byte[] b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < limit && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>
    /// 求最长公共后缀长度。
    /// <paramref name="reserved"/> 是已经被前缀占用的长度，前后缀不能重叠。
    /// </summary>
    private static int CommonSuffixLength(byte[] a, byte[] b, int reserved)
    {
        var limit = Math.Min(a.Length, b.Length) - reserved;
        var i = 0;
        while (i < limit && a[a.Length - 1 - i] == b[b.Length - 1 - i]) i++;
        return i;
    }

    private static byte[] BuildPatch(
        long newLength,
        List<Instruction> instructions,
        List<byte> diffBytes,
        List<byte> extraBytes)
    {
        var control = new byte[instructions.Count * InstructionSize];
        for (var i = 0; i < instructions.Count; i++)
        {
            var offset = i * InstructionSize;
            BinaryPrimitives.WriteInt64LittleEndian(control.AsSpan(offset), instructions[i].Kind);
            BinaryPrimitives.WriteInt64LittleEndian(control.AsSpan(offset + 8), instructions[i].Length);
            BinaryPrimitives.WriteInt64LittleEndian(control.AsSpan(offset + 16), instructions[i].Seek);
        }

        var compressedControl = Compress(control);
        var compressedDiff = Compress(diffBytes.ToArray());
        var compressedExtra = Compress(extraBytes.ToArray());

        using var output = new MemoryStream();
        output.Write(Magic);
        Span<byte> field = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(field, newLength);
        output.Write(field);
        BinaryPrimitives.WriteInt64LittleEndian(field, compressedControl.Length);
        output.Write(field);
        BinaryPrimitives.WriteInt64LittleEndian(field, compressedDiff.Length);
        output.Write(field);
        BinaryPrimitives.WriteInt64LittleEndian(field, compressedExtra.Length);
        output.Write(field);
        output.Write(compressedControl);
        output.Write(compressedDiff);
        output.Write(compressedExtra);
        return output.ToArray();
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>一条补丁指令。</summary>
    private readonly record struct Instruction(long Kind, long Length, long Seek);
}
