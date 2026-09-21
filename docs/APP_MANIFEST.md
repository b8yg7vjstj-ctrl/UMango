# app.manifest 的结构约束（真机踩坑记录）

`src/UMango.App/app.manifest` **必须保持极简**，不要在文件里加长注释。原因见下。

## 曾经的错误

第一版把 `<supportedOS>` 放进了 `<application>/<windowsSettings>` 里：

```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAwareness .../>
    <supportedOS Id="{8e0f7a12-...}"/>   <!-- ❌ 放错了 -->
  </windowsSettings>
</application>
```

真机启动时报（SideBySide 事件日志）：

> Activation context generation failed for "UMango.exe".
> Error in manifest or policy file on line 10.
> The element **supportedOS** appears as a child of element
> **urn:schemas-microsoft-com:asm.v1^windowsSettings** which is not supported by this version of Windows.

原因是**命名空间归属不同**：

| 元素 | 所属命名空间 | 正确父元素 |
|------|-------------|-----------|
| `<supportedOS>` | `urn:schemas-microsoft-com:compatibility.v1` | `<compatibility><application>` |
| `<dpiAware>` / `<dpiAwareness>` | `urn:schemas-microsoft-com:asm.v3` | `<application><windowsSettings>` |

混放会让 Windows 把元素解析成 `asm.v1^windowsSettings` 并**直接拒绝加载整个应用**。

## 为什么注释也要清掉

第二版在 manifest 里写了一段长中文注释解释这个坑，**结果仍然启动失败**，
日志里的行号（第 10 行）正好指向注释内容 —— 说明带这类内容的清单文件在解析时出了问题。
因此本文件的教训是：

1. **manifest 保持极简，不放注释**；解释放本文件与代码提交信息里；
2. **改动 manifest 后必须在真机实跑一次** —— `dotnet build`、单元测试、
   XAML 静态审计**都发现不了**这类错误（清单是运行期才被 Windows 读取的）。
   这条缺陷就是只有真机才暴露出来的典型例子。

## 相关文件

- `src/UMango.App/app.manifest` —— 实际清单
- `src/UMango.App/UMango.App.csproj` —— 通过 `<ApplicationManifest>` 引用
