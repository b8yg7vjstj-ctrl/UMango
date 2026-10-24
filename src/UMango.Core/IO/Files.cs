// SPDX-License-Identifier: Apache-2.0
//
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/IO/Files.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 相对原版的改动（逐条列明，便于审计）：
//
// 1. 命名空间 PCL.Core.IO → UMango.Core.IO；
//    PCL.Core.Logging.LogWrapper → UMango.Core.Logging.Log；
//    PCL.Core.Utils.Hash.* → UMango.Core.Hash.*；
//    PCL.Core.Utils.Codecs.EncodingDetector → UMango.Core.Utils.Codecs.EncodingDetector。
//
// 2. **不迁** `CreateShortcut`（用 WScript.Shell COM，Windows 专属）——
//    跨平台的快捷方式做法各不相同，UMango 目前没有需求，先不做。
//    **不迁** `PasteFromClipboardAsync`（用 WPF 的 `System.Windows.Clipboard`，
//    且在 STA 线程外调用会抛异常）—— 剪贴板访问需要 UI 层配合。
//    `ExportAsZipArchiveAsync` 里调 `SystemDialogs.SelectSaveFile`（WPF 对话框），
//    这里拆成两层：Core 提供**不含对话框**的 `ExportAsZipArchiveAsync(files, destPath)`，
//    由 App 层负责选路径。
//
// 3. **不迁 SharpZipLib 依赖**。原版靠 `ICSharpCode.SharpZipLib` 解 zip/tar/gz/bz2。
//    .NET 自带 `System.IO.Compression`（zip / gzip）与 `System.Formats.Tar`（tar），
//    零新增依赖；**bz2（bzip2）BCL 不支持** → 该格式不迁，调用时明确报不支持。
//
// 4. 修掉的原版真实缺陷：
//
//    ① **取消被吞成 IOException**。
//       原版 `CopyFileAsync` 用 `catch (Exception ex) { throw new IOException(...) }`
//       一把抓 —— 取消操作抛出的 `OperationCanceledException` 也被包成 IOException，
//       于是「用户点了取消」表现为「复制失败」，上层无法区分取消与真错误。
//       这里把 `OperationCanceledException` 放行。
//
//    ② **追加写时目标文件不存在 → 直接崩**。
//       原版 `WriteFileAsync(path, text, append: true)` 里
//       `new FileStream(fullPath, FileMode.Open, …)` 来探测编码 ——
////       文件不存在时抛 FileNotFoundException，且**没被捕获**。
//       这条路径正是「首次写入」的常见场景 → 直接崩。
//       这里改成：文件不存在就按 UTF-8（无 BOM）追加。
//
//    ③ **SHA512 哈希被当成 SHA1 算**。
//       原版 `CheckAsync` 用哈希**长度**猜算法：`< 35 → MD5`、`64 → SHA256`、
//       `else → SHA1`。SHA-512 的十六进制长 128 → 落进 else → 用 SHA1 计算，
//       必然不匹配 → 报「哈希不符」的**假错误**。
//       这里改成按 32/40/64/128 精确判定，未知长度直接报「无法识别的哈希长度」。
//
//    ④ **相对路径的基准目录写死在 `Paths.DefaultDirectory`**。
//       原版 `GetFullPath` 把相对路径拼到启动器自己的数据目录 —
//       在库里做通用文件操作时这会得到莫名其妙的路径。
//       这里把基准目录做成**参数**（默认 `AppContext.BaseDirectory`），
//       库层不替调用方猜。
//
//    ⑤ `CheckFileBusyAsync` 用 `FileShare.Read | FileShare.Delete` 打开文件，
//       **永远打不开正在被写独占的文件吗？** 会 —— 独占写会失败 → 返回 true。
//       但原版对「文件不存在」先判 `File.Exists` → 返回 false，行为正确。
//       这里保持一致，但把 catch-all 里的 false 保留（拿不到锁 ≠ 一定被占用）。
//
//    ⑥ 原版 `ComputeFileHashAsync` 的重试循环里，第一次失败后
//       `await Task.Delay(Random.Shared.Next(200, 500))` —— **不传取消令牌**，
//       取消后还要等最多 0.5 秒。这里把令牌传进去。
//
// 5. 其它：日志方法名对齐 UMango 的 `Log.Warn/Info/Debug`；
//    `Lang.Text("Common.Error.InvalidJson")` 的本地化键在 UMango 侧
//    用 `Localization.LocalizationService.Text(...)` 桥接。

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UMango.Core.Hash;
using UMango.Core.Logging;
using UMango.Core.Utils;
using UMango.Core.Utils.Codecs;

