using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using UMango.Core.Caching;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;
using UMango.Core.Records;

namespace UMango.Avalonia.Views;

/// <summary>
/// 主页（macOS/Linux 壳）：数据全部来自 UMango.Core 的真实探测。
/// 与 WPF 版共用同一注册表与缓存逻辑 —— 这里只是另一个"壳"。
/// </summary>
public partial class HomeView : UserControl
{
    // 缓存路径与 WPF 版同规则：InstallRecordStore.DefaultStorePath 同目录（跨平台路径由 Core 决定）
    private static readonly string CachePath = Path.Combine(
        Path.GetDirectoryName(InstallRecordStore.DefaultStorePath) ?? ".",
        "source-availability.json");

    private readonly PackageManagerRegistry _registry =
        PackageManagerFactory.CreateDefault(new JsonAvailabilityCache(CachePath));

    public HomeView()
    {
        InitializeComponent();
        PlatformText.Text = SystemInfo.Describe();
        ArchText.Text = $"家目录：{SystemInfo.HomeDirectory}";
        _ = LoadManagersAsync();
        BtnRefresh.Click += async (_, _) => await LoadManagersAsync();
    }

    private async Task LoadManagersAsync()
    {
        BtnRefresh.IsEnabled = false;
        try
        {
            var categories = await _registry.GetCategoriesAsync(refresh: false);
            ManagersList.ItemsSource = categories.Select(c => new ManagerRow
            {
                Title = c.Title,
                Detail = Describe(c),
                StateBrush = BrushFor(c),
            }).ToList();
        }
        catch (Exception ex)
        {
            ManagersList.ItemsSource = new[]
            {
                new ManagerRow { Title = "探测失败", Detail = ex.Message, StateBrush = Brushes.Gray }
            };
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    private static string Describe(PackageSourceCategory c)
    {
        if (!c.SupportedHere) return $"当前平台不适用 · {c.Subtitle}";
        if (c.Available) return $"可用 · {c.Subtitle}";
        return $"{c.Detail ?? c.InstallHint} · {c.Subtitle}";
    }

    private static IBrush BrushFor(PackageSourceCategory c) => c switch
    {
        { Available: true } => Brushes.SeaGreen,
        { SupportedHere: false } => Brushes.LightGray,
        _ => Brushes.IndianRed,
    };

    public sealed class ManagerRow
    {
        public required string Title { get; init; }
        public required string Detail { get; init; }
        public required IBrush StateBrush { get; init; }
    }
}
