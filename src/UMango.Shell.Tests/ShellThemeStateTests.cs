using UMango.Shell.Theming;
using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// 主题状态的共享真相源测试。
///
/// <para>
/// 为什么这些测试重要：WPF 壳与 Avalonia 壳都读 <see cref="ShellThemeState"/>。
/// 如果两个壳各自存一份"当前深浅色"，就会出现"设置页显示深色、切到 Avalonia 却是浅色"
/// 这种漂移 —— 而且很难在真机上发现。
/// </para>
/// </summary>
public class ShellThemeStateTests
{
    [Fact]
    public void Default_IsPclClassicAndLight()
    {
        var state = new ShellThemeState();

        Assert.Equal(ThemeKind.PclClassic, state.Kind);
        Assert.False(state.IsDark);
    }

    [Fact]
    public void Apply_ChangesKindAndRaisesOnce()
    {
        var state = new ShellThemeState();
        var count = 0;
        state.ThemeChanged += (_, _) => count++;

        state.Apply(ThemeKind.Fluent10, dark: false);

        Assert.Equal(ThemeKind.Fluent10, state.Kind);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Apply_SameValues_DoesNotRaise()
    {
        var state = new ShellThemeState();
        state.Apply(ThemeKind.Fluent10, dark: true);

        var count = 0;
        state.ThemeChanged += (_, _) => count++;

        state.Apply(ThemeKind.Fluent10, dark: true);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Apply_OnlyDarkChanges_StillRaises()
    {
        var state = new ShellThemeState();
        state.Apply(ThemeKind.Fluent10, dark: false);

        var count = 0;
        state.ThemeChanged += (_, _) => count++;

        state.Apply(ThemeKind.Fluent10, dark: true);

        Assert.Equal(1, count);
        Assert.True(state.IsDark);
    }

    [Fact]
    public void SettingKind_RaisesOnce()
    {
        var state = new ShellThemeState();
        var count = 0;
        state.ThemeChanged += (_, _) => count++;

        state.Kind = ThemeKind.Fluent10;
        state.Kind = ThemeKind.Fluent10;

        Assert.Equal(1, count);
    }

    [Fact]
    public void SettingIsDark_RaisesOnce()
    {
        var state = new ShellThemeState();
        var count = 0;
        state.ThemeChanged += (_, _) => count++;

        state.IsDark = true;
        state.IsDark = true;

        Assert.Equal(1, count);
    }

    [Fact]
    public void Current_IsSharedSingleton()
    {
        Assert.Same(ShellThemeState.Current, ShellThemeState.Current);
    }
}
