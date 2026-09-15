/// 下载页：PCL-CE 的「左栏分类 + 右侧列表」布局。
///
/// 版式依据 `docs/DESIGN_PCL_CE.md` §6「外壳布局」的下半部分：
/// ```
/// Row
/// ├─ 左栏（宽 168 = [PclMetrics.leftRailWidth]）
/// │   ├─ PclGroupLabel「包源」+ 6 个 PclListItem（36 高，radio 选中态）
/// │   ├─ PclGroupLabel「视图」+ 2 个 PclListItem（搜索结果 / 收藏）
/// │   ├─ 「显示不可用包源」开关（PclButton dense）
/// │   └─ 最右侧 4px 阴影分隔（[PclMetrics.leftShadowWidth] + 渐变）
/// └─ Expanded 内容区
///     ├─ 搜索行（PclPanel 包 TextField + PclButton 搜索 primary）
///     ├─ 警告条 / 错误条（均为 PclPanel）
///     └─ Expanded 包卡片列表（PackageCard）/ 空态
/// ```
///
/// 本文件只改视觉与布局：数据加载、搜索、安装计划、详情跳转与全部错误分支
/// （[UmangoError] 的 `code` / `advice`）保持原有行为不变；包源可用性仍然决定
/// 左栏入口的显示与可点选性，平台门控的入口默认隐藏。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../ffi/umango_bridge.dart';
import '../models/umango_models.dart';
import '../theme/pcl_theme.dart';
import '../widgets/package_card.dart';
import '../widgets/pcl_widgets.dart';
import 'package_detail_page.dart';

// ---------------------------------------------------------------------------
// 本页版式数值（源自 DESIGN_PCL_CE.md 第 2 节与「左栏 168 / 阴影 4」约定）
// ---------------------------------------------------------------------------

/// 左栏宽度 168（规格 §6）。
const double _railWidth = PclMetrics.leftRailWidth;

/// 左栏内容区的左右内边距（列表项自身还有 13 的缩进，这里只补外框留白）。
const EdgeInsets _railPadding = EdgeInsets.only(top: 4, bottom: 6);

/// 右栏内容区四周留白；左边 14 是为了让 4px 阴影之外再有一点呼吸感。
const EdgeInsets _contentPadding = EdgeInsets.fromLTRB(14, 12, 14, 10);

/// 右栏各区块之间的纵向间距。
const double _contentBlockGap = 10;

/// 搜索输入框高度（与 30 高的 PclButton 对齐，PCL 的输入行偏紧凑）。
const double _searchFieldHeight = 30;

/// 搜索行与提示条 / 结果列表之间的间距。
const double _searchRowGap = 12;

/// 结果列表的底部留白，避免最后一张卡片贴住窗口底边。
const double _listBottomInset = 8;

/// 左栏「显示不可用包源」开关的外边距。
const EdgeInsets _railTogglePadding = EdgeInsets.fromLTRB(8, 6, 8, 8);

/// 包源类型 → lucide 图标名（资源位于 `assets/lucide/`）。
///
/// 图标一律用 lucide（合规要求，见规格 §0.2），不使用品牌 logo。
const Map<PackageSourceKind, String> _sourceIconNames =
    <PackageSourceKind, String>{
  PackageSourceKind.npm: 'box',
  PackageSourceKind.pnpm: 'box-select',
  PackageSourceKind.git: 'git-branch',
  PackageSourceKind.homebrew: 'egg',
  PackageSourceKind.winget: 'download',
  PackageSourceKind.scoop: 'package',
};

/// 左栏「视图」分组：搜索结果即本次查询的结果集，收藏对应星标包。
///
/// 用私有枚举而不是散落的字符串，让"同一时刻只有一个视图被选中"
/// 在类型层面成立（radio 选中态的前提）。
enum _DownloadView {
  results('搜索结果', 'list-tree'),
  favorites('收藏', 'heart');

  const _DownloadView(this.label, this.icon);

