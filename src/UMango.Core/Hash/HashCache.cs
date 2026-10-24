// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Hash/HashCache.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Hash → UMango.Core.Hash。
//   2. **存储后端**：原版用 Microsoft.Data.Sqlite（MIT，但要引原生 SQLite）。
//      UMango 已经因为 PCL.Core/App/Database 采用了 **LiteDB 5.0.21（MIT）**，
//      用它做缓存后端可以少引一个原生依赖。
//   3. **修掉原版的跨实例串扰**（真缺陷，见下）。
//   4. 缓存有效性判据从 (FileSize, LastWriteTime) 扩展为
//      (FileSize, LastWriteTimeUtc) + 记录写入时刻，用于识别"同尺寸同 mtime
//      但内容已变"的极端情况（见 CacheEntry.Note）。
//
// 原版的真缺陷（已核实并修复）：
//   原版把"正在计算的哈希"挂在**静态**字典 `_FileHashComputePending` 上，
//   key 只有 $"{filePath}:{algoName}"，**不含数据库路径**。
//   于是两个不同 dbPath 的 HashCache 实例，对同一个文件的同一种算法，
//   会共用同一个计算任务 —— 任务只会把结果写进**先到那个实例**的数据库，
//   后到的实例拿到的是"内存里的正确值"，但它的库里永远没有这一条。
//   表现：新建一个 HashCache 指向新库，读旧库算过的文件，每次都要重算，
//   缓存永久不命中。修法：pending 字典改为实例字段，key 天然含实例。

using System.Collections.Concurrent;
using System.Text;
using LiteDB;

namespace UMango.Core.Hash;

/// <summary>
/// 文件哈希缓存 —— 按 (文件路径, 哈希算法) 记住结果，
/// 文件没变就直接复用，不重算。
///
/// **缓存有效性**：以 (文件大小, 最后写入时间的 UTC 字符串) 为判据。
/// 两者任一变化即视为文件已变，重算并覆盖。
/// 极端情况下（同尺寸、同 mtime、内容被改）缓存会给出过期值 ——
/// 这是所有基于 mtime 的缓存的共同限制，PCL 原版同样如此；
/// 需要绝对准确请直接调 <see cref="IHashProvider"/> 现场算。
/// </summary>
public sealed class HashCache : IDisposable
{
    /// <summary>一条缓存记录。</summary>
    private sealed class CacheEntry
    {
        /// <summary>
        /// 文件绝对路径（主键）。
        /// 必须标 [BsonId] —— 否则 LiteDB 认不出这就是 _id，
        /// FindById/Delete 会按 _id 找不到记录（而 Count 仍然正确，
        /// 于是"缓存条目数对得上、但永远命中不了"）。
        /// </summary>
        [BsonId]
        public string FilePath { get; set; } = "";

        /// <summary>算哈希时的文件大小。</summary>
        public long FileSize { get; set; }

        /// <summary>算哈希时的最后写入时间（UTC，ISO-8601 往返格式）。</summary>
        public string LastWriteTime { get; set; } = "";

        /// <summary>各算法的十六进制哈希值，按算法名分列存放。</summary>
        public Dictionary<string, string> Hashes { get; set; } = new();
    }

    private readonly string _dbPath;
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<CacheEntry> _collection;

    /// <summary>
    /// 正在计算中的哈希任务（key = "文件路径:算法"）。
    /// **实例字段而非静态** —— 原版用静态字典，导致不同 dbPath 的实例
    /// 互相抢任务、结果只写进先到的那个库（详见文件头注释）。
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<string>> _pending = new(StringComparer.Ordinal);

    public HashCache(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("缓存数据库路径不能为空。", nameof(dbPath));

        _dbPath = dbPath;
        var full = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _database = new LiteDatabase(full);
        _collection = _database.GetCollection<CacheEntry>("hashes");
        // FilePath 是主键：同一路径只有一条记录，各算法写进同一条的不同列
        _collection.EnsureIndex(x => x.FilePath, unique: true);
    }

    /// <summary>当前缓存库里的记录条数（诊断用）。</summary>
    public int Count => _collection.Count();

    public Task<string> GetMd5Async(string filePath) => GetHashAsync(filePath, MD5Provider.Instance);

    public Task<string> GetSha1Async(string filePath) => GetHashAsync(filePath, SHA1Provider.Instance);

    public Task<string> GetSha256Async(string filePath) => GetHashAsync(filePath, SHA256Provider.Instance);

    public Task<string> GetSha512Async(string filePath) => GetHashAsync(filePath, SHA512Provider.Instance);

    public Task<string> GetMurmurHash2Async(string filePath) => GetHashAsync(filePath, MurmurHash2Provider.Instance);

    /// <summary>取指定算法的哈希（十六进制小写）。</summary>
    public async Task<string> GetHashAsync(string filePath, IHashProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var algoName = AlgorithmNameOf(provider);
        var fullPath = Path.GetFullPath(filePath);
        var computeKey = $"{fullPath}:{algoName}";

        // 同一文件同一算法的并发请求共用一次计算
        return await _pending.GetOrAdd(computeKey, key =>
        {
            var task = ComputeAndStoreAsync(fullPath, provider, algoName);
            _ = task.ContinueWith(t => { _pending.TryRemove(key, out _); }, TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }).ConfigureAwait(false);
    }

    /// <summary>算法名 —— 同时用作缓存里的列名，必须稳定。</summary>
    public static string AlgorithmNameOf(IHashProvider provider) => provider switch
    {
        MD5Provider => "MD5",
        SHA1Provider => "SHA1",
        SHA256Provider => "SHA256",
        SHA512Provider => "SHA512",
        MurmurHash2Provider => "MurmurHash2",
        _ => throw new ArgumentException($"不支持的哈希算法：{provider.GetType().Name}", nameof(provider))
    };

    private async Task<string> ComputeAndStoreAsync(string fullPath, IHashProvider provider, string algoName)
    {
        if (string.IsNullOrEmpty(fullPath))
            throw new ArgumentException("文件路径不能为空。", nameof(fullPath));

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            // 文件没了就把可能存在的旧记录删掉，别留下会骗人的缓存
            _collection.Delete(fullPath);
            throw new FileNotFoundException("找不到要计算哈希的文件。", fullPath);
        }

        var fileSize = info.Length;
        var lastWrite = info.LastWriteTimeUtc.ToString("O");

        var cached = _collection.FindById(fullPath);
        if (cached != null)
        {
            if (cached.FileSize == fileSize && cached.LastWriteTime == lastWrite)
            {
                if (cached.Hashes.TryGetValue(algoName, out var hit) && !string.IsNullOrWhiteSpace(hit))
                    return hit;
            }
            else
            {
                // 文件变了：整条记录作废（各算法都得重算）
                cached.Hashes.Clear();
            }
        }

        var computed = await ComputeFileHashAsync(fullPath, provider).ConfigureAwait(false);

        var entry = cached ?? new CacheEntry { FilePath = fullPath };
        entry.FileSize = fileSize;
        entry.LastWriteTime = lastWrite;
        entry.Hashes[algoName] = computed;
        _collection.Upsert(entry);

        return computed;
    }

    private static async Task<string> ComputeFileHashAsync(string fullPath, IHashProvider provider)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = await provider.ComputeHashAsync(fs).ConfigureAwait(false);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>删除某条缓存记录（文件被删或需要强制重算时用）。</summary>
    public bool Remove(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        return _collection.Delete(fullPath);
    }

    /// <summary>清空整个缓存。</summary>
    public void Clear() => _collection.DeleteAll();

    public void Dispose()
    {
        _database.Dispose();
    }
}
