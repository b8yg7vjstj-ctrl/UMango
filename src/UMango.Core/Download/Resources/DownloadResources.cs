// SPDX-License-Identifier: Apache-2.0
//
// 下载资源协调器的全局接入点。
//
// 与 `UMango.Core.Network.ProxyHolder` 同样的模式：**一个进程内共享的实例**，
// 供下载引擎在没有显式注入时使用（引擎是第三方工具调用方，拿不到 DI 容器）。
//
// 显式注入仍然优先：引擎实现若持有自己的 <see cref="DownloadResourceManager"/>，
// 就用它而不是这个全局实例。

using System;

namespace UMango.Core.Download.Resources;

/// <summary>全局下载资源协调器。</summary>
public static class DownloadResources
{
    private static DownloadResourceManager _current = new();

    /// <summary>
    /// 当前全局协调器。
    /// 赋值时会**释放旧实例**（避免额度泄漏）。
    /// </summary>
    public static DownloadResourceManager Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var old = Interlocked.Exchange(ref _current, value);
            if (!ReferenceEquals(old, value))
            {
                old.Dispose();
            }
        }
    }

    /// <summary>当前活跃连接数（供界面显示）。</summary>
    public static int ActiveConnectionCount => Current.ActiveConnectionCount;

    /// <summary>当前聚合下载速率（字节/秒，供界面显示）。</summary>
    public static long DownloadSpeed => Current.DownloadSpeed;
}
