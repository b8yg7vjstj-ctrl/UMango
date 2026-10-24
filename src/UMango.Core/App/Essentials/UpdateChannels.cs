using System;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.App.Essentials;

/// <summary>
/// 更新通道。PCL-CE 用 <c>UpdateChannel.stable / beta</c> 两个值，
/// 这里保留同样的两档，但**不引入 PCL 的"当前版本是不是 beta"那套联动逻辑**
/// （PCL 的 beta 状态由「版本号里带 beta」或「用户在设置里选了 beta」共同决定，
/// 且在通道不匹配时会拿另一个通道的版本比较 —— 对 UMango 而言这是自找麻烦）。
/// </summary>
public enum UpdateChannelKind
{
    /// <summary>正式版通道：只接受非预发布版本。</summary>
    Stable,

    /// <summary>预发布通道：接受预发布版本，也接受正式版（正式版通常更新）。</summary>
    Prerelease
}

/// <summary>更新通道的展示信息。</summary>
public static class UpdateChannelKindExtensions
{
    public static string Id(this UpdateChannelKind c) => c == UpdateChannelKind.Stable ? "stable" : "prerelease";

    public static bool AcceptsPrerelease(this UpdateChannelKind c) => c == UpdateChannelKind.Prerelease;
}

/// <summary>
/// 一个版本源的查询结果。
///
/// 用对象联合（成功 / 失败）而不是异常流，是因为"某个源不可用"在多源场景下
/// 是**正常状态**而不是错误 —— 上层要做的只是「记下来，试下一个」。
/// </summary>
public sealed class UpdateSourceResult
{
    private UpdateSourceResult(UpdateInfo? info, UpdateCheckFailure? failure)
    {
        Info = info;
        Failure = failure;
    }

    public UpdateInfo? Info { get; }
    public UpdateCheckFailure? Failure { get; }
    public bool IsSuccess => Info is not null;

    public static UpdateSourceResult Success(UpdateInfo info) =>
        new UpdateSourceResult(info ?? throw new ArgumentNullException(nameof(info)), null);

    public static UpdateSourceResult Failed(UpdateCheckFailure failure) =>
        new UpdateSourceResult(null, failure ?? throw new ArgumentNullException(nameof(failure)));

    public static UpdateSourceResult Failed(UpdateCheckError error, string message, Exception? inner = null) =>
        Failed(new UpdateCheckFailure(error, message, inner));

    public override string ToString() => IsSuccess
        ? $"成功：{Info!.Version}"
        : $"失败：{Failure!.Error} / {Failure.Message}";
}

/// <summary>
/// 版本源。UMango 的多通道自更新抽象。
///
/// **与 PCL-CE 的关系**：PCL 的 <c>IUpdateSource</c>（<c>Modules/Updates/</c>，受限许可目录）
/// 只能借鉴架构、不能复制代码，因此这里按同一套语义**重新实现**：
/// ① 每个源负责回答"最新版本是什么"；
/// ② 上层用 <see cref="UpdateChannelChain"/> 把多个源串成降级链。
///
/// 与 PCL 版的差异：
/// ① PCL 的 <c>IUpdateSource</c> 是**同步**接口（内部用 <c>.Result</c> 阻塞），
///    这里全异步 —— 阻塞式 UI 线程上的网络调用是 PCL 老版本的著名卡顿源；
/// ② PCL 的源返回自己的 <c>VersionDataModel</c>，这里统一返回
///    <see cref="UpdateInfo"/>，免得每个源各自定义一套版本号语义。
/// </summary>
public interface IUpdateSource
{
    /// <summary>源的名称（用于日志与界面展示）。</summary>
    string Name { get; }

    /// <summary>
    /// 本机是否有可能用这个源（比如需要某个工具存在、或者配置里没启用）。
    /// 返回 false 的源会被直接跳过，**不会**产生网络请求。
    /// </summary>
    bool IsAvailable();

    /// <summary>查询指定通道的最新版本。</summary>
    Task<UpdateSourceResult> FetchAsync(UpdateChannelKind channel, string localVersion, CancellationToken ct = default);
}
