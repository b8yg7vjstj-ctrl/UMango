/// 工具页：运行诊断 + 架构分层 + 硬性约束 + 开发路线图 + 第三方资源 + 合规声明。
///
/// 本文件是 `docs/DESIGN_PCL_CE.md` 的实现之一：该规格第 6 节的页面映射把
/// PCL 的「工具」标签映射到 [ToolsPage]（新建）。
/// 所有颜色 / 尺寸 / 字号一律取自 `lib/theme/pcl_theme.dart` 的 token
/// （即规格第 1/2/3 节的数值），所有控件一律用 `lib/widgets/pcl_widgets.dart`
/// 的原语 —— 不使用 Material 默认观感，也不新增任何第三方依赖。
///
/// 分区结构（每区一张 [PclPanel] 卡片，标题行 = [PclIcon] + 14 号标题）：
///   1. 运行诊断：`core.info` 键值列表 + 桥接层自检（含加载态与错误分支）
///   2. 架构分层：Flutter / Rust / C++ 三层职责
///   3. 硬性约束（红线）：5 条
///   4. 开发路线图：阶段1 ~ 阶段5
///   5. 第三方资源与许可
///   6. 合规声明（规格第 7 节原文）
///
/// 依赖约束：只依赖 `../ffi` `../models` `../theme` `../widgets` 与 `../animation`，
/// **不 import 任何具体页面**（与 [PclPanel] 等原语的约束保持一致）。
library;

import 'dart:convert';

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../ffi/umango_bridge.dart';
import '../models/umango_models.dart';
import '../theme/pcl_theme.dart';
import '../widgets/pcl_widgets.dart';

// ---------------------------------------------------------------------------
// 规格数值与常量（集中在文件顶部，方便与 docs/DESIGN_PCL_CE.md 对照）
// ---------------------------------------------------------------------------

/// 分区之间的垂直间距 10（与规格第 2 节「外层边距 10」保持同一节奏）。
const double _sectionGap = 10;

/// 分区卡片内部的内容左右内边距 14。
const double _sectionPaddingX = 14;

/// 键值列表的标签列宽 104：足够放下「可用性缓存 TTL」这类最长标签，
/// 固定宽度让所有值左对齐成一列，诊断信息才能竖着扫读。
const double _kvLabelWidth = 104;

/// 路线图阶段列的固定宽度 52：「阶段1」到「阶段5」等宽，后面的标题始终对齐。
const double _phaseLabelWidth = 52;

/// 自检 JSON 块的最大高度 240：超出后在块内滚动，
/// 避免一段长 JSON 把整页撑得失控（自检结果本身可能包含全部检查项）。
const double _jsonBlockMaxHeight = 240;

/// 桥接层报告的「经过 C++ 桥接层」的进程执行器标识。
///
/// 依据 `docs/ROADMAP.md`：如果这里显示的不是 `cpp-bridge`，说明 Rust 侧没有
/// 接受 C++ 的宿主回调 —— 进程调用其实没经过桥接层，只是"看起来能用"。
/// 这类退化不会抛异常也不会崩，所以必须在界面上显式区分出来。
const String _cppBridgeProcessRunner = 'cpp-bridge';

/// 等宽字体候选链（诊断信息用）。
///
/// 不引入新依赖：macOS 用 Menlo、Windows 用 Consolas，其余回落到通用 monospace。
/// JSON 与路径必须等宽展示 —— 它们靠缩进和字符对齐表达结构，
/// 比例字体会让层级变得难以扫读。
const TextStyle _monoStyle = TextStyle(
  fontFamily: 'Menlo',
  fontFamilyFallback: <String>['Consolas', 'Courier New', 'monospace'],
  fontSize: PclTypography.caption,
  height: 1.5,
  color: PclColors.c1,
);

/// 合规声明原文 —— `docs/DESIGN_PCL_CE.md` 第 7 节。
///
/// **逐字照录，不得改写。** 文档里的换行只是排版折行，界面按容器宽度自然换行，
/// 因此这里写成单行字面量，保证声明文本连续、可被逐字核对：
///
/// > UMango 是独立开发的第三方软件，与 PCL / PCL-CE 及其开发者无关联。
/// > 界面设计参照 PCL-CE（`Plain Craft Launcher 2/` 目录适用《PCL 分发有限许可》
/// > 与《PCL 存储库合理使用指南》），本项目未复制其源代码、XAML 或图片资源，
/// > 全部代码自行编写，图标使用 MIT 许可的 lucide。
// ignore: lines_longer_than_80_chars
const String _complianceStatement = 'UMango 是独立开发的第三方软件，与 PCL / PCL-CE 及其开发者无关联。界面设计参照 PCL-CE（Plain Craft Launcher 2/ 目录适用《PCL 分发有限许可》与《PCL 存储库合理使用指南》），本项目未复制其源代码、XAML 或图片资源，全部代码自行编写，图标使用 MIT 许可的 lucide。';

