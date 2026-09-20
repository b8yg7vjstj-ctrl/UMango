# PCL-CE 代码构成、开源协议与自定义协议规范分析报告

> **分析对象**：`PCL-Community/PCL-CE`，分支 `dev`，commit `ab6e2f1`（`fix(setup-java): 修复 Java 选择页面刷新无效 (#3609)`）
> **数据来源**：本地浅克隆 `/tmp/pclce/PCL-CE`（17 MB，只读）+ 上游 GitHub 原始文件 + 官方仓库元数据
> **方法**：5 个并行子 Agent 分工分析（PCL.Core / WPF 前端 / 第三方许可 / 生成器·测试·CI / 自定义协议规范），主控 Agent 独立复核关键结论
> **重要限制**：本次为 `--depth 1` 浅克隆，**git 历史不可用**，因此"上游代码 vs CE 新增"的判断基于代码内容证据，无法用 git blame 追溯；凡证据不足处本报告明确标注

---

## 0. 结论速览

| 问题 | 结论 |
|---|---|
| 代码总量 | C# 约 **138,400 行** / 803 个 `.cs`；XAML 约 24,200 行 / 100 个文件 |
| 许可结构 | **三层异构**：Apache-2.0 基础设施层 + 自定义专有协议主体层 + 第三方组件层 |
| 自定义协议性质 | **非 OSI 开源协议**，属"源码可得的有限许可"（source-available, proprietary-with-permissions） |
| 自定义协议的核心规范 | 允许免费原样再分发；对"重度使用"（改代码/重实现实质功能）设定 **5 项强制义务**：命名前缀、二次创作声明、关于页首位署名+开源、正版与赞助劝导、禁止赞助解锁式体验（主要色相须为蓝色） |
| PCL-CE 自身合规性 | 5 项义务中 **3 项完全满足**（署名/开源/指南保留、正版与赞助劝导、蓝色主色相+无赞助解锁），**2 项部分满足**（二次创作声明的逐字表述、命名展示形式不统一）。实证：关于页首位即龙腾猫跃 + 爱发电链接，且项目自陈"这是 PCL CE，应该没有这玩意"（无隐藏主题解锁） |
| 上游对比（新发现） | 上游 PCL 的《分发有限许可》**只有标题 + shimo.im 外链**；CE **把全文内联进仓库**，并**新增**根 `LICENSE` 实现 Apache-2.0/自定义条款的目录级二分。指南部分逐字未改 |
| **最硬的合规问题** | 内置 `linkd.exe` 已确证为 **Microsoft 专有二进制**（`© Microsoft Corporation. All rights reserved.`，Server 2003 Resource Kit v5.2.3790.0），**无任何再分发授权且零归属**；另有未披露的 HMCL GPL-3.0 `transformer.jar` 与无许可的 `forge-installer.jar` |
| 最值得注意的结构性发现 | **许可证边界 ≈ 功能边界**：Apache-2.0 的 `PCL.Core` 只含基础设施（网络/认证/缓存/UI 基元），而"启动 Minecraft"这一实质功能仍在自定义许可目录内 → 仅凭 Apache-2.0 部分**无法再造一个启动器** |

---

## 1. 仓库构成与代码规模

### 1.1 四个工程 + 仓库根

| 路径 | 类型 | loc | 角色 | 适用协议 |
|---|---|---|---|---|
| `PCL.Core/` | 类库 (`net10.0-windows`, WPF) | 544 个 `.cs` / ≈45,000 行 | CE **重写**的核心基础设施库 | **Apache-2.0** |
| `Plain Craft Launcher 2/` | WPF 应用 (`WinExe`) | 205 个 `.cs` / ≈86,100 行 + 92 个 `.xaml` / ≈9,800 行 | 上游 PCL2 继承的**启动器主体**（页面、控件、模块、启动管线） | **自定义协议** |
| `PCL.Core.Test/` | MSTest 测试库 | 46 个 `.cs` / 5,280 行 | 核心库单元测试（177 个 `[TestMethod]`） | Apache-2.0 |
| `PCL.Core.SourceGenerators/` | Roslyn 源生成器 (`netstandard2.0`) | 8 个 `.cs` / ≈2,040 行 | 5 个增量生成器（配置、IoC 生命周期、DI） | Apache-2.0 |
| `.github/`, `cliff.toml`, `*.md` | 工程配置 | 18 个文件 | CI/CD、变更日志、贡献规范 | Apache-2.0 |

> 计数方法差异说明：`wc -l` 计换行符，脚本按行迭代会多计"末行无换行"的文件，故不同来源有 ±300 行偏差；本报告取整。

### 1.2 一张图看清"协议如何切分代码"

```
PCL-CE/
├── LICENSE ...................... Apache-2.0 全文 + SPECIAL DIRECTORY 条款（"Copyright 2025 PCL Community"）
│
├── PCL.Core/ .................... Apache-2.0  ← CE 新写的"零件库"
│   ├── IO/Net, IO/Storage ....... HTTP/DoH/Polly 重试、SQLite+LiteDB 缓存、内容寻址哈希库
│   ├── Minecraft/IdentityModel .. 4 种账号体系（MS OAuth / 离线 / Authlib / Yggdrasil Connect）
│   ├── Minecraft/Java ........... JVM 扫描与 PE 头解析
│   ├── Minecraft/Launch ......... ⚠ 仅 1 文件 54 行（只释放 log4j2 配置）——启动管线不在这里
│   ├── Link/ .................... 联机大厅、EasyTier 进程管理、自研 Scaffolding 协议
│   ├── UI/ ...................... 主题、自研动画引擎、模糊特效、SVG 控件
│   └── Utils/Secret ............. ChaCha20/AES-GCM + CNG-DPAPI 密钥保护
│
├── Plain Craft Launcher 2/ ...... 自定义《PCL 分发有限许可》 ← 启动器"本体"
│   ├── Modules/Minecraft/ ....... ⚑ 真正的启动管线：ModLaunch.cs (2,807 行) classpath/参数/JVM
│   ├── Modules/Base/ ............ ModBase.cs (3,759 行) 上游基础设施
│   ├── Pages/ ................... 下载/实例/设置/联机 等全部 UI 页面
│   ├── Images/, Resources/ ...... 图标、字体、捆绑 jar/exe
│   ├── LICENCE .................. 自定义协议正文（随发布以 Content 分发）
│   └── metadata.json ............ 版本 + 20 条第三方许可清单（About 页数据源）
│
├── PCL.Core.Test/ ............... Apache-2.0
├── PCL.Core.SourceGenerators/ ... Apache-2.0
└── LICENSE ...................... Apache-2.0（根）；GitHub 侧边栏仅显示 "Apache-2.0 license"
```

---

## 2. 各部分代码详解

### 2.1 `PCL.Core` — Map of the core library

| 目录 | 文件 | 行数 | 关键内容 |
|---|---:|---:|---|
| `App/` | 90 | 9,055 | `Basics`、`Paths`、`Config`（253 处 `[ConfigItem]`）、`Secrets` |
| `App/Configuration/` | 28 | 2,478 | `ConfigService`、`ConfigItem<T>`、`ConfigSource`(Shared/SharedEncrypt/Local) |
| `App/Essentials/` | 13 | 1,468 | `RpcService`(命名管道 RPC)、`SingleInstanceService`、`TelemetryService`(Sentry) |
| `App/IoC/` | 13 | 1,325 | `Lifecycle` 生命周期状态机（**非传统 DI 容器**） |
| `App/EventBus/` | 4 | 290 | 弱引用订阅的 channel→type→Guid 三层字典 |
| `App/Tasks/` | 8 | 281 | `TaskCenter` + 能力接口探测（Cancelable/Pausable/Progressive） |
| `App/Localization/` | 7 | 974 | 7 种语言 XAML，默认 zh-CN |
| `IO/Net/` | 25 | 2,313 | 7 个具名 `HttpClient`、Polly 指数退避重试、DoH、代理、HTTP 缓存 |
| `IO/Storage/` | 16 | 2,135 | `HashStorage`（2 字符前缀分桶）、`CacheService`(SQLite 元数据 + 文件落盘) |
| `IO/Download/` | 6 | 163 | ⚠ **只有接口契约，全仓库无实现** |
| `Link/` | 59 | 5,018 | 联机子系统（见 §2.1.1） |
| `Minecraft/` | 108 | 6,800 | `GameCore`、`ServerAddressResolver`(SRV/DoH)、NBT、Java 扫描 |
| `Minecraft/Launch/` | **1** | **54** | ⚠ 结构性缺位——启动主流程在 WPF 项目内 |
| `UI/` | 129 | 9,835 | 主题、**自研动画引擎（87 文件）**、模糊特效、`Tooltip`/`MotdRenderer` |
| `Utils/` | 92 | 7,219 | `Result`、`PEHeaderReader`、`SemVer`、编码检测、哈希、加密、校验器 |
| `ViewModel/` | 1 | 114 | `NewsViewModel`（CommunityToolkit.Mvvm） |

