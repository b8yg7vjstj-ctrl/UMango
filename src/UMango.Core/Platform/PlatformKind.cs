namespace UMango.Core.Platform;

/// <summary>目标平台。用于包源能力门控与窗口风格选择。</summary>
public enum PlatformKind
{
    Windows,
    MacOs,
    Linux
}

public static class PlatformKindExtensions
{
    public static string Id(this PlatformKind p) => p switch
    {
        PlatformKind.Windows => "windows",
        PlatformKind.MacOs => "macos",
        _ => "linux"
    };

    public static string DisplayName(this PlatformKind p) => p switch
    {
        PlatformKind.Windows => "Windows",
        PlatformKind.MacOs => "macOS",
        _ => "Linux"
    };
}
