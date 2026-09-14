/// 设置页：动画风格 + 包源检测 + 诊断。
///
/// 需求要求动画风格是「全局独立配置项，用户可在设置里切换」，
/// 因此这里既有整套预设，也能单独调列表入场方向与弹窗方向，
/// 并带一个可反复触发的实时预览区 —— 设置改了到底有没有生效，
/// 用户当场就能看到，不用来回切页。
library;

import 'package:flutter/material.dart';

import '../animation/animation_config.dart';
import '../animation/motion_widgets.dart';
import '../ffi/umango_bridge.dart';
import '../models/umango_models.dart';
import '../widgets/package_card.dart';

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

  @override
  Widget build(BuildContext context) {
    final config = AnimationScope.of(context);

    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
      children: [
        _SectionTitle(
          icon: Icons.animation,
          title: '动画风格',
          subtitle: 'PCL-CE 的动画向量已做镜像改造：列表改为纵向入场、弹窗改为向右滑出。'
              '整套风格可随时切换，修改立即生效。',
        ),
        const SizedBox(height: 12),
        _PresetRow(config: config),
        const SizedBox(height: 16),
        _Card(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _RowLabel('总开关'),
              SwitchListTile(
                dense: true,
                contentPadding: EdgeInsets.zero,
                value: config.enabled,
                onChanged: (v) => AnimationScope.update(context, config.copyWith(enabled: v)),
                title: const Text('启用自定义动画'),
                subtitle: Text(
                  AnimationScope.prefersReducedMotion(context)
                      ? '注意：系统已开启"减少动态效果"，动画会被自动跳过'
                      : '关闭后所有列表与弹窗动画直接跳过',
                  style: const TextStyle(fontSize: 12),
                ),
              ),
              const Divider(height: 20),
              _RowLabel('列表入场动画'),
              const SizedBox(height: 6),
              DropdownButtonFormField<ListEntranceStyle>(
                initialValue: config.listEntrance,
                isDense: true,
                decoration: const InputDecoration(labelText: '入场方向'),
                items: [
                  for (final style in ListEntranceStyle.values)
                    DropdownMenuItem(
                      value: style,
                      child: Text('${style.displayName} · ${style.description}',
                          style: const TextStyle(fontSize: 13)),
                    ),
                ],
                onChanged: (v) => v == null
                    ? null
                    : AnimationScope.update(context, config.copyWith(listEntrance: v)),
              ),
              const SizedBox(height: 14),
              _RowLabel('弹窗动画'),
              const SizedBox(height: 6),
              DropdownButtonFormField<DialogMotionStyle>(
                initialValue: config.dialogMotion,
                isDense: true,
                decoration: const InputDecoration(labelText: '弹出方向'),
                items: [
                  for (final style in DialogMotionStyle.values)
                    DropdownMenuItem(
                      value: style,
                      child: Text('${style.displayName} · ${style.description}',
                          style: const TextStyle(fontSize: 13)),
                    ),
                ],
                onChanged: (v) => v == null
                    ? null
                    : AnimationScope.update(context, config.copyWith(dialogMotion: v)),
              ),
              const SizedBox(height: 18),
              _RowLabel('节奏参数'),
              const SizedBox(height: 4),
              _DurationSlider(
                label: '单项时长',
                value: config.listDuration,
                min: 100,
                max: 800,
                onChanged: (d) => AnimationScope.update(context, config.copyWith(listDuration: d)),
              ),
              _DurationSlider(
                label: '逐条延迟',
                value: config.staggerStep,
                min: 0,
                max: 120,
                onChanged: (d) => AnimationScope.update(context, config.copyWith(staggerStep: d)),
              ),
              _DurationSlider(
                label: '弹窗时长',
                value: config.dialogDuration,
                min: 80,
                max: 600,
                onChanged: (d) => AnimationScope.update(context, config.copyWith(dialogDuration: d)),
              ),
              const SizedBox(height: 8),
              Row(
                children: [
                  Expanded(
                    child: Text(
                      '位移幅度 ${(config.offsetExtent * 100).toStringAsFixed(0)}%',
                      style: const TextStyle(fontSize: 12.5),
                    ),
                  ),
                  Expanded(
                    flex: 2,
                    child: Slider(
                      value: config.offsetExtent,
                      min: 0.05,
                      max: 0.6,
                      onChanged: (v) =>
                          AnimationScope.update(context, config.copyWith(offsetExtent: v)),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
        const SizedBox(height: 18),
        _PreviewCard(
          generation: _previewGeneration,
          onReplay: () => setState(() => _previewGeneration++),
        ),
        const SizedBox(height: 24),
        _SectionTitle(
          icon: Icons.hub_outlined,
          title: '包源检测',
          subtitle: '可用性结果带 24 小时缓存保护：缓存期内不重复探测 CLI；'
              '刷新失败时会沿用上一次的成功结果并标注"数据可能过期"，而不是直接判为不可用。',
          action: TextButton.icon(
            onPressed: _loadingSources ? null : () => _loadSources(refresh: true),
            icon: const Icon(Icons.refresh, size: 16),
            label: const Text('重新检测'),
          ),
        ),
        const SizedBox(height: 12),
        if (_loadingSources)
          const Padding(
            padding: EdgeInsets.all(24),
            child: Center(child: CircularProgressIndicator(strokeWidth: 2)),
          )
        else if (_sourcesError != null)
          StatusBanner(
            icon: Icons.error_outline,
            title: '包源检测失败',
            message: _sourcesError,
            tone: StatusTone.error,
          )
        else
          _Card(
            child: Column(
              children: [
                for (var i = 0; i < _sources.length; i++) ...[
                  _SourceRow(source: _sources[i], index: i),
                  if (i != _sources.length - 1) const Divider(height: 18),
                ],
              ],
            ),
          ),
        const SizedBox(height: 24),
        _SectionTitle(
          icon: Icons.monitor_heart_outlined,
          title: '运行诊断',
          subtitle: '用于确认三层架构真的接通了：进程执行器显示 cpp-bridge，'
              '说明进程调用确实经过 C++ 桥接层，而不是悄悄退化成了纯 Rust 实现。',
        ),
        const SizedBox(height: 12),
        const _DiagnosticsCard(),
      ],
    );
  }
}

class _PresetRow extends StatelessWidget {
  const _PresetRow({required this.config});

  final AnimationConfig config;

  @override
  Widget build(BuildContext context) {
    final active = config.matchingPreset;
    return Wrap(
      spacing: 10,
      runSpacing: 10,
      children: [
        for (final preset in AnimationPreset.values)
          _PresetCard(
            preset: preset,
            active: active == preset,
            onTap: () => AnimationScope.update(context, preset.config),
          ),
      ],
    );
  }
}

class _PresetCard extends StatelessWidget {
  const _PresetCard({required this.preset, required this.active, required this.onTap});

  final AnimationPreset preset;
  final bool active;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(9),
      child: Container(
        width: 186,
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(9),
          border: Border.all(
            color: active ? theme.colorScheme.primary : theme.dividerColor,
            width: active ? 1.6 : 1,
          ),
          color: active
              ? theme.colorScheme.primaryContainer.withValues(alpha: 0.35)
              : theme.cardColor,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Text(preset.displayName,
                    style: theme.textTheme.bodyMedium
                        ?.copyWith(fontWeight: FontWeight.w700)),
                if (active) ...[
                  const Spacer(),
                  Icon(Icons.check_circle, size: 15, color: theme.colorScheme.primary),
                ],
              ],
            ),
            const SizedBox(height: 4),
            Text(preset.description,
                style: TextStyle(fontSize: 11.5, color: theme.colorScheme.outline)),
          ],
        ),
      ),
    );
  }
}

