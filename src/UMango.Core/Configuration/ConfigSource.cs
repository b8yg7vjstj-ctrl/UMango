namespace UMango.Core.Configuration;

/// <summary>
/// 配置来源。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/ConfigSource.cs</c>（Apache-2.0），
/// 语义一致：同一个设置项可以来自不同文件，用户在设置页看到的仍然是同一项。
///
/// UMango 与 PCL 的差别：PCL 的 <c>GameInstance</c> 是"某个 Minecraft 实例目录下的配置"，
/// UMango 没有实例概念，但保留了来源这个维度 —— 将来做"每个包管理器一组独立设置"时
/// 直接复用 <see cref="GameInstance"/> 语义（按 <c>argument</c> 上下文区分）。
/// </summary>
public enum ConfigSource
{
    /// <summary>全局共享配置（跨程序实例共用，例如主题、动画开关）。</summary>
    Shared,

    /// <summary>加密的全局共享配置（放密钥、令牌这类不能明文落盘的东西）。</summary>
    SharedEncrypted,

    /// <summary>本地配置（只属于当前用户/当前安装）。</summary>
    Local,

    /// <summary>按上下文区分的独立配置（PCL 叫"游戏实例配置"）。</summary>
    GameInstance
}
