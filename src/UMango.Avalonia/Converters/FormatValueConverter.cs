// SPDX-License-Identifier: Apache-2.0
//
// 「格式串 + 值」拼文案的转换器 —— 补 Avalonia 的一个缺口。
//
// 背景：WPF 的 MultiBinding 支持 StringFormat，而格式串本身可以是 DynamicResource
// （语言切换时自动重新格式化）：
//
//     <MultiBinding StringFormat="{DynamicResource Fmt.SpeedFactor}">
//       <Binding Path="AnimationSpeedFactor"/>
//     </MultiBinding>
//
// Avalonia 的 MultiBinding 没有 StringFormat 属性（AVLN3000），而把格式串写成
// `StringFormat='速度倍率：{0:0.0}x'` 又会把中文/英文硬编码进 XAML —— 语言切换就失效了。
// 所以改由本转换器承担：第一个绑定是格式串（走 DynamicResource），第二个是要填的值。

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace UMango.Avalonia.Converters;

/// <summary>
/// 把 <c>[格式串, 值]</c> 拼成一段文案，格式串支持标准的 <c>{0}</c> / <c>{0:0.0}</c> 占位符。
/// </summary>
/// <remarks>
/// 只支持单值占位（<c>{0}</c>），因为设置页里所有「带数字的文案」都是这个形状；
/// 需要多值时应该直接暴露一个现成的字符串属性，而不是把格式化逻辑塞进视图。
/// 格式串或值缺失时返回空串（而不是抛异常或显示 "System.Object"），
/// 这样语言文件漏翻一条也不会让整页显示成一片异常文本。
/// </remarks>
public sealed class FormatValueConverter : IMultiValueConverter
{
    /// <summary>XAML 里用 <c>{x:Static conv:FormatValueConverter.Instance}</c> 引用。</summary>
    public static FormatValueConverter Instance { get; } = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Count: >= 2 }) return string.Empty;

        if (values[0] is not string format || format.Length == 0) return string.Empty;

        var value = values[1];
        if (value is null) return string.Empty;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, value);
        }
        catch (FormatException)
        {
            // 语言文件里写了非法占位符（例如 {1}）：宁可回落到裸格式串，也不要整页崩。
            return format;
        }
    }
}
