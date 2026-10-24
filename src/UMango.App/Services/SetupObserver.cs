using UMango.Core.Configuration;
using UMango.Core.Download.Resources;
using UMango.Core.Network;

namespace UMango.App.Services;

/// <summary>
/// 窗口外观的应用目标（由 <see cref="MainWindow"/> 实现）。
///
/// <para>为什么要有这个接口：设置观察者要能改窗口属性，但
/// <c>MainWindow</c> 是 WPF 类型，App 层之外的测试宿主（macOS 上跑单测）加载
/// PresentationFramework 会崩。把"要改什么"抽成接口后，观察者逻辑可以在
/// 无 WPF 的环境里被完整覆盖 —— 只要给一个假的实现即可。</para>
/// </summary>
public interface IWindowAppearanceTarget
{
    /// <summary>应用窗口不透明度（0~1）。</summary>
    void ApplyWindowOpacity(double opacity);

    /// <summary>锁定或解锁窗口大小。</summary>
    void ApplyWindowSizeLock(bool locked);

    /// <summary>应用界面字体（留空 = 恢复主题默认）。</summary>
    void ApplyUiFont(string? fontFamily);

    /// <summary>应用标题栏文字（留空 = 恢复应用名）。</summary>
    void ApplyTitleBarText(string? text);

    /// <summary>标题栏是否左对齐。</summary>
    void ApplyTitleBarAlignment(bool leftAligned);
}

