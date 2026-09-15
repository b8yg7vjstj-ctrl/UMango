/// 设置页：动画风格 + 包源检测 + 运行诊断。
///
/// 需求要求动画风格是「全局独立配置项，用户可在设置里切换」，
/// 因此这里既有整套预设，也能单独调列表入场方向与弹窗方向，
/// 并带一个可反复触发的实时预览区 —— 设置改了到底有没有生效，
/// 用户当场就能看到，不用来回切页。
///
/// 视觉按 `docs/DESIGN_PCL_CE.md` 落地：
///   * 外层是 [SingleChildScrollView]，内边距 16；
///   * 内容按语义分成若干 [PclPanel] 卡片（圆角 6 / 白底 / 1px `gray6` 描边），
///     卡片间距 12；
///   * 每张卡片首行是 PCL 风格标题行：lucide 图标 + 14 号标题 + 右侧可选操作按钮；
///   * 说明文字 12 号 `gray3`，正文 13 号 `c1`，控件外观全部走契约 token
///     （下拉框圆角 3 / 1px `c1` 描边 / 白底；滑杆 `c3` + `gray5`；能力标签 `c6` + `c2`）。
///
/// 业务逻辑（数据加载、bridge 调用、错误处理分支、AnimationScope 的读写）与改造前
/// **完全一致**，本次只替换控件外观与分组方式。
library;

import 'package:flutter/material.dart';

import '../animation/animation_config.dart';
import '../animation/motion_widgets.dart';
import '../ffi/umango_bridge.dart';
import '../models/umango_models.dart';
import '../theme/pcl_theme.dart';
import '../widgets/pcl_widgets.dart';

// ---------------------------------------------------------------------------
// 本页专用度量
//
// 契约给的是「窗口/列表项/卡片/按钮」这一级的数值；页面内的卡片间距、表单标签列宽
// 这类排版细节契约没有直接规定，因此这里集中定义，取值贴齐契约的节奏（6 / 12 / 13）。
// ---------------------------------------------------------------------------

/// 页面内边距 16。
const double _pagePadding = 16.0;

/// 卡片之间的间距 12。
const double _cardGap = 12.0;

/// 卡片内边距 12：与 PCL 的紧凑信息密度一致（相当于列表项高 36、按钮高 30 的同一节奏）。
const EdgeInsets _cardPadding = EdgeInsets.all(12.0);

/// 卡片标题行与卡片正文之间的间距。
const double _cardHeaderGap = 10.0;

/// 卡片标题行左侧 lucide 图标的尺寸：契约的元信息图标是 12、按钮内图标 14，
/// 标题行取 16（与 [PclIcon] 的默认值一致）以匹配 14 号标题。
const double _cardIconSize = 16.0;

/// 表单行左侧标签列宽。
const double _formLabelWidth = 96.0;

/// 包源名称列宽。
const double _sourceNameWidth = 92.0;

/// 包源能力标签列宽（右对齐）。
const double _sourceChipsWidth = 200.0;

/// 滑杆右侧数值列宽。
const double _sliderValueWidth = 56.0;

/// 成功态文字色。
///
/// `DESIGN_PCL_CE.md` 的语义色只有红色系，没有成功绿；这里沿用项目
/// `StatusBanner(StatusTone.success)` 的取值（同一个绿），保证全站语义色一致。
const Color _successText = Color(0xFF1E6B33);

/// 警示态底色 / 文字色。
///
/// 同上：契约没有警示黄，沿用项目 `StatusBanner(StatusTone.warning)` 的取值，
/// 用于「缓存数据」「未验证」这类**不是错误**的提醒。
const Color _warningBack = Color(0xFFFFF3E0);
const Color _warningText = Color(0xFF8A5300);

class SettingsPage extends StatefulWidget {
  const SettingsPage({super.key, required this.onOpenAbout});

  final VoidCallback onOpenAbout;