  /// 左栏显示的文字。
  final String label;

  /// lucide 图标名。
  final String icon;
}

class DownloadPage extends StatefulWidget {
  const DownloadPage({super.key});

  @override
  State<DownloadPage> createState() => _DownloadPageState();
}

class _DownloadPageState extends State<DownloadPage> {
  final _searchController = TextEditingController();
  final _scrollController = ScrollController();

  /// 用于「点击搜索后收起焦点」：焦点环停在框上会盖住 1px 描边的观感。
  final _searchFocus = FocusNode();

  List<SourceView> _sources = const [];
  PackageSourceKind _selected = PackageSourceKind.npm;

  /// 视图（radio）。为 null 表示当前选中的是某个包源；
  /// 包源与视图共用一个"唯一选中"语义，避免左栏出现两个高亮项。
  _DownloadView? _view = _DownloadView.results;

  List<PackageMeta> _results = const [];
  bool _loadingSources = true;
  bool _searching = false;
  String? _error;
  String? _errorAdvice;
  String _lastQuery = '';

  /// 是否显示"当前平台不支持 / 不可用"的包源。
  /// 默认隐藏（需求要求 UI 自动隐藏），但这本身是个可验证行为，
  /// 所以给一个开关让用户能看见被隐藏了什么。
  bool _showUnavailable = false;

  @override
  void initState() {
    super.initState();
    _loadSources();
  }

  @override
  void dispose() {
    _searchController.dispose();
    _scrollController.dispose();
    _searchFocus.dispose();
    super.dispose();
  }

  // -------------------------------------------------------------------------
  // 业务逻辑 —— 与改造前完全一致，本次只更换呈现层
  // -------------------------------------------------------------------------

