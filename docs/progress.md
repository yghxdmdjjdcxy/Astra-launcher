# Astra Launcher — 开发进度与里程碑记录

> 目标（已确认范围）：完成下载/版本/实例/Java/启动/设置链路 → 官方 Microsoft 登录 → 服务器列表；原版 + Fabric/Quilt/Forge/NeoForge；官方源为主、BMCLAPI 镜像可切换/自动回退；保持手写 MVVM；不含安装器与自动更新；最终真实 1.21.1 下载 + 离线启动验收。
> 节奏：连续推进，每个里程碑自测通过后记录并继续；不破坏既有测试，持续补测试。

## 里程碑总览

| 里程碑 | 内容 | 状态 |
|---|---|---|
| Phase 0 | 工程审计（只读） | ✅ 完成（见 `docs/phase1-verification.md`） |
| Phase 1 | Git 基线 + Release 发布包 + GUI 验证 | ✅ 完成（见 `docs/phase1-verification.md`） |
| **M1a** | 下载引擎加固 + 基础服务（路径/设置/下载源/日志轮转/单实例）+ 实例页 UI 安全修复 | ✅ 完成（见下） |
| M1b | 版本清单/元数据/客户端/库/资源下载 + 下载页 UI + 实例按版本创建 | ⏳ 下一步 |
| M2 | Java 运行时管理与选择、实例设置、设置页 | 待开始 |
| M3 | 启动链路修复接线 + 启动 UI + 离线启动 1.21.1 到主菜单 | 待开始 |
| M4 | 官方 Microsoft OAuth + Xbox/Minecraft 链 + DPAPI 令牌 + 账户页 | 待开始 |
| M5 | Fabric/Quilt/Forge/NeoForge 安装与启动集成 | 待开始 |
| M6 | 服务器列表 + --server/--port 一键加入 | 待开始 |
| M7 | 全量测试、真实端到端验收、文档与最终报告 | 待开始 |

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
