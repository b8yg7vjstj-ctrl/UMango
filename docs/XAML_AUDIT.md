# XAML 静态审计工具

## 为什么需要它

本项目在 **macOS 上开发**，而 **WPF 只能在 Windows 上运行**（`Microsoft.WindowsDesktop.App` 运行时在 macOS 上不存在，实测跑测试宿主会直接失败）。

后果是：XAML 里有一大类错误**在本机既编译不过关、也测不出来**，只有到 Windows 上跑起来才会暴露：

| 错误类型 | 运行时表现 | 编译器是否报错 |
|---------|-----------|--------------|
| `{Binding Path}` 拼错 / 属性不存在 | 静默不显示数据，或绑定报错写进输出窗口 | ❌ 不报（绑定靠运行时反射解析） |
| `{DynamicResource Key}` 键不存在 | **静默不赋值** —— 颜色/尺寸退回 WPF 默认值 | ❌ 不报 |
| `{StaticResource Key}` 键不存在 | 抛 `ResourceReferenceKeyNotFoundException` | ❌ 不报（要等到加载 XAML） |
| XAML 挂的事件处理器没写 | 抛 `XamlParseException` | ❌ 不报 |
| **四套主题字典键不一致** | 切到那套主题时部分 token 静默失效 | ❌ 不报 |

`UMango.App` 里这类引用共 **800+ 处**。这个工具把它们变成**可在 macOS 上检查的静态问题**。

## 怎么用

```bash
scripts/xaml-audit.sh              # 审计
scripts/xaml-audit.sh --dump-keys  # 诊断：打印每个文件收集到的资源键与模板作用域
```

退出码：`0` = 无 ERROR（可直接用于 CI），`1` = 有 ERROR。

## 它检查什么

| # | 检查 | 严重度 | 说明 |
|---|------|-------|------|
| 1 | 资源键引用是否存在 | ERROR | 在全部 XAML 收集到的定义里查找 |
| 2 | **四套主题字典键是否一致** | ERROR | 换主题的根基；缺键会静默失效 |
| 3 | XAML 事件处理器在 code-behind 是否存在 | ERROR | 按 `void`/`async void` + 方法名匹配 |
| 4 | 绑定路径是否存在 | ERROR | 见下节的作用域解析 |

WARN 目前不产生（曾有一个"x:Name 对不上"的检查，**已删除** —— 它 115 条全是误报，把命名空间片段、枚举取值、类型名都当成了控件名。一个 100% 误报的检查比没有检查更糟，会训练人忽略审计输出）。

## 绑定作用域是怎么解析的

绑定路径的校验必须知道"这一处的 DataContext 是什么"。工具按下面的顺序推断，**任何一步拿不到就放弃检查**（宁可不查，也不要拿错类型去校验、制造一片假报错）：

1. **页面根作用域**：从 code-behind 里找 `DataContext = new Xxx(...)`，得到 ViewModel 类型名；
2. **集合内的元素作用域**：容器上 `ItemsSource="{Binding Prop}"` → 查 ViewModel 的 `Prop` 声明类型 → 取泛型元素类型（`IReadOnlyList<DownloadEngineInfo>` → `DownloadEngineInfo`）；
3. **按 key 引用的模板**：`ItemTemplate="{StaticResource Xxx}"` 的模板定义点通常拿不到引用处信息，所以先把「模板键 → 元素类型」记下来，再去校验该模板内部的绑定；
4. **内联模板**（无 `x:Key` 的 `DataTemplate`）：直接用结构上的嵌套关系。

## 实现要点（都是踩过的坑）

- **不加载 WPF 程序集**：工具自身是 `net10.0` 且不引用 WPF，C# 侧用 **Roslyn 只做语法分析**（`CSharpSyntaxTree.ParseText`），XAML 侧用 `XDocument`。纯文本处理，所以能在 macOS 上跑。
- **`record` 的位置参数要当成属性收集**：漏掉时 `record DownloadEngineInfo(string Id, bool IsAvailable, ...)` 会被视为"一个属性都没有"，模板里的绑定全被误报。
- **`DataTemplate` 自己也要走遍历**：曾经把它当"纯容器、只遍历子元素"，结果它自己的 `x:Key` 收不进定义集合，所有 `ItemTemplate="{StaticResource Xxx}"` 全被误报。（线索：`<Style>` 没被特殊对待，所以只有 Style 的键能收到 —— 这个不对称暴露了问题。）
- **`[ObservableProperty]` 字段要映射成属性名**：`_fooBar` / `m_fooBar` → `FooBar`。CommunityToolkit 的源生成器会在编译期产出这些属性，纯语法分析看不到，必须自己推。

## 有效性验证（注入缺陷）

工具本身必须被验证 —— 否则可能只是个"永远说 OK"的东西。以下四类缺陷都**实际注入过并确认被抓到**：

| 注入的缺陷 | 结果 |
|-----------|------|
| 把 `{Binding ProtocolsText}` 改成 `{Binding ProtcolsText}` | ✅ 抓到 |
| 把 `{DynamicResource BrG8}` 改成 `{DynamicResource BrNotAThing}` | ✅ 抓到 |
| 把 `SelectionChanged` 的处理器名改成不存在的方法 | ✅ 抓到 |
| **只**在四套主题字典中的一套里把 `BrC3` 改名 | ✅ 抓到（由检查 #2 命中） |

第 4 项最初**漏掉了** —— 因为"键是否存在"看的是全局并集，只改一套字典时并集里仍然有该键。正是这次漏报促成了检查 #2（四套字典键一致性）的加入。

## 它抓到的真实缺陷

- **`BrRedBack` / `BrRedDark` 不存在**：`DownloaderView.xaml` 的错误提示条引用了这两个键，而主题字典里只有 `BrRed`。`DynamicResource` 找不到键时静默不赋值 —— 那个错误提示条**不会有背景色、文字也是默认色**，即"错误提示看起来不像错误"。已补 `BrRedBack` 到四套字典（四套 key 校验仍一致），`BrRedDark` 改用已有的 `BrRed`。

## 已知不覆盖的范围

- 不做 XAML 语法/类型检查（那是编译器的活）；
- 不校验 `DataTrigger` / `MultiBinding` 内部的复杂路径；
- 不校验 `Style`/`ControlTemplate` 内的 `TemplateBinding`（它绑的是模板父级，语义不同）；
- 不校验跨文件的 `ResourceDictionary` 动态合并（只在静态声明范围内查找）。
