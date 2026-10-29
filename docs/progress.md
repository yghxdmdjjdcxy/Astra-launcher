# Astra Launcher — 开发进度与里程碑记录

> 目标（已确认范围）：完成下载/版本/实例/Java/启动/设置链路 → 官方 Microsoft 登录 → 服务器列表；原版 + Fabric/Quilt/Forge/NeoForge；官方源为主、BMCLAPI 镜像可切换/自动回退；保持手写 MVVM；不含安装器与自动更新；最终真实 1.21.1 下载 + 离线启动验收。
> 节奏：连续推进，每个里程碑自测通过后记录并继续；不破坏既有测试，持续补测试。

## 里程碑总览

| 里程碑 | 内容 | 状态 |
|---|---|---|
| Phase 0 | 工程审计（只读） | ✅ 完成（见 `docs/phase1-verification.md`） |
| Phase 1 | Git 基线 + Release 发布包 + GUI 验证 | ✅ 完成（见 `docs/phase1-verification.md`） |
| **M1a** | 下载引擎加固 + 基础服务（路径/设置/下载源/日志轮转/单实例）+ 实例页 UI 安全修复 | ✅ 完成（见下） |
| **M1b** | 版本清单/元数据/安装计划/下载（client/libraries/assets/log4j）+ 下载页 UI + 实例版本选择器 | ✅ 完成（见下） |
| **M2** | 启动链路修复接线 + 启动 UI + 真实离线启动 Minecraft 到主菜单（E2E） | ✅ 完成（见下） |
| **M3** | 多线程分段下载 + 设置页/Java 选择 + 额外参数转发 + legacy 库回退 + 精确 1.21.1 E2E | ✅ 完成（见下） |
| **M4** | 官方 Microsoft 登录：OAuth + Xbox/Minecraft 链 + DPAPI 令牌 + 账户页/多账户/离线档案 | ✅ 完成（见下） |
| **M4.5** | UI 可理解性改版（卡片布局/中文说明/步骤引导/状态汇总/服务器占位说明） | ✅ 完成（见下） |
| M5 | Fabric/Quilt/Forge/NeoForge 安装与启动集成 | ⏳ 下一步 |
| M6 | 服务器列表 + --server/--port 一键加入 | 待开始 |
| M7 | 全量测试、真实端到端验收、文档与最终报告 | 待开始 |

> 顺序调整说明：M1b 完成后优先打通"真实可启动"链路（原 M3 提前为 M2），设置页/Java 管理作为 M3 紧随其后，符合用户"先下载/版本/实例/Java/启动/设置"的可用性优先目标。

---

## M1a 记录（2026-10-25 晚间）

### 交付内容

1. **下载引擎重写（AstraLauncher.Downloader）**
   - 超时模型：`RequestHeadersTimeout`（连接/响应头）与 `ReadIdleTimeout`（每读到数据重置）分离，彻底移除"整请求 45s 预算"。
   - 超时与用户取消分离：新增 `DownloadTimeoutException`，超时可重试；`OperationCanceledException` 仅用于暂停/取消。
   - 完整性：新增 `ExpectedSize`、下载结束校验 received==total/ExpectedSize、SHA-1 校验；`DownloadIntegrityException.DeletePartialFile` 控制重试时是否丢弃半文件。
   - 断点续传：`.astra.part.meta` 记录 ETag/Last-Modified/总长度，发 `If-Range`，并校验 206 的 `Content-Range.From == existing`；416 自动重建；服务器忽略 Range 时安全重启。
   - 重试：5xx/408/425/429 可重试并遵循 `Retry-After`；4xx 快速失败；指数退避有上限。
   - 生命周期/竞态：worker 循环全兜底不退出；`TryBegin` 增加运行守卫；Pause→Resume 使用 generation + 取消原因修复竞态；Dispose 结算排队/暂停项（Completion 不再悬挂）；进度事件移出锁且节流、订阅者异常隔离；host 信号量释放。
2. **基础服务（Core/Infrastructure）**
   - `ILauncherPaths` 扩展 9 个目录 + `EnsureCreated()`；`LauncherSettings` + `SettingsService`（settings.json 原子写、坏文件隔离、默认值/规范化、Changed 事件）；`IDownloadSourceResolver` + `DownloadSourceResolver`（Official/Mirror/OfficialWithFallback、Mojang 元数据/库/资源映射）；`FileLoggerProvider` 按日文件 + 10MB 轮转 + 保留 5 份 + 全异常兜底。
3. **运行态加固**
   - 单实例互斥（`Local\AstraLauncher.SingleInstance`，含 abandoned mutex 处理）；ContentRoot 固定为 exe 目录，appsettings 稳定加载；启动异常处理器提前注册并友好提示；OnExit 线程池执行 StopAsync 避免 UI 死锁。
4. **实例页 UI 修复**
   - 删除确认对话框（区分受管目录/仅登记两种文案）；刷新/重命名后按 Id 强制重选（record 值相等曾导致 WPF 选中丢失）；异常中文提示 + `ILogger` 记录；表单标签与列表表头。
5. **游戏进程（Game/Launch）**
   - `GameProcess` stdout/stderr 持续排空（最近 2000 行缓存 + OutputReceived 事件），修复管道写满死锁；`Kill()`/`IAsyncDisposable`/退出码上报；`CreateNoWindow=true`。
6. **实例清单（Game/Instances）**
   - `.bak` 每次成功写入后更新 + 损坏清单隔离恢复；删除顺序改为"先写清单再删目录"；复制目标不得位于源目录内（防自复制）；受管目录边界改为根前缀校验。

### 验证结果（本机实测）

| 检查 | 结果 |
|---|---|
| `dotnet build AstraLauncher.sln -c Release` | **0 警告 / 0 错误** |
| `dotnet test ... -c Release` | **65 通过 / 0 失败 / 0 跳过**（原 15 → 65，新增下载队列 13 项、日志/设置/下载源/路径、进程管道、实例清单、VM 用例） |
| GUI 冒烟（Release exe，UIA + Win32 对话框） | 主窗口就绪；实例页标签/表头齐全；创建实例成功；刷新后选中保持；删除弹出确认框（文案正确，点"是"后目录删除、状态更新、列表清空）；单实例提示"Astra Launcher 已在运行。"并正常退出；主窗口关闭退出码 0；运行期日志无 Critical/Error |
| 测试数据清理 | 仅保留日志文件；无残留 manifest/实例目录/进程 |

