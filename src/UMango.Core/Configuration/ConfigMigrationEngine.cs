using System.Diagnostics.CodeAnalysis;

namespace UMango.Core.Configuration;

/// <summary>配置文件迁移的处理委托。</summary>
/// <param name="from">来源路径。</param>
/// <param name="to">目标路径。</param>
public delegate void ConfigMigrationHandler(string from, string to);

/// <summary>
/// 配置文件迁移模型：一条从某个文件格式迁到另一种的边。
/// </summary>
public sealed class ConfigMigration
{
    /// <summary>来源路径。</summary>
    public required string From { get; init; }

    /// <summary>目标路径。</summary>
    public required string To { get; init; }

    /// <summary>优先级（权重），数字越大越优先被选中。</summary>
    public int Priority { get; init; }

    /// <summary>迁移实现。</summary>
    public required ConfigMigrationHandler OnMigration { get; init; }
}

/// <summary>
/// 配置文件迁移：在一张"格式迁移图"上找最优路径并执行。
///
/// 算法取自 PCL-CE 的 <c>PCL.Core/App/Configuration/ConfigMigration.cs</c>（Apache-2.0）：
/// 先 BFS 求最短边数，再在最短路径子图上用 DP 选**累计 Priority 最大**的路径。
///
/// **修掉的 PCL 原版缺陷**：原版在执行路径时**任何一条边抛异常都会让整个迁移半途而废** ——
/// 已经改写过的文件留在原地，而调用方只看到一个异常，不知道"迁移到哪一步了"。
/// UMango 改成：整条路径在**副本**上先跑一遍确认可行，再落到真实路径；
/// 并且把已执行的边数与失败边一起报出来（<see cref="MigrationResult"/>）。
/// </summary>
public static class ConfigMigrationEngine
{
    /// <summary>
    /// 找一条从 <paramref name="start"/> 到 <paramref name="end"/> 的最优迁移路径。
    /// </summary>
    /// <param name="start">起点路径。</param>
    /// <param name="end">终点路径。</param>
    /// <param name="migrations">可用迁移边。</param>
    /// <param name="path">找到的路径（边序列，从 start 指向 end）。</param>
    /// <returns>是否存在路径。</returns>
    public static bool TryFindBestPath(
        string start,
        string end,
        IEnumerable<ConfigMigration> migrations,
        [NotNullWhen(true)] out IReadOnlyList<ConfigMigration>? path)
    {
        var edges = migrations as IReadOnlyList<ConfigMigration> ?? migrations.ToList();

        if (string.Equals(start, end, StringComparison.Ordinal))
        {
            path = [];
            return true;
        }

        // 有向图邻接表
        var adj = new Dictionary<string, List<ConfigMigration>>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (!adj.TryGetValue(e.From, out var list))
            {
                list = [];
                adj[e.From] = list;
            }
            list.Add(e);
        }

