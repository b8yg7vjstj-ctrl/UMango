// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/IconManager.cs 与 PCL.Core/UI/IconModel.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Icons；
//   2. 原版 `IconModel` 装的是 WPF `UIElement`（XamlReader.Parse 的产物），
//      把 Core 绑死在 WPF 上 → 改为**平台中立的图标模型**（`IconModel` = 名称 + 矢量描述），
//      WPF 的 XAML 装载留在 App 层（原版的 `TryLoadIconFromXaml` / `LoadIconFromXaml`）；
//   3. 原版 `IconManager` 继承 `INotifyPropertyChanged` 但 `SelectedIcon` 的 setter
//      只在**值不同**时才通知，且用的是 `field` 关键字 —— 保留其语义；
//   4. 修掉原版两个真实缺陷（见下方各条注释）。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UMango.Core.UI.Svg;

namespace UMango.Core.UI.Icons;

/// <summary>
/// 一个已登记的图标。
///
/// PCL 原版是 <c>record IconModel(string Name, UIElement? XamlContent)</c> ——
/// 装的是 WPF 控件。这里改为**平台中立的矢量描述**，
/// 由各宿主（WPF / Avalonia）自行渲染。
/// </summary>
/// <param name="Name">图标名（登记时用的键）。</param>
/// <param name="Source">图标的矢量描述（宿主无关）。</param>
public sealed record IconModel(string Name, IconSource? Source)
{
    /// <summary>图标是否可用（有实际内容）。</summary>
    public bool HasContent => Source is not null;
}

/// <summary>
/// 图标的矢量描述（宿主无关）。
///
/// 用的是 Core 里已有的 SVG 解析结果（<see cref="Svg.SvgIconModel"/>），
/// 宿主（WPF / Avalonia）自行把它画成图元。
/// </summary>
/// <param name="Width">画布宽（用户单位）。</param>
/// <param name="Height">画布高（用户单位）。</param>
/// <param name="Shapes">图元列表。</param>
public sealed record IconSource(
    double Width,
    double Height,
    IReadOnlyList<Svg.SvgShape> Shapes)
{
    /// <summary>
    /// 从 SVG 源码构造。
    ///
    /// 解析失败（非法 XML、缺根节点等）返回 null —— 图标加载失败不该炸掉整个界面。
    /// </summary>
    public static IconSource? FromSvg(string? svg)
    {
        if (string.IsNullOrWhiteSpace(svg)) return null;
        try
        {
            var root = Svg.SvgSourceParser.ParseSource(svg);
            var model = Svg.SvgDocumentParser.Parse(root);
            return new IconSource(model.Width, model.Height, model.Shapes);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// 图标登记表：按名字登记图标，并跟踪「当前选中」。
///
/// PCL 原版 <c>PCL.Core/UI/IconManager.cs</c>（84 行，Apache-2.0）。
/// 本类是它的**平台中立等价物**（原版依赖 WPF 的 <c>Application.Current</c> 与 <c>XamlReader</c>）。
/// </summary>
public class IconRegistry : INotifyPropertyChanged
{
    private readonly Dictionary<string, IconModel> _index;

    /// <summary>创建登记表。</summary>
    /// <param name="caseInsensitive">
    /// 名字是否**大小写不敏感**。
    /// UMango 的 SVG 图标缓存用 <see cref="StringComparer.OrdinalIgnoreCase"/>，
    /// 这里保持一致 —— 否则「界面上写 <c>Home</c>、登记的是 <c>home</c>」会静默取不到。
    /// </param>
    public IconRegistry(bool caseInsensitive = true)
    {
        _index = new Dictionary<string, IconModel>(
            caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>当前选中的图标。</summary>
    public IconModel? SelectedIcon
    {
        get => _selectedIcon;
        set
        {
            if (ReferenceEquals(_selectedIcon, value)) return;
            _selectedIcon = value;
            OnPropertyChanged(nameof(SelectedIcon));
        }
    }

    private IconModel? _selectedIcon;

    /// <summary>登记的图标数量。</summary>
    public int Count => _index.Count;

    /// <summary>全部登记的图标名（按登记顺序外的字典序，无序）。</summary>
    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_index.Keys;

    /// <summary>按名字取图标。</summary>
    public bool TryGet(string? name, out IconModel? model)
    {
        model = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _index.TryGetValue(name, out model);
    }

    /// <summary>
    /// 按名字选中图标。
    ///
    /// 找不到就**不动**当前选中（返回 false），而不是把选中清空 ——
    /// PCL 原版就是这个行为。
    /// </summary>
    public bool SetSelectedIconByName(string? name)
    {
        if (!TryGet(name, out var model) || model is null) return false;
        SelectedIcon = model;
        return true;
    }

    /// <summary>
    /// 登记一个图标。
    ///
    /// 名字为空/空白、或名字已存在 → 返回 false（**不覆盖**）。
    /// PCL 原版同理（<c>if (string.IsNullOrWhiteSpace(name) || _iconIndex.ContainsKey(name)) return false;</c>）。
    /// </summary>
    public bool AddIcon(string? name, IconSource? source)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (_index.ContainsKey(name)) return false;
        _index[name] = new IconModel(name, source);
        return true;
    }

    /// <summary>
    /// 移除一个图标。
    ///
    /// ⚠️ **修掉的真实缺陷**：PCL 原版 <c>RemoveIconByName</c> 只从字典里删，
    /// **不管当前选中是不是它** —— 于是「选中」会一直指着一个已经移除的图标。
    /// 这里在移除时把选中一并清掉（除非选中的是别的图标）。
    /// </summary>
    public bool RemoveIconByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!_index.Remove(name)) return false;
        if (_selectedIcon is not null
            && string.Equals(_selectedIcon.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            SelectedIcon = null;
        }
        return true;
    }

    /// <summary>清空登记表（含当前选中）。</summary>
    public void Clear()
    {
        _index.Clear();
        SelectedIcon = null;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变更通知。</summary>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
