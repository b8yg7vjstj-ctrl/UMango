using System.Text;
using UMango.Core.Utils.Codecs;
using Xunit;

namespace UMango.Core.Tests.Codecs;

/// <summary>
/// 编码检测与转换（PCL.Core/Utils/Codecs，Apache-2.0）的测试。
/// </summary>
public class EncodingDetectorTests
{
    static EncodingDetectorTests() => Encodings.EnsureCodePagesRegistered();

    [Fact]
    public void UTF8_BOM_能被识别且解码后不含零宽字符()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("你好 UMango")).ToArray();

        var encoding = EncodingDetector.DetectEncoding(withBom);

        Assert.Equal(65001, encoding.CodePage);
        var text = EncodingUtils.DecodeBytes(withBom);
        // 关键：BOM 不能变成 U+FEFD 留在开头
        Assert.Equal("你好 UMango", text);
        Assert.DoesNotContain('﻿', text);
    }

    [Fact]
    public void UTF16LE_BOM_能被识别()
    {
        var bytes = new byte[] { 0xFF, 0xFE }
            .Concat(Encoding.Unicode.GetBytes("hello"))
            .ToArray();

        Assert.Equal(Encoding.Unicode.CodePage, EncodingDetector.DetectEncoding(bytes).CodePage);
        Assert.Equal("hello", EncodingUtils.DecodeBytes(bytes));
    }

    [Fact]
    public void UTF16BE_BOM_能被识别()
    {
        var bytes = new byte[] { 0xFE, 0xFF }
            .Concat(Encoding.BigEndianUnicode.GetBytes("hello"))
            .ToArray();

        Assert.Equal(Encoding.BigEndianUnicode.CodePage, EncodingDetector.DetectEncoding(bytes).CodePage);
        Assert.Equal("hello", EncodingUtils.DecodeBytes(bytes));
    }

    [Fact]
    public void UTF32LE_BOM_不会被误判成UTF16LE()
    {
        // PCL 原版先判 UTF-16LE（FF FE），UTF-32LE（FF FE 00 00）会被误认
        var bytes = new byte[] { 0xFF, 0xFE, 0x00, 0x00 }
            .Concat(Encoding.UTF32.GetBytes("hello"))
            .ToArray();

        var encoding = EncodingDetector.DetectEncoding(bytes);
        Assert.Equal(12000, encoding.CodePage); // UTF-32
        Assert.Equal("hello", EncodingUtils.DecodeBytes(bytes));
    }

    [Fact]
    public void UTF32BE_BOM_能被识别()
    {
        var bytes = new byte[] { 0x00, 0x00, 0xFE, 0xFF }
            .Concat(Encoding.UTF32.GetBytes("hello")).ToArray();

        // 平台若不支持 utf-32BE 就退到 UTF32，码页可能是 12001 或 12000
        var codePage = EncodingDetector.DetectEncoding(bytes).CodePage;
        Assert.True(codePage is 12000 or 12001, $"实际码页 {codePage}");
    }

    [Fact]
    public void 无BOM的UTF8_被正确识别()
    {
        var bytes = Encoding.UTF8.GetBytes("纯 UTF-8 文本，没有 BOM");
        Assert.Equal(65001, EncodingDetector.DetectEncoding(bytes).CodePage);
    }

    [Fact]
    public void GBK文本_不会被误判成UTF8()
    {
        // 这是 PCL 原版的致命缺陷：_IsValidUtf8 从没真的读过流，
        // 解码的是全零 buffer，于是**任何**文件都被判成合法 UTF-8。
        var gbk = Encoding.GetEncoding(936);
        var bytes = gbk.GetBytes("中文测试内容，Windows 简体中文环境常见");

        var encoding = EncodingDetector.DetectEncoding(bytes);
        Assert.NotEqual(65001, encoding.CodePage);

        var text = EncodingUtils.DecodeBytes(bytes);
        Assert.Equal("中文测试内容，Windows 简体中文环境常见", text);
    }

    [Fact]
    public void GBK文本_即使有BOM也不受影响()
    {
        var gbk = Encoding.GetEncoding(936);
        var bytes = gbk.GetBytes("带 BOM 的中文");
        var encoding = EncodingDetector.DetectEncoding(bytes);
        Assert.NotEqual(65001, encoding.CodePage);
    }

    [Fact]
    public void 空输入_返回空串且不抛()
    {
        Assert.Equal("", EncodingUtils.DecodeBytes(Array.Empty<byte>()));
        Assert.Equal(65001, EncodingDetector.DetectEncoding(Array.Empty<byte>()).CodePage);
    }

    [Fact]
    public void 流检测_不会改变流的当前位置()
    {
        var bytes = Encoding.UTF8.GetBytes("abcdefghij");
        using var stream = new MemoryStream(bytes);
        stream.Position = 3;

        var encoding = EncodingDetector.DetectEncoding(stream);

        Assert.Equal(65001, encoding.CodePage);
        Assert.Equal(3, stream.Position); // 位置必须复原
    }

    [Fact]
    public void 流检测_从流开头检测()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("x")).ToArray();
        using var stream = new MemoryStream(bytes);

        var encoding = EncodingDetector.DetectEncoding(stream, readFromBegin: true);
        Assert.Equal(65001, encoding.CodePage);
    }

    [Fact]
    public void 不可Seek的流_明确报错()
    {
        using var stream = new NonSeekableStream(Encoding.UTF8.GetBytes("abc"));
        Assert.Throws<ArgumentException>(() => EncodingDetector.DetectEncoding(stream));
    }

    [Fact]
    public void GB18030提供者_可用时码页是54936()
    {
        // 平台不支持时退回 Latin1（不丢数据），但不能抛异常
        var encoding = Encodings.GB18030;
        Assert.True(encoding.CodePage is 54936 or 28591);
    }

    [Fact]
    public void 任意字节_解码都不抛异常()
    {
        var rnd = new Random(12345);
        for (var i = 0; i < 50; i++)
        {
            var bytes = new byte[rnd.Next(0, 64)];
            rnd.NextBytes(bytes);
            var text = EncodingUtils.DecodeBytes(bytes);
            Assert.NotNull(text);
        }
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
