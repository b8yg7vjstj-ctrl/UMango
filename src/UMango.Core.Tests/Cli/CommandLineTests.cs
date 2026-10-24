using UMango.Core.Cli;
using Xunit;

namespace UMango.Core.Tests.Cli;

/// <summary>
/// 命令行解析的回归测试。
///
/// 这些用例同时锁住了「从 PCL-CE 迁来时修掉的缺陷」——
/// 每一处 <c>// 原版……</c> 注释都对应下面的一条用例。
/// </summary>
public class CommandLineTests
{
    // Parse 的第二个参数是「**args[0] 那一级的子命令定义**」，
    // 不是含主命令的整棵树 —— 这是 PCL-CE 的签名语义。
    private static readonly SubcommandDefinition[] Subs =
    [
        ("install", []),
        ("uninstall", []),
        ("search", [("source", [])]),
        ("config", [])
    ];

    private static CommandLine Parse(params string[] args) => CommandLine.Parse(args, Subs);

    // ── 基本形状 ────────────────────────────────────────────────

    [Fact]
    public void 第一个元素是主命令()
    {
        var line = Parse("umango");
        Assert.Equal("umango", line.CommandText);
        Assert.Null(line.Subcommand);
        Assert.Empty(line.Arguments);
        Assert.Empty(line.Positional);
    }

    [Fact]
    public void 空参数数组会抛ArgumentException而不是IndexOutOfRange()
    {
        // 原版直接取 args[0]，空数组会抛 IndexOutOfRangeException。
        Assert.Throws<ArgumentException>(() => CommandLine.Parse([], Subs));
    }

    [Fact]
    public void TryParse在空数组时返回false而不是抛异常()
    {
        Assert.False(CommandLine.TryParse([], out var line, Subs));
        Assert.Null(line);
    }

    // ── 位置参数（原版完全缺失）────────────────────────────────

    [Fact]
    public void 裸token进入位置参数而不是被当成参数键()
    {
        // 原版：裸 token 变成键，"1234" 会成为键 "1234"。
        var line = Parse("umango", "express", "4.17.0");
        Assert.Equal(["express", "4.17.0"], line.Positional);
        Assert.DoesNotContain("express", line.Arguments.Keys);
        Assert.DoesNotContain("4.17.0", line.Arguments.Keys);
    }

    [Fact]
    public void 已注册的子命令名优先于位置参数()
    {
        // 与 PCL-CE 一致：子命令名一旦匹配，就走子命令分支，
        // 不会再作为位置参数出现在父级里。
        var line = Parse("umango", "install", "express");
        Assert.Equal("install", line.SubcommandText);
        Assert.Equal(["express"], line.Subcommand!.Positional);
    }

    [Fact]
    public void 数字token也进入位置参数()
    {
        // 原版的测试明确断言 "1234" → 键 "1234"。这在 UMango 里是错的：
        // 版本号、端口号都是位置参数。
        var line = Parse("umango", "1234");
        Assert.Equal(["1234"], line.Positional);
        Assert.Empty(line.Arguments);
    }

    [Fact]
    public void 双横线之后的一切都是位置参数()
    {
        var line = Parse("umango", "--", "--not-a-flag", "-x");
        Assert.Equal(["--not-a-flag", "-x"], line.Positional);
        Assert.Empty(line.Arguments);
    }

    // ── --key=value（原版不支持）───────────────────────────────

    [Fact]
    public void 等号形式的参数被正确解析()
    {
        // 原版：`=` 不是特殊字符，"--key=value" 会被当成键 "key=value"。
        var line = Parse("umango", "--source=npm");
        Assert.Equal("npm", line.Arguments["source"].ValueText);
    }

    [Fact]
    public void 等号形式的值里可以再含等号()
    {
        var line = Parse("umango", "--query=a=b");
        Assert.Equal("a=b", line.Arguments["query"].ValueText);
    }

    [Fact]
    public void 等号形式的值仍会被推断类型()
    {
        var line = Parse("umango", "--limit=10");
        Assert.Equal(ArgumentValueKind.Decimal, line.Arguments["limit"].ValueKind);
        Assert.Equal(10m, line.Arguments["limit"].CastValue<decimal>());
    }

    // ── --key value ────────────────────────────────────────────

    [Fact]
    public void 空格形式的参数会吃掉下一个token()
    {
        var line = Parse("umango", "--source", "npm");
        Assert.Equal("npm", line.Arguments["source"].ValueText);
    }