// ---------------------------------------------------------------------------
// 页面
// ---------------------------------------------------------------------------

/// 工具页 —— PCL 四个顶部标签里的第 4 个。
///
/// 外壳按 `const ToolsPage()` 构造（见规格第 6 节的页面映射），
/// 因此构造签名必须是 `const ToolsPage({super.key})`。
class ToolsPage extends StatefulWidget {
  /// 创建工具页。
  const ToolsPage({super.key});

  @override
  State<ToolsPage> createState() => _ToolsPageState();
}

class _ToolsPageState extends State<ToolsPage> {
  /// 最近一次成功读取的核心层信息；null 表示尚未取到。
  CoreInfo? _coreInfo;

  /// 桥接层 ABI 版本；读取失败时为 null（读取失败不应影响整页诊断）。
  int? _abiVersion;

  /// 是否正在读取诊断信息。
  bool _loading = true;

  /// 诊断读取失败时的人类可读说明。
  String? _loadErrorMessage;

  /// [UmangoError] 的机器可读错误码。
  String? _loadErrorCode;

  /// [UmangoError] 给出的兜底建议。
  String? _loadErrorAdvice;

  /// 动态库加载失败时已尝试过的路径（仅 [BridgeLoadException] 有）。
  List<String> _attemptedPaths = const <String>[];

  /// 桥接层自检是否正在执行。
  bool _selfTesting = false;

  /// 自检返回的原始 JSON（缩进后展示）。
  String? _selfTestJson;

  /// 自检失败说明。
  String? _selfTestError;

  @override
  void initState() {
    super.initState();
    _loadDiagnostics();
  }