**架构要点**

- **IoC 是"编译期状态机"**：服务用 `Activator.CreateInstance(type, true)` 创建（要求私有构造函数 → **不存在构造函数注入**）。`PCL.Core/App/IoC/LifecycleState.cs` 被 csproj 显式列为 `AdditionalFiles`，由 `LifecycleServiceTypesGenerator` 用**正则解析**（而非 Roslyn 符号）提取 9 个生命周期状态，生成 `LifecycleServiceTypes` 服务注册表并编译期强绑定。
- **加密栈设计考究**：自描述信封 `MagicNumber = 0x454E4321` + 版本号；按版本回退解密（v0 ChaCha20 / v1 ChaCha20-Poly1305 / v2 AES-GCM），加密时按硬件能力择优；主密钥 32 字节存 `UserKey.bin`，由 **CNG DPAPI** 保护，写入走 tmp + `Flush(true)` + `File.Move` 原子替换。
- **不整洁处**：`Minecraft/Launch` 与 `IO/Download` 是"空壳/悬空抽象"；`ConfigGenerator` 静默吞掉所有异常（`catch { }`）；全部 5 个生成器**不产生任何诊断**。

#### 2.1.1 自研联机协议栈（CE 特色，`Link/` 59 文件 5,018 行）

这是 CE 相对上游最主要的自研增量，也是本仓库的"自定义协议"在**技术协议**意义上的存在：

| 组件 | 文件/行数 | 说明 |
|---|---|---|
| `Link/Scaffolding/` | 35 / 2,437 | **自研大厅协议**：`ScaffoldingClient`/`ScaffoldingServer`、`Client/Framing/{ProtocolReader,ProtocolWriter}`（自定义分帧）、5 个请求类型（Ping/PlayerPing/GetProtocols/GetServerProt/GetPlayerProfileList）、protobuf-net 序列化、服务端 `IRequestHandler` 分派 |
| `Link/Lobby/` | 5 / 1,102 | 大厅编排：发现/加入/离开、15 秒游戏状态轮询 Timer |
| `Link/EasyTier/` | 4 / 450 | 拉起外部 `easytier-core.exe`/`easytier-cli.exe`（**运行时下载**，非捆绑）做 P2P 组网/NAT 穿透 |
| `Link/McPing/` | 10 / 642 | Minecraft Server List Ping（SLP）；`LegacyMcPingService` 旧协议探测**尚未实现**（TODO） |
| `Link/Natayark/` | 1 / 150 | Natayark ID 第三方账号 OAuth |

⚠ **硬编码事实**：`ScaffoldingFactory.cs:18` 硬编码 `HostIp = "10.114.51.41"` 与厂商串 `Vendor = "PCL CE {ver}, EasyTier {ver}"`。

### 2.2 `Plain Craft Launcher 2` — 启动器本体

**最大文件 Top 8**（全部位于此目录）

| 文件 | 行数 | 职责 |
|---|---:|---|
| `Pages/PageDownload/ModDownloadLib.cs` | 4,578 | 游戏/加载器安装下载核心（模块级代码混在页面目录） |
| `Modules/Minecraft/ModComp.cs` | 3,823 | 社区资源/Mod 元数据（加载 `mcmod.buf` 索引） |
| `Modules/Base/ModBase.cs` | 3,759 | 上游基础设施、转换器（`AdditionConverter` 等） |
| `Pages/PageInstance/PageInstanceCompResource.xaml.cs` | 2,961 | 实例资源管理 |
| `Modules/Minecraft/ModLaunch.cs` | 2,807 | ⚑ **启动管线 + `McLaunchPrecheck`** |
| `Modules/Minecraft/ModDownload.cs` | 2,502 | 下载 |
| `Modules/Minecraft/ModLocalComp.cs` | 2,493 | 本地资源 |
| `FormMain.xaml.cs` | 2,243 | 主窗口 + `PageType` 路由 |

**CE 新增（有硬证据）**：`Modules/ModLink.cs`（联机）、`Modules/ModWebServer.cs`（内嵌 HTTP + Natayark OAuth 回调）、`Modules/Event/`（XAML 附加事件服务）、`Modules/Network/`（`ModNet` 门面 + `RequestSigning`）、`Modules/UI/`（`ThemeManager`、`HintService`）、`Modules/Updates/`（Minio/MirrorChyan 更新通道）、`Modules/Minecraft/CrashAnalysis/`（**30 文件的崩溃分析子系统**）、`Pages/PageTools/PageToolsGameLink`（联机页）、`PageSetupGameLink`、多语言页。
**无法判定归属**（证据不足）：`PageInstanceModDisabled`、`PageInstanceServer`/`ServerCard` 等。

**主色系**：`Application.xaml` 定义 8 级蓝色阶梯 `#343d4a → #0b5bcb → #1370f3 → #4890f5 → #96c0f9 → #d5e6fd → #e0eafd → #eaf2fe`；`oauth-complete.html` 内含 `--hue: 210deg`。

### 2.3 `PCL.Core.SourceGenerators` — 5 个增量生成器

| 生成器 | 触发 | 产出 |
|---|---|---|
| `LifecycleServiceTypesGenerator` (292 行) | 类含 `LifecycleService` 属性 + **`AdditionalFiles` 传入的 `LifecycleState.cs` 文本正则解析** | `LifecycleServiceTypes.g.cs`：每状态一个 `Type[]`，按 `Priority` 降序 |
| `LifecycleScopeGenerator` (306 行) | `[LifecycleScope]` + `[LifecycleStart/Stop/…]` | `partial class X : ILifecycleService`（15 处） |
| `DependencyCollectorGenerator` (350 行) | `[DependencyCollector<T>]` / `[DependencyInjectionPoint]` | `DependencyGroups._GroupMap` + 注入方法 |
| `ConfigGenerator` (746 行) | `[ConfigItem<T>]` / `[ConfigGroup]` | 配置访问器 + `ConfigService._InitializeConfigItems()` |
| `EnvironmentInteropGenerator` (69 行) | ⚠ 仅 `CompilationProvider`，**读取构建机环境变量** | `EnvironmentInterop.SecretDictionary` |

⚠ **两点值得注意**：
1. `EnvironmentInteropGenerator` 把 CI 注入的 `PCL_MS_CLIENT_ID`、`PCL_SENTRY_DSN`、`PCL_NAID_CLIENT_SECRET` 等**以明文 C# 字符串字面量写入生成源码**；`PCL_WRITE_SECRET` 未设置时静默不生成（本地/PR 构建）。
2. **生成器零诊断**：无 `DiagnosticDescriptor`，`ConfigGenerator.cs:69-72` 明确注释"可添加诊断，此处直接忽略以免打断编译"。

### 2.4 `PCL.Core.Test` 与 CI

- **177 个测试**，MSTest v4（4.3.0），密集覆盖缓存/存储（≈2,000 行）、存档与档案、校验器、哈希编码、EventBus、AES-GCM/ChaCha20、Modrinth。
- **零覆盖**：`App/IoC`（生命周期容器本体）、`App/Configuration`、`UI/`、`Link/`、`Logging/` —— 即"生成器喂给的那个容器"完全没测。
- 部分测试是**联网测试**且无跳过守卫（`Modrinth.GetProjectTest`、`DohConnection`、`McPing`）。
- ⚠ **CI 从不执行测试**：`build-test.yml` 的 `paths-ignore` 含 `PCL.Core.Test/**`，且 `.slnx` 中 `PCL.Core.Test` 被显式排除出解决方案构建（`<Build Solution="*|*" Project="false" />`）。

**CI/发布**：`build-test`（CI × x64/ARM64）→ `reusable-build`（ubuntu-latest, .NET 10.0.x）→ 稳定/预发布两套 `release-*_publish`（`Release`/`Beta` 配置）。签名用 **GPG 分离签名**（`crazy-max/ghaction-import-gpg`）而非 Authenticode；公钥 `GPG-PUBLIC-KEY.asc` 的 UID 为 `NPCL-Community <sign@pclc.cc>`，指纹 `81D9430A309B84272D518584EDF4453F0BBB862E`（与 `cliff.toml:33` 硬编码一致）。
⚠ 该公钥文件是 **UTF-16LE + BOM**，非标准 ASCII armor，朴素 `gpg --import`/`strings` 流程会失败。
⚠ **无 SBOM 步骤；无任何 workflow 打包 `LICENSE`/`NOTICE`**（详见 §7）。