  @override
  State<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends State<SettingsPage> {
  List<SourceView> _sources = const [];
  bool _loadingSources = true;
  String? _sourcesError;
  int _previewGeneration = 0;

  @override
  void initState() {
    super.initState();
    _loadSources();
  }

  Future<void> _loadSources({bool refresh = false}) async {
    setState(() {
      _loadingSources = true;
      _sourcesError = null;
    });
    try {
      final sources = await UmangoBridge.instance.sources(refresh: refresh);
      if (!mounted) return;
      setState(() {
        _sources = sources;
        _loadingSources = false;
      });
    } on UmangoError catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingSources = false;
        _sourcesError = '${e.message}（建议：${e.advice.displayName}）';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingSources = false;
        _sourcesError = '$e';
      });
    }
  }

  /// 预览弹窗：仍然走 [showUmangoDialog]，这样预览到的就是真实的弹窗动画风格
  /// （用 showDialog 会固定使用系统缩放动画，预览不出配置效果）。
  void _showDialogPreview(AnimationConfig config) {
    showUmangoDialog<void>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('弹窗动画预览'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Text('当前弹窗风格：${config.dialogMotion.displayName}',
                style: PclTypography.bodyStyle),
            const SizedBox(height: 8),
            Text(config.dialogMotion.description,
                style: PclTypography.captionStyle),
          ],
        ),
        actions: <Widget>[
          PclButton(
            label: '关闭',
            dense: true,
            onPressed: () => Navigator.of(context).pop(),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    // 配置仍然从 AnimationScope 读取（读取方式与改造前一致）。
    final config = AnimationScope.of(context);
    // 局部变量接住可空字段，便于类型提升（Dart 不会提升可变字段）。
    final String? sourcesError = _sourcesError;

    return SingleChildScrollView(
      padding: const EdgeInsets.all(_pagePadding),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          // ── 卡片 1：动画风格（4 个预设，点击即整组应用） ────────────────────
          _SettingsCard(
            icon: 'sparkles',
            title: '动画风格',
            subtitle: 'PCL-CE 的动画向量已做镜像改造：列表改为纵向入场、弹窗改为向右滑出。'
                '整套风格可随时切换，修改立即生效。',
            action: PclTag(text: config.matchingPreset?.displayName ?? '自定义'),
            child: _PresetRow(config: config),
          ),
          const SizedBox(height: _cardGap),

          // ── 卡片 2：总开关 ────────────────────────────────────────────────
          _SettingsCard(
            icon: 'puzzle',
            title: '总开关',
            child: _MasterSwitchRow(config: config),
          ),
          const SizedBox(height: _cardGap),

          // ── 卡片 3：列表入场方向（每个选项都带说明文字） ────────────────────
          _SettingsCard(
            icon: 'list-tree',
            title: '列表入场动画',
            child: _FormRow(
              label: '入场方向',
              child: DropdownButtonFormField<ListEntranceStyle>(
                initialValue: config.listEntrance,
                isDense: true,
                isExpanded: true,
                icon: const PclIcon(
                  name: 'chevron-down',
                  size: 14,
                  color: PclColors.gray2,
                ),
                style: PclTypography.bodyStyle,
                decoration: _pclDropdownDecoration(),
                items: <DropdownMenuItem<ListEntranceStyle>>[
                  for (final style in ListEntranceStyle.values)
                    DropdownMenuItem<ListEntranceStyle>(
                      value: style,
                      child: Text(
                        '${style.displayName} · ${style.description}',
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: PclTypography.bodyStyle,
                      ),
                    ),
                ],
                onChanged: (v) => v == null
                    ? null
                    : AnimationScope.update(
                        context, config.copyWith(listEntrance: v)),
              ),
            ),
          ),
          const SizedBox(height: _cardGap),

          // ── 卡片 4：弹窗动画方向 ──────────────────────────────────────────
          _SettingsCard(
            icon: 'box-select',
            title: '弹窗动画',
            child: _FormRow(
              label: '弹出方向',
              child: DropdownButtonFormField<DialogMotionStyle>(
                initialValue: config.dialogMotion,
                isDense: true,
                isExpanded: true,
                icon: const PclIcon(
                  name: 'chevron-down',
                  size: 14,
                  color: PclColors.gray2,
                ),
                style: PclTypography.bodyStyle,
                decoration: _pclDropdownDecoration(),
                items: <DropdownMenuItem<DialogMotionStyle>>[
                  for (final style in DialogMotionStyle.values)
                    DropdownMenuItem<DialogMotionStyle>(
                      value: style,
                      child: Text(
                        '${style.displayName} · ${style.description}',
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: PclTypography.bodyStyle,
                      ),
                    ),
                ],
                onChanged: (v) => v == null
                    ? null
                    : AnimationScope.update(
                        context, config.copyWith(dialogMotion: v)),
              ),
            ),
          ),
          const SizedBox(height: _cardGap),

          // ── 卡片 5：节奏参数（三个时长 + 位移幅度） ────────────────────────
          _SettingsCard(
            icon: 'gauge',
            title: '节奏参数',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                _DurationSlider(
                  label: '单项时长',
                  value: config.listDuration,
                  min: 100,
                  max: 800,
                  onChanged: (d) => AnimationScope.update(
                      context, config.copyWith(listDuration: d)),
                ),
                _DurationSlider(
                  label: '逐条延迟',
                  value: config.staggerStep,
                  min: 0,
                  max: 120,
                  onChanged: (d) => AnimationScope.update(
                      context, config.copyWith(staggerStep: d)),
                ),
                _DurationSlider(
                  label: '弹窗时长',
                  value: config.dialogDuration,
                  min: 80,
                  max: 600,
                  onChanged: (d) => AnimationScope.update(
                      context, config.copyWith(dialogDuration: d)),
                ),
                _ExtentSlider(
                  value: config.offsetExtent,
                  onChanged: (v) => AnimationScope.update(
                      context, config.copyWith(offsetExtent: v)),
                ),
              ],
            ),
          ),
          const SizedBox(height: _cardGap),

          // ── 卡片 6：效果预览（重播入场 / 预览弹窗） ────────────────────────
          _SettingsCard(
            icon: 'rocket',
            title: '效果预览',
            subtitle: '当前：${config.listEntrance.displayName} + '
                '${config.dialogMotion.displayName}',
            action: Row(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                PclButton(
                  label: '重播入场',
                  icon: 'refresh-cw',
                  dense: true,
                  onPressed: () => setState(() => _previewGeneration++),
                ),
                const SizedBox(width: 6),
                PclButton(
                  label: '预览弹窗',
                  icon: 'external-link',
                  dense: true,
                  onPressed: () => _showDialogPreview(config),
                ),
              ],
            ),
            child: _PreviewList(generation: _previewGeneration),
          ),
          const SizedBox(height: _cardGap),

          // ── 卡片 7：包源检测 ─────────────────────────────────────────────
          _SettingsCard(
            icon: 'server',
            title: '包源检测',
            subtitle: '可用性结果带 24 小时缓存保护：缓存期内不重复探测 CLI；'
                '刷新失败时会沿用上一次的成功结果并标注"数据可能过期"，而不是直接判为不可用。',
            action: PclButton(
              label: '重新检测',
              icon: 'refresh-cw',
              dense: true,
              onPressed:
                  _loadingSources ? null : () => _loadSources(refresh: true),
            ),
            child: _loadingSources
                ? const Padding(
                    padding: EdgeInsets.symmetric(vertical: 16.0),
                    child: Center(
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                  )
                : sourcesError != null
                    ? _ErrorBanner(title: '包源检测失败', message: sourcesError)
                    : _SourceList(sources: _sources),
          ),
          const SizedBox(height: _cardGap),

          // ── 卡片 8：运行诊断 ─────────────────────────────────────────────
          const _DiagnosticsCard(),
        ],
      ),
    );
  }
}

