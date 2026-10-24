// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgIconParser.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Svg；
//   2. 原版在 PCL.Core 里直接用 XDocument（要引 System.Xml.Linq），
//      UMango 放到独立的 SvgSourceParser，方便把 XML 读取收敛在一处；
//   3. **安全加固**：原版用 DtdProcessing.Prohibit + XmlResolver = null，
//      这一点保留（SVG 是外部内容，必须禁 DTD/外部实体，防止 XXE 与实体膨胀）。
//      另外原版没有限制 XML 深度，这里加了一个深度上限。

using System.Xml;
using System.Xml.Linq;

namespace UMango.Core.UI.Svg;

/// <summary>把 SVG 源码读成平台中立的元素树。</summary>
public static class SvgSourceParser
{
    /// <summary>XML 嵌套深度上限（防止深层嵌套导致的资源耗尽）。</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// 解析 SVG 源码。
    /// </summary>
    /// <exception cref="FormatException">源码不是合法 XML 或缺少根节点时抛出。</exception>
    public static SvgElement ParseSource(string svg)
    {
        ArgumentNullException.ThrowIfNull(svg);

        using var stringReader = new StringReader(svg);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });

        var document = XDocument.Load(xmlReader, LoadOptions.None);
        var root = document.Root ?? throw new FormatException("SVG 文件缺少根节点。");

        return Convert(root, 0);
    }

    private static SvgElement Convert(XElement element, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("SVG 结构嵌套过深。");

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in element.Attributes())
        {
            // 跳过命名空间声明（xmlns / xmlns:xxx）。
            if (attribute.IsNamespaceDeclaration)
                continue;
            attributes[attribute.Name.LocalName] = attribute.Value;
        }

        var result = new SvgElement
        {
            Name = element.Name.LocalName,
            Attributes = attributes,
        };

        foreach (var child in element.Elements())
            result.Children.Add(Convert(child, depth + 1));

        return result;
    }
}
