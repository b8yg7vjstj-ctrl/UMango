using UMango.Core.Animation;
using UMango.Core.Media.Background;

namespace UMango.Core.Configuration;

/// <summary>
/// UMango 的界面设置目录。
///
/// 设置项集中声明在这里，**设置页由这些描述符生成**，不再逐项手写 XAML。
/// 键名用 `分组.子项` 形式且**一旦发布就不要改** —— 落盘与迁移都依赖它。
///
/// 分组名即设置页的卡片名（当前：外观 / 动画 / 调试 / 行为）。
/// </summary>
public static class UiSettings
{
    // ---------------- 分组名 ----------------
    public const string SectionAppearance = "外观";
    public const string SectionAnimation = "动画";
    public const string SectionDebug = "调试";
    public const string SectionBehavior = "行为";

    // ---------------- 键名 ----------------
    public const string ThemeKindKey = "ui.appearance.theme";
    public const string DarkModeKey = "ui.appearance.darkMode";

    /// <summary>
    /// 深浅色跟随策略：<c>system</c> / <c>light</c> / <c>dark</c>。
    /// 见 <c>UMango.Core.UI.Theme.SystemThemeResolver</c>。
    /// </summary>
    public const string ThemeFollowKey = "ui.appearance.themeFollow";

    public const string AnimationEnabledKey = "ui.animation.enabled";
    public const string AnimationDirectionKey = "ui.animation.direction";
    public const string AnimationSpeedKey = "ui.animation.speedFactor";
    public const string AnimationFpsKey = "ui.animation.fpsLimit";
    public const string AnimationProfileKey = "ui.animation.profile";

    public const string DebugModeKey = "debug.terminalMode";

    public const string ShowUnsupportedSourcesKey = "behavior.showUnsupportedSources";
    public const string ConfirmBeforeInstallKey = "behavior.confirmBeforeInstall";

    /// <summary>公告源地址（留空 = 不拉取公告）。</summary>
    public const string AnnouncementSourceKey = "announcement.source";

    /// <summary>已展示过的公告 id 列表（内部簿记，不需要用户看见）。</summary>
    public const string AnnouncementShowedKey = "announcement.showed";

    /// <summary>反馈源地址（留空 = 不拉取反馈列表）。</summary>
    public const string FeedbackSourceKey = "feedback.source";

    /// <summary>提交反馈的目标地址（留空 = 只导出草稿，不跳转）。</summary>
    public const string FeedbackSubmitUrlKey = "feedback.submitUrl";

    /// <summary>背景音乐开关。</summary>
    public const string MusicEnabledKey = "music.enabled";

    /// <summary>随机播放（关闭 = 按文件名顺序循环）。</summary>
    public const string MusicShuffleKey = "music.shuffle";

    /// <summary>音乐音量（0~1）。</summary>
    public const string MusicVolumeKey = "music.volume";

    /// <summary>音乐目录（留空 = 用程序目录下的 Musics）。</summary>
    public const string MusicDirectoryKey = "music.directory";

    /// <summary>动态背景开关（图片/视频背景总开关）。</summary>
    public const string BackgroundEnabledKey = "background.enabled";

    /// <summary>动态背景目录（留空 = 用程序目录下的 Pictures）。</summary>
    public const string BackgroundDirectoryKey = "background.directory";

    /// <summary>动态背景是否允许视频（关闭 = 只用静态图片，省资源）。</summary>
    public const string BackgroundVideoEnabledKey = "background.videoEnabled";

    /// <summary>动态背景是否在忙碌时强制继续播放。</summary>
    public const string BackgroundForcePlayKey = "background.forcePlay";

    /// <summary>动态背景的铺放方式（智能/居中/适应/拉伸/平铺/四角）。</summary>
    public const string BackgroundSuitKey = "background.suit";

    /// <summary>动态背景的不透明度（0~100，与 PCL 的千分比一致）。</summary>
    public const string BackgroundOpacityKey = "background.opacity";

    /// <summary>动态背景的模糊半径（0 表示不模糊）。</summary>
    public const string BackgroundBlurKey = "background.blur";

