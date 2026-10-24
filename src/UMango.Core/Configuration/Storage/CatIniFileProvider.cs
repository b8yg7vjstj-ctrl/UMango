using System.Text;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// LTCat-style ini 键值文件：每行 <c>键:值</c>。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/CatIniFileProvider.cs</c>（Apache-2.0）。
///
/// **修掉的 PCL 原版缺陷**：原版读文件时
/// <c>var split = line.Split(':', 2); _dict[split[0]] = split[1];</c>
/// —— 一行里没有冒号时 <c>split[1]</c> 直接 <c>IndexOutOfRangeException</c>，
/// 整个配置文件读不出来（用户随手加一行注释就会让应用起不来）。
/// UMango 改成：没有冒号的行**跳过并记警告**，不致命的行不该让整个文件报废。
/// </summary>
public sealed class CatIniFileProvider : CommonFileProvider
{
    private readonly Dictionary<string, string> _dict = new(StringComparer.Ordinal);
    private readonly List<string> _skippedLines = [];

    /// <summary>构造。文件不存在时视为空。</summary>
    public CatIniFileProvider(string path) : base(path)
    {
        if (!File.Exists(path)) return;

        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var split = line.Split(':', 2);
            if (split.Length < 2)
            {
                // PCL 原版在这里抛 IndexOutOfRangeException —— 修掉。
                _skippedLines.Add($"第 {lineNumber} 行缺少 ':'，已忽略：{line}");
                continue;
            }

            _dict[split[0]] = split[1];
        }
    }

    /// <summary>读文件时被忽略的行（供日志/诊断用）。</summary>
    public IReadOnlyList<string> SkippedLines => _skippedLines;

    /// <summary>全部键。</summary>
    public IEnumerable<string> Keys => _dict.Keys;

    /// <inheritdoc />
    public override T Get<T>(string key)
    {
        if (!_dict.TryGetValue(key, out var value)) throw new KeyNotFoundException($"Not found: '{key}'");
        return ValueConverter.FromText<T>(value)
               ?? throw new InvalidDataException($"配置项 '{key}' 的值无法转换为 {typeof(T).Name}");
    }

    /// <inheritdoc />
    public override void Set<T>(string key, T value) => _dict[key] = ValueConverter.ToText(value);

    /// <inheritdoc />
    public override bool Exists(string key) => _dict.ContainsKey(key);

    /// <inheritdoc />
    public override void Remove(string key) => _dict.Remove(key);

    /// <inheritdoc />
    protected override void WriteToStream(Stream stream)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var (key, value) in _dict)
        {
            var keyStr = key.Replace("\r", " ").Replace("\n", " ");
            var valueStr = value.Replace("\r", " ").Replace("\n", " ");
            writer.WriteLine($"{keyStr}:{valueStr}");
        }
        writer.Flush();
    }
}
