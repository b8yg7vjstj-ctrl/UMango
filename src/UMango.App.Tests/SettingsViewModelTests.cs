using Xunit;
using UMango.App.ViewModels;
using UMango.Core.Localization;

namespace UMango.App.Tests;

/// <summary>
/// 设置页 ViewModel 的用例。
///
/// **为什么在 App.Tests 而不是 Shell.Tests**：
/// <c>SettingsViewModel</c> 目前仍在 <c>UMango.App</c>（它引用了 WPF 的主题/对话框），
/// 而 Shell.Tests 只引用 Shell，不引用 App —— 否则跨平台测试会被 PresentationFramework 拖住。
/// 等 SettingsViewModel 也下沉到 Shell 后，这一组再搬过去。
/// </summary>
public class SettingsViewModelTests
{

    [Fact]
    public void 设置页语言选项在无UI环境也能构造()
    {
        // 回归：AppLocalization.BuildOptions 原先调 Text() → 碰 Application 类型 →
        // PresentationFramework 只存在于 Windows → macOS 测试宿主 FileNotFoundException，
        // 7 个 SettingsViewModel 用例全军覆没。本用例保证那条路径不再依赖 WPF 类型。
        var vm = new SettingsViewModel();
        Assert.NotEmpty(vm.Languages);
        Assert.Equal("auto", vm.Languages[0].Code);
        Assert.False(string.IsNullOrWhiteSpace(vm.Languages[0].DisplayName));
        Assert.NotNull(vm.SelectedLanguage);
    }

    [Fact]
    public void 设置页提供两种主题族与两种动画方向()
    {
        // Themes/AnimationDirections 的 Title/Description 是**字段初始化器**（构造时求值 L()），
        // 所以 Provider 要在构造之前装好。
        using (ChineseProvider.Scope())
        {
            var vm = new SettingsViewModel();
            Assert.Equal(2, vm.Themes.Count);
            Assert.Equal(2, vm.AnimationDirections.Count);

            // 需求要求两种动画矢量都可选（默认镜像 UMango，另一项还原 PCL）
            Assert.Contains(vm.AnimationDirections, o => o.Mode == UMango.Core.Animation.AnimationDirectionMode.Mirrored);
            Assert.Contains(vm.AnimationDirections, o => o.Mode == UMango.Core.Animation.AnimationDirectionMode.PclCompatible);
            Assert.Contains(vm.Themes, t => t.Title.Contains("PCL"));
            Assert.Contains(vm.Themes, t => t.Title.Contains("Fluent"));
        }
    }

    [Fact]
    public void 设置页的动画开关读写走设置服务()
    {
        var vm = new SettingsViewModel();
        var original = vm.AnimationEnabled;

        try
        {
            vm.AnimationEnabled = !original;
            Assert.Equal(!original, vm.AnimationEnabled);
        }
        finally
        {
            vm.AnimationEnabled = original;   // 还原，避免污染真实设置文件
        }
    }

    [Fact]
    public void 设置页速度倍率被夹在合法范围()
    {
        var vm = new SettingsViewModel();
        var original = vm.AnimationSpeedFactor;

        try
        {
            vm.AnimationSpeedFactor = 99.0;
            Assert.InRange(vm.AnimationSpeedFactor, 0.5, 2.0);
            vm.AnimationSpeedFactor = 0.01;
            Assert.InRange(vm.AnimationSpeedFactor, 0.5, 2.0);
        }
        finally
        {
            vm.AnimationSpeedFactor = original;
        }
    }

    [Fact]
    public void 设置页的日志保留条数读写走设置服务且被夹在合法范围()
    {
        var vm = new SettingsViewModel();
        var original = vm.LogBufferCapacity;

        try
        {
            vm.LogBufferCapacity = 12345;
            Assert.Equal(12345, vm.LogBufferCapacity);

            // 越界值被夹住（描述符 Min=200 / Max=50000）
            vm.LogBufferCapacity = 1;
            Assert.Equal(200, vm.LogBufferCapacity);

            vm.LogBufferCapacity = 999999;
            Assert.Equal(50000, vm.LogBufferCapacity);
        }
        finally
        {
            vm.LogBufferCapacity = original;   // 还原，避免污染真实设置文件
        }
    }

    [Fact]
    public void 设置页能读到记录统计()
    {
        var vm = new SettingsViewModel();
        Assert.True(vm.TotalRecords >= 0);
        Assert.True(vm.SuccessCount >= 0);
        Assert.True(vm.FailureCount >= 0);
        Assert.True(vm.SuccessCount + vm.FailureCount <= vm.TotalRecords);
    }
}

/// <summary>
/// 测试用的中文文案提供器：把 <c>zh-CN.xaml</c> 里的键→文案装进
/// <see cref="LocalizationService"/>，让断言能写具体中文（而不是资源键）。
/// </summary>
internal sealed class ChineseProvider : UMango.Core.Localization.ITextProvider
{
    private static readonly System.Collections.Generic.Dictionary<string, string> Table =
        new(System.StringComparer.Ordinal);

    private const string Pattern = "<sys:String x:Key=\"" + "([^\"]+)\"" + ">(.*)</sys:String>";

    static ChineseProvider()
    {
        var path = System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "UMango.App", "Localization", "Languages", "zh-CN.xaml");
        if (!System.IO.File.Exists(path)) return;
        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, Pattern);
            if (m.Success) Table[m.Groups[1].Value] = m.Groups[2].Value;
        }
    }

    public string Text(string key) => Table.TryGetValue(key, out var v) ? v : key;

    public static ScopeHandle Scope()
    {
        var previous = UMango.Core.Localization.LocalizationService.CurrentProvider;
        UMango.Core.Localization.LocalizationService.SetTextProvider(new ChineseProvider());
        return new ScopeHandle(previous);
    }

    internal readonly struct ScopeHandle : System.IDisposable
    {
        private readonly UMango.Core.Localization.ITextProvider _restore;
        public ScopeHandle(UMango.Core.Localization.ITextProvider restore) => _restore = restore;
        public void Dispose() => UMango.Core.Localization.LocalizationService.SetTextProvider(_restore);
    }
}
