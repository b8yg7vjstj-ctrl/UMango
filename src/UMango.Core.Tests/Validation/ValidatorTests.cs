using FluentValidation;
using UMango.Core.Localization;
using UMango.Core.Validation;

namespace UMango.Core.Tests.Validation;

/// <summary>
/// 验证器族测试。
///
/// 断言一律针对**资源键**而不是中文文案 —— Core 的默认 Provider 是
/// <see cref="KeyOnlyTextProvider"/>，无 UI 环境下消息就是键本身。
/// 这样做还有个额外好处：键名打错会立刻暴露（消息变成另一个键），
/// 而中文文案写错只会在切换语言时才被用户看到。
/// </summary>
public class ValidatorTests
{
    private static string[] MessagesOf<T>(AbstractValidator<T> validator, T input)
        => validator.Validate(input).Errors.Select(e => e.ErrorMessage).ToArray();

    // ── 空值 ──────────────────────────────────────────────

    [Fact]
    public void 空串会命中空值规则()
    {
        var v = new NullOrEmptyValidator();
        Assert.Contains(ValidationText.M(ValidationText.Empty), MessagesOf(v, ""));
    }

    [Fact]
    public void 非空串通过空值规则()
    {
        var v = new NullOrEmptyValidator();
        Assert.True(v.Validate("abc").IsValid);
    }

    [Fact]
    public void 纯空白会命中空白规则()
    {
        var v = new NullOrWhiteSpaceValidator();
        Assert.Contains(ValidationText.M(ValidationText.Blank), MessagesOf(v, "   "));
    }

    // ── 整数 ──────────────────────────────────────────────

    [Fact]
    public void 整数规则接受范围内的数字()
    {
        var v = new IntValidator(max: 100, min: 1);
        Assert.True(v.Validate("50").IsValid);
    }

    [Fact]
    public void 整数规则拒绝非数字()
    {
        var v = new IntValidator();
        Assert.Contains(ValidationText.M(ValidationText.NotInteger), MessagesOf(v, "abc"));
    }

    [Fact]
    public void 整数规则拒绝超过上限()
    {
        var v = new IntValidator(max: 100);
        Assert.Contains(ValidationText.M(ValidationText.AboveMax, 100), MessagesOf(v, "101"));
    }

    [Fact]
    public void 整数规则拒绝低于下限()
    {
        var v = new IntValidator(min: 10);
        Assert.Contains(ValidationText.M(ValidationText.BelowMin, 10), MessagesOf(v, "9"));
    }

    [Fact]
    public void 整数规则拒绝位数过多的数字()
    {
        var v = new IntValidator();
        Assert.Contains(ValidationText.M(ValidationText.NumberTooLong), MessagesOf(v, "123456789"));
    }

    // ── 长度 ──────────────────────────────────────────────

    [Fact]
    public void 长度规则接受区间内的字符串()
    {
        var v = new StringLengthValidator(min: 2, max: 5);
        Assert.True(v.Validate("abc").IsValid);
    }

    [Fact]
    public void 长度规则拒绝短于下限()
    {
        var v = new StringLengthValidator(min: 3);
        Assert.Contains(ValidationText.M(ValidationText.MinLength, 3), MessagesOf(v, "ab"));
    }

    [Fact]
    public void 长度规则拒绝长于上限()
    {
        var v = new StringLengthValidator(max: 3);
        Assert.Contains(ValidationText.M(ValidationText.MaxLength, 3), MessagesOf(v, "abcd"));
    }

    [Fact]
    public void 长度规则在上界相等时仍然接受()
    {
        // 回归：PCL 原版第一条规则写成 "x.Length != Max || Max == Min"，
        // 长度恰好等于 Max 时被判失败 —— 条件写反了。
        var v = new StringLengthValidator(min: 0, max: 4);
        Assert.True(v.Validate("abcd").IsValid);
    }

    [Fact]
    public void 上下界相同时要求恰好等长()
    {
        var v = new StringLengthValidator(min: 4, max: 4);
        Assert.True(v.Validate("abcd").IsValid);
        Assert.Contains(ValidationText.M(ValidationText.ExactLength, 4), MessagesOf(v, "abc"));
    }

