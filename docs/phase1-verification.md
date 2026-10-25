# Astra Launcher — Phase 1 基线、发布包与 GUI 验证记录

- 执行时间：2026-10-25（Asia/Shanghai）
- 执行环境：Windows 10 专业工作站版 10.0.19045 x64；.NET SDK 10.0.100；PowerShell 7.4.7 / Windows PowerShell 5.1；屏幕 2560×1600，DPI 120（125%）
- 仓库：`E:\Astra Launcher of Deepseek\Astra Launcher\Astra Launcher`
- 结论：**Git 基线已建立；Release 发布包已按当前源码重新生成；GUI 主窗口、5 页导航、实例管理全流程实机验证通过（24/24 检查项 PASS）；共记录 14 项实际界面问题（1 项 High、6 项 Medium、7 项 Low）。**
- 声明：本阶段**未修改任何应用源码**（30 个 .cs、2 个 .xaml、8 个 .csproj 内容与审计基线逐字节一致）；新增内容仅为 Git 基线、本记录与 `docs/evidence/` 证据文件；测试产生的实例数据已全部清理。

---

## 1. Git 基线

| 项 | 结果 |
|---|---|
| 仓库状态 | 已在仓库根目录初始化 Git（原工程此前的 bin/obj/artifacts 旧产物不纳入版本控制） |
| 基线提交 | `1f0ec9168bd8159679700659ac5ce399ac9968fb`（`chore: establish source baseline (pre-existing source preserved; generated outputs ignored)`） |
| 纳入文件 | 50 个：30 个 `.cs`、2 个 `.xaml`、8 个 `.csproj`、6 个 README、`.sln`、`Directory.Build.props`、`NuGet.Config`、`AstraLauncher.App/appsettings.json`、`.gitignore` |
| 忽略规则 | `bin/`、`obj/`、`artifacts/`、`TestResults/`、`*.trx`、`coverage*.xml`、`*.coverage`、`.vs/`、`*.user`、`*.suo`（见 `.gitignore`） |
| 工作区 | `git status --porcelain` 为空（clean） |
| 源码保护 | 审计基线中 50 个源码/配置文件 SHA256 复核 **全部未改变**（0 变更） |

> 说明：最初由后台任务启动 `git init` 与初始提交，其在提交完成后即被中断；本阶段已核对该提交仅包含原有 49 个源码/配置文件 + 新增 `.gitignore`，无任何源码内容变化后予以保留，并继续完成后续构建、发布、验证与记录。

---

## 2. Release 发布包（与当前源码一致）

### 2.1 旧包处置

上一轮工作区中已有的 `artifacts/AstraLauncher-win-x64/`（未纳入 Git）是**陈旧快照**（其内嵌 `AstraLauncher.App.dll` 只有 15 个类型，缺少当前源码中的 `AsyncRelayCommand`/异步状态机等 21 个类型；exe mtime 2026-10-25 14:23:59）。已整体归档到仓库外：

