// SPDX-License-Identifier: Apache-2.0
//
// WPF 补间绑定器 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// 原始设计（PCL）：
//   每次补间带着「目标对象 + 通道」，`AniRun` 按通道用 `dynamic` 反射改属性：
//   `DeltaLeft` / `DeltaTop`（改 Margin）/ `Opacity +=` / `Width +=` /
//   `((dynamic)obj).Radius +=` / `RenderTransform` 上的 `X +=` / `Y +=` …
//
// UMango 的差异：
//   1. PCL 用 `dynamic` 反射 —— 编译期毫无保护，属性名写错要到运行期才炸。
//      这里改成**显式的通道 → 依赖属性映射表**，写错通道在编译期就看得见。
//   2. PCL 的 X/Y 走 `Margin`（`DeltaLeft`/`DeltaTop`），
//      而 TranslateX/TranslateY 走 `RenderTransform` —— 两套坐标系混用。
//      这里把 X/Y 也统一到 `RenderTransform`（TranslateTransform），
//      与 UMango 其余动画（`WpfAnimatable.FromElement`）一致。
//   3. PCL 的 `AaDouble` 把对象和依赖属性塞进 `object[]` 再 `dynamic` 取出来 ——
//      这里用 <see cref="WpfPropertyBinding"/> 显式声明。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UMango.Core.Animation;

namespace UMango.App.Animation;

/// <summary>一个「依赖属性 → 动画目标」的声明。</summary>
/// <param name="Key">属性键（用于 <see cref="TweenChannel.Double"/> 这类通用通道）。</param>
/// <param name="Property">被动画的依赖属性。</param>
public sealed record WpfPropertyBinding(string Key, DependencyProperty Property);

/// <summary>
/// 把 Core 的补间通道映射到 WPF 的具体依赖属性。
///
/// 对应 PCL 的 <c>AniRun</c> 里那一大堆 <c>switch (ani.typeSub)</c>（借鉴设计）。
/// </summary>
public sealed class WpfTweenBinder
{
    private readonly Dictionary<TweenChannel, IAnimatable> _channels = [];
    private readonly Dictionary<string, IAnimatable> _properties = [];

    /// <summary>被动画的元素。</summary>
    public FrameworkElement? Element { get; set; }

    /// <summary>把某个通道接到一个具体的 <see cref="IAnimatable"/>。</summary>
    public WpfTweenBinder Bind(TweenChannel channel, IAnimatable target)
    {
        _channels[channel] = target;
        return this;
    }

    /// <summary>把某个属性键接到一个具体的 <see cref="IAnimatable"/>。</summary>
    public WpfTweenBinder Bind(string propertyKey, IAnimatable target)
    {
        _properties[propertyKey] = target;
        return this;
    }

    /// <summary>取某个通道的当前目标（没有则 null）。</summary>
    public IAnimatable? Resolve(TweenChannel channel) => _channels.GetValueOrDefault(channel);

    /// <summary>取某个属性键的当前目标（没有则 null）。</summary>
    public IAnimatable? Resolve(string propertyKey) => _properties.GetValueOrDefault(propertyKey);

    /// <summary>
    /// 为一个元素建立**默认**的通道绑定表。
    ///
    /// 与 PCL 的默认行为对应：
    ///   · X / Y → <see cref="TranslateTransform"/>（PCL 走 Margin，这里统一到变换）
    ///   · Opacity → 元素本身
    ///   · Width / Height → 元素的依赖属性
    /// </summary>
    public static WpfTweenBinder ForElement(FrameworkElement element, bool includeTransforms = true)
    {
        ArgumentNullException.ThrowIfNull(element);

        var binder = new WpfTweenBinder { Element = element };

        if (includeTransforms)
        {
            var transform = element.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }

            binder.Bind(TweenChannel.X, new WpfAnimatable(() => transform.X, v => transform.X = v));
            binder.Bind(TweenChannel.Y, new WpfAnimatable(() => transform.Y, v => transform.Y = v));
            binder.Bind(TweenChannel.TranslateX, new WpfAnimatable(() => transform.X, v => transform.X = v));
            binder.Bind(TweenChannel.TranslateY, new WpfAnimatable(() => transform.Y, v => transform.Y = v));
        }

        binder.Bind(TweenChannel.Opacity, new WpfAnimatable(() => element.Opacity, v => element.Opacity = v));
        binder.Bind(TweenChannel.Width, new WpfAnimatable(() => element.Width, v => element.Width = v));
        binder.Bind(TweenChannel.Height, new WpfAnimatable(() => element.Height, v => element.Height = v));

        return binder;
    }

    /// <summary>
    /// 导出一份绑定表，交给 Core 的 <see cref="TweenPlanner"/> 使用。
    ///
    /// 只有**本绑定器里已注册**的通道才会导出 —— 没注册的通道会由 Core 自动创建一个
    /// 纯内存目标（不接回 WPF），这在测试里很方便，但在真机上就是"动画没效果"。
    /// 所以真机路径应当只用 <see cref="ForElement"/> 建立的表。
    /// </summary>
    public IReadOnlyList<TweenChannelBinding> ToBindings()
    {
        var list = new List<TweenChannelBinding>(_channels.Count + _properties.Count);
        foreach (var (channel, target) in _channels)
            list.Add(new TweenChannelBinding(channel, target));
        return list;
    }

    /// <summary>
    /// 把「通用 double 属性」通道接到具体的依赖属性上。
    ///
    /// 对应 PCL 的 <c>AaDouble(obj, DependencyProperty prop, value)</c>。
    /// </summary>
    public WpfTweenBinder BindProperty(string propertyKey, DependencyObject owner, DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(property);

        _properties[propertyKey] = new WpfValueAnimatable(owner, property);
        return this;
    }
}
