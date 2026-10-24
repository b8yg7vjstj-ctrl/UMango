// SPDX-License-Identifier: Apache-2.0
//
// 借鉴自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   Plain Craft Launcher 2/FormMain.xaml.cs（PageChange / PageBack / PageNameGet）
//   与 Plain Craft Launcher 2/Pages/PageType.cs
//
// 原文件位于**自定义许可目录**（不是 Apache-2.0），因此**不逐字复制**，
// 只借鉴其**机制**，在本文件重新实现（思路一致、代码全部自写）。
//
// 原版机制：
//   · `PageStackData` 记录"要去的页面 + 附加数据"。
//   · `PageNameGet(stack)` 返回次级页面的标题；**返回空串表示这是主页面**。
//   · `PageChange` 切页；`PageBack` 回到栈顶（栈空则回主页面）。
//   · 切页时同步左侧导航项的选中态与副标题。
//
// UMango 的差异与理由：
//   1. 原版把"标题是否为空"当作"是不是主页面"的判据 —— 这让**空标题的次级页面**
//      无法存在（切过去会被当成主页面，标题也不显示）。UMango 用显式的
//      `IsSecondary` 标志区分，不靠标题内容推断。
//   2. 原版 `PageBack()` 弹栈后**不更新 `pageCurrent` 之外的状态**（左侧选中态
//      交给 PageChangeActual 内部的 switch），容易出现"标题变了但左侧高亮没变"。
//      UMango 把「当前页 + 栈」的变化统一收敛到一处，订阅者拿到的永远是自洽快照。
//   3. 原版 `PageChangeActual` 开头有个"同页同子类型就返回"的早退，
//      但**子类型不同**时会重复切页并重跑动画。UMango 明确区分
//      "同页同参数"（直接返回）与"同页不同参数"（触发参数变更通知）。
//   4. 原版无参数字典，是 `PageStackData` 里塞一堆 nullable 字段（`additional.Value`）。
//      UMango 用 `object?` 参数 + 强类型取值，避免 `additional.Value` 那种
//      "可能没值还要强转"的写法。

namespace UMango.Core.UI.Navigation;

/// <summary>一个导航条目的标识。字符串即可，UMango 不强制定义枚举。</summary>
public sealed record NavigationPageId(string Value)
{
    public override string ToString() => Value;

    public static implicit operator NavigationPageId(string value) => new(value);
}

/// <summary>导航栈中的一个条目。</summary>
/// <param name="Page">页面标识。</param>
/// <param name="Title">页面标题（次级页面会显示在顶部副标题栏）。</param>
/// <param name="IsSecondary">是否是次级页面。次级页面会显示返回按钮。</param>
/// <param name="Parameter">页面参数（强类型由使用方自行约定）。</param>
public sealed record NavigationEntry(
    NavigationPageId Page,
    string? Title = null,
    bool IsSecondary = false,
    object? Parameter = null);

/// <summary>导航状态的一次快照 —— 订阅者拿到的永远是自洽的一份。</summary>
/// <param name="Current">当前页。</param>
/// <param name="Stack">回退栈（栈顶是最近压入的）。</param>
/// <param name="CanGoBack">是否有可返回的上一层。</param>
/// <param name="Title">当前页标题（可能是 null）。</param>
public readonly record struct NavigationSnapshot(
    NavigationEntry Current,
    IReadOnlyList<NavigationEntry> Stack,
    bool CanGoBack,
    string? Title);

/// <summary>
/// 通用导航栈。
///
/// PCL-CE 的导航栈与 Minecraft 强耦合（页面类型全是 MC 相关），
/// 这里只保留**与内容无关的那一层**：压栈 / 返回 / 标题 / 选中态同步。
/// </summary>
public sealed class NavigationService
{
    private readonly List<NavigationEntry> _stack = [];
    private readonly List<NavigationEntry> _history = [];
    private NavigationEntry _current;
    private bool _suspended;

    /// <summary>构造。<paramref name="root"/> 是主页面（初始页）。</summary>
    public NavigationService(NavigationEntry root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _current = root;
        _history.Add(root);
    }

    /// <summary>构造（用字符串页标识）。</summary>
    public NavigationService(string rootPage, string? title = null)
        : this(new NavigationEntry(new NavigationPageId(rootPage), title, false, null))
    {
    }