证据：`docs/evidence/m1a/`（build-release.log、test-release.log、gui-smoke.log、instances-page.png、delete-confirm.png）。

### 遗留到后续里程碑

- `IDownloadSourceResolver` 当前以默认镜像 URL 注册（设置页 M2 接入后随设置动态更新）。
- 下载页/版本列表 UI 未做（M1b）；账号/服务器页仍为占位（M4/M6）。
- `GameCommandBuilder` 的 legacy classpath、路径边界校验、os.arch rules、未知占位符、logging 配置等留待 M3（启动接线时统一处理并补测试）。

---

## M1b 记录（2026-10-25 深夜）

### 交付内容

1. **版本清单服务（Infrastructure）**：`VersionManifestService` 实现 `IVersionManifestService` —— 官方清单 URI、`cache/version_manifest_v2.json` + `.fetched` 双文件缓存（30min TTL）、内存缓存 + 信号量串行刷新、按 `DownloadSourceMode` 顺序尝试候选（官方→镜像）并自动回退、全失败抛 `InvalidOperationException`（保留最后错误）、网络失败回退旧缓存、原子写缓存、System.Text.Json 严格解析、`release`/`snapshot` 过滤 + ReleaseTime 降序。
2. **元数据存储（Game）**：`VersionMetadataStore.EnsureResolvedAsync` 递归确保本版本及父版本 JSON（manifest summary URL + 候选源 + SHA-1 校验 + 临时文件原子落盘 + 继承环检测），最后用 `VersionMetadataResolver` 返回合并元数据；元数据模型/解析器/合并新增 `logging`（log4j2 配置）支持。
3. **安装服务（Game）**：`VersionInstallService` 实现 `CreatePlanAsync` / `InstallAsync` / `IsVersionInstalledAsync`
   - 六类文件：`version-json`、`client`、`library`（含 natives classifier、os/arch/version/features rules 本地求值、路径限制在 libraries 目录内）、`asset-index`、`asset`（3888 个对象、按 hash 去重、官方 resources URL → 镜像候选）、`logging`；
   - 下载：按设置并发度，逐文件候选源回退，`DownloadRequest` 携带 size+SHA-1，已完成文件跳过（大小匹配），取消贯通（取消句柄），失败保留 `.part` 续传；
   - 进度：`VersionInstallProgress`（总文件/完成/总字节/已下载/阶段/当前文件），250ms 节流 + 完成时 100% 收尾。
4. **UI**：下载页重做（版本列表 + 刷新/下载/取消 + 进度条 + 状态）；实例页版本输入改为**可编辑 ComboBox**（版本清单 103 项自动填充，离线仍可手输）；Shell 启动时并行初始化实例页/下载页/版本列表；依赖注入新增 HttpClient、DownloadQueue、版本清单/元数据/安装服务。
5. **工程**：Game 工程新增 Downloader 项目引用与 Logging.Abstractions 依赖；DownloadQueue 增加同步 `IDisposable`（宿主同步释放安全）。

### 验证结果（本机实测）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 警告 / 0 错误** |
| `dotnet test` | **93 通过 / 0 失败 / 0 跳过**（新增 14 清单服务 + 5 元数据存储 + 4 安装服务 + 4 下载页 VM 用例） |
| **真实网络集成 harness**（Mojang + BMCLAPI 实测） | 清单 103 个正式版本（latest 26.3）；镜像清单 HTTP 200（278 KB）；1.21.1 元数据解析成功（mainClass、Java 21、97 个库、assetIndex 17、log4j2 配置）；安装计划 **3962 个文件 / 919,909,872 字节**（asset=3888、library=70、client=1、asset-index=1、logging=1、version-json=1）；asset index 落盘；随机小资源对象经下载队列真实下载并 SHA-1/大小校验通过 |
| GUI 冒烟（真实网络） | 主窗口就绪；下载页显示"已加载 103 个正式版本。"并渲染版本列表；实例页版本 ComboBox 展开 **103 项**；关闭退出码 0；运行期日志无 Critical/Error |

证据：`docs/evidence/m1b/`（real-network-harness.log、gui-smoke.log、downloads-page.png、instances-version-picker.png、build/test 日志）。

### 遗留到后续里程碑

- 真实 1.21.1 **完整下载 + 离线启动**（约 877 MiB）留到 M2/E2E：需要启动命令构造修复、Java 选择、进程日志面板，以及启动前自动安装（调用 M1b 的安装服务）。
- 设置页 UI（下载源切换/并发/内存/快照显示）在 M3；当前安装服务已读取 LauncherSettings 的并发与下载源。
- 镜像 URL 映射对 piston-meta/piston-data 的路径是否与 BMCLAPI 完全兼容需在真实全量下载中验证（缓存回退已兜底）。

---

## M2 记录（2026-10-25 深夜，关键 E2E 达成）

### 交付内容

1. **启动命令构造修复（GameCommandBuilder 重写，449 行）**
   - 共享 `LaunchRuleEvaluator`：os.name / os.arch（x86_64/amd64/aarch64 别名）/ os.version（1s 超时正则）/ features（全 false 才匹配），最后匹配生效；builder、library 过滤、natives 解压三处共用。
   - 路径根约束（`LaunchPathGuard`）：版本 id、版本目录、natives 目录、client jar、library artifact、native archive 全部限制在对应根目录内并拒绝 `..`/绝对路径，异常消息含原始路径与解析路径。
   - client jar：`downloads.client.path` → `versions/<id>/<id>.jar` → 继承版本父 jar 回退。
   - legacy（<1.13）自动补 `-cp ${classpath}` 与 `-Djava.library.path=${natives_directory}`；`-Xmx` 移到元数据 JVM 参数之后；logging（log4j2）参数按 `assets/logging/<id>` 是否存在自动追加并替换 `${path}`；新增 `${auth_xuid}/${clientid}/${user_properties}/${auth_session}`，替换后残留 `${` 抛 `GameLaunchValidationException`；Maven 坐标回退；natives zip-slip 校验保留。
