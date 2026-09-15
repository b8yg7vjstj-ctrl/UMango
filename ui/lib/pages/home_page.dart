/// 主页：平台概览与包源能力矩阵（PCL-CE 观感）。
///
/// 数据逻辑保持不变：仍然只调用 `UmangoBridge.coreInfo()` 与 `sources()`，
/// 错误分支仍然展示 [UmangoError] 的 `code` 与 `advice`（可操作建议），
/// 能力矩阵与「阶段1 能力边界」的说明文案也原样保留 —— 本次只改视觉与布局。
///
/// 视觉契约：`docs/DESIGN_PCL_CE.md`。页面里不再出现 Card / ListTile 等
/// Material 默认组件，全部改用 PclPanel / PclButton / PclTag / PclIcon。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../ffi/umango_bridge.dart';
import '../models/umango_models.dart';
import '../theme/pcl_theme.dart';
import '../widgets/pcl_widgets.dart';

/// 能力矩阵的列宽：包源 130 / 状态 118 / 能力 Expanded（与其他页面共用同一节奏）。
const double _colSourceWidth = 130;
const double _colStateWidth = 118;

/// 表格行高 36（与 [PclMetrics.listItemHeight] 一致）。
const double _matrixRowHeight = PclMetrics.listItemHeight;

/// 未验证 / 降级状态的琥珀色 `#8A5300`（与依赖树、包卡片共用的语义色）。
const Color _warnAmber = Color(0xFF8A5300);

class HomePage extends StatefulWidget {
  const HomePage({super.key, required this.onGoToDownload});

  final VoidCallback onGoToDownload;

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  CoreInfo? _info;
  List<SourceView> _sources = const [];
  String? _error;