  /// 读取核心层信息（`core.info`）与 ABI 版本。
  ///
  /// 错误分支与其它页面保持一致：优先识别 [UmangoError]（带 code / advice），
  /// 再处理动态库缺失 [BridgeLoadException]（带已尝试路径），最后兜底任意异常。
  Future<void> _loadDiagnostics() async {
    setState(() {
      _loading = true;
      _loadErrorMessage = null;
      _loadErrorCode = null;
      _loadErrorAdvice = null;
      _attemptedPaths = const <String>[];
    });

    try {
      // 注意：instance 本身就可能抛 BridgeLoadException（动态库没构建或没找到）
      final bridge = UmangoBridge.instance;
      final CoreInfo info = await bridge.coreInfo();

      // ABI 版本是桥接层句柄上的同步 getter，与 core.info 是两套来源；
      // 单独 try 是因为它读不到时不应该让整个诊断面板变成错误态。
      int? abi;
      try {
        abi = bridge.abiVersion;
      } catch (_) {
        abi = null;
      }

      if (!mounted) return;
      setState(() {
        _coreInfo = info;
        _abiVersion = abi;
        _loading = false;
      });
    } on UmangoError catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _loadErrorMessage = e.message;
        _loadErrorCode = e.code;
        _loadErrorAdvice = e.advice.displayName;
      });
    } on BridgeLoadException catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _loadErrorMessage = e.message;
        _attemptedPaths = e.attemptedPaths;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _loadErrorMessage = '$e';
      });
    }
  }

  /// 运行桥接层自检（`umb_bridge_self_test`），把原始 JSON 展示出来。
  Future<void> _runSelfTest() async {
    setState(() {
      _selfTesting = true;
      _selfTestJson = null;
      _selfTestError = null;
    });

    try {
      final Map<String, dynamic> result = await UmangoBridge.instance.selfTest();
      if (!mounted) return;
      setState(() {
        _selfTesting = false;
        // 缩进后的 JSON 才是可读的：自检结果是一棵嵌套结构（含各检查项与 ABI 信息）
        _selfTestJson = const JsonEncoder.withIndent('  ').convert(result);
      });
    } on UmangoError catch (e) {
      if (!mounted) return;
      setState(() {
        _selfTesting = false;
        _selfTestError =
            '${e.code}：${e.message}（建议：${e.advice.displayName}）';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _selfTesting = false;
        _selfTestError = '$e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    // 各分区依次入场，方向与延迟由全局动画配置决定（设置页可切换），
    // 因此这里只提供序号，不自行写死任何动画参数。
    return ListView(
      padding: const EdgeInsets.fromLTRB(14, 12, 14, 24),
      children: <Widget>[
        ListEntrance(index: 0, child: _buildDiagnosticsSection()),
        const SizedBox(height: _sectionGap),
        ListEntrance(index: 1, child: _buildArchitectureSection()),
        const SizedBox(height: _sectionGap),
        ListEntrance(index: 2, child: _buildConstraintsSection()),
        const SizedBox(height: _sectionGap),
        ListEntrance(index: 3, child: _buildRoadmapSection()),
        const SizedBox(height: _sectionGap),
        ListEntrance(index: 4, child: _buildResourcesSection()),
        const SizedBox(height: _sectionGap),
        ListEntrance(index: 5, child: _buildComplianceSection()),
      ],
    );
  }

  // -------------------------------------------------------------------------
  // 1. 运行诊断
  // -------------------------------------------------------------------------

  Widget _buildDiagnosticsSection() {
    final CoreInfo? info = _coreInfo;
    final String? errorMessage = _loadErrorMessage;

    return _PclSection(
      icon: 'gauge',
      title: '运行诊断',
      trailing: PclIconButton(
        icon: 'refresh-cw',
        size: PclMetrics.iconButtonSize,
        tooltip: '重新读取诊断信息',
        onPressed: _loading ? null : _loadDiagnostics,
      ),
      children: <Widget>[
        if (_loading)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 4),
            child: Row(
              children: <Widget>[
                SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
                SizedBox(width: 8),
                Text('正在读取核心层信息…', style: PclTypography.captionStyle),
              ],
            ),
          )
        else if (errorMessage != null)
          _ErrorBar(
            message: errorMessage,
            code: _loadErrorCode,
            advice: _loadErrorAdvice,
            attemptedPaths: _attemptedPaths,
            onRetry: _loadDiagnostics,
          )
        else if (info != null)
          ..._diagnosticRows(info),

        const SizedBox(height: 12),
        const _HairLine(),
        const SizedBox(height: 10),

        // ---- 桥接层自检 ----
        Row(
          crossAxisAlignment: CrossAxisAlignment.center,
          children: <Widget>[
            PclButton(
              label: _selfTesting ? '自检中…' : '桥接层自检',
              icon: 'flask-conical',
              dense: true,
              onPressed: _selfTesting ? null : _runSelfTest,
            ),
            const SizedBox(width: 8),
            const Expanded(
              child: Text(
                '调用 umb_bridge_self_test，逐项校验 C++ 与 Rust 两层的 ABI 一致性',
                style: PclTypography.captionStyle,
              ),
            ),
          ],
        ),
        if (_selfTestError != null) ...<Widget>[
          const SizedBox(height: 8),
          _InlineError(message: _selfTestError!),
        ],
        if (_selfTestJson != null) ...<Widget>[
          const SizedBox(height: 8),
          const Text('自检结果（原始 JSON）', style: PclTypography.captionStyle),
          const SizedBox(height: 4),
          _MonoBlock(text: _selfTestJson!),
        ],
      ],
    );
  }

  /// 诊断键值行：核心层版本 / 宿主平台 / 架构 / ABI 版本 / 缓存 TTL / 进程执行器。
  List<Widget> _diagnosticRows(CoreInfo info) {
    final String runner = info.processRunner;
    // 只有真的走 C++ 宿主回调链路时才用主强调色高亮，否则如实降级为次级文字色。
    final bool viaCppBridge = runner == _cppBridgeProcessRunner;

    return <Widget>[
      _KvRow('核心层版本', info.coreVersion),
      _KvRow('宿主平台', '${info.platformDisplay}（${info.platform}）'),
      _KvRow('架构', info.arch),
      _KvRow(
        'ABI 版本',
        _abiVersion?.toString() ?? '读取失败',
        note: '对应 native/include/umango_bridge.h 的 UMANGO_ABI_VERSION；'
            '两端不匹配时桥接层会直接拒绝初始化',
      ),
      _KvRow(
        '可用性缓存 TTL',
        _formatTtl(info.availabilityTtlSecs),
        note: '包源可用性检测结果的缓存时长，过期后重新探测（探测会真的执行 CLI）',
      ),
      _KvRow(
        '进程执行器',
        runner,
        valueColor: viaCppBridge ? PclColors.c3 : PclColors.gray2,
        note: viaCppBridge
            ? '进程调用确实经过 C++ 桥接层'
            : '未经过 C++ 桥接层：纯 Rust 执行器只用于单元测试与开发，'
                '发布路径上应当显示 cpp-bridge',
        noteColor: viaCppBridge ? PclColors.c3 : PclColors.gray3,
      ),
    ];
  }

  // -------------------------------------------------------------------------
  // 2. 架构分层
  // -------------------------------------------------------------------------

  Widget _buildArchitectureSection() {
    return const _PclSection(
      icon: 'layers',
      title: '架构分层',
      children: <Widget>[
        _LayerRow(
          tag: 'Flutter',
          name: 'Flutter 前端层',
          detail: '界面渲染、动画引擎、交互逻辑与全局动画配置。业务数据一律经 '
              'UmangoBridge 的 C ABI 取得，页面里不直接解析 JSON。',
        ),
        _LayerRow(
          tag: 'Rust',
          name: 'Rust 核心业务层',
          detail: '领域模型、包源适配器、版本解析（复用 node-semver）、依赖树计算、'
              '24 小时可用性缓存、JSON API 分发，阶段4 起还负责插件调度。'
              '它不直接创建进程，也不做文件 IO —— 因此业务逻辑可以脱离 GUI 完整单测。',
        ),
        _LayerRow(
          tag: 'C++',
          name: 'C++ 桥接层',
          detail: '进程创建（posix_spawn / CreateProcess）、本地文件读写、系统信息、'
              'ABI 版本校验、UTF-8 净化与 JSON 写入。Rust 只用 ProcessRunner '
              '描述"要跑什么命令"，真正执行的是这一层。',
        ),
        SizedBox(height: 4),
        Text(
          '调用链：Dart —(dart:ffi C ABI)→ C++ —(静态链接 libumango_ffi.a)→ Rust；'
          'Rust 需要执行命令时反向回调 C++ 的宿主函数表 umango_host_vtable，'
          '所以发布路径上的进程执行器应当显示 cpp-bridge。',
          style: PclTypography.captionStyle,
        ),
      ],
    );
  }

  // -------------------------------------------------------------------------
  // 3. 硬性约束（红线）
  // -------------------------------------------------------------------------

  Widget _buildConstraintsSection() {
    return const _PclSection(
      icon: 'triangle-alert',
      title: '硬性约束（红线）',
      children: <Widget>[
        _RuleRow(
          index: 1,
          title: '代码隔离',
          detail: '仅观摩 PCL-CE 的交互设计，业务代码全部手写，'
              '不复制其源码、XAML 文本与图片资源。',
        ),
        _RuleRow(
          index: 2,
          title: '插件模块例外',
          detail: '插件解析／导入模块允许复用 HMCL-CE / PCL-CE 的插件加载代码，'
              '但必须单独隔离存放（阶段4 落地）。',
        ),
        _RuleRow(
          index: 3,
          title: '技术栈',
          detail: 'Rust + C++ + Flutter，不使用 Electron 网页套壳。',
        ),
        _RuleRow(
          index: 4,
          title: '后端剥离 MC',
          detail: '核心层不含任何 Minecraft 启动与资源下载逻辑，只保留包管理业务。',
        ),
        _RuleRow(
          index: 5,
          title: '动画向量改造',
          detail: '列表入场改为纵向、弹窗改为向右滑出，'
              '降低与 PCL-CE 在视觉代码上的相似度。',
        ),
      ],
    );
  }

  // -------------------------------------------------------------------------
  // 4. 开发路线图
  // -------------------------------------------------------------------------

  Widget _buildRoadmapSection() {
    return const _PclSection(
      icon: 'list-tree',
      title: '开发路线图',
      children: <Widget>[
        _PhaseRow(
          phase: '阶段1',
          title: '三层架构骨架与只读能力',
          done: true,
          detail: '三层架构打通（Flutter ⇄ C++ ⇄ Rust）+ 包源列表与平台门控、'
              '搜索、版本列表（通道区分）、依赖树可视化、下载量官方优先 / '
              'GitHub Stars 兜底、全局动画配置系统。',
        ),
        _PhaseRow(
          phase: '阶段2',
          title: '适配器深化',
          done: false,
          detail: 'git 分支读取与非 GitHub 主机、npm dist-tags 多通道展示、'
              'Homebrew cask、适配器级错误分类细化、Windows 真机验证 winget / scoop。',
        ),
        _PhaseRow(
          phase: '阶段3',
          title: '安装 / 卸载 / 升级',
          done: false,
          detail: 'packages.install 走 C++ run_process 流式输出、stdout 逐行推送进度、'
              '已安装检测与过期提示、危险操作二次确认。',
        ),
        _PhaseRow(
          phase: '阶段4',
          title: '插件系统',
          done: false,
          detail: 'Cordis 风格插件内核（生命周期、钩子、事件总线、沙箱）、'
              'PCL-CE 插件格式兼容导入（隔离存放）、扩展点：新增包源适配器与自定义面板。',
        ),
        _PhaseRow(
          phase: '阶段5',
          title: '全量测试与兜底完善',
          done: false,
          detail: '网络异常 / 源不可用 / CLI 缺失的系统性覆盖、依赖树冲突场景、'
              '长列表与深依赖树性能验证、接入 CI。',
        ),
      ],
    );
  }

  // -------------------------------------------------------------------------
  // 5. 第三方资源与许可
  // -------------------------------------------------------------------------

  Widget _buildResourcesSection() {
    return const _PclSection(
      icon: 'scroll',
      title: '第三方资源与许可',
      children: <Widget>[
        _ResourceRow(
          name: 'npm / pnpm / git / Homebrew 图标',
          license: 'CC0 1.0',
          detail: '来自 simple-icons（CC0 1.0，公有领域），用于包源图标渲染。',
        ),
        _ResourceRow(
          name: '界面图标',
          license: 'MIT',
          detail: '来自 lucide（MIT）。它是通用开源图标集，不是 PCL-CE 的资源，'
              '与 PCL-CE / Minecraft 官方无隶属或授权关系。',
        ),
        _ResourceRow(
          name: '版本范围解析',
          license: '复用 crate',
          detail: '复用 node-semver（Rust crate），未自行重造语义化版本匹配逻辑。',
        ),
        _ResourceRow(
          name: 'winget / scoop 图标',
          license: '自绘',
          detail: '这两个包源没有可用的官方图标，界面使用自绘字母徽章，'
              '未使用任何来源不明的商标资源。',
        ),
      ],
    );
  }

  // -------------------------------------------------------------------------
  // 6. 合规声明
  // -------------------------------------------------------------------------

  Widget _buildComplianceSection() {
    return const _PclSection(
      icon: 'info',
      title: '合规声明',
      children: <Widget>[
        Text(_complianceStatement, style: _complianceStyle),
        SizedBox(height: 6),
        Text(
          '本节文字逐字照录 docs/DESIGN_PCL_CE.md 第 7 节，不得改写。',
          style: PclTypography.metaStyle,
        ),
      ],
    );
  }
}

