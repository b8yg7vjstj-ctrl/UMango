namespace UMango.App;

/// <summary>
/// 提示浮层的**列队策略**（纯逻辑，不依赖任何界面类型，因此可无界面测试）。
///
/// 为什么单独抽出来：这条策略有两条非显然规则（去重 + 条数上限），
/// 它们决定了"刷屏时界面会不会糊住"。留在 MainWindow 里就只能靠肉眼验证；
/// 抽成纯函数后可以锁进单元测试，而 MainWindow 只负责渲染与线程编组。
///
/// 规则对齐 PCL-CE 的 HintService：
///   · 相同文案（且已在显示中）不再叠一条 —— PCL 的 HintWaiting 也做同样检查；
///   · 同时显示条数有上限 —— 否则连续失败会瞬间铺满整屏。
///
/// 可见性为 public（而非 internal）：本工程没有配置 InternalsVisibleTo，
/// 测试工程无法看到 internal 类型。这与仓库既有做法一致（见 GitPackageManager 的同类注释）。
/// </summary>
public static class ToastPolicy
{
    /// <summary>同时显示的提示条上限。超过则挤掉最旧的。</summary>
    public const int MaxVisible = 4;

    /// <summary>
    /// 判断一条新提示是否应当入列。
    /// </summary>
    /// <param name="existing">当前显示中的文案（顺序无关，只用于查重）。</param>
    /// <param name="message">新提示文案。</param>
    /// <returns>true = 应显示；false = 与现有提示重复，丢弃。</returns>
    public static bool ShouldEnqueue(IEnumerable<string> existing, string message)
    {
        // 空文案不入列：PCL 侧偶发传 null/空（调用方拼串失败），显示一个空浮条没有意义
        if (string.IsNullOrWhiteSpace(message)) return false;

        foreach (var item in existing)
        {
            if (string.Equals(item, message, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// 为一条新提示腾出位置后应有的移除条数（0 表示无需移除）。
    /// 用"要移除几条"而不是直接改集合，是为了让本函数保持无副作用、可测试。
    /// </summary>
    /// <param name="currentCount">当前显示条数。</param>
    public static int EvictionsNeeded(int currentCount)
        => currentCount >= MaxVisible ? currentCount - MaxVisible + 1 : 0;
}