/// 卡片外壳：PclPanel（圆角 6 / 白底 / 1px `gray6` 描边）+ PCL 风格标题行。
///
/// 标题行 = 左侧 lucide 图标 + 14 号标题 + 右侧可选操作按钮；
/// 标题下方的 [subtitle] 是 12 号 `gray3` 的说明文字。
class _SettingsCard extends StatelessWidget {
  const _SettingsCard({
    required this.icon,
    required this.title,
    required this.child,
    this.subtitle,
    this.action,
  });

  /// lucide 图标名。
  final String icon;

  /// 卡片标题（14 号）。
  final String title;

  /// 卡片正文。
  final Widget child;

  /// 可选的说明文字（12 号 `gray3`）。
  final String? subtitle;

  /// 标题行右侧的可选操作控件。
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final String? sub = subtitle;

    return PclPanel(
      color: Colors.white,
      border: Border.all(
        color: PclColors.gray6,
        width: PclMetrics.buttonBorderWidth,
      ),
      padding: _cardPadding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Row(
            children: <Widget>[
              PclIcon(name: icon, size: _cardIconSize, color: PclColors.c3),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: PclTypography.cardTitleStyle
                      .copyWith(fontWeight: FontWeight.w600),
                ),
              ),
              // 空安全元素：没有操作控件时该项直接省略。
              ?action,
            ],
          ),
          if (sub != null) ...<Widget>[
            const SizedBox(height: 6),
            Text(sub, style: PclTypography.captionStyle),
          ],
          const SizedBox(height: _cardHeaderGap),
          child,
        ],
      ),
    );
  }
}

/// 表单行：左侧 13 号标签 + 右侧控件。
class _FormRow extends StatelessWidget {
  const _FormRow({required this.label, required this.child});

  final String label;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: <Widget>[
        SizedBox(
          width: _formLabelWidth,
          child: Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: PclTypography.bodyStyle,
          ),
        ),
        const SizedBox(width: 8),
        Expanded(child: child),
      ],
    );
  }
}