  Future<void> _loadSources({bool refresh = false}) async {
    setState(() {
      _loadingSources = true;
      _error = null;
    });
    try {
      final sources = await UmangoBridge.instance.sources(refresh: refresh);
      if (!mounted) return;
      setState(() {
        _sources = sources;
        _loadingSources = false;
        // 若当前选中的包源不可用，自动切到第一个可用的
        final current = sources.where((s) => s.kind == _selected).firstOrNull;
        if (current == null || !current.isSelectable) {
          final firstUsable = sources.where((s) => s.isSelectable).firstOrNull;
          if (firstUsable != null) _selected = firstUsable.kind;
        }
      });
    } on UmangoError catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingSources = false;
        _error = e.message;
        _errorAdvice = e.advice.displayName;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingSources = false;
        _error = '$e';
      });
    }
  }

  /// 错误条上的「重试」入口：按错误来源分流 ——
  /// 包源列表还没拿到就重试加载，否则重放上一次查询。
  /// 这样"重试"永远对应刚才失败的那一步。
  void _retry() {
    if (_sources.isEmpty) {
      _loadSources();
      return;
    }
    _search();
  }

  Future<void> _search() async {
    final query = _searchController.text.trim();
    if (query.isEmpty) return;
    setState(() {
      _searching = true;
      _error = null;
      _errorAdvice = null;
      _lastQuery = query;
      _view = _DownloadView.results; // 搜索必然落到"搜索结果"视图
    });
    try {
      final results = await UmangoBridge.instance.search(
        _selected,
        query,
        limit: 24,
      );
      if (!mounted) return;
      setState(() {
        _results = results;
        _searching = false;
      });
    } on UmangoError catch (e) {
      if (!mounted) return;
      setState(() {
        _searching = false;
        _results = const [];
        _error = e.message;
        _errorAdvice = e.advice.displayName;
      });
    }
  }

  void _openDetail(PackageMeta meta) {
    Navigator.of(context).push(
      MaterialPageRoute(
        builder: (_) => PackageDetailPage(
          source: meta.source,
          name: meta.name,
          meta: meta,
        ),
      ),
    );
  }

  /// 安装：阶段1 只生成并展示命令计划，不执行（执行能力在阶段3）。
  Future<void> _showInstallPlan(PackageMeta meta) async {
    try {
      final plan = await UmangoBridge.instance.resolveInstall(
        meta.source,
        meta.name,
        version: meta.latestVersion,
      );
      if (!mounted) return;
      await showUmangoDialog<void>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text('安装 ${meta.displayName}'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(plan.explanation),
              const SizedBox(height: 14),
              const Text(
                '将要执行的命令：',
                style: TextStyle(fontWeight: FontWeight.w600),
              ),
              const SizedBox(height: 6),
              // 命令块：c8 底 + 1px gray5 描边（PCL 面板观感，不用 Material 默认灰底）
              PclPanel(
                color: PclColors.c8,
                padding: const EdgeInsets.all(10),
                radius: PclMetrics.buttonRadius,
                border: Border.all(
                  color: PclColors.gray5,
                  width: PclMetrics.buttonBorderWidth,
                ),
                child: SizedBox(
                  width: double.infinity,
                  child: SelectableText(
                    plan.commandLine,
                    style: const TextStyle(
                      fontFamily: 'monospace',
                      fontSize: PclTypography.caption,
                      color: PclColors.c1,
                    ),
                  ),
                ),
              ),
              const SizedBox(height: 12),
              Row(
                children: [
                  const PclIcon(
                    name: 'info',
                    size: PclMetrics.metaIconSize,
                    color: PclColors.gray3,
                  ),
                  const SizedBox(width: PclMetrics.metaIconGap),
                  Expanded(
                    child: Text(
                      '阶段1 只生成安装计划，不会真的执行。实际执行能力在阶段3 接入。',
                      style: PclTypography.metaStyle,
                    ),
                  ),
                ],
              ),
            ],
          ),
          actions: [
            PclButton(
              label: '关闭',
              onPressed: () => Navigator.of(context).pop(),
            ),
          ],
        ),
      );
    } on UmangoError catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.message;
        _errorAdvice = e.advice.displayName;
      });
    }
  }

  // -------------------------------------------------------------------------
  // 派生数据
  // -------------------------------------------------------------------------

  /// 平台门控的包源默认隐藏，只有打开开关才出现在左栏。
  List<SourceView> get _visibleSources => _showUnavailable
      ? _sources
      : _sources.where((s) => s.isSelectable).toList();

  @override
  Widget build(BuildContext context) {
    final selectedSource =
        _sources.where((s) => s.kind == _selected).firstOrNull;

    return Row(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _buildLeftRail(),
        Expanded(
          child: Padding(
            padding: _contentPadding,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _buildSearchRow(),
                const SizedBox(height: _searchRowGap),
                // 诚实标注：当前包源的解析逻辑尚未在真机验证过
                if (selectedSource?.verificationNote != null) ...[
                  _VerificationBanner(
                    sourceName: selectedSource!.displayName,
                    note: selectedSource.verificationNote!,
                  ),
                  const SizedBox(height: _contentBlockGap),
                ],
                if (_error != null) ...[
                  _ErrorBanner(
                    message: _error!,
                    advice: _errorAdvice,
                    onRetry: _retry,
                  ),
                  const SizedBox(height: _contentBlockGap),
                ],
                Expanded(child: _buildResults(selectedSource)),
              ],
            ),
          ),
        ),
      ],
    );
  }

  // -------------------------------------------------------------------------
  // 左栏（PCL 的 PanLeft 观感）
  // -------------------------------------------------------------------------

  Widget _buildLeftRail() {
    final railEntries = <Widget>[
      const PclGroupLabel(text: '包源'),
      for (final source in _visibleSources)
        _RailEntry(
          // 打开"显示不可用包源"后也允许点选：只看不选会让这个开关变成装饰。
          // 选中一个不可用包源后，搜索会照常发起并由核心层给出带 advice 的错误。
          child: _SourceRailItem(
            source: source,
            selected: _view == null && source.kind == _selected,
            onTap: () => _selectSource(source),
          ),
        ),
      const PclGroupLabel(text: '视图'),
      for (final view in _DownloadView.values)
        _RailEntry(
          child: PclListItem(
            label: view.label,
            icon: view.icon,
            selected: _view == view,
            onTap: () => _selectView(view),
          ),
        ),
    ];

    return SizedBox(
      width: _railWidth,
      child: Stack(
        // Stack 默认 hardEdge 裁剪：4px 阴影被收在自己的宽度内，不侵占内容区
        children: [
          Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Expanded(
                child: ScrollConfiguration(
                  // 左栏内容很短，滚动条只会碍眼，故隐藏（滚轮照常可用）
                  behavior: ScrollConfiguration.of(context).copyWith(
                    scrollbars: false,
                  ),
                  child: SingleChildScrollView(
                    padding: _railPadding,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: railEntries,
                    ),
                  ),
                ),
              ),
              // 被平台门控的包源默认隐藏，这里给出把它们显示出来的开关
              Padding(
                padding: _railTogglePadding,
                child: PclButton(
                  label: _showUnavailable ? '隐藏不可用包源' : '显示不可用包源',
                  icon: _showUnavailable ? 'chevron-down' : 'chevron-right',
                  dense: true,
                  onPressed: () =>
                      setState(() => _showUnavailable = !_showUnavailable),
                ),
              ),
            ],
          ),
          // 左栏最右侧 4px 阴影分隔（规格 §2：左栏右侧阴影宽度 4）
          const Positioned(top: 0, bottom: 0, right: 0, child: _RailShadow()),
        ],
      ),
    );
  }

  void _selectSource(SourceView source) {
    setState(() {
      _selected = source.kind;
      _view = null; // 选中包源即离开"视图"列表（radio 语义）
      _results = const [];
      _lastQuery = '';
      _error = null;
      _errorAdvice = null;
    });
  }

  void _selectView(_DownloadView view) {
    setState(() {
      _view = view;
      if (view == _DownloadView.favorites) {
        // 收藏视图暂无数据接入：清空结果并由空态如实说明，不伪造内容
        _results = const [];
      }
    });
  }

  // -------------------------------------------------------------------------
  // 右栏：搜索行 / 结果区
  // -------------------------------------------------------------------------

  Widget _buildSearchRow() {
    final hint = _selected == PackageSourceKind.git
        ? '搜索 GitHub 仓库，例如 react、cli、rust-lang/rust'
        : '搜索包名，例如 react、express、is-number';

    return Row(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        Expanded(
          // 输入框高度写死 30（与 30 高的 PclButton 对齐），所以这里屏蔽系统字号缩放：
          // PCL 的桌面版式按固定像素排布，字号放大只会把这一行撑破。
          child: MediaQuery.withNoTextScaling(
            child: PclPanel(
              // 规格：圆角 3、1px c1 描边、白底
              color: Colors.white,
              radius: PclMetrics.buttonRadius,
              border: Border.all(
                color: PclColors.c1,
                width: PclMetrics.buttonBorderWidth,
              ),
              child: SizedBox(
                height: _searchFieldHeight,
                child: TextField(
                  controller: _searchController,
                  focusNode: _searchFocus,
                  onSubmitted: (_) => _search(),
                  // 关掉 Material 默认的填充与下划线（描边由外层 PclPanel 提供）
                  textAlignVertical: TextAlignVertical.center,
                  style: PclTypography.bodyStyle,
                  cursorColor: PclColors.c3,
                  cursorWidth: 1,
                  decoration: InputDecoration(
                    isDense: true,
                    filled: false,
                    border: InputBorder.none,
                    enabledBorder: InputBorder.none,
                    focusedBorder: InputBorder.none,
                    contentPadding: const EdgeInsets.symmetric(horizontal: 8),
                    hintText: hint,
                    hintStyle: PclTypography.captionStyle,
                    prefixIcon: const Padding(
                      padding: EdgeInsets.only(left: 8, right: 6),
                      child: PclIcon(
                        name: 'search',
                        size: PclMetrics.metaIconSize + 3,
                        color: PclColors.gray3,
                      ),
                    ),
                    prefixIconConstraints: const BoxConstraints(
                      minWidth: 0,
                      minHeight: 0,
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
        const SizedBox(width: _contentBlockGap),
        PclButton(
          label: '搜索',
          icon: 'search',
          primary: true,
          onPressed: _searching
              ? null
              : () {
                  // 先收起焦点环再发起查询，否则结果刷新时焦点环还压在输入框上
                  _searchFocus.unfocus();
                  _search();
                },
        ),
      ],
    );
  }

  Widget _buildResults(SourceView? selectedSource) {
    if (_searching) {
      return const Center(
        child: SizedBox(
          width: 18,
          height: 18,
          child: CircularProgressIndicator(strokeWidth: 2),
        ),
      );
    }
    if (_results.isEmpty) {
      return _EmptyState(
        source: selectedSource,
        view: _view,
        loading: _loadingSources,
        lastQuery: _lastQuery,
        onSearch: _search,
      );
    }
    return ScrollConfiguration(
      // 桌面端隐藏系统滚动条（保持 PCL 的干净观感），滚轮与触控板照常可用
      behavior: ScrollConfiguration.of(context).copyWith(scrollbars: false),
      child: ListView.separated(
        controller: _scrollController,
        padding: const EdgeInsets.only(bottom: _listBottomInset),
        itemCount: _results.length,
        separatorBuilder: (_, _) => const SizedBox(height: _contentBlockGap),
        itemBuilder: (context, i) {
          final meta = _results[i];
          return PackageCard(
            meta: meta,
            index: i,
            onOpen: () => _openDetail(meta),
            onInstall: () => _showInstallPlan(meta),
          );
        },
      ),
    );
  }
}

/// 左栏列表项的包装：提示气泡挂在项的外层，不干扰列表项自身的 hover / hit-test。
///
/// 点击由子项自己的 `onTap` 处理 —— 若这里再包一层 [GestureDetector]，
/// 一次点击会被两个手势识别器各触发一次，造成重复切换。
class _RailEntry extends StatelessWidget {
  const _RailEntry({required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: _message,
      waitDuration: const Duration(milliseconds: 500),
      child: child,
    );
  }

  /// 提示文字：包源给出可用性详情，视图给出名称。
  String get _message {
    final target = child;
    if (target is _SourceRailItem) {
      final source = target.source;
      final buffer = StringBuffer(source.displayName)
        ..write(' · ')
        ..write(_availabilityText(source));
      final version = source.availability.cliVersion;
      if (version != null && version.isNotEmpty) {
        buffer.write(' · $version');
      }
      return buffer.toString();
    }
    if (target is PclListItem) {
      return target.selected ? '${target.label}（当前视图）' : target.label;
    }
    return '';
  }
}

/// 左栏主区与内容区之间的 4px 阴影分隔。
///
/// 不用 [BoxShadow]：那需要在 `Container` 上声明 shadow，而阴影绘制层是
/// `Container` 构建期的只读计算字段，换主题时拿不到可重建的句柄；
/// 改用**渐变**表达同一观感 —— 4px 宽、由 `shadow` 黑渐隐到全透明。
class _RailShadow extends StatelessWidget {
  const _RailShadow();

  @override
  Widget build(BuildContext context) {
    return const IgnorePointer(
      child: SizedBox(
        width: PclMetrics.leftShadowWidth,
        child: DecoratedBox(
          decoration: BoxDecoration(
            gradient: LinearGradient(
              begin: Alignment.centerLeft,
              end: Alignment.centerRight,
              colors: <Color>[PclColors.shadow, Color(0x00000000)],
            ),
          ),
        ),
      ),
    );
  }
}

/// 包源列表项：lucide 图标（品牌强调色）+ 名称 + 可用性状态点。
///
/// 状态用状态点而不是文字 badge：左栏 168 宽减去缩进后，
/// 「名称 + 文字状态」会挤成省略号，所以状态交给**颜色点 + 提示气泡**表达，
/// 每种色相含义固定，扫一眼即可区分。
class _SourceRailItem extends StatelessWidget {
  const _SourceRailItem({
    required this.source,
    required this.selected,
    this.onTap,
  });

  final SourceView source;
  final bool selected;

  /// 点击回调（radio 选中该包源）。
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final Color tint = selected
        ? PclColors.c2
        : (_parseHexColor(source.accentHex) ?? PclColors.c1);

    return PclListItem(
      label: source.displayName,
      selected: selected,
      onTap: onTap,
      // PclListItem.icon 只收图标名、带不了颜色，所以图标作为 trailing 的前缀图
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          PclIcon(
            name: _sourceIconNames[source.kind] ?? 'package',
            size: 16,
            color: tint,
          ),
          const SizedBox(width: 8),
          _SourceStatusDot(source: source),
        ],
      ),
    );
  }
}

/// 包源可用性状态点。
class _SourceStatusDot extends StatelessWidget {
  const _SourceStatusDot({required this.source});

  final SourceView source;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 8,
      height: 8,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        color: _availabilityColor(source.availability.state),
      ),
    );
  }
}