---

## 3. 三层许可证架构（本报告核心）

### 3.1 第一层：根 `LICENSE` = Apache-2.0 + 例外条款

根 `LICENSE` 是**标准 Apache-2.0 全文**（207 行），但把 Apache 官方的"APPENDIX 如何应用本许可证"样板**改写成了例外声明**：

```
Copyright 2025 PCL Community

SPECIAL DIRECTORY: Plain Craft Launcher 2/

Licensed under some custom terms discribed in the 'Plain Craft Launcher 2/LICENCE' file.
```

**观察（属于分析推断，非协议原文）**：这是一种不常见但可用的构造——Apache-2.0 正文 + 目录级例外。它的实际效果是"Apache-2.0 覆盖 `PCL.Core*`、`.github` 等；`Plain Craft Launcher 2/` 排除在外"。**风险点**：GitHub 仓库侧边栏因此显示 "Apache-2.0 license"，而**占代码量 62%、且包含启动器全部实质功能的主程序目录实际是专有自定义许可**——对只想"拿代码就用"的第三方构成实质性误导。

### 3.2 第二层：`Plain Craft Launcher 2/LICENCE` = 自定义专有协议

全文由三部分组成（正文见 §4）：

1. **《PCL 分发有限许可》**——法律性条款，版权归 **成都瓜皮龙科技有限公司**。
2. **《PCL 存储库合理使用指南》**——**自称"仅作为说明，不是法律文件或协议"**，但承载了全部行为规范。
3. **"说人话的备注"**——非正式说明，收尾语气友善（"常规、善意的使用都没啥问题的，放心吧！"）。

该协议**不是 OSI 认可的开源许可证**：它不授予修改权与再许可权（只授予"原样副本"的分发权），因此准确表述是 **source-available / 专有许可**。同时，`Plain Craft Launcher 2.csproj:30` 保留 `Copyright © 龙腾猫跃 2016. All Rights Reserved.`，本地化文案含**中国计算机软件著作权登记号 `2020SR0875133`**——说明上游 PCL 有登记著作权，这解释了为何采用自定义协议而非标准开源协议。

### 3.3 第三层：第三方组件

**三条投递通道**（归口文件不同，也是最容易出问题的地方）：

| 通道 | 归口 | 条目数 | 承载机制 |
|---|---|---|---|
| NuGet 包 | 4 个 `.csproj` | **39 个不重复包**（45 条 `PackageReference`） | 无集中声明，仅 `PCL.Core/NOTICE`（5 条，且已失效） |
| 内置二进制/资产 | `Plain Craft Launcher 2/Resources/`、`PCL.Core/UI/Assets/` | 14 项 | 主要靠 `metadata.json` 的 20 条 `licenses` |
| 运行时下载 | EasyTier 等 | — | **不随仓库分发**，故无再分发义务 |

### 3.4 第三层明细：第三方组件许可清单

#### (a) NuGet 依赖（39 个不重复包 / 45 条引用）

| 包 | 版本 | 工程 | 许可 | 是否在 `metadata.json` |
|---|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | Core | MIT | ✗ |
| Microsoft.Xaml.Behaviors.Wpf | 1.1.142 | Core | MIT | ✗ |
| Wacton.Unicolour | 8.0.0 | Core | MIT（nuspec 用 `license type=file`，**非 SPDX 表达式**） | ✓ (Unicolour) |
| Microsoft.CSharp | 4.7.0 | Core+Test | MIT | ✗ |
| System.Management | 10.0.9 | Core | MIT | ✗ |
| System.Security.Cryptography.ProtectedData | 10.0.9 | Core | MIT | ✗ |
| PlainToolkit.CngProtectedData | 2.1.0 | Core | MIT | ✗ |
| SharpZipLib | 1.4.2 | Core | MIT | ✗（仅在失效的 `NOTICE` 中） |
| Microsoft.Data.Sqlite | 10.0.9 | Core+App | MIT | ✓ |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.3 | Core+App | Apache-2.0 (+ SQLite public domain) | ✗ |
| System.Text.Json | 10.0.9 | Core+Test | MIT | ✗ |
| YamlDotNet | 18.1.0 | Core | MIT | ✗ |
| fNbt | 1.0.0 | Core+Test | **BSD-3-Clause**（非 MIT；fNbt v0.5.0+ 起为 3-Clause BSD） | ✗ **零归属** |
| LiteDB | 5.0.21 | Core | MIT | ✓ |
| Ae.Dns.Client | 3.1.0 | Core | MIT | ✓ |
| Stun.Net | 10.0.6 | Core | MIT（来源 HMBSbige/NatTypeTester） | ✓ |
| Microsoft.Extensions.Http / Logging / Logging.Abstractions | 10.0.9 | Core | MIT | ✗ |
| Polly | 8.7.0 | Core | BSD-3-Clause | ✗ |
| Humanizer.Core.zh-CN | 3.0.10 | Core | MIT | ✗ |
| Sentry | 6.6.0 | Core | MIT | ✓ |
| System.IdentityModel.Tokens.Jwt | 8.19.1 | Core | MIT | ✗ |
| FluentValidation | 12.1.1 | Core | Apache-2.0 | ✗ |
| Dapper | 2.1.79 | App | Apache-2.0 | ✓ |
| DotNet.Glob | 3.1.3 | App | MIT（nuspec **无 SPDX 表达式**，仅 `licenseUrl` 指向 GitHub，故为推断值） | ✓ |
| Downloader | 5.9.4 | App | MIT | ✓ |
| Markdig.Wpf | 0.5.0.1 | App | **MIT**（注意：其上游 Markdig 本体为 **BSD-2-Clause**） | ✓（未标许可名，仅给链接） |
| NAudio | 2.3.0 | App | MIT | ✓ |
| protobuf-net | 3.2.56 | App | Apache-2.0 | ✓ |
| System.Net.Http (4.3.4) / System.Text.RegularExpressions (4.3.1) | 4.3.x | App | ⚠ **无 SPDX 表达式**，`licenseUrl` 为 `fwlink/?LinkId=329770`（Microsoft「.NET Library」遗产条款）；上游 `dotnet/corefx` 为 MIT（推断） | ✗ |
| Microsoft.CodeAnalysis.CSharp / Analyzers | 5.6.0 | SG (PrivateAssets) | MIT | ✗ |
| MSTest.* / Microsoft.NET.Test.Sdk | 4.3.0 / 18.7.0 | Test | MIT | ✗ |

> 结论：`metadata.json` 的 20 条清单**大约只覆盖实际 NuGet 依赖的一半**，且对多数包连"许可名称"都不写（只给外链）。
>
> ⚠ **本表的边界（诚实声明）**：仓库内**没有 `packages.lock.json`、没有 `project.assets.json`、没有 `Directory.Packages.props`、没有 `NuGet.config`**，且未执行 `dotnet restore`，因此**传递依赖（transitive dependencies）无法在离线状态下枚举**。上表仅为**直接依赖**。已知的传递依赖例：`Markdig.Wpf`(MIT) → `Markdig`(BSD-2-Clause)、`SQLitePCLRaw.bundle_e_sqlite3` → SQLite 本体的 public domain。**这意味着实际需要归属的第三方组件数量高于 30，而项目方对此没有任何机器可读的完整清单**（也无 SBOM）——这既是合规缺口，也是一个可复现性/供应链审计问题。

#### (b) 内置二进制与资产（逐项复核）