// ---------------------------------------------------------------------------
// 样式派生
// ---------------------------------------------------------------------------

/// 合规声明正文：12 号（说明层级）+ 行高 1.7，用主要文字色保证可读性 ——
/// 这段是法律性声明，不能用次级灰糊弄过去。
const TextStyle _complianceStyle = TextStyle(
  fontSize: PclTypography.caption,
  fontFamilyFallback: PclTypography.fontFallback,
  color: PclColors.gray1,
  height: 1.7,
);

/// 把缓存秒数转成诊断用的可读文本。
///
/// 整小时时补一个换算：默认的 24 小时缓存写成 `86400 秒` 时，
/// 排查的人还得自己除一遍才知道是多久。
String _formatTtl(int seconds) {
  if (seconds <= 0) {
    return '未设置（0 秒）';
  }
  if (seconds % 3600 == 0) {
    return '$seconds 秒（${seconds ~/ 3600} 小时）';
  }
  return '$seconds 秒';
}

// ---------------------------------------------------------------------------
// 私有组件
// ---------------------------------------------------------------------------

/// 分区卡片：统一「标题行 + 细分隔线 + 内容」的结构。
///
/// 规格第 6 节要求标签页内容按卡片分区，标题行是 lucide 图标 + 14 号标题
/// （[PclTypography.cardTitle]）。这里把重复结构收敛到一个组件，
/// 避免各分区各自发挥导致观感不一致。
class _PclSection extends StatelessWidget {
  const _PclSection({
    required this.icon,
    required this.title,
    required this.children,
    this.trailing,
  });