2. **启动编排（GameLaunchService）**：检查实例目录 → `InstallAsync`（自动补齐/校验游戏文件，进度映射）→ 解析元数据 → Java 选择（显式路径优先；否则精确匹配 `javaVersion.majorVersion`，再取最小可用高版本；无可用 Java 给出含版本列表的错误）→ 构建命令 → 启动进程并上报 `LaunchProgress`；离线会话日志不含令牌。
3. **离线档案（OfflineProfile）**：Java `UUID.nameUUIDFromBytes("OfflinePlayer:"+name)` 兼容的 MD5 UUID v3（version/IETF variant 位、Java 字节序），已知值 `Notch → b50ad385-829d-3141-a216-7e7d7539ba7f` 交叉验证；玩家名 1..16 校验。
4. **进程抽象**：`IGameProcess` / `IGameProcessLauncher`（便于测试与 UI），stdout/stderr 持续排空 + 最近 2000 行缓存；`Kill()`/退出码上报。
5. **UI**：实例页新增「启动游戏 / 结束游戏」、启动进度条与阶段文本、游戏日志面板（最新在上，上限 500 行）；启动使用设置中的内存/Java/离线玩家名；DI 注册 GameCommandBuilder / GameProcessLauncher / GameLaunchService。

### 验证结果（本机实测）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 警告 / 0 错误** |
| `dotnet test` | **126 通过 / 0 失败 / 0 跳过**（新增 builder 9、启动编排/离线档案 22、启动 UI 2 等） |
| **真实下载 E2E（Downloads 页）** | 通过下载页选择版本并下载：**4670 个文件 / 527.5 MiB**，约 20.5 分钟，SHA-1 与大小全部校验通过；进度条 100%，状态"版本 1.21.11 已下载并校验完成。" |
| **真实离线启动 E2E** | 实例页创建实例 → 点击启动（自动检查文件/选择 Java）→ 启动出 `java` 进程，窗口 **`Minecraft 1.21.11`（GLFW30）**，截图确认进入 **Minecraft 主菜单**（Singleplayer/Multiplayer/Realms/Options/Quit Game）；WM_CLOSE 后应用状态"**游戏已退出（退出码 0）**"；应用退出码 0，运行期日志无 Critical/Error |
| 文件落盘 | `versions/1.21.11/1.21.11.jar`（31 MB）、libraries 75 个 jar（65.6 MB）、assets/indexes/29.json、assets/objects 4591 个对象、assets/logging；应用数据合计 **527.9 MB** |

证据：`docs/evidence/m2/`（minecraft-main-menu.png、minecraft-first-screen.png、launcher-after-launch.png、downloads-complete.png、download-install.log、launch-main-menu.log、launch-exit.log、launch-diagnostic.log、build/test 日志）。

### 说明与遗留

- 本次真实下载选中了 **1.21.11**（版本列表选择逻辑按子串匹配 `1.21.1` 命中了 `1.21.11`）；启动/主菜单均已验证。**精确 1.21.1 的端到端验证**放到下一里程碑榜首（已安装文件为 1.21.11，需再装 1.21.1 的 client/libs/assets 差异文件）。
- builder 队友指出的 legacy（<1.13）安装链缺口：installer 对无 `downloads` 的旧版库/natives-only 库未做 Maven 回退，legacy E2E 留到 M5 加载器/旧版兼容阶段处理。
- `ExtraGameArguments` / `ExtraJvmArguments` 目前未转发（builder 无扩展点），随 M3 设置页一起补齐。
- 游戏日志面板改为"最新在上"以避免需要滚动；后续可加自动滚动条。

---

## M3 记录（2026-10-26 晚间：多线程下载 + 设置页 + 精确 1.21.1 E2E）

### 交付内容

1. **单文件多线程分段下载（DownloadQueue）**
   - 新增 `DownloadQueueOptions`：`EnableSegmentedDownloads=true`、`SegmentsPerFile=4`、`MinimumSegmentSizeBytes=2MiB`、`MaximumConcurrentSegments=8`（均有范围校验）。
   - 大文件（已知大小 ≥ 阈值）先发 `Range: bytes=0-0` 探测：206 → 按段并行下载（每段独立 Range、独立 FileStream 写同一预分配 `.astra.part` 的不同偏移，`SemaphoreSlim` 限制并发段数，段内重试 3 次并校验 ContentRange）；200（服务器忽略 Range）→ 复用响应回退单流；小文件/未知大小 → 单流。
   - 进度聚合单调不回退；任一段最终失败取消其它段并抛原始异常；取消/暂停删除 part（分段模式不支持续传，属设计取舍）；成功后长度 + SHA-1 校验并原子替换。
   - App DI 队列调整为 8 workers / host 6 并发 / 4 段 / 8 并发段。
2. **设置页 + Java 运行时选择（WPF/MVVM）**：`SettingsPageViewModel` 加载/保存 `settings.json`（内存、并发下载、下载源、镜像地址、Java 自动/手动、离线玩家名、显示快照、额外 JVM/游戏参数）；Java 探测列表 + 自动选择；异常中文提示 + 日志；`PageKey.Settings` + 左侧"设置"导航 + 设置页 XAML；Shell 启动初始化设置页；App DI 注册。
3. **额外 JVM/游戏参数真正转发**：`GameLaunchRequest` 新增可选 `ExtraJvmArguments/ExtraGameArguments`；Builder 将 JVM 额外参数放在 `-Xmx` 之后、mainClass 之前，游戏额外参数追加在 game 参数之后；`GameLaunchService` 支持空格分词 + 双引号整体参数、未闭合引号快速失败、日志不记录参数内容。
4. **legacy（<1.13）库安装回退**：无 `downloads.artifact` URL 的库按 Maven 坐标到 `libraries/<path>`，URL 指向 `https://libraries.minecraft.net/<mavenPath>`；natives classifier 缺失时按坐标+classifier 回退；保留路径根校验/去重。真实旧版启动端到端仍留 M5 验证。
5. **精确 1.21.1 端到端**：下载页精确选中 `1.21.1`（避免 1.21.11 子串误选）→ 下载 → 实例页创建实例并启动 → 进入 Minecraft 1.21.1 主菜单 → 退出码 0。