/// 实时预览：改了设置立刻能看到列表入场效果，还能主动重播。
class _PreviewCard extends StatelessWidget {
  const _PreviewCard({required this.generation, required this.onReplay});

  final int generation;
  final VoidCallback onReplay;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final config = AnimationScope.of(context);

    return _Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text('效果预览', style: theme.textTheme.titleSmall),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  '当前：${config.listEntrance.displayName} + ${config.dialogMotion.displayName}',
                  style: TextStyle(fontSize: 12, color: theme.colorScheme.outline),
                ),
              ),
              TextButton.icon(
                onPressed: onReplay,
                icon: const Icon(Icons.replay, size: 16),
                label: const Text('重播入场'),
              ),
              const SizedBox(width: 6),
              OutlinedButton.icon(
                onPressed: () => showUmangoDialog<void>(
                  context: context,
                  builder: (context) => AlertDialog(
                    title: const Text('弹窗动画预览'),
                    content: Column(
                      mainAxisSize: MainAxisSize.min,
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text('当前弹窗风格：${config.dialogMotion.displayName}'),
                        const SizedBox(height: 8),
                        Text(
                          config.dialogMotion.description,
                          style: TextStyle(
                            fontSize: 12,
                            color: Theme.of(context).colorScheme.outline,
                          ),
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
                ),
                icon: const Icon(Icons.open_in_new, size: 16),
                label: const Text('预览弹窗'),
              ),
            ],
          ),
          const SizedBox(height: 12),
          // 用 generation 作为 axisKey：点"重播"时重建列表以重新播放入场
          Column(
            key: ValueKey('preview-$generation'),
            children: [
              for (var i = 0; i < 4; i++)
                ListEntrance(
                  index: i,
                  axisKey: generation,
                  child: Container(
                    margin: const EdgeInsets.only(bottom: 7),
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                    decoration: BoxDecoration(
                      color: theme.colorScheme.surfaceContainerHighest
                          .withValues(alpha: 0.55),
                      borderRadius: BorderRadius.circular(7),
                    ),
                    child: Row(
                      children: [
                        Icon(Icons.circle, size: 7, color: theme.colorScheme.primary),
                        const SizedBox(width: 10),
                        Text('预览列表项 ${i + 1}',
                            style: const TextStyle(fontSize: 12.5)),
                      ],
                    ),
                  ),
                ),
            ],
          ),
        ],
      ),
    );
  }
}

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
    return Row(
      children: [
        SizedBox(
          width: 88,
          child: Text(
            '$label ${value.inMilliseconds}ms',
            style: const TextStyle(fontSize: 12.5),
          ),
        ),
        Expanded(
          child: Slider(
            value: value.inMilliseconds.clamp(min, max).toDouble(),
            min: min.toDouble(),
            max: max.toDouble(),
            onChanged: (v) => onChanged(Duration(milliseconds: v.round())),
          ),
        ),
      ],
    );
  }
}

