using UMango.Core.Configuration;

namespace UMango.Core.Tests.Configuration;

/// <summary>
/// 配置观察者 → 应用动作（对应 PCL-CE 的 <c>ModSetup.cs</c> 观察者模式）。
///
/// <para>这些用例的重点不是"能不能改值"，而是<b>顺序、完整性、失败可见性</b>：
/// PCL 的 <c>ApplyAll()</c> 靠手写列举，少写一项会静默漏掉；
/// UMango 从登记表按注册顺序自动枚举 —— 用例要把这个差异钉住。</para>
/// </summary>
public sealed class SetupApplierTests
{
    [Fact]
    public void 登记后可按键应用()
    {
        var applier = new SetupApplier();
        var applied = 0;
        applier.Register("a", _ => applied++);

        Assert.True(applier.Apply("a", 42));
        Assert.Equal(1, applied);
    }

    [Fact]
    public void 未登记的键返回false且不抛异常()
    {
        var applier = new SetupApplier();

        Assert.False(applier.Apply("missing", 1));
        Assert.False(applier.Contains("missing"));
    }

    [Fact]
    public void 键为空或重复登记时抛异常()
    {
        var applier = new SetupApplier();
        applier.Register("a", _ => { });

        Assert.Throws<ArgumentException>(() => applier.Register("  ", _ => { }));
        Assert.Throws<ArgumentException>(() => applier.Register("a", _ => { }));
    }

    [Fact]
    public void 应用动作为null时抛异常()
    {
        var applier = new SetupApplier();
        Assert.Throws<ArgumentNullException>(() => applier.Register("a", null!));
    }

    [Fact]
    public void ApplyAll按注册顺序应用()
    {
        var applier = new SetupApplier();
        var order = new List<string>();
        applier.Register("first", _ => order.Add("first"));
        applier.Register("second", _ => order.Add("second"));
        applier.Register("third", _ => order.Add("third"));

        var count = applier.ApplyAll(_ => null);

        Assert.Equal(3, count);
        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public void ApplyAll读取的值来自调用方委托()
    {
        var applier = new SetupApplier();
        object? seen = null;
        applier.Register("a", v => seen = v);

        applier.ApplyAll(key => key + "!");

        Assert.Equal("a!", seen);
    }

    [Fact]
    public void OnChanged只处理已登记的键()
    {
        var applier = new SetupApplier();
        var applied = 0;
        applier.Register("a", _ => applied++);

        Assert.False(applier.OnChanged("b", _ => null));
        Assert.Equal(0, applied);

        Assert.True(applier.OnChanged("a", _ => 1));
        Assert.Equal(1, applied);
    }

    [Fact]
    public void OnChanged的读取委托为null时抛异常()
    {
        var applier = new SetupApplier();
        Assert.Throws<ArgumentNullException>(() => applier.OnChanged("a", null!));
    }

    [Fact]
    public void 应用动作抛异常时不吞掉()
    {
        var applier = new SetupApplier();
        applier.Register("boom", _ => throw new InvalidOperationException("炸了"));

        var ex = Assert.Throws<InvalidOperationException>(() => applier.Apply("boom", null));
        Assert.Equal("炸了", ex.Message);
    }

    [Fact]
    public void ApplyAll中途抛异常时后续项不再应用()
    {
        var applier = new SetupApplier();
        var later = 0;
        applier.Register("boom", _ => throw new InvalidOperationException());
        applier.Register("later", _ => later++);

        Assert.Throws<InvalidOperationException>(() => applier.ApplyAll(_ => null));
        Assert.Equal(0, later);
    }

    [Fact]
    public void Keys按注册顺序列出且Count一致()
    {
        var applier = new SetupApplier();
        applier.Register("a", _ => { });
        applier.Register("b", _ => { });

        Assert.Equal(["a", "b"], applier.Keys);
        Assert.Equal(2, applier.Count);
    }
}