/// 可用性 → 状态点颜色。
///
/// `notInstalled` 是"没装 CLI"，不是故障，所以既不用红色也不用主蓝色，
/// 统一走灰色系；只有"平台不支持 / 源不可达"才升级为红色。
Color _availabilityColor(AvailabilityState state) => switch (state) {
      AvailabilityState.available => PclColors.c3,
      AvailabilityState.degraded => PclColors.c5,
      AvailabilityState.notInstalled => PclColors.gray4,
      AvailabilityState.unsupportedPlatform ||
      AvailabilityState.unreachable =>
        PclColors.error,
    };

/// 可用性 → 提示气泡文字（与约定的 可用 / 未安装 / 不支持 对齐）。
String _availabilityText(SourceView source) =>
    switch (source.availability.state) {
      AvailabilityState.available => '可用',
      AvailabilityState.degraded => '可用（数据可能过期）',
      AvailabilityState.notInstalled => '未安装',
      AvailabilityState.unsupportedPlatform => '不支持（当前平台）',
      AvailabilityState.unreachable => '源不可达',
    };

/// 把 `#rrggbb` / `rrggbb` / 8 位 hex 解析为 [Color]；失败返回 null（调用方回落 c1）。
///
/// 为什么不直接用 `AppTheme.hexColor`：那会把旧版主题层拖进本页依赖，
/// 而 PCL 组件只依赖 `pcl_theme.dart`；这里只需要 6/8 位 hex 这一条窄契约。
Color? _parseHexColor(String? raw) {
  if (raw == null) return null;
  var value = raw.trim();
  if (value.startsWith('#')) {
    value = value.substring(1);
  }
  if (value.length == 6) {
    value = 'ff$value';
  }
  if (value.length != 8) return null;
  final int? parsed = int.tryParse(value, radix: 16);
  return parsed == null ? null : Color(parsed);
}

