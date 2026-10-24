// SPDX-License-Identifier: Apache-2.0
//
// 主页清单的**存储**与**热更新**。
//
// 对应 PCL-CE 的 `Pages/PageLaunch/PageLaunchRight.xaml.cs`（898 行，受限许可目录，
// 只能借鉴设计）。PCL 原版的做法是：监听远程 XAML 的下载、解压、版本比对，
// 写盘后再 `XamlReader.Parse` + 实例化。UMango 不加载远程 XAML（安全结论见
// HomepageManifest.cs 头部），这里只保留「用户放一个 JSON 文件在本地 → 界面自动更新」
// 这一层体验。

using System;
using System.IO;
using System.Text.Json;

namespace UMango.Core.UI.Homepage;

/// <summary>主页清单的存储服务。</summary>
public sealed class HomepageService
{
    /// <summary>默认清单文件名。</summary>
    public const string DefaultFileName = "homepage.json";

    /// <summary>清单文件路径。</summary>
    public string ManifestPath { get; }

    /// <summary>动作注册表。</summary>
    public HomepageActionRegistry Actions { get; }

    /// <summary>最近一次加载结果。</summary>
    public HomepageParseResult? LastResult { get; private set; }

    private FileSystemWatcher? _watcher;
    private readonly object _lock = new();

    /// <summary>创建服务。</summary>
    /// <param name="manifestPath">清单文件路径；为 null 时用默认路径。</param>
    /// <param name="actions">动作注册表；为 null 时建一个空的。</param>
    public HomepageService(string? manifestPath = null, HomepageActionRegistry? actions = null)
    {
        ManifestPath = manifestPath ?? Path.Combine(DefaultDirectory(), DefaultFileName);
        Actions = actions ?? new HomepageActionRegistry();
    }

    /// <summary>默认清单目录（用户配置目录下的 UMango 文件夹）。</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UMango");

    /// <summary>清单文件是否存在。</summary>
    public bool Exists => File.Exists(ManifestPath);

    /// <summary>
    /// 读取并解析清单。
    /// 文件不存在时返回**空清单**（不是错误）—— 没配置主页是正常状态。
    /// </summary>
    public HomepageParseResult Load()
    {
        lock (_lock)
        {
            if (!File.Exists(ManifestPath))
            {
                var empty = new HomepageParseResult { Manifest = new HomepageManifest() };
                LastResult = empty;
                return empty;
            }

            string text;
            try
            {
                text = File.ReadAllText(ManifestPath);
            }
            catch (Exception ex)
            {
                var failed = new HomepageParseResult { Error = "读取清单失败：" + ex.Message };
                LastResult = failed;
                return failed;
            }

            var result = HomepageManifestParser.Parse(text, Actions.IsRegistered);
            LastResult = result;
            return result;
        }
    }

    /// <summary>保存清单（写临时文件再替换，避免半截文件被读）。</summary>
    public void Save(HomepageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(ManifestPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            var tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ManifestPath, overwrite: true);
        }
    }

    /// <summary>开始监听清单文件的变化（热更新）。</summary>
    /// <param name="onChanged">清单变化时的回调（在后台线程上触发）。</param>
    public void Watch(Action<HomepageParseResult> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        StopWatching();

        var dir = Path.GetDirectoryName(ManifestPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;

        var watcher = new FileSystemWatcher(dir, Path.GetFileName(ManifestPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, _) => onChanged(Load());
        watcher.Created += (_, _) => onChanged(Load());
        watcher.Deleted += (_, _) => onChanged(Load());
        watcher.Renamed += (_, _) => onChanged(Load());
        _watcher = watcher;
    }

    /// <summary>停止监听。</summary>
    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
