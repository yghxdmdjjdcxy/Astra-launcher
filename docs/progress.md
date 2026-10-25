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
| M2 | 启动链路修复接线 + 启动 UI + 真实离线启动 1.21.1 到主菜单（E2E） | ⏳ 下一步 |
| M3 | 设置页 + Java 运行时管理/选择、实例设置 | 待开始 |
| M4 | 官方 Microsoft OAuth + Xbox/Minecraft 链 + DPAPI 令牌 + 账户页 | 待开始 |
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