| 文件 | 大小 | 溯源（已核实的硬证据） | 许可状态 |
|---|---|---|---|
| `legacyfix.jar` | 997 KB | manifest: `Premain-Class: uk.betacraft.legacyfix.LegacyFixAgent`；内含 shade 的 `javassist`、`org/json`；**jar 内无任何 LICENSE/NOTICE**。字节数与上游 `files.betacraft.uk` 索引中的 `legacyfix-2.0-20260502.jar` **完全一致** → 未修改的上游构建 | **GPL-3.0**（`metadata.json` 已声明 ✓）；⚠ 但按 GPL-3.0 §4/§6 应随附许可文本与对应源码获取方式，现状缺失 |
| `java-wrapper.jar` | 15.8 KB | `Main-Class: oolloo.jlw.Wrapper`（00ll00/java_launch_wrapper）；内嵌 `libjlw-{aarch64,x86,x86_64}-1.4.3.dll` | MIT（已声明 ✓） |
| `transformer.jar` | 2.2 KB | ⚠ 含 `org/jackhuang/hmcl/HMCLTransformerDiscoveryService.class` + `META-INF/services/cpw.mods.modlauncher...`（2020-05-03）→ **来自 HMCL** | ⚠ **HMCL 为 GPL-3.0**，`metadata.json` **未列出** |
| `lwjgl-unsafe-agent.jar` | 9.8 KB | `Premain-Class: org.glavo.lwjgl.UnsafeAgent`（HMCL-dev/lwjgl-unsafe-agent）；条目时间戳统一 `1980-02-01`（可复现构建） | Apache-2.0（已声明 ✓） |
| `forge-installer.jar` | 3.2 KB | 仅 1 个类 `com/bangbang93/ForgeInstaller.class`，**无 MANIFEST.MF**。确为 BMCLAPI 作者 bangbang93 的封装类，**不是 Forge 官方安装器** | ⚠ **无许可声明**，`metadata.json` 未列出 |
| `linkd.exe` | 11 KB | 🔴 **已确证为 Microsoft 专有工具**：UTF-16LE `VS_VERSION_INFO` 资源内含 `CompanyName = Microsoft Corporation`、`FileDescription = Utility to create a directory name grafting`、`FileVersion = 5.2.3790.0 built by: dnsrv_dev(v-smgum)`、`ProductName = Microsoft Windows Operating System`、`LegalCopyright = © Microsoft Corporation. All rights reserved.`、`OriginalFilename = linkd.exe`；导入 `ntdll.dll` 的 `NtFsControlFile`、`RtlDosPathNameToNtPathName_U`；帮助文本自述"LINKD grafts (links) the target name directly into the name space at Source… NTFS in Windows 2000" → **Windows 2000 / Server 2003 Resource Kit 的 NTFS 目录联接工具** | ❌ **专有许可，保留全部权利，无任何再分发授权**；且 `NOTICE`/`metadata.json` **均未列出**（用于目录联接，`ModLaunch.cs:1249/1285`） |
| `Font.ttf` | 20 KB | 直接解析 `name` 表：family = **"PCL English"**，`nameID 3 = "PCL English:Version 1.00"`，`nameID 5 = "Version 1.00 August 15, 2020, initial release"`，PostScript 名 `PCLEnglish` | ⚠ **无任何许可元数据**：缺失 `nameID 13/14`（许可文本字段）；`OS/2 fsType = 0x0000`（可安装嵌入，无限制）；`DSIG` 表仅 8 字节即表头 → **未数字签名**（`file` 命令的 "digitally signed" 是误报）。推断为 PCL 自有字体，但**无法验证，不应视为已澄清** |
| `mcmod.buf` | 621 KB | `file` = **gzip**（解压 1.25 MB），`ModComp.cs:701` 以 `GetResourceStream` 加载 → mcmod.cn 索引数据 | ⚠ 数据文件，许可未声明 |
| `log4j2-debug.xml` / `log4j2-legacy-debug.xml` | 1.7/1.6 KB | log4j2 调试配置模板（Apache Log4j 项目配置格式） | 未声明（log4j2 本体为 Apache-2.0，此处仅配置文件） |
| `PCL.Core/UI/Assets/IconPacks/lucide/` | 91 SVG | 含 `LICENSE.txt` | **ISC**（含部分 MIT）——已声明 ✓ |
| — | — | **通用结论**：对上列 5 个 `.jar` 逐一执行 `unzip -l \| grep -iE "licen\|notice\|copying"`，**全部无命中** → 5 个 jar 内部**均无许可文本、无 `META-INF/LICENSE`、无 NOTICE**。对 GPL-3.0 的 `legacyfix.jar` 与 `transformer.jar` 而言，这意味着 GPL-3.0 §4「随程序向接收者提供本许可副本」与 §6「提供对应源码」的义务目前未被满足 |

> **图标资产的归属特例**：`Images/` 下的图标资源对应 `metadata.json` 中的 `Iconmonstr`（其自有许可**不要求**署名，故该条目属礼节性）与 **`Iconfont`**（阿里巴巴 ALIMAMA MUX）。⚠ `Iconfont` 条目是全清单中**唯一 `"license": null`** 的一项，而 About 页恰好用 `NullToVisibilityConverter` 在 `license` 为空时**隐藏"查看许可证"按钮** —— 因此该资产**随附了署名、却没有任何许可声明**。
| `PCL.Core/UI/Assets/Shaders/AdaptiveBlur.{hlsl,ps}` | — | 自研模糊着色器 | PCL 自有 |
| `hints/{zh-CN,en-US}.txt` | 各 75 KB | 首行 "PCL CE 是由 PCL-Community 开发的 PCL 社区衍生版本。" | PCL 自有（同时是 CE 署名证据） |

#### (c) 运行时下载（不构成再分发）

- **EasyTier**（`easytier-core.exe`/`easytier-cli.exe`）下载到 `Paths.SharedLocalData/EasyTier/<版本>/`：**LGPL-3.0**。因不随仓库分发，项目本身无再分发义务；About 页有 EasyTier 头像致谢但 `metadata.json` **无许可条目**。
- MirrorChyan CDN：第三方**付费**分发平台，用户自持 CDK 可选启用。

#### (d) 源码内嵌第三方代码（逐文件许可头）

全仓库**唯一**带许可头的 WPF 文件：

`Plain Craft Launcher 2/Controls/Behaviors/ClipboardInterceptor.cs:8-16`（逐字）：
```
// Author: uye (owner of the MaaAssistantArknights team)
// Original Source: MaaAssistantArknights project - https://github.com/MaaAssistantArknights/MaaAssistantArknights
// License: Apache License 2.0 (this file only)
//
// This file is based on work originally developed in the MaaAssistantArknights project,
// which is licensed under the GNU AGPL v3.0 only.
//
// As the original author of this code, I am re-licensing this specific file under
// the Apache License 2.0 for use in PCL2-CE.
```
→ 这是一次**由原作者本人做出的、有书面记录的重新授权**（AGPL-3.0 → Apache-2.0，仅限该文件），处理方式规范、值得肯定。

`PCL.Core` 内另有 3 个文件带第三方许可头：

| 文件 | 声明 |
|---|---|
| `UI/BackgroundPresenter.cs:8-11` | 来自/修改于 `OrgEleCho/EleCho.WpfSuite`，作者 EleCho，**MIT** |
| `UI/Controls/BlurBorder.cs:9-12` | 同上（EleCho.WpfSuite, MIT） |
| `Utils/Diff/BsDiff.cs:1-40` | 完整双许可正文：参考 `LogosBible/bsdiff.net`，含 **MIT 全文**（Copyright 2010-2024 Logos Bible Software）+ **BSD-2-Clause 全文**（Copyright 2003-2005 Colin Percival） |

> 除此之外，`PCL.Core` 全库 **SPDX 标识符 0 处**、无逐文件版权头；WPF 项目中 `Copyright` 字样仅作为 UI 资源键出现。即：**项目自身不采用逐文件许可头机制**，归属完全依赖"根 LICENSE + LICENCE + metadata.json + NOTICE + About 页"这套集中式机制。

---

## 4. 自定义协议确定了哪些规范（逐条规范表）

> 以下每条均绑定原句。**协议文本要求 ≠ 法律意见**；凡属解释性判断，本报告明确标注。

### 4.1 《PCL 分发有限许可》——法律性条款