    /// <summary>全部分组（按设置页展示顺序）</summary>
    public static readonly string[] SectionOrder =
        [SectionAppearance, SectionAnimation, SectionDebug, SectionBehavior];

    /// <summary>
    /// 全部设置描述符。
    /// 默认值对齐 PCL-CE 的语义：动画开启、方向为 UMango 自有矢量、帧率上限 59。
    /// </summary>
    public static IReadOnlyList<SettingDescriptor> All =>
    [
        new SettingDescriptor
        {
            Key = ThemeKindKey,
            Section = SectionAppearance,
            Title = "主题族",
            Description = "PCL 经典沿用 PCL-CE 的配色与尺寸；Windows 10 Fluent 还原 Win10 设置的方正紧凑观感"
                          + "（注意：Fluent Design 与 WinUI3/Win11 不是一回事）。",
            Kind = SettingKind.Enum,
            EnumType = typeof(ThemeFamily),
            DefaultValue = ThemeFamily.PclClassic,
            Order = 10
        },
        new SettingDescriptor
        {
            Key = DarkModeKey,
            Section = SectionAppearance,
            Title = "深色模式",
            Description = "与主题族正交：两套主题族各有浅色与深色版本，共四套 token 字典。",
            Kind = SettingKind.Bool,
            DefaultValue = false,
            Order = 20
        },

        new SettingDescriptor
        {
            Key = AnimationEnabledKey,
            Section = SectionAnimation,
            Title = "启用动画",
            Description = "关闭后所有动画时长归零、元素直接落到终态，不会留下半透明或偏移的残影。",
            Kind = SettingKind.Bool,
            DefaultValue = true,
            Order = 10
        },
        new SettingDescriptor
        {
            Key = AnimationDirectionKey,
            Section = SectionAnimation,
            Title = "方向矢量",
            Description = "UMango 矢量：列表项垂直进入、对话框向右滑入（与 PCL-CE 刻意镜像）；"
                          + "PCL 兼容：列表项水平进入、对话框向左滑入。",
            Kind = SettingKind.Enum,
            EnumType = typeof(AnimationDirectionMode),
            DefaultValue = AnimationDirectionMode.Mirrored,
            Order = 20
        },
        new SettingDescriptor
        {
            Key = AnimationProfileKey,
            Section = SectionAnimation,
            Title = "动效预设",
            Description = "PCL：动效参数完整采用 PCL-CE 原版数值（切页落体回弹 250/350ms、级联 25ms）；"
                          + "Fluent：WinUI 语义的克制短动效。随主题切换自动同步，可在此覆盖。",
            Kind = SettingKind.Enum,
            EnumType = typeof(MotionProfileKind),
            DefaultValue = MotionProfileKind.Pcl,
            Order = 25
        },
        new SettingDescriptor
        {
            Key = AnimationSpeedKey,
            Section = SectionAnimation,
            Title = "速度倍率",
            Description = "调小可放慢观察动画细节，与 PCL-CE 的 AnimationSpeed 调试项同用途。",
            Kind = SettingKind.Double,
            DefaultValue = 1.0,
            Min = 0.5,
            Max = 2.0,
            Order = 30
        },
        new SettingDescriptor
        {
            Key = AnimationFpsKey,
            Section = SectionAnimation,
            Title = "帧率上限",
            Description = "默认 59，取自 PCL-CE 的 UiAniFPS 默认值。",
            Kind = SettingKind.Int,
            DefaultValue = 59,
            Min = 15,
            Max = 240,
            Order = 40
        },

        new SettingDescriptor
        {
            Key = DebugModeKey,
            Section = SectionDebug,
            Title = "调试模式（终端输出）",
            Description = "开启后保留终端完整输出，并在进度页把内联终端输出区展开显示；"
                          + "关闭时只保留最近若干行，避免超长输出拖慢界面。",
            Kind = SettingKind.Bool,
            DefaultValue = false,
            Order = 10
        },

        new SettingDescriptor
        {
            Key = ShowUnsupportedSourcesKey,
            Section = SectionBehavior,
            Title = "显示当前平台不支持的包源",
            Description = "默认隐藏。打开后可以看见那些包源为何不可用（例如 winget 仅限 Windows）。",
            Kind = SettingKind.Bool,
            DefaultValue = false,
            Order = 10
        },
        new SettingDescriptor
        {
            Key = ConfirmBeforeInstallKey,
            Section = SectionBehavior,
            Title = "安装前二次确认",
            Description = "安装会真实改动系统，默认开启确认；关闭后点安装直接执行。",
            Kind = SettingKind.Bool,
            DefaultValue = true,
            Order = 20
        },

        new SettingDescriptor
        {
            Key = AnnouncementSourceKey,
            Section = SectionBehavior,
            Title = "公告源",
            Description = "留空表示不拉取主页公告。填入一个返回 {\"content\":[…]} 的 JSON 地址即可；"
                          + "公告里的按钮只能触发已注册的动作，远程内容无法执行任意命令。",
            Kind = SettingKind.String,
            DefaultValue = "",
            Order = 30
        },

        new SettingDescriptor
        {
            Key = AnnouncementShowedKey,
            Section = SectionBehavior,
            Title = "已展示的公告",
            Description = "内部簿记：记录已经弹过的公告 id，避免每次启动重复弹窗。",
            Kind = SettingKind.String,
            DefaultValue = "",
            Order = 31
        },

        new SettingDescriptor
        {
            Key = FeedbackSourceKey,
            Section = SectionBehavior,
            Title = "反馈源",
            Description = "留空表示不拉取反馈列表。填入一个返回 {\"items\":[…]} 的 JSON 地址即可，"
                          + "形状与 GitHub Issues API 兼容；反馈内容只读展示，不会执行其中任何内容。",
            Kind = SettingKind.String,
            DefaultValue = "",
            Order = 32
        },

        new SettingDescriptor
        {
            Key = FeedbackSubmitUrlKey,
            Section = SectionBehavior,
            Title = "反馈提交地址",
            Description = "填入后，点「提交反馈」会带上预填的诊断信息打开该地址（可用 {title} / {body} 占位符）；"
                          + "留空则只把草稿复制到剪贴板。",
            Kind = SettingKind.String,
            DefaultValue = "",
            Order = 33
        },

        new SettingDescriptor
        {
            Key = MusicEnabledKey,
            Section = SectionBehavior,
            Title = "背景音乐",
            Description = "在工具页播放音乐目录里的曲目。支持 .wav / .mp3；"
                          + "NAudio 不带 FLAC 解码器，.flac 会被判为格式不支持。",
            Kind = SettingKind.Bool,
            DefaultValue = false,
            Order = 40
        },

        new SettingDescriptor
        {
            Key = MusicShuffleKey,
            Section = SectionBehavior,
            Title = "随机播放",
            Description = "打开后随机挑选曲目；关闭则按文件名顺序循环，播完一轮重来。",
            Kind = SettingKind.Bool,
            DefaultValue = true,
            Order = 41
        },

        new SettingDescriptor
        {
            Key = MusicVolumeKey,
            Section = SectionBehavior,
            Title = "音乐音量",
            Description = "0 ~ 1 之间的数值。",
            Kind = SettingKind.Double,
            DefaultValue = 0.5d,
            Order = 42
        },

        new SettingDescriptor
        {
            Key = MusicDirectoryKey,
            Section = SectionBehavior,
            Title = "音乐目录",
            Description = "留空则使用程序目录下的 Musics 文件夹。目录不存在会自动创建。",
            Kind = SettingKind.String,
            DefaultValue = "",
            Order = 43
        },

        new SettingDescriptor
        {
            Key = BackgroundEnabledKey,
            Section = SectionBehavior,
            Title = "动态背景",
            Description = "在界面底层显示图片或视频背景。视频会占用较多资源，"
                          + "窗口最小化或在执行耗时任务时会自动暂停。",
            Kind = SettingKind.Bool,
            DefaultValue = false,
            Order = 50
        },

        new SettingDescriptor
        {
            Key = BackgroundDirectoryKey,
            Section = SectionBehavior,
            Title = "背景目录",
            Description = "留空则使用程序目录下的 Pictures 文件夹。目录不存在会自动创建。",
            Kind = SettingKind.String,
            DefaultValue = "",
            Order = 51
        },

        new SettingDescriptor
        {
            Key = BackgroundVideoEnabledKey,
            Section = SectionBehavior,
            Title = "允许视频背景",
            Description = "关闭后只使用静态图片作为背景，更省资源。",
            Kind = SettingKind.Bool,
            DefaultValue = true,
            Order = 52
        },

        new SettingDescriptor
        {
            Key = BackgroundForcePlayKey,
            Section = SectionBehavior,
            Title = "忙碌时强制播放",
            Description = "打开后，即使正在执行耗时任务也继续播放动态背景。",
            Kind = SettingKind.Bool,
            DefaultValue = false,
            Order = 53
        },

        new SettingDescriptor
        {
            Key = BackgroundSuitKey,
            Section = SectionBehavior,
            Title = "背景铺放方式",
            Description = "智能：图片比窗口小就平铺，否则适应（与 PCL 一致）。"
                          + "也可固定为居中 / 适应 / 拉伸 / 平铺 / 靠某个角。",
            Kind = SettingKind.Enum,
            EnumType = typeof(BackgroundSuitMode),
            DefaultValue = BackgroundSuitMode.Smart,
            Order = 54
        },

        new SettingDescriptor
        {
            Key = BackgroundOpacityKey,
            Section = SectionBehavior,
            Title = "背景不透明度",
            Description = "0~1000，越小越透明（沿用 PCL 的千分比刻度：1000 = 完全不透明）。",
            Kind = SettingKind.Int,
            DefaultValue = 1000,
            Order = 55
        },

        new SettingDescriptor
        {
            Key = BackgroundBlurKey,
            Section = SectionBehavior,
            Title = "背景模糊",
            Description = "0 表示不模糊；数值越大越模糊（与 PCL 一致）。",
            Kind = SettingKind.Int,
            DefaultValue = 0,
            Order = 56
        },
    ];

