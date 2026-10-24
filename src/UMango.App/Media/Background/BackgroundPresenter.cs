// SPDX-License-Identifier: Apache-2.0
//
// 动态背景的显示端（唯一碰 WPF 媒体栈的地方）。
//
// 设计语义借鉴 PCL-CE 的 `Plain Craft Launcher 2/Modules/ModVideoBack.cs`
// （受限许可目录，只借鉴语义、不复制代码）：
//   · 优先播放图片（静态、无资源开销），视频作为可选增强；
//   · 窗口最小化 / 忙碌时暂停，强制播放可覆盖忙碌判定；
//   · 播放失败按错误码区分「不是 H.264」与其它失败，给出不同提示。
//
// 判定逻辑（何时播/何时停/失败怎么分类）全在 Core 的
// `UMango.Core.Media.Background`，这里只做「把判定结果落到控件上」。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using UMango.Core.Media.Background;

namespace UMango.App.Media.Background;

/// <summary>
/// 动态背景的显示端：在窗口底层铺一张图片或一个 <see cref="MediaElement"/>。
/// </summary>
public sealed class BackgroundPresenter
{
    private readonly Border _image;
    private readonly MediaElement _media;

    /// <summary>
    /// 构造显示端。
    /// </summary>
    /// <param name="image">
    /// 图片层（放最底）。**必须是 Border 而不是 Image** ——
    /// PCL 原版用 <c>ImgBack.Background = new ImageBrush(...)</c>，
    /// WPF 的 <see cref="Image"/> 控件没有 <c>Brush</c> 属性（那是 <see cref="ImageBrush"/> 的），
    /// 铺放方式（平铺/四角对齐）也正是靠 <c>Border.Background</c> 的画刷来表达的。
    /// </param>
    /// <param name="media">视频层（放图片之上，但内容区会盖住它）。</param>
    public BackgroundPresenter(Border image, MediaElement media)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(media);

        _image = image;
        _media = media;