  /// 原始异常对象。
  ///
  /// **为什么同时留一个字符串和一个对象**：`_error` 是原来的兜底展示（任何异常都能
  /// 转成文本），而 [UmangoError] 带机器可读的 `code` 与 `advice`，展开建议时必须
  /// 拿到原对象。两者并存不改变任何错误分支的判断逻辑。
  Object? _lastError;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final bridge = UmangoBridge.instance;
      final info = await bridge.coreInfo();
      final sources = await bridge.sources();
      if (!mounted) return;
      setState(() {
        _info = info;
        _sources = sources;
        _error = null;
        _lastError = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = '$e';
        _lastError = e;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final info = _info;
    final error = _error;

    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
      children: [
        if (error != null) ...[
          ListEntrance(
            index: 0,
            child: _ErrorPanel(
              message: error,
              error: _lastError,
              onRetry: _load,
            ),
          ),
          const SizedBox(height: 16),
        ],
        if (info != null) ...[
          ListEntrance(
            index: 0,
            child: _OverviewPanel(
              info: info,
              onGoToDownload: widget.onGoToDownload,
            ),
          ),
          const SizedBox(height: 18),
        ],
        Text(
          '包源能力矩阵',
          style: PclTypography.cardTitleStyle.copyWith(fontWeight: FontWeight.w600),
        ),
        const SizedBox(height: 4),
        // 文案随新规则更新：被平台门控的源不再被隐藏，而是如实列出并标注状态。
        const Text(
          '被平台门控或缺少命令行工具的包源不会被过滤掉，而是如实列出并标注状态；'
          '带「未验证」标记的适配器其解析逻辑尚未在真机验证过。',
          style: PclTypography.captionStyle,
        ),
        const SizedBox(height: 12),
        ListEntrance(
          index: 1,
          child: PclPanel(
            radius: PclMetrics.windowRadius,
            color: Colors.white,
            border: Border.all(color: PclColors.gray5),
            padding: const EdgeInsets.symmetric(horizontal: 14),
            child: Column(
              children: [
                const _MatrixHeader(),
                const _MatrixDivider(),
                for (var i = 0; i < _sources.length; i++) ...[
                  _MatrixRow(source: _sources[i], index: i),
                  if (i != _sources.length - 1) const _MatrixDivider(),
                ],
              ],
            ),
          ),
        ),
        const SizedBox(height: 20),
        ListEntrance(
          index: 2,
          child: PclPanel(
            radius: PclMetrics.windowRadius,
            color: Colors.white,
            border: Border.all(color: PclColors.gray5),
            padding: const EdgeInsets.all(14),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '阶段1 能力边界（如实说明）',
                  style: PclTypography.cardTitleStyle.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
                ),
                const SizedBox(height: 10),
                const _Bullet('已实现：包源列表、搜索、版本列表（含通道区分）、'
                    '依赖树解析与可视化、下载量统计（官方优先 / GitHub Stars 兜底）、'
                    '动画风格配置、包源可用性 24 小时保护。'),
                const _Bullet('未实现：安装 / 卸载 / 升级的实际执行 —— 界面只生成命令计划供确认，'
                    '真正执行在阶段3 接入。'),
                const _Bullet('未验证：winget 与 scoop 适配器受平台限制无法在 macOS 上验证，'
                    '已在界面明确标注。'),
                const _Bullet('未打包：桥接层动态库尚未打进 .app（需改 Xcode 工程），'
                    '当前按固定顺序从构建目录加载。'),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

/// 错误兜底面板（PCL：PclPanel + triangle-alert + redBack 底）。
///
/// 保留原有语义：[UmangoError] 的 `code` 与 `advice` 都要给用户看到 ——
/// 只甩一段原始字符串等于把排障成本转嫁给用户。
class _ErrorPanel extends StatelessWidget {
  const _ErrorPanel({
    required this.message,
    required this.error,
    required this.onRetry,
  });

  /// 兜底文本（任何异常都有的可读形式）。
  final String message;

  /// 原始异常，用于取出 [UmangoError] 的结构化字段。
  final Object? error;

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final e = error;
    final String? code = e is UmangoError ? e.code : null;
    final String? advice = e is UmangoError ? e.advice.displayName : null;
    final String body = e is UmangoError ? e.message : message;

    return PclPanel(
      radius: PclMetrics.windowRadius,
      color: PclColors.redBack,
      border: Border.all(color: PclColors.redLight),
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const PclIcon(
                name: 'triangle-alert',
                size: 15,
                color: PclColors.redDark,
              ),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  '无法读取核心层信息',
                  style: PclTypography.bodyStyle.copyWith(
                    fontWeight: FontWeight.w600,
                    color: PclColors.redDark,
                  ),
                ),
              ),
              PclButton(label: '重试', dense: true, onPressed: onRetry),
            ],
          ),
          const SizedBox(height: 8),
          Text(
            body,
            style: PclTypography.captionStyle.copyWith(color: PclColors.redDark),
          ),
          if (code != null || advice != null) ...[
            const SizedBox(height: 8),
            Wrap(
              spacing: 6,
              runSpacing: 4,
              children: [
                if (code != null)
                  PclTag(
                    text: '错误码 $code',
                    color: PclColors.redDark,
                    background: Colors.white,
                  ),
                if (advice != null)
                  PclTag(
                    text: '建议 $advice',
                    color: PclColors.redDark,
                    background: Colors.white,
                  ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

/// 顶部概览卡：自绘应用图标 + 标题 + 「开始搜索包」主按钮 + kv 行。
class _OverviewPanel extends StatelessWidget {
  const _OverviewPanel({required this.info, required this.onGoToDownload});

  final CoreInfo info;
  final VoidCallback onGoToDownload;

  @override
  Widget build(BuildContext context) {
    final bool cppBridge = info.processRunner == 'cpp-bridge';

    return PclPanel(
      radius: PclMetrics.windowRadius,
      color: Colors.white,
      border: Border.all(color: PclColors.gray5),
      padding: const EdgeInsets.all(14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // 应用图标：**自绘**（PCL 蓝渐变圆角底 + lucide `box`），
              // 不使用任何 PCL-CE 的图片资源，也不再用 emoji 占位。
              Container(
                width: PclMetrics.cardIconSize,
                height: PclMetrics.cardIconSize,
                decoration: BoxDecoration(
                  gradient: const LinearGradient(
                    begin: Alignment.topLeft,
                    end: Alignment.bottomRight,
                    colors: [PclColors.c3, PclColors.c5],
                  ),
                  borderRadius: BorderRadius.circular(PclMetrics.cardIconRadius),
                ),
                child: const Center(
                  child: PclIcon(name: 'box', size: 26, color: Colors.white),
                ),
              ),
              const SizedBox(width: PclMetrics.cardIconTextGap),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('UMango', style: PclTypography.pageTitleStyle),
                    const SizedBox(height: 2),
                    const Text(
                      'Uneven Mango Application · 跨平台包管理器图形界面',
                      style: PclTypography.captionStyle,
                    ),
                  ],
                ),
              ),
              PclButton(
                label: '开始搜索包',
                icon: 'search',
                primary: true,
                onPressed: onGoToDownload,
              ),
            ],
          ),
          const SizedBox(height: 12),
          // kv 行：核心层 / 桥接层 / 执行器 / 平台 / 缓存
          _KvRow(label: '核心层版本', value: 'Rust ${info.coreVersion}'),
          _KvRow(
            label: '桥接层 ABI',
            value: 'C++ / ABI ${UmangoBridge.instance.abiVersion}',
          ),
          // 进程执行器是关键指标：真的是 cpp-bridge 才说明进程调用过了桥接层，
          // 因此命中时用主强调色 c3 高亮，否则按未达标警示。
          _KvRow(
            label: '进程执行器',
            value: info.processRunner,
            highlight: cppBridge,
          ),
          _KvRow(label: '宿主平台', value: '${info.platformDisplay} ${info.arch}'),
          _KvRow(
            label: '可用性缓存',
            value: '${info.availabilityTtlSecs ~/ 3600} 小时',
          ),
        ],
      ),
    );
  }
}

/// 概览卡里的一行键值：左侧 12 号 gray3 的键，右侧等宽 13 号的值。
class _KvRow extends StatelessWidget {
  const _KvRow({required this.label, required this.value, this.highlight});