    /// <summary>当前页。</summary>
    public NavigationEntry Current => _current;

    /// <summary>当前页标题。</summary>
    public string? Title => _current.Title;

    /// <summary>回退栈（只读副本）。</summary>
    public IReadOnlyList<NavigationEntry> Stack => _stack.ToArray();

    /// <summary>是否有可返回的上一层。</summary>
    public bool CanGoBack => _stack.Count > 0;

    /// <summary>栈深度。</summary>
    public int Depth => _stack.Count;

    /// <summary>切换历史（只读副本），便于调试与"最近访问"。</summary>
    public IReadOnlyList<NavigationEntry> History => _history.ToArray();

    /// <summary>导航状态变化时触发（压栈、返回、切主页面都会触发）。</summary>
    public event EventHandler<NavigationSnapshot>? Navigated;

    /// <summary>
    /// 暂停通知 —— 在批量修改时避免多次触发。
    /// 退出时会自动恢复并补发一次。
    /// </summary>
    public IDisposable SuspendNotifications()
    {
        _suspended = true;
        return new ResumeScope(this);
    }

    /// <summary>
    /// 切换到主页面：清空回退栈。
    /// </summary>
    public void NavigateRoot(NavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _stack.Clear();
        SetCurrent(entry);
    }

    /// <summary>
    /// 压栈进入次级页面。
    /// </summary>
    /// <param name="entry">要进入的页面。</param>
    /// <param name="replace">为 true 时替换当前页（当前页不留在回退栈里）。</param>
    public void Push(NavigationEntry entry, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // replace = true：不把当前页压进回退栈，而是**就地替换**当前页。
        // 这样「A → replace 成 B」之后返回仍然回到 A 之前的那一层。
        if (!replace)
            _stack.Add(_current);

        SetCurrent(entry);
    }

    /// <summary>压栈进入次级页面（用字符串页标识）。</summary>
    public void Push(string page, string? title = null, object? parameter = null, bool replace = false)
        => Push(new NavigationEntry(new NavigationPageId(page), title, true, parameter), replace);

    /// <summary>
    /// 返回上一层。没有上一层时返回 false（调用方应自行处理"回主页面"）。
    /// </summary>
    public bool Back()
    {
        if (_stack.Count == 0) return false;

        var entry = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        SetCurrent(entry);
        return true;
    }

    /// <summary>
    /// 返回。有上一层就返回上一层；没有就回到主页面。
    ///
    /// 与 PCL 的 <c>PageBack()</c> 语义一致：栈空时回主页面。
    /// </summary>
    public void BackOrRoot(NavigationEntry root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!Back())
            NavigateRoot(root);
    }

    /// <summary>
    /// 切换到某个已压入的页面（把它上面的都弹掉）。
    /// </summary>
    public bool NavigateTo(NavigationPageId page)
    {
        ArgumentNullException.ThrowIfNull(page);

        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Page != page) continue;

            var entry = _stack[i];
            _stack.RemoveRange(i, _stack.Count - i);
            SetCurrent(entry);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 更新当前页的参数（不切页）。
    ///
    /// 页面标识与参数都相同时**不触发**通知 —— 避免无意义的重绘。
    /// </summary>
    public bool UpdateParameter(object? parameter)
    {
        if (Equals(_current.Parameter, parameter)) return false;

        _current = _current with { Parameter = parameter };
        Notify();
        return true;
    }

    /// <summary>更新当前页的标题（不切页）。</summary>
    public bool UpdateTitle(string? title)
    {
        if (string.Equals(_current.Title, title, StringComparison.Ordinal)) return false;

        _current = _current with { Title = title };
        Notify();
        return true;
    }

    private void SetCurrent(NavigationEntry entry)
    {
        _current = entry;
        _history.Add(entry);
        Notify();
    }

    private void Notify()
    {
        if (_suspended) return;

        Navigated?.Invoke(this, new NavigationSnapshot(_current, _stack.ToArray(), CanGoBack, Title));
    }

    private sealed class ResumeScope : IDisposable
    {
        private readonly NavigationService _owner;
        private bool _done;

        public ResumeScope(NavigationService owner) => _owner = owner;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _owner._suspended = false;
            _owner.Notify();
        }
    }
}
