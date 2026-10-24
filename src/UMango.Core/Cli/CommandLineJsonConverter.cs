// SPDX-License-Identifier: Apache-2.0
//
// 本文件派生自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/App/Cli/CommandLine.cs 内的 CommandLineJsonConverter。
// PCL.Core 采用 Apache-2.0 许可，故本文件亦以 Apache-2.0 发布，详见仓库根目录 LICENSE。
//
// 相对原版的改动：
//   1. 拆成独立文件（原版内嵌在 CommandLine.cs 里）；
//   2. 命名空间 PCL.Core.App.Cli → UMango.Core.Cli；
//   3. 读写时带上位置参数与重复键，否则序列化再反序列化会丢信息。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace UMango.Core.Cli;

/// <summary>
/// <see cref="CommandLine"/> 的 JSON 转换器。
/// </summary>
public sealed class CommandLineJsonConverter : JsonConverter<CommandLine>
{
    /// <inheritdoc/>
    public override CommandLine? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected object for CommandLine.");

        string? commandText = null;
        CommandLine? subcommand = null;
        var arguments = new Dictionary<string, CommandArgument>();
        var positional = new List<string>();
        var duplicateKeys = new List<string>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected property name.");

            var propName = reader.GetString();
            if (!reader.Read()) throw new JsonException("Unexpected end of json.");

            switch (propName)
            {
                case "cmd":
                    commandText = reader.GetString() ?? throw new JsonException("cmd cannot be null.");
                    break;
                case "sub":
                    subcommand = JsonSerializer.Deserialize<CommandLine>(ref reader, options);
                    break;
                case "args":
                    ReadArguments(ref reader, arguments);
                    break;
                case "pos":
                    ReadPositional(ref reader, positional);
                    break;
                case "dup":
                    ReadStrings(ref reader, duplicateKeys);
                    break;
                default:
                    // 忽略未知字段，提升前向兼容
                    reader.Skip();
                    break;
            }
        }

        if (string.IsNullOrEmpty(commandText)) throw new JsonException("Missing required property: cmd.");

        return new CommandLine
        {
            CommandText = commandText,
            Arguments = arguments.AsReadOnly(),
            Subcommand = subcommand,
            Positional = positional.AsReadOnly(),
            DuplicateKeys = duplicateKeys.AsReadOnly()
        };
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, CommandLine value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("cmd", value.CommandText);
        if (value.Arguments.Count > 0)
        {
            writer.WritePropertyName("args");
            writer.WriteStartArray();
            foreach (var arg in value.Arguments.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("k", arg.Key);
                writer.WriteNumber("t", (int)arg.ValueKind);
                writer.WriteString("v", arg.ValueText);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (value.Positional.Count > 0)
        {
            writer.WritePropertyName("pos");
            writer.WriteStartArray();
            foreach (var p in value.Positional) writer.WriteStringValue(p);
            writer.WriteEndArray();
        }

        if (value.DuplicateKeys.Count > 0)
        {
            writer.WritePropertyName("dup");
            writer.WriteStartArray();
            foreach (var d in value.DuplicateKeys) writer.WriteStringValue(d);
            writer.WriteEndArray();
        }

        if (value.Subcommand is not null)
        {
            writer.WritePropertyName("sub");
            JsonSerializer.Serialize(writer, value.Subcommand, options);
        }

        writer.WriteEndObject();
    }

    private static void ReadArguments(ref Utf8JsonReader reader, Dictionary<string, CommandArgument> target)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("args must be an array.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return;
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Argument item must be an object.");

            string? key = null;
            ArgumentValueKind? kind = null;
            string? valueText = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected argument property name.");

                var name = reader.GetString();
                if (!reader.Read()) throw new JsonException("Unexpected end of json.");

                switch (name)
                {
                    case "k":
                        key = reader.GetString();
                        break;
                    case "t":
                        kind = (ArgumentValueKind)reader.GetInt32();
                        break;
                    case "v":
                        valueText = reader.GetString() ?? string.Empty;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (string.IsNullOrEmpty(key)) throw new JsonException("Argument missing key(k).");
            if (kind is null) throw new JsonException("Argument missing type(t).");
            valueText ??= string.Empty;

            CommandArgument arg = kind.Value switch
            {
                ArgumentValueKind.Bool => new BoolArgument { Key = key, ValueText = valueText },
                ArgumentValueKind.Decimal => new DecimalArgument { Key = key, ValueText = valueText },
                ArgumentValueKind.Text => new TextArgument { Key = key, ValueText = valueText },
                _ => throw new JsonException($"Unsupported argument type: {(int)kind.Value}")
            };
            target[arg.Key] = arg;
        }

        throw new JsonException("args array is not closed.");
    }

    private static void ReadPositional(ref Utf8JsonReader reader, List<string> target)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("pos must be an array.");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return;
            target.Add(reader.GetString() ?? string.Empty);
        }
    }

    private static void ReadStrings(ref Utf8JsonReader reader, List<string> target)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("dup must be an array.");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return;
            target.Add(reader.GetString() ?? string.Empty);
        }
    }
}
