using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 触碰**全局静态状态**的测试归到同一个集合，由 xUnit 串行执行。
///
/// 目前涉及的是 <see cref="UMango.Core.Localization.LocalizationService"/> 的文案提供器：
/// 它是全局单例，替换之后整个进程里的 <c>Text()</c> 都会变。
///
/// 为什么必须串行（真实事故）：
///   · 「替换提供器」类用例会临时把提供器换成返回中文的表；
///   · 其它用例（如验证器族）断言的是**资源键**本身 ——
///     若两者并行，同一个断言就会时而拿到键、时而拿到中文，测试结果随机。
///   · App.Tests 里已经因为同类问题炸过一次（栈溢出），
///     这里把 Core 侧也纳入同一约束。
/// </summary>
[CollectionDefinition("静态状态", DisableParallelization = true)]
public sealed class StaticStateCollection;
