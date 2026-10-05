using Avalonia;
using System;

namespace UMango.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Avalonia 应用配置。
    /// 与 WPF 版共享全部业务（UMango.Core），仅 UI 壳按平台各建一份 ——
    /// 这是「先 WPF 跑通全功能，再移植 Avalonia」路线的落地。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
