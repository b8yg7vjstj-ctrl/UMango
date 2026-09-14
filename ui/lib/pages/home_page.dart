/// 主页：平台概览与包源能力矩阵。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../ffi/umango_bridge.dart';
import '../models/umango_models.dart';

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
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final info = _info;

    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
      children: [
        if (_error != null)
          StatusBanner(
            icon: Icons.error_outline,
            title: '无法读取核心层信息',
            message: _error,
            tone: StatusTone.error,
            action: TextButton(onPressed: _load, child: const Text('重试')),
          ),
        if (info != null) ...[
          ListEntrance(
            index: 0,
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(18),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Container(
                          width: 46,
                          height: 46,
                          decoration: BoxDecoration(
                            gradient: const LinearGradient(
                              begin: Alignment.topLeft,
                              end: Alignment.bottomRight,
                              colors: [Color(0xFFFBB040), Color(0xFFE08A1E)],
                            ),
                            borderRadius: BorderRadius.circular(12),
                          ),
                          child: const Center(
                            child: Text('🥭', style: TextStyle(fontSize: 24)),
                          ),
                        ),
                        const SizedBox(width: 14),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                'UMango',
                                style: theme.textTheme.titleLarge
                                    ?.copyWith(fontWeight: FontWeight.w800),
                              ),
                              const SizedBox(height: 2),
                              Text(
                                'Uneven Mango Application · 跨平台包管理器图形界面',
                                style: TextStyle(
                                  fontSize: 12.5,
                                  color: theme.colorScheme.onSurfaceVariant,
                                ),
                              ),
                            ],
                          ),
                        ),
                        FilledButton.icon(
                          onPressed: widget.onGoToDownload,
                          icon: const Icon(Icons.search, size: 17),
                          label: const Text('开始搜索包'),
                        ),
                      ],
                    ),
                    const SizedBox(height: 16),
                    Wrap(
                      spacing: 22,
                      runSpacing: 12,
                      children: [
                        _Fact(label: '核心层', value: 'Rust ${info.coreVersion}'),
                        _Fact(label: '桥接层', value: 'C++ / ABI ${UmangoBridge.instance.abiVersion}'),
                        _Fact(
                          label: '进程执行器',
                          value: info.processRunner,
                          ok: info.processRunner == 'cpp-bridge',
                        ),
                        _Fact(label: '宿主平台', value: '${info.platformDisplay} ${info.arch}'),
                        _Fact(
                          label: '可用性缓存',
                          value: '${info.availabilityTtlSecs ~/ 3600} 小时',
                        ),
                      ],
                    ),
                  ],
                ),
              ),
            ),
          ),
          const SizedBox(height: 20),
        ],
        Text('包源能力矩阵', style: theme.textTheme.titleSmall),
        const SizedBox(height: 4),
        Text(
          '平台不支持或工具未安装的包源会被自动隐藏（可在下载页打开开关查看被隐藏的项）。'
          '带「未验证」标记的适配器其解析逻辑尚未在真机验证过。',
          style: TextStyle(fontSize: 12, color: theme.colorScheme.onSurfaceVariant),
        ),
        const SizedBox(height: 12),
        ListEntrance(
          index: 1,
          child: Card(
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Column(
                children: [
                  _MatrixHeader(),
                  const Divider(height: 1),
                  for (var i = 0; i < _sources.length; i++) ...[
                    _MatrixRow(source: _sources[i], index: i),
                    if (i != _sources.length - 1) const Divider(height: 1),
                  ],
                ],
              ),
            ),
          ),
        ),
        const SizedBox(height: 20),
        ListEntrance(
          index: 2,
          child: Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('阶段1 能力边界（如实说明）', style: theme.textTheme.titleSmall),
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
        ),
      ],
    );
  }
}

class _Fact extends StatelessWidget {
  const _Fact({required this.label, required this.value, this.ok});

  final String label;
  final String value;
  final bool? ok;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(label, style: TextStyle(fontSize: 11, color: theme.colorScheme.outline)),
        const SizedBox(height: 2),
        Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (ok != null) ...[
              Icon(
                ok! ? Icons.check_circle : Icons.warning_amber_rounded,
                size: 13,
                color: ok! ? const Color(0xFF1E6B33) : const Color(0xFF8A5300),
              ),
              const SizedBox(width: 4),
            ],
            Text(
              value,
              style: TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w600,
                fontFamily: 'monospace',
                color: ok == false ? const Color(0xFF8A5300) : theme.colorScheme.onSurface,
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _MatrixHeader extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final style = TextStyle(
      fontSize: 11,
      fontWeight: FontWeight.w700,
      color: Theme.of(context).colorScheme.outline,
    );
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 10),
      child: Row(
        children: [
          SizedBox(width: 130, child: Text('包源', style: style)),
          SizedBox(width: 118, child: Text('状态', style: style)),
          Expanded(child: Text('能力', style: style)),
        ],
      ),
    );
  }
}

class _MatrixRow extends StatelessWidget {
  const _MatrixRow({required this.source, required this.index});

  final SourceView source;
  final int index;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final a = source.availability;

    final color = switch (a.state) {
      AvailabilityState.available => const Color(0xFF1E6B33),
      AvailabilityState.degraded => const Color(0xFF8A5300),
      _ => theme.colorScheme.outline,
    };

    final capabilities = <String>[
      if (source.capabilities.search) '搜索',
      if (source.capabilities.listVersions) '版本列表',
      if (source.capabilities.resolveDeps) '依赖树' else '无依赖数据',
      if (source.capabilities.officialDownloads) '官方下载量' else 'Stars 兜底',
      if (source.capabilities.install) '安装',
    ];

    return ListEntrance(
      index: index,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 11),
        child: Row(
          children: [
            SizedBox(
              width: 130,
              child: Row(
                children: [
                  Text(
                    source.displayName,
                    style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600),
                  ),
                  if (source.verificationNote != null) ...[
                    const SizedBox(width: 5),
                    const Tooltip(
                      message: '解析逻辑尚未在真机验证',
                      child: Icon(Icons.science_outlined, size: 13, color: Color(0xFF8A5300)),
                    ),
                  ],
                ],
              ),
            ),
            SizedBox(
              width: 118,
              child: Text(a.state.displayName, style: TextStyle(fontSize: 12, color: color)),
            ),
            Expanded(
              child: Wrap(
                spacing: 6,
                runSpacing: 4,
                children: [
                  for (final c in capabilities)
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                      decoration: BoxDecoration(
                        color: theme.colorScheme.surfaceContainerHighest,
                        borderRadius: BorderRadius.circular(4),
                      ),
                      child: Text(c, style: const TextStyle(fontSize: 10.5)),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

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
          const Padding(
            padding: EdgeInsets.only(top: 6, right: 8),
            child: Icon(Icons.circle, size: 5),
          ),
          Expanded(
            child: Text(
              text,
              style: TextStyle(
                fontSize: 12.5,
                color: Theme.of(context).colorScheme.onSurfaceVariant,
                height: 1.5,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
