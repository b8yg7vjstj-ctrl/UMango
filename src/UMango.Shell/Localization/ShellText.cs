// SPDX-License-Identifier: Apache-2.0
//
// 平台中立的文案简写入口。
//
// 为什么要有它：一个 .cs 文件里常有好几个类（主 ViewModel + 若干子 ViewModel），
// 主类里的 private L() 别的类看不见；而在各自的文件里定义文件级 internal static class L
// 又会在同一命名空间里撞车（CS0101）。这个类把「文案查找」放在一处，谁都能用。

using UMango.Core.Localization;

namespace UMango.Shell.Localization;

/// <summary>
/// 文案简写（等价于 <see cref="LocalizationService.Text(string, object[])"/>）。
///
/// <para>
/// 无 UI 环境下 Provider 是 <c>KeyOnlyTextProvider</c>，返回资源键本身而不抛 ——
/// 这正是无界面测试的预期降级路径。
/// </para>
/// </summary>
public static class ShellText
{
    /// <summary>取当前语言下的文本（找不到键时返回键本身）。</summary>
    public static string L(string key, params object?[] args) => LocalizationService.Text(key, args);
}
