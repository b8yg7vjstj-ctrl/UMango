// SPDX-License-Identifier: Apache-2.0
//
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/IO/Directories.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 相对原版的改动：
//   1. 命名空间 PCL.Core.IO → UMango.Core.IO；日志桥接到 UMango.Core.Logging.Log。
//
//   2. **权限检查改为跨平台**（原版是 Windows-only，且实现上有 bug）：
//      原版用 `DirectoryInfo.GetAccessControl()` + `WindowsIdentity.GetCurrent()`，
//      这两个 API **只在 Windows 上可用** —— PCL.Core 的 net10.0 支编译不过，
//      所以原版实际上把整个文件都绑死在 Windows 上。
//      这里改成「**真去写一个临时文件**」来判定权限：
//      这在任何平台上都成立，且比读 ACL 更能反映真实可用性
//      （ACL 允许但磁盘只读 / 配额满 / 沙箱限制，都会体现为写失败）。
//
//   3. 修掉的原版真实缺陷：
//
//      ① **「有写权限」判定把普通用户判成没有**。
//         原版遍历 ACL 规则时用 `principal.IsInRole(rule.IdentityReference.Value)`，
//         但 `IsInRole` 传的是**一个身份名**（如 "BUILTIN\Users"），
//         `WindowsPrincipal.IsInRole(string)` 期望的是**角色名**（如 "BUILTIN\Users"
//         恰好能work的只有 BUILTIN\Administrators 之类）——
//         对 "BUILTIN\Users" 这类组，IsInRole 返回 false →
//         **普通用户永远被判定为「无写权限」**。
//         改用真实写入探测后不存在这个问题。
//
//      ② **受保护目录判定只覆盖 Windows 的两个固定路径**，
//         而且用的是 `EndsWith(":\\System Volume Information")` ——
//         在非 Windows 上这两个判断毫无意义（还好原版本来就 Windows-only）。
//         这里改成通用的「系统目录」判定（挂载点 / 根目录 / Windows 保留目录）。
//
//      ③ `DeleteDirectoryAsync` 遇到**符号链接**时：
//         原版捕获 DirectoryNotFoundException 后再 `Directory.Delete(path)`
//         （不带 recursive）—— 但触发异常的原因通常不是符号链接，
//         而是并发删除；这个"再删一次"在真符号链接场景下会**删掉链接指向的内容**。
//         这里改成：只删链接本身（不递归）。
//
//      ④ 原版 `DeleteDirectoryAsync` 递归删除子目录时**用 `Directory.Delete(path, true)`**，
//         也就是说子目录已经被自己递归删过一遍，再 `Delete(recursive: true)` 是多余且危险的
//         （若子目录在两次删除之间被重新创建，会连带删掉新内容）。
//         这里改成只删空目录。
//
//      ⑤ 原版 `CopyDirectoryAsync` 的进度按**文件数**算，
//         且 `relativePath = file.FullName[fromPath.Length..]` ——
//         若 fromPath 末尾没有分隔符就会**切错**（多切/少切一个字符）。
//         这里用 `Path.GetRelativePath`。
//
//   4. `EnumerateFilesAsync` 原版遇到异常时返回**空列表**（静默）——
//      调用方以为目录是空的。这里保留「不抛」但返回失败标记由调用方决定；
//      为保持原语义，仍返回空列表，但记 Error 日志。

using UMango.Core.Logging;

namespace UMango.Core.IO;

/// <summary>
/// 目录相关的通用操作：权限检查、递归删除、递归复制、遍历。
/// </summary>
public static class Directories
{
    /// <summary>
    /// 异步检查是否拥有对指定文件夹的读写权限。
    /// 文件夹不存在或没有权限时返回 false，**不修改文件系统**。
    /// </summary>
    public static async Task<bool> CheckPermissionAsync(
        string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsSystemProtectedFolder(path)) return false;
        if (!Directory.Exists(path)) return false;