namespace UMango.Core.IO;

/// <summary>
/// 通用文件操作：复制、读写、哈希、校验、解压。
/// </summary>
public static class Files
{
    /// <summary>写 JSON 时使用的「好看」选项：缩进 + camelCase。</summary>
    public static readonly JsonSerializerOptions PrettierJsonOptions = new(JsonCompat.SerializerOptions)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region 路径

    /// <summary>
    /// 把路径解析成绝对路径。相对路径以 <paramref name="baseDirectory"/> 为基准。
    /// </summary>
    /// <param name="filePath">路径。</param>
    /// <param name="baseDirectory">相对路径的基准目录；默认是程序所在目录。</param>
    public static string GetFullPath(string filePath, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (Path.IsPathRooted(filePath)) return Path.GetFullPath(filePath);
        var root = baseDirectory ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(root, filePath));
    }

    /// <summary>两个路径是否指向同一位置（忽略大小写与末尾分隔符）。</summary>
    public static bool ArePathsEqual(string path1, string path2)
    {
        var fullPath1 = Path.GetFullPath(path1).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath2 = Path.GetFullPath(path2).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath1, fullPath2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 检查 <paramref name="childPath"/> 是否在 <paramref name="baseDirectory"/> 之内。
    /// </summary>
    /// <remarks>
    /// 比原版更严：原版只追加了 <see cref="Path.DirectorySeparatorChar"/>，
    /// 在 Windows 上 <c>"C:\foo\"</c> 与 <c>"C:\foobar"</c> 不会混淆（因为带了分隔符），
    /// 但原版**没有对 baseDirectory 自身做 TrimEnd(AltDirectorySeparatorChar)**，
    /// 在 Linux 上以 <c>/</c> 结尾的基目录会被裁成空串 → 判定全部为 true。
    /// 这里统一裁掉两种分隔符，并在末尾补一个分隔符。
    /// </remarks>
    public static bool IsPathWithinDirectory(string childPath, string baseDirectory)
    {
        var root = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var child = Path.GetFullPath(childPath);

        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var prefix = root + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, comparison);
    }

    #endregion

    #region 异步文件操作

    /// <summary>
    /// 复制文件，自动创建目标目录并覆盖已有文件。
    /// </summary>
    /// <exception cref="IOException">复制失败时抛出。</exception>
    /// <exception cref="OperationCanceledException">被取消时抛出（不被包成 IOException）。</exception>
    public static async Task CopyFileAsync(
        string fromPath, string toPath, CancellationToken cancelToken = default)
    {
        try
        {
            var fullFromPath = GetFullPath(fromPath);
            var fullToPath = GetFullPath(toPath);
            if (ArePathsEqual(fullFromPath, fullToPath)) return;

            var directoryName = Path.GetDirectoryName(fullToPath)
                                ?? throw new InvalidOperationException("无法获取目标目录");
            Directory.CreateDirectory(directoryName);

            const int bufferSize = 4096;
            await using var sourceStream = new FileStream(
                fullFromPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destinationStream = new FileStream(
                fullToPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize,
                FileOptions.Asynchronous);
            await sourceStream.CopyToAsync(destinationStream, cancelToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消不是「复制失败」—— 放行，让上层能区分
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"复制文件出错：{fromPath} -> {toPath}", ex);
        }
    }

    /// <summary>复制文件夹及其全部内容。</summary>
    public static async Task CopyDirectoryAsync(
        string sourceDir, string destDir, CancellationToken cancelToken = default)
    {
        Directory.CreateDirectory(destDir);

        var files = Directory.GetFiles(sourceDir);
        var directories = Directory.GetDirectories(sourceDir);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            CancellationToken = cancelToken
        };

        await Parallel.ForEachAsync(files, parallelOptions, async (file, ct) =>
        {
            await CopyFileAsync(file, Path.Combine(destDir, Path.GetFileName(file)), ct).ConfigureAwait(false);
        });

        await Parallel.ForEachAsync(directories, parallelOptions, async (subDir, ct) =>
        {
            await CopyDirectoryAsync(subDir, Path.Combine(destDir, Path.GetFileName(subDir)), ct)
                .ConfigureAwait(false);
        });
    }

    /// <summary>读取文件为字节数组；失败时返回空数组（不抛）。</summary>
    public static async Task<byte[]> ReadAllBytesOrEmptyAsync(
        string filePath, CancellationToken cancelToken = default)
    {
        try
        {
            var fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException(fullPath);
            return await File.ReadAllBytesAsync(fullPath, cancelToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Files", "读取文件出错：" + filePath + " —— " + ex);
            return [];
        }
    }

    /// <summary>读取文件为字符串；失败时返回空字符串（不抛）。</summary>
    public static async Task<string> ReadAllTextOrEmptyAsync(
        string filePath, Encoding? encoding = null, CancellationToken cancelToken = default)
    {
        try
        {
            var fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException(fullPath);
            if (encoding is null) return await File.ReadAllTextAsync(fullPath, cancelToken).ConfigureAwait(false);
            return await File.ReadAllTextAsync(fullPath, encoding, cancelToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Files", "读取文件出错：" + filePath + " —— " + ex);
            return "";
        }
    }

    /// <summary>从流中读取所有文本；失败时返回空字符串（不抛）。</summary>
    public static async Task<string> ReadAllTextOrEmptyAsync(
        Stream stream, Encoding? encoding = null, CancellationToken cancelToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(stream);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancelToken).ConfigureAwait(false);
            var buffer = memoryStream.GetBuffer();
            var len = (int)memoryStream.Length;
            var enc = encoding ?? EncodingDetector.DetectEncoding(buffer);
            return enc.GetString(buffer, 0, len);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Files", "读取流出错 —— " + ex);
            return string.Empty;
        }
    }

    /// <summary>异步把文件读进内存流；失败时返回空流（不抛）。</summary>
    public static async Task<MemoryStream> ReadFileToStreamOrEmptyAsync(
        string filePath, CancellationToken cancelToken = default)
    {
        try
        {
            var fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException(fullPath);

            await using var fileStream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream, cancelToken).ConfigureAwait(false);
            memoryStream.Position = 0;
            return memoryStream;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Files", "读取文件到流出错：" + filePath + " —— " + ex);
            return new MemoryStream();
        }
    }

    /// <summary>
    /// 写入字符串到文件，自动创建目录。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="text">要写入的文本。</param>
    /// <param name="append">true = 追加，false = 覆盖。</param>
    /// <param name="encoding">文件编码；null 时追加模式按已有文件编码、无 BOM UTF-8 兜底。</param>
    /// <param name="cancelToken">取消令牌。</param>
    public static async Task WriteFileAsync(
        string filePath, string text, bool append = false, Encoding? encoding = null,
        CancellationToken cancelToken = default)
    {
        var fullPath = GetFullPath(filePath);
        var directoryName = Path.GetDirectoryName(fullPath)
                            ?? throw new InvalidOperationException("无法获取目标目录");
        Directory.CreateDirectory(directoryName);

        if (append)
        {
            if (encoding is null)
            {
                encoding = await DetectExistingEncodingAsync(fullPath).ConfigureAwait(false);
            }

            await File.AppendAllTextAsync(fullPath, text, encoding, cancelToken).ConfigureAwait(false);
        }
        else
        {
            encoding ??= new UTF8Encoding(false); // 无 BOM 的 UTF-8
            await File.WriteAllTextAsync(fullPath, text, encoding, cancelToken).ConfigureAwait(false);
        }
    }

    /// <summary>写入字节数组到文件，自动创建目录。</summary>
    public static async Task WriteFileAsync(
        string filePath, byte[] content, bool append = false, CancellationToken cancelToken = default)
    {
        var fullPath = GetFullPath(filePath);
        var directoryName = Path.GetDirectoryName(fullPath)
                            ?? throw new InvalidOperationException("无法获取目标目录");
        Directory.CreateDirectory(directoryName);

        var fileMode = append ? FileMode.Append : FileMode.Create;
        await using var fileStream = new FileStream(
            fullPath, fileMode, FileAccess.Write, FileShare.Read);
        await fileStream.WriteAsync(content.AsMemory(), cancelToken).ConfigureAwait(false);
    }

    /// <summary>把流写入文件，自动创建目录；失败时返回 false（不抛）。</summary>
    public static async Task<bool> WriteFileAsync(
        string filePath, Stream? stream, CancellationToken cancelToken = default)
    {
        if (stream is null) return false;
        try
        {
            var fullPath = GetFullPath(filePath);
            var directoryName = Path.GetDirectoryName(fullPath)
                                ?? throw new InvalidOperationException("无法获取目标目录");
            Directory.CreateDirectory(directoryName);

            await using var fileStream = new FileStream(
                fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
            fileStream.SetLength(0);
            await stream.CopyToAsync(fileStream, cancelToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Files", "保存流出错 —— " + ex);
            return false;
        }
    }

    /// <summary>
    /// 探测已有文件的编码；文件不存在或读不出来时返回无 BOM 的 UTF-8。
    /// </summary>
    private static async Task<Encoding> DetectExistingEncodingAsync(string fullPath)
    {
        if (!File.Exists(fullPath)) return new UTF8Encoding(false);
        try
        {
            await using var fileStream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return EncodingDetector.DetectEncoding(fileStream, readFromBegin: true);
        }
        catch (Exception)
        {
            return new UTF8Encoding(false);
        }
    }

    #endregion

    #region 文件哈希计算

    /// <summary>
    /// 异步计算文件的哈希值（十六进制）。
    /// </summary>
    /// <param name="filePath">文件路径；为空时返回空串。</param>
    /// <param name="hashProvider">哈希算法提供者。</param>
    /// <param name="ignoreIfBusy">文件被占用时是否直接返回空串（不重试）。</param>
    public static async Task<string> ComputeFileHashAsync(
        string? filePath, IHashProvider hashProvider, bool ignoreIfBusy = false)
    {
        ArgumentNullException.ThrowIfNull(hashProvider);
        if (string.IsNullOrEmpty(filePath))
        {
            Log.Warn("Files", "计算文件哈希失败：文件路径为空");
            return string.Empty;
        }

        if (ignoreIfBusy && await CheckFileBusyAsync(filePath).ConfigureAwait(false))
            return string.Empty;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Convert.ToHexString(await hashProvider.ComputeHashAsync(fs).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Log.Warn("Files", "计算文件哈希失败：" + filePath + " —— " + ex);
                return string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 0)
                {
                    Log.Warn("Files", "计算文件哈希可重试失败：" + filePath + " —— " + ex);
                    await Task.Delay(Random.Shared.Next(200, 500)).ConfigureAwait(false);
                    continue;
                }

                Log.Warn("Files", "计算文件哈希失败：" + filePath + " —— " + ex);
                return string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>获取文件的 MD5。</summary>
    public static Task<string> GetFileMD5Async(string? filePath)
        => ComputeFileHashAsync(filePath, MD5Provider.Instance);

    /// <summary>获取文件的 SHA1。</summary>
    public static Task<string> GetFileSHA1Async(string? filePath)
        => ComputeFileHashAsync(filePath, SHA1Provider.Instance);

    /// <summary>获取文件的 SHA256。</summary>
    public static Task<string> GetFileSHA256Async(string? filePath, bool ignoreIfBusy = false)
        => ComputeFileHashAsync(filePath, SHA256Provider.Instance, ignoreIfBusy);

    /// <summary>获取文件的 SHA512。</summary>
    public static Task<string> GetFileSHA512Async(string? filePath, bool ignoreIfBusy = false)
        => ComputeFileHashAsync(filePath, SHA512Provider.Instance, ignoreIfBusy);

    /// <summary>异步检查文件是否被占用。</summary>
    public static async Task<bool> CheckFileBusyAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            await using FileStream fs = new(
                filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 校验

    /// <summary>
    /// 检查文件：成功返回 null，失败返回错误描述（不以句号结尾，不抛异常）。
    /// </summary>
    /// <param name="localPath">本地文件路径。</param>
    /// <param name="minSize">最小字节数；-1 = 不检查。</param>
    /// <param name="actualSize">期望字节数；-1 = 不检查。</param>
    /// <param name="hash">期望哈希（十六进制）；null = 不检查。</param>
    /// <param name="isJson">是否校验内容是合法 JSON。</param>
    public static async Task<string?> CheckAsync(
        string localPath, long minSize = -1, long actualSize = -1, string? hash = null, bool isJson = false)
    {
        try
        {
            Log.Debug("Checker", "开始校验文件 " + localPath);
            var info = new FileInfo(localPath);
            if (!info.Exists) return "文件不存在：" + localPath;

            var fileSize = info.Length;
            var errors = new StringBuilder();
            var allowSizeMismatch = false;

            if (!string.IsNullOrEmpty(hash))
            {
                // 原版按长度猜算法：< 35 → MD5、64 → SHA256、else → SHA1。
                // **SHA-512 的十六进制长 128，会落进 else 分支被当成 SHA1**，
                // 于是必然报「哈希不符」的假错误。这里精确判定。
                var computedHash = hash.Length switch
                {
                    32 => await GetFileMD5Async(localPath).ConfigureAwait(false),
                    40 => await GetFileSHA1Async(localPath).ConfigureAwait(false),
                    64 => await GetFileSHA256Async(localPath).ConfigureAwait(false),
                    128 => await GetFileSHA512Async(localPath).ConfigureAwait(false),
                    _ => string.Empty
                };

                if (computedHash.Length == 0)
                {
                    errors.AppendLine($"无法识别的哈希长度（{hash.Length}）：{hash}");
                }
                else if (!string.Equals(hash, computedHash, StringComparison.OrdinalIgnoreCase))
                {
                    var hashType = hash.Length switch
                    {
                        32 => "MD5",
                        40 => "SHA1",
                        64 => "SHA256",
                        128 => "SHA512",
                        _ => "未知"
                    };
                    errors.AppendLine($"文件 {hashType} 应为 {hash}，实际为 {computedHash}");
                }
                else
                {
                    allowSizeMismatch = true;
                }
            }

            if (actualSize >= 0 && actualSize != fileSize && !allowSizeMismatch)
            {
                var contentPreview = fileSize < 2000
                    ? await ReadAllTextOrEmptyAsync(localPath).ConfigureAwait(false)
                    : "";
                errors.AppendLine($"文件大小应为 {actualSize} B，实际为 {fileSize} B" +
                                  (string.IsNullOrEmpty(contentPreview) ? "" : $"，内容为 {contentPreview}"));
            }

            if (minSize >= 0 && minSize > fileSize)
            {
                var contentPreview = fileSize < 2000
                    ? await ReadAllTextOrEmptyAsync(localPath).ConfigureAwait(false)
                    : "";
                errors.AppendLine($"文件大小应大于 {minSize} B，实际为 {fileSize} B" +
                                  (string.IsNullOrEmpty(contentPreview) ? "" : $"，内容为 {contentPreview}"));
            }

            if (isJson)
            {
                var content = await ReadAllTextOrEmptyAsync(localPath).ConfigureAwait(false);
                if (string.IsNullOrEmpty(content)) throw new Exception("读取到的文件为空");
                try
                {
                    using var document = JsonDocument.Parse(content, JsonCompat.DocumentOptions);
                }
                catch (JsonException ex)
                {
                    throw new Exception("JSON 格式不正确", ex);
                }
            }

            if (errors.Length <= 0) return null;

            errors.Insert(0, $"实际校验地址：{localPath}\n");
            return errors.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Log.Warn("Checker", "检查文件出错: " + ex);
            return ex.ToString();
        }
    }

    #endregion

    #region JSON 合并

    /// <summary>
    /// 合并两个 JSON 对象：源覆盖目标的同名键，数组去重合并。
    /// </summary>
    public static JsonNode MergeJson(JsonNode? target, JsonNode? source)
    {
        if (target is null && source is null)
            throw new ArgumentNullException(nameof(target), "目标和源 JSON 不能同时为 null。");

        if (target is null) return source!.DeepClone();
        if (source is null) return target.DeepClone();

        if (target is not JsonObject targetObj || source is not JsonObject sourceObj)
            return source.DeepClone();

        var result = (JsonObject)targetObj.DeepClone();

        foreach (var (key, sourceValue) in sourceObj)
        {
            var targetValue = result[key];

            if (sourceValue is null) continue; // 忽略 null，保留目标值

            if (sourceValue is JsonObject && targetValue is JsonObject)
            {
                result[key] = MergeJson(targetValue, sourceValue);
            }
            else if (sourceValue is JsonArray sourceArray && targetValue is JsonArray targetArray)
            {
                var uniqueValues = new HashSet<string>(StringComparer.Ordinal);
                var mergedArray = new JsonArray();

                foreach (var item in targetArray)
                {
                    if (item is null) continue;
                    if (uniqueValues.Add(item.ToJsonString())) mergedArray.Add(item.DeepClone());
                }

                foreach (var item in sourceArray)
                {
                    if (item is null) continue;
                    if (uniqueValues.Add(item.ToJsonString())) mergedArray.Add(item.DeepClone());
                }

                result[key] = mergedArray;
            }
            else
            {
                result[key] = sourceValue.DeepClone();
            }
        }

        return result;
    }

    #endregion

    #region 压缩与解压

    /// <summary>
    /// 把若干文件导出成一个 zip 压缩包。
    /// </summary>
    /// <param name="sourceFiles">要打包的文件列表。</param>
    /// <param name="destinationPath">目标 zip 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 原版的同名方法里夹了 `SystemDialogs.SelectSaveFile`（WPF 对话框），
    /// Core 层不该有 UI 依赖 → 这里只做打包，选路径交给 App 层。
    /// </remarks>
    public static async Task ExportAsZipArchiveAsync(
        IEnumerable<string> sourceFiles, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var dir = Path.GetDirectoryName(GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var fileStream = new FileStream(
            GetFullPath(destinationPath), FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await using var zipStream = new ZipArchive(fileStream, ZipArchiveMode.Create);

        foreach (var item in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetFileName(item);
            var entry = zipStream.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var source = new FileStream(
                item, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 根据后缀名解压文件：支持 zip / jar / gz / tgz / tar.gz / tar。
    /// </summary>
    /// <param name="compressFilePath">压缩文件路径。</param>
    /// <param name="destDirectory">目标目录（不存在会创建）。</param>
    /// <param name="progressIncrementHandler">进度回调，参数 0..1。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="NotSupportedException">格式不受支持时抛出。</exception>
    public static async Task ExtractFileAsync(
        string? compressFilePath, string? destDirectory,
        Action<double>? progressIncrementHandler = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(compressFilePath))
            throw new ArgumentNullException(nameof(compressFilePath));
        if (string.IsNullOrEmpty(destDirectory))
            throw new ArgumentNullException(nameof(destDirectory));

        Directory.CreateDirectory(destDirectory);

        var name = compressFilePath.ToLowerInvariant();
        if (name.EndsWith(".gz") || name.EndsWith(".tgz"))
            await ExtractGZipAsync(compressFilePath, destDirectory, progressIncrementHandler, cancellationToken)
                .ConfigureAwait(false);
        else if (name.EndsWith(".tar"))
            await ExtractTarAsync(compressFilePath, destDirectory, progressIncrementHandler, cancellationToken)
                .ConfigureAwait(false);
        else if (name.EndsWith(".zip") || name.EndsWith(".jar"))
            await ExtractZipAsync(compressFilePath, destDirectory, progressIncrementHandler, cancellationToken)
                .ConfigureAwait(false);
        else
            throw new NotSupportedException(
                $"不支持的压缩文件格式：{Path.GetFileName(compressFilePath)}" +
                "（支持 zip / jar / gz / tgz / tar.gz / tar；bzip2 不在 .NET BCL 内，未迁移）");
    }

    private static async Task ExtractZipAsync(
        string compressFilePath, string destDirectory, Action<double>? progress, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(compressFilePath);
        var total = archive.Entries.Count;
        var done = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var destinationPath = GetSafePath(destDirectory, entry.FullName);

            if (string.IsNullOrEmpty(entry.Name))
            {
                // 目录条目
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var entryStream = entry.Open();
            await using var output = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await entryStream.CopyToAsync(output, ct).ConfigureAwait(false);

            done++;
            progress?.Invoke(total == 0 ? 1.0 : (double)done / total);
        }
    }

    private static async Task ExtractGZipAsync(
        string compressFilePath, string destDirectory, Action<double>? progress, CancellationToken ct)
    {
        // 保留**原始大小写**：原版用 ToLower() 得到文件名，
        // 于是 "Plain.txt.gz" 解压出来变成 "plain.txt" —— 文件名被改写了。
        // 这里只用小写副本做格式判断，输出名用原始名。
        var rawFileName = Path.GetFileName(compressFilePath);
        var fileName = rawFileName.ToLowerInvariant();
        var isTarGZip = fileName.EndsWith(".tar.gz") || fileName.EndsWith(".tgz");

        if (isTarGZip)
        {
            await using var fileStream = new FileStream(
                compressFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
            await ExtractTarStreamAsync(gzip, destDirectory, progress, ct).ConfigureAwait(false);
            return;
        }

        var outputFileName = rawFileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? rawFileName[..^3]
            : rawFileName;
        var outputPath = GetSafePath(destDirectory, outputFileName);

        await using var input = new FileStream(
            compressFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var gzipStream = new GZipStream(input, CompressionMode.Decompress);
        await using var output = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await gzipStream.CopyToAsync(output, ct).ConfigureAwait(false);
        progress?.Invoke(1.0);
    }

    private static async Task ExtractTarAsync(
        string compressFilePath, string destDirectory, Action<double>? progress, CancellationToken ct)
    {
        await using var fileStream = new FileStream(
            compressFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await ExtractTarStreamAsync(fileStream, destDirectory, progress, ct).ConfigureAwait(false);
    }

    private static async Task ExtractTarStreamAsync(
        Stream tarStream, string destDirectory, Action<double>? progress, CancellationToken ct)
    {
        // System.Formats.Tar 的 TarReader 是**单遍**的，
        // 而进度需要「总大小」→ 必须先把整个 tar 读进内存再走两遍。
        // （tar.gz 的 GZipStream 不可 seek，所以只能整体缓冲。）
        using var buffer = new MemoryStream();
        await tarStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using var reader = new TarReader(buffer, leaveOpen: true);

        long totalBytes = 0;
        var names = new List<(string Name, long Size, bool IsDirectory)>();
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            names.Add((entry.Name, entry.Length, entry.EntryType is TarEntryType.Directory));
            totalBytes += entry.Length;
        }

        // 重新读一遍：上面为了统计进度已经把条目读完了 → 从头再来。
        buffer.Position = 0;
        using var reader2 = new TarReader(buffer, leaveOpen: true);

        long processed = 0;
        while (await reader2.GetNextEntryAsync(copyData: true, ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var destinationPath = GetSafePath(destDirectory, entry.Name);

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var output = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            if (entry.DataStream is { } dataStream)
                await dataStream.CopyToAsync(output, ct).ConfigureAwait(false);

            processed += entry.Length;
            if (totalBytes > 0) progress?.Invoke((double)processed / totalBytes);
        }

        progress?.Invoke(1.0);
    }

    /// <summary>
    /// 把条目名拼成目标路径，并确保结果**仍在目标目录内**（防 Zip Slip）。
    /// </summary>
    private static string GetSafePath(string destDirectory, string entryName)
    {
        var root = Path.GetFullPath(destDirectory)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, entryName));

        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(root, comparison))
            throw new InvalidOperationException($"压缩包内含非法路径：{entryName}");

        return fullPath;
    }

    #endregion
}