    /// <summary>把描述符注册到服务里。</summary>
    public static void RegisterAll(SettingsService service) => service.RegisterRange(All);

    /// <summary>
    /// 从设置服务读出动画设置。
    ///
    /// **每次读取都新建一个实例**，而不是长期持有同一个对象 ——
    /// 设置服务是唯一权威，AnimationSettings 只是一个"给 AnimationPlanner 用的快照"。
    /// 若长期持有，就又多出一份可能与服务不一致的副本（这正是设置类 bug 的常见来源）。
    /// </summary>
    public static AnimationSettings ReadAnimationSettings(SettingsService service) => new()
    {
        Enabled = service.Get<bool>(AnimationEnabledKey),
        Direction = service.Get<AnimationDirectionMode>(AnimationDirectionKey),
        SpeedFactor = service.Get<double>(AnimationSpeedKey),
        FpsLimit = service.Get<int>(AnimationFpsKey),
        Profile = service.Get<MotionProfileKind>(AnimationProfileKey)
    };

    /// <summary>把动画设置整体写回设置服务（用于"一键还原默认"这类场景）。</summary>
    public static void WriteAnimationSettings(SettingsService service, AnimationSettings settings)
    {
        service.Set(AnimationEnabledKey, settings.Enabled);
        service.Set(AnimationDirectionKey, settings.Direction);
        service.Set(AnimationSpeedKey, Math.Clamp(settings.SpeedFactor, 0.5, 2.0));
        service.Set(AnimationFpsKey, Math.Clamp(settings.FpsLimit, 15, 240));
        service.Set(AnimationProfileKey, settings.Profile);
    }
}

/// <summary>主题族（与 UMango.App.Themes.ThemeKind 对应；放在 Core 是为了让设置能在这里声明）</summary>
public enum ThemeFamily
{
    PclClassic,
    Fluent10
}
