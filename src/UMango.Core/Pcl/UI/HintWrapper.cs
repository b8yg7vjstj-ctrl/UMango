// ============================================================================
// 本文件逐字采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）
//   原路径：PCL.Core/UI/HintWrapper.cs（36 行）
//   许可：Apache-2.0（PCL.Core 层），允许复用；SPDX-License-Identifier: Apache-2.0
// UMango 采用改动：无（逐字）。依赖 Lang 见 Pcl/App/Localization/Lang.cs 的等价桥。
// ============================================================================
namespace PCL.Core.UI;

public enum HintTheme
{
    Info,
    Success,
    Error,
    Warning
}

public delegate void HintHandler(
    string message,
    HintTheme theme
);

public static class HintWrapper
{
    public static event HintHandler? OnShow;

    public static void Show(string message, HintTheme theme = HintTheme.Info)
    {
        OnShow?.Invoke(message, theme);
    }
}
