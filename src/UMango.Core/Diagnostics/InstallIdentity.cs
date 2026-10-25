// SPDX-License-Identifier: Apache-2.0
//
// 安装标识 —— 用于把同一个安装实例的崩溃报告串起来。
//
// 与 PCL-CE `PCL.Core/Utils/Secret/Identify.cs` 的**实质差异**（隐私）：
//   PCL 原版用 WMI 读取主板 UUID / 主板序列号 / CPU ProcessorId，
//   哈希成 `LauncherId` —— 这是**硬件指纹**，同一台机器换用户也相同。
//   UMango 不做机器指纹：这里生成一个**随机 GUID**，随安装目录存放，
//   卸载重装即失效，也不与任何硬件标识关联。

using System;
using System.IO;

namespace UMango.Core.Diagnostics;

/// <summary>安装标识：随机生成、落盘复用。</summary>
public static class InstallIdentity
{
    private static readonly object Gate = new();
    private static string? _cached;

    /// <summary>
    /// 读取安装标识；文件不存在或损坏时生成新的并写回。
    /// </summary>
    /// <param name="path">标识文件路径。</param>
    public static string GetOrCreate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("标识文件路径不能为空", nameof(path));

        lock (Gate)
        {
            if (_cached is not null) return _cached;
            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (existing.Length > 0)
                    {
                        _cached = existing;
                        return existing;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到就当作没有，落到下面重新生成
            }

            var generated = Guid.NewGuid().ToString("N");
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, generated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不进去也不该让程序起不来 —— 标识只是辅助信息
            }

            _cached = generated;
            return generated;
        }
    }

    /// <summary>清空缓存（测试用）。</summary>
    public static void ResetCache() => _cached = null;
}
