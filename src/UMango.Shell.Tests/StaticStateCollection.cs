using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// 把测试进程的数据目录重定向到临时目录。
///
/// <para>
/// 为什么必须做：<c>AppSettings.Settings</c> 在**静态初始化**时就把
/// <c>AppPaths.Current.SettingsFile</c> 快照成了字段，之后再改 <c>AppPaths.Current</c> 无效。
/// 不隔离的话，本进程里任何一次设置写入（设置页测试改动画倍率、
/// 下载页测试打开「显示不支持的包源」……）都会落到**用户真实的** settings.json 上，
/// 既污染真实配置，也让"默认值是 false"这类断言受本机历史值影响而随机变红。
/// </para>
///
/// <para>
/// 环境变量是唯一能在首次访问 <c>AppPaths</c> 之前生效的手段，
/// 而模块初始化器保证在所有测试代码之前执行。
/// </para>
/// </summary>
internal static class TestEnvironment
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RedirectDataDirectory()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "umango-shell-tests-" + System.Environment.ProcessId);

        System.Environment.SetEnvironmentVariable("UMANGO_DATA_DIR", root);
        UMango.Core.Platform.AppPaths.Reset();
    }
}

/// <summary>
/// 触碰**全局静态状态**的测试归到同一个集合，由 xUnit 串行执行。
///
/// <para>
/// 目前涉及的是 Core 的 <c>LocalizationService.CurrentProvider</c>：
/// 它是全局单例，替换之后整个进程里的 <c>Text()</c> 都会变。
/// </para>
///
/// <para>
/// 为什么必须串行：有些用例断言的是"取到的文案"与"界面上的文案"相等
/// （例如工具页的错误提示）。若另一个用例在两者之间替换了 provider，
/// 前者取到资源键、后者取到中文文案，断言就会**偶发失败** ——
/// 这种失败与被测代码无关，纯粹是测试之间互相干扰。
/// </para>
///
/// <para>
/// 与 Core.Tests / App.Tests 的同名集合保持同一套规则。
/// </para>
/// </summary>
[CollectionDefinition("静态状态", DisableParallelization = true)]
public sealed class StaticStateCollection;