/// 下拉框统一外观：圆角 3（[PclMetrics.buttonRadius]）、1px `c1` 描边、白底。
///
/// 聚焦时描边换成主强调色 `c3`，与主题的 inputDecorationTheme 保持同一套观感。
InputDecoration _pclDropdownDecoration() => InputDecoration(
      isDense: true,
      filled: true,
      fillColor: Colors.white,
      contentPadding: const EdgeInsets.symmetric(horizontal: 10.0, vertical: 8.0),
      border: _pclOutline(PclColors.c1),
      enabledBorder: _pclOutline(PclColors.c1),
      focusedBorder: _pclOutline(PclColors.c3),
    );

/// 契约 §2「按钮 MyButton」的描边规格：圆角 3 + 1px。
OutlineInputBorder _pclOutline(Color color) => OutlineInputBorder(
      borderRadius: BorderRadius.circular(PclMetrics.buttonRadius),
      borderSide: BorderSide(
        color: color,
        width: PclMetrics.buttonBorderWidth,
      ),
    );

/// 总开关行。
///
/// 契约要求「选中色 `c3`、非选中轨道 `gray5`」。Flutter 3.44 已把过去的
/// `Switch.activeColor` 拆成 thumb/track 两个参数，因此这里写成
/// `activeTrackColor`/`activeThumbColor = c3`（白色滑块压在 c3 轨道上）
/// 与 `inactiveTrackColor = gray5`，观感与 `activeColor: c3` 等价且不触发弃用告警。
class _MasterSwitchRow extends StatelessWidget {
  const _MasterSwitchRow({required this.config});

  final AnimationConfig config;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: <Widget>[
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Text('启用自定义动画', style: PclTypography.bodyStyle),
              const SizedBox(height: 2),
              Text(
                AnimationScope.prefersReducedMotion(context)
                    ? '注意：系统已开启"减少动态效果"，动画会被自动跳过'
                    : '关闭后所有列表与弹窗动画直接跳过',
                style: PclTypography.captionStyle,
              ),
            ],
          ),
        ),
        const SizedBox(width: 12),
        Switch(
          value: config.enabled,
          activeThumbColor: Colors.white,
          activeTrackColor: PclColors.c3,
          inactiveThumbColor: Colors.white,
          inactiveTrackColor: PclColors.gray5,
          onChanged: (v) =>
              AnimationScope.update(context, config.copyWith(enabled: v)),
        ),
      ],
    );
  }
}

/// 时长滑杆行：滑杆 `activeColor = c3`、`inactiveColor = gray5`（契约明示），
/// 右侧用 12 号 `gray3` 显示当前毫秒数。
class _DurationSlider extends StatelessWidget {
  const _DurationSlider({
    required this.label,
    required this.value,
    required this.min,
    required this.max,
    required this.onChanged,
  });

  final String label;
  final Duration value;
  final int min;
  final int max;
  final ValueChanged<Duration> onChanged;

  @override
  Widget build(BuildContext context) {
    return _FormRow(
      label: label,
      child: Row(
        children: <Widget>[
          Expanded(
            child: Slider(
              value: value.inMilliseconds.clamp(min, max).toDouble(),
              min: min.toDouble(),
              max: max.toDouble(),
              activeColor: PclColors.c3,
              inactiveColor: PclColors.gray5,
              onChanged: (v) => onChanged(Duration(milliseconds: v.round())),
            ),
          ),
          SizedBox(
            width: _sliderValueWidth,
            child: Text(
              '${value.inMilliseconds}ms',
              textAlign: TextAlign.right,
              style: PclTypography.captionStyle,
            ),
          ),
        ],
      ),
    );
  }
}

/// 位移幅度滑杆行（取值是自身尺寸的比例，界面按百分比显示）。
class _ExtentSlider extends StatelessWidget {
  const _ExtentSlider({required this.value, required this.onChanged});

  final double value;
  final ValueChanged<double> onChanged;

  @override
  Widget build(BuildContext context) {
    return _FormRow(
      label: '位移幅度',
      child: Row(
        children: <Widget>[
          Expanded(
            child: Slider(
              value: value,
              min: 0.05,
              max: 0.6,
              activeColor: PclColors.c3,
              inactiveColor: PclColors.gray5,
              onChanged: onChanged,
            ),
          ),
          SizedBox(
            width: _sliderValueWidth,
            child: Text(
              '${(value * 100).toStringAsFixed(0)}%',
              textAlign: TextAlign.right,
              style: PclTypography.captionStyle,
            ),
          ),
        ],
      ),
    );
  }
}

