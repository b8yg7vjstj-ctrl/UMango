namespace UMango.Core.Install;

/// <summary>
/// 安装阶段的统一命名。
///
/// 不同包管理器的措辞差别很大（npm 说 idealTree、brew 说 pouring、scoop 说 extracting），
/// 界面要按「下载方式」把它们归并成同一组子项，因此在这里统一命名 ——
/// 这也是「降低重复代码率」的一部分：六个适配器不需要各写一套中文文案。
/// </summary>
public static class StepNames
{
    public const string ResolveDependencies = "解析依赖";
    public const string FetchManifest = "获取清单";
    public const string Download = "下载";
    public const string Build = "编译";
    public const string LinkWrite = "链接与写入";

    /// <summary>子项固定展示顺序（阶段推进的一般顺序）</summary>
    public static readonly string[] Ordered =
        [ResolveDependencies, FetchManifest, Download, Build, LinkWrite];
}
