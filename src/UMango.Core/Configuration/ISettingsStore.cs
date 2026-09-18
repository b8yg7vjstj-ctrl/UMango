namespace UMango.Core.Configuration;

/// <summary>
/// 设置持久化抽象。把"存哪里、存成什么格式"与"设置有哪些、怎么校验"分开 ——
/// 于是设置逻辑可以脱离文件系统被单测（用内存实现即可）。
/// PCL-CE 那边对应的是 IConfigProvider 一族（JSON/YAML/INI 多种实现）。
/// </summary>
public interface ISettingsStore
{
    /// <summary>读取全部键值。文件不存在时返回空字典。</summary>
    IReadOnlyDictionary<string, object?> Load();

    /// <summary>写入全部键值。</summary>
    void Save(IReadOnlyDictionary<string, object?> values);
}