    // ── 正则 ──────────────────────────────────────────────

    [Fact]
    public void 正则规则接受匹配项()
    {
        var v = new RegexValidator("^a+$");
        Assert.True(v.Validate("aaa").IsValid);
    }

    [Fact]
    public void 正则规则拒绝不匹配项()
    {
        var v = new RegexValidator("^a+$");
        Assert.Contains(ValidationText.M(ValidationText.RegexMismatch, "^a+$"), MessagesOf(v, "b"));
    }

    [Fact]
    public void 正则规则可用自定义键()
    {
        var v = new RegexValidator("^a+$", "Validation.Blacklisted");
        Assert.Contains(ValidationText.M(ValidationText.Blacklisted, "^a+$"), MessagesOf(v, "b"));
    }

    // ── 网址 ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1")]
    public void 网址规则接受合法http地址(string url)
    {
        var v = new HttpValidator();
        Assert.True(v.Validate(url).IsValid);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    public void 网址规则拒绝非http地址(string url)
    {
        var v = new HttpValidator();
        Assert.Contains(ValidationText.M(ValidationText.InvalidUrl), MessagesOf(v, url));
    }

    [Fact]
    public void 网址规则在允许空串时接受空串()
    {
        var v = new HttpValidator(allowNullOrEmpty: true);
        Assert.True(v.Validate("").IsValid);
    }

    [Fact]
    public void 网址或unc规则接受unc路径()
    {
        var v = new HttpAndUncValidator();
        Assert.True(v.Validate(@"\\server\share\file").IsValid);
    }

    // ── 黑名单 ────────────────────────────────────────────

    [Fact]
    public void 黑名单规则拒绝命中词条()
    {
        var v = new BlacklistValidator(["badword"]);
        Assert.Contains(ValidationText.M(ValidationText.Blacklisted, "badword"), MessagesOf(v, "xxbadwordxx"));
    }

    [Fact]
    public void 黑名单规则忽略大小写()
    {
        var v = new BlacklistValidator(["badword"]);
        Assert.False(v.Validate("BADWORD").IsValid);
    }

    [Fact]
    public void 黑名单规则接受未命中内容()
    {
        var v = new BlacklistValidator(["badword"]);
        Assert.True(v.Validate("goodword").IsValid);
    }

    // ── 文件名 ────────────────────────────────────────────

    [Fact]
    public void 文件名规则接受普通名称()
    {
        var v = new FileNameValidator();
        Assert.True(v.Validate("readme.md").IsValid);
    }

    [Fact]
    public void 文件名规则拒绝以空格开头()
    {
        var v = new FileNameValidator();
        Assert.Contains(ValidationText.M(ValidationText.LeadingSpace), MessagesOf(v, " abc"));
    }

    [Fact]
    public void 文件名规则拒绝以空格结尾()
    {
        var v = new FileNameValidator();
        Assert.Contains(ValidationText.M(ValidationText.TrailingSpace), MessagesOf(v, "abc "));
    }

    [Fact]
    public void 文件名规则拒绝以小数点结尾()
    {
        var v = new FileNameValidator();
        Assert.Contains(ValidationText.M(ValidationText.TrailingDot), MessagesOf(v, "abc."));
    }

    [Fact]
    public void 文件名规则拒绝Windows保留名()
    {
        var v = new FileNameValidator();
        Assert.Contains(ValidationText.M(ValidationText.ReservedWord, "CON"), MessagesOf(v, "CON"));
    }

    [Fact]
    public void 文件名规则忽略保留名大小写()
    {
        // 回归：原版用 Equals（大小写敏感），"con" 会被漏掉，
        // 但 Windows 上 "con" 同样非法。
        var v = new FileNameValidator();
        Assert.Contains(ValidationText.M(ValidationText.ReservedWord, "CON"), MessagesOf(v, "con"));
    }

    [Fact]
    public void 文件名规则默认允许感叹号()
    {
        // UMango 与 PCL 的差异：Minecraft 不许 "!"，但 git 仓库名、npm 包名里它合法。
        var v = new FileNameValidator();
        Assert.True(v.Validate("wow!").IsValid);
    }

    [Fact]
    public void 文件名规则开启Minecraft检查后拒绝感叹号()
    {
        var v = new FileNameValidator(useMinecraftCharCheck: true);
        // PCL 原版传 ["!;"]（一个两字符的串），于是 "wow!" 根本命中不了 —— 那是原版的 bug。
        Assert.Contains(ValidationText.M(ValidationText.InvalidChar, "!"), MessagesOf(v, "wow!"));
    }

    [Fact]
    public void 文件名规则拒绝与父目录下现有文件重名()
    {
        var dir = Directory.CreateTempSubdirectory("umango-validate-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.txt"), "x");
            var v = new FileNameValidator(dir.FullName);
            Assert.Contains(ValidationText.M(ValidationText.DuplicateFile), MessagesOf(v, "a.txt"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 文件名规则在父文件夹不存在时报父目录缺失()
    {
        var missing = Path.Combine(Path.GetTempPath(), "umango-not-exist-" + Guid.NewGuid().ToString("n"));
        var v = new FileNameValidator(missing, requireParentFolderExists: true);
        Assert.False(v.Validate("a.txt").IsValid);
    }

    [Fact]
    public void 文件名规则在父文件夹不存在但允许时通过()
    {
        var missing = Path.Combine(Path.GetTempPath(), "umango-not-exist-" + Guid.NewGuid().ToString("n"));
        var v = new FileNameValidator(missing, requireParentFolderExists: false);
        Assert.True(v.Validate("a.txt").IsValid);
    }

    // ── 文件夹名 ──────────────────────────────────────────

    [Fact]
    public void 文件夹名规则接受普通名称()
    {
        var v = new FolderNameValidator();
        Assert.True(v.Validate("MyFolder").IsValid);
    }

    [Fact]
    public void 文件夹名规则拒绝与父目录下现有目录重名()
    {
        var dir = Directory.CreateTempSubdirectory("umango-validate-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));
            var v = new FolderNameValidator(dir.FullName);
            Assert.Contains(ValidationText.M(ValidationText.DuplicateFolder), MessagesOf(v, "sub"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 文件夹名规则可忽略同名目录()
    {
        var dir = Directory.CreateTempSubdirectory("umango-validate-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));
            var v = new FolderNameValidator(dir.FullName, ignoreSameNameInParentFolder: true);
            Assert.True(v.Validate("sub").IsValid);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ── 路径 ──────────────────────────────────────────────

    [Fact]
    public void 路径规则接受正常路径()
    {
        var v = new FolderPathValidator();
        Assert.True(v.Validate("/tmp/some/folder").IsValid);
    }

    [Fact]
    public void 路径规则拒绝空串()
    {
        var v = new FolderPathValidator();
        Assert.Contains(ValidationText.M(ValidationText.Empty), MessagesOf(v, ""));
    }

    [Fact]
    public void 路径规则逐段检查并拒绝保留名()
    {
        var v = new FolderPathValidator();
        Assert.Contains(ValidationText.M(ValidationText.ReservedWord, "CON"), MessagesOf(v, "/tmp/CON/x"));
    }

    [Fact]
    public void 路径拆分会去掉根目录()
    {
        // 根 = Path.GetPathRoot：在 Windows 上是 "C:\\"，在 Unix 上只有 "/"。
        // 所以 Unix 上 "tmp" 是真实的一段（不是根的一部分），不能被剥掉。
        var segments = FolderPathValidator.GetSubPaths("/tmp/a/b");
        Assert.Equal(["tmp", "a", "b"], segments);
    }

    [Fact]
    public void 路径拆分会去掉末尾分隔符()
    {
        var segments = FolderPathValidator.GetSubPaths("/tmp/a/b/");
        Assert.Equal(["tmp", "a", "b"], segments);
    }
}
