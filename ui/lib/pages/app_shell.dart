/// 应用外壳：左侧导航 + 内容区。
///
/// 结构参考 PCL-CE 的「左侧功能导航 + 右侧内容页」布局思路，代码全部自研。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import 'about_page.dart';
import 'download_page.dart';
import 'home_page.dart';
import 'settings_page.dart';

/// 导航项定义
enum AppSection {
  home('主页', Icons.home_outlined, Icons.home),
  download('下载', Icons.cloud_download_outlined, Icons.cloud_download),
  settings('设置', Icons.settings_outlined, Icons.settings),
  about('关于', Icons.info_outline, Icons.info);

  const AppSection(this.label, this.icon, this.activeIcon);
  final String label;
  final IconData icon;
  final IconData activeIcon;
}

class AppShell extends StatefulWidget {
  const AppShell({super.key});

  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  AppSection _section = AppSection.home;
  final _navKey = GlobalKey();

  void _go(AppSection section) => setState(() => _section = section);

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      body: Row(
        children: [
          // ---------------- 侧边栏 ----------------
          Container(
            width: 196,
            decoration: BoxDecoration(
              color: theme.colorScheme.surface,
              border: Border(right: BorderSide(color: theme.dividerColor)),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 22, 16, 18),
                  child: Row(
                    children: [
                      Container(
                        width: 30,
                        height: 30,
                        decoration: BoxDecoration(
                          gradient: const LinearGradient(
                            begin: Alignment.topLeft,
                            end: Alignment.bottomRight,
                            colors: [Color(0xFFFBB040), Color(0xFFE08A1E)],
                          ),
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: const Center(
                          child: Text('🥭', style: TextStyle(fontSize: 15)),
                        ),
                      ),
                      const SizedBox(width: 10),
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text('UMango',
                              style: theme.textTheme.titleSmall
                                  ?.copyWith(fontWeight: FontWeight.w800)),
                          Text('包管理器',
                              style: TextStyle(
                                fontSize: 10.5,
                                color: theme.colorScheme.outline,
                              )),
                        ],
                      ),
                    ],
                  ),
                ),
                const Divider(height: 1),
                const SizedBox(height: 8),
                // 导航项带轻微入场，方向跟随全局动画配置
                ...AppSection.values.asMap().entries.map((entry) {
                  final index = entry.key;
                  final section = entry.value;
                  final selected = section == _section;
                  return ListEntrance(
                    index: index,
                    child: Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 2),
                      child: Material(
                        color: selected
                            ? theme.colorScheme.primaryContainer.withValues(alpha: 0.55)
                            : Colors.transparent,
                        borderRadius: BorderRadius.circular(8),
                        child: InkWell(
                          onTap: () => _go(section),
                          borderRadius: BorderRadius.circular(8),
                          child: Padding(
                            padding: const EdgeInsets.symmetric(
                                horizontal: 12, vertical: 11),
                            child: Row(
                              children: [
                                Icon(
                                  selected ? section.activeIcon : section.icon,
                                  size: 18,
                                  color: selected
                                      ? theme.colorScheme.primary
                                      : theme.colorScheme.onSurfaceVariant,
                                ),
                                const SizedBox(width: 11),
                                Text(
                                  section.label,
                                  style: TextStyle(
                                    fontSize: 13,
                                    fontWeight:
                                        selected ? FontWeight.w700 : FontWeight.w500,
                                    color: selected
                                        ? theme.colorScheme.primary
                                        : theme.colorScheme.onSurface,
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                    ),
                  );
                }),
                const Spacer(),
                const Divider(height: 1),
                Padding(
                  padding: const EdgeInsets.all(14),
                  child: Text(
                    '阶段1 · 骨架与只读能力\n安装执行将在阶段3 接入',
                    style: TextStyle(
                      fontSize: 10.5,
                      height: 1.5,
                      color: theme.colorScheme.outline,
                    ),
                  ),
                ),
              ],
            ),
          ),
          // ---------------- 内容区 ----------------
          Expanded(
            child: PageFadeThrough(
              child: KeyedSubtree(
                key: ValueKey(_section),
                child: switch (_section) {
                  AppSection.home => HomePage(
                      key: _navKey,
                      onGoToDownload: () => _go(AppSection.download),
                    ),
                  AppSection.download => const DownloadPage(),
                  AppSection.settings => SettingsPage(
                      onOpenAbout: () => _go(AppSection.about),
                    ),
                  AppSection.about => const AboutPage(),
                },
              ),
            ),
          ),
        ],
      ),
    );
  }
}