### 验证结果（本机实测）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 警告 / 0 错误** |
| `dotnet test` | **144 通过 / 0 失败 / 0 跳过**（新增分段下载 6、设置页 4、导航、额外参数、legacy 回退等） |
| **多线程下载效果** | 1.21.1：**3962 文件 / 877.3 MiB / 686 秒**（约 1.28 MiB/s；client jar 25.6 MB 分段下载）；对比 M2 单流 1.21.11：527.5 MiB / 1242 秒（约 0.43 MiB/s）→ 综合吞吐约 **3 倍** |
| **精确 1.21.1 E2E** | 状态"版本 1.21.1 已下载并校验完成。"；启动出 `java` 进程，窗口 **`Minecraft 1.21.1`（GLFW30）**，点击 Continue 后进入 **1.21.1 主菜单**；WM_CLOSE 后应用状态"**游戏已退出（退出码 0）**"；应用退出码 0，运行期日志 0 Critical / 0 Error |
| 设置页 GUI 冒烟 | 12/12 标签齐全；点击保存 → "设置已保存。"；`settings.json` 正确写入（4096 MiB、并发 4、源=OfficialWithFallback、镜像=BMCLAPI、离线名 Player）；关闭退出码 0，日志无错误 |
| 磁盘 | 应用数据 1.30 GB（1.21.11 + 1.21.1 两套版本文件），C: 剩余约 4 GB |

证据：`docs/evidence/m3/`（minecraft-1.21.1-main-menu.png、minecraft-1.21.1-first-screen.png、downloads-1.21.1-complete.png、settings-page.png、e2e-1.21.1.log、settings-smoke.log、build/test 日志）。

### 说明与遗留

- 分段下载设计上不支持断点续传（大文件暂停会丢弃该文件 part 重新开始）；1.21.1 E2E 全程未中断。
- 设置保存后镜像地址/显示快照的运行期联动尚未接线（`IDownloadSourceResolver` 启动时以默认镜像构造，修改镜像需重启；快照过滤仍待下载页接入）。
- 一次失败的 E2E 尝试中出现过 `DownloadQueue` 被提前 Dispose 的一次性异常，后续完整 E2E 未复现（临时诊断代码已移除）；作为稳定性观察项。
- 本机 HTTP 实际经本地代理（127.0.0.1:7890），分段下载仍按请求并行生效。
- 真实 legacy（1.8.9 等）完整启动留 M5 加载器/旧版兼容阶段验证。

### 单文件发布（2026-10-26）

- 新增 `scripts/publish-single-file.ps1`：`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`，产出**唯一文件** `artifacts/AstraLauncher-win-x64-single/AstraLauncher.exe`。
- 结果：**62.1 MB（65,079,284 字节）**，自包含 .NET 10/WPF 运行时（目标机无需安装 .NET），ProductVersion `1.0.0+d6892b7…`，SHA-256 `E7B839E5EE2AF529F3C8B3B923A1A4023B2F9E1EDA5E975817D3EA88A0774FE9`。
- `AstraLauncher.App.csproj` 将 `appsettings.json` 标记为 `CopyToPublishDirectory=Never`（该文件可选，默认日志级别与内容等价），保证发布目录只有 1 个 exe；普通文件夹构建仍保留该文件。
- 单文件 GUI 冒烟：启动 2 秒内出现主窗口；首页/设置/下载导航正常；关闭退出码 0；运行期日志 0 Critical/0 Error；启动后 exe 目录仍只有 1 个文件（原生组件解压到 `%TEMP%\.net`，属单文件 WPF 正常行为）。
- 证据：`docs/evidence/single-exe/`（single-file-welcome.png、single-file-settings.png、smoke.log、publish.log、build-info.txt）。

### `.minecraft` 共享目录（2026-10-26 深夜，兼容其他启动器）

- `ILauncherPaths`/`LauncherPaths` 拆分：`ApplicationDataDirectory`（启动器私有：settings/logs/instances/java-runtime/cache/temp，仍在 `%LOCALAPPDATA%\AstraLauncher`）与 `MinecraftDirectory`（共享：versions/libraries/assets）。
- 默认 Minecraft 目录解析顺序：exe 旁边的 `.minecraft`（已存在即用，便携模式）→ 已存在的 `%APPDATA%\.minecraft`（复用官方启动器/PCL 的安装）→ 在 exe 旁创建 `.minecraft`（若安装目录只读则回退 `%APPDATA%\.minecraft`）。目录会在设置页显示，可编辑并带"浏览…"文件夹选择器，保存后立即生效。
- 标准布局：`versions/<id>/<id>.json+jar`、`libraries/`、`assets/indexes|objects|logging`，其他启动器可直接识别；已有文件按 SHA-1/大小校验后复用，只补缺失文件（含 PCL 已安装版本，本地 metadata 优先读取，无需重新下载）。
- 自动迁移：第一次以新版本启动时，若旧布局 `%LOCALAPPDATA%\AstraLauncher\{versions,libraries,assets}` 存在且目标 `.minecraft` 还没有版本，则把三目录迁移（同卷 Move，跨卷复制后删除）到 `.minecraft`，启动器私有目录保持不变；目标已有版本时跳过迁移避免覆盖。
- 修复：`VersionMetadataStore` 与 `GameLaunchService` 里遗留的 `ApplicationDataDirectory` 硬编码改为 `MinecraftDirectory`（否则迁移后启动会去旧路径找 version json）。
- 验证（本机实测）：
  - 便携模式（exe 旁空 `.minecraft`）：启动 45 秒完成 1.3 GB 迁移；`.minecraft` 得到 **versions=2（1.21.1、1.21.11）/ 117 个库 jar / 8362 个资源对象 / 3 个 asset index / logging**，旧目录的 versions/libraries/assets 消失，启动器私有目录保留；设置页显示便携路径；创建实例启动 **Minecraft 1.21.1**（GLFW30）→ 主菜单截图 → 退出码 0，日志 0 Critical/0 Error。
  - 单文件 exe（放在 `.minecraft` 旁边）：启动 5 秒，设置页解析到 exe 旁 `.minecraft`，启动同一实例 20 秒进入 **1.21.1 主菜单**，退出码 0，0 Critical/0 Error；exe 目录保持"exe + .minecraft"两项。
  - 既有 `%APPDATA%\.minecraft`（官方 26.3）也被正确识别并复用（测试中曾写入的临时 1.21.1 文件已清理，仅保留 26.3）。
