# 动画规范

## 一、需求与实现对照

| 项目 | PCL-CE 原版 | UMango 默认 | 实现位置 |
|------|-------------|-------------|----------|
| 列表入场 | 横向进入 | **纵向进入**（自下而上） | `ListEntranceStyle.verticalStagger` |
| 弹窗弹出 | 向左滑出 | **向右滑出**（自左向右滑入） | `DialogMotionStyle.slideRightward` |

保留 PCL-CE 的页面结构与交互逻辑，只改**动画向量** ——
这既能降低视觉层面的代码相似度，也是真实可用的偏好项。

## 二、关于"向右滑出"的向量定义（需要确认的一点）

镜像一个位移向量有两种可能理解：

- **理解 A**「自左向右滑入」：`begin = Offset(-1, 0)` → `Offset.zero`，
  即弹窗从左侧出现、向右移动。这才是 PCL-CE「向左滑出」的真正镜像。
- **理解 B**「从右侧出现」：`begin = Offset(+1, 0)`，与"向左"同向，不属于镜像。

**当前实现取理解 A**（`DialogMotionStyle.slideRightward`）。
如果你的本意是理解 B，把设置页的弹窗风格切成「从右侧滑入」
（`DialogMotionStyle.slideFromRight`）即可，无需改代码。
——这也是把这套向量做成可配置项的价值之一。

同理，列表入场也提供了「横向从右进入」等选项，可自由组合。

## 三、可切换的风格

### 预设（设置页一键切换）

| 预设 | 列表入场 | 弹窗 |
|------|----------|------|
| **UMango 默认** | 纵向逐条进入 | 向右滑出 |
| PCL-CE 原版向量 | 横向逐条进入 | 向左滑出 |
| 轻柔 | 仅淡入 | 缩放弹出 |
| 关闭动画 | 无 | 无 |

### 单项可调

- **列表入场方向**：纵向逐条 / 横向逐条 / 横向从右 / 仅淡入 / 关闭
- **弹窗方向**：向右滑出 / 向左滑出 / 从右侧滑入 / 缩放弹出 / 关闭
- **单项时长**：100–800 ms（默认 320）
- **逐条延迟**：0–120 ms（默认 38）
- **弹窗时长**：80–600 ms（默认 260）
- **位移幅度**：自身尺寸的 5%–60%（默认 22%）

设置页带**实时预览区**，改完立刻能看到列表入场效果，并可主动重播。

## 四、两条必须遵守的规则

### 1. 配置说不动画就真的不动画

不做"反正就一点点"的偷偷动效。`enabled = false` 或对应风格为 `none` 时，
组件直接返回原始 child，不创建任何 AnimationController。

### 2. 尊重系统"减少动态效果"

`MediaQuery.disableAnimations` 为 true 时（macOS 的"减少动态效果"设置），
所有自定义动画自动跳过。开了这个选项的用户通常有生理原因，界面不应强行播放动画。
实现见 `AnimationScope.prefersReducedMotion`。

## 五、性能上的两个刻意设计

### 逐条延迟要封顶

延迟按序号线性累加，但**封顶在 14 项**：

```dart
Duration delayForIndex(int index) {
  final clamped = index.clamp(0, 14);
  return Duration(microseconds: staggerStep.inMicroseconds * clamped);
}
```

依赖树可能有几百个节点，若不封顶，最后一项要等好几秒才开始入场 ——
那不叫动画，叫卡顿。

### 弹窗必须用 showGeneralDialog

`showDialog` 固定使用系统缩放动画，无法改向量。
因此弹窗统一走 `showUmangoDialog`（内部 `showGeneralDialog` + 自定义
`transitionBuilder`），这是"弹窗方向可配置"能成立的前提。

## 六、代码入口

| 文件 | 内容 |
|------|------|
| `ui/lib/animation/animation_config.dart` | `AnimationConfig`、两套风格枚举、预设、`AnimationScope` |
| `ui/lib/animation/motion_widgets.dart` | `ListEntrance`、`showUmangoDialog`、`PageFadeThrough`、`StatusBanner` |

配置挂在应用根部（`main.dart` 的 `AnimationScope`），
因此是真正的**全局**配置：任何页面用 `AnimationScope.of(context)` 都能取到，
修改立即对全应用生效。

> 注意：阶段1 的动画配置是**内存态**，重启后恢复默认。
> 持久化可用已实现的 C++ `write_file` / `read_file` 回调落盘到用户配置目录。

## 七、其他动效

- **页面切换**（侧边栏切页）：轻微横向淡入 + 8px 位移，属导航层级，
  不参与"列表/弹窗"的风格配置。
- **状态提示条**（加载中 / 出错）：统一用 `StatusBanner`，带轻微入场，
  颜色随语义变化（错误 / 警告 / 成功 / 中性）。