    [Fact]
    public void 空格形式遇到下一个横线参数就不吃它的值()
    {
        // "--source --verbose"：--verbose 是另一个开关，
        // 不能被当成 --source 的值吃掉。
        var line = Parse("umango", "--source", "--verbose");
        Assert.True(line.Arguments["verbose"].CastValue<bool>());
        Assert.Equal(ArgumentValueKind.Bool, line.Arguments["source"].ValueKind);
        Assert.Equal(string.Empty, line.Arguments["source"].ValueText);
    }

    [Fact]
    public void 空格形式遇到子命令名就不吃它的值()
    {
        var line = Parse("umango", "--source", "install");
        Assert.Equal("install", line.SubcommandText);
        Assert.Equal(ArgumentValueKind.Bool, line.Arguments["source"].ValueKind);
        Assert.Equal(string.Empty, line.Arguments["source"].ValueText);
    }

    // ── 子命令 ─────────────────────────────────────────────────

    [Fact]
    public void 子命令会被解析成子树()
    {
        var line = Parse("umango", "search", "--source", "npm");
        Assert.Equal("search", line.SubcommandText);
        Assert.Equal("npm", line.Subcommand!.Arguments["source"].ValueText);
    }

    [Fact]
    public void 子命令名不会被记成参数键()
    {
        var line = Parse("umango", "install");
        Assert.DoesNotContain("install", line.Arguments.Keys);
    }

    [Fact]
    public void 多级子命令可以嵌套()
    {
        var line = Parse("umango", "search", "source", "--name", "x");
        Assert.Equal("search", line.SubcommandText);
        Assert.Equal("source", line.Subcommand!.SubcommandText);
        Assert.Equal("x", line.Subcommand.Subcommand!.Arguments["name"].ValueText);
    }

    // ── 重复键（原版静默覆盖）─────────────────────────────────

    [Fact]
    public void 重复键保留首次出现的值并记录下来()
    {
        // 原版：后者直接覆盖前者，用户写错了也没人知道。
        var line = Parse("umango", "--source", "npm", "--source", "yarn");
        Assert.Equal("npm", line.Arguments["source"].ValueText);
        Assert.Equal(["source"], line.DuplicateKeys);
    }

    [Fact]
    public void 没有重复键时列表为空()
    {
        var line = Parse("umango", "--source", "npm");
        Assert.Empty(line.DuplicateKeys);
    }

    // ── 值类型推断 ─────────────────────────────────────────────

    [Fact]
    public void 布尔值能被识别()
    {
        var line = Parse("umango", "--verbose", "true");
        Assert.Equal(ArgumentValueKind.Bool, line.Arguments["verbose"].ValueKind);
        Assert.True(line.Arguments["verbose"].CastValue<bool>());
    }

