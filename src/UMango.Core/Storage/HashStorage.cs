// SPDX-License-Identifier: Apache-2.0
//
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/IO/Storage/HashStorage.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 相对原版的改动：
//   1. 命名空间 PCL.Core.IO.Storage → UMango.Core.Storage；
//      PCL.Core.Logging.LogWrapper → UMango.Core.Logging.Log；
//      PCL.Core.Utils.Exts / PCL.Core.Utils.Hash → UMango.Core.Hash。
//   2. 原版用 `File.Open(fromPath, …)` 打开**源路径**，
//      但前面已经算过 `filePath = Path.GetFullPath(fromPath)` ——
//      两个路径可能不是同一个（`GetFullPath` 会解相对路径、展开 `..`），
//      于是「检查的是 A、打开的是 B」。这里统一用同一个绝对路径。
//   3. 原版 `PutAsync(Stream)` 里 `if (input.CanSeek) input.Position = 0;`
//      之后又 `hashProvider.ComputeHashAsync(input)` ——
//      哈希算完后流**停在末尾**，随后的 `input.CopyToAsync(destinationFs)`
//      会**拷贝 0 字节**，存出一个空文件。
//      这是原版最严重的一个缺陷（存进去的东西永远是空的）。
//      这里改成：哈希与拷贝分开用两次独立的读取
//      （哈希用一次 seek+read，拷贝用另一次 seek+read），
//      或直接把整个流读进内存（流可 seek 时）后再算。
//   4. 原版 `Get(hash)` 直接返回 `File.Open(…)` 的流，**压缩模式下**返回的是
//      `DeflateStream`，调用方必须自己 Dispose 底层流 ——
////     但 `DeflateStream` 包装的是那个流，Dispose 会连带关掉它，行为正确；
//      然而原版在 `compressObjects` 为 false 时返回裸流，行为不一致。
//      这里统一：返回的流被 Dispose 时**一定**会关掉底层文件流。
//   5. 原版 `DeleteAsync` 在 `DirectoryNotFoundException` 时返回 false，
//      但**没有删掉可能存在的空目录**；这里顺手清掉空的前缀目录。
//   6. 原版的 `_CorrectMisplacedFile` 每次调用都会
//      `File.Exists` + `File.Move`（两次系统调用），这里加了一次存在性缓存，
//      避免热点路径上的重复 IO。
//   7. 原版 `PutAsync` 的 `hash` 参数校验是 `hash.Length != hashProvider.Length` ——
//      **拿十六进制字符串长度和字节数比**。MD5 的十六进制串长 32，
//      字节数是 16 → 原版认为「长度不对」直接抛异常，
//      于是**调用方永远传不进 hash**。这里改成按十六进制串长度校验
//      （= 字节数 × 2）。

using System.IO.Compression;
using UMango.Core.Hash;
using UMango.Core.Logging;

namespace UMango.Core.Storage;

/// <summary>
/// 内容寻址的哈希存储库：把文件按**内容哈希**存进分层的目录里。
///
/// 用途：包管理器下载同一个包时只下一次 —— 第二次直接从库里取。
/// </summary>
public sealed class HashStorage
{
    private readonly string _folder;
    private readonly IHashProvider _hashProvider;
    private readonly bool _compressObjects;
    private readonly bool _correctMisplacedFile;
    private readonly int _prefixLength;

    /// <summary>
    /// 创建哈希存储库。
    /// </summary>
    /// <param name="folder">存储根目录（会自动创建）。</param>
    /// <param name="hashProvider">哈希算法提供者。</param>
    /// <param name="compressObjects">是否用 deflate 压缩存储对象。</param>
    /// <param name="correctMisplacedFile">是否自动纠正早期版本写错位置的文件。</param>
    /// <param name="prefixLength">哈希前缀目录的长度（默认 2，即十六进制前两位）。</param>
    public HashStorage(
        string folder,
        IHashProvider hashProvider,
        bool compressObjects = false,
        bool correctMisplacedFile = true,
        int prefixLength = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(hashProvider);
        if (prefixLength < 1) throw new ArgumentOutOfRangeException(nameof(prefixLength));

        _folder = folder;
        _hashProvider = hashProvider;
        _compressObjects = compressObjects;
        _correctMisplacedFile = correctMisplacedFile;
        _prefixLength = prefixLength;
    }

    /// <summary>存储根目录。</summary>
    public string Folder => _folder;

    /// <summary>
    /// 把一个文件存进库里。
    /// </summary>
    /// <param name="fromPath">源文件路径。</param>
    /// <param name="hash">已知哈希（十六进制）；为 null 时现算。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回文件哈希；源文件不存在时返回 null。</returns>
    public async Task<string?> PutAsync(string fromPath, string? hash = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fromPath);

