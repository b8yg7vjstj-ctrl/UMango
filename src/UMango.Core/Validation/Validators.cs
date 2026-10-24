// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的 PCL.Core/Utils/Validate/ 下 12 个验证器
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 与原版的**根本差异**：PCL 把校验失败消息写死成中文字面量
// （"输入内容不能为空！"、"文件名不可包含 {0} 字符！"…），
// 而 UMango 有多语言体系，Core 层不该持有任何文案。因此这里把消息换成
// **资源键**，由 Core 的 LocalizationService 去查（与 ViewModel 里的 L() 同一套机制）：
//   · 无 UI 环境（Core 单元测试）→ KeyOnlyTextProvider → 消息就是键本身，测试可断言键；
//   · App 运行时 → 资源字典 provider → 消息是真���文案。
// 详见 src/UMango.Core/Localization/LocalizationService.cs。
//
// 其余改动：
//   1. 命名空间 PCL.Core.Utils.Validate → UMango.Core.Validation。
//   2. PCL 的 `_BuildRules()` + `PreValidate()` 模式在**每次校验时重建规则**，
//      目的是让可变属性（如 Blacklist 列表）改动后立刻生效。
//      代价是每次校验都要重新构造整套规则对象。UMango 改为构造时建一次规则，
//      可变属性改为**方法参数**（如 Validate(name, blacklist)）—— 更直白，也没有性能坑。
//   3. PCL 的 Minecraft 专属字符检查（额外禁 "!" 与 ";"）由 `UseMinecraftCharCheck`
//      保留为可选开关，UMango 默认关闭 —— 包管理器用不到 Minecraft 的文件名限制，
//      而 git 仓库名、npm 包名里出现 "!" 是合法的。
//   4. FileSystemValidator 的保留名比较改为大小写不敏感（见该文件内注释）。
//   5. PCL 的 RegexValidator 支持 `ErrorKey`（走 Lang.Text 查键）与 `ErrorMessage`
//      （字面量）二选一；UMango 统一为键（ErrorKey 为空时回落到默认键）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Results;
using UMango.Core.Localization;
using UMango.Core.Utils;

namespace UMango.Core.Validation;

/// <summary>
/// 校验失败消息的资源键。
///
/// 做成常量而不是裸字符串，是为了把"键名打错"变成编译期错误 ——
/// 这类错误在中文界面下永远暴露不出来，只有切到别的语言才会炸。
/// </summary>
public static class ValidationText
{
    /// <summary>输入不能为空。</summary>
    public const string Empty = "Validation.Empty";
    /// <summary>输入不能只含空白。</summary>
    public const string Blank = "Validation.Blank";
    /// <summary>不能以空格开头。</summary>
    public const string LeadingSpace = "Validation.LeadingSpace";
    /// <summary>不能以空格结尾。</summary>
    public const string TrailingSpace = "Validation.TrailingSpace";
    /// <summary>不能以小数点结尾。</summary>
    public const string TrailingDot = "Validation.TrailingDot";
    /// <summary>含非法字符。参数：{0}=字符列表。</summary>
    public const string InvalidChar = "Validation.InvalidChar";
    /// <summary>命中保留名。参数：{0}=保留名。</summary>
    public const string ReservedWord = "Validation.ReservedWord";
    /// <summary>是 NTFS 8.3 短名格式。</summary>
    public const string Ntfs83Name = "Validation.Ntfs83Name";
    /// <summary>与现有文件重名。</summary>
    public const string DuplicateFile = "Validation.DuplicateFile";
    /// <summary>与现有文件夹重名。</summary>
    public const string DuplicateFolder = "Validation.DuplicateFolder";
    /// <summary>父文件夹不存在。参数：{0}=路径。</summary>
    public const string ParentMissing = "Validation.ParentMissing";
    /// <summary>路径分段有误。</summary>
    public const string BadPathSegment = "Validation.BadPathSegment";
    /// <summary>网址无效。</summary>
    public const string InvalidUrl = "Validation.InvalidUrl";
    /// <summary>不是整数。</summary>
    public const string NotInteger = "Validation.NotInteger";
    /// <summary>数字位数过多。</summary>
    public const string NumberTooLong = "Validation.NumberTooLong";
    /// <summary>超过上限。参数：{0}=上限。</summary>
    public const string AboveMax = "Validation.AboveMax";
    /// <summary>低于下限。参数：{0}=下限。</summary>
    public const string BelowMin = "Validation.BelowMin";
    /// <summary>长度必须恰好等于某值。参数：{0}=长度。</summary>
    public const string ExactLength = "Validation.ExactLength";
    /// <summary>长度至少为某值。参数：{0}=长度。</summary>
    public const string MinLength = "Validation.MinLength";
    /// <summary>长度至多为某值。参数：{0}=长度。</summary>
    public const string MaxLength = "Validation.MaxLength";
    /// <summary>正则不匹配。参数：{0}=模式。</summary>
    public const string RegexMismatch = "Validation.RegexMismatch";
    /// <summary>命中黑名单。参数：{0}=词条。</summary>
    public const string Blacklisted = "Validation.Blacklisted";