    [Theory]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("disable")]
    [InlineData("disabled")]
    [InlineData("false")]
    public void 否定词被识别为假而不是真(string word)
    {
        // 原版的口径是「不是 0 / false 就是 true」，
        // 于是 `--no-cache` 会被读成 true —— 正好和用户的意思相反。
        var line = Parse("umango", "--cache", word);
        Assert.False(line.Arguments["cache"].CastValue<bool>());
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("enable")]
    [InlineData("true")]
    public void 肯定词被识别为真(string word)
    {
        var line = Parse("umango", "--cache", word);
        Assert.True(line.Arguments["cache"].CastValue<bool>());
    }

    [Fact]
    public void 没有值的横线参数是开着的开关()
    {
        // 命令行里 flag 的通行语义：--verbose 就是打开 verbose。
        var line = Parse("umango", "--verbose");
        Assert.Equal(ArgumentValueKind.Bool, line.Arguments["verbose"].ValueKind);
        Assert.True(line.Arguments["verbose"].CastValue<bool>());
    }

    [Fact]
    public void 小数被识别为小数且与区域无关()
    {
        // 原版是 decimal.Parse(ValueText)，走当前区域：
        // 德语区域下 "1.5" 会读成 15。命令行里的数字必须区域无关。
        var line = Parse("umango", "--limit", "1.5");
        Assert.Equal(ArgumentValueKind.Decimal, line.Arguments["limit"].ValueKind);
        Assert.Equal(1.5m, line.Arguments["limit"].CastValue<decimal>());
    }

    [Fact]
    public void 带千分位逗号的数字被当成文本()
    {
        // "1,5" 在有些区域是 1.5，在有些区域是 15 —— 命令行里应当一律当文本。
        var line = Parse("umango", "--limit", "1,5");
        Assert.Equal(ArgumentValueKind.Text, line.Arguments["limit"].ValueKind);
    }

    [Fact]
    public void 普通词被识别为文本()
    {
        var line = Parse("umango", "--source", "npm");
        Assert.Equal(ArgumentValueKind.Text, line.Arguments["source"].ValueKind);
        Assert.Equal("npm", line.Arguments["source"].CastValue<string>());
    }

    [Fact]
    public void 尝试取值能区分不存在与类型不匹配()
    {
        var line = Parse("umango", "--source", "npm");

        var (ok, matchedText) = line.TryGetArgumentValue<string>("source", out var s);
        Assert.True(ok);
        Assert.True(matchedText);
        Assert.Equal("npm", s);

        var (exists, matched) = line.TryGetArgumentValue<int>("source", out _);
        Assert.True(exists);
        Assert.False(matched);

        var (exists2, matched2) = line.TryGetArgumentValue<int>("missing", out _);
        Assert.False(exists2);
        Assert.False(matched2);
    }

    // ── 区域无关（PCL 原版在这里是区域敏感的）──────────────────

    [Fact]
    public void 小数解析不受当前区域影响()
    {
        // 原版是 decimal.Parse(ValueText)，走**当前区域**：
        // 德语区域下 "1.5" 会被读成 15、"1,5" 被读成 1.5。
        // 命令行里的数字必须区域无关，否则同一份脚本在不同机器上结果不同。
        //
        // 这条用例必须显式切区域：测试宿主默认区域是 invariant，
        // 在那种环境下原版的行为和修好的行为一模一样 —— 缺陷是隐形的。
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // de-DE：千分位是 .，小数点是 ,
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var line = Parse("umango", "--limit", "1.5");
            Assert.Equal(ArgumentValueKind.Decimal, line.Arguments["limit"].ValueKind);
            Assert.Equal(1.5m, line.Arguments["limit"].CastValue<decimal>());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void 布尔判定不受当前区域影响()
    {
        // tr-TR 的坑：土耳其语里 "I" 的小写是点号不含点，
        // 于是 ToLower("I") 不等于 "i"。
        // 一定要用含字母 I 的否定词（"disable"）才能暴露这个问题 ——
        // 用 "true" / "false" 是测不出来的（它们没有 I）。
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            // 必须用**大写**输入：土耳其语的坑只在大写 I 上发作
            // （小写 i 变小写还是 i）。PCL 原版用 ToLower()，
            // 在 tr-TR 下 "DISABLE" 会变成 "dısable" → 匹配不上否定词 → 被读成真。
            var line = Parse("umango", "--cache", "DISABLE");
            Assert.False(line.Arguments["cache"].CastValue<bool>());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void 直接构造的小数参数也按不变区域解析()
    {
        // 解析器在推断类型时会直接把值塞进 DecimalArgument.Value，
        // 于是 DecimalArgument.ParseValueText 平时不会被走到 ——
        // 但它是 public 契约的一部分，外部代码可以只给 ValueText。
        // 这条用例守住那条路径。
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var arg = new DecimalArgument { Key = "limit", ValueText = "1.5" };
            Assert.Equal(1.5m, arg.CastValue<decimal>());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void 直接构造的布尔参数也按不变区域解析()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var arg = new BoolArgument { Key = "cache", ValueText = "DISABLE" };
            Assert.False(arg.CastValue<bool>());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    // ── JSON 往返 ──────────────────────────────────────────────

    [Fact]
    public void JSON往返不丢信息()
    {
        var line = Parse("umango", "express", "react", "--source", "npm", "--source", "yarn");
        var json = System.Text.Json.JsonSerializer.Serialize(line);
        var back = System.Text.Json.JsonSerializer.Deserialize<CommandLine>(json);

        Assert.NotNull(back);
        Assert.Equal("umango", back!.CommandText);
        Assert.Equal(["express", "react"], back.Positional);
        Assert.Equal("npm", back.Arguments["source"].ValueText);
        Assert.Equal(["source"], back.DuplicateKeys);
    }

    [Fact]
    public void JSON往返保留子命令()
    {
        var line = Parse("umango", "search", "--source", "npm");
        var json = System.Text.Json.JsonSerializer.Serialize(line);
        var back = System.Text.Json.JsonSerializer.Deserialize<CommandLine>(json);

        Assert.NotNull(back);
        Assert.Equal("search", back!.SubcommandText);
        Assert.Equal("npm", back.Subcommand!.Arguments["source"].ValueText);
    }
}
