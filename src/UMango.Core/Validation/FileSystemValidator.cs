// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的 PCL.Core/Utils/Validate/FileSystemValidator.cs
// 原文件：Apache-2.0。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Validate → UMango.Core.Validation。
//   2. 校验消息不再硬编码中文，改为返回**结构化的失败原因**，
//      由上层（Core 的 ITextProvider / App 的资源字典）决定怎么措辞。
//      理由：Core 必须与平台无关且不持有文案；PCL 原版直接写死中文字面量，
//      在 UMango 的多语言体系下是死路。
//   3. Windows 保留名列表里补上了 COM¹/²/³、LPT¹/²/³（原版已有），
//      并把 CON/PRN 等的比较改为 OrdinalIgnoreCase（原版用 Equals，大小写敏感，
//      "con" 会被漏掉 —— 但 Windows 上 "con" 同样非法）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UMango.Core.Validation;

/// <summary>
/// 文件系统相关校验的公共辅助。
///
/// 原版是抽象类（供各验证器继承来拿到受保护静态方法）；UMango 改成静态类，
/// 让验证器可以正常继承 FluentValidation 的 <c>AbstractValidator&lt;string&gt;</c>
/// —— 两者继承链不同，塞在一起会导致 <c>IValidator&lt;string&gt;</c> 的成员没人实现。
/// </summary>
public static class FileSystemValidator
{
    /// <summary>
    /// 找出输入中包含的非法字符。
    /// </summary>
    /// <param name="input">待检查的字符串。</param>
    /// <param name="extraInvalidStrings">额外的非法字符（按整串比较）。</param>
    /// <returns>非法字符列表；没有则返回 <c>null</c>。</returns>
    public static IReadOnlyList<string>? CheckInvalidStrings(string input, string[] extraInvalidStrings)
    {
        if (string.IsNullOrEmpty(input)) return null;

        var invalidStrings = Path.GetInvalidFileNameChars().Select(c => c.ToString()).ToArray();
        invalidStrings = invalidStrings.Concat(extraInvalidStrings).ToArray();

        var found = invalidStrings
            .Where(input.Contains)
            .Distinct()
            .ToArray();

        return found.Length != 0 ? found : null;
    }

    /// <summary>
    /// 找出输入中命中的 Windows 保留名。
    /// </summary>
    /// <param name="input">待检查的文件名。</param>
    /// <param name="extraReservedWords">额外的保留名。</param>
    /// <returns>命中的保留名；没有则返回 <c>null</c>。</returns>
    public static string? CheckReservedWord(string input, string[] extraReservedWords)
    {
        if (string.IsNullOrEmpty(input)) return null;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(input);

        IEnumerable<string> reserved =
        [
            "CON", "PRN", "AUX", "CLOCK$", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
            "COM8", "COM9", "COM¹", "COM²", "COM³", "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
            "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
        ];
        reserved = reserved.Concat(extraReservedWords);

        // 原版用 Equals（大小写敏感），"con" 会被漏掉 —— 但 Windows 上 "con" 同样非法。
        var matched = reserved.FirstOrDefault(r => r.Equals(nameWithoutExtension, StringComparison.OrdinalIgnoreCase));

        return matched ?? null;
    }
}