/// 4 个动画预设卡片（点击即整组应用 [AnimationPreset.config]）。
class _PresetRow extends StatelessWidget {
  const _PresetRow({required this.config});

  final AnimationConfig config;

  @override
  Widget build(BuildContext context) {
    final active = config.matchingPreset;
    // 4 张卡片等分卡片宽度（而不是固定宽度 + Wrap）：窗口默认 850 宽时正好排成一行，
    // 不会出现「3 张一行 + 1 张掉到下一行」的断行。
    //
    // ⚠️ 这里必须套 IntrinsicHeight：
    // Row 的 cross axis 是**垂直**方向，而本页整页在 SingleChildScrollView 里
    // （高度无界）。直接给 Row 加 CrossAxisAlignment.stretch 会要求子项填充一个
    // 无法确定的无限高度，触发 'hasSize' 断言失败（RenderBox was not laid out），
    // 页面直接崩。IntrinsicHeight 先把 Row 的高度定为最高子项的高度，
    // stretch 才有一个确定的目标高度可用。
    // —— 这个崩溃是集成测试抓出来的：编译能过、单元测试也全绿，
    //    只有真实渲染才会暴露。
    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          for (final preset in AnimationPreset.values) ...<Widget>[
            Expanded(
              child: _PresetCard(
                preset: preset,
                active: active == preset,
                onTap: () => AnimationScope.update(context, preset.config),
              ),
            ),
            if (preset != AnimationPreset.values.last)
              const SizedBox(width: 12),
          ],
        ],
      ),
    );
  }
}

/// 单个预设卡片：圆角 6 + 1px 描边；选中态用 `c6` 底 + `c3` 描边 + `c2` 文字
/// （与 [PclListItem] / PclTag 的「选中 = c6 底 + c2 字」保持同一套语义）；
/// 悬停时未选中的卡片给 `gray8` 浅底，提供桌面端应有的悬停反馈。
class _PresetCard extends StatefulWidget {
  const _PresetCard({
    required this.preset,
    required this.active,
    required this.onTap,
  });

  final AnimationPreset preset;
  final bool active;
  final VoidCallback onTap;

  @override
  State<_PresetCard> createState() => _PresetCardState();
}

class _PresetCardState extends State<_PresetCard> {
  bool _hovered = false;

  void _setHovered(bool value) {
    if (_hovered == value) return;
    setState(() => _hovered = value);
  }

  @override
  Widget build(BuildContext context) {
    final bool active = widget.active;
    final Color background = active
        ? PclColors.c6
        : (_hovered ? PclColors.gray8 : Colors.white);

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => _setHovered(true),
      onExit: (_) => _setHovered(false),
      child: GestureDetector(
        onTap: widget.onTap,
        behavior: HitTestBehavior.opaque,
        child: Container(
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: background,
            borderRadius: BorderRadius.circular(PclMetrics.windowRadius),
            border: Border.all(
              color: active ? PclColors.c3 : PclColors.gray6,
              width: PclMetrics.buttonBorderWidth,
            ),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Row(
                children: <Widget>[
                  Expanded(
                    child: Text(
                      widget.preset.displayName,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: PclTypography.cardTitleStyle.copyWith(
                        fontWeight: FontWeight.w600,
                        color: active ? PclColors.c2 : PclColors.c1,
                      ),
                    ),
                  ),
                  if (active)
                    const PclIcon(
                      name: 'circle-check',
                      size: 14,
                      color: PclColors.c3,
                    ),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                widget.preset.description,
                style: PclTypography.captionStyle,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// 实时预览列表：改了设置立刻能看到入场效果，点「重播入场」重建列表再播一次。
class _PreviewList extends StatelessWidget {
  const _PreviewList({required this.generation});

  final int generation;

  @override
  Widget build(BuildContext context) {
    return Column(
      // 用 generation 作为 key：点「重播」时重建列表以重新播放入场。
      key: ValueKey<String>('preview-$generation'),
      children: <Widget>[
        for (var i = 0; i < 4; i++)
          ListEntrance(
            index: i,
            axisKey: generation,
            child: Container(
              margin: const EdgeInsets.only(bottom: 6.0),
              padding: const EdgeInsets.symmetric(
                horizontal: 10.0,
                vertical: 8.0,
              ),
              decoration: BoxDecoration(
                // c8 = 契约里的「最浅蓝（面板底）」，用作预览条底色。
                color: PclColors.c8,
                borderRadius: BorderRadius.circular(PclMetrics.buttonRadius),
                border: Border.all(
                  color: PclColors.gray6,
                  width: PclMetrics.buttonBorderWidth,
                ),
              ),
              child: Row(
                children: <Widget>[
                  const PclIcon(
                    name: 'package',
                    size: PclMetrics.metaIconSize,
                    color: PclColors.c3,
                  ),
                  const SizedBox(width: 8),
                  Text('预览列表项 ${i + 1}', style: PclTypography.bodyStyle),
                ],
              ),
            ),
          ),
      ],
    );
  }
}

/// 错误提示条（PCL 数值自绘）。
///
/// 契约 §5 没有「状态条」组件，项目里的 `StatusBanner` 用的是 Material 的 12 圆角与
/// 18 内边距 —— 直接用会把非契约数值带进本页，所以按契约数值自绘：
/// `redBack` 底 + 1px `error` 描边 + 6 圆角，文字用 `redDark`；
/// 仍然包在 [ListEntrance] 里，保持原有的入场动画。
class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner({required this.title, required this.message, this.action});

  final String title;
  final String message;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final Widget? act = action;
    return ListEntrance(
      index: 0,
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(12.0),
        decoration: BoxDecoration(
          color: PclColors.redBack,
          borderRadius: BorderRadius.circular(PclMetrics.windowRadius),
          border: Border.all(
            color: PclColors.error,
            width: PclMetrics.buttonBorderWidth,
          ),
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            const PclIcon(
              name: 'triangle-alert',
              size: 16,
              color: PclColors.redDark,
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Text(
                    title,
                    style: PclTypography.bodyStyle.copyWith(
                      fontWeight: FontWeight.w600,
                      color: PclColors.redDark,
                    ),
                  ),
                  const SizedBox(height: 4),
                  SelectableText(
                    message,
                    style: PclTypography.captionStyle
                        .copyWith(color: PclColors.redDark),
                  ),
                ],
              ),
            ),
            if (act != null) ...<Widget>[const SizedBox(width: 8), act],
          ],
        ),
      ),
    );
  }
}

