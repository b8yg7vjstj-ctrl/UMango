/// 关于页：项目定位、合规红线与路线图。
///
/// 把项目硬性约束写在应用里，而不是只写在文档里 ——
/// 这些约束决定了后续所有代码该怎么写，放在界面上更容易被看见和遵守。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';

class AboutPage extends StatelessWidget {
  const AboutPage({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
      children: [
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
                      const Text('🥭', style: TextStyle(fontSize: 30)),
                      const SizedBox(width: 12),
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text('UMango',
                              style: theme.textTheme.titleLarge
                                  ?.copyWith(fontWeight: FontWeight.w800)),
                          const SizedBox(height: 2),
                          Text(
                            'Uneven Mango Application · 全局独立应用',
                            style: TextStyle(
                              fontSize: 12.5,
                              color: theme.colorScheme.onSurfaceVariant,
                            ),
                          ),
                        ],
                      ),
                    ],
                  ),
                  const SizedBox(height: 14),
                  Text(
                    '从零开发的跨平台包管理器图形界面。交互设计对标 PCL-CE，'
                    '但所有业务代码自研；目标是用图形界面管理 npm / pnpm / git / scoop / '
                    'winget / Homebrew 等命令行包管理器。',
                    style: TextStyle(
                      fontSize: 12.5,
                      height: 1.6,
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
        const SizedBox(height: 18),
        _Section(
          index: 1,
          title: '架构分层',
          children: const [
            _Line('Flutter 前端层', '界面渲染、动画引擎、交互逻辑、独立的动画配置模块'),
            _Line('Rust 核心业务层', '包源适配器、版本解析、依赖树计算、可用性缓存、后续的插件调度'),
            _Line('C++ 桥接层', '系统调用、进程创建（posix_spawn / CreateProcess）、本地文件读写'),
            _Line('通信方式', 'Dart ⇄ C++ 走 dart:ffi 的 C ABI；C++ ⇄ Rust 双向：'
                '向下转发业务调用，向上提供进程执行回调'),
          ],
        ),
        const SizedBox(height: 14),
        _Section(
          index: 2,
          title: '硬性约束（红线）',
          children: const [
            _Line('代码隔离', '仅观摩 PCL-CE 的交互设计，业务代码全部手写，不复制其源码'),
            _Line('插件模块例外', '插件解析／导入模块可复用 HMCL-CE / PCL-CE 的插件加载代码，单独隔离'),
            _Line('技术栈', 'Rust + C++ + Flutter，不使用 Electron 网页套壳'),
            _Line('后端剥离 MC', '不包含任何 Minecraft 启动与资源下载逻辑，只保留包管理业务'),
            _Line('动画向量改造', '列表入场改为纵向、弹窗改为向右滑出，降低视觉代码相似度'),
          ],
        ),
        const SizedBox(height: 14),
        _Section(
          index: 3,
          title: '开发路线图',
          children: const [
            _Line('阶段1（当前）', '三层架构打通 + 空 UI 框架 + 动画配置系统 + 包源／版本／依赖树'),
            _Line('阶段2', '包管理器适配器深化，优先补齐可在本机验证的 npm / pnpm / git'),
            _Line('阶段3', '安装 / 卸载 / 升级的实际执行与进度反馈'),
            _Line('阶段4', 'Cordis 风格插件系统 + PCL-CE 插件导入模块'),
            _Line('阶段5', '全量测试，完善网络异常与源不可用的兜底逻辑'),
          ],
        ),
        const SizedBox(height: 14),
        ListEntrance(
          index: 4,
          child: Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('第三方资源与授权', style: theme.textTheme.titleSmall),
                  const SizedBox(height: 8),
                  Text(
                    '· npm / pnpm / git / Homebrew 图标来自 simple-icons，授权 CC0 1.0（公有领域）。\n'
                    '· winget 与 scoop 无可用官方图标，界面使用自绘字母徽章，未使用任何来源不明的商标资源。\n'
                    '· 版本范围解析复用 node-semver（Rust crate），未自行重造语义化版本匹配逻辑。',
                    style: TextStyle(
                      fontSize: 12,
                      height: 1.7,
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ],
    );
  }
}

class _Section extends StatelessWidget {
  const _Section({required this.index, required this.title, required this.children});

  final int index;
  final String title;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    return ListEntrance(
      index: index,
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: Theme.of(context).textTheme.titleSmall),
              const SizedBox(height: 10),
              ...children,
            ],
          ),
        ),
      ),
    );
  }
}

class _Line extends StatelessWidget {
  const _Line(this.label, this.text);

  final String label;
  final String text;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 9),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 118,
            child: Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                fontWeight: FontWeight.w700,
                color: theme.colorScheme.onSurface,
              ),
            ),
          ),
          Expanded(
            child: Text(
              text,
              style: TextStyle(
                fontSize: 12.5,
                height: 1.55,
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
