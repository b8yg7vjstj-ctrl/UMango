using System.ComponentModel;
using System.Globalization;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// 值与字符串之间的转换。
///
/// 取自 PCL-CE 的 <c>PCL.Core/Utils/Exts/StringExtension.cs</c>（Apache-2.0），
/// 补上了 PCL 原版**没有**的 <c>ConvertToString</c> 的 null 语义（PCL 的返回 null，
/// 但 INI/YAML 存储层需要空字符串而不是 null）。
/// </summary>
public static class ValueConverter
{
    /// <summary>把任意值转成字符串（null 转成空串）。</summary>
    public static string ToText(object? obj)
    {
        if (obj is null) return string.Empty;
        if (obj is string s) return s;

        var converter = TypeDescriptor.GetConverter(obj.GetType());
        if (converter.CanConvertTo(typeof(string)))
        {
            var o = converter.ConvertToInvariantString(obj);
            return o as string ?? string.Empty;
        }

        if (obj is IFormattable fmt) return fmt.ToString(null, CultureInfo.InvariantCulture);

        return obj.ToString() ?? string.Empty;
    }

    /// <summary>把字符串转成目标类型。</summary>
    /// <exception cref="NotSupportedException">目标类型没有可用的字符串解析入口。</exception>
    public static object? FromText(string? value, Type targetType)
    {
        if (targetType == typeof(string)) return value;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value is null)
            return underlying.IsValueType ? Activator.CreateInstance(underlying) : null;

        if (underlying == typeof(bool))
        {
            // PCL 原版：'true' / '1' 视为真；这里再加上 "yes"/"on" 这类常见写法
            var t = value.Trim();
            if (bool.TryParse(t, out var b)) return b;
            if (t is "1" or "yes" or "on" or "enable" or "enabled") return true;
            if (t is "0" or "no" or "off" or "disable" or "disabled") return false;
            throw new FormatException($"无法把 '{value}' 转成布尔值");
        }

        if (underlying.IsEnum) return Enum.Parse(underlying, value, ignoreCase: true);

        if (underlying == typeof(byte[])) return Convert.FromBase64String(value);

        var parse = underlying.GetMethod("Parse",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            binder: null, types: [typeof(string)], modifiers: null);
        if (parse is not null) return parse.Invoke(null, [value]);

        var converter = TypeDescriptor.GetConverter(underlying);
        if (converter.CanConvertFrom(typeof(string))) return converter.ConvertFromInvariantString(value);

        throw new NotSupportedException($"无法将字符串转换为类型 {underlying.FullName}");
    }

    /// <summary>泛型版 <see cref="FromText"/>。</summary>
    public static T? FromText<T>(string? value) => (T?)FromText(value, typeof(T));
}