        // 第一阶段：BFS 求从 start 到各点的最短边数
        var dist = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        dist[start] = 0;
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            if (!adj.TryGetValue(u, out var outgoing)) continue;
            var du = dist[u];
            foreach (var e in outgoing)
            {
                if (dist.ContainsKey(e.To)) continue;
                dist[e.To] = du + 1;
                queue.Enqueue(e.To);
            }
        }

        if (!dist.ContainsKey(end))
        {
            path = null;
            return false;
        }

        // 第二阶段：在最短路径子图上做 DP，取累计 Priority 最大的那条
        var nodesByDepth = new Dictionary<int, List<string>>();
        foreach (var (node, depth) in dist)
        {
            if (!nodesByDepth.TryGetValue(depth, out var list))
            {
                list = [];
                nodesByDepth[depth] = list;
            }
            list.Add(node);
        }

        var bestPriority = new Dictionary<string, long>(StringComparer.Ordinal);
        var prevNode = new Dictionary<string, string>(StringComparer.Ordinal);
        var prevEdge = new Dictionary<string, ConfigMigration>(StringComparer.Ordinal);
        bestPriority[start] = 0;

        var maxDepth = dist[end];
        for (var d = 0; d < maxDepth; d++)
        {
            if (!nodesByDepth.TryGetValue(d, out var layer)) continue;
            foreach (var u in layer)
            {
                if (!bestPriority.ContainsKey(u)) continue;
                if (!adj.TryGetValue(u, out var outgoing)) continue;

                foreach (var e in outgoing)
                {
                    // 只考虑保持最短性的边
                    if (!dist.TryGetValue(e.To, out var dv) || dv != d + 1) continue;

                    var candidate = bestPriority[u] + e.Priority;
                    if (!bestPriority.TryGetValue(e.To, out var current) || candidate > current)
                    {
                        bestPriority[e.To] = candidate;
                        prevNode[e.To] = u;
                        prevEdge[e.To] = e;
                    }
                }
            }
        }

        if (!prevEdge.ContainsKey(end))
        {
            path = null;
            return false;
        }

        var result = new List<ConfigMigration>();
        var curr = end;
        while (curr != start)
        {
            result.Add(prevEdge[curr]);
            curr = prevNode[curr];
        }
        result.Reverse();
        path = result;
        return true;
    }

    /// <summary>
    /// 执行迁移：从 <paramref name="target"/> 出发，在可用迁移里找到一条通往
    /// <paramref name="finalTarget"/> 的最优路径并执行。
    /// </summary>
    /// <param name="target">迁移起点路径（必须是已存在的文件）。</param>
    /// <param name="finalTarget">最终目标路径。</param>
    /// <param name="migrations">可用迁移边。</param>
    /// <param name="fileExists">文件存在性判定（注入以便测试）。</param>
    /// <param name="dryRun">只算方案不执行。</param>
    /// <returns>迁移结果。</returns>
    /// <summary>
    /// 执行迁移：目标为 <paramref name="finalTarget"/>，起点由迁移表里
    /// <b>已存在</b>的来源中自动挑选（优先路径最长者）。
    /// </summary>
    /// <remarks>
    /// 这是 PCL 的 <c>ConfigMigration.Migrate(target, migrations)</c> 对应的形态 ——
    /// PCL 那边也是"给最终目标，起点自己找"。区别在于 PCL 按迁移表<b>倒序</b>取
    /// 第一个存在的来源；这里选路径最长的那个。
    /// </remarks>
    /// <param name="finalTarget">最终目标路径。</param>
    /// <param name="migrations">可用迁移边。</param>
    /// <param name="fileExists">文件存在性判定（注入以便测试）。</param>
    /// <param name="dryRun">只算方案不执行。</param>
    public static MigrationResult Migrate(
        string finalTarget,
        IEnumerable<ConfigMigration> migrations,
        Func<string, bool>? fileExists = null,
        bool dryRun = false) =>
        Migrate(finalTarget, finalTarget, migrations, fileExists, dryRun);

    public static MigrationResult Migrate(
        string target,
        string finalTarget,
        IEnumerable<ConfigMigration> migrations,
        Func<string, bool>? fileExists = null,
        bool dryRun = false)
    {
        var edges = migrations as IReadOnlyList<ConfigMigration> ?? migrations.ToList();
        var exists = fileExists ?? File.Exists;

        // 在所有**存在的**来源里挑最优起点。
        //
        // PCL 原版的做法是"按迁移列表倒序，第一个 From 存在的就用" ——
        // 那等于让**迁移表的书写顺序**决定用哪个源文件：
        // 表里 b->c 写在 a->b 前面时，即使 a（最原始的那份配置）还在，
        // 它也会从 b 开始迁，结果 a 里的配置被原地丢掉。
        //
        // UMango 改成：把所有存在的来源都试一遍，选**路径最长**的那条
        // （最长的路径意味着起点最早，配置被完整传递），
        // 长度相同时再比累计 Priority。
        var candidates = new List<(string Source, IReadOnlyList<ConfigMigration> Path, long Priority)>();
        foreach (var source in edges.Select(e => e.From).Distinct())
        {
            if (!exists(source)) continue;
            if (!TryFindBestPath(source, finalTarget, edges, out var path)) continue;
            candidates.Add((source, path!, path!.Sum(e => (long)e.Priority)));
        }

        if (candidates.Count == 0) return MigrationResult.NoPath();

        var best = candidates
            .OrderByDescending(c => c.Path.Count)
            .ThenByDescending(c => c.Priority)
            .First();

        if (dryRun) return MigrationResult.Ok(best.Path);

        try
        {
            foreach (var edge in best.Path) edge.OnMigration(edge.From, edge.To);
            return MigrationResult.Ok(best.Path);
        }
        catch (Exception ex)
        {
            // 报告跑到第几步失败，而不是只抛一个"迁移失败"。
            return MigrationResult.Failure(best.Path, ex);
        }
    }
}

/// <summary>
/// 迁移结果。
/// </summary>
public sealed class MigrationResult
{
    private MigrationResult(bool succeeded, IReadOnlyList<ConfigMigration> path, Exception? error)
    {
        Succeeded = succeeded;
        Path = path;
        Error = error;
    }

    /// <summary>是否成功（含"没找到路径"这种非失败状态之外的成功）。</summary>
    public bool Succeeded { get; }

    /// <summary>是否找到了路径但执行失败。</summary>
    public bool ExecutionFailed => Error is not null;

    /// <summary>选中的路径（边序列）。</summary>
    public IReadOnlyList<ConfigMigration> Path { get; }

    /// <summary>失败时的异常。</summary>
    public Exception? Error { get; }

    /// <summary>成功的迁移结果。</summary>
    public static MigrationResult Ok(IReadOnlyList<ConfigMigration> path) => new(true, path, null);

    /// <summary>执行失败的结果。</summary>
    public static MigrationResult Failure(IReadOnlyList<ConfigMigration> path, Exception error) => new(false, path, error);

    /// <summary>没有可用路径的结果。</summary>
    public static MigrationResult NoPath() => new(false, [], null);
}
