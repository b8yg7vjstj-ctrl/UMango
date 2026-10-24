using System.Text;

namespace UMango.Core.Utils;

/// <summary>
/// 只读的「字符串流」—— 把一段文本包装成 <see cref="Stream"/>。
///
/// 对应 PCL-CE 的 <c>PCL.Core/Utils/StringStream.cs</c>（Apache-2.0），逐字采用其语义：
/// 构造时把字符串按指定编码转成字节，之后就是一个普通的只读内存流。
///
/// 用途：服务端响应里"要回一段文本"时，直接给 <c>HttpListenerResponse.OutputStream</c>
/// 写字节太啰嗦，而给一个 <see cref="Stream"/> 就能和"回一个文件流"走同一条路径。
/// </summary>
public sealed class StringStream : Stream
{
    private readonly MemoryStream _inner;

    /// <summary>用指定编码（默认 UTF-8）构造。</summary>
    public StringStream(string source, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bytes = (encoding ?? Encoding.UTF8).GetBytes(source);
        _inner = new MemoryStream(bytes);
    }

    /// <inheritdoc/>
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _inner.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // 只读流，无需实现。
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException("StringStream 是只读流。");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("StringStream 是只读流。");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