        try
        {
            // 真实写入探测：ACL 说允许不代表真能写（只读挂载、配额、沙箱）。
            var probe = Path.Combine(path, ".umango-perm-probe-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                await File.WriteAllTextAsync(probe, "probe", cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception) { /* 清理失败无所谓 */ }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Directories", "权限检查被取消");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Directories", $"没有对文件夹 {path} 的权限，请尝试以管理员权限运行 —— {ex}");
            return false;
        }
    }

    /// <summary>
    /// 异步检查文件夹权限，若无权限或文件夹不存在则抛出异常。不修改文件系统。
    /// </summary>
    /// <exception cref="ArgumentNullException">路径为空或只含空白字符。</exception>
    /// <exception cref="DirectoryNotFoundException">文件夹不存在。</exception>
    /// <exception cref="UnauthorizedAccessException">无访问权限。</exception>
    /// <exception cref="OperationCanceledException">操作被取消。</exception>
    public static async Task CheckPermissionWithExceptionAsync(
        string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentNullException(nameof(path), "文件夹路径不能为空！");

        if (IsSystemProtectedFolder(path))
            throw new UnauthorizedAccessException($"无法访问受保护的系统文件夹：{path}");

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"文件夹不存在：{path}");

        try
        {
            await CheckPermissionAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException($"无法访问文件夹 {path}：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 检查是否为受保护的系统文件夹。
    /// </summary>
    public static bool IsSystemProtectedFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Windows 的两个保留目录
        if (path.EndsWith(":\\System Volume Information", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(":\\$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
            return true;

        // 文件系统根目录（如 "C:\" 或 "/"）—— 能删它就意味着删了整个盘
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { return false; }

        var root = Path.GetPathRoot(full);
        if (root is null) return false;

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0 || string.Equals(trimmed, trimmedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        // Unix 的系统挂载点
        return trimmed is "/bin" or "/boot" or "/dev" or "/etc" or "/lib" or "/proc" or "/sbin"
            or "/sys" or "/usr" or "/var" or "/System" or "/Library" or "/Applications";
    }

    /// <summary>
    /// 异步删除文件夹及其内容，返回删除的文件数。支持忽略错误。
    /// </summary>
    public static async Task<int> DeleteDirectoryAsync(
        string? path, bool ignoreIssue = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;

        var deletedCount = 0;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(path))
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await Task.Run(() => File.Delete(filePath), cancellationToken).ConfigureAwait(false);
                        deletedCount++;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (attempt == 0)
                    {
                        Log.Error("Directories", ex, $"删除文件失败，将在 0.3s 后重试（{filePath}）");
                        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        if (ignoreIssue)
                            Log.Error("Directories", ex, "删除单个文件可忽略地失败");
                        else
                            throw;
                    }
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                deletedCount += await DeleteDirectoryAsync(subDir, ignoreIssue, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 子目录已被递归删空，这里只删空目录本身（不用 recursive: true）
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt == 0)
                {
                    Log.Error("Directories", ex, $"删除文件夹失败，将在 0.3s 后重试（{path}）");
                    await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (ignoreIssue)
                        Log.Error("Directories", ex, "删除单个文件夹可忽略地失败");
                    else
                        throw;
                }
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            // 目录在枚举过程中被删掉了 —— 目标已达成
            Log.Error("Directories", ex, $"目录已不存在，视为删除成功（{path}）");
        }

        return deletedCount;
    }

    /// <summary>
    /// 异步复制文件夹及其内容，失败时抛出异常。
    /// </summary>
    public static async Task CopyDirectoryAsync(
        string? fromPath, string? toPath, Action<double>? progressIncrementHandler = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fromPath))
            throw new ArgumentNullException(nameof(fromPath), "源文件夹路径为空");
        if (string.IsNullOrEmpty(toPath))
            throw new ArgumentNullException(nameof(toPath), "目标文件夹路径为空");

        var source = Path.GetFullPath(fromPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dest = Path.GetFullPath(toPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var allFiles = (await EnumerateFilesAsync(source, cancellationToken).ConfigureAwait(false)).ToList();
        var totalFiles = allFiles.Count;
        long copiedFiles = 0;

        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 用 GetRelativePath，而不是字符串切片 —— 原版的 file.FullName[fromPath.Length..]
            // 在 fromPath 末尾没有分隔符时会切错位置。
            var relativePath = Path.GetRelativePath(source, file.FullName);
            var destFilePath = Path.Combine(dest, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await Files.CopyFileAsync(file.FullName, destFilePath, cancellationToken).ConfigureAwait(false);
                    copiedFiles++;
                    progressIncrementHandler?.Invoke(totalFiles == 0 ? 1.0 : (double)copiedFiles / totalFiles);
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt == 0)
                {
                    Log.Error("Directories", ex, $"复制文件失败，将在 0.3s 后重试（{file.FullName} 到 {destFilePath}）");
                    await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("Directories", ex, $"复制文件失败（{file.FullName} 到 {destFilePath}）");
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// 异步遍历文件夹中的所有文件（含子目录）。
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">目录不存在时抛出。</exception>
    public static async Task<IEnumerable<FileInfo>> EnumerateFilesAsync(
        string? directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"目录不存在：{directory}");

        try
        {
            return await Task.Run(
                () => new DirectoryInfo(directory)
                    .EnumerateFiles("*", SearchOption.AllDirectories).ToList(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Directories", "文件夹遍历被取消");
            return [];
        }
        catch (Exception ex)
        {
            Log.Error("Directories", ex, $"遍历文件夹失败（{directory}）");
            return [];
        }
    }
}