  /// 标题左侧的 lucide 图标名。
  final String icon;

  /// 标题文字（14 号）。
  final String title;

  /// 卡片内容。
  final List<Widget> children;

  /// 标题行右侧的附加控件（例如刷新按钮）。
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    return PclPanel(
      // semiWhite（#bbffffff）面板底 + gray5 1px 描边：卡片放在白底内容区上时，
      // 半透明白与背景同为浅色，只有靠 1px 描边才能看出卡片边界。
      color: PclColors.semiWhite,
      border: Border.all(
        color: PclColors.gray5,
        width: PclMetrics.buttonBorderWidth,
      ),
      padding: const EdgeInsets.fromLTRB(
        _sectionPaddingX,
        11,
        _sectionPaddingX,
        13,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Row(
            children: <Widget>[
              PclIcon(name: icon, size: 16, color: PclColors.c3),
              const SizedBox(width: 6),
              Expanded(
                child: Text(title, style: PclTypography.cardTitleStyle),
              ),
              // 空安全元素：没有右侧控件时该项直接省略（与 pcl_widgets 的写法一致）
              ?trailing,
            ],
          ),
          const SizedBox(height: 8),
          const _HairLine(),
          const SizedBox(height: 10),
          ...children,
        ],
      ),
    );
  }
}

