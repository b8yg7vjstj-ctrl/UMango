/// UMango 视觉主题。
///
/// 桌面端取向：偏紧凑的间距、较小的圆角、清晰的层次，不做移动端那种大圆角
/// 与夸张阴影 —— 与"工具型桌面应用"的定位一致。
library;

import 'package:flutter/material.dart';

class AppTheme {
  /// 主色调：偏冷的蓝色，与包管理器工具的气质相符。
  static const Color seed = Color(0xFF1F6FEB);

  static ThemeData light() => _build(Brightness.light);
  static ThemeData dark() => _build(Brightness.dark);

  static ThemeData _build(Brightness brightness) {
    final scheme = ColorScheme.fromSeed(seedColor: seed, brightness: brightness);
    final isLight = brightness == Brightness.light;

    return ThemeData(
      useMaterial3: true,
      colorScheme: scheme,
      scaffoldBackgroundColor: isLight ? const Color(0xFFF6F7F9) : const Color(0xFF16181C),
      visualDensity: VisualDensity.compact,
      splashFactory: InkSparkle.splashFactory,

      // 桌面端默认字体略小一点，信息密度更高
      textTheme: const TextTheme().apply(
        bodyColor: scheme.onSurface,
        displayColor: scheme.onSurface,
      ),

      cardTheme: CardThemeData(
        elevation: 0,
        margin: EdgeInsets.zero,
        color: isLight ? Colors.white : const Color(0xFF1E2126),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(10),
          side: BorderSide(
            color: isLight ? const Color(0xFFE3E6EA) : const Color(0xFF2C3037),
          ),
        ),
      ),

      dividerTheme: DividerThemeData(
        color: isLight ? const Color(0xFFE3E6EA) : const Color(0xFF2C3037),
        space: 1,
        thickness: 1,
      ),

      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: isLight ? Colors.white : const Color(0xFF1E2126),
        isDense: true,
        contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: isLight ? const Color(0xFFD8DDE3) : const Color(0xFF343941)),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: isLight ? const Color(0xFFD8DDE3) : const Color(0xFF343941)),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: scheme.primary, width: 1.6),
        ),
      ),

      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
        ),
      ),

      chipTheme: ChipThemeData(
        side: BorderSide(color: isLight ? const Color(0xFFDDE1E6) : const Color(0xFF343941)),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6)),
        labelStyle: const TextStyle(fontSize: 12),
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      ),

      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: isLight ? const Color(0xE6202329) : const Color(0xF02B2F36),
          borderRadius: BorderRadius.circular(6),
        ),
        textStyle: const TextStyle(color: Colors.white, fontSize: 12),
        waitDuration: const Duration(milliseconds: 400),
      ),

      listTileTheme: const ListTileThemeData(dense: true),
    );
  }

  /// 把 `#RRGGBB` 解析成 Color，解析失败返回兜底色。
  static Color hexColor(String? hex, {Color fallback = const Color(0xFF6B7280)}) {
    if (hex == null || hex.isEmpty) return fallback;
    var value = hex.replaceFirst('#', '').trim();
    if (value.length == 6) value = 'FF$value';
    final parsed = int.tryParse(value, radix: 16);
    return parsed == null ? fallback : Color(parsed);
  }
}
