// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using UMango.Core.Tools;
using Xunit;

namespace UMango.Core.Tests.Tools;

public sealed class CleanupPlannerTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 空目录_统计为无内容()
    {
        var dir = MakeTempDir();
        try
        {
            var plan = CleanupPlanner.Plan(new Dictionary<CleanupTarget, string>
            {
                [CleanupTarget.Cache] = dir
            });

            Assert.Single(plan.Items);
            Assert.Equal(0, plan.TotalFileCount);
            Assert.Equal(0, plan.TotalBytes);
            Assert.True(plan.IsEmpty);
            Assert.False(plan.Items[0].HasContent);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 统计_递归_含子目录文件与字节数()
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "12345");
            File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "123");

            var plan = CleanupPlanner.Plan(new Dictionary<CleanupTarget, string>
            {
                [CleanupTarget.Cache] = dir
            });

            Assert.Equal(2, plan.TotalFileCount);
            Assert.Equal(8, plan.TotalBytes);
            Assert.False(plan.IsEmpty);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 统计_非递归_不含子目录()
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
            File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "1");

            var plan = CleanupPlanner.Plan(
                new Dictionary<CleanupTarget, string> { [CleanupTarget.Cache] = dir },
                recursive: false);

            Assert.Equal(1, plan.TotalFileCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 统计_目录不存在_记为零但保留目标()
    {
        var missing = Path.Combine(Path.GetTempPath(), "umango-missing-" + Guid.NewGuid().ToString("N"));
        var plan = CleanupPlanner.Plan(new Dictionary<CleanupTarget, string>
        {
            [CleanupTarget.Logs] = missing
        });

        Assert.Single(plan.Items);
        Assert.Equal(CleanupTarget.Logs, plan.Items[0].Target);
        Assert.Equal(0, plan.Items[0].FileCount);
        Assert.Equal(missing, plan.Items[0].Directory);
    }

    [Fact]
    public void 统计_空路径_记为零()
    {
        var plan = CleanupPlanner.Plan(new Dictionary<CleanupTarget, string>
        {
            [CleanupTarget.Cache] = ""
        });

        Assert.Equal(0, plan.TotalFileCount);
        Assert.Equal("", plan.Items[0].Directory);
    }

    [Fact]
    public void 统计_null_抛异常()
        => Assert.Throws<ArgumentNullException>(() => CleanupPlanner.Plan(null!));

    [Fact]
    public void 执行_删除内容但保留目录本身()
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "12345");
            File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "123");

            var plan = CleanupPlanner.Plan(new Dictionary<CleanupTarget, string>
            {
                [CleanupTarget.Cache] = dir
            });
            var (files, bytes) = CleanupPlanner.Execute(plan);

            Assert.Equal(2, files);
            Assert.Equal(8, bytes);
            Assert.True(Directory.Exists(dir));
            Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 执行_空计划_不删任何东西()
    {
        var (files, bytes) = CleanupPlanner.Execute(CleanupPlan.Empty);
        Assert.Equal(0, files);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void 执行_null_抛异常()
        => Assert.Throws<ArgumentNullException>(() => CleanupPlanner.Execute(null!));

    [Fact]
    public void 清理后_再次统计为空()
    {
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
            var targets = new Dictionary<CleanupTarget, string> { [CleanupTarget.Cache] = dir };

            CleanupPlanner.Execute(CleanupPlanner.Plan(targets));
            var second = CleanupPlanner.Plan(targets);

            Assert.True(second.IsEmpty);
        }
        finally { Directory.Delete(dir, true); }
    }
}