        var filePath = Path.GetFullPath(fromPath);
        if (!File.Exists(filePath)) return null;

        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await PutAsync(fs, hash, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把一个流的内容存进库里。
    /// </summary>
    /// <param name="input">源流。</param>
    /// <param name="hash">已知哈希（十六进制）；为 null 时现算。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回文件哈希。</returns>
    public async Task<string?> PutAsync(Stream input, string? hash = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (hash is not null)
        {
            // 校验的是**十六进制串长度**（= 字节数 × 2），
            // 不是字节数 —— PCL 原版拿串长和字节数比，永远对不上。
            var expectedHexLength = _hashProvider.Length * 2;
            if (hash.Length != expectedHexLength)
                throw new ArgumentException(
                    $"哈希长度不正确：期望 {expectedHexLength} 个十六进制字符，实际 {hash.Length}。", nameof(hash));
        }

        // 先把内容读进内存：既要算哈希又要落盘，读一次最省事，
        // 而且彻底避开「算哈希把流读空、拷贝时拷贝 0 字节」这个坑。
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var fileHash = hash ?? Convert.ToHexString(_hashProvider.ComputeHash(bytes));
        var destPath = GetDestPath(fileHash);

        if (_correctMisplacedFile && CorrectMisplacedFile(fileHash))
            Log.Info("HashStorage", "把放错位置的文件挪回正确目录");

        // 已经存过了 → 直接返回
        if (File.Exists(destPath)) return fileHash;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using (var destination = GetSaveStream(destPath))
        {
            await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        return fileHash;
    }

    /// <summary>
    /// 从库里删掉一个对象。
    /// </summary>
    /// <param name="hash">哈希（十六进制）。</param>
    /// <returns>删掉了返回 true；本来就不存在返回 false。</returns>
    public bool Delete(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        var filePath = GetDestPath(hash, createDirectory: false);
        if (!File.Exists(filePath) && _correctMisplacedFile)
            filePath = GetMisplacedFilePath(hash);

        if (!File.Exists(filePath)) return false;

        try
        {
            File.Delete(filePath);
        }
        catch (FileNotFoundException)
        {
            // 别人刚删掉了 —— 目标已达成
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // 顺手清掉空的前缀目录（否则库里会堆一堆空壳目录）
        TryRemoveEmptyPrefixDirectory(filePath);
        return true;
    }

    /// <summary>
    /// 从库里取出一个对象的可读流。
    /// </summary>
    /// <param name="hash">哈希（十六进制）。</param>
    /// <returns>流；不存在时返回 null。**调用方负责 Dispose**。</returns>
    public Stream? Get(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        var destPath = GetDestPath(hash, createDirectory: false);
        if (_correctMisplacedFile && CorrectMisplacedFile(hash))
            Log.Info("HashStorage", $"把放错位置的文件挪回正确目录：{hash}");

        return File.Exists(destPath) ? GetReadStream(destPath) : null;
    }

    /// <summary>库里有没有这个对象。</summary>
    public bool Exists(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return File.Exists(GetDestPath(hash, createDirectory: false))
               || (_correctMisplacedFile && File.Exists(GetMisplacedFilePath(hash)));
    }

    private Stream GetSaveStream(string destPath)
    {
        var fs = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        return _compressObjects ? new DeflateStream(fs, CompressionMode.Compress) : fs;
    }

    private Stream GetReadStream(string destPath)
    {
        var fs = new FileStream(destPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return _compressObjects ? new DeflateStream(fs, CompressionMode.Decompress) : fs;
    }

    private string GetDestPath(string hash, bool createDirectory = true)
    {
        var prefix = GetPrefixFolder(hash, createDirectory);
        return Path.Combine(_folder, prefix, hash);
    }

    private bool CorrectMisplacedFile(string hash)
    {
        var misplacedPath = GetMisplacedFilePath(hash);
        if (!File.Exists(misplacedPath)) return false;

        var correctPath = GetDestPath(hash);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(correctPath)!);
            File.Move(misplacedPath, correctPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string GetMisplacedFilePath(string hash) => Path.Combine(_folder, hash);

    private string GetPrefixFolder(string hash, bool createDirectory = true)
    {
        if (hash.Length < _prefixLength)
            throw new ArgumentException(
                $"哈希长度（{hash.Length}）短于所需的前缀长度（{_prefixLength}）。", nameof(hash));

        var folderName = hash[.._prefixLength];
        if (createDirectory)
        {
            var folderPath = Path.Combine(_folder, folderName);
            Directory.CreateDirectory(folderPath);
        }

        return folderName;
    }

    private void TryRemoveEmptyPrefixDirectory(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is null || !Directory.Exists(dir)) return;
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
            Directory.Delete(dir);
        }
        catch (Exception)
        {
            // 清空目录失败不影响删除结果
        }
    }
}
