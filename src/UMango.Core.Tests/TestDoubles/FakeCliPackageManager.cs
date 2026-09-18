namespace UMango.Core.Tests.TestDoubles;

/// <summary>
/// 包管理器适配器的测试替身。
///
/// 用途有两个：
///   1. <b>可控性</b>：真实的 Winget/Scoop 适配器在 macOS 上会被平台门控挡掉，
///      而"门控是否真的在探测之前返回、有没有去执行 CLI"需要一个能**计次**的观察点 ——
///      这里的 <see cref="ProbeCallCount"/> 就是那个观察点；
///   2. <b>暴露 protected 成员</b>：基类的容错解析（ParseJson）、"包不存在"归一化
///      （LooksLikeNotFound）、Truncate、Unsupported 都是 protected，
///      继承后包一层 public 静态方法即可直接断言，无需修改 UMango.Core。
/// </summary>
public sealed class FakeCliPackageManager : CliPackageManagerBase
{
    private readonly PackageSourceKind _kind;
    private readonly string _installDirectory;
    private readonly SourceCapabilities _capabilities;

    public FakeCliPackageManager(
        PackageSourceKind kind,
        string installDirectory = "/fake/install-dir",
        SourceCapabilities? capabilities = null)
    {
        _kind = kind;
        _installDirectory = installDirectory;
        _capabilities = capabilities ?? SourceCapabilities.None;
    }

    public override PackageSourceKind Kind => _kind;

    public override SourceCapabilities Capabilities => _capabilities;

    public override string InstallHint => $"{_kind.DisplayName()} 的测试安装指引";

    protected override string DefaultInstallDirectory => _installDirectory;

    /// <summary>
    /// ProbeAsync 被真实调用（进入基类探测流程）的次数。
    /// 平台不支持时它必须保持 0 —— 说明门控在"执行 CLI"之前就返回了。
    /// </summary>
    public int ProbeCallCount { get; private set; }

    public override Task<SourceAvailability> ProbeAsync(CancellationToken ct = default)
    {
        ProbeCallCount++;
        return base.ProbeAsync(ct);
    }

    // ---------- 把基类的 protected 工具暴露成 public（仅供测试断言） ----------

    /// <summary>容错 JSON 解析（CLI 会把警告混进 stdout）</summary>
    public static T? ParseJsonProbe<T>(string raw, string context) => ParseJson<T>(raw, context);

    /// <summary>"包不存在"归一化判断</summary>
    public static bool LooksLikeNotFoundProbe(string stderr, string stdout) =>
        LooksLikeNotFound(stderr, stdout);

    /// <summary>输出截断</summary>
    public static string TruncateProbe(string value, int max) => Truncate(value, max);

    /// <summary>统一"不支持"异常</summary>
    public ProcessException UnsupportedProbe(string feature) => Unsupported(feature);
}