- 归档路径：`C:\Users\admin\AppData\Local\Temp\astra-audit\stale-artifacts-2026-10-25-1423\`
- 旧 exe：131,546,874 B，SHA256 `CCE699D7D481F1CBD192357934B5065E382FB89C860E12679E0CC58A56AE16D3`

### 2.2 新包生成

执行命令（在仓库根目录）：

```powershell
dotnet publish .\AstraLauncher.App\AstraLauncher.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o .\artifacts\AstraLauncher-win-x64
```

结果：**exit 0，24.7 s**，输出目录 `artifacts/AstraLauncher-win-x64/`（`artifacts/` 已被 `.gitignore` 忽略，不进入 Git）。

| 文件 | 大小（B） | SHA256 |
|---|---:|---|
| AstraLauncher.App.exe | 131,563,770 | `9461C208581817A1B2E6FDB90EFA219B6B5DC117016FF6A01D446C07C056660A` |
| appsettings.json | 108 | `09F49A4A1DAC0AA9DB518CB0DA7D5A079121D33EB8B717C3F3D05361DB2F0828` |
| D3DCompiler_47_cor3.dll | 4,916,840 | `A05D04A270F68C8C6D6EA2D23BEBF8CD1D5453B26B5442FA54965F90F1C62082` |
| PenImc_cor3.dll | 157,968 | `2A32EAB48B9F44C3FF2EA8018C60779D60B98E36D86D4386945596549F962B07` |
| PresentationNative_cor3.dll | 1,235,720 | `2AB47A53C7BB6C7EC37D7A2392BAE99EFE99E8BCF506948F1628F1BB577295ED` |
| vcruntime140_cor3.dll | 124,544 | `D5E4D9A3E835FA679450145D6A7D94E36573A509317111904D9B3712C30D9066` |
| wpfgfx_cor3.dll | 1,951,536 | `C1C3EDA8E20903D94A6D118813695637D360973A902B181ED086F971A1269AC8` |

合计 139,950,486 B（约 133.47 MiB），7 个文件，无 PDB，`appsettings.json` 与源码逐字节一致，原生 WPF DLL 与上一包哈希一致（来自同一 runtime pack 10.0.0）。

**与源码一致性证据**：

- 新 exe 的 `ProductVersion = 1.0.0+1f0ec9168bd8159679700659ac5ce399ac9968fb`，包含基线提交哈希；
- 该 exe 由当前源码 publish 得到，并在第 4 节 GUI 验证中实际运行（当前源码新增的异步命令、实例选择/重命名/复制/删除行为均生效）；
- 仓库当前无任何未提交的源码改动（`git status` clean）。

---

## 3. 构建与测试

构建日志原件见 `docs/evidence/build-logs/`。

| # | 命令 | 退出码 | 耗时 | 结果 |
|---|---|---|---|---|
| 1 | `dotnet restore AstraLauncher.sln` | 0 | 13.7 s | 8/8 项目还原/最新，无 NU1xxx |
| 2 | `dotnet build AstraLauncher.sln -c Release --no-restore` | 0 | 46.8 s | **0 个警告 / 0 个错误**（50 个源文件构建通过） |
| 3 | `dotnet test AstraLauncher.Tests\AstraLauncher.Tests.csproj -c Release --no-build` | 0 | 9.6 s（测试执行 715 ms） | **15 通过 / 0 失败 / 0 跳过（15/15）** |
| 4 | `dotnet publish`（见 §2.2） | 0 | 24.7 s | Release 发布包生成成功 |

测试分布与上一阶段一致：DownloadQueue 5、GameInstanceRepository 2、VersionMetadataResolver 2、JavaVersionParser 4、LauncherPaths 1、NavigationService 1。

---

## 4. Windows GUI 实机验证

### 4.1 方法与环境

- 运行对象：§2.2 生成的 `artifacts/AstraLauncher-win-x64/AstraLauncher.App.exe`（self-contained single-file，工作目录=包目录）。
- 交互方式：使用 **UI Automation（Windows PowerShell 5.1 + `UIAutomationClient`）** 查找/点击按钮、读写文本框、选择列表项、读取文本与列表内容；每步截图；文件系统与 `%LOCALAPPDATA%\AstraLauncher\instances.json` 双重核对。
- 环境：Windows 10 19045 x64；屏幕 2560×1600，DPI 120（125%）；窗口外框 1200×700 物理像素（XAML 960×560 DIP 在 125% 缩放后的等价值，验证通过）；启动到主窗口就绪约 **1.5 s**（1545 ms）。
- 测试前用户数据：仅有日志文件（无 `instances.json`、无 `instances/` 目录）；测试后已恢复为同一状态。

### 4.2 测试矩阵（24/24 PASS）

| ID | 检查项 | 预期 | 实测 | 结论 |
|---|---|---|---|---|
| T01 | 启动应用并显示主窗口 | 标题正确、可见、进程响应、仅 1 个顶层窗口 | `title='Astra Launcher'`、offscreen=False、Responding=True、windows=1、rect=1200×700、startupMs=1545 | PASS |
| T02 | 首页初始内容 | 显示欢迎标题与副标题 | “欢迎使用 Astra Launcher” + “基础架构已就绪……” | PASS |
| T03 | 导航-下载 | 显示“Stage 3：真实下载队列。” | 文案一致 | PASS |
| T04 | 导航-账户 | 显示“Stage 4：官方 Microsoft 登录与离线档案。” | 文案一致 | PASS |
| T05 | 导航-服务器 | 显示“Stage 7：服务器与联机基础设施。” | 文案一致 | PASS |
| T06 | 游戏实例页控件结构 | 标题+说明、3 个文本框、7 个页面按钮、2 个列表、状态“已加载 0 个实例。” | 全部一致（总按钮 12 = 5 导航 + 7 页面） | PASS |
| T07 | 未选择实例时重命名 | 状态栏提示“请先选择实例。” | 与预期一致，无弹窗 | PASS |
| T08 | 空名称创建校验 | 给出可读的必填提示 | 实际显示 `Instance name is required. (Parameter 'name')`（英文异常文本，见 U01） | PASS（记录问题） |
| T09 | 创建实例 | 状态提示、列表 1 项、清单 1 条、受管目录创建、创建后自动选中 | 全部符合；目录 `%LOCALAPPDATA%\AstraLauncher\instances\2904…6970` 存在；selected=GUI验证-实例A | PASS |
| T10 | 刷新实例列表 | 状态“已加载 1 个实例。”、列表 1 项 | 符合；但刷新前选中项在刷新后丢失（见 U02） | PASS（记录问题） |
| T11a | 选中丢失后直接重命名 | 系统提示“请先选择实例。” | 与预期一致 | PASS |
| T11 | 重命名实例 | 状态提示、列表与清单名称更新 | 状态“实例已重命名。”、列表与清单均为“GUI验证-重命名A”；重命名后选中项丢失（见 U03） | PASS（记录问题） |
| T12a | 选中丢失后直接复制 | 系统提示“请先选择实例。” | 与预期一致 | PASS |
| T12 | 复制实例 | 列表 2 项、清单 2 条、两个受管目录存在 | 符合；列表出现两个同名项（见 U04） | PASS |
| T12b | 两个实例重命名为同名 | 列表出现两个完全同名项 | `同名实例 / 同名实例`（清单为两个不同 Id/目录） | PASS（验证不可区分性） |
| T13 | 导入外部目录 | 列表 3 项、清单 `IsManagedDirectory=false`、外部目录不变 | 符合；导入目录为临时夹具目录 | PASS |
| T14 | 删除导入实例 | 仅删登记、清单减少、外部目录保留、无弹窗 | 状态“实例登记已删除；导入目录未被修改。”；外部目录仍存在；窗口数 1→1 | PASS |
| T15 | 检测 Java 运行时 | 触发探测并显示结果、右侧列表列出结果 | 状态“检测到 9 个 Java 运行时。”；列表 9 项；UIA 点击返回 71 ms；检测总耗时 2510 ms | PASS |
| T16 | 删除受管实例 | 状态提示、受管目录删除、清单减少 | 状态“实例及其受管理目录已删除。”；目录已删除；列表剩 1 项；**无确认弹窗**（见 U08） | PASS（记录问题） |
| T17 | 关闭主窗口 | 进程 12 s 内退出且退出码 0 | exited=True、exitCode=0 | PASS |
| T18 | 重启后实例持久化 | 重启后加载 1 个实例 | 状态“已加载 1 个实例。”、列表 1 项“同名实例” | PASS |
| T19 | 清理剩余测试实例 | 列表清空 | 删除成功，remaining 为空 | PASS |
| T20 | 清理测试数据 | 用户数据恢复为测试前（仅日志） | 仅 `logs/astra-2026-10-25.log` 保留 | PASS |
| T21 | 运行期日志健康 | 无 Critical/Error 日志 | critical=0、error=0 | PASS |

原始逐条记录：`docs/evidence/gui-test-v4.log`；结构化结果与 14 项界面问题：`docs/evidence/gui-test-results-v4.json`；交互脚本：`docs/evidence/gui-verify-v4.ps1`。

### 4.3 已确认可用的行为

- 主窗口正常创建、响应、可正常关闭（exit code 0），单实例运行期间仅 1 个顶层窗口。
- 5 个导航页全部可切换，页面内容与 XAML 定义一致（下载/账户/服务器为静态占位页，属当前阶段预期）。
- 实例管理全链路可用：创建（自动选中、写清单、建受管目录）→ 刷新 → 重命名 → 复制 → 导入外部目录（只登记）→ 删除导入实例（保留外部目录）→ 删除受管实例（递归删除目录）→ 重启后从 `instances.json` 恢复。
- Java 探测可用：本机识别 9 个 Java 运行时（Oracle/JDK/Adoptium 等路径），右侧列表展示结果，状态栏汇总数量。
- 日志在工作目录=包目录时按 `appsettings.json` 过滤，运行期无 Critical/Error。

---

## 5. 实际观察到的界面问题（14 项）

| ID | 级别 | 问题 | 实测证据 | 截图 |
|---|---|---|---|---|
| U08 | **High** | 删除受管实例无二次确认，单击即递归删除实例目录 | 点击“删除”后 77 ms 内目录被删除；期间进程顶层窗口数 1→1（无确认对话框） | `10-before-delete.png` |
| U01 | Medium | 空名称校验直接显示 .NET 英文异常文本 | 状态栏：`Instance name is required. (Parameter 'name')`（其余界面为中文） | 无（状态栏文本，见日志 T08） |
| U02 | Medium | 刷新后列表选中项被清空 | 刷新前 selected=GUI验证-实例A；刷新后 selected 为空 | 无（见日志 T10） |
| U03 | Medium | 重命名成功后列表选中项被清空（刷新/复制/删除同类操作同样需要重新选择） | 重命名后 selectedAfter 为空；随后直接点“复制/重命名”提示“请先选择实例。” | 无（见日志 T11/T12a） |
| U05 | Medium | 检测 Java 无进度指示、不可取消（本次未观察到明显 UI 冻结） | UIA 点击 71 ms 返回（同步枚举耗时短），检测总耗时 2510 ms；期间无进度条/取消按钮 | `09-java-detected.png` |
| U06 | Medium | 实例表单输入框无可见标签/占位提示 | 3 个 Edit 的 UIA Name/HelpText 均为空，仅“导入路径”有 ToolTip | `05-instances-empty.png` |
| U14 | Medium | 所有操作反馈仅靠底部状态栏，无进度指示/对话框 | 错误、成功、删除等均只改状态栏；无弹窗/Toast/进度条 | 见日志 T08/T10/T15/T16 |
| U04 | Low | 实例列表仅显示名称，同名实例无法区分 | 列表同时出现两个“同名实例”（清单内为两个不同 Id/目录） | `07-same-names.png` |
| U07 | Low | Java 运行时列表只显示 `java.exe` 完整路径，不显示版本/厂商 | 9 项均为原始路径；路径过长需横向滚动 | `09-java-detected.png` |
| U09 | Low | 实例页无版本选择器，版本号为自由文本（默认 1.21.1） | 版本可任意输入，创建时不校验 `versions/<id>/<id>.json` 是否存在 | `05-instances-empty.png` |
| U10 | Low | 导入路径仅文本框 + ToolTip，无“浏览”按钮 | UIA 第 3 个 Edit 无按钮相邻（仅“导入目录”按钮） | `05-instances-empty.png` |
| U11 | Low | 两个 ListBox 无标题/列头，右侧 Java 列表只有路径 | 左列表=实例名、右列表=java.exe 路径，均无标签 | `05-instances-empty.png` / `09-java-detected.png` |
| U12 | Low | 侧边栏无当前页面选中态 | 5 个导航按钮外观一致，无法看出当前页 | 全部截图 |
| U13 | Low | 窗口使用默认 .NET 图标与默认标题栏样式 | 标题栏左上角为默认图标 | `01-welcome.png` |

补充说明（非缺陷，属当前阶段实现状态）：下载/账户/服务器三页为静态占位文案（“Stage 3/4/7”），与本阶段审计结论一致；欢迎页为静态文案。

---

## 6. 清理与最终状态

- 测试创建的 3 个实例、`instances.json`、导入夹具目录已全部删除；`%LOCALAPPDATA%\AstraLauncher` 下仅保留日志文件。
- 无残留 `AstraLauncher.App` 进程；应用以退出码 0 正常退出。
- 仓库：`git status` clean；`artifacts/` 为新生成发布包（Git 忽略）；`bin/obj` 为构建产物（Git 忽略）。
- 应用源码与配置零改动（50 个审计文件 SHA256 复核一致）。

---

## 7. 限制与说明

1. GUI 验证为单机、单次会话的自动化验收（UI Automation + 截图），未做多分辨率/多 DPI/多语言/多用户的交叉验证；截图为 125% DPI 下的 1184×692 可见窗口区域。
2. 未验证真实 Minecraft 启动/下载/登录（对应功能在 Phase 0 审计中确认尚未接线或未实现），本次仅验证现有 GUI 与实例管理、Java 探测。
3. 本次未做崩溃注入、弱网、并发双开等破坏性测试；这些属于审计报告中已列出的风险项，建议在后续阶段以单元/集成测试覆盖。
4. `docs/evidence/gui-test-v4.log` 中 U05 行标题存在脚本字符串拼接截断（原始记录保留），其正确内容以 `gui-test-results-v4.json` 与上表为准。
5. 发布包为 self-contained single-file x64（无 PDB，`DebugType=None`），位于 `artifacts/AstraLauncher-win-x64/`，不纳入 Git；发布命令见 §2.2。

---

## 8. 复现方式

```powershell
# 1) 构建与测试
dotnet restore AstraLauncher.sln
dotnet build AstraLauncher.sln -c Release --no-restore
dotnet test .\AstraLauncher.Tests\AstraLauncher.Tests.csproj -c Release --no-build

# 2) 生成发布包
dotnet publish .\AstraLauncher.App\AstraLauncher.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:DebugType=None `
  -o .\artifacts\AstraLauncher-win-x64

# 3) 运行（工作目录=包目录，确保 appsettings.json 生效）
Start-Process .\artifacts\AstraLauncher-win-x64\AstraLauncher.App.exe `
  -WorkingDirectory .\artifacts\AstraLauncher-win-x64
```

GUI 自动化验收脚本（参考）：`docs/evidence/gui-verify-v4.ps1`（脚本内含本次机器的绝对路径与临时目录，复用时需按环境调整）。
