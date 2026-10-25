// SPDX-License-Identifier: Apache-2.0
//
// 平台中立的「主题族 + 深浅色」状态。
//
// 为什么要有它：主题这件事有两层，各壳都得做，但只有一层是共通的：
//   · 共通层（这里）：当前选的是哪个主题族、深色还是浅色、什么时候变了；
//   · 各自的层：WPF 换 ResourceDictionary，Avalonia 换 IResourceDictionary。
//
// 为什么不让两个壳各写一份枚举：枚举值一旦分叉，"深色"在两个壳里指的是
// 不同的东西，切换逻辑就没法共用，配置项也会写出两个版本。
//
// 数值权威在 WPF 侧那四份 token 字典（逐像素校验过），Avalonia 侧由
// `scripts/sync-theme-tokens.py` 从 WPF 侧单向生成 —— 两个壳的 token 不会漂移。

namespace UMango.Shell.Theming;

/// <summary>可选主题族。深浅色是同一族的第二个维度。</summary>
public enum ThemeKind
{
    /// <summary>PCL 经典：1:1 复刻 PCL-CE 的观感（数值有录屏逐像素校验依据，不得改动）。</summary>
    PclClassic,

    /// <summary>Windows 10 Fluent：还原 Win10「设置」的观感。注意 Fluent Design ≠ WinUI3/Win11。</summary>
    Fluent10,
}

/// <summary>
/// 主题状态的**唯一真相源**（平台中立）。
///
/// 用法：
/// <code>
/// ShellThemeState.Current.Kind = ThemeKind.Fluent10;
/// ShellThemeState.Current.IsDark = true;   // 改任一项都会触发 ThemeChanged
/// </code>
///
/// 各壳订阅 <see cref="ThemeChanged"/>，在自己的线程上换资源字典。
/// </summary>
public sealed class ShellThemeState
{
    /// <summary>进程级单例（两个壳各用各的进程，不会互相干扰）。</summary>
    public static ShellThemeState Current { get; } = new();

    private ThemeKind _kind = ThemeKind.PclClassic;
    private bool _isDark;

    /// <summary>当前主题族。</summary>
    public ThemeKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>当前是否为深色。</summary>
    public bool IsDark
    {
        get => _isDark;
        set
        {
            if (_isDark == value) return;
            _isDark = value;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>主题状态变化时触发（各壳在这里换自己的资源字典）。</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>一次性设置主题族与深浅色，**只在真的有变化时**才触发一次事件。</summary>
    public void Apply(ThemeKind kind, bool dark)
    {
        var kindChanged = _kind != kind;
        var darkChanged = _isDark != dark;

        if (!kindChanged && !darkChanged) return;

        _kind = kind;
        _isDark = dark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}
