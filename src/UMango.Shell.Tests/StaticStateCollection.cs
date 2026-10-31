using Xunit;

namespace UMango.Shell.Tests;

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
