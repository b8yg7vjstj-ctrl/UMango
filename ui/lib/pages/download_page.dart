/// 下载页：包源列表 + 搜索 + 包卡片。
///
/// 对应需求「3.1 包源与下载卡片」：
///   · 保留下载卡片 UI，把原 Mod 源列表改为包管理器源列表；
///   · 源列表为 npm / git / scoop / winget / pnpm（+ Homebrew）；
///   · 平台不支持的包源由核心层标记，界面据此隐藏。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../ffi/umango_bridge.dart';
import '../models/umango_models.dart';
import '../widgets/package_card.dart';
import 'package_detail_page.dart';

class DownloadPage extends StatefulWidget {
  const DownloadPage({super.key});

  @override
  State<DownloadPage> createState() => _DownloadPageState();
}

class _DownloadPageState extends State<DownloadPage> {
  final _searchController = TextEditingController();
  final _scrollController = ScrollController();

  List<SourceView> _sources = const [];
  PackageSourceKind _selected = PackageSourceKind.npm;
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
    super.dispose();
  }

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

  Future<void> _search() async {
    final query = _searchController.text.trim();
    if (query.isEmpty) return;
    setState(() {
      _searching = true;
      _error = null;
      _lastQuery = query;
    });
    try {
      final results = await UmangoBridge.instance.search(_selected, query, limit: 24);
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
    Navigator.of(context).push(MaterialPageRoute(
      builder: (_) => PackageDetailPage(source: meta.source, name: meta.name, meta: meta),
    ));
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
              const Text('将要执行的命令：', style: TextStyle(fontWeight: FontWeight.w600)),
              const SizedBox(height: 6),
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: Theme.of(context).colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(6),
                ),
                child: SelectableText(
                  plan.commandLine,
                  style: const TextStyle(fontFamily: 'monospace', fontSize: 12),
                ),
              ),
              const SizedBox(height: 12),
              Row(
                children: [
                  Icon(Icons.info_outline,
                      size: 15, color: Theme.of(context).colorScheme.outline),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      '阶段1 只生成安装计划，不会真的执行。实际执行能力在阶段3 接入。',
                      style: TextStyle(
                        fontSize: 12,
                        color: Theme.of(context).colorScheme.outline,
                      ),
                    ),
                  ),
                ],
              ),
            ],
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('关闭'),
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

  List<SourceView> get _visibleSources => _showUnavailable
      ? _sources
      : _sources.where((s) => s.isSelectable).toList();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final selectedSource = _sources.where((s) => s.kind == _selected).firstOrNull;

    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 0),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // ---------------- 左：包源列表 ----------------
          SizedBox(
            width: 244,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Text('包源', style: theme.textTheme.titleSmall),
                    const Spacer(),
                    Tooltip(
                      message: '重新检测所有包源的可用性',
                      child: IconButton(
                        onPressed: _loadingSources ? null : () => _loadSources(refresh: true),
                        iconSize: 17,
                        visualDensity: VisualDensity.compact,
                        icon: const Icon(Icons.refresh),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 4),
                Expanded(
                  child: _loadingSources
                      ? const Center(child: CircularProgressIndicator(strokeWidth: 2))
                      : ListView.separated(
                          itemCount: _visibleSources.length,
                          separatorBuilder: (_, __) => const SizedBox(height: 7),
                          itemBuilder: (context, i) {
                            final source = _visibleSources[i];
                            return SourceCard(
                              source: source,
                              index: i,
                              selected: source.kind == _selected,
                              onTap: () => setState(() {
                                _selected = source.kind;
                                _results = const [];
                              }),
                            );
                          },
                        ),
                ),
                const SizedBox(height: 6),
                Row(
                  children: [
                    SizedBox(
                      height: 26,
                      width: 34,
                      child: Switch(
                        value: _showUnavailable,
                        onChanged: (v) => setState(() => _showUnavailable = v),
                      ),
                    ),
                    const SizedBox(width: 6),
                    Expanded(
                      child: Text(
                        '显示不可用包源',
                        style: TextStyle(fontSize: 11.5, color: theme.colorScheme.outline),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          const SizedBox(width: 18),
          // ---------------- 右：搜索与结果 ----------------
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: _searchController,
                        onSubmitted: (_) => _search(),
                        decoration: InputDecoration(
                          hintText: _selected == PackageSourceKind.git
                              ? '搜索 GitHub 仓库，例如 react、cli、rust-lang/rust'
                              : '搜索包名，例如 react、express、is-number',
                          prefixIcon: const Icon(Icons.search, size: 19),
                        ),
                      ),
                    ),
                    const SizedBox(width: 10),
                    FilledButton(
                      onPressed: _searching ? null : _search,
                      child: _searching
                          ? const SizedBox(
                              width: 15,
                              height: 15,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Text('搜索'),
                    ),
                  ],
                ),
                const SizedBox(height: 14),
                if (selectedSource?.verificationNote != null)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 10),
                    child: StatusBanner(
                      icon: Icons.science_outlined,
                      title: '${selectedSource!.displayName} 适配器的解析逻辑未在真机验证',
                      message: selectedSource.verificationNote,
                      tone: StatusTone.warning,
                    ),
                  ),
                if (_error != null)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 10),
                    child: StatusBanner(
                      icon: Icons.error_outline,
                      title: '查询失败',
                      message: '$_error${_errorAdvice == null ? '' : '\n建议：$_errorAdvice'}',
                      tone: StatusTone.error,
                      action: TextButton(
                        onPressed: _search,
                        child: const Text('重试'),
                      ),
                    ),
                  ),
                Expanded(
                  child: _results.isEmpty
                      ? _EmptyState(
                          source: selectedSource,
                          lastQuery: _lastQuery,
                          onSearch: _search,
                        )
                      : ListView.separated(
                          controller: _scrollController,
                          padding: const EdgeInsets.only(bottom: 20),
                          itemCount: _results.length,
                          separatorBuilder: (_, __) => const SizedBox(height: 10),
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
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _EmptyState extends StatelessWidget {
  const _EmptyState({required this.source, required this.lastQuery, required this.onSearch});

  final SourceView? source;
  final String lastQuery;
  final VoidCallback onSearch;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final sourceName = source?.displayName ?? '当前包源';

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 460),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.inventory_2_outlined, size: 40, color: theme.colorScheme.outline),
            const SizedBox(height: 14),
            Text(
              lastQuery.isEmpty ? '输入关键字开始搜索' : '「$lastQuery」没有找到结果',
              style: theme.textTheme.titleSmall,
            ),
            const SizedBox(height: 8),
            Text(
              source == null
                  ? '请先在左侧选择包源。'
                  : source!.capabilities.search
                      ? '将在 $sourceName 中检索包，点击卡片可查看版本列表与依赖树。'
                      : '$sourceName 不支持搜索，可改用其他包源。',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
            ),
            if (lastQuery.isNotEmpty) ...[
              const SizedBox(height: 14),
              OutlinedButton(onPressed: onSearch, child: const Text('重试')),
            ],
          ],
        ),
      ),
    );
  }
}
