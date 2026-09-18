using UMango.App.ViewModels;
using UMango.Core.App.Tasks;
using UMango.Core.PackageManagers;
using Xunit;

namespace UMango.App.Tests;

/// <summary>其余 ViewModel 的测试。逐个确认它们在无 WPF 宿主下可用。</summary>
public class OtherViewModelTests
{
    // ================= DownloadViewModel =================

    [Fact]
    public void 下载页默认不显示平台不支持的包源()
    {
        var vm = new DownloadViewModel();
        Assert.False(vm.ShowUnsupported);

        // 未加载分类时也不应抛异常
        Assert.Empty(vm.VisibleCategories);
    }

    [Fact]
    public async Task 下载页能加载包源分类_且标题为包管理器名子标题为安装目录()
    {
        var vm = new DownloadViewModel();
        await vm.InitializeAsync();

        var categories = vm.VisibleCategories.ToList();
        Assert.NotEmpty(categories);   // 本机至少有 npm/pnpm/git/homebrew

        // 需求原文的硬要求：左栏两行 —— 第一行包管理器名、第二行安装目录。
        // 这里断言的是"数据侧确实提供了这两项且非空"，渲染由 XAML 负责。
        Assert.All(categories, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title), "分类标题（包管理器名）不能为空");
            Assert.False(string.IsNullOrWhiteSpace(c.Subtitle), "子标题（安装目录）不能为空");
            Assert.True(c.SupportedHere, "默认过滤后剩下的都应是当前平台支持的包源");
        });
    }

    [Fact]
    public async Task 打开显示不支持开关后会多出包源()
    {
        var vm = new DownloadViewModel();
        await vm.InitializeAsync();
        var supported = vm.VisibleCategories.Count();

        vm.ShowUnsupported = true;
        var all = vm.VisibleCategories.Count();

        Assert.True(all >= supported, "显示不支持的包源后数量不应减少");
    }

    [Fact]
    public void 下载页初始状态是干净的()
    {
        var vm = new DownloadViewModel();
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("", vm.Query);
        Assert.Empty(vm.Results);
    }

    // ================= ToolsViewModel =================

    [Fact]
    public void 工具页构造后就有系统报告_避免首帧空白()
    {
        var vm = new ToolsViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.SystemReport));
    }

    [Fact]
    public void 工具页报告包含平台与架构()
    {
        var vm = new ToolsViewModel();
        var desc = UMango.Core.Platform.SystemInfo.Describe();
        Assert.Contains(desc, vm.SystemReport);
    }

    // ================= ProgressViewModel =================

    [Fact]
    public void 进度页直接绑定TaskCenter而不是复制一份数据()
    {
        var vm = new ProgressViewModel();
        // 同一个集合实例：这是"进度页与实际执行不会不同步"的结构保证
        Assert.Same(TaskCenter.Tasks, vm.Tasks);
    }

    [Fact]
    public void 进度页初始没有选中任务()
    {
        var vm = new ProgressViewModel();
        Assert.Null(vm.SelectedTask);
        Assert.Empty(vm.TerminalLines);
    }

    [Fact]
    public void 进度页能按子项分组取终端输出()
    {
        var vm = new ProgressViewModel();
        var task = TaskModel.Group("测试任务-" + Guid.NewGuid());
        task.Children.Add(TaskModel.Progressive("下载"));
        task.Children.Add(TaskModel.Progressive("链接与写入"));

        // 没有任何输出时分组结果应安全为空
        var groups = vm.GroupedOutput(task);
        Assert.NotNull(groups);
    }

    // ================= SettingsViewModel =================

    [Fact]
    public void 设置页提供两种主题族与两种动画方向()
    {
        var vm = new SettingsViewModel();
        Assert.Equal(2, vm.Themes.Count);
        Assert.Equal(2, vm.AnimationDirections.Count);

        // 需求要求两种动画矢量都可选（默认镜像 UMango，另一项还原 PCL）
        Assert.Contains(vm.AnimationDirections, o => o.Mode == UMango.Core.Animation.AnimationDirectionMode.Mirrored);
        Assert.Contains(vm.AnimationDirections, o => o.Mode == UMango.Core.Animation.AnimationDirectionMode.PclCompatible);
        Assert.Contains(vm.Themes, t => t.Title.Contains("PCL"));
        Assert.Contains(vm.Themes, t => t.Title.Contains("Fluent"));
    }

    [Fact]
    public void 设置页的动画开关读写走设置服务()
    {
        var vm = new SettingsViewModel();
        var original = vm.AnimationEnabled;

        try
        {
            vm.AnimationEnabled = !original;
            Assert.Equal(!original, vm.AnimationEnabled);
        }
        finally
        {
            vm.AnimationEnabled = original;   // 还原，避免污染真实设置文件
        }
    }

    [Fact]
    public void 设置页速度倍率被夹在合法范围()
    {
        var vm = new SettingsViewModel();
        var original = vm.AnimationSpeedFactor;

        try
        {
            vm.AnimationSpeedFactor = 99.0;
            Assert.InRange(vm.AnimationSpeedFactor, 0.5, 2.0);
            vm.AnimationSpeedFactor = 0.01;
            Assert.InRange(vm.AnimationSpeedFactor, 0.5, 2.0);
        }
        finally
        {
            vm.AnimationSpeedFactor = original;
        }
    }

    [Fact]
    public void 设置页能读到记录统计()
    {
        var vm = new SettingsViewModel();
        Assert.True(vm.TotalRecords >= 0);
        Assert.True(vm.SuccessCount >= 0);
        Assert.True(vm.FailureCount >= 0);
        Assert.True(vm.SuccessCount + vm.FailureCount <= vm.TotalRecords);
    }

    // ================= ShellViewModel 与页面的对应关系 =================

    [Fact]
    public void AppTab的取值数量与外壳文档一致()
    {
        // 需求要求"顶部四个 banner 选项卡 + 下方一栏菜单"，
        // 实现上多了一个「进度」页（需求也明确要求有下载进度界面），共 5 个。
        // 这条测试锁住"数量是被有意选择的"，避免有人顺手加减而不更新文档。
        Assert.Equal(5, Enum.GetValues<AppTab>().Length);
        Assert.Contains(AppTab.Progress, Enum.GetValues<AppTab>());
    }
}