| # | 触发条件 | 具体要求 / 授予 | 限制与例外 | 原文依据 |
|---|---|---|---|---|
| **A1** | 任何使用 | 权利授予以**持续不收费**为前提："在持续不收费的前提下，许可方授予您一项全球范围内、非排他、免版税、**不得再许可**的有限许可" | 授予的权利**仅限原样副本**：存储、镜像、备份、上传、下载、向公众提供、传播和再分发 | "在持续不收费的前提下……可以存储、镜像、备份、上传、下载、向公众提供、传播和再分发原样副本" |
| **A2** | 定义"原样副本" | 未改变的副本即为原样副本；**传输、存储、镜像、压缩或解压原样副本，不视为改变** | → 因此"打包成 zip 再分发"仍属被许可行为 | "「原样副本」是指未经任何方式改变的软件副本。传输、存储、镜像、压缩或解压原样副本，不视为改变" |
| **A3** | 定义"收费" | 以获得/下载/复制/分发/安装/预装/使用原样副本为**对价或条件**，直接或间接向接收者收费，包括付费下载、**会员访问门槛**、**强制赞助**或其他变相收费 | **例外（重要）**："承载平台通过与获得原样副本无直接对应关系的一般广告、一般赞助、平台服务或其他独立业务取得收入，不视为收费" | 见上引原句 |
| **A4** | 第三方内容 | 协议**不覆盖**已明确标注为第三方内容的部分，且**不限制**第三方内容原有许可证明确授予的权利 | → 第三方组件仍适用其自身许可 | "软件不包括已明确标注为第三方内容的部分，本许可亦不限制第三方内容原有许可证所明确授予的权利" |
| **A5** | 权利传递 | 每一位从你处获得原样副本的接收者，**均可直接从许可方获得**本许可授予的相应权利 | 类似下游保护设计；但仅覆盖"原样副本"，不改"不得再许可"的性质 | "每一位从您处获得原样副本的接收者，均可在遵守本许可的前提下，直接从许可方获得本许可明确授予的相应权利" |
| **A6** | 接受方式 | 行使本许可授予的任何权利，即视为同意本许可 | — | "您行使本许可明确授予的任何权利，即表示同意遵守本许可" |
| **A7** | 权利保留 | 除明确授予的权利外，许可方保留全部权利 | 版权人：**成都瓜皮龙科技有限公司** | "除本许可明确授予的权利外，许可方保留软件及其相关知识产权中的全部权利" |

> **A1+A2 的关键推论（解释性）**：该协议**不授予修改权**。你把 PCL 改一行再分发，就已经超出"分发有限许可"的授权范围，落入《合理使用指南》约束的"重度使用"。

### 4.2 《PCL 存储库合理使用指南》——行为规范

先明确其性质与承诺：

| # | 规范 | 原文依据 |
|---|---|---|
| **B0** | **本指南不是法律文件**："本指南仅作为说明，不是法律文件或协议"；但开发者承诺"在遵守本指南，且**善意、合理使用**的情况下，**不会追究责任**" | "本指南仅作为说明，不是法律文件或协议……但开发者承诺，在遵守本指南，且善意、合理使用的情况下，不会追究责任" |

**→ 使用强度二分法**

| # | 档位 | 判定标准 | 常见情形（原文举例） |
|---|---|---|---|
| **B1** | **轻度使用** | 仅参考**极小部分**内容，**或**制作的软件与 PCL 的**实质功能不存在任何重叠** | "使用或参考存储库中极小部分的内容，来开发启动器或其他软件"；"虽参考了较大量的内容，但制作的软件与 PCL 的实质功能不存在任何重叠" |
| **B2** | **重度使用** | 不符合轻度使用的情形即为重度 | "基于本存储库进行修改"；"参考存储库中较大量的内容，以制作或重新实现 PCL 的部分实质功能（例如启动或下载 Java 版 Minecraft，下载或管理 Mod 等）" |

**→ 轻度使用义务（2 项）**

| # | 义务 | 原文依据 |
|---|---|---|
| **B3** | ① 以适当的形式**给出署名** | "以适当的形式给出署名" |
| **B4** | ② **不得混淆**或暗示与 PCL 有关。禁例：`Potato Craft Launcher`、`PCL 手机版`（"等会造成混淆或暗示这与 PCL 有关的名称"） | 见 B4 原句 |

**→ 重度使用义务（5 项，本协议最具约束力的部分）**

| # | 义务 | 具体要求 | 原文依据 |
|---|---|---|---|
| **B5** | ① 二次创作声明 | "明确表示您的内容是**第三方基于 PCL 独立进行二次创作的产物**" | 同左 |
| **B6** | ② **命名规则** | 名称**必须以 `Plain Craft Launcher (PCL)` 开头**，其后添加足以表现"由第三方修改"的后缀。<br>✅ 允许示例：`PCL 某服务器魔改版`、`PCL PR Collection`、`PCL (XX Bug Fix)`、`PCL XXX修改版`<br>❌ 禁止示例：`PCL 某服务器特供版`（暗示官方提供）、`PCL 3`、`PCL 手机版`、`PCL++`、`PCL Pro`（像官方后续作品） | "名称必须以 Plain Craft Launcher (PCL) 开头，并在其后添加足以表现其由第三方修改的后缀" |
| **B7** | ③ 署名 + 开源 + 指南延续 | (a) 在**关于页面的首位**给出**龙腾猫跃**的署名及**对应的赞助链接**；(b) **公开源代码**；(c) **继续使用本文件作为指南**（原文建议"可参考 CC BY-SA 协议"） | 见左 |
| **B8** | ④ 正版/赞助劝导 | "软件若有 Minecraft 启动功能，则应实现与 **`McLaunchPrecheck` 函数一致（或更激进）** 的 Minecraft 正版购买劝导与赞助劝导" | 见左 |
| **B9** | ⑤ 禁止赞助解锁式体验 | Windows 上运行时**不应有与 PCL 赞助解锁功能类似的功能或表现**。<br>原文举例：**"界面的主要色相必须为蓝色"**（即使没有修改功能，改为其他色相也和需要赞助解锁的主题功能类似） | 见左 |

**→ 落款备注**：这些规则"主要是为了阻止恶意的使用、倒卖和'山寨版'的出现，常规、善意的使用都没啥问题的"；有疑问可到 `Meloong-Git/PCL` 的 Discussions 提问。

### 4.3 该自定义协议的规范特征小结

1. **以"是否收费"为唯一的分发开关**：免费 = 可自由镜像/再分发原样副本；一旦收费，连"原样副本分发"都不被授权（A1+A3）。
2. **区分"原样分发"与"改造后分发"两套规则**：前者由许可条款管辖，后者**没有授予任何权利**，只由"指南"给出免责口径（B5–B9）。
3. **命名与署名是硬性品牌条款**（B6/B7），并把"公开源代码"作为重度使用者的义务——形成一种"类 copyleft 的开放要求"，但**不要求相同方式共享**（只说"可参考 CC BY-SA"）。
4. **把上游产品的商业/运营特征写成了合规条件**（B8 的劝导、B9 的蓝色主色相），这在开源协议中相当罕见——它保护的不只是版权，而是**原作者的品牌识别度与变现路径**。
5. **非 OSI 开源**：不授予修改权、不授予再许可权 → 不应在对外描述中称其为"开源协议"。

---

## 5. PCL-CE 对自定义协议的合规性审计

PCL-CE 修改了 PCL 源码并继承其品牌资产 → **属 B2 重度使用**，故 B5–B9 全部适用。逐条核验：