- 证据：`docs/evidence/minecraft-directory/`（settings-minecraft-directory.png、minecraft-1.21.1-from-portable-main-menu.png、portable-e2e.log、single-exe-e2e.log、layout.txt、build/test 日志）。
- 遗留：Fabric/Quilt/Forge/NeoForge 等加载器版本（PCL 自定义版本）要到 M5 才会出现在版本列表并能启动；旧版（<1.13）启动链仍留 M5。

---

## M4 记录（2026-10-27 晚间：官方微软登录链 + 离线档案 + 账户页）

### 交付内容

1. **认证链（AstraLauncher.Authentication）**
   - `DeviceCodeOAuthClient`：Microsoft identity platform 设备码流（`consumers` 端点，scope `XboxLive.signin offline_access`），设备码请求/轮询（authorization_pending / slow_down / expired_token / authorization_declined）、刷新令牌。
   - `XboxLiveAuthenticator`：`user.auth.xboxlive.com` RPS 认证（`d=<access_token>`）、`xsts.auth.xboxlive.com` RPS 授权（`rp://api.minecraftservices.com/`），解析 uhs/xid（XUID），映射 XErr（无 Xbox 档案/地区/儿童账户/封禁）。
   - `MinecraftAuthenticator`：`login_with_xbox`（`XBL3.0 x=<uhs>;<xsts>`）→ `/minecraft/profile`，UUID 32 位 hex 转标准连字符格式；404 → 未拥有 Java 版。
   - `DpapiTokenProtector`：Windows DPAPI（CurrentUser）加解密刷新令牌。
   - `AccountStore`：`%LOCALAPPDATA%\AstraLauncher\accounts.json`，refresh token 仅以 DPAPI 密文持久化、不落 access token，原子写、损坏文件隔离；`AccountService`：账户增删选、设备码流程编排、启动前刷新链（refresh → Xbox → XSTS → Minecraft）、离线 UUID（Java 兼容，Notch 测试向量）。
2. **契约与启动集成**：`AccountProfile/DeviceCodeInfo/MicrosoftTokenSet/XboxLiveToken/XstsToken/MinecraftSessionInfo/AccountAuthenticationException/AccountErrorCode` + `IAccountService` 等；`LauncherSettings.MicrosoftClientId`；实例启动时按选中账户构建会话（Microsoft → 刷新后的 Authenticated GameSession，含 XUID/client id；Offline → `OfflineProfile`），已删除 Core 里重复的旧 AccountProfile 记录。
3. **账户页（WPF/MVVM）**：Client ID 输入、添加 Microsoft 账户（设备码区：浏览器地址/用户码/有效期/取消）、离线档案创建（≤16 字符）、账户列表（官方/离线标签、UUID/XUID）、选择/删除（确认）/刷新；Shell 启动初始化、App DI 注册全部认证服务。
4. **配置说明**：真实 Microsoft 登录需在 Azure 应用注册（公共客户端/移动和桌面应用、允许公共客户端流、委托权限 XboxLive.signin + offline_access）后把 Application (client) ID 填入账户页；未填时离线档案完全可用。

### 验证结果（本机实测）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 警告 / 0 错误** |
| `dotnet test` | **220 通过 / 0 失败 / 0 跳过**（新增认证 HTTP 流 25+、DPAPI、账户服务、账户页 VM、导航等 68 个用例） |
| 账户页 GUI（真实运行） | 8/8 关键控件；添加离线档案 **M4Tester** → 列表显示"离线档案 + UUID(d7a8b078-…)"；`accounts.json` 落盘；Client ID 为空时给出中文提示与对话框 |
| 选中账户启动 | 选中离线档案后创建实例 → 启动 **Minecraft 1.21.1**（GLFW30）→ 主菜单；退出码 0；`settings.SelectedAccountId`/OfflinePlayerName 持久化；日志 0 Critical / 0 Error |
| 真实 Microsoft 端点联通 | 用占位 Client ID 触发 `添加 Microsoft 账户` → 真实请求 `login.microsoftonline.com/consumers/oauth2/v2.0/devicecode`，返回 HTTP 400 → 账户页中文错误提示"Microsoft 或 Minecraft 服务返回了无效响应"（预期行为，证明 OAuth 请求/错误映射路径已接通；日志只记录错误码，不含任何令牌） |
| DPAPI | 单元测试：Protect/Unprotect 往返、密文不含明文、坏密文抛错；AccountService 测试断言 refresh token 仅以加密形式交给 store |

证据：`docs/evidence/m4/`（accounts-page.png、offline-account-added.png、microsoft-clientid-required.png、launch-with-offline-account-main-menu.png、accounts-e2e.log、oauth-error.log、build/test 日志）。

### 说明与遗留

- **真实 Microsoft 账户端到端登录未验证**（缺少有效的 Azure 公共客户端 Client ID）；Xbox/XSTS/Minecraft 链、DPAPI 存储、刷新逻辑由单元测试与真实端点错误路径覆盖，填入合法 Client ID 后即可走完设备码登录。若你提供 Client ID，我可以补真实登录 + 启动验收。
- 存在性提示：Microsoft 登录失败时不会自动回退离线启动（避免以错误身份进多人游戏），会给出中文错误并中止。
- legacy（<1.13）完整启动与 Fabric/Quilt/Forge/NeoForge 接入留 M5。

---

## M4.5 记录（2026-10-27 晚间：UI 可理解性改版）

### 改动内容（纯 UI/展示层，不改变下载/启动/认证逻辑）