/// 包源状态行列表：行间是 1px `gray6` 浅分隔（契约 §1 gray6 的用途）。
class _SourceList extends StatelessWidget {
  const _SourceList({required this.sources});

  final List<SourceView> sources;

  @override
  Widget build(BuildContext context) {
    if (sources.isEmpty) {
      // 契约没有规定空态；给一句中文说明，避免出现一张只有描边的空卡片。
      return Text(
        '未检测到任何包源，可点击右上角「重新检测」重试。',
        style: PclTypography.captionStyle,
      );
    }
    return Column(
      children: <Widget>[
        for (var i = 0; i < sources.length; i++) ...<Widget>[
          _SourceRow(source: sources[i], index: i),
          if (i != sources.length - 1) const Divider(height: 12.0),
        ],
      ],
    );
  }
}

/// 单个包源的状态行：状态 / CLI 版本 / CLI 路径 / stale 标注 / verificationNote 提示。
class _SourceRow extends StatelessWidget {
  const _SourceRow({required this.source, required this.index});

  final SourceView source;
  final int index;

  @override
  Widget build(BuildContext context) {
    final a = source.availability;
    final String? verificationNote = source.verificationNote;

    // 状态色：可用 = 项目的成功绿、降级 = 项目的警示黄，
    // 其余不可用状态统一用契约的 error 红 / gray3。
    final (Color color, String icon) = switch (a.state) {
      AvailabilityState.available => (_successText, 'circle-check'),
      AvailabilityState.degraded => (_warningText, 'clock'),
      AvailabilityState.unsupportedPlatform => (PclColors.gray3, 'box'),
      AvailabilityState.notInstalled => (PclColors.error, 'download'),
      AvailabilityState.unreachable => (PclColors.error, 'triangle-alert'),
    };

    return ListEntrance(
      index: index,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          PclIcon(name: icon, size: _cardIconSize, color: color),
          const SizedBox(width: 8),
          SizedBox(
            width: _sourceNameWidth,
            child: Text(
              source.displayName,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: PclTypography.bodyStyle
                  .copyWith(fontWeight: FontWeight.w600),
            ),
          ),
          SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    Text(
                      a.state.displayName,
                      style: PclTypography.captionStyle.copyWith(color: color),
                    ),
                    if (a.stale) ...<Widget>[
                      const SizedBox(width: 6),
                      const PclTag(
                        text: '缓存数据',
                        background: _warningBack,
                        color: _warningText,
                      ),
                    ],
                    if (verificationNote != null) ...<Widget>[
                      const SizedBox(width: 6),
                      // 原来是一个只有图标的 Tooltip；换成带文字的小标签，
                      // 提示内容直接用 verificationNote 正文（判空分支不变），
                      // 用户不用悬停也能看出「这条包源尚未在真机验证」。
                      Tooltip(
                        message: verificationNote,
                        child: const PclTag(
                          text: '未验证',
                          background: _warningBack,
                          color: _warningText,
                        ),
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 3),
                Text(
                  <String>[
                    if (a.cliVersion != null) '版本 ${a.cliVersion}',
                    if (a.cliPath != null) a.cliPath!,
                    if (a.detail != null) a.detail!,
                  ].join(' · '),
                  style: PclTypography.captionStyle,
                ),
              ],
            ),
          ),
          const SizedBox(width: 8),
          SizedBox(
            width: _sourceChipsWidth,
            child: _CapabilityChips(capabilities: source.capabilities),
          ),
        ],
      ),
    );
  }
}

