/// dart:ffi 绑定对**真实动态库**的集成测试。
///
/// 与其他测试不同，这里不做任何 mock：它加载真正构建出来的
/// `libumango_bridge.dylib`，穿过 C++ 桥接层调用 Rust 核心层，
/// 并真的执行 npm 等外部 CLI。
///
/// 它验证的是"三层真的连通"，而不只是"各自的单元逻辑正确"。
/// 前置条件：先执行 `scripts/build.sh all`。
///
/// 注意：应用界面层的测试见 app_test.dart；本文件只碰数据层，
/// 因此它的失败能精确定位到"FFI / 核心层"而不是"界面"。
library;

import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:umango/ffi/umango_bridge.dart';
import 'package:umango/models/umango_models.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  late UmangoBridge bridge;

  setUpAll(() {
    try {
      bridge = UmangoBridge.load();
    } catch (e) {
      fail('无法加载桥接层动态库，请先执行 scripts/build.sh native。\n$e');
    }
  });

  group('桥接层基础', () {
    test('ABI 版本匹配且平台标识有效', () {
      expect(bridge.abiVersion, 1);
      expect(bridge.coreVersion, isNotEmpty);
      expect(bridge.platform, anyOf('macos', 'windows', 'linux'));
    });

    test('进程执行器是 cpp-bridge（而不是退回 native）', () {
      // 整条链路最关键的指标：若为 native，说明 C++ 进程调用层没被用上，
      // 只是"看起来能跑"。这类退化不会抛异常，必须有断言盯着。
      expect(bridge.processRunner, 'cpp-bridge');
    });

    test('自检程序返回 ok=true 且确认 C++ 进程层生效', () async {
      final result = await bridge.selfTest();
      expect(result['ok'], isTrue, reason: '自检结果：$result');
      expect(result['cpp_process_layer_active'], isTrue);
      expect(result['echo_stdout'], contains('umango-bridge-self-test'));
      expect(result['which_sh_ok'], isTrue);
    });
  });

  group('sources.list', () {
    test('返回 6 个包源，且门控与能力位自洽', () async {
      final sources = await bridge.sources();
      expect(sources.length, 6);

      for (final source in sources) {
        if (!source.supportedHere) {
          expect(source.availability.state,
              AvailabilityState.unsupportedPlatform);
          expect(source.isSelectable, isFalse);
        }
      }
      final npm = sources.firstWhere((s) => s.kind == PackageSourceKind.npm);
      expect(npm.availability.state, AvailabilityState.available);
      expect(npm.capabilities.officialDownloads, isTrue);
    });

    test('刷新后仍返回完整列表（24h 缓存不丢数据）', () async {
      final refreshed = await bridge.sources(refresh: true);
      expect(refreshed.length, 6);
      final npm = refreshed.firstWhere((s) => s.kind == PackageSourceKind.npm);
      expect(npm.availability.state, AvailabilityState.available);
    });
  });

  group('packages.versions（真实调用 npm CLI）', () {
    test('取到 is-number 的版本列表且最新在前', () async {
      final versions = await bridge.versions(PackageSourceKind.npm, 'is-number');
      expect(versions, isNotEmpty);
      expect(versions.any((v) => v.channel == ReleaseChannel.stable), isTrue);
    });

    test('不存在的包抛出 package_not_found', () async {
      await expectLater(
        bridge.versions(
          PackageSourceKind.npm,
          'umango-definitely-not-a-real-package-xyz-12345',
        ),
        throwsA(isA<UmangoError>()
            .having((e) => e.code, 'code', 'package_not_found')),
      );
    });
  });

  group('packages.dependencies（依赖树端到端）', () {
    test('is-odd 的依赖树被完整解析且无降级警告', () async {
      final tree =
          await bridge.dependencies(PackageSourceKind.npm, 'is-odd', maxDepth: 2);

      expect(tree.root.name, 'is-odd');
      // 关键：树构建器在单点失败时也会返回 ok，
      // 所以不能只断言"调用成功"，必须确认真的解析出了节点。
      expect(tree.root.state, DependencyState.resolved);
      expect(tree.warnings, isEmpty, reason: '实际警告：${tree.warnings}');
      expect(tree.summary.total, greaterThanOrEqualTo(2));
    });

    test('深度限制生效（不展开超过指定层数）', () async {
      final shallow =
          await bridge.dependencies(PackageSourceKind.npm, 'express', maxDepth: 1);
      for (final child in shallow.root.children) {
        expect(child.children, isEmpty);
      }
    });
  });

  group('packages.downloads（官方优先 → Stars 兜底）', () {
    test('npm 走官方下载量接口', () async {
      final stats = await bridge.downloads(PackageSourceKind.npm, 'is-number');
      expect(stats, isA<OfficialDownloadStats>());
      expect(stats.isFallback, isFalse);
      expect(stats.count, greaterThan(1000));
    });

    test('pnpm 无官方接口时自动兜底为 GitHub Stars', () async {
      final stats = await bridge.downloads(
        PackageSourceKind.pnpm,
        'is-number',
        repository: 'git+https://github.com/jonschlinkert/is-number.git',
      );
      expect(stats, isA<GitHubStarsStats>(),
          reason: 'pnpm 没有独立下载量统计，必须兜底到 Stars');
      expect(stats.metricName, 'GitHub Stars');
      expect(stats.isFallback, isTrue);
    });

    test('缺少仓库信息时诚实返回未知，而不是编造数字', () async {
      final stats = await bridge.downloads(PackageSourceKind.pnpm, 'some-pkg');
      expect(stats.count, isNull);
      expect(stats.metricName, '暂无数据');
    });
  });

  group('packages.resolve_install（命令计划）', () {
    test('按包源与作用域生成正确命令', () async {
      final global = await bridge.resolveInstall(
        PackageSourceKind.npm,
        'is-number',
        version: '7.0.0',
      );
      expect(global.program, 'npm');
      expect(global.commandLine, contains('is-number@7.0.0'));

      final local = await bridge.resolveInstall(
        PackageSourceKind.npm,
        'is-number',
        scope: InstallScope.projectLocal,
      );
      expect(local.args, isNot(contains('-g')));
    });
  });

  group('错误路径与并发安全', () {
    test('未知方法返回结构化错误', () async {
      await expectLater(
        bridge.invoke('definitely.not.a.method'),
        throwsA(isA<UmangoError>()),
      );
    });

    test('并发调用不会互相干扰', () async {
      final results = await Future.wait([
        bridge.coreInfo(),
        bridge.coreInfo(),
        bridge.coreInfo(),
      ]);
      for (final info in results) {
        expect(info.processRunner, 'cpp-bridge');
      }
    });
  });
}
