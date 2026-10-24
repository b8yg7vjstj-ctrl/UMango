using System.Text;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// 键值文件的公共基类：负责"安全落盘"。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/CommonFileProvider.cs</c>（Apache-2.0）。
///
/// 落盘策略（与 PCL 一致）：先写临时文件并 <c>Flush(true)</c>，再原子替换目标文件，
/// 旧文件留一份 <c>.bak</c>。这样进程中途被杀也不会留下半截配置。
/// </summary>
public abstract class CommonFileProvider(string path) : IKeyValueFileProvider
{
    /// <inheritdoc />
    public string FilePath { get; set; } = path;

    /// <inheritdoc />
    public abstract T Get<T>(string key);

    /// <inheritdoc />
    public abstract void Set<T>(string key, T value);

    /// <inheritdoc />
    public abstract bool Exists(string key);

    /// <inheritdoc />
    public abstract void Remove(string key);

    /// <summary>把当前内容写到流里（由子类实现）。</summary>
    protected abstract void WriteToStream(Stream stream);

    /// <inheritdoc />
    public void Sync()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmpFile = $"{FilePath}.tmp{Random.Shared.Next(10000, 99999):00000}";
        var bakFile = $"{FilePath}.bak";

        using (var stream = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            WriteToStream(stream);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(FilePath)) File.Replace(tmpFile, FilePath, bakFile, ignoreMetadataErrors: true);
        else File.Move(tmpFile, FilePath);

        // PCL 留下 .bak；UMango 保留它（用户出问题时能手动回滚），但只留一份。
    }

    /// <summary>以 UTF-8（无 BOM）写文本。</summary>
    protected static void WriteText(Stream stream, string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }
}