| 义务 | 判定 | 证据 |
|---|---|---|
| **B5** 二次创作声明 | 🟡 **部分满足**（语义到位，无逐字声明） | `Plain Craft Launcher 2.csproj:28` `<Description>Minecraft 启动器 (作者: 龙腾猫跃; 经社区二次开发版本)</Description>`；`PCL.Core/README.md`："这是为 PCL CE 版本特别开发的核心库"；`Resources/hints/zh-CN.txt` 首行："PCL CE 是由 PCL-Community 开发的 PCL 社区衍生版本。"；`README.md:29`："PCL CE 是基于 PCL 开源代码二次开发的社区版本"；`zh-CN.xaml:1474`："你正在使用 PCL CE（PCL 社区版）！此版本为**独立开发和维护**，与原版维护路线不同…"<br>⚠ 全库**未找到**逐字对齐规则用语的"第三方基于 PCL 独立进行二次创作的产物"一句。正向证据：`zh-CN.xaml:2515` 版权行为 `Copyright © 2026 PCL Community & 龙腾猫跃.`，未抹去原作者 |
| **B6** 命名规则 | 🟡 **部分满足**（机器可读命名全合格，文档展示名不统一） | **合格**：`FormMain.xaml:13` 窗口标题、`csproj:27,29` `AssemblyTitle`/`Product`、`metadata.json:2`、`Program.cs:29` 启动横幅 `Welcome to Plain Craft Launcher 2 Community Edition!` 均为 `Plain Craft Launcher Community Edition`；发行产物名 `PCL2_CE_<config>_<arch>.exe`。`Community Edition`/`CE` 构成合格的"第三方修改后缀"<br>⚠ **不合格处**：`README.md:7` 一级标题为 `# PCL Community Edition`，**缺 `Plain Craft**`。因 B6 允许示例中包含 `PCL PR Collection` 这类 `PCL` 缩写形式，故属**展示形式不统一**而非严重违反，但为可整改项 |
| **B7(a)** 关于页**首位**署名 + 赞助链接 | ✅ **满足** | `Pages/PageSetup/PageSetupAbout.xaml:44-53` 三行首卡：**Row 0 = `Setup.About.OriginalAuthor`（头像 `LTCat.jpg`）**，Row 1 = PCL Community，Row 2 = 版本；`zh-CN.xaml:2470-2471` → `"龙腾猫跃"` / `"Plain Craft Launcher 的原作者！"`。链接次序上**第一个链接**即 `https://ifdian.net/a/LTCat`（`:87`，文案 `Setup.About.SponsorOriginalAuthor` = "赞助原作者"） |
| **B7(b)** 公开源代码 | ✅ **满足** | 仓库公开；About 页 `Setup.About.SourceCode` → `https://github.com/PCL-Community/PCL2-CE`（`:101-103`）；另有 `Setup.About.Legal.CommunitySource` = "PCL CE 开源代码" |
| **B7(c)** 继续使用本文件作为指南 | ✅ **满足**（分发形态有一处提示） | `Plain Craft Launcher 2/LICENCE` 保留原文件（63 行，含两文件全部条款）；`Plain Craft Launcher 2.csproj:119` `<Content Include="LICENCE" />` 使其随构建/发布产物落到 artifact<br>⚠ 提示：发布工作流只把重命名后的 `.exe` 作为 release 资产上传（`release-stable_publish.yml:74-76`），因此**只下载 exe 的最终用户不会直接拿到 `LICENCE` 文本**。规则义务对象是二次创作者，不影响判定，但对"向公众提供副本"场景附带许可文本更稳妥 |
| **B8** 正版购买劝导 + 赞助劝导 | ✅ **满足** | `Modules/Minecraft/ModLaunch.cs:34` 存在 `McLaunchPrecheck()`，在 `:330` 启动前调用。**正版劝导**：`:144-181` 未检测到 MS 档案时弹窗，按钮 1 打开 `xbox.com/zh-cn/games/store/minecraft-java-...` 购买页；受限区域分支提供"购买 / 演示模式(`--demo`) / 返回"。**赞助劝导**：`:95-138` 在启动计数 10/20/40…2000 次时弹窗，按钮 1 打开 `https://afdian.com/a/LTCat`（含"但是我拒绝"选项）<br>⚠ 两点限定：赞助劝导整段包在 `#if BETA` 内；`:140-142` 在 `DEBUG/DEBUGCI` 下 `return` 跳过正版劝导 |
| **B9** 蓝色主色相 + 无赞助解锁 | ✅ **满足（含一处需说明的边界）** | **色相**：`ConfigEnums.cs:25-31` `enum ColorTheme { SkyBlue, CatBlue, DeathBlue, HmclBlue }`，LCH 色相值分别为 235/255/268/275（`ThemeService.cs:157-162`），**全部为蓝色系**；默认 `Config.cs:268/273` = `ColorTheme.CatBlue`；`Application.xaml` 主色阶为蓝色族；`oauth-complete.html` `--hue: 210deg`。<br>**无赞助解锁**：全库检索 `supporter/sponsor/会员/vip/付费/premium/unlock/激活码` **未发现任何付费门槛或功能解锁逻辑**（唯一 `Unlock()` 命中是位图锁）。**决定性证据**是上游"赞助解锁隐藏主题"的彩蛋在此被改写为：`zh-CN.xaml:2491`「隐藏主题 混乱黄 已……嗯不对，这是 **PCL CE，应该没有这玩意**……」——项目自行声明不存在隐藏主题解锁。赞助仅出现在**可拒绝的弹窗**（`Minecraft.Launch.Donate.*`）与**纯外链按钮**（`PageSetupUI.xaml.cs:670`，只 `OpenUrl`，不回传不解锁） |

### 5.1 需要说明的边界情形

1. **主题色可调 vs "主要色相必须为蓝色"**：CE 允许用户在 **3 个蓝色预设**间切换（`SkyBlue`/`CatBlue`/`DeathBlue`，`HmclBlue` 仅愚人节出现，见 `PageSetupUI.xaml.cs:15-17`）。由于全部预设均为蓝色系、且**不涉及任何付费解锁**，本报告判断不构成 B9 所禁止的"与赞助解锁类似的功能或表现"。
2. **可选付费 CDN（MirrorChyan）**：`zh-CN.xaml:3194` 描述 MirrorChyan 为"付费的第三方应用分发平台……这是一项可选服务，**不填入 CDK 不影响正常使用与更新**"。在 A3 的定义下，付款方向是"用户 → 第三方平台"，PCL CE 自身未向接收者收取任何对价，故**不构成"收费"**；即便从严解释，"与原样副本无直接对应关系……的独立业务"也落在 A3 明文例外内。**这是本协议最有意思的解释边界**。
3. **"不得再许可"与 `PCL.Core` 的 Apache-2.0 关系**：`Plain Craft Launcher 2/` 与 `PCL.Core/` 分属不同许可、不同目录，属各自独立授权，不构成对上游代码的再许可。但需注意**反向不对称**：Apache-2.0 **允许** sublicense（`LICENSE` 第 66–71 行 "…sublicense, and distribute the Work…"），因此 `PCL.Core` 部分可以被下游商用、闭源；而自定义许可部分**明文"不得再许可"**，把该目录代码以 Apache-2.0 再次授权**在协议文本上没有依据**。另需明确：Apache-2.0 的代码也**不能被别人改许可**——第 123–128 行只允许对"**你自己的修改部分**"附加或提供不同条款。
4. **仓内文本无法自洽判定的一处**：根 `LICENSE` 用 `SPECIAL DIRECTORY: Plain Craft Launcher 2/` + 一句普通说明性句子界定例外，而 `ALL THE OTHER DIRECTORIES` 未明文排除该目录，故 `Plain Craft Launcher 2/` 下的代码是否**同时**受 Apache-2.0 覆盖，在仓库文本内无法定论——自定义许可"持续不收费"与 Apache-2.0 允许收费分发在字面上相互冲突。**这属于解释问题，不是已证事实**，但足以提示任何基于该目录二次开发的第三方谨慎处理。

### 5.2 与上游 PCL 的协议差异（已核验）

