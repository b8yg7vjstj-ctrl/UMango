/// 包详情页：版本列表 + 依赖关系树。
///
/// 对应需求「3.2 版本与依赖管理」：
///   · 版本列表区分快照/测试版/正式版（通道判定在 Rust 侧完成）；
///   · 依赖树可视化，依赖项可点击：直接安装 或 跳转版本详情页；
///   · 依赖故障（网络异常、源访问失败）有兜底呈现。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../ffi/umango_bridge.dart';
import '../models/umango_models.dart';
import '../theme/app_theme.dart';
import '../widgets/dependency_tree.dart';
import '../widgets/package_card.dart';
import '../widgets/source_icon.dart';

class PackageDetailPage extends StatefulWidget {
  const PackageDetailPage({
    super.key,
    required this.source,
    required this.name,
    this.meta,
    this.presetVersion,
  });

  final PackageSourceKind source;
  final String name;

  /// 从搜索结果带过来的元数据（可能为空，例如从依赖树跳进来时）
  final PackageMeta? meta;

  /// 指定要高亮的版本（从依赖节点跳转时带上）
  final String? presetVersion;

  @override
  State<PackageDetailPage> createState() => _PackageDetailPageState();
}

class _PackageDetailPageState extends State<PackageDetailPage>
    with SingleTickerProviderStateMixin {
  late final TabController _tabController;

  List<PackageVersion> _versions = const [];
  DependencyTree? _tree;
  bool _loadingVersions = true;
  bool _loadingTree = false;
  String? _versionError;
  String? _treeError;

  /// 版本列表通道筛选
  ReleaseChannel? _channelFilter;

  /// 依赖树参数
  int _maxDepth = 3;
  bool _includeOptional = false;

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: 2, vsync: this);
    _loadVersions();
    _loadTree();
  }

  @override
  void dispose() {
    _tabController.dispose();
    super.dispose();
  }

  String get _cacheKey => '${widget.source.id}:${widget.name}';

  Future<void> _loadVersions() async {
    setState(() {
      _loadingVersions = true;
      _versionError = null;
    });
    try {
      final versions = await UmangoBridge.instance.versions(widget.source, widget.name);
      if (!mounted) return;
      setState(() {
        _versions = versions;
        _loadingVersions = false;
      });
    } on UmangoError catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingVersions = false;
        _versionError = '${e.message}\n建议：${e.advice.displayName}';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingVersions = false;
        _versionError = '$e';
      });
    }
  }

  Future<void> _loadTree() async {
    setState(() {
      _loadingTree = true;
      _treeError = null;
    });
    try {
      final tree = await UmangoBridge.instance.dependencies(
        widget.source,
        widget.name,
        version: widget.presetVersion,
        maxDepth: _maxDepth,
        includeOptionalKinds: _includeOptional,
      );
      if (!mounted) return;
      setState(() {
        _tree = tree;
        _loadingTree = false;
      });
    } on UmangoError catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingTree = false;
        _treeError = '${e.message}\n建议：${e.advice.displayName}';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingTree = false;
        _treeError = '$e';
      });
    }
  }

  Future<void> _showInstallPlan(String name, String? version) async {
    try {
      final plan = await UmangoBridge.instance
          .resolveInstall(widget.source, name, version: version);
      if (!mounted) return;
      await showUmangoDialog<void>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text('安装 $name'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(plan.explanation),
              const SizedBox(height: 12),
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
              const SizedBox(height: 10),
              Text(
                '阶段1 只展示命令计划，不会执行。',
                style: TextStyle(fontSize: 12, color: Theme.of(context).colorScheme.outline),
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
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('无法生成安装计划：${e.message}')),
      );
    }
  }

  void _openDependency(DependencyNode node) {
    Navigator.of(context).push(MaterialPageRoute(
      builder: (_) => PackageDetailPage(
        source: node.source,
        name: node.name,
        presetVersion: node.resolved,
      ),
    ));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final meta = widget.meta;

    return Scaffold(
      backgroundColor: Colors.transparent,
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // ---------------- 头部 ----------------
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 0),
            child: Row(
              children: [
                IconButton(
                  onPressed: () => Navigator.of(context).maybePop(),
                  icon: const Icon(Icons.arrow_back, size: 19),
                  visualDensity: VisualDensity.compact,
                  tooltip: '返回',
                ),
                const SizedBox(width: 4),
                SourceIcon(kind: widget.source, size: 38),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Flexible(
                            child: Text(
                              meta?.displayName ?? widget.name,
                              style: theme.textTheme.titleMedium
                                  ?.copyWith(fontWeight: FontWeight.w700),
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                          const SizedBox(width: 8),
                          if (meta?.latestVersion != null) ...[
                            Text(meta!.latestVersion!,
                                style: TextStyle(
                                  color: theme.colorScheme.primary,
                                  fontWeight: FontWeight.w600,
                                )),
                            const SizedBox(width: 6),
                            ChannelChip(channel: meta.latestChannel, compact: true),
                          ],
                        ],
                      ),
                      const SizedBox(height: 3),
                      Text(
                        '${widget.source.displayName} · ${widget.name}'
                        '${meta?.summary == null ? '' : ' · ${meta!.summary}'}',
                        style: TextStyle(
                          fontSize: 12,
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ],
                  ),
                ),
                if (meta != null) DownloadBadge(stats: meta.downloads),
                const SizedBox(width: 12),
                FilledButton.icon(
                  onPressed: () => _showInstallPlan(widget.name, widget.presetVersion),
                  icon: const Icon(Icons.download_outlined, size: 17),
                  label: const Text('安装'),
                ),
              ],
            ),
          ),
          const SizedBox(height: 10),
          // ---------------- Tab ----------------
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20),
            child: TabBar(
              controller: _tabController,
              isScrollable: true,
              tabAlignment: TabAlignment.start,
              tabs: [
                Tab(text: '版本列表${_versions.isEmpty ? '' : ' (${_versions.length})'}'),
                Tab(
                  text: '依赖树'
                      '${_tree == null ? '' : ' (${_tree!.summary.total})'}',
                ),
              ],
            ),
          ),
          const Divider(height: 1),
          Expanded(
            child: TabBarView(
              controller: _tabController,
              children: [
                _buildVersionTab(theme),
                _buildTreeTab(theme),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildVersionTab(ThemeData theme) {
    if (_loadingVersions) {
      return const Center(child: CircularProgressIndicator(strokeWidth: 2));
    }
    if (_versionError != null) {
      return Padding(
        padding: const EdgeInsets.all(20),
        child: StatusBanner(
          icon: Icons.error_outline,
          title: '无法获取版本列表',
          message: _versionError,
          tone: StatusTone.error,
          action: TextButton(onPressed: _loadVersions, child: const Text('重试')),
        ),
      );
    }
    if (_versions.isEmpty) {
      return const Center(child: Text('该包没有可用版本'));
    }

    // 通道统计
    final counts = <ReleaseChannel, int>{};
    for (final v in _versions) {
      counts[v.channel] = (counts[v.channel] ?? 0) + 1;
    }

    final filtered = _channelFilter == null
        ? _versions
        : _versions.where((v) => v.channel == _channelFilter).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 12, 20, 8),
          child: Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              FilterChip(
                label: Text('全部 (${_versions.length})'),
                selected: _channelFilter == null,
                onSelected: (_) => setState(() => _channelFilter = null),
              ),
              for (final channel in ReleaseChannel.values)
                if ((counts[channel] ?? 0) > 0)
                  FilterChip(
                    label: Text('${channel.displayName} (${counts[channel]})'),
                    selected: _channelFilter == channel,
                    onSelected: (_) => setState(() => _channelFilter = channel),
                  ),
            ],
          ),
        ),
        Expanded(
          child: ListView.separated(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 24),
            itemCount: filtered.length,
            separatorBuilder: (_, __) => const SizedBox(height: 6),
            itemBuilder: (context, i) {
              final version = filtered[i];
              final isPreset = widget.presetVersion != null &&
                  (version.version == widget.presetVersion ||
                      version.version.contains(widget.presetVersion!));
              return ListEntrance(
                key: ValueKey('${_cacheKey}-${version.version}'),
                index: i,
                axisKey: _channelFilter,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
                  decoration: BoxDecoration(
                    color: isPreset
                        ? theme.colorScheme.primaryContainer.withValues(alpha: 0.35)
                        : theme.cardColor,
                    borderRadius: BorderRadius.circular(8),
                    border: Border.all(
                      color: isPreset ? theme.colorScheme.primary : theme.dividerColor,
                    ),
                  ),
                  child: Row(
                    children: [
                      SizedBox(
                        width: 168,
                        child: Text(
                          version.version,
                          style: TextStyle(
                            fontFamily: 'monospace',
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                            color: channelColor(version.channel, context),
                          ),
                        ),
                      ),
                      ChannelChip(channel: version.channel, compact: true),
                      const SizedBox(width: 10),
                      if (version.deprecated)
                        Container(
                          margin: const EdgeInsets.only(right: 8),
                          padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 2),
                          decoration: BoxDecoration(
                            color: theme.colorScheme.errorContainer,
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            '已弃用',
                            style: TextStyle(
                              fontSize: 10.5,
                              color: theme.colorScheme.onErrorContainer,
                            ),
                          ),
                        ),
                      Expanded(
                        child: Text(
                          version.note ??
                              (version.publishedAt == null
                                  ? ''
                                  : '发布于 ${humanizeIso(version.publishedAt!)}'),
                          style: TextStyle(
                            fontSize: 11.5,
                            color: theme.colorScheme.outline,
                          ),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                      TextButton.icon(
                        onPressed: () => _showInstallPlan(widget.name, version.version),
                        icon: const Icon(Icons.download_outlined, size: 15),
                        label: const Text('安装此版本'),
                      ),
                    ],
                  ),
                ),
              );
            },
          ),
        ),
      ],
    );
  }

  Widget _buildTreeTab(ThemeData theme) {
    return Column(
      children: [
        // 依赖树参数控制
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 12, 20, 8),
          child: Row(
            children: [
              Text('递归深度', style: theme.textTheme.bodySmall),
              const SizedBox(width: 8),
              SizedBox(
                width: 120,
                child: Slider(
                  value: _maxDepth.toDouble(),
                  min: 1,
                  max: 6,
                  divisions: 5,
                  label: '$_maxDepth 层',
                  onChanged: _loadingTree
                      ? null
                      : (v) => setState(() => _maxDepth = v.round()),
                  onChangeEnd: (_) => _loadTree(),
                ),
              ),
              Text('$_maxDepth 层', style: theme.textTheme.bodySmall),
              const SizedBox(width: 16),
              FilterChip(
                label: const Text('含 dev / peer / optional 依赖'),
                selected: _includeOptional,
                onSelected: _loadingTree
                    ? null
                    : (v) {
                        setState(() => _includeOptional = v);
                        _loadTree();
                      },
              ),
              const Spacer(),
              IconButton(
                onPressed: _loadingTree ? null : _loadTree,
                iconSize: 18,
                visualDensity: VisualDensity.compact,
                tooltip: '重新解析依赖树',
                icon: const Icon(Icons.refresh),
              ),
            ],
          ),
        ),
        const Divider(height: 1),
        Expanded(
          child: _loadingTree
              ? const Center(child: CircularProgressIndicator(strokeWidth: 2))
              : _treeError != null
                  ? Padding(
                      padding: const EdgeInsets.all(20),
                      child: StatusBanner(
                        icon: Icons.error_outline,
                        title: '依赖树解析失败',
                        message: _treeError,
                        tone: StatusTone.error,
                        action: TextButton(onPressed: _loadTree, child: const Text('重试')),
                      ),
                    )
                  : _tree == null
                      ? const Center(child: Text('暂无依赖数据'))
                      : SingleChildScrollView(
                          padding: const EdgeInsets.fromLTRB(20, 14, 20, 24),
                          child: DependencyTreeView(
                            tree: _tree!,
                            onInstallDependency: (node) =>
                                _showInstallPlan(node.name, node.resolved),
                            onOpenDependency: _openDependency,
                          ),
                        ),
        ),
      ],
    );
  }
}
