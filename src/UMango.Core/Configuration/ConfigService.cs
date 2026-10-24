using UMango.Core.Configuration.Storage;

namespace UMango.Core.Configuration;

/// <summary>
/// 配置服务：把「多后端存取仓库」与「描述符驱动的设置服务」缝在一起。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/ConfigService.cs</c>（Apache-2.0）。
///
/// **与 PCL 的实质差异（四处）**：
/// <list type="number">
///   <item><b>不是静态类</b>。PCL 的 <c>ConfigService</c> 是纯静态单例
///         （<c>_Items</c> / <c>_KeySet</c> 都是静态字段），测试之间会互相污染。
///         UMango 做成实例类，测试可以随手造一个干净实例。</item>
///   <item><b>读取失败不退出程序</b>。PCL 在配置读坏时调用
///         <c>Lifecycle.ForceShutdown(-2)</c> —— 配置坏了就让整个程序退出。
///         UMango 改成：把坏文件挪到 <c>.failbackup</c>、用 <c>.bak</c> 恢复，
///         然后<b>继续启动</b>（配置没了可以重设，程序没了用户什么都没了）。</item>
///   <item><b>迁移选起点按路径最长</b>。PCL 的 <c>ConfigMigration.Migrate</c> 按迁移表
///         倒序取"第一个 From 存在的"，等于让<b>书写顺序</b>决定用哪个源文件 ——
///         老配置还在时也可能被跳过。UMango 选路径最长的起点（起点最早 = 配置最完整）。</item>
///   <item><b>事件总线与存储分离</b>。PCL 把事件挂在 <c>ConfigItem</c> 上，
///         UMango 把「值怎么存」与「谁被通知」分成两个可分别测试的部件。</item>
/// </list>
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly Dictionary<ConfigSource, ConfigStorage> _providers = [];
    private readonly ConfigEventHub _events = new();
    private readonly List<string> _recoveredFiles = [];

    /// <summary>构造。</summary>
    public ConfigService()
    {
        // 事件总线放在服务上，但仍然是可独立取用的对象（界面可以只拿它订阅）。
        Events = _events;
    }

    /// <summary>配置事件总线。</summary>
    public ConfigEventHub Events { get; }

    /// <summary>已注册的存取仓库来源。</summary>
    public IReadOnlyCollection<ConfigSource> Sources => _providers.Keys;

    /// <summary>启动时因为文件损坏而**被恢复**过的文件路径。</summary>
    public IReadOnlyList<string> RecoveredFiles => _recoveredFiles;

    /// <summary>注册某个来源的存取仓库。</summary>
    public void RegisterProvider(ConfigSource source, ConfigStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _providers[source] = storage;
    }

    /// <summary>取某个来源的存取仓库。</summary>
    /// <exception cref="InvalidOperationException">该来源尚未注册。</exception>
    public ConfigStorage GetProvider(ConfigSource source) =>
        _providers.TryGetValue(source, out var storage)
            ? storage
            : throw new InvalidOperationException($"未注册的配置来源：{source}");

    /// <summary>某个来源是否已注册。</summary>
    public bool HasProvider(ConfigSource source) => _providers.ContainsKey(source);

    /// <summary>
    /// 读一个值。来源优先取 <paramref name="source"/>，其次回退到
    /// <see cref="ConfigSource.Shared"/>（PCL 的语义：本地没有就用共享的）。
    /// </summary>
    public bool TryGetValue<T>(string key, out T? value, ConfigSource source = ConfigSource.Shared,
        object? argument = null)
    {
        if (_providers.TryGetValue(source, out var storage) &&
            storage.GetValue(key, out value, argument))
            return true;

        if (source != ConfigSource.Shared &&
            _providers.TryGetValue(ConfigSource.Shared, out var shared) &&
            shared.GetValue(key, out value, argument))
            return true;

        value = default;
        return false;
    }

    /// <summary>写一个值。</summary>
    public void SetValue<T>(string key, T value, ConfigSource source = ConfigSource.Shared,
        object? argument = null) =>
        GetProvider(source).SetValue(key, value, argument);

    /// <summary>删一个键。</summary>
    public void Delete(string key, ConfigSource source = ConfigSource.Shared, object? argument = null) =>
        GetProvider(source).Delete(key, argument);

    /// <summary>键是否存在（先查指定来源，再查共享）。</summary>
    public bool Exists(string key, ConfigSource source = ConfigSource.Shared, object? argument = null)
    {
        if (_providers.TryGetValue(source, out var storage) && storage.Exists(key, argument)) return true;
        return source != ConfigSource.Shared &&
               _providers.TryGetValue(ConfigSource.Shared, out var shared) &&
               shared.Exists(key, argument);
    }

    /// <summary>
    /// 启动配置文件初始化：文件损坏时把它挪到 <c>.failbackup</c>，
    /// 并尝试用 <c>.bak</c> 恢复 —— 而不是让程序退出。
    /// </summary>
    /// <param name="path">配置文件路径。</param>
    /// <param name="provider">用于验证文件是否可解析的 provider 工厂。
    /// 默认用 JSON（UMango 的共享配置就是 JSON）；YAML / INI 后端传自己的工厂。</param>
    /// <returns>恢复成功时返回 true。</returns>
    public bool TryInitializeFile(string path, Func<string, IKeyValueFileProvider>? provider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path)) return true;   // 没有文件不是错误

        try
        {
            // 真正打开一次，确认能解析。构造 provider 就会抛。
            var factory = provider ?? (p => new JsonFileProvider(p));
            _ = factory(path);
            return true;
        }
        catch (Exception)
        {
            // 走到这里说明文件坏了。
        }

        var backupPath = path + ".failbackup";
        var bakPath = path + ".bak";

        try
        {
            File.Move(path, backupPath, overwrite: true);
        }
        catch (Exception)
        {
            // 连挪都挪不动（文件被占用）—— 那就保留原样，让上层决定。
            return false;
        }

        _recoveredFiles.Add(backupPath);

        if (File.Exists(bakPath))
        {
            try
            {
                File.Copy(bakPath, path, overwrite: true);
                return true;
            }
            catch (Exception)
            {
                // 恢复副本也失败了 —— 至少原文件已经被挪走，用户不会丢数据。
            }
        }

        return false;
    }

    /// <summary>
    /// 跑一次迁移。目标文件已存在时<b>不迁移</b>（PCL 的语义）。
    /// </summary>
    public MigrationResult Migrate(string target, IEnumerable<ConfigMigration> migrations)
    {
        if (File.Exists(target)) return MigrationResult.NoPath();
        return ConfigMigrationEngine.Migrate(target, migrations, File.Exists);
    }

    /// <summary>停止所有存取仓库（落盘并释放资源）。</summary>
    public void Stop()
    {
        foreach (var storage in _providers.Values)
        {
            try { storage.Stop(); }
            catch (Exception) { /* 一个后端停不下来不该影响其它后端 */ }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _providers.Clear();
    }
}
