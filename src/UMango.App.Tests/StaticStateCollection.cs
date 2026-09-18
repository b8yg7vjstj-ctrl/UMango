using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 触碰**全局静态状态**的测试归到同一个集合，由 xUnit 串行执行。
///
/// 为什么需要：App 层有若干静态门面（<see cref="UMango.App.Services.AppServices"/>、
/// <c>AppSettings.Settings</c>、Core 的 <c>Log</c> / <c>TerminalBridge.DebugMode</c> /
/// <c>TaskCenter</c>），它们没有 per-test 隔离。若不串行，
/// 一个测试写下的日志/设置会污染另一个测试的断言 ——
/// 这一点是被真实失败暴露出来的：接线冒烟测试断言"日志里无 ERROR"时，
/// 匹配到了另一个测试故意制造的导出失败日志。
/// </summary>
[CollectionDefinition("静态状态", DisableParallelization = true)]
public sealed class StaticStateCollection;
