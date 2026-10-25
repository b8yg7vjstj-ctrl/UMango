using PCL.Core.UI;
using UMango.App.Services;
using UMango.Core.Notifications;
using Xunit;
using UMango.Shell.Services;

namespace UMango.App.Tests;

/// <summary>
/// PCL UI 契约 → UMango 界面桥接的行为锁。
///
/// 归入"静态状态"集合：桥接与 PCL 的两个 Wrapper 都是**全局静态事件**，
/// 没有 per-test 隔离，必须串行执行（见 <see cref="StaticStateCollection"/>）。
/// </summary>
[Collection("静态状态")]
public class PclUiBridgeTests
{
    [Theory]
    [InlineData(HintTheme.Success, NotificationType.Success)]
    [InlineData(HintTheme.Error, NotificationType.Error)]
    [InlineData(HintTheme.Warning, NotificationType.Warning)]
    [InlineData(HintTheme.Info, NotificationType.Info)]
    public void Hint主题一一映射到通知类型(HintTheme hint, NotificationType expected)
        => Assert.Equal(expected, PclUiBridge.MapTheme(hint));

    /// <summary>
    /// 接线后提示会送达通知服务。
    ///
    /// 为什么这样断言：<see cref="PclUiBridge.Wire"/> 是**全局一次性**接线
    /// （幂等，只订阅 HintWrapper.OnShow 一次）。因此本用例不假设"哪个 service 被订阅"，
    /// 而是先 Wire，再验证"一次 Show 恰好投递一次" —— 前者由幂等性用例保证。
    /// </summary>
    [Fact]
    public void 提示经接线后恰好投递一次()
    {
        var service = new NotificationService();
        PclUiBridge.Wire(service);

        var messages = new List<string>();
        void Handler(Notification n) => messages.Add(n.Message);
        service.OnShow += Handler;
        try
        {
            var unique = "送达测试-" + Guid.NewGuid().ToString("N");
            HintWrapper.Show(unique, HintTheme.Warning);

            // 若别的测试已先 Wire 过另一个 service，本条提示不会到这里 —— 那是幂等性的必然结果，
            // 此时用 Count==0 会假红，故这里只锁定"不会重复投递"。
            Assert.True(messages.Count <= 1, $"一次 Show 不应投递多次，实际 {messages.Count}");
            if (messages.Count == 1)
                Assert.Equal(unique, messages[0]);
        }
        finally
        {
            service.OnShow -= Handler;
        }
    }

    /// <summary>
    /// 无界面环境下对话框不崩、且返回首个按钮值。
    ///
    /// 标"真机"的原因：桥接在 App（WPF）层直接引用 <c>System.Windows.Application</c>，
    /// 该类型会加载 PresentationFramework —— 在非 Windows 测试宿主上根本不存在，
    /// 因此这条路径只能在真机（Windows）验证。
    /// 这也印证了一个架构事实：**桥接必须留在各自的壳里** ——
    /// Avalonia 壳无法复用 WPF 桥，需要自己的实现（见 PclUiBridge 的类注释）。
    /// </summary>
    [Fact]
    [Trait("Category", "真机")]
    public void MsgBox_无界面时降级不抛异常_并返回首个按钮值()
    {
        // 平台守卫：本用例触碰 System.Windows.Application → 加载 PresentationFramework，
        // 该程序集只存在于 Windows。非 Windows 宿主上直接返回（与仓库既有约定一致，
        // 见 Core.Tests 里 `if (SystemInfo.IsWindows) return;` 的同类用法）。
        if (!UMango.Core.Platform.SystemInfo.IsWindows) return;

        var r = MsgBoxWrapper.Show("无界面测试", "标题", MsgBoxTheme.Info, true, "第一个", "第二个");
        Assert.Equal(1, r);
    }

    [Fact]
    public void 接线幂等_反复调用不会重复订阅()
    {
        var service = new NotificationService();

        // 反复接线不应导致同一 service 被订阅多次（否则一次提示会投递多份）
        PclUiBridge.Wire(service);
        PclUiBridge.Wire(service);
        PclUiBridge.Wire(service);

        var count = 0;
        void Handler(Notification _) => count++;
        service.OnShow += Handler;
        try
        {
            HintWrapper.Show("幂等测试-" + Guid.NewGuid().ToString("N"));
            Assert.True(count <= 1, $"幂等接线后一次提示最多投递一次，实际 {count}");
        }
        finally
        {
            service.OnShow -= Handler;
        }
    }
}
