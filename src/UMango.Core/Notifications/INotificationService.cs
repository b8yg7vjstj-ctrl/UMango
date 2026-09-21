namespace UMango.Core.Notifications;

/// <summary>全局提示/通知服务。对应 PCL-CE 的 HintService（Apache-2.0）。</summary>
public interface INotificationService
{
    void Show(Notification notification);
    void ShowSuccess(string message);
    void ShowError(string message);
    void ShowWarning(string message);
    void ShowInfo(string message);
    void ClearAll();
}

public sealed class Notification
{
    public required string Message { get; init; }
    public NotificationType Type { get; init; }
    public int DurationMs { get; init; } = 3000;
}

public enum NotificationType { Success, Error, Warning, Info }
