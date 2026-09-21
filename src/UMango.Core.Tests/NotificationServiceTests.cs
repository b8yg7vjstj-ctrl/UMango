using UMango.Core.Notifications;
using Xunit;

namespace UMango.Core.Tests;

public class NotificationServiceTests
{
    [Fact]
    public void 显示通知并触发事件()
    {
        var svc = new NotificationService();
        Notification? received = null;
        svc.OnShow += n => received = n;
        svc.Show(new Notification { Message = "test", Type = NotificationType.Success });
        Assert.NotNull(received);
        Assert.Equal("test", received!.Message);
    }

    [Fact]
    public void 清空后队列为空()
    {
        var svc = new NotificationService();
        svc.ShowInfo("a");
        svc.ShowInfo("b");
        Assert.Equal(2, svc.Queue.Count);
        svc.ClearAll();
        Assert.Empty(svc.Queue);
    }

    [Fact]
    public void 便捷方法创建正确类型()
    {
        var svc = new NotificationService();
        svc.ShowSuccess("ok"); svc.ShowError("err"); svc.ShowWarning("warn"); svc.ShowInfo("info");
        Assert.Equal(4, svc.Queue.Count);
        Assert.Equal(NotificationType.Success, svc.Queue[0].Type);
        Assert.Equal(NotificationType.Error, svc.Queue[1].Type);
        Assert.Equal(NotificationType.Warning, svc.Queue[2].Type);
        Assert.Equal(NotificationType.Info, svc.Queue[3].Type);
    }
}
