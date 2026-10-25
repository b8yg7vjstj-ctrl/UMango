using UMango.Core.UI.Homepage;
using UMango.Shell.Services;

namespace UMango.Shell.Services;

/// <summary>
/// 主页清单的 App 侧接入点。
///
/// **为什么 Core 里的 <see cref="HomepageService"/> 不够用**：
/// Core 只认得「卡片」与「动作名」，但**动作要做什么**是界面层的事
/// （跳页、开浏览器、刷新数据），Core 不该知道。
///
/// 这里的分工：
///   · Core：解析、校验、白名单判定（纯数据，无 UI 依赖，macOS 上可测）；
///   · App：动作实现（跳页 / 打开链接 / 触发刷新）与界面呈现。
///
/// **安全边界仍然在 Core 那一侧**：清单里写的动作名必须先在这里注册过，
/// 否则解析阶段就把它丢了 —— 攻击者不能靠清单引入新的动作。
/// </summary>
public sealed class HomepageHost
{
    private readonly Dictionary<string, Action<string?>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>主页清单服务。</summary>
    public HomepageService Service { get; }

    /// <summary>清单变化时的回调（界面用它刷新卡片）。</summary>
    public event Action<HomepageParseResult>? ManifestChanged;

    /// <summary>创建接入点。</summary>
    /// <param name="manifestPath">清单路径；为 null 时用用户配置目录下的默认位置。</param>
    public HomepageHost(string? manifestPath = null)
    {
        // **动作白名单必须和 Service 用同一个注册表**：
        // 解析阶段靠 `Actions.IsRegistered` 判断「清单里的动作名是否被允许」，
        // 如果 Service 拿的是另一个（空的）注册表，所有卡片都会因为动作未注册被丢掉。
        var actions = new HomepageActionRegistry()
            .Register(new HomepageActionDescriptor("open-downloads", "打开下载页"))
            .Register(new HomepageActionDescriptor("open-settings", "打开设置页"))
            .Register(new HomepageActionDescriptor("open-logs", "打开日志页"))
            .Register(new HomepageActionDescriptor("open-tools", "打开工具页"))
            .Register(new HomepageActionDescriptor("refresh-sources", "重新探测包源", requiresConfirmation: false));

        Service = new HomepageService(manifestPath: manifestPath, actions: actions);

        // 动作实现由 App 层注册（Core 只认名字）。
        _handlers["open-downloads"] = _ => Navigate("downloads");
        _handlers["open-settings"] = _ => Navigate("settings");
        _handlers["open-logs"] = _ => Navigate("logs");
        _handlers["open-tools"] = _ => Navigate("tools");
        _handlers["refresh-sources"] = _ => RefreshRequested?.Invoke();
    }

    /// <summary>请求跳到某个页面（由 MainWindow 订阅处理）。</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>请求刷新包源（由主页订阅处理）。</summary>
    public event Action? RefreshRequested;

    /// <summary>清单路径（供界面显示「清单在哪儿」）。</summary>
    public string ManifestPath => Service.ManifestPath;

    /// <summary>读取清单（清单不存在时返回空清单，不是错误）。</summary>
    public HomepageParseResult Load() => Service.Load();

    /// <summary>开始监听清单变化（热更新）。</summary>
    public void StartWatching()
    {
        Service.Watch(result => ManifestChanged?.Invoke(result));
    }

    /// <summary>停止监听。</summary>
    public void StopWatching() => Service.StopWatching();

    /// <summary>执行一个已注册动作；未注册则返回 false（不会执行任何东西）。</summary>
    public bool InvokeAction(string name, string? argument)
    {
        if (!_handlers.TryGetValue(name, out var handler))
            return false;
        handler(argument);
        return true;
    }

    private void Navigate(string target) => NavigationRequested?.Invoke(target);
}