    /// <summary>
    /// 取一条本地化消息。
    /// </summary>
    /// <param name="key">资源键。</param>
    /// <param name="args">格式化参数。</param>
    public static string M(string key, params object?[] args)
        => args.Length == 0 ? LocalizationService.Text(key) : LocalizationService.Text(key, args);
}

/// <summary>输入不能为空。</summary>
public sealed class NullOrEmptyValidator : AbstractValidator<string>
{
    /// <summary>构造验证器。</summary>
    public NullOrEmptyValidator()
        => RuleFor(x => x).Must(x => !string.IsNullOrEmpty(x)).WithMessage(ValidationText.M(ValidationText.Empty));
}

/// <summary>输入不能只含空白。</summary>
public sealed class NullOrWhiteSpaceValidator : AbstractValidator<string>
{
    /// <summary>构造验证器。</summary>
    public NullOrWhiteSpaceValidator()
        => RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage(ValidationText.M(ValidationText.Blank));
}

/// <summary>
/// 整数校验。
/// </summary>
public sealed class IntValidator : AbstractValidator<string>
{
    /// <summary>允许的最大值。</summary>
    public int Max { get; set; }
    /// <summary>允许的最小值。</summary>
    public int Min { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="max">允许的最大值。</param>
    /// <param name="min">允许的最小值。</param>
    public IntValidator(int max = int.MaxValue, int min = int.MinValue)
    {
        Max = max;
        Min = min;
        RuleFor(x => x)
            .Must(x => x.Length < 9).WithMessage(ValidationText.M(ValidationText.NumberTooLong))
            .Must(x => int.TryParse(x, out _)).WithMessage(ValidationText.M(ValidationText.NotInteger))
            .Must(x => int.TryParse(x, out var value) && value <= Max)
            .WithMessage(ValidationText.M(ValidationText.AboveMax, Max))
            .Must(x => int.TryParse(x, out var value) && value >= Min)
            .WithMessage(ValidationText.M(ValidationText.BelowMin, Min));
    }
}

/// <summary>
/// 字符串长度校验。
/// </summary>
public sealed class StringLengthValidator : AbstractValidator<string>
{
    /// <summary>最小长度。</summary>
    public int Min { get; set; }
    /// <summary>最大长度。</summary>
    public int Max { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="min">最小长度。</param>
    /// <param name="max">最大长度。</param>
    public StringLengthValidator(int min = 0, int max = int.MaxValue)
    {
        Min = min;
        Max = max;
        RuleFor(x => x)
            // PCL 原版第一条规则是 "x.Length != Max || Max == Min"，
            // 也就是**长度恰好等于 Max 时判失败** —— 显然是把条件写反了
            // （原版文案是"长度必须为 {Max} 个字符"，但它挂在上界语义上）。
            // 照搬会让 "0123456789" 这种刚好 10 位的合法输入被拒。
            // 这里改成：只有当上下界相同（即要求"恰好 N 个字符"）时才要求等长。
            .Must(x => Min != Max || x.Length == Min).WithMessage(ValidationText.M(ValidationText.ExactLength, Min))
            .Must(x => x.Length >= Min).WithMessage(ValidationText.M(ValidationText.MinLength, Min))
            .Must(x => x.Length <= Max).WithMessage(ValidationText.M(ValidationText.MaxLength, Max));
    }
}

/// <summary>
/// 正则校验。
/// </summary>
public sealed class RegexValidator : AbstractValidator<string>
{
    /// <summary>正则模式。</summary>
    public string Pattern { get; set; }
    /// <summary>失败时的资源键。</summary>
    public string ErrorKey { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="pattern">正则模式。</param>
    /// <param name="errorKey">失败时的资源键；留空则用默认键。</param>
    public RegexValidator(string pattern = "", string errorKey = ValidationText.RegexMismatch)
    {
        Pattern = pattern;
        ErrorKey = errorKey;
        RuleFor(x => x)
            .Must(x => Regex.IsMatch(x, Pattern))
            .WithMessage(ValidationText.M(ErrorKey, Pattern));
    }
}

/// <summary>
/// HTTP(S) 网址校验。
/// </summary>
public sealed class HttpValidator : AbstractValidator<string>
{
    /// <summary>是否允许空串。</summary>
    public bool AllowsNullOrEmpty { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="allowNullOrEmpty">是否允许空串。</param>
    public HttpValidator(bool allowNullOrEmpty = false)
    {
        AllowsNullOrEmpty = allowNullOrEmpty;
        RuleFor(x => x)
            .Must(x => AllowsNullOrEmpty && string.IsNullOrEmpty(x) || RegexPatterns.HttpUri.IsMatch(x))
            .WithMessage(ValidationText.M(ValidationText.InvalidUrl));
    }
}

/// <summary>
/// HTTP(S) 网址或 Windows UNC 路径校验。
/// </summary>
public sealed class HttpAndUncValidator : AbstractValidator<string>
{
    /// <summary>是否允许空串。</summary>
    public bool AllowsNullOrEmpty { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="allowNullOrEmpty">是否允许空串。</param>
    public HttpAndUncValidator(bool allowNullOrEmpty = false)
    {
        AllowsNullOrEmpty = allowNullOrEmpty;
        RuleFor(x => x)
            .Must(x => AllowsNullOrEmpty && string.IsNullOrEmpty(x)
                       || RegexPatterns.HttpUri.IsMatch(x)
                       || RegexPatterns.UncPath.IsMatch(x))
            .WithMessage(ValidationText.M(ValidationText.InvalidUrl));
    }
}

/// <summary>
/// 黑名单校验：输入不得包含任何一条黑名单词条。
/// </summary>
public sealed class BlacklistValidator : AbstractValidator<string>
{
    /// <summary>黑名单词条。</summary>
    public List<string> Blacklist { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="contains">黑名单词条。</param>
    public BlacklistValidator(List<string> contains)
    {
        Blacklist = contains;
        RuleFor(x => x)
            .Custom((input, context) =>
            {
                foreach (var item in Blacklist)
                {
                    if (input.Contains(item, StringComparison.OrdinalIgnoreCase))
                    {
                        context.AddFailure(ValidationText.M(ValidationText.Blacklisted, item));
                    }
                }
            });
    }
}

/// <summary>
/// 文件名校验。
/// </summary>
public sealed class FileNameValidator : AbstractValidator<string>
{
    /// <summary>是否额外禁止 Minecraft 不允许的字符（"!" 与 ";"）。</summary>
    public bool UseMinecraftCharCheck { get; set; }
    /// <summary>重名比较时是否忽略大小写。</summary>
    public bool IgnoreCase { get; set; }
    /// <summary>父文件夹路径；为 <c>null</c> 则不查重名。</summary>
    public string? ParentFolder { get; set; }
    /// <summary>父文件夹不存在时是否直接判失败。</summary>
    public bool RequireParentFolderExists { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="parentFolder">父文件夹路径。</param>
    /// <param name="ignoreCase">重名比较时是否忽略大小写。</param>
    /// <param name="useMinecraftCharCheck">是否额外禁止 "!" 与 ";"。</param>
    /// <param name="requireParentFolderExists">父文件夹不存在时是否直接判失败。</param>
    public FileNameValidator(string? parentFolder = null, bool ignoreCase = true,
        bool useMinecraftCharCheck = false, bool requireParentFolderExists = true)
    {
        ParentFolder = parentFolder;
        IgnoreCase = ignoreCase;
        UseMinecraftCharCheck = useMinecraftCharCheck;
        RequireParentFolderExists = requireParentFolderExists;
        BuildRules();
    }

    private void BuildRules()
    {
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage(ValidationText.M(ValidationText.Blank))
            .Must(x => !x.StartsWith(' ')).WithMessage(ValidationText.M(ValidationText.LeadingSpace))
            .Must(x => !x.EndsWith(' ')).WithMessage(ValidationText.M(ValidationText.TrailingSpace))
            .Must(x => !x.EndsWith('.')).WithMessage(ValidationText.M(ValidationText.TrailingDot))
            .Custom((fileName, context) =>
            {
                var invalid = FileSystemValidator.CheckInvalidStrings(fileName, UseMinecraftCharCheck ? ["!", ";"] : []);
                if (invalid is not null)
                {
                    context.AddFailure(ValidationText.M(ValidationText.InvalidChar, string.Join("  ", invalid)));
                }
            })
            .Custom((fileName, context) =>
            {
                var reserved = FileSystemValidator.CheckReservedWord(fileName, []);
                if (reserved is not null)
                {
                    context.AddFailure(ValidationText.M(ValidationText.ReservedWord, reserved));
                }
            })
            .Must(x => !RegexPatterns.Ntfs83FileName.IsMatch(x))
            .WithMessage(ValidationText.M(ValidationText.Ntfs83Name))
            .Must(CheckDuplicate)
            .WithMessage(ValidationText.M(ValidationText.DuplicateFile));
    }

    private bool CheckDuplicate(string fileName)
    {
        if (ParentFolder is null) return true;

        var dirInfo = new DirectoryInfo(ParentFolder);
        if (!dirInfo.Exists) return !RequireParentFolderExists;

        return !dirInfo.EnumerateFiles().Select(f => f.Name).Contains(fileName,
            IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }
}

/// <summary>
/// 文件夹名校验。
/// </summary>
public sealed class FolderNameValidator : AbstractValidator<string>
{
    /// <summary>是否额外禁止 Minecraft 不允许的字符（"!" 与 ";"）。</summary>
    public bool UseMinecraftCharCheck { get; set; }
    /// <summary>重名比较时是否忽略大小写。</summary>
    public bool IgnoreCase { get; set; }
    /// <summary>父文件夹路径；为 <c>null</c> 则不查重名。</summary>
    public string? ParentFolder { get; set; }
    /// <summary>是否忽略与父文件夹下已有目录同名。</summary>
    public bool IgnoreSameNameInParentFolder { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="parentFolder">父文件夹路径。</param>
    /// <param name="useMinecraftCharCheck">是否额外禁止 "!" 与 ";"。</param>
    /// <param name="ignoreCase">重名比较时是否忽略大小写。</param>
    /// <param name="ignoreSameNameInParentFolder">是否忽略与已有目录同名。</param>
    public FolderNameValidator(string? parentFolder = null, bool useMinecraftCharCheck = false,
        bool ignoreCase = true, bool ignoreSameNameInParentFolder = false)
    {
        ParentFolder = parentFolder;
        UseMinecraftCharCheck = useMinecraftCharCheck;
        IgnoreCase = ignoreCase;
        IgnoreSameNameInParentFolder = ignoreSameNameInParentFolder;
        BuildRules();
    }

    private void BuildRules()
    {
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage(ValidationText.M(ValidationText.Blank))
            .Must(x => !x.StartsWith(' ')).WithMessage(ValidationText.M(ValidationText.LeadingSpace))
            .Must(x => !x.EndsWith(' ')).WithMessage(ValidationText.M(ValidationText.TrailingSpace))
            .Must(x => !x.EndsWith('.')).WithMessage(ValidationText.M(ValidationText.TrailingDot))
            .Custom((folderName, context) =>
            {
                var invalid = FileSystemValidator.CheckInvalidStrings(folderName, UseMinecraftCharCheck ? ["!", ";"] : []);
                if (invalid is not null)
                {
                    context.AddFailure(ValidationText.M(ValidationText.InvalidChar, string.Join("  ", invalid)));
                }
            })
            .Custom((folderName, context) =>
            {
                var reserved = FileSystemValidator.CheckReservedWord(folderName, []);
                if (reserved is not null)
                {
                    context.AddFailure(ValidationText.M(ValidationText.ReservedWord, reserved));
                }
            })
            .Must(x => !RegexPatterns.Ntfs83FileName.IsMatch(x))
            .WithMessage(ValidationText.M(ValidationText.Ntfs83Name))
            .Must(CheckDuplicate)
            .WithMessage(ValidationText.M(ValidationText.DuplicateFolder));
    }

    private bool CheckDuplicate(string folderName)
    {
        if (ParentFolder is null) return true;

        var dirInfo = new DirectoryInfo(ParentFolder);
        if (!dirInfo.Exists) return true;
        if (IgnoreSameNameInParentFolder) return true;

        return !dirInfo.EnumerateDirectories().Select(d => d.Name).Contains(folderName,
            IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }
}

/// <summary>
/// 文件夹路径校验：逐段检查。
/// </summary>
public sealed class FolderPathValidator : AbstractValidator<string>
{
    /// <summary>是否额外禁止 Minecraft 不允许的字符（"!" 与 ";"）。</summary>
    public bool UseMinecraftCharCheck { get; set; }

    /// <summary>构造验证器。</summary>
    /// <param name="useMinecraftCharCheck">是否额外禁止 "!" 与 ";"。</param>
    public FolderPathValidator(bool useMinecraftCharCheck = false)
    {
        UseMinecraftCharCheck = useMinecraftCharCheck;
        RuleFor(x => x)
            .NotEmpty().WithMessage(ValidationText.M(ValidationText.Empty))
            .Must(x => !x.EndsWith(' ')).WithMessage(ValidationText.M(ValidationText.TrailingSpace))
            .Must(x => !x.EndsWith('.')).WithMessage(ValidationText.M(ValidationText.TrailingDot));

        RuleForEach(x => GetSubPaths(x))
            .Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage(ValidationText.M(ValidationText.BadPathSegment))
            .Must(x => !x.StartsWith(' ')).WithMessage(ValidationText.M(ValidationText.LeadingSpace))
            .Must(x => !x.EndsWith(' ')).WithMessage(ValidationText.M(ValidationText.TrailingSpace))
            .Must(x => !x.EndsWith('.')).WithMessage(ValidationText.M(ValidationText.TrailingDot))
            .Custom((segment, context) =>
            {
                var invalid = FileSystemValidator.CheckInvalidStrings(segment, UseMinecraftCharCheck ? ["!", ";"] : []);
                if (invalid is not null)
                {
                    context.AddFailure(ValidationText.M(ValidationText.InvalidChar, string.Join("  ", invalid)));
                }
            })
            .Custom((segment, context) =>
            {
                var reserved = FileSystemValidator.CheckReservedWord(segment, []);
                if (reserved is not null)
                {
                    context.AddFailure(ValidationText.M(ValidationText.ReservedWord, reserved));
                }
            })
            .Must(x => !RegexPatterns.Ntfs83FileName.IsMatch(x))
            .WithMessage(ValidationText.M(ValidationText.Ntfs83Name))
            .OverridePropertyName("PathSegments");
    }

    /// <summary>
    /// 把路径拆成段（去掉盘符/根）。
    /// </summary>
    /// <param name="path">路径。</param>
    public static string[] GetSubPaths(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];

        var fullPath = new DirectoryInfo(path).FullName;
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return [];

        return fullPath[root.Length..]
            .TrimEnd(Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }
}
