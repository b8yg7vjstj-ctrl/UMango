# 着色器资源

本目录下的 `AdaptiveBlur.ps` / `AdaptiveBlur.hlsl` 来自
**PCL-CE**（https://github.com/PCL-Community/PCL-CE）的
`PCL.Core/UI/Assets/Shaders/`，采用 **Apache-2.0** 许可，允许复制与再分发。

## 为什么直接带预编译产物

`AdaptiveBlur.ps` 是 **FXC 预编译过的像素着色器二进制**（`ps_3_0` 目标）。
WPF 的 `PixelShader.UriSource` 只能加载已编译的 `.ps`，不能现场编译 `.hlsl`。

PCL 原版附了 `CompileShader.bat`（需要 Windows SDK 里的 `fxc.exe`），
但**编译产物本身是确定性的**：同一份 HLSL 编译出的 `.ps` 逐字节一致。
因此直接采用预编译产物，构建链上就不需要引入 FXC 依赖。

`.hlsl` 一并保留，便于追溯与日后重新编译。

## 常量寄存器约定（改着色器时必须同步）

| 寄存器 | 语义 | 说明 |
|--------|------|------|
| `C0` | `Radius` | 模糊半径（像素） |
| `C1` | `SamplingRate` | 采样率 0.05~1 |
| `C2` | `QualityBias` | 质量偏置 |
| `C3` | `TextureSize` | `x=width, y=height, z=1/width, w=1/height` |
| `S0` | `InputTexture` | 输入采样器 |

`src/UMango.App/Effects/AdaptiveBlurEffect.cs` 里的
`PixelShaderConstantCallback(n)` 正是按这张表绑定常量。

## 打包注意

资源名会被 WPF 规范化为**全小写**（打包后是 `assets/shaders/adaptiveblur.ps`），
而 pack URI 查资源是**区分大小写**的 —— 所以代码里写 URI 时必须全小写。
