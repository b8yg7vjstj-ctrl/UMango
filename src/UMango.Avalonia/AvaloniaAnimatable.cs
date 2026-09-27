using Avalonia;
using Avalonia.Media;
using UMango.Core.Animation;

namespace UMango.Avalonia;

/// <summary>
/// 把 Avalonia 的属性包装成 Core 动画引擎的 <see cref="IAnimatable"/>。
///
/// 与 WPF 侧的 <c>WpfAnimatable</c> 同构：Core 引擎只认 GetValue/SetValue，
/// 平台壳负责把"值"接到自家属性上。这样 PCL 的原版动效参数（时长/缓动/关键帧）
/// 在两个框架上走的是**同一套引擎与同一份数值**。
/// </summary>
public static class AvaloniaAnimatable
{
    /// <summary>取元素的三份可动画目标：位移 X、位移 Y、不透明度。</summary>
    public static (IAnimatable X, IAnimatable Y, IAnimatable Opacity) FromElement(Visual element)
    {
        var transform = new TranslateTransform();
        // Avalonia 的 RenderTransform 需要显式赋值才会生效
        element.RenderTransform = transform;

        IAnimatable x = new DelegateAnimatable(
            () => transform.X,
            v => transform.X = v is double d ? d : 0);
        IAnimatable y = new DelegateAnimatable(
            () => transform.Y,
            v => transform.Y = v is double d ? d : 0);
        IAnimatable opacity = new DelegateAnimatable(
            () => element.Opacity,
            v => element.Opacity = v is double d ? d : 1);

        return (x, y, opacity);
    }
}
