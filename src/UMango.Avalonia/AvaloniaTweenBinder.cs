// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 补间绑定器 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// 与 WPF 侧 <c>WpfTweenBinder</c> 同构：Core 的补间描述是纯数据，
// 宿主负责把"通道"接到自家属性上。于是同一份 PCL 数值在两个框架上
// 得到**逐帧一致**的结果 —— 这正是"两个壳，一套动效"的含义。
//
// PCL 与这里的差异：
//   PCL 用 `dynamic` 反射改 WPF 属性（`((dynamic)obj).Opacity += delta`），
//   编译期毫无保护。这里改成显式的通道 → 属性绑定表。

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UMango.Core.Animation;

namespace UMango.Avalonia;

/// <summary>把 Core 的补间通道映射到 Avalonia 的具体属性。</summary>
public sealed class AvaloniaTweenBinder
{
    private readonly Dictionary<TweenChannel, IAnimatable> _channels = [];

    /// <summary>被动画的元素。</summary>
    public Visual? Element { get; set; }

    /// <summary>把某个通道接到一个 <see cref="IAnimatable"/>。</summary>
    public AvaloniaTweenBinder Bind(TweenChannel channel, IAnimatable target)
    {
        _channels[channel] = target;
        return this;
    }

    /// <summary>取某个通道的当前目标（没有则 null）。</summary>
    public IAnimatable? Resolve(TweenChannel channel) => _channels.GetValueOrDefault(channel);

    /// <summary>
    /// 为一个元素建立**默认**的通道绑定表。
    ///
    /// 与 PCL 的默认行为对应：
    ///   · X / Y / TranslateX / TranslateY → <see cref="TranslateTransform"/>
    ///   · Opacity → 元素本身
    ///   · Width / Height → 元素的对应属性
    /// </summary>
    public static AvaloniaTweenBinder ForElement(Control element, bool includeTransforms = true)
    {
        ArgumentNullException.ThrowIfNull(element);

        var binder = new AvaloniaTweenBinder { Element = element };

        if (includeTransforms)
        {
            var transform = element.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }

            binder.Bind(TweenChannel.X, new DelegateAnimatable(
                () => transform.X, v => transform.X = v is double d ? d : 0));
            binder.Bind(TweenChannel.Y, new DelegateAnimatable(
                () => transform.Y, v => transform.Y = v is double d ? d : 0));
            binder.Bind(TweenChannel.TranslateX, new DelegateAnimatable(
                () => transform.X, v => transform.X = v is double d ? d : 0));
            binder.Bind(TweenChannel.TranslateY, new DelegateAnimatable(
                () => transform.Y, v => transform.Y = v is double d ? d : 0));
        }

        binder.Bind(TweenChannel.Opacity, new DelegateAnimatable(
            () => element.Opacity, v => element.Opacity = v is double d ? d : 1));
        binder.Bind(TweenChannel.Width, new DelegateAnimatable(
            () => element.Width, v => element.Width = v is double d ? d : double.NaN));
        binder.Bind(TweenChannel.Height, new DelegateAnimatable(
            () => element.Height, v => element.Height = v is double d ? d : double.NaN));

        return binder;
    }

    /// <summary>导出绑定表，交给 Core 的 <see cref="TweenPlanner"/>。</summary>
    public IReadOnlyList<TweenChannelBinding> ToBindings()
    {
        var list = new List<TweenChannelBinding>(_channels.Count);
        foreach (var (channel, target) in _channels)
            list.Add(new TweenChannelBinding(channel, target));
        return list;
    }
}
