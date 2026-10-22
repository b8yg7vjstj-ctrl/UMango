using UMango.Core.Localization;

namespace UMango.App.Localization;

/// <summary>
/// 视图层（XAML 的 code-behind / Converter）的本地化入口。
///
/// **为什么不在每个 code-behind 里各写一个 L()**：
/// 一个 .xaml.cs 常有多个类（主类 + 若干 Converter），主类里的 private L() 别的类看不见（CS0103）；
/// 而在各自的 .cs 里定义文件级 <c>internal static class L</c> 又会在同一命名空间里撞车（CS0101）。
///
/// **为什么不直接调 <see cref="LocalizationService.Text(string, object[])"/>**：
/// 那样每处都要写全定限名；且与 ViewModel 的 L() 保持同名同形，替换时不易漏。
///
/// 与 ViewModel 的 L() 语义一致：无 UI 环境下 Provider 是 KeyOnlyTextProvider，
/// 返回资源键本身而不抛 —— 这正是无界面测试的预期降级路径。
/// </summary>
internal static class ViewText
{
    internal static string L(string key, params object?[] args)
        => LocalizationService.Text(key, args);
}