  final String label;
  final String value;

  /// `null` = 中性值；`true` = 达标（c3）；`false` = 未达标（琥珀 `#8A5300`）。
  final bool? highlight;

  @override
  Widget build(BuildContext context) {
    final bool? ok = highlight;
    final Color valueColor = ok == null
        ? PclColors.c1
        : (ok ? PclColors.c3 : _warnAmber);

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          SizedBox(
            width: 92,
            child: Text(label, style: PclTypography.captionStyle),
          ),
          if (ok != null) ...[
            PclIcon(
              name: ok ? 'circle-check' : 'triangle-alert',
              size: PclMetrics.metaIconSize,
              color: valueColor,
            ),
            const SizedBox(width: PclMetrics.metaIconGap),
          ],
          Flexible(
            child: Text(
              value,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: PclTypography.body,
                fontFamily: 'monospace',
                color: valueColor,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// 能力矩阵表头：12 号 gray3，行高 36，列宽 130 / 118 / Expanded。
class _MatrixHeader extends StatelessWidget {
  const _MatrixHeader();

  @override
  Widget build(BuildContext context) {
    const TextStyle style = TextStyle(
      fontSize: PclTypography.caption,
      color: PclColors.gray3,
      fontFamilyFallback: PclTypography.fontFallback,
      fontWeight: FontWeight.w600,
    );
    return SizedBox(
      height: _matrixRowHeight,
      child: Row(
        children: const [
          SizedBox(width: _colSourceWidth, child: Text('包源', style: style)),
          SizedBox(width: _colStateWidth, child: Text('状态', style: style)),
          Expanded(child: Text('能力', style: style)),
        ],
      ),
    );
  }
}

/// 表格分隔线：1px gray6（规格「浅分隔」），比 Divider 更贴近 PCL 的克制观感。
class _MatrixDivider extends StatelessWidget {
  const _MatrixDivider();

  @override
  Widget build(BuildContext context) {
    return Container(height: 1, color: PclColors.gray6);
  }
}

/// 能力矩阵的一行。
///
/// **被平台门控的源不过滤**：`_sources` 里有什么就渲染什么，门控结果只体现在
/// 「状态」列的文字颜色上（可用 gray3 / 不可用 error），矩阵因此始终与核心层
/// 的真实能力一致。
class _MatrixRow extends StatelessWidget {
  const _MatrixRow({required this.source, required this.index});

  final SourceView source;
  final int index;

  @override
  Widget build(BuildContext context) {
    final a = source.availability;
    final Color stateColor = a.state.isUsable ? PclColors.gray3 : PclColors.error;
    final String? note = source.verificationNote;

    return ListEntrance(
      index: index,
      child: SizedBox(
        height: _matrixRowHeight,
        child: Row(
          children: [
            SizedBox(
              width: _colSourceWidth,
              child: Row(
                children: [
                  Flexible(
                    child: Text(
                      source.displayName,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontSize: PclTypography.body,
                        fontWeight: FontWeight.w600,
                        fontFamilyFallback: PclTypography.fontFallback,
                        color: PclColors.c1,
                      ),
                    ),
                  ),
                  if (note != null) ...[
                    const SizedBox(width: 5),
                    // 未验证标记：lucide `flask-conical` + tooltip，
                    // 「看起来能用」与「已验证」必须在视觉上分开。
                    Tooltip(
                      message: note,
                      child: const PclIcon(
                        name: 'flask-conical',
                        size: 13,
                        color: _warnAmber,
                      ),
                    ),
                  ],
                ],
              ),
            ),
            SizedBox(
              width: _colStateWidth,
              child: Text(
                a.state.displayName,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontSize: PclTypography.caption,
                  fontFamilyFallback: PclTypography.fontFallback,
                  color: stateColor,
                ),
              ),
            ),
            Expanded(
              child: Wrap(
                spacing: 4,
                runSpacing: 2,
                children: [
                  for (final (String text, bool supported) in _capabilityTags(source))
                    PclTag(
                      text: text,
                      // 不支持的能力如实标注（gray7 底 + gray3 字），而不是从矩阵里消失。
                      color: supported ? null : PclColors.gray3,
                      background: supported ? null : PclColors.gray7,
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  /// 能力标签：(文字, 是否支持)。
  ///
  /// 保留原有语义 —— 依赖树缺数据时写「无依赖数据」、下载量用兜底时写
  /// 「Stars 兜底」，避免把兜底数据伪装成官方数据。
  List<(String, bool)> _capabilityTags(SourceView s) {
    final c = s.capabilities;
    return <(String, bool)>[
      ('搜索', c.search),
      ('版本列表', c.listVersions),
      c.resolveDeps ? ('依赖树', true) : ('无依赖数据', false),
      c.officialDownloads ? ('官方下载量', true) : ('Stars 兜底', false),
      ('安装', c.install),
    ];
  }
}

/// 说明文字的圆点条目：4px 圆点 + 12 号 gray3 正文。
class _Bullet extends StatelessWidget {
  const _Bullet(this.text);

  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 7),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 6, right: 8),
            child: Container(
              width: 4,
              height: 4,
              decoration: const BoxDecoration(
                color: PclColors.gray4,
                shape: BoxShape.circle,
              ),
            ),
          ),
          Expanded(
            child: Text(
              text,
              style: PclTypography.captionStyle.copyWith(height: 1.5),
            ),
          ),
        ],
      ),
    );
  }
}
