/// 动画配置系统的行为测试。
///
/// 重点验证需求点名的两个向量：
///   · 列表入场 = 纵向（PCL-CE 原版是横向）
///   · 弹窗弹出 = 向右（PCL-CE 原版是向左）
/// 这两条一旦被改错，界面的"视觉差异化"就不成立了，所以用测试钉住。
library;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:umango/animation/animation_config.dart';
import 'package:umango/animation/motion_widgets.dart';

void main() {
  group('默认配置（UMango 要求的向量）', () {
    test('列表入场默认为纵向', () {
      const config = AnimationConfig();
      expect(config.listEntrance, ListEntranceStyle.verticalStagger);
      expect(config.listEntrance.isVertical, isTrue);
    });

    test('弹窗弹出默认为向右滑出（自左向右滑入）', () {
      const config = AnimationConfig();
      expect(config.dialogMotion, DialogMotionStyle.slideRightward);
      expect(config.dialogBeginOffset(), const Offset(-1, 0));
    });

    test('默认纵向入场的起点在下方（自下而上）', () {
      const config = AnimationConfig();
      final begin = config.beginOffset();
      expect(begin.dx, 0, reason: '纵向入场不应有横向位移');
      expect(begin.dy, greaterThan(0), reason: '应从下方进入');
    });
  });

  group('与 PCL-CE 原版互为镜像', () {
    test('原版预设是横向 + 向左，与默认正好相反', () {
      final classic = AnimationPreset.classic.config;
      const umango = AnimationConfig();

      expect(classic.listEntrance, ListEntranceStyle.horizontalStagger);
      expect(classic.dialogMotion, DialogMotionStyle.slideLeftward);
      expect(classic.dialogBeginOffset(), const Offset(1, 0));
      expect(
        classic.dialogBeginOffset().dx.sign,
        -umango.dialogBeginOffset().dx.sign,
      );

      // 注意不要直接比较两者 dx 的符号：纵向风格 dx 恒为 0，符号比较没有意义。
      expect(classic.beginOffset().dx, lessThan(0), reason: '原版自左进入');
      expect(umango.beginOffset().dx, 0);
      expect(umango.beginOffset().dy, greaterThan(0));
    });

    test('横向风格的两个方向互为反向', () {
      const fromLeft =
          AnimationConfig(listEntrance: ListEntranceStyle.horizontalStagger);
      const fromRight =
          AnimationConfig(listEntrance: ListEntranceStyle.horizontalFromRight);
      expect(fromLeft.beginOffset().dx, lessThan(0));
      expect(fromRight.beginOffset().dx, greaterThan(0));
    });
  });

  group('逐条延迟封顶', () {
    test('前 14 项线性递增', () {
      const config = AnimationConfig(staggerStep: Duration(milliseconds: 10));
      expect(config.delayForIndex(0), Duration.zero);
      expect(config.delayForIndex(1), const Duration(milliseconds: 10));
      expect(config.delayForIndex(5), const Duration(milliseconds: 50));
    });

    test('超过 14 项后不再累加（长列表不能被延迟拖垮）', () {
      const config = AnimationConfig(staggerStep: Duration(milliseconds: 10));
      final atCap = config.delayForIndex(14);
      expect(config.delayForIndex(15), atCap);
      expect(config.delayForIndex(500), atCap);
      expect(atCap, const Duration(milliseconds: 140));
    });

    test('依赖树几百个节点时最后一项的等待仍可接受', () {
      const config = AnimationConfig();
      expect(config.delayForIndex(400).inMilliseconds, lessThan(1000));
    });
  });

  group('预设与配置匹配', () {
    test('每个预设都能被自身识别出来', () {
      for (final preset in AnimationPreset.values) {
        expect(preset.config.matchingPreset, preset,
            reason: '${preset.displayName} 应能识别自身');
      }
    });

    test('自定义组合不匹配任何预设', () {
      const custom = AnimationConfig(
        listEntrance: ListEntranceStyle.horizontalFromRight,
        dialogMotion: DialogMotionStyle.scaleUp,
      );
      expect(custom.matchingPreset, isNull);
    });

    test('关闭预设确实全部禁用', () {
      final off = AnimationPreset.off.config;
      expect(off.enabled, isFalse);
      expect(off.listEntrance, ListEntranceStyle.none);
      expect(off.dialogMotion, DialogMotionStyle.none);
    });
  });

  group('copyWith 与相等性', () {
    test('只改指定字段，其余保持不变', () {
      const base = AnimationConfig();
      final changed = base.copyWith(
        listEntrance: ListEntranceStyle.fadeOnly,
        staggerStep: const Duration(milliseconds: 5),
      );
      expect(changed.listEntrance, ListEntranceStyle.fadeOnly);
      expect(changed.staggerStep, const Duration(milliseconds: 5));
      expect(changed.dialogMotion, base.dialogMotion);
      expect(changed.enabled, base.enabled);
      expect(changed.offsetExtent, base.offsetExtent);
    });

    test('相等性基于全部字段', () {
      const a = AnimationConfig();
      const b = AnimationConfig();
      expect(a, equals(b));
      expect(a.hashCode, equals(b.hashCode));
      expect(a.copyWith(enabled: false), isNot(equals(a)));
    });
  });

  group('AnimationScope', () {
    testWidgets('子树能读到配置，修改能回传', (tester) async {
      var captured = const AnimationConfig();
      late AnimationConfig readBack;

      await tester.pumpWidget(
        AnimationScope(
          config:
              const AnimationConfig(listEntrance: ListEntranceStyle.fadeOnly),
          onChanged: (c) => captured = c,
          child: Builder(builder: (context) {
            readBack = AnimationScope.of(context);
            return const SizedBox();
          }),
        ),
      );

      expect(readBack.listEntrance, ListEntranceStyle.fadeOnly);
      AnimationScope.update(
        tester.element(find.byType(SizedBox)),
        readBack.copyWith(dialogMotion: DialogMotionStyle.scaleUp),
      );
      expect(captured.dialogMotion, DialogMotionStyle.scaleUp);
    });

    testWidgets('没有祖先 Scope 时返回默认配置而不抛异常', (tester) async {
      late AnimationConfig config;
      await tester.pumpWidget(
        Builder(builder: (context) {
          config = AnimationScope.of(context);
          return const SizedBox();
        }),
      );
      expect(config, const AnimationConfig());
    });
  });

  group('无障碍与"关闭就真不动"', () {
    testWidgets('系统要求减少动态效果时被识别', (tester) async {
      late bool prefersReduced;
      await tester.pumpWidget(
        MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: Builder(builder: (context) {
            prefersReduced = AnimationScope.prefersReducedMotion(context);
            return const SizedBox();
          }),
        ),
      );
      expect(prefersReduced, isTrue);
    });

    testWidgets('关闭动画时不做偷偷的动效', (tester) async {
      await tester.pumpWidget(
        AnimationScope(
          config: const AnimationConfig(
            enabled: false,
            listEntrance: ListEntranceStyle.none,
            dialogMotion: DialogMotionStyle.none,
          ),
          onChanged: (_) {},
          child: const Directionality(
            textDirection: TextDirection.ltr,
            child: ListEntrance(index: 3, child: Text('项目')),
          ),
        ),
      );
      await tester.pump();

      expect(find.text('项目'), findsOneWidget);
      // 关闭时必须直接返回原 child，不套任何位移/透明度包装
      expect(find.byType(SlideTransition), findsNothing);
      expect(find.byType(FadeTransition), findsNothing);
    });
  });
}
