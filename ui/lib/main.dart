/// UMango 应用入口。
///
/// 启动顺序：
///   1. 尝试加载 C++ 桥接层动态库；
///   2. 加载失败时显示**可操作的诊断页**（列出尝试过的路径与构建命令），
///      而不是抛异常白屏 —— 阶段1 尚未把 dylib 打进 .app，
///      这条失败路径是真实会遇到的；
///   3. 成功后进入主界面。
library;

import 'dart:io';

import 'package:flutter/material.dart';

import 'animation/animation_config.dart';
import 'ffi/umango_bridge.dart';
import 'pages/about_page.dart';
import 'pages/app_shell.dart';
import 'theme/app_theme.dart';

void main() {
  runApp(const UmangoApp());
}

class UmangoApp extends StatefulWidget {
  const UmangoApp({super.key});

  @override
  State<UmangoApp> createState() => _UmangoAppState();
}

class _UmangoAppState extends State<UmangoApp> {
  /// 全局动画配置。挂在应用根部，因此是真正的"全局独立配置项"。
  AnimationConfig _animation = const AnimationConfig();
  ThemeMode _themeMode = ThemeMode.system;

  @override
  Widget build(BuildContext context) {
    return AnimationScope(
      config: _animation,
      onChanged: (config) => setState(() => _animation = config),
      child: MaterialApp(
        title: 'UMango',
        debugShowCheckedModeBanner: false,
        theme: AppTheme.light(),
        darkTheme: AppTheme.dark(),
        themeMode: _themeMode,
        home: _BridgeGate(
          themeMode: _themeMode,
          onToggleTheme: () => setState(() {
            _themeMode = switch (_themeMode) {
              ThemeMode.system => ThemeMode.light,
              ThemeMode.light => ThemeMode.dark,
              ThemeMode.dark => ThemeMode.system,
            };
          }),
        ),
      ),
    );
  }
}

/// 桥接层加载闸门：成功进入主界面，失败显示诊断页。
class _BridgeGate extends StatefulWidget {
  const _BridgeGate({required this.onToggleTheme, required this.themeMode});

  final VoidCallback onToggleTheme;
  final ThemeMode themeMode;

  @override
  State<_BridgeGate> createState() => _BridgeGateState();
}

class _BridgeGateState extends State<_BridgeGate> {
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _tryLoad();
  }

  Future<void> _tryLoad() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      UmangoBridge.load();
      if (!mounted) return;
      setState(() => _loading = false);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = '$e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) {
      return const Scaffold(
        body: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              CircularProgressIndicator(strokeWidth: 2),
              SizedBox(height: 16),
              Text('正在加载核心层…', style: TextStyle(fontSize: 13)),
            ],
          ),
        ),
      );
    }

    if (_error != null) {
      return _BridgeErrorScreen(
        error: _error!,
        onRetry: _tryLoad,
        onToggleTheme: widget.onToggleTheme,
      );
    }

    return Stack(
      children: [
        const AppShell(),
        Positioned(
          right: 14,
          top: 10,
          child: IconButton(
            onPressed: widget.onToggleTheme,
            tooltip: '切换主题（当前：${_themeLabel(widget.themeMode)}）',
            iconSize: 17,
            icon: Icon(switch (widget.themeMode) {
              ThemeMode.system => Icons.brightness_auto_outlined,
              ThemeMode.light => Icons.light_mode_outlined,
              ThemeMode.dark => Icons.dark_mode_outlined,
            }),
          ),
        ),
      ],
    );
  }

  String _themeLabel(ThemeMode mode) => switch (mode) {
        ThemeMode.system => '跟随系统',
        ThemeMode.light => '浅色',
        ThemeMode.dark => '深色',
      };
}

/// 桥接层加载失败的诊断页。
class _BridgeErrorScreen extends StatelessWidget {
  const _BridgeErrorScreen({
    required this.error,
    required this.onRetry,
    required this.onToggleTheme,
  });

  final String error;
  final VoidCallback onRetry;
  final VoidCallback onToggleTheme;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final attempted = UmangoBridge.attemptedPaths;

    return Scaffold(
      body: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(32),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 720),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.extension_off_outlined,
                        size: 26, color: theme.colorScheme.error),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        '核心层未能加载',
                        style: theme.textTheme.titleMedium
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                    ),
                    IconButton(
                      onPressed: onToggleTheme,
                      iconSize: 18,
                      icon: const Icon(Icons.brightness_auto_outlined),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Text(
                  'UMango 的业务逻辑位于 Rust 核心层，经 C++ 桥接层以动态库形式提供。'
                  '当前这个动态库没有被找到 —— 界面本身是好的，只是暂时连不上后端。',
                  style: TextStyle(
                    fontSize: 13,
                    height: 1.6,
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 18),
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(14),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.errorContainer.withValues(alpha: 0.35),
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: SelectableText(
                    error,
                    style: const TextStyle(fontFamily: 'monospace', fontSize: 11.5),
                  ),
                ),
                const SizedBox(height: 18),
                Text('已尝试的加载路径', style: theme.textTheme.titleSmall),
                const SizedBox(height: 8),
                for (final path in attempted)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: SelectableText(
                      '· $path',
                      style: const TextStyle(fontFamily: 'monospace', fontSize: 11.5),
                    ),
                  ),
                const SizedBox(height: 18),
                Text('如何修复', style: theme.textTheme.titleSmall),
                const SizedBox(height: 8),
                const SelectableText(
                  '在项目根目录执行：\n'
                  '    scripts/build.sh\n'
                  '该脚本会依次构建 Rust 核心层与 C++ 桥接层，产出 libumango_bridge.dylib。\n'
                  '也可以用环境变量 UMANGO_BRIDGE_LIB 直接指定动态库的绝对路径。',
                  style: TextStyle(fontFamily: 'monospace', fontSize: 11.5, height: 1.7),
                ),
                const SizedBox(height: 20),
                Row(
                  children: [
                    FilledButton.icon(
                      onPressed: onRetry,
                      icon: const Icon(Icons.refresh, size: 17),
                      label: const Text('重新加载'),
                    ),
                    const SizedBox(width: 10),
                    OutlinedButton(
                      onPressed: () => Navigator.of(context).push(
                        MaterialPageRoute(builder: (_) => const AboutPage()),
                      ),
                      child: const Text('查看项目说明'),
                    ),
                  ],
                ),
                const SizedBox(height: 10),
                Text(
                  '宿主平台：${Platform.operatingSystem}',
                  style: TextStyle(fontSize: 11.5, color: theme.colorScheme.outline),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