/// <summary>
/// 配置观察者（对应 PCL-CE 的 <c>ModSetup.cs</c> 配置观察者机制）。
///
/// <para>它做两件事：</para>
/// <list type="number">
///   <item><b>启动时</b>把当前所有相关设置应用一遍（<see cref="ApplyAll"/>）；</item>
///   <item><b>运行中</b>某个设置变了就重新应用那一条（<see cref="OnSettingsChanged"/>）。</item>
/// </list>
///
/// <para>与 PCL 的差异（有意为之）：</para>
/// <list type="bullet">
///   <item>PCL 的应用方法直接改 WPF 全局静态状态，不可测；
///         这里把"该改什么"抽成 <see cref="SetupApplier"/> + 接口，<b>可以单测</b>。</item>
///   <item>PCL 的 <c>ApplyAll()</c> 手工列举；UMango 从登记表按注册顺序自动枚举 ——
///         少写一项不会静默漏掉。</item>
/// </list>
/// </summary>
public sealed class SetupObserver : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SetupApplier _applier = new();
    private readonly IWindowAppearanceTarget? _window;
    private readonly Action<string>? _onError;
    private bool _disposed;

    /// <summary>已登记的配置键（按注册顺序）。</summary>
    public IReadOnlyList<string> Keys => _applier.Keys;

    /// <summary>
    /// 构造观察者并登记全部应用动作。
    /// </summary>
    /// <param name="settings">设置服务（唯一权威）。</param>
    /// <param name="window">窗口外观应用目标；为 null 时跳过窗口相关项（无头宿主）。</param>
    /// <param name="proxyManager">代理管理器；为 null 时跳过代理相关项。</param>
    /// <param name="onError">某个键的应用动作抛异常时的回调（通常是记日志）。</param>
    public SetupObserver(
        SettingsService settings,
        IWindowAppearanceTarget? window = null,
        ProxyManager? proxyManager = null,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _window = window;
        _onError = onError;
        _proxyManager = proxyManager;

        RegisterWindowKeys();
        RegisterDownloadKeys();
        RegisterProxyKeys();
    }

    private readonly ProxyManager? _proxyManager;

    // ---------------- 窗口外观 ----------------

    private void RegisterWindowKeys()
    {
        _applier.Register(UiSettings.WindowOpacityKey, v =>
        {
            var permille = ToInt(v, 1000);
            _window?.ApplyWindowOpacity(Math.Clamp(permille / 1000.0, 0.2, 1.0));
        });

        _applier.Register(UiSettings.LockWindowSizeKey, v =>
        {
            _window?.ApplyWindowSizeLock(ToBool(v));
        });

        _applier.Register(UiSettings.FontKey, v =>
        {
            _window?.ApplyUiFont(ToStringOrNull(v));
        });

        _applier.Register(UiSettings.TitleBarTextKey, v =>
        {
            _window?.ApplyTitleBarText(ToStringOrNull(v));
        });

        _applier.Register(UiSettings.TitleBarLeftAlignKey, v =>
        {
            _window?.ApplyTitleBarAlignment(ToBool(v));
        });
    }

    // ---------------- 下载限额 ----------------

    private void RegisterDownloadKeys()
    {
        _applier.Register(UiSettings.DownloadThreadLimitKey, v =>
        {
            DownloadResources.Current.MaxConnections = Math.Clamp(ToInt(v, 8), 1, 64);
        });

        _applier.Register(UiSettings.DownloadFileConnectionLimitKey, v =>
        {
            DownloadResources.Current.MaxConnectionsPerHost = Math.Clamp(ToInt(v, 4), 1, 64);
        });

        _applier.Register(UiSettings.DownloadSpeedLimitKey, v =>
        {
            // 设置项的单位是 KB/秒，资源协调器用的是字节/秒。
            var kbPerSecond = Math.Max(0, ToInt(v, 0));
            DownloadResources.Current.SpeedLimitBytesPerSecond = kbPerSecond * 1024L;
        });
    }

    // ---------------- 代理 ----------------

    private void RegisterProxyKeys()
    {
        _applier.Register(UiSettings.ProxyModeKey, v =>
        {
            if (_proxyManager is null) return;
            _proxyManager.Mode = ParseProxyMode(ToStringOrNull(v));
            if (_proxyManager.Mode == ProxyMode.SystemProxy)
            {
                _proxyManager.RefreshSystemProxy();
            }
        });

        _applier.Register(UiSettings.ProxyAddressKey, v =>
        {
            if (_proxyManager is null) return;
            var raw = ToStringOrNull(v);
            if (string.IsNullOrWhiteSpace(raw))
            {
                _proxyManager.CustomProxyAddress = null;
                return;
            }

            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                _proxyManager.CustomProxyAddress = uri;
            }
        });

        _applier.Register(UiSettings.ProxyUsernameKey, v =>
        {
            if (_proxyManager is null) return;
            _proxyManager.CustomProxyCredentials = BuildCredentials(
                ToStringOrNull(v), ToStringOrNull(_settings.Get<string>(UiSettings.ProxyPasswordKey)));
        });

        _applier.Register(UiSettings.ProxyPasswordKey, v =>
        {
            if (_proxyManager is null) return;
            _proxyManager.CustomProxyCredentials = BuildCredentials(
                ToStringOrNull(_settings.Get<string>(UiSettings.ProxyUsernameKey)), ToStringOrNull(v));
        });
    }

    private static System.Net.NetworkCredential? BuildCredentials(string? user, string? password)
    {
        if (string.IsNullOrEmpty(user)) return null;
        return new System.Net.NetworkCredential(user, password ?? string.Empty);
    }

    /// <summary>把模式字符串解析成 <see cref="ProxyMode"/>（解析不到 = 不用代理）。</summary>
    public static ProxyMode ParseProxyMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "system" => ProxyMode.SystemProxy,
        "custom" => ProxyMode.CustomProxy,
        _ => ProxyMode.NoProxy,
    };

    // ---------------- 应用 ----------------

    /// <summary>把某个键的当前值应用出去。</summary>
    public bool Apply(string key) => ApplySafely(key, () => _applier.Apply(key, _settings.Get<object?>(key)));

    /// <summary>启动时应用全部已登记的设置。</summary>
    public int ApplyAll()
    {
        var applied = 0;
        foreach (var key in _applier.Keys)
        {
            if (ApplySafely(key, () => _applier.Apply(key, _settings.Get<object?>(key)))) applied++;
        }

        return applied;
    }

    /// <summary>设置变更回调：某个键变了就重新应用那一条。</summary>
    public void OnSettingsChanged(string key)
    {
        if (!_applier.Contains(key)) return;
        ApplySafely(key, () => _applier.Apply(key, _settings.Get<object?>(key)));
    }

    /// <summary>
    /// 包一层 try/catch：某个设置的应用动作抛异常时**记下来但不中断其它设置**。
    ///
    /// <para>与 <see cref="SetupApplier"/> 的裸行为不同是有意的：
    /// 启动时逐条应用，一个设置坏了不应该让后面所有设置都失效。
    /// 但也**不静默** —— 通过 <c>onError</c> 回调把问题交出去。</para>
    /// </summary>
    private bool ApplySafely(string key, Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"应用设置「{key}」失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>订阅设置变更（幂等：重复调用不会重复订阅）。</summary>
    private bool _subscribed;

    /// <summary>开始监听设置变更。</summary>
    public void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _settings.Changed += OnSettingsChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subscribed)
        {
            _settings.Changed -= OnSettingsChanged;
            _subscribed = false;
        }
    }

    // ---------------- 取值辅助 ----------------

    private static bool ToBool(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var parsed) && parsed,
        int i => i != 0,
        _ => false,
    };

    private static int ToInt(object? value, int fallback) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        float f => (int)f,
        string s => int.TryParse(s, out var parsed) ? parsed : fallback,
        _ => fallback,
    };

    private static string? ToStringOrNull(object? value)
    {
        var text = value as string ?? value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