1. **全局框架**：窗口 1220×780（最小 1000×660）；左侧深色导航改为「导航 / 配置」分组，底部显示版本；顶部显示"当前账户"；底部固定显示使用引导与版本号；统一字体层级、卡片容器、主按钮（蓝）/次按钮/危险按钮样式与提示文字样式。
2. **首页**：新增"三步开始游戏"引导卡（① 下载版本 / ② 创建实例 / ③ 启动游戏，每张卡带说明与跳转按钮）；"当前状态"汇总 Minecraft 目录、当前账户、Java 检测数量、正式版本数量；"小提示"列出首次下载大小、离线档案与下载失败处理。
3. **游戏实例**：拆成"新建 / 导入实例"、"实例列表（工具栏：刷新/重命名/复制/删除，条目显示名称/版本/目录）"、"Java 运行时（显示版本与路径 + 自动选择说明）"、"启动游戏（当前选中实例 + 空闲提示 + 进度/阶段）"、"游戏日志（最新在上）"卡片；底部状态行。
4. **下载**：区分"工具栏+说明"、"可选版本（版本号 + 发布日期）"、"已选版本 + 进度/提示"；空闲时显示"尚未开始下载。选择版本后点击「下载所选版本」。"；重复说明多线程与文件复用。
5. **账户**：三者并排/分区展示——"① 官方 Microsoft 登录"（Client ID、添加/取消、设备码蓝框）、"② 离线档案"（玩家名说明）、"③ 账户列表"（官方/离线标签、UUID/XUID、删除/刷新、选中账户用于启动说明）。
6. **设置**：四个分组卡片（Minecraft 目录 / 性能与下载 / Java 运行时 / 游戏与参数），下载源改为中文选项（官方源 / 仅镜像 / 官方优先自动回退），每项附说明与默认值提示；底部固定"保存设置 + 状态"。
7. **服务器**：由占位一句话改为"计划中的功能"说明卡（保存地址、`--server/--port` 自动拼接、从 servers.dat 导入），明确 M6 计划与当前替代操作。
8. **修复**：首页 Java 汇总改为取设置页检测结果（显示"已检测到 9 个 Java"）；消除 WPF `Trigger` 对自身 `Text` 条件的非法用法（改为 `DataTrigger`，此前会导致实例页运行时崩溃）。

### 验证（本机实测）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 警告 / 0 错误** |
| `dotnet test` | **220/220 通过**（UI 改版不影响既有用例） |
| UI 全页面冒烟（真实运行 + 截图） | 首页 / 游戏实例 / 下载 / 账户 / 服务器 / 设置逐页导航：关键文本与控件全部命中（7/7、5/5、6/6、2/2、6/6 等）；每页截图；关闭退出码 0；修复后无新的 Critical/Error |
| 首页状态汇总 | 显示 Minecraft 目录、账户 M4Tester（离线）、已检测 9 个 Java、已加载 103 个正式版本 |

证据：`docs/evidence/ui-redesign/`（01-welcome.png、02-instances.png、03-downloads.png、04-accounts.png、05-servers.png、06-settings.png、ui-smoke.log）。

### 说明

- 本次仅改 `MainWindow.xaml`、`ShellViewModel`（状态汇总属性）和下载页初始提示文案，不涉及引擎/认证逻辑。
- 实例页 Java 列表仍以「检测 Java」按钮触发（设置页启动时会自动检测并显示数量）；为保持启动速度未在实例页初始化时重复探测。
- 下一里程碑回到功能：M5 加载器（Fabric/Quilt/Forge/NeoForge）。

### 数据目录统一到 .minecraft（2026-10-27 深夜，修复"数据还在默认目录"）

- 新的完整布局：**游戏文件**在 `<MinecraftDirectory>`（versions/libraries/assets），**启动器数据**（settings.json、accounts.json[DPAPI]、logs、instances 及其中的存档/options、cache、temp、java-runtime）在 **`<MinecraftDirectory>\AstraLauncher`**；便携模式下整个 `.minecraft` 目录自包含，可整体搬移，其他启动器仍可直接使用 versions/libraries/assets。
- 升级迁移：首次启动发现旧数据在 `%LOCALAPPDATA%\AstraLauncher`（或 `<默认Minecraft>\AstraLauncher` 之外的显式目录）时，自动把整个数据目录移动到 `<MinecraftDirectory>\AstraLauncher`；`instances.json`/`.bak` 中的实例绝对路径同步改写；迁移成功后删除旧目录；若目标已有 settings.json 则跳过，迁移失败保留旧目录并记录 warning。
- 启动流程调整：在创建 DI 宿主前完成「解析 Minecraft 目录 → 读取设置中的自定义目录 → 迁移旧数据 → 构造 LauncherPaths」，所以日志/账户/实例从第一次启动起就位于 `.minecraft\AstraLauncher`；设置页新增只读「启动器数据」路径展示，并说明修改 Minecraft 目录后启动器数据会在下次启动迁移到新目录下的 AstraLauncher。
- 附带修复：实例「导入目录」为空时给出中文提示而不是抛异常对话框。
- 验证：`dotnet build` 0/0；`dotnet test` **223/223**（新增 3 个 LauncherDataMigrator 用例：数据搬迁+实例路径改写、目标已存在跳过、源不存在无操作）。
- **真实环境迁移实测（F:\MC，用户实际目录）**：
  - 迁移前：`%LOCALAPPDATA%\AstraLauncher` 存在（17.3 MB：settings/accounts/logs/instances），`F:\MC\.minecraft\AstraLauncher` 不存在。
  - 用新 exe 启动一次后：旧目录被移除；`F:\MC\.minecraft\AstraLauncher` 生成（settings.json、accounts.json、instances.json、logs、instances 及两个实例目录，共 17.6 MB）；`instances.json` 中实例路径已改写为 `F:\MC\.minecraft\AstraLauncher\instances\...`。
  - 设置页显示：游戏目录 `F:\MC\.minecraft`、启动器数据 `F:\MC\.minecraft\AstraLauncher`。
  - 实例列表正常（2 个实例，路径在 .minecraft 内），启动 1.21.1 → 游戏窗口出现（30 秒）→ 正常退出码 0；迁移后日志 0 Critical / 0 Error（此前的 Critical 均为修复前只读绑定导致，已修复：只读 TextBox 绑定加 `Mode=OneWay`）。
  - 下载的版本文件确认在 `F:\MC\.minecraft\versions\1.21.1\1.21.1.jar+json`、`1.20.1`（以及 PCL 原有版本），没有写入 `%LOCALAPPDATA%`。
- 证据：`docs/evidence/datadir-migration/`（verify.log、settings-datadir.png、game-from-migrated-data.png、layout.txt）。

### M5 加载器集成（2026-10-29）