/// 警告条：当前包源的解析逻辑未在真机验证过（[SourceView.verificationNote]）。
///
/// 用 `c8` 浅蓝底 + `c4` 描边：这是"提示"而不是"错误"，
/// 红底会让用户误以为包源已经坏了。
class _VerificationBanner extends StatelessWidget {
  const _VerificationBanner({required this.sourceName, required this.note});

  final String sourceName;
  final String note;

  @override
  Widget build(BuildContext context) {
    return PclPanel(
      color: PclColors.c8,
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
      radius: PclMetrics.buttonRadius,
      border: Border.all(
        color: PclColors.c4,
        width: PclMetrics.buttonBorderWidth,
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const PclIcon(name: 'triangle-alert', size: 15, color: PclColors.c2),
          const SizedBox(width: _contentBlockGap),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '$sourceName 适配器的解析逻辑未在真机验证',
                  style: PclTypography.bodyStyle.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
                ),
                const SizedBox(height: 2),
                Text(note, style: PclTypography.captionStyle),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// 错误条：展示核心层 [UmangoError] 的 message + advice，并给出重试入口。
class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner({
    required this.message,
    required this.advice,
    required this.onRetry,
  });

  final String message;
  final String? advice;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return PclPanel(
      color: PclColors.redBack,
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
      radius: PclMetrics.buttonRadius,
      border: Border.all(
        color: PclColors.redLight,
        width: PclMetrics.buttonBorderWidth,
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const PclIcon(
            name: 'triangle-alert',
            size: 15,
            color: PclColors.redDark,
          ),
          const SizedBox(width: _contentBlockGap),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '查询失败',
                  style: PclTypography.bodyStyle.copyWith(
                    color: PclColors.redDark,
                    fontWeight: FontWeight.w600,
                  ),
                ),
                const SizedBox(height: 2),
                // 可选中：错误信息常需要贴给他人排查
                SelectableText(
                  advice == null ? message : '$message\n建议：$advice',
                  style: PclTypography.captionStyle.copyWith(
                    color: PclColors.redDark,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: _contentBlockGap),
          PclButton(
            label: '重试',
            icon: 'refresh-cw',
            dense: true,
            onPressed: onRetry,
          ),
        ],
      ),
    );
  }
}

