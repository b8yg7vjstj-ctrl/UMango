namespace UMango.Core.Configuration;

/// <summary>设置项的值类型。决定界面用什么控件，以及从 JSON 读回时怎么还原。</summary>
public enum SettingKind
{
    Bool,
    Int,
    Double,
    String,
    /// <summary>枚举（取值由 <see cref="SettingDescriptor.EnumType"/> 给出）</summary>
    Enum
}

/// <summary>
/// 一个设置项的**完整描述**。
///
/// 思路取自 PCL-CE 的配置体系（PCL.Core/App/Configuration，Apache-2.0）：
/// 那里用 <c>[ConfigItem]</c> 特性 + 源生成器把设置声明成属性。
/// UMango 取同样的核心思想（**描述符驱动**），但不用源生成器 ——
/// 描述符是普通对象，于是能在 macOS 上被单测覆盖，也不引入编译期魔法。
///
/// **最大的收益**：设置页可以由描述符列表生成，不必为每个设置项手写一段 XAML；
/// 新增一个设置 = 新增一个描述符，而不是"Core 加字段 + App 加属性 + XAML 加控件"三处改。
/// </summary>
public sealed record SettingDescriptor
{
    /// <summary>稳定键名（形如 ui.animation.enabled）。落盘与迁移都依赖它，改动即破坏兼容</summary>
    public required string Key { get; init; }

    /// <summary>分组（设置页的一张卡片）</summary>
    public required string Section { get; init; }

    /// <summary>界面标题</summary>
    public required string Title { get; init; }

    /// <summary>界面说明（写清"改了会怎样"）</summary>
    public string? Description { get; init; }

    public required SettingKind Kind { get; init; }

    /// <summary>默认值。**必须与 Kind 匹配**（由 Validate 校验）</summary>
    public required object? DefaultValue { get; init; }

    /// <summary>数值范围（仅 Int/Double）</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }

    /// <summary>枚举类型（仅 Kind=Enum）</summary>
    public Type? EnumType { get; init; }

    /// <summary>组内排序（小的在前）</summary>
    public int Order { get; init; }

    /// <summary>
    /// 该设置是否需要重启才生效。
    /// 界面据此给用户提示 —— 悄悄要求重启却不告知，是设置页最常见的体验问题。
    /// </summary>
    public bool RequiresRestart { get; init; }

    /// <summary>自检：描述符自身是否自洽。注册时就调用，把错误挡在开发期。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key)) throw new ArgumentException("设置键不能为空");
        if (string.IsNullOrWhiteSpace(Section)) throw new ArgumentException($"{Key}: 分组不能为空");
        if (string.IsNullOrWhiteSpace(Title)) throw new ArgumentException($"{Key}: 标题不能为空");

        var ok = Kind switch
        {
            SettingKind.Bool => DefaultValue is bool,
            SettingKind.Int => DefaultValue is int,
            SettingKind.Double => DefaultValue is double,
            SettingKind.String => DefaultValue is null or string,
            SettingKind.Enum => DefaultValue is not null && EnumType is not null
                                && EnumType.IsEnum && EnumType.IsInstanceOfType(DefaultValue),
            _ => false
        };
        if (!ok)
            throw new ArgumentException($"{Key}: 默认值 {DefaultValue?.GetType().Name ?? "null"} 与类型 {Kind} 不匹配");

        if (Kind == SettingKind.Enum && !Enum.IsDefined(EnumType!, DefaultValue!))
            throw new ArgumentException($"{Key}: 默认值不是 {EnumType!.Name} 的合法取值");
    }
}