- **加载器目录**：新增 `ILoaderCatalog` / `LoaderCatalog`，支持 Fabric（Fabric Meta）、Quilt（Quilt Meta）、Forge（官方 promotions）、NeoForge（官方 Maven API，失败时回退 BMCLAPI `maven-metadata.xml`）；下载页新增「安装模组加载器（可选）」卡片：选择加载器 → 刷新版本（默认选推荐版）→ 安装/取消，进度与状态沿用下载页样式。
- **安装实现**：Fabric/Quilt 通过 `profile/json` 生成 `versions/<loader>-<mc>/<id>.json` 后交给安装管线补齐库/资源；Forge/NeoForge 下载官方安装器（优先官方，失败回退 BMCLAPI 镜像）并以 Java 21/17 运行 `--installClient`；运行前自动创建最小 `launcher_profiles.json`（安装器要求）。NeoForge 安装器在官方 Maven 不可达时使用镜像补丁后的安装器；安装器内置 `version.json` 可直接落盘。
- **本地版本检测**：启动时扫描 `<.minecraft>/versions/*/<id>.json`，把 PCL/其他启动器已安装的版本合并进「游戏实例」的版本下拉框；标题显示「… · N 个本地版本」。
- **元数据/下载修复**：继承合并按 classifier 保留 natives 变体（此前仅按 group:artifact 去重会把 lwjgl base jar 覆盖成 natives 变体，导致 Fabric/Quilt 启动 `NoClassDefFoundError`）；安装计划对本地加载器版本跳过 Mojang manifest 下载；解析器容忍空 `url` 与空 `logging`（旧 Forge/新版 NeoForge 配置）；Forge/NeoForge Maven 主机映射到 BMCLAPI 镜像并在回退模式下优先镜像；下载页切换版本/加载器时的异步竞态用请求序号丢弃过期结果。
- 验证：`dotnet build -c Release` **0 警告 / 0 错误**；`dotnet test` **266/266 通过**（新增 LoaderCatalog、FabricLike/ForgeLike 安装、ExternalProcessRunner、加载器继承 classifier 回归、Forge/NeoForge 镜像映射等用例）。
- **真实 1.21.1 E2E（本机）**：
  - Fabric Loader 0.20.1：下载页安装耗时 67 s（254 个 Fabric 版本列表），生成 `fabric-loader-0.20.1-1.21.1`；创建实例 M5-Fabric 启动 → 20 s 出现 `Minecraft* 1.21.1` 主菜单（左下角 `1.21.1/Fabric (Modded)`）→ 正常退出码 0。
  - Quilt Loader 0.20.0-beta.9：版本列表 307 个，安装 6 s；实例 M5-Quilt 启动 → 32 s 主菜单 → 退出码 0。
  - Forge 52.1.20：版本列表 1 个（promotions），安装器运行 3 s + 文件补齐；实例 M5-Forge 启动 → 10 s 主菜单 → 退出码 0。
  - NeoForge 21.1.265：版本列表 260 个（BMCLAPI 镜像 XML），安装器/版本清单/库文件补齐成功；实例 M5-NeoForge 启动仍失败在 NeoForge `BootstrapLauncher` 构建 service module layer（`NoSuchElementException`，module `neoforge` 解析问题）——**已知限制**，安装链路已验证，启动参数待后续修复。
- 证据：`docs/evidence/m5/`（m5-downloads-loader.png、m5-loader-installed.png、m5-fabric-main-menu.png、loader-Quilt-game.png、loader-Forge-game.png、loader-page-NeoForge.png，e2e.log、fabric-launch.log、loader-Quilt.log、loader-Forge.log、loader-NeoForge.log、neo-launch2.log）。
- **F:\MC 真实环境最终验收（单文件 exe，M5 提交 1428edb，2026-10-29）**：首页显示「已加载 103 个正式版本 · 9 个本地版本」；下载页加载器卡片正常；实例列表 2 个；启动 1.21.1 → 10 s 主菜单 → 退出码 0；日志记录 `Added 9 local version(s) from F:\MC\.minecraft\versions`，0 Critical / 0 Error。证据：`docs/evidence/m5/final-home.png`、`final-downloads-loader.png`、`final-instances.png`、`final-game-main-menu.png`、`final-smoke.log`。
- 下一步：M6 服务器列表（保存/编辑服务器，实例启动自动拼接 `--server/--port`，servers.dat 相关只读解析），随后 M7 最终端到端验收与文档收尾。

### M6 服务器列表与一键加入（2026-10-29）

- **数据层**：新增 `SavedServer`（Id / 名称 / 地址 / 可选端口 / DisplayAddress）与 `IServerRepository`；`JsonServerRepository` 把列表保存到启动器数据目录的 `servers.json`（临时文件原子替换；损坏 JSON 只记 warning 并回退为空列表），新增 4 个仓储测试。
- **启动链路**：`GameLaunchOptions`/`GameLaunchRequest` 增加 `ServerAddress`/`ServerPort`；命令构建器在普通游戏参数之后追加 `--server <host> [--port <port>]`，并校验地址非空/无空白/长度与端口 1–65535；`GameLaunchService` 记录 "Joining server …" 日志。
- **服务器页**：由占位说明改为可用界面——服务器列表（名称 + host:port）、刷新/新增/删除/「启动并加入」、名称/地址/端口编辑与保存、状态提示；地址支持 `host`、`host:port`、粘贴 URL 和 `[IPv6]:port`，端口留空使用默认。
- **一键加入**：Shell 订阅服务器页 JoinRequested，使用「游戏实例」页当前选中实例启动，并自动切到实例页显示进度/游戏日志；未选择实例时给出中文提示。
- **修复**：服务器列表刷新改用合并而非 `Clear()`，避免 WPF `SelectedItem` TwoWay 绑定把刚选中的服务器清空（smoke 中曾导致「请先选择一个服务器」）。
- 验证：`dotnet build -c Release` **0 警告 / 0 错误**；`dotnet test` **276/276 通过**（新增 4 个 JSON 仓储用例、4 个命令构建器服务器参数用例）。
- **GUI + 真实启动验证（dev 构建）**：服务器页保存「本地测试服 / 127.0.0.1:25565」→ `servers.json` 落盘；在游戏实例页选中 M5-Fabric 后回到服务器页点「启动并加入」→ 应用日志记录 `Joining server 127.0.0.1:25565 directly when launching version fabric-loader-0.20.1-1.21.1.`，Fabric 1.21.1 游戏窗口/主菜单出现；截图与日志见 `docs/evidence/m6/`（m6-servers-empty.png、m6-servers-saved.png、m6-join-game.png、m6-smoke.log、m6-join.log、servers.json）。
- 说明：`servers.dat` 只读导入为可选项，本轮未实现（服务器页以独立 `servers.json` 管理）；下一步 M7：NeoForge 启动问题尽量修复、README/docs 收尾、重新发布单文件 exe 并在 F:\MC 做最终验收。