对比对象：[`Meloong-Git/PCL`](https://github.com/Meloong-Git/PCL) `main` 分支根目录 `LICENCE`。

| 项目 | 上游 PCL 根 `LICENCE` | PCL-CE `Plain Craft Launcher 2/LICENCE` |
|---|---|---|
| 文件位置 | **仓库根**（单文件覆盖全仓）；上游**没有** `Plain Craft Launcher 2/LICENCE` 这个路径（404）；上游**也没有**根 `LICENSE` | 子目录 `LICENCE`；另有**新增**的根 `LICENSE`（Apache-2.0 + SPECIAL DIRECTORY） |
| 《PCL 分发有限许可》 | ⚠ **只有标题 + 外部跳转**：「《PCL 分发有限许可》\n\n详见 https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo」——**无定义、无权利条款** | ✅ **完整内联**：版权行（成都瓜皮龙科技有限公司）、一、定义（软件/原样副本/收费）、二、权利（不收费前提、七项被授予行为、接收者直接授权），共 16 行 |
| 《PCL 存储库合理使用指南》 | 轻度/重度二分 + 5 项义务 + 允许与禁止示例 + 备注 | **逐字相同**（含 `McLaunchPrecheck`、赞助解锁、主要色相必须为蓝色、CC BY-SA、discussions 链接） |
| 目录级二分 | 无 | **CE 新增** |

**核验结论**：CE 相对上游在协议层主要做了两件事——① 把上游**仅以超链接指向 shimo 文档**的《PCL 分发有限许可》**完整内联进仓库**（使"不收费"的定义、六项收入豁免、七项被授予行为、接收者直接授权等条款变得可离线查阅）；② **新增**根 `LICENSE`，用 `SPECIAL DIRECTORY` 实现 Apache-2.0 / 自定义条款的目录级二分。指南部分**未发现实质改动**。
**未能核验**：本地为浅克隆，无法逐字节 diff 历史版本，故"CE 是否**有意**加严了许可文本"无法验证，只能报告当下文本差异。

---

## 6. 实证检验：`PCL.Core` 是否真的是独立重写？

这个问题决定了"把 `PCL.Core` 标为 Apache-2.0"是否成立——若其中含衍生自自定义许可 PCL 的代码，则 Apache-2.0 授权对该部分无效。

| 检验项 | 结果 |
|---|---|
| `PCL.Core` 中引用上游问题号（`Meloong-Git/#NNNN`）的次数 | **0**（对照：`Plain Craft Launcher 2/` 中 4 处） |
| 源码注释文本重合度（提取长度 ≥16 且含中文的注释行做集合交集） | `PCL.Core` 1,750 条 vs WPF 项目 1,832 条，**交集仅 9 条（0.51%）**，且**全部是 `<param name="value">值。</param>` 之类的通用 XML 文档模板** |
| 项目自述 | `PCL.Core/README.md`："为 PCL CE 版本特别开发的核心库，由社区开发与维护" |
| 原始代码归属声明 | PCL.Core 内 3 处第三方许可头均指向 **EleCho.WpfSuite** 与 **bsdiff.net**，**无一处指向上游 PCL** |

**结论（证据支持强度：中高，但属启发式而非证明）**：现有证据一致指向 `PCL.Core` 是 CE 社区新写的库，而非从上游 PCL 移植；因此将其整体置于 Apache-2.0 之下在项目内部逻辑上是自洽的。需注意该检验基于注释文本，**不能证明不存在逻辑层面的重新实现**（尤其是认证、缓存等与上游功能重叠的模块）。

---

## 7. 风险、缺口与不一致（可操作发现）

| 级别 | 发现 | 证据 |
|---|---|---|
| 🔴 | **`PCL.Core/NOTICE` 已失效且从未被分发**。5 条中有 **3 条是死条目**（`Microsoft.Net.Compilers.Toolset`、`Microsoft.Toolkit.Uwp.Notifications`、`PolySharp` 在全部 csproj/源码中**零引用**）；同时，Apache-2.0 所要求的 NOTICE 传播机制形同虚设——**没有任何 csproj/workflow 引用该文件** | 逐包 grep：csproj_refs=0、cs_uses=0 |
| 🔴 | **内置 `transformer.jar` 来自 HMCL（GPL-3.0）但未列入任何许可清单**。`metadata.json` 列了 HMCL-dev 的两个 **Apache-2.0** 子项目（mesa-loader-windows、lwjgl-unsafe-agent），却漏了这个来自 HMCL 主仓库（GPL-3.0）的类 | jar 内 `org/jackhuang/hmcl/…`；[HMCL 仓库](https://github.com/HMCL-dev/HMCL) 标 GPL-3.0 |
| 🔴 | **内置 `linkd.exe` 是 Microsoft 专有二进制，且完全未声明**——这是全仓库**最清晰、最确定**的合规问题。版本资源自证 `© Microsoft Corporation. All rights reserved.` / Windows Server 2003 Resource Kit v5.2.3790.0，**不存在任何允许再分发的授权**；`NOTICE`、`metadata.json`、仓库内任何文本均未提及它。建议用 C# 原生互操作（`CreateHardLink` / `FSCTL_SET_REPARSE_POINT`）或自研实现替换 | §3.4(b) |
| 🟠 | **`forge-installer.jar` 无任何许可/来源声明**：上游为 `bangbang93/forge-install-bootstrapper`，**该仓库完全没有 `LICENSE` 文件** → 默认"保留全部权利"，再分发无授权。仅 1 个类、无 manifest、`metadata.json` 未列 | `unzip -l` + 上游文件清单 |
| 🟠 | **`ClipboardInterceptor.cs` 的 AGPL→Apache 重新授权声明无法在仓库内验证**：文件头声明由原作者 uye 单独重新授权，但该声明成立的前提是 uye 是 MAA 对应代码的**唯一**著作权人。若该代码路径有第三方贡献，其贡献仍为 AGPL-3.0-only，则此文件会成为 Apache/专有拆分产物中的 AGPL-only 文件——**本清单中理论严重度最高的一项**。建议向维护者索取书面授权并归档。此处仅指出风险，**不主张已构成违规** | 文件头 + MAA 仓库为 AGPL-3.0-only |
| 🟠 | **归属覆盖不全**：`metadata.json` 20 条 vs 实际 **39 个 NuGet 包** + 14 项内置资产。未列出的还有 `Font.ttf`、`mcmod.buf`、`log4j2*.xml`、`SharpZipLib`、`YamlDotNet`、`CommunityToolkit.Mvvm`、`Microsoft.Xaml.Behaviors.Wpf`、`Humanizer`、`PlainToolkit.CngProtectedData`、`System.*`/`Microsoft.Extensions.*` 一族，以及 `EleCho.WpfSuite`（源码内嵌）等 | §3.4 对照表 |
| 🔴 | **两个 BSD-3-Clause 库完全零归属**：`fNbt`（用于 `PCL.Core` + `PCL.Core.Test`）与 `Polly` 均**既不在 `NOTICE` 也不在 `metadata.json`**。BSD-3-Clause 明确要求二进制分发时复现版权声明与免责声明 | §3.4(a) |
| 🟠 | **Apache-2.0 的 NOTICE 传播义务未覆盖**：4 个 Apache-2.0 依赖中 `metadata.json` 只列了 Dapper 与 protobuf-net，`FluentValidation` 与 `SQLitePCLRaw.bundle_e_sqlite3` **缺席**；且两处清单都只给「名称+链接」，**不携带上游 NOTICE 正文** | §3.4(a) |
| 🟠 | **无 SBOM、CI 不打包 `LICENSE`/`NOTICE`**：`grep -i sbom .github/` = 0；唯一许可相关的打包项是 `<Content Include="LICENCE" />` | §2.4 |
| 🟠 | **无 SBOM、无 lock 文件**：`grep -i sbom .github/` = 0；仓库内没有 `packages.lock.json`/`project.assets.json`/`Directory.Packages.props`/`NuGet.config` → **传递依赖集合既未固定也无法离线审计**，许可证盘点天然不完整 | §3.4(a) |
| 🟡 | **GitHub 侧边栏显示 "Apache-2.0 license"**，而占代码量约 62% 且含全部实质功能的主程序目录是专有自定义许可 | 根 LICENSE 仅反映第一层 |
| 🟡 | **`PCL.Core/` 目录内没有 LICENSE 副本**（根 LICENSE 为准），`NOTICE` 又未打包 → 分发 `PCL.Core` 二进制时缺少随附许可文本 | 目录列表 |
| 🟡 | **测试从不执行**：`build-test.yml` 的 `paths-ignore` 含 `PCL.Core.Test/**`，`.slnx` 又把它排除出解决方案构建 | §2.4 |
| 🟡 | **`paths-ignore` 含 `**/LICENSE`、`**/LICENCE`** → 修改许可文件**不触发任何 CI 构建** | `build-test.yml:12-13` |
| 🟡 | **GPG 公钥文件为 UTF-16LE + BOM**，非标准 ASCII armor | `file` 判定 |
| 🟡 | **构建期密钥以明文写入生成源码**（`EnvironmentInteropGenerator`）；若 CI 产物被反编译，`PCL_MS_CLIENT_ID`/`PCL_NAID_CLIENT_SECRET`/`PCL_SENTRY_DSN` 可直接读出 | 生成器源码 + 环境变量清单 |
| 🟡 | **`README.md:7` 名称为 `# PCL Community Edition`，缺 `Plain Craft`**，与 `FormMain.xaml:13`、`csproj:27/29`、`metadata.json:2` 的正式名不一致（B6 展示形式不统一） | §5 表 |
| 🟡 | **根 `LICENSE` 尾部构造的三处张力**：`Copyright 2025 PCL Community` 被填在 Apache 附录模板中原本要求填写**版权人**的占位处（易与"许可管理人"混同）；`discribed` 为原文拼写错误；该年份与 About 页 `Copyright © 2026 PCL Community & 龙腾猫跃`（`zh-CN.xaml:2515`）年份及署名范围不一致 | `LICENSE:187-197` |
| 🟡 | **例外以目录路径而非文件清单界定**：`SPECIAL DIRECTORY: Plain Craft Launcher 2/` 对未来的新增顶层目录不产生指向，需读者自行解释为 `ALL THE OTHER DIRECTORIES` 覆盖 | `LICENSE:191-197` |
| 🟡 | **遗留死配置有踩坑风险**：`Config.cs:283` `UiLauncherHue`（默认 180）、`:288` `UiLauncherSat`、`:303` `UiLauncherTheme`（注释自陈"传说中的主题选择，但是没卵用"）等字段**全仓库无读取点**。下游二次开发者若误以为这是可用的色相开关并接到实际渲染上，就会触发 B9 的蓝色主色相要求 | `Config.cs:283-303` |
| ⚪ | 代码卫生：`IO/Download` 悬空抽象（无实现者）；`Minecraft/Launch` 仅 54 行；14 处 TODO；`ConfigGenerator` 静默吞异常；全部生成器零诊断；`DependencyGroup.InvokeInjection` 对异步委托直接 `.Wait()`（潜在死锁）；`ModStyle.cs:15` 自述 `// Partly generated by claude-sonnet-4-20250514` | 各 Agent 明细 |

---

## 8. 若你要使用 PCL-CE 的代码：操作清单

### (a) 使用 Apache-2.0 部分（`PCL.Core/`、`PCL.Core.Test/`、`PCL.Core.SourceGenerators/`、`.github/` 等）

**必须做**
1. 随分发提供 Apache-2.0 许可副本（根 `LICENSE` 即可）。
2. **修改过的文件加显著变更声明**（Apache-2.0 §4(b)）。
3. 保留全部版权、专利、商标与归属声明（§4(c)）。
4. 若上游提供 `NOTICE`，须一并传播（§4(d)）——注意本项目该文件已失效未打包，实操上请自行补全第三方归属。
5. 保留 `PCL.Core` 内 3 处第三方代码头：EleCho.WpfSuite（MIT）、bsdiff.net（MIT + BSD-2-Clause）。

**可以做**：商用、闭源、修改、再许可（Apache-2.0 允许）；但**不得**使用 PCL 商标暗示官方关联（§6 不授予商标权）。
**注意**：你得到的只是基础设施——**没有启动 Minecraft 的能力**（见 §0）。

### (b) 使用自定义许可部分（`Plain Craft Launcher 2/`）

**可以**：在**持续不收费**的前提下，存储、镜像、备份、上传、下载、向公众提供、传播、再分发**原样副本**（含压缩/解压）。
**不可以**：
1. ❌ 修改后再分发（未授予修改权）——除非接受 B5–B9 全部义务并依赖"善意合理使用不追责"的承诺。
2. ❌ 以"获得/使用副本"为条件向接收者收费（付费下载、会员门槛、强制赞助等）。
3. ❌ 再许可给第三方。
4. ❌ 命名为 `PCL 3`、`PCL++`、`PCL Pro`、`PCL 手机版`、`PCL 某服务器特供版` 之类。
**做重度二次开发则必须**：① 声明为第三方独立二次创作；② 名称以 `Plain Craft Launcher (PCL)` 开头 + 第三方修改后缀；③ 关于页**首位**署名龙腾猫跃 + 赞助链接、公开源码、继续沿用该指南文件；④ 实现不弱于 `McLaunchPrecheck` 的正版/赞助劝导；⑤ 不得出现赞助解锁式功能，界面主要色相须为蓝色。
**表述建议**：对外**不要**称该部分为"开源"，准确说法是"源码可得的专有限制许可"。

### (c) 使用其中的第三方组件

逐项遵循其自身许可：`legacyfix.jar`（GPL-3.0，注意传染性）、`transformer.jar`（HMCL/GPL-3.0）、`java-wrapper.jar`（MIT）、`lwjgl-unsafe-agent.jar`（Apache-2.0）、`lucide` 图标（ISC）、其余 NuGet 包按 §3.4(a) 表。**GPL-3.0 组件的处理提示**：仓库以独立 `.jar` 形式分发、由独立 JVM 进程加载，通常可论证为"聚合（mere aggregation）"而非衍生；但**若你把它静态并入自己的程序，GPL-3.0 的传染性就会触发**。

---

## 9. 方法论：本次多 Agent 协作

| Agent | 范围 | 交付 |
|---|---|---|
| A1 | `PCL.Core`（544 文件 / 45k 行） | 目录命名空间地图、IoC/EventBus/加密/网络/认证/Link 深潜、扩展点表、`NOTICE` 全文、代码质量观察 |
| A2 | `Plain Craft Launcher 2`（205 文件 / 86k 行） | 页面/控件/模块归属、`metadata.json`、`Application.xaml` 色彩系统、`Resources/` 逐文件溯源、文件级许可头普查 |
| A3 | 第三方依赖与许可清单（范围最广） | 39 个包的**逐版本 nuspec 核验**、7 项内置二进制的溯源与可再分发性判定（**含 `linkd.exe` 的 PE 版本资源提取**）、归属机制充分性评估、6 项最高优先级整改建议 |
| A4 | 生成器 / 测试 / CI | 5 个生成器机制、测试覆盖评估、workflow、`cliff.toml`、GPG 公钥身份 |
| A5 | 自定义协议规范提取 | 规范表（A-1…A-8 / B-0…B-7）、合规审计、**上游 PCL 协议对比**、第三方实操清单、`LICENSE` 尾部构造的张力分析 |
| 主控 | 交叉复核与统稿 | 独立读取 `LICENCE`/`NOTICE`/`metadata.json`/`PageSetupAbout.xaml`/`ModLaunch.cs`；独立核验 `forge-installer.jar`、`transformer.jar`、`ClipboardInterceptor.cs`；独立做「注释血缘」检验（§6）、`metadata.json`⇄About 页绑定验证、许可规范表与合规审计；**对 `linkd.exe` 一度作出错误的"证据不足"判定，后经 A3 提示自行复现并改正**（见下） |

**协作要点与纠错记录**：并行分工让 138k 行代码在数轮内完成普查，且多路发现可相互印证（例如 `NOTICE` 失效被 A1、A4、主控三路独立命中；`metadata.json` 与 About 页的绑定被主控与 A3 独立确认）。

**⚠ 一次值得记录的反向纠错（主控出错、子 Agent 正确）**：A2 曾从 PE 特征推断 `linkd.exe` 是"Microsoft Windows Server Resource Kit 的 linkd.exe"。主控用 `strings -a -n 4 linkd.exe | head -40` 复核，只看到 `kernel32.dll`、`linkd.pdb` 与 CRT 控制台导入，未见任何版权字符串，遂**判定 A2 证据不足并"降级"为"来源无从确认"**。随后 A3 用 UTF-16LE 方式提取 PE 资源，取出完整的 `VS_VERSION_INFO`：`CompanyName = Microsoft Corporation`、`LegalCopyright = © Microsoft Corporation. All rights reserved.`、`FileVersion = 5.2.3790.0 built by: dnsrv_dev(v-smgum)`。主控随即用 Python 直接解码该文件的 UTF-16LE 序列**独立复现了同一结果**，确证 **A2 原本是对的、主控的"纠正"是错的**，并在本报告中改正。
**根因**：`strings` 默认只扫描 ASCII 字节序列，而 Windows 版本资源以 **UTF-16LE** 存储；叠加 `head -40` 截断，导致证据被静默丢弃——而"没有找到证据"被误当成"证据表明不存在"。**教训**：在 Windows PE 取证中必须显式处理宽字符资源，且不能用"字符串搜索无命中"论证"某属性不存在"（否定性结论需要穷尽性证据，而非有界搜索）。这一条也直接提升了本次审计的严重度判定：`linkd.exe` 从"⚠ 来源不明"上升为 🔴 **"确证的 Microsoft 专有二进制、无再分发授权、且零归属"**——全仓库最清晰的一处合规问题。

**其余交叉复核成果**：① 主控把 A5 的"命名完全满足"细化为"部分满足"，补入 `README.md:7` 缺 `Plain Craft` 这一不合格处；② 主控补充了 A1/A2 未覆盖的两项独立证据——`metadata.json` 与 About 页 `ItemsSource` 的绑定关系（证明许可清单**确实会渲染给用户**）、以及 §6 的"注释血缘"检验；③ A3 修正了主控的两处数据错误：NuGet 包数应为 **39 个不重复包（45 条引用）**而非 30 个，`fNbt` 的许可应为 **BSD-3-Clause** 而非 MIT。

---

## 10. 一句话总结

PCL-CE 用**目录边界**把三种法律状态干净地切开了：`PCL.Core` 是新写的 Apache-2.0 基础设施，`Plain Craft Launcher 2/` 是继承自龙腾猫跃、由**自定义《PCL 分发有限许可》+《存储库合理使用指南》**约束的专有主体，第三方组件则散落在 NuGet、内置二进制与运行时下载三条通道上；该自定义协议确定的规范可归纳为**"免费可原样再分发，改造则必须满足命名、署名、开源、正版劝导、禁赞助解锁（主色相蓝）五项义务"**；PCL-CE 自身对这五项均可验证为大体满足，真正的薄弱环节不在协议合规，而在**第三方归属的完整性**——失效的 `NOTICE`、未声明的 HMCL GPL-3.0 组件、**确证为微软专有且零归属的 `linkd.exe`**，以及"Apache-2.0"标签对主程序目录专有性质的掩盖。