        _media.Stretch = Stretch.UniformToFill;
        // ⚠ WPF 的枚举是 System.Windows.Controls.MediaState（值：Manual / Play / Pause / Stop），
        //   **不是** WinRT 的 MediaPlayerBehavior —— 后者在 WPF 里不存在。
        _media.LoadedBehavior = MediaState.Manual;
        _media.UnloadedBehavior = MediaState.Manual;
        _media.MediaFailed += OnMediaFailed;
    }

    /// <summary>当前状态。</summary>
    public BackgroundPlaybackState State { get; private set; } = BackgroundPlaybackState.Stopped;

    /// <summary>最近一次失败的原因（无失败时为 <see cref="BackgroundFailureKind.None"/>）。</summary>
    public BackgroundFailureKind LastFailure { get; private set; } = BackgroundFailureKind.None;

    /// <summary>最近一次失败的文件名。</summary>
    public string? LastFailedFile { get; private set; }

    /// <summary>失败时触发（供界面给提示）。</summary>
    public event EventHandler<BackgroundFailureKind>? Failed;

    /// <summary>开始播放一张图片背景。</summary>
    /// <param name="image">图片。</param>
    /// <param name="suit">铺放方式（智能会按容器尺寸解析）。</param>
    /// <param name="containerWidth">容器宽（用于解析「智能」）。</param>
    /// <param name="containerHeight">容器高（用于解析「智能」）。</param>
    public void ShowImage(
        BackgroundImage image,
        BackgroundSuitMode suit = BackgroundSuitMode.Smart,
        double containerWidth = 0,
        double containerHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(image);

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(image.Path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            var resolved = BackgroundSuitPolicy.Resolve(
                suit,
                new BackgroundSuitInput(
                    bitmap.PixelWidth,
                    bitmap.PixelHeight,
                    containerWidth > 0 ? containerWidth : bitmap.PixelWidth,
                    containerHeight > 0 ? containerHeight : bitmap.PixelHeight));

            var brush = new ImageBrush(bitmap) { TileMode = TileMode.None };
            ApplySuit(brush, resolved);

            _image.Background = brush;
            _image.Visibility = Visibility.Visible;

            // 图片优先：视频层收起来（PCL 原版也是「先试图片」）。
            _media.Source = null;
            _media.Visibility = Visibility.Collapsed;

            State = BackgroundPlaybackState.Playing;
            LastFailure = BackgroundFailureKind.None;
            LastFailedFile = null;
        }
        catch (Exception ex)
        {
            Fail(ex, image.FileName);
        }
    }

    /// <summary>
    /// 应用不透明度与模糊（对应 PCL 的 <c>UiBackgroundOpacity</c> / <c>UiBackgroundBlur</c>）。
    /// </summary>
    /// <param name="opacityPerMille">不透明度，千分比（PCL 刻度：1000 = 完全不透明）。</param>
    /// <param name="blurRadius">模糊半径；0 表示不模糊。</param>
    public void ApplyAppearance(int opacityPerMille, int blurRadius)
    {
        _image.Opacity = Math.Clamp(opacityPerMille / 1000d, 0d, 1d);

        if (blurRadius <= 0)
        {
            _image.Effect = null;
            _image.Margin = new Thickness(0);
            return;
        }

        // PCL 原版：Radius = value + 1，并把 Margin 往外扩 -(value+1)/1.8，
        // 避免模糊后边缘出现半透明的一条。
        var radius = blurRadius + 1;
        _image.Effect = new BlurEffect { Radius = radius };
        _image.Margin = new Thickness(-radius / 1.8d);
    }

    /// <summary>
    /// 按铺放方式把画刷与对齐落到位。
    /// </summary>
    private void ApplySuit(ImageBrush brush, BackgroundSuitMode mode)
    {
        brush.Stretch = mode switch
        {
            BackgroundSuitMode.Fit => Stretch.UniformToFill,
            BackgroundSuitMode.Stretch => Stretch.Fill,
            _ => Stretch.None,
        };

        if (mode == BackgroundSuitMode.Tile)
        {
            brush.TileMode = TileMode.Tile;
            brush.ViewportUnits = BrushMappingMode.Absolute;
            var source = brush.ImageSource as BitmapSource;
            brush.Viewport = new Rect(0, 0, source?.PixelWidth ?? 0, source?.PixelHeight ?? 0);
        }
        else
        {
            brush.TileMode = TileMode.None;
            brush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
            brush.Viewport = new Rect(0, 0, 1, 1);
        }

        _image.HorizontalAlignment = mode switch
        {
            BackgroundSuitMode.Center => HorizontalAlignment.Center,
            BackgroundSuitMode.TopLeft or BackgroundSuitMode.BottomLeft => HorizontalAlignment.Left,
            BackgroundSuitMode.TopRight or BackgroundSuitMode.BottomRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch,
        };

        _image.VerticalAlignment = mode switch
        {
            BackgroundSuitMode.Center => VerticalAlignment.Center,
            BackgroundSuitMode.TopLeft or BackgroundSuitMode.TopRight => VerticalAlignment.Top,
            BackgroundSuitMode.BottomLeft or BackgroundSuitMode.BottomRight => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };
    }

    /// <summary>开始播放一段视频背景。</summary>
    public void ShowVideo(BackgroundVideo video)
    {
        ArgumentNullException.ThrowIfNull(video);

        try
        {
            _media.Source = new Uri(video.Path, UriKind.Absolute);
            _media.Visibility = Visibility.Visible;
            _image.Visibility = Visibility.Collapsed;
            _media.Play();

            State = BackgroundPlaybackState.Playing;
            LastFailure = BackgroundFailureKind.None;
            LastFailedFile = null;
        }
        catch (Exception ex)
        {
            Fail(ex, video.FileName);
        }
    }

    /// <summary>
    /// 按策略把当前状态落到控件上。
    /// </summary>
    /// <param name="hasContent">是否还有背景内容。</param>
    /// <param name="environment">环境状态。</param>
    public void Apply(bool hasContent, BackgroundEnvironment environment)
    {
        var target = BackgroundPlaybackPolicy.Resolve(hasContent, environment, State);

        if (target == State) return;

        State = target;

        switch (target)
        {
            case BackgroundPlaybackState.Playing:
                if (_media.Source is not null) _media.Play();
                break;

            case BackgroundPlaybackState.Paused:
                if (_media.Source is not null) _media.Pause();
                break;

            case BackgroundPlaybackState.Stopped:
                if (_media.Source is not null)
                {
                    _media.Stop();
                    _media.Position = TimeSpan.Zero;
                    _media.Source = null;
                }
                _image.Background = null;
                _image.Visibility = Visibility.Collapsed;
                _media.Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>清空背景（收起所有层）。</summary>
    public void Clear()
    {
        State = BackgroundPlaybackState.Stopped;

        if (_media.Source is not null)
        {
            _media.Stop();
            _media.Position = TimeSpan.Zero;
            _media.Source = null;
        }

        _image.Background = null;
        _image.Visibility = Visibility.Collapsed;
        _media.Visibility = Visibility.Collapsed;
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Fail(e.ErrorException, System.IO.Path.GetFileName(_media.Source?.OriginalString ?? ""));
    }

    private void Fail(Exception? exception, string? fileName)
    {
        var kind = BackgroundFailureClassifier.Classify(exception, fileName);

        LastFailure = kind;
        LastFailedFile = fileName;
        State = BackgroundPlaybackState.Stopped;

        _image.Visibility = Visibility.Collapsed;
        _media.Visibility = Visibility.Collapsed;

        Failed?.Invoke(this, kind);
    }
}
