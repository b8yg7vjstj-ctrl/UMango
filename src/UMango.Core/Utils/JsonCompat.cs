// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的 PCL.Core/Utils/JsonCompat.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils → UMango.Core.Utils。
//   2. 删去 PCL 专属的 Newtonsoft 兼容层（JsonNode 那一堆便捷方法与
//      DateTime 归一化等），只保留本仓库真正用到的宽松 JSON 配置与转换器。
//      —— UMango 不做 MC 启动器，不存在"把 Newtonsoft 的行为搬过来"的需求；
//         保留宽松配置本身（大小写不敏感、允许尾逗号、允许注释、
//         数字可从字符串读）是因为包管理器各家 API 的 JSON 格式并不统一。
//   3. FluentDateTimeConverter / FlexibleBoolConverter / FlexibleStringConverter
//      按原版语义重写（见各转换器注释）。

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UMango.Core.Utils;

/// <summary>
/// 宽松 JSON 序列化的统一入口。
///
/// 包管理器的各家 API（npm registry、PyPI、crates.io、winget…）在 JSON 上
/// 并不统一：大���写不统一、数字有时带引号、时间格式有 ISO 也有本地格式。
/// 这里把"宽容"的部分集中在一处，而不是散落在每个调用点。
/// </summary>
public static class JsonCompat
{
    public static readonly JsonNodeOptions NodeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// 统一的宽松 JSON 配置。该实例在静态初始化时已被冻结，调用方不能修改全局行为。
    /// 如需追加调用点专用设置，请用 <c>new JsonSerializerOptions(JsonCompat.SerializerOptions)</c> 克隆后修改。
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = _CreateSerializerOptions();

    private static JsonSerializerOptions _CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // 包管理器的 API 生态基本都用 camelCase；发出去的请求体也按 camelCase 写。
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new FlexibleDateTimeConverter(),
                new FlexibleBoolConverter(),
                new FlexibleStringConverter(),
                new JsonStringEnumConverter()
            }
        };
        options.MakeReadOnly(true);
        return options;
    }

    /// <summary>解析一段 JSON 文本为节点。</summary>
    public static JsonNode? ParseNode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text, NodeOptions, DocumentOptions);
    }
}

/// <summary>
/// 宽松的 DateTime 转换：接受 ISO 8601，也接受 Unix 时间戳（秒或毫秒）。
/// </summary>
public sealed class FlexibleDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var raw = reader.GetDouble();
            // 大于 1e11 的按毫秒算（2023 年之后的秒级时间戳约 1.7e9，毫秒约 1.7e12）
            var ms = Math.Abs(raw) > 1e11 ? raw : raw * 1000;
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text)) return default;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        throw new JsonException($"无法把 \"{text}\" 解析为日期时间。");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>
/// 宽松的 bool 转换：接受 "true"/"false"/"1"/"0"/"yes"/"no" 等字符串形式。
/// </summary>
public sealed class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.Number:
                return reader.GetDouble() != 0;
            case JsonTokenType.String:
            {
                var text = reader.GetString()?.Trim();
                if (string.IsNullOrEmpty(text)) return false;
                if (bool.TryParse(text, out var b)) return b;
                if (text is "1") return true;
                if (text is "0") return false;
                if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)) return false;
                throw new JsonException($"无法把 \"{text}\" 解析为布尔值。");
            }
            default:
                throw new JsonException($"无法把 {reader.TokenType} 解析为布尔值。");
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

/// <summary>
/// 宽松的 string 转换：数字、布尔、null 都能读成字符串。
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number => reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => "",
            _ => reader.GetString() ?? ""
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