/// 包源能力标签（搜索 / 依赖树 / 官方下载量）。
///
/// 契约没有单独的「能力」组件，这里用 `FilterChip` 呈现选中态：
/// `c6` 底 + `c2` 文字 + 圆角 3。
/// 三项恒显示、以「是否选中」表达是否支持（原实现只列出支持项）——
/// 数据仍然只读 `source.capabilities` 的三个布尔值，判断逻辑一行未改。
class _CapabilityChips extends StatelessWidget {
  const _CapabilityChips({required this.capabilities});

  final SourceCapabilities capabilities;

  @override
  Widget build(BuildContext context) {
    final List<(String, bool)> entries = <(String, bool)>[
      ('搜索', capabilities.search),
      ('依赖树', capabilities.resolveDeps),
      ('官方下载量', capabilities.officialDownloads),
    ];

    return Wrap(
      alignment: WrapAlignment.end,
      spacing: 4.0,
      runSpacing: 4.0,
      children: <Widget>[
        for (final (String label, bool supported) in entries)
          FilterChip(
            label: Text(label),
            selected: supported,
            // 能力标签是只读信息（不参与筛选），因此 onSelected 传 null；
            // 配色全部显式给出，不受 M3 禁用态默认色影响。
            onSelected: null,
            showCheckmark: false,
            visualDensity: VisualDensity.compact,
            materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
            backgroundColor: PclColors.gray8,
            selectedColor: PclColors.c6,
            disabledColor: PclColors.gray7,
            side: BorderSide.none,
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(PclMetrics.tagRadius),
            ),
            labelStyle: TextStyle(
              fontSize: PclTypography.label,
              color: supported ? PclColors.c2 : PclColors.gray4,
              fontFamilyFallback: PclTypography.fontFallback,
            ),
            padding: const EdgeInsets.symmetric(horizontal: 6.0, vertical: 2.0),
            // 关掉 M3 默认的 8px 标签内边距，让三个能力标签能排在一行（PCL 的标签很紧凑）。
            labelPadding: EdgeInsets.zero,
          ),
      ],
    );
  }
}

/// 运行诊断卡片：core.info 各项 + 桥接层自检 JSON + 重新诊断按钮。
class _DiagnosticsCard extends StatefulWidget {
  const _DiagnosticsCard();

  @override
  State<_DiagnosticsCard> createState() => _DiagnosticsCardState();
}