/// 1px 浅分隔线（gray6，与主题的 dividerTheme 同色，用于卡片内部的层级切分）。
class _HairLine extends StatelessWidget {
  const _HairLine();

  @override
  Widget build(BuildContext context) {
    return Container(height: 1, color: PclColors.gray6);
  }
}

/// 诊断键值行：左侧固定宽度标签（12 号 gray2）+ 右侧值（13 号 c1）+ 可选行内说明。
///
/// [valueColor] 非空时值加粗：只有需要引起注意的项（例如真的走了 C++ 桥接层的
/// 进程执行器）才变色，否则诊断列表会满屏都是颜色，等于没有重点。
class _KvRow extends StatelessWidget {
  const _KvRow(
    this.label,
    this.value, {
    this.valueColor,
    this.note,
    this.noteColor,
  });

  /// 左侧标签。
  final String label;

  /// 右侧主值。
  final String value;

  /// 值颜色（非空即加粗强调）。
  final Color? valueColor;

  /// 值下方的说明文字。
  final String? note;

  /// 说明文字颜色，默认 gray3。
  final Color? noteColor;

  @override
  Widget build(BuildContext context) {
    final String? noteText = note;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          SizedBox(
            width: _kvLabelWidth,
            child: Text(
              label,
              style: PclTypography.captionStyle.copyWith(
                color: PclColors.gray2,
              ),
            ),
          ),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Text(
                  value,
                  style: TextStyle(
                    fontSize: PclTypography.body,
                    fontFamilyFallback: PclTypography.fontFallback,
                    color: valueColor ?? PclColors.c1,
                    fontWeight: valueColor == null
                        ? FontWeight.w400
                        : FontWeight.w600,
                    height: 1.35,
                  ),
                ),
                if (noteText != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 2),
                    child: Text(
                      noteText,
                      style: TextStyle(
                        fontSize: PclTypography.caption,
                        fontFamilyFallback: PclTypography.fontFallback,
                        color: noteColor ?? PclColors.gray3,
                        height: 1.45,
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// 架构分层的一行：技术标签 + 层名 （13 号）+ 职责说明（12 号 gray3）。
class _LayerRow extends StatelessWidget {
  const _LayerRow({
    required this.tag,
    required this.name,
    required this.detail,
  });

  /// 技术栈标签文字（Flutter / Rust / C++）。
  final String tag;

  /// 层名。
  final String name;

  /// 职责说明。
  final String detail;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Row(
            children: <Widget>[
              Flexible(
                child: Text(
                  name,
                  style: TextStyle(
                    fontSize: PclTypography.body,
                    fontFamilyFallback: PclTypography.fontFallback,
                    color: PclColors.c1,
                    fontWeight: FontWeight.w600,
                    height: 1.3,
                  ),
                ),
              ),
              const SizedBox(width: 6),
              PclTag(text: tag),
            ],
          ),
          const SizedBox(height: 3),
          Text(
            detail,
            style: PclTypography.captionStyle.copyWith(height: 1.6),
          ),
        ],
      ),
    );
  }
}

/// 红线的一条：序号徽章 + 标题 + 说明。
class _RuleRow extends StatelessWidget {
  const _RuleRow({
    required this.index,
    required this.title,
    required this.detail,
  });

  /// 序号（1 起）。
  final int index;

  /// 约束名称。
  final String title;

