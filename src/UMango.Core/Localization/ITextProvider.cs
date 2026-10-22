using System;

namespace UMango.Core.Localization;

/// <summary>
/// 文案查找提供器。让业务层/ViewModel 能在**不依赖 WPF** 的前提下取本地化文案。
///
/// **为什么需要这一层**：WPF 的 <c>Application.Current.TryFindResource</c> 会拖入
/// PresentationFramework，而它只存在于 Windows —— macOS 的测试宿主一旦碰到
/// <c>Application</c> 这个类型就 <c>FileNotFoundException</c>
/// （App.Tests 里 56 处构造 ViewModel，全都会被拖垮）。
/// 于是把「去哪找文案」抽象成接口：App 层用资源字典实现，测试用查表实现，
/// 无 UI 环境下用「直接返回键」的默认实现。
/// </summary>
public interface ITextProvider
{
    /// <summary>
    /// 取 <paramref name="key"/> 对应的文案；找不到时返回 <paramref name="key"/> 本身
    /// （与 PCL 的 <c>Lang</c> 行为一致：调试构建返回 <c>!key!</c>，这里更宽容）。
    /// </summary>
    string Text(string key);
}

/// <summary>
/// 无 UI 环境的默认提供器：永远返回键本身。
/// 用于单元测试，以及 Core 独立运行（控制台/服务模式）时。
/// </summary>
public sealed class KeyOnlyTextProvider : ITextProvider
{
    public static readonly KeyOnlyTextProvider Instance = new();

    public string Text(string key) => key;
}
