using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// YAML 节点与事件流互转。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/YamlNodeConverter.cs</c>（Apache-2.0）。
///
/// 来源注记（保留）：修改自 https://dotnetfiddle.net/jaG1i1 ，
/// 原始出处 https://stackoverflow.com/a/40727087 ，
/// 原作者 Antoine Aubry（YamlDotNet 的作者）。
/// </summary>
public static class YamlNodeConverter
{
    /// <summary>把事件流包成 <see cref="IParser"/>。</summary>
    public sealed class EventStreamParserAdapter(IEnumerable<ParsingEvent> events) : IParser
    {
        private readonly IEnumerator<ParsingEvent> _enumerator = events.GetEnumerator();

        /// <inheritdoc />
        public ParsingEvent Current => _enumerator.Current;

        /// <inheritdoc />
        public bool MoveNext() => _enumerator.MoveNext();
    }

    /// <summary>把事件流包成解析器。</summary>
    public static IParser GetParser(this IEnumerable<ParsingEvent> eventStream) =>
        new EventStreamParserAdapter(eventStream);

    /// <summary>把整个流转成事件流。</summary>
    public static IEnumerable<ParsingEvent> ConvertToEventStream(this YamlStream stream)
    {
        yield return new StreamStart();
        foreach (var document in stream.Documents)
        {
            foreach (var evt in document.ConvertToEventStream()) yield return evt;
        }
        yield return new StreamEnd();
    }

    /// <summary>把文档转成事件流。</summary>
    public static IEnumerable<ParsingEvent> ConvertToEventStream(this YamlDocument document)
    {
        yield return new DocumentStart();
        foreach (var evt in document.RootNode.ConvertToEventStream()) yield return evt;
        yield return new DocumentEnd(false);
    }

    /// <summary>把节点转成事件流。</summary>
    public static IEnumerable<ParsingEvent> ConvertToEventStream(this YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                foreach (var evt in EmitScalar(scalar)) yield return evt;
                yield break;
            case YamlSequenceNode sequence:
                foreach (var evt in EmitSequence(sequence)) yield return evt;
                yield break;
            case YamlMappingNode mapping:
                foreach (var evt in EmitMapping(mapping)) yield return evt;
                yield break;
            default:
                throw new NotSupportedException($"Unsupported node type: {node.GetType().Name}");
        }
    }

    private static IEnumerable<ParsingEvent> EmitScalar(YamlScalarNode scalar)
    {
        yield return new Scalar(scalar.Anchor, scalar.Tag, scalar.Value!, scalar.Style, false, false);
    }

    private static IEnumerable<ParsingEvent> EmitSequence(YamlSequenceNode sequence)
    {
        yield return new SequenceStart(sequence.Anchor, sequence.Tag, false, sequence.Style);
        foreach (var node in sequence.Children)
        {
            foreach (var evt in node.ConvertToEventStream()) yield return evt;
        }
        yield return new SequenceEnd();
    }

    private static IEnumerable<ParsingEvent> EmitMapping(YamlMappingNode mapping)
    {
        yield return new MappingStart(mapping.Anchor, mapping.Tag, false, mapping.Style);
        foreach (var pair in mapping.Children)
        {
            foreach (var evt in pair.Key.ConvertToEventStream()) yield return evt;
            foreach (var evt in pair.Value.ConvertToEventStream()) yield return evt;
        }
        yield return new MappingEnd();
    }
}
