/// 集成测试共享工具。
///
/// 这些测试运行在**真实的 macOS 应用**里（`flutter test integration_test -d macos`），
/// 不是 flutter_tester 模拟环境。因此：
///   · 可以真的加载 dart:ffi 动态库、真的执行 npm 等 CLI；
///   · 定时器是真实的，等待真实异步可以直接 `await`；
///   · 验证到的是"应用真的能用"，而不是"逻辑单元自洽"。
///
/// 为什么不用普通 `flutter test`：本机 flutter_tester 启动后无法连回测试
/// 框架的 socket（会静默挂住 148 秒以上），详见 docs/ROADMAP.md 的说明。
library;

import 'dart:convert';

import 'package:flutter/services.dart' show rootBundle;
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:umango/ffi/umango_bridge.dart';
import 'package:umango/main.dart';

/// 真实异步等待：集成测试里定时器是真的，直接 await 即可。
Future<void> realDelay(WidgetTester tester, Duration duration) async {
  final end = DateTime.now().add(duration);
  while (DateTime.now().isBefore(end)) {
    await tester.pump(const Duration(milliseconds: 100));
    await Future<void>.delayed(const Duration(milliseconds: 50));
  }
}

/// 挂载应用并等待核心层 + 首页数据真正加载完成。
///
/// 首页启动时会调用 `core.info` 与 `sources.list`，后者会真的去探测
/// npm/pnpm/git/brew 是否安装（执行 `--version`），因此这里必须等足够久。
Future<void> pumpAppUntilReady(WidgetTester tester) async {
  await tester.pumpWidget(const UmangoApp());
  await tester.pump();
  // 桥接层加载
  await realDelay(tester, const Duration(seconds: 2));
  // 首页数据（含 CLI 探测）
  await realDelay(tester, const Duration(seconds: 6));
}

/// 读取真实抓取的核心层 JSON 输出（通过 asset bundle）。
///
/// 这些 JSON 是用 Python ctypes 调用 C ABI 从真实核心层导出的，
/// 不是手写样例，因此可以用来钉住前后端字段契约。
Future<Map<String, dynamic>> loadEnvelope(String name) async {
  final raw = await rootBundle.loadString('assets/fixtures/$name.json');
  return jsonDecode(raw) as Map<String, dynamic>;
}

/// 取信封里的 `data`。
Future<T> fixtureData<T>(String name) async {
  final env = await loadEnvelope(name);
  expect(env['ok'], isTrue, reason: 'fixture $name 应为成功响应');
  return env['data'] as T;
}

/// 确保桥接层可用；不可用则直接失败并给出修复命令。
void requireBridge() {
  try {
    final bridge = UmangoBridge.load();
    // 触发一次真实调用，确认不只是加载成功
    expect(bridge.abiVersion, 1);
    expect(bridge.processRunner, 'cpp-bridge',
        reason: 'Rust 侧应使用 C++ 进程执行器；若为 native 说明宿回主调没生效');
  } catch (e) {
    fail('桥接层不可用，请先执行 scripts/build.sh native。\n$e');
  }
}