class _DiagnosticsCardState extends State<_DiagnosticsCard> {
  CoreInfo? _info;
  Map<String, dynamic>? _selfTest;
  String? _error;
  bool _running = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _running = true;
      _error = null;
    });
    try {
      final bridge = UmangoBridge.instance;
      final info = await bridge.coreInfo();
      final selfTest = await bridge.selfTest();
      if (!mounted) return;
      setState(() {
        _info = info;
        _selfTest = selfTest;
        _running = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _running = false;
        _error = '$e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    // 局部变量接住可空字段，便于类型提升。
    final CoreInfo? info = _info;
    final Map<String, dynamic>? selfTest = _selfTest;
    final String? error = _error;

    return _SettingsCard(
      icon: 'wrench',
      title: '运行诊断',
      subtitle: '用于确认三层架构真的接通了：进程执行器显示 cpp-bridge，'
          '说明进程调用确实经过 C++ 桥接层，而不是悄悄退化成了纯 Rust 实现。',
      action: PclButton(
        label: '重新诊断',
        icon: 'refresh-cw',
        dense: true,
        onPressed: _running ? null : _load,
      ),
      child: _running
          ? const Padding(
              padding: EdgeInsets.symmetric(vertical: 16.0),
              child: Center(child: CircularProgressIndicator(strokeWidth: 2)),
            )
          : error != null
              ? _ErrorBanner(
                  title: '诊断失败',
                  message: error,
                  action: PclButton(label: '重试', dense: true, onPressed: _load),
                )
              : info == null
                  ? const SizedBox.shrink()
                  : Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        Wrap(
                          spacing: 20,
                          runSpacing: 10,
                          children: <Widget>[
                            _Diag(
                              label: '核心层版本',
                              value: info.coreVersion,
                            ),
                            _Diag(
                              label: '宿主平台',
                              value: '${info.platformDisplay} (${info.arch})',
                            ),
                            _Diag(
                              label: 'ABI 版本',
                              value: '${UmangoBridge.instance.abiVersion}',
                            ),
                            _Diag(
                              label: '可用性缓存 TTL',
                              value: '${info.availabilityTtlSecs ~/ 3600} 小时',
                            ),
                            _Diag(
                              label: '进程执行器',
                              value: info.processRunner,
                              highlight: info.processRunner == 'cpp-bridge',
                              hint: info.processRunner == 'cpp-bridge'
                                  ? '进程调用确实经过 C++ 桥接层'
                                  : '警告：未经过 C++ 层，可能是宿主回调未生效',
                            ),
                          ],
                        ),
                        const SizedBox(height: 14),
                        if (selfTest != null) ...<Widget>[
                          Text('桥接层自检', style: PclTypography.captionStyle),
                          const SizedBox(height: 6),
                          Container(
                            width: double.infinity,
                            padding: const EdgeInsets.all(10),
                            decoration: BoxDecoration(
                              // gray8 = 契约里的「更浅底」，用在只读代码块上很克制。
                              color: PclColors.gray8,
                              borderRadius:
                                  BorderRadius.circular(PclMetrics.buttonRadius),
                              border: Border.all(
                                color: PclColors.gray6,
                                width: PclMetrics.buttonBorderWidth,
                              ),
                            ),
                            child: SelectableText(
                              const JsonEncoderPretty().convert(selfTest),
                              style: const TextStyle(
                                fontFamily: 'monospace',
                                fontFamilyFallback: PclTypography.fontFallback,
                                fontSize: PclTypography.caption,
                                color: PclColors.gray2,
                                height: 1.35,
                              ),
                            ),
                          ),
                        ],
                      ],
                    ),
    );
  }
}

/// 轻量 JSON 美化输出（避免为此引入依赖）
class JsonEncoderPretty {
  const JsonEncoderPretty();

  String convert(Object? value) {
    final buffer = StringBuffer();
    _write(value, buffer, 0);
    return buffer.toString();
  }

  void _write(Object? value, StringBuffer out, int indent) {
    final pad = '  ' * indent;
    final padInner = '  ' * (indent + 1);
    if (value is Map) {
      if (value.isEmpty) {
        out.write('{}');
        return;
      }
      out.write('{\n');
      final entries = value.entries.toList();
      for (var i = 0; i < entries.length; i++) {
        out.write(padInner);
        out.write('"${entries[i].key}": ');
        _write(entries[i].value, out, indent + 1);
        if (i != entries.length - 1) out.write(',');
        out.write('\n');
      }
      out.write('$pad}');
    } else if (value is List) {
      if (value.isEmpty) {
        out.write('[]');
        return;
      }
      out.write('[\n');
      for (var i = 0; i < value.length; i++) {
        out.write(padInner);
        _write(value[i], out, indent + 1);
        if (i != value.length - 1) out.write(',');
        out.write('\n');
      }
      out.write('$pad]');
    } else if (value is String) {
      out.write('"$value"');
    } else {
      out.write('$value');
    }
  }
}

/// 单条诊断项：12 号 `gray3` 标签 + 13 号等宽值。
///
/// 突出显示时用契约的 `c2`（「深蓝（强调文字）」）—— 契约没有成功绿，
/// 强调色就取契约自带的那一支，避免引入规范外的颜色。
class _Diag extends StatelessWidget {
  const _Diag({
    required this.label,
    required this.value,
    this.highlight = false,
    this.hint,
  });

  final String label;
  final String value;
  final bool highlight;
  final String? hint;

  @override
  Widget build(BuildContext context) {
    final Widget content = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        Text(label, style: PclTypography.captionStyle),
        const SizedBox(height: 2),
        Text(
          value,
          style: TextStyle(
            fontSize: PclTypography.body,
            fontWeight: FontWeight.w600,
            fontFamily: 'monospace',
            fontFamilyFallback: PclTypography.fontFallback,
            color: highlight ? PclColors.c2 : PclColors.c1,
          ),
        ),
      ],
    );

    final String? tip = hint;
    // hint 为空时不再套 Tooltip：空的提示框只会在悬停时干扰阅读。
    if (tip == null) return content;
    return Tooltip(message: tip, child: content);
  }
}
