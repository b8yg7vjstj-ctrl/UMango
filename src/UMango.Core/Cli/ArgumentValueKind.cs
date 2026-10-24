// SPDX-License-Identifier: Apache-2.0
//
// 本文件派生自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/App/Cli/ArgumentValueKind.cs。PCL.Core 采用 Apache-2.0 许可，
// 故本文件亦以 Apache-2.0 发布，详见仓库根目录 LICENSE。
//
// 相对原版的改动：仅命名空间 PCL.Core.App.Cli → UMango.Core.Cli。

namespace UMango.Core.Cli;

/// <summary>
/// 命令行参数值的类型。
/// </summary>
public enum ArgumentValueKind
{
    /// <summary>布尔。</summary>
    Bool,

    /// <summary>小数。</summary>
    Decimal,

    /// <summary>文本。</summary>
    Text,
}