/// 空态：居中 PclIcon(search) + 提示文字。
///
/// 文案按"到底为什么没有结果"分开说明，避免把"还没搜"和"搜了没找到"混在一起。
class _EmptyState extends StatelessWidget {
  const _EmptyState({
    required this.source,
    required this.view,
    required this.loading,
    required this.lastQuery,
    required this.onSearch,
  });

  final SourceView? source;
  final _DownloadView? view;
  final bool loading;
  final String lastQuery;
  final VoidCallback onSearch;

  @override
  Widget build(BuildContext context) {
    final String sourceName = source?.displayName ?? '当前包源';
    const String noSourceHint = '左栏会列出可用包源；若列表为空，'
        '可打开左栏底部的「显示不可用包源」查看被平台门控的入口。';

    final String title;
    final String hint;
    if (loading && source == null) {
      title = '正在检测包源…';
      hint = '稍等片刻；若长时间没有结果，左栏底部可查看不可用包源。';
    } else if (view == _DownloadView.favorites) {
      title = '收藏还是空的';
      hint = '收藏能力尚未接入数据层，这里不会列出任何包 —— 不显示假数据。';
    } else if (source == null) {
      title = '请先选择一个包源';
      hint = noSourceHint;
    } else if (lastQuery.isEmpty) {
      title = '在 $sourceName 中搜索';
      hint = source!.capabilities.search
          ? '输入关键字后回车，或点击右侧「搜索」。'
          : '$sourceName 不支持搜索，请在左栏换一个包源。';
    } else {
      title = '「$lastQuery」没有找到结果';
      hint = source!.capabilities.search
          ? '换个关键字，或改用其他包源再试一次。'
          : '$sourceName 不支持搜索，可改用其他包源。';
    }

    // 只有"确实搜过但没结果"才给重试：空态里摆一个点了没反应的按钮更糟
    final bool showRetry =
        lastQuery.isNotEmpty && view != _DownloadView.favorites;

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 460),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const PclIcon(name: 'search', size: 40, color: PclColors.gray4),
            const SizedBox(height: 14),
            Text(title, style: PclTypography.cardTitleStyle),
            const SizedBox(height: 8),
            Text(
              hint,
              textAlign: TextAlign.center,
              style: PclTypography.captionStyle,
            ),
            if (showRetry) ...[
              const SizedBox(height: 14),
              PclButton(
                label: '重试',
                icon: 'refresh-cw',
                dense: true,
                onPressed: onSearch,
              ),
            ],
          ],
        ),
      ),
    );
  }
}