class _SourceRow extends StatelessWidget {
  const _SourceRow({required this.source, required this.index});

  final SourceView source;
  final int index;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final a = source.availability;

    final (color, icon) = switch (a.state) {
      AvailabilityState.available => (const Color(0xFF1E6B33), Icons.check_circle_outline),
      AvailabilityState.degraded => (const Color(0xFF8A5300), Icons.history_toggle_off),
      AvailabilityState.unsupportedPlatform => (theme.colorScheme.outline, Icons.block),
      AvailabilityState.notInstalled => (theme.colorScheme.error, Icons.download_for_offline_outlined),
      AvailabilityState.unreachable => (theme.colorScheme.error, Icons.cloud_off),
    };

    return ListEntrance(
      index: index,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 16, color: color),
          const SizedBox(width: 10),
          SizedBox(
            width: 96,
            child: Text(
              source.displayName,
              style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600),
            ),
          ),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Text(a.state.displayName,
                        style: TextStyle(fontSize: 12.5, color: color)),
                    if (a.stale) ...[
                      const SizedBox(width: 6),
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                        decoration: BoxDecoration(
                          color: const Color(0xFFFFF3E0),
                          borderRadius: BorderRadius.circular(4),
                        ),
                        child: const Text('缓存数据',
                            style: TextStyle(fontSize: 10, color: Color(0xFF8A5300))),
                      ),
                    ],
                    if (source.verificationNote != null) ...[
                      const SizedBox(width: 6),
                      const Tooltip(
                        message: '该适配器的解析逻辑尚未在真机验证',
                        child: Icon(Icons.science_outlined,
                            size: 13, color: Color(0xFF8A5300)),
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 3),
                Text(
                  [
                    if (a.cliVersion != null) '版本 ${a.cliVersion}',
                    if (a.cliPath != null) a.cliPath!,
                    if (a.detail != null) a.detail!,
                  ].join(' · '),
                  style: TextStyle(fontSize: 11, color: theme.colorScheme.outline),
                ),
              ],
            ),
          ),
          SizedBox(
            width: 132,
            child: Text(
              [
                if (source.capabilities.search) '搜索',
                if (source.capabilities.resolveDeps) '依赖树',
                if (source.capabilities.officialDownloads) '官方下载量',
              ].join(' / '),
              style: TextStyle(fontSize: 11, color: theme.colorScheme.outline),
              textAlign: TextAlign.right,
            ),
          ),
        ],
      ),
    );
  }
}

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
    final theme = Theme.of(context);
    final info = _info;

    return _Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (_running)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 16),
              child: Center(child: CircularProgressIndicator(strokeWidth: 2)),
            )
          else if (_error != null)
            StatusBanner(
              icon: Icons.error_outline,
              title: '诊断失败',
              message: _error,
              tone: StatusTone.error,
              action: TextButton(onPressed: _load, child: const Text('重试')),
            )
          else if (info != null) ...[
            Wrap(
              spacing: 20,
              runSpacing: 10,
              children: [
                _Diag(label: '核心层版本', value: info.coreVersion),
                _Diag(label: '宿主平台', value: '${info.platformDisplay} (${info.arch})'),
                _Diag(label: 'ABI 版本', value: '${UmangoBridge.instance.abiVersion}'),
                _Diag(label: '可用性缓存 TTL', value: '${info.availabilityTtlSecs ~/ 3600} 小时'),
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
            if (_selfTest != null) ...[
              Text('桥接层自检', style: theme.textTheme.bodySmall),
              const SizedBox(height: 6),
              Container(
                width: double.infinity,
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: theme.colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(6),
                ),
                child: SelectableText(
                  const JsonEncoderPretty().convert(_selfTest!),
                  style: const TextStyle(fontFamily: 'monospace', fontSize: 11.5),
                ),
              ),
            ],
            const SizedBox(height: 10),
            Row(
              children: [
                TextButton.icon(
                  onPressed: _load,
                  icon: const Icon(Icons.refresh, size: 16),
                  label: const Text('重新诊断'),
                ),
              ],
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
    final theme = Theme.of(context);
    return Tooltip(
      message: hint ?? '',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(label, style: TextStyle(fontSize: 11, color: theme.colorScheme.outline)),
          const SizedBox(height: 2),
          Text(
            value,
            style: TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              fontFamily: 'monospace',
              color: highlight ? const Color(0xFF1E6B33) : theme.colorScheme.onSurface,
            ),
          ),
        ],
      ),
    );
  }
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle({
    required this.icon,
    required this.title,
    required this.subtitle,
    this.action,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 18, color: theme.colorScheme.primary),
        const SizedBox(width: 9),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: theme.textTheme.titleSmall),
              const SizedBox(height: 3),
              Text(
                subtitle,
                style: TextStyle(fontSize: 12, color: theme.colorScheme.onSurfaceVariant),
              ),
            ],
          ),
        ),
        if (action != null) action!,
      ],
    );
  }
}

class _Card extends StatelessWidget {
  const _Card({required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) => Card(
        child: Padding(padding: const EdgeInsets.all(16), child: child),
      );
}

class _RowLabel extends StatelessWidget {
  const _RowLabel(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Text(
        text,
        style: Theme.of(context)
            .textTheme
            .bodyMedium
            ?.copyWith(fontWeight: FontWeight.w700),
      );
}