  /// 约束说明。
  final String detail;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          _IndexBadge(index: index),
          const SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Text(
                  title,
                  style: TextStyle(
                    fontSize: PclTypography.body,
                    fontFamilyFallback: PclTypography.fontFallback,
                    color: PclColors.c1,
                    fontWeight: FontWeight.w600,
                    height: 1.3,
                  ),
                ),
                const SizedBox(height: 3),
                Text(
                  detail,
                  style: PclTypography.captionStyle.copyWith(height: 1.6),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// 序号徽章：16 × 16、圆角 3（[PclMetrics.tagRadius]）、c6 底 + c2 字 11 号，
/// 与 [PclTag] 同一套观感，只是把内容固定成一位数字。
class _IndexBadge extends StatelessWidget {
  const _IndexBadge({required this.index});

  /// 序号。
  final int index;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 16,
      height: 16,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: PclColors.c6,
        borderRadius: BorderRadius.circular(PclMetrics.tagRadius),
      ),
      child: Text(
        '$index',
        style: const TextStyle(
          fontSize: PclTypography.label,
          fontFamilyFallback: PclTypography.fontFallback,
          color: PclColors.c2,
          height: 1.0,
        ),
      ),
    );
  }
}

/// 路线图的一行：阶段列（固定宽）+ 标题 + 状态标签 + 内容。
class _PhaseRow extends StatelessWidget {
  const _PhaseRow({
    required this.phase,
    required this.title,
    required this.done,
    required this.detail,
  });

  /// 阶段标识（阶段1 … 阶段5）。
  final String phase;

  /// 阶段主题。
  final String title;

  /// 是否已完成。
  final bool done;

  /// 阶段内容。
  final String detail;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          SizedBox(
            width: _phaseLabelWidth,
            child: Text(
              phase,
              style: TextStyle(
                fontSize: PclTypography.body,
                fontFamilyFallback: PclTypography.fontFallback,
                color: PclColors.c1,
                fontWeight: FontWeight.w600,
                height: 1.3,
              ),
            ),
          ),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    Flexible(
                      child: Text(
                        title,
                        style: TextStyle(
                          fontSize: PclTypography.body,
                          fontFamilyFallback: PclTypography.fontFallback,
                          color: PclColors.c1,
                          height: 1.3,
                        ),
                      ),
                    ),
                    const SizedBox(width: 6),
                    // 已完成用主强调色底 + 白字，未开始的阶段用默认浅蓝底，
                    // 这样"当前进度"在卡片里一眼可见。
                    if (done)
                      const PclTag(
                        text: '已完成',
                        color: Colors.white,
                        background: PclColors.c3,
                      )
                    else
                      const PclTag(text: '计划中'),
                  ],
                ),
                const SizedBox(height: 3),
                Text(
                  detail,
                  style: PclTypography.captionStyle.copyWith(height: 1.6),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// 第三方资源的一行：资源名 + 许可标签 + 来源说明。
class _ResourceRow extends StatelessWidget {
  const _ResourceRow({
    required this.name,
    required this.license,
    required this.detail,
  });

  /// 资源名。
  final String name;

  /// 许可标签文字。
  final String license;

  /// 来源与用途说明。
  final String detail;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Row(
            children: <Widget>[
              Flexible(
                child: Text(
                  name,
                  style: TextStyle(
                    fontSize: PclTypography.body,
                    fontFamilyFallback: PclTypography.fontFallback,
                    color: PclColors.c1,
                    height: 1.3,
                  ),
                ),
              ),
              const SizedBox(width: 6),
              PclTag(text: license),
            ],
          ),
          const SizedBox(height: 3),
          Text(
            detail,
            style: PclTypography.captionStyle.copyWith(height: 1.6),
          ),
        ],
      ),
    );
  }
}

/// 错误条：`redBack` 底 + `redLight` 描边 + [PclPanel] 圆角，
/// 内含原因、[UmangoError] 的 code / advice（或动态库加载时尝试过的路径）与重试按钮。
///
/// 规格第 1 节把 `redBack` 定义为红底（错误面板背景），因此错误一律用这个组合，
/// 而不是 Material 的默认错误色。
class _ErrorBar extends StatelessWidget {
  const _ErrorBar({
    required this.message,
    this.code,
    this.advice,
    this.attemptedPaths = const <String>[],
    this.onRetry,
  });

  /// 错误说明（来自 `UmangoError.message` 或异常字符串）。
  final String message;

  /// `UmangoError.code`。
  final String? code;

  /// `UmangoError.advice`（已转成中文显示名）。
  final String? advice;

  /// 动态库加载失败时已尝试过的路径。
  final List<String> attemptedPaths;

