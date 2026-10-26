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
| M4 | 官方 Microsoft 登录：OAuth + Xbox/Minecraft 链 + DPAPI 令牌 + 账户页/多账户/离线档案 | ⏳ 下一步 |
| M5 | Fabric/Quilt/Forge/NeoForge 安装与启动集成 | 待开始 |
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