### M7 收尾与最终验收（2026-10-29）

- **M6 后续修复**：
  - 服务器/实例选择在页面切换后可能被 WPF `SelectedItem` TwoWay 绑定清空：加入「最后一次选中 ID + 列表首项 + 重新读取 servers.json」多级回退（`69de28b`）。
  - 一键加入把启动任务抛到后台线程，导致 `InstancesPageViewModel` 从非 UI 线程更新 ObservableCollection（Unobserved `NotSupportedException` / 启动卡在「准备启动」）：服务器 ViewModel 的异步续体不再 `ConfigureAwait(false)`，启动始终在 UI 线程开始（`5ec665c`）。
  - Shell 对服务器页依赖改为必需注入，避免 JoinRequested 未订阅（`69de28b`）。
- **最终单文件 exe**：`artifacts/AstraLauncher-win-x64-single/AstraLauncher.exe`，版本 `1.0.0+5ec665c`，65,171,141 字节，SHA256 `FEEFAFB00B750B9DF1ED3E4502BBBB137B1CE017620764E553C2D5966EBF49D6`；已部署到 `F:\MC\AstraLauncher.exe`。
- **F:\MC 最终验收（运行部署的 exe）**：
  - 首页：「已加载 103 个正式版本 · 9 个本地版本」。
  - 服务器页：保存「本地测试服 / 127.0.0.1:25565」，`servers.json` 落盘。
  - 游戏实例页选中 1.21.1 实例 → 服务器页点「启动并加入」→ 实例页显示 `[launcher] 加入服务器：127.0.0.1:25565`，应用日志记录 `Joining server 127.0.0.1:25565 directly when launching version 1.21.1.`；24 s 后出现 `Minecraft 1.21.1` 窗口，正常关闭后退出码 0。
  - 本次运行日志无新增 Critical / Error（日志中唯一 Critical 是 01:01 修复前的 Unobserved task exception 历史记录）。
- **NeoForge 启动（已知限制，尽力修复但未完成）**：分析了其 `BootstrapLauncher`/`DisplayWindow.updateModuleReads` 对 service module layer 中 `neoforge` 模块的要求，尝试了 universal jar 加入 `-p`、`-DlegacyClassPath`、模块去重等方案；可复现的失败依次为 `NoSuchElementException` 与 `ResolutionException: reads more than one module named neoforge`，未找到与官方启动器一致的参数组合，故不并入默认启动路径；安装链路（目录/安装器/版本清单/库文件补齐）已验证，README 中保留已知限制说明。
- 验证：`dotnet build -c Release` **0 警告 / 0 错误**；`dotnet test` **276/276 通过**。
- 证据：`docs/evidence/m7/`（final2.log、final2-home.png、final2-servers.png、final2-game.png、app-join.log、servers.json、final2.ps1）。
- 结论：M1–M6 功能完成；真实 1.21.1 下载/离线启动到主菜单、Fabric/Quilt/Forge 安装与启动、服务器列表与 `--server/--port` 一键加入均通过端到端验收；NeoForge 启动作为已知限制记录。

### M8 MOD 管理与 M9 崩溃自动分析/修复（2026-10-29）

- **M8 MOD 管理**：
  - `LocalModService`：扫描实例 `mods` 目录，解析 Fabric/Quilt/Forge/NeoForge 元数据（含依赖），本地 jar 批量导入（SHA-256 去重）、启用/禁用（`.jar` ↔ `.jar.disabled`）、删除；4 个仓储/服务用例。
  - `ModrinthClient`：公开 API 搜索、按游戏版本+加载器筛选版本、下载 primary 文件并校验 SHA-1，`.part` 临时文件。
  - 新增「模组」页（实例选择、本地列表与导入/启停/删除/打开目录、Modrinth 搜索与安装、自动识别实例版本/加载器）；导航、DI、Shell 初始化接入。
  - 真实验证：本地测试 MOD 识别与禁用；Modrinth `fabric api` 搜索 48 条结果；安装小体积 MOD `thegodgenutility-0.1.jar`（66,230 字节）到 M5-Fabric 实例成功。
- **M9 崩溃自动分析/修复**：
  - `CrashAnalyzer` 读取 `logs/latest.log` 与最新 `crash-reports/*.txt`，识别缺失前置依赖（Forge/NeoForge `MISSING`、Fabric missing 行）、重复 MOD、`Suspected Mods`、Java 版本、OOM、显卡/OpenGL、加载器/Mixin 等；过滤堆栈帧避免把类名误判为 MOD。
  - `CrashRepairService` 仅执行可逆修复：从 Modrinth 安装缺失前置、禁用可疑/重复 MOD、内存 +1 GiB；`CrashAnalysisDialogService` 展示 PCL 风格详情/建议与「自动修复 / 手动处理 / 查看日志」，修复前先询问。
  - 游戏退出后（含 exit code 0 但存在崩溃标记的情况）自动分析；实例页记录分析/选择/修复到游戏日志。
  - 真实验证：导入 `changed&机械动力`（Forge 1.20.1）副本并启动，游戏崩溃后自动弹出崩溃分析窗口，识别「缺少前置 MOD create（由 create_dragons_plus / create_hypertube 要求）」；选择手动处理后保留现场，未修改任何 MOD。
- 验证：`dotnet build -c Release` **0 警告 / 0 错误**；`dotnet test` **295/295 通过**。
- 证据：`docs/evidence/m8/`（m8-mods-list.png、m8-mods-toggled.png、m8-modrinth-results.png、m8-install-result.png、m8-smoke.log、m8-install.log）与 `docs/evidence/m9/`（m9-crash2-dialog.png、m9-crash2-after.png、m9-crash2.log）。