  /// 重试回调。
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    final String? codeText = code;
    final String? adviceText = advice;
    final VoidCallback? retry = onRetry;
    final bool hasPaths = attemptedPaths.isNotEmpty;

    return PclPanel(
      color: PclColors.redBack,
      border: Border.all(
        color: PclColors.redLight,
        width: PclMetrics.buttonBorderWidth,
      ),
      padding: const EdgeInsets.fromLTRB(10, 9, 10, 9),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              const PclIcon(
                name: 'triangle-alert',
                size: 16,
                color: PclColors.redDark,
              ),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  message,
                  style: const TextStyle(
                    fontSize: PclTypography.body,
                    fontFamilyFallback: PclTypography.fontFallback,
                    color: PclColors.redDark,
                    height: 1.45,
                  ),
                ),
              ),
            ],
          ),
          if (codeText != null || adviceText != null) ...<Widget>[
            const SizedBox(height: 7),
            Row(
              crossAxisAlignment: CrossAxisAlignment.center,
              children: <Widget>[
                if (codeText != null)
                  PclTag(
                    text: '错误码 $codeText',
                    color: PclColors.redDark,
                    background: Colors.white,
                  ),
                if (codeText != null && adviceText != null)
                  const SizedBox(width: 6),
                if (adviceText != null)
                  Flexible(
                    child: Text(
                      '建议：$adviceText',
                      style: const TextStyle(
                        fontSize: PclTypography.caption,
                        fontFamilyFallback: PclTypography.fontFallback,
                        color: PclColors.redDark,
                        height: 1.3,
                      ),
                    ),
                  ),
                const Spacer(),
                if (retry != null)
                  PclButton(
                    label: '重试',
                    icon: 'refresh-cw',
                    dense: true,
                    onPressed: retry,
                  ),
              ],
            ),
          ] else if (retry != null) ...<Widget>[
            const SizedBox(height: 7),
            Align(
              alignment: Alignment.centerLeft,
              child: PclButton(
                label: '重试',
                icon: 'refresh-cw',
                dense: true,
                onPressed: retry,
              ),
            ),
          ],
          if (hasPaths) ...<Widget>[
            const SizedBox(height: 7),
            const Text(
              '已尝试加载的动态库路径：',
              style: TextStyle(
                fontSize: PclTypography.caption,
                fontFamilyFallback: PclTypography.fontFallback,
                color: PclColors.redDark,
                height: 1.3,
              ),
            ),
            const SizedBox(height: 4),
            // 路径必须等宽展示：判断"是路径写错了还是根本没构建"全靠逐行比对
            _MonoBlock(
              text: attemptedPaths.map((String p) => '· $p').join('\n'),
              maxHeight: 140,
            ),
          ],
        ],
      ),
    );
  }
}

/// 自检失败时的一行内错误提示（比整块错误条轻，因为按钮本身就是重试入口）。
class _InlineError extends StatelessWidget {
  const _InlineError({required this.message});

  /// 错误说明。
  final String message;

  @override
  Widget build(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        const PclIcon(
          name: 'triangle-alert',
          size: 14,
          color: PclColors.redDark,
        ),
        const SizedBox(width: 6),
        Expanded(
          child: Text(
            message,
            style: const TextStyle(
              fontSize: PclTypography.caption,
              fontFamilyFallback: PclTypography.fontFallback,
              color: PclColors.redDark,
              height: 1.45,
            ),
          ),
        ),
      ],
    );
  }
}

/// 等宽字体块：浅底（gray8）+ gray5 描边 + 3 圆角（[PclMetrics.buttonRadius]）。
///
/// 内容用 [SelectableText]：诊断信息是要被复制出来贴到 issue 里的，
/// 不可选中的文本会逼着用户手抄。
class _MonoBlock extends StatelessWidget {
  const _MonoBlock({required this.text, this.maxHeight = _jsonBlockMaxHeight});

  /// 块内文本。
  final String text;

  /// 最大高度，超出后在块内滚动。
  final double maxHeight;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.fromLTRB(10, 8, 10, 8),
      decoration: BoxDecoration(
        color: PclColors.gray8,
        borderRadius: BorderRadius.circular(PclMetrics.buttonRadius),
        border: Border.all(
          color: PclColors.gray5,
          width: PclMetrics.buttonBorderWidth,
        ),
      ),
      child: ConstrainedBox(
        constraints: BoxConstraints(maxHeight: maxHeight),
        child: SingleChildScrollView(
          child: SelectableText(text, style: _monoStyle),
        ),
      ),
    );
  }
}
