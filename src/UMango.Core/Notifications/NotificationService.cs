namespace UMango.Core.Notifications;

/// <summary>通知服务内存实现。WPF 渲染层由 App 项目提供。</summary>
public sealed class NotificationService : INotificationService
{
    private readonly List<Notification> _queue = [];
    private readonly Lock _lock = new();
    public event Action<Notification>? OnShow;
    public event Action? OnClear;
    public IReadOnlyList<Notification> Queue { get { lock (_lock) return _queue.ToList(); } }
    public void Show(Notification notification) { lock (_lock) _queue.Add(notification); OnShow?.Invoke(notification); }
    public void ShowSuccess(string message) => Show(new Notification { Message = message, Type = NotificationType.Success });
    public void ShowError(string message) => Show(new Notification { Message = message, Type = NotificationType.Error });
    public void ShowWarning(string message) => Show(new Notification { Message = message, Type = NotificationType.Warning });
    public void ShowInfo(string message) => Show(new Notification { Message = message, Type = NotificationType.Info });
    public void ClearAll() { lock (_lock) _queue.Clear(); OnClear?.Invoke(); }
}
