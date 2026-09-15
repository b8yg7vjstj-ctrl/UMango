/// `flutter drive` 的 host 侧驱动：把应用侧 `binding.takeScreenshot()` 产出的
/// PNG 字节写到磁盘。
///
/// 运行方式（**必须**走 drive，不能用 `flutter test`）：
/// ```
/// cd "$P/ui"
/// flutter drive \
///   --driver=test_driver/integration_test.dart \
///   --target=integration_test/screenshot_test.dart \
///   -d macos
/// ```
///
/// 输出目录：
///   · 应用侧兜底抓帧（**真正的主力产物**）→ `$P/ui/build/screenshots/<name>.png`，
///     由 `screenshot_test.dart` 自己写盘，不经过本文件；
///   · 本文件的 `onScreenshot` 回调 → `$P/ui/build/screenshots/driver_<name>.png`，
///     只有在平台实现 `captureScreenshot` 时才产出（macOS 上不会，见下）。
///   （`flutter drive` 的 host 进程 CWD 就是包目录，所以相对路径即包内路径。）
///
/// ## 为什么 onScreenshot 还要带兜底（重要，实测结论）
///
/// 上游的 `integration_test_macos` 原生插件**只实现了 `allTestsFinished`**，
/// 没有实现 `captureScreenshot`：
///
/// ```swift
/// // flutter/packages/integration_test/integration_test_macos/.../IntegrationTestPlugin.swift
/// public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
///   switch call.method {
///   case "allTestsFinished": result(nil)
///   default: result(FlutterMethodNotImplemented)   // ← captureScreenshot 落到这里
///   }
/// }
/// ```
///
/// 而 `IntegrationTestWidgetsFlutterBinding.takeScreenshot()` 走的正是
/// `plugins.flutter.io/integration_test` 通道的 `captureScreenshot` 方法
/// （见 `integration_test/lib/src/_callback_io.dart`），因此在本机 macOS 上
/// 它**必然**抛 `MissingPluginException`。
///
/// 另一个坑：该 macOS 插件的 `pubspec.yaml` **没有声明 `macos` 平台**，
/// 所以本项目 `ui/pubspec.yaml` 里 `integration_test: sdk: flutter` 解析出来的
/// `.flutter-plugins-dependencies` 的 `macos` 数组是空的，
/// `macos/Flutter/GeneratedPluginRegistrant.swift` 里也不会注册它 ——
/// 也就是说连 `allTestsFinished` 都是 MissingPlugin，但这不影响测试本身。
///
/// 结论：应用侧必须自己兜底抓帧（`screenshot_test.dart` 里的
/// `_captureFramePng()`，直接用 `RenderView.layer.toImage()`），
/// 并且由应用侧**直接写盘**，不依赖这个回调。本回调仍按标准写法保留，
/// 一旦将来上游补上 macOS 实现，同一条链路会自动开始产出 `driver_*.png`，
/// 两条产物可用像素比对互相验证。
library;

// 驱动侧是 CLI，print 就是它的输出接口（上游 integration_test_driver.dart
// 自己也带 avoid_print 的 ignore）。
// ignore_for_file: avoid_print

import 'dart:io';

import 'package:integration_test/integration_test_driver.dart';

/// 与 `screenshot_test.dart` 里 `screenshotOutputDirectory()` 约定的同一目录。
const String _screenshotDirName = 'build/screenshots';

Future<void> main() async {
  await integrationDriver(
    // 截图在应用侧失败也要把 response data 落下来，便于事后诊断
    writeResponseOnFailure: true,
    responseDataCallback: (Map<String, dynamic>? data) async {
      if (data == null) {
        print('[driver] 应用侧没有回传任何数据（response.data == null）');
        return;
      }

      final Object? screenshots = data['screenshots'];
      if (screenshots is! List) {
        print(
          '[driver] response.data 里没有 screenshots 字段：${data.keys.toList()}',
        );
        return;
      }

      final Directory out = Directory(_screenshotDirName);
      await out.create(recursive: true);

      int written = 0;
      for (final Object? item in screenshots) {
        if (item is! Map) continue;
        final Object? name = item['screenshotName'];
        final Object? bytes = item['bytes'];
        if (name is! String || bytes is! List) continue;

        final File file = File('${out.path}/driver_$name.png');
        await file.writeAsBytes(bytes.cast<int>(), flush: true);
        written++;
        print(
          '[driver] 已写出 ${file.path}（${(bytes.length / 1024).toStringAsFixed(1)} KiB）',
        );
      }

      print('[driver] 共写出 $written 张应用侧截图到 ${out.absolute.path}');
      print('[driver] 另外一份 CSV 数据：integration_response_data.json');
    },
  );
}
