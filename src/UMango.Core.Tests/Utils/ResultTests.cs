// SPDX-License-Identifier: Apache-2.0
//
// PCL-CE 工具族（Result / RandomUtils / TimeUtils）的单元测试。
//
// 这三个文件是从 PCL.Core（Apache-2.0）逐字采用的，
// 测试要钉住它们的行为契约，尤其是**边界**。

using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests.Utils;

[Collection("静态状态")]
public class ResultTests
{
    [Fact]
    public void 成功结果可以取到值()
    {
        var r = Result<int, string>.Ok(42);
        Assert.True(r.IsSuccess);
        Assert.False(r.IsFailure);
        Assert.Equal(42, r.Value);
    }

    [Fact]
    public void 失败结果可以取到错误()
    {
        var r = Result<int, string>.Err("boom");
        Assert.False(r.IsSuccess);
        Assert.True(r.IsFailure);
        Assert.Equal("boom", r.Error);
    }

    [Fact]
    public void 成功结果取错误会抛异常()
    {
        var r = Result<int, string>.Ok(1);
        Assert.Throws<InvalidOperationException>(() => r.Error);
    }

    [Fact]
    public void 失败结果取值会抛异常()
    {
        var r = Result<int, string>.Err("e");
        Assert.Throws<InvalidOperationException>(() => r.Value);
    }

    [Fact]
    public void Match按状态选择分支()
    {
        Assert.Equal("ok:1", Result<int, string>.Ok(1).Match(v => $"ok:{v}", e => $"err:{e}"));
        Assert.Equal("err:e", Result<int, string>.Err("e").Match(v => $"ok:{v}", e => $"err:{e}"));
    }

    [Fact]
    public void IfSuccess只在成功时执行()
    {
        var hit = false;
        Result<int, string>.Ok(1).IfSuccess(_ => hit = true);
        Assert.True(hit);

        hit = false;
        Result<int, string>.Err("e").IfSuccess(_ => hit = true);
        Assert.False(hit);
    }

    [Fact]
    public void IfFailure只在失败时执行()
    {
        var hit = false;
        Result<int, string>.Err("e").IfFailure(_ => hit = true);
        Assert.True(hit);

        hit = false;
        Result<int, string>.Ok(1).IfFailure(_ => hit = true);
        Assert.False(hit);
    }

    [Fact]
    public void Map只映射成功值()
    {
        Assert.Equal(2, Result<int, string>.Ok(1).Map(v => v * 2).Value);
        Assert.Equal("e", Result<int, string>.Err("e").Map(v => v * 2).Error);
    }

    [Fact]
    public void MapError只映射错误()
    {
        Assert.Equal("E", Result<int, string>.Err("e").MapError(e => e.ToUpperInvariant()).Error);
        Assert.Equal(1, Result<int, string>.Ok(1).MapError(e => e.ToUpperInvariant()).Value);
    }

    [Fact]
    public void Bind链式传递成功值()
    {
        var r = Result<int, string>.Ok(2).Bind(v => Result<string, string>.Ok($"v={v}"));
        Assert.Equal("v=2", r.Value);
    }

    [Fact]
    public void Bind失败时短路不调用映射()
    {
        var called = false;
        var r = Result<int, string>.Err("e").Bind(v =>
        {
            called = true;
            return Result<string, string>.Ok("x");
        });
        Assert.False(called);
        Assert.Equal("e", r.Error);
    }

    [Fact]
    public void BindError只在失败时调用()
    {
        var called = false;
        var r = Result<int, string>.Ok(1).BindError(e =>
        {
            called = true;
            return Result<int, string>.Ok(9);
        });
        Assert.False(called);
        Assert.Equal(1, r.Value);
    }

    [Fact]
    public void OrElse在失败时给默认值()
    {
        Assert.Equal(5, Result<int, string>.Err("e").OrElse(5));
        Assert.Equal(1, Result<int, string>.Ok(1).OrElse(5));
    }

    [Fact]
    public void OrElse工厂只在失败时调用()
    {
        var called = false;
        Result<int, string>.Ok(1).OrElse(() =>
        {
            called = true;
            return 5;
        });
        Assert.False(called);
    }

    [Fact]
    public void Expect失败时抛指定消息()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Result<int, string>.Err("e").Expect("boom"));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void UnwrapOrThrow失败时抛指定异常类型()
    {
        var ex = Assert.Throws<TimeoutException>(() =>
            Result<int, string>.Err("e").UnwrapOrThrow(msg => new TimeoutException(msg)));
        Assert.Equal("e", ex.Message);
    }

    [Fact]
    public void 相同内容的成功结果相等()
    {
        Assert.Equal(Result<int, string>.Ok(1), Result<int, string>.Ok(1));
        Assert.Equal(Result<int, string>.Ok(1).GetHashCode(), Result<int, string>.Ok(1).GetHashCode());
    }

    [Fact]
    public void 相同内容的失败结果相等()
    {
        Assert.Equal(Result<int, string>.Err("e"), Result<int, string>.Err("e"));
    }

    [Fact]
    public void 成功与失败结果不相等()
    {
        Assert.NotEqual(Result<int, string>.Ok(1), Result<int, string>.Err("e"));
    }

    [Fact]
    public void ToString显示Ok或Err()
    {
        Assert.StartsWith("Ok(", Result<int, string>.Ok(1).ToString());
        Assert.StartsWith("Err(", Result<int, string>.Err("e").ToString());
    }
}
