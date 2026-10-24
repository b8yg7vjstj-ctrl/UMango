namespace UMango.Core.Notifications;

/// <summary>通知服务内存实现。WPF 渲染层由 App 项目提供。</summary>
public sealed class NotificationService : INotificationService
{
    private readonly List<Notification> _queue = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// 提示队列本体 —— **策略的唯一来源**。
    ///
    /// 为什么把 PCL-CE 的 <c>HintService</c> 语义接进来：
    /// 原来这里只有一个"谁调用就广播给谁"的事件流，去重与条数上限散落在
    /// App 层的 <c>ToastPolicy</c> 里。两处各写一套规则，迟早走偏
    /// （一个查「已显示」、一个查「等待中」就是典型）。
    /// 现在规则只在 <see cref="HintService"/> 一处，两种调用方式
    /// （PCL 契约的 <c>HintWrapper</c>、App 内部的一次性通知）都汇到同一条队列上。
    /// </summary>
    public HintService Hints { get; }

    public event Action<Notification>? OnShow;
    public event Action? OnClear;

    public IReadOnlyList<Notification> Queue { get { lock (_lock) return _queue.ToList(); } }

    /// <summary>构造。使用自带的提示队列。</summary>
    public NotificationService() : this(new HintService()) { }

    /// <summary>构造。与外部共享同一个提示队列（界面层用它来取要显示什么）。</summary>
    public NotificationService(HintService hints)
    {
        Hints = hints ?? throw new ArgumentNullException(nameof(hints));
    }

    public void Show(Notification notification)
    {
        lock (_lock) _queue.Add(notification);

        // 同时入提示队列 —— 去重/上限都在那里判定。
        // 这里刻意**不**用 HintService 的返回值来决定是否广播 OnShow：
        // OnShow 是"有人请求显示了一条通知"这一事实的信号（PCL 契约用），
        // 而 HintService 的丢弃是"这条不值得占用屏幕位置"（渲染策略用）。两者语义不同。
        Hints.Hint(notification.Message, ToHintType(notification.Type), log: false);

        OnShow?.Invoke(notification);
    }

    public void ShowSuccess(string message) => Show(new Notification { Message = message, Type = NotificationType.Success });
    public void ShowError(string message) => Show(new Notification { Message = message, Type = NotificationType.Error });
    public void ShowWarning(string message) => Show(new Notification { Message = message, Type = NotificationType.Warning });
    public void ShowInfo(string message) => Show(new Notification { Message = message, Type = NotificationType.Info });

    public void ClearAll()
    {
        lock (_lock) _queue.Clear();
        Hints.HideAll();
        OnClear?.Invoke();
    }

    /// <summary>通知类型 → 提示类型（两个枚举一一对应）。</summary>
    public static HintType ToHintType(NotificationType type) => type switch
    {
        NotificationType.Success => HintType.Success,
        NotificationType.Error => HintType.Error,
        NotificationType.Warning => HintType.Warning,
        _ => HintType.Info,
    };
}
