// SPDX-License-Identifier: Apache-2.0
//
// WPF 侧的剪贴板实现（Shell 的 <see cref="IClipboardService"/> 的 Windows 版本）。

using System.Windows;
using UMango.Shell.Capabilities;

namespace UMango.App.Services;

/// <summary>
/// WPF 剪贴板。
///
/// <para>
/// **必须在 UI 线程上调用** —— WPF 的 <c>Clipboard.SetText</c> 会在非 UI 线程时抛
/// <c>COMException</c>（"Cannot access the Clipboard"）。因此这里做一次线程编组。
/// </para>
/// </summary>
public sealed class WpfClipboardService : IClipboardService
{
    /// <summary>把文本写入剪贴板。</summary>
    public void SetText(string text)
    {
        var app = Application.Current;
        if (app is null) return;

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => SetText(text));
            return;
        }

        try
        {
            Clipboard.SetText(text ?? string.Empty);
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占着时会抛 —— 复制失败不该让界面崩。
        }
    }
}
