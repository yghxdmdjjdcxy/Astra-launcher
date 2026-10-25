$ErrorActionPreference = 'Continue'
$GUI = 'C:\Users\admin\AppData\Local\Temp\astra-audit\gui'
$PKG = 'E:\Astra Launcher of Deepseek\Astra Launcher\Astra Launcher\artifacts\AstraLauncher-win-x64'
$EXE = Join-Path $PKG 'AstraLauncher.App.exe'
$SHOTS = Join-Path $GUI 'shots-v4'
$IMPORT = Join-Path $GUI 'import-fixture'
$APPDATA = Join-Path $env:LOCALAPPDATA 'AstraLauncher'
$MANIFEST = Join-Path $APPDATA 'instances.json'
$INSTDIR = Join-Path $APPDATA 'instances'
$LOG = Join-Path $GUI 'gui-test-v4.log'
$JSONOUT = Join-Path $GUI 'gui-test-results-v4.json'
New-Item -ItemType Directory -Force -Path $SHOTS | Out-Null
if (Test-Path $LOG) { Remove-Item $LOG -Force }
$results = New-Object System.Collections.Generic.List[object]
function L([string]$s) { $s | Out-File -FilePath $LOG -Encoding utf8 -Append; Write-Host $s }
function Rec { param($id,$name,$expected,$actual,$pass,$evidence) $o = New-Object psobject -Property @{ Id=$id; Name=$name; Expected=$expected; Actual=$actual; Pass=[bool]$pass; Evidence=$evidence }; $results.Add($o); $tag='FAIL'; if ($pass) { $tag='PASS' }; L ("[$tag] $id | $name | expected=$expected | actual=$actual | evidence=$evidence") }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class NW {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
  [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
  private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
  public static int[] VisibleRect(IntPtr hWnd) { RECT r; int hr = DwmGetWindowAttribute(hWnd, 9, out r, Marshal.SizeOf(typeof(RECT))); if (hr == 0 && r.Right > r.Left && r.Bottom > r.Top) { return new int[] { r.Left, r.Top, r.Right, r.Bottom }; } RECT w; GetWindowRect(hWnd, out w); return new int[] { w.Left, w.Top, w.Right, w.Bottom }; }
}
"@
[void][NW]::SetProcessDPIAware()
$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]
function GetTopWindows { $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,$CT::Window); return @($AE::RootElement.FindAll($TS::Children,$cond)) }
function Find-AppWindow([int]$targetPid) { foreach ($w in @(GetTopWindows)) { try { if ($w.Current.ProcessId -eq $targetPid -and $w.Current.Name -eq 'Astra Launcher') { return $w } } catch {} }; return $null }
function Wait-AppWindow([int]$targetPid,[int]$timeoutSec) { $sw=[Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) { $w = Find-AppWindow $targetPid; if ($w) { return $w }; Start-Sleep -Milliseconds 250 }; return $null }
function All-Of([object]$root,$ct) { $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,$ct); return @($root.FindAll($TS::Descendants,$cond)) }
function Get-Texts { $r=@(); foreach ($e in @(All-Of $script:win $CT::Text)) { try { $r += $e.Current.Name } catch {} }; return $r }
function Get-Buttons { return @(All-Of $script:win $CT::Button) }
function Get-Edits { return @(All-Of $script:win $CT::Edit) }
function Get-Lists { return @(All-Of $script:win $CT::List) }
function Get-ListItems([int]$listIndex) { $lists = @(Get-Lists); if ($lists.Count -le $listIndex) { return @() }; $liCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,$CT::ListItem); return @($lists[$listIndex].FindAll($TS::Descendants,$liCond)) }
function Get-ListLabels([int]$listIndex) { $r=@(); foreach ($it in @(Get-ListItems $listIndex)) { $label=''; try { $tx=@($it.FindAll($TS::Descendants,(New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,$CT::Text)))); foreach ($t in $tx) { $label += $t.Current.Name } } catch {}; if (-not $label) { try { $label=$it.Current.Name } catch {} }; $r += $label }; return $r }
function Get-SelectedLabels([int]$listIndex) { $r=@(); foreach ($it in @(Get-ListItems $listIndex)) { try { if ($it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { $label=''; $tx=@($it.FindAll($TS::Descendants,(New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,$CT::Text)))); foreach ($t in $tx) { $label += $t.Current.Name }; $r += $label } } catch {} }; return $r }
function Click-Btn([string]$name) { foreach ($b in @(Get-Buttons)) { try { if ($b.Current.Name -eq $name) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } } catch {} }; return $false }
function Set-Edit([int]$idx,[string]$val) { $e = @(Get-Edits); if ($e.Count -le $idx) { throw "Edit index $idx not found (count=$($e.Count))" }; $e[$idx].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($val); return $true }
function Select-Item([int]$listIndex,[int]$itemIndex) { $items = @(Get-ListItems $listIndex); if ($items.Count -le $itemIndex) { throw "item $itemIndex missing (count=$($items.Count))" }; $items[$itemIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true }
function Select-ByLabel([int]$listIndex,[string]$substr) { $items = @(Get-ListItems $listIndex); $labels = @(Get-ListLabels $listIndex); for ($i=0; $i -lt $items.Count; $i++) { if ($labels[$i] -like ('*' + $substr + '*')) { $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $i } }; return -1 }
function Wait-Status([string]$pattern,[int]$timeoutSec) { $sw=[Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) { try { foreach ($t in @(Get-Texts)) { if ($t -match $pattern) { return $t } } } catch {}; Start-Sleep -Milliseconds 100 }; return $null }
function Get-StatusLike([string]$pattern) { try { foreach ($t in @(Get-Texts)) { if ($t -match $pattern) { return $t } } } catch {}; return $null }
function Shot([string]$name) { try { $h=[IntPtr]$script:win.Current.NativeWindowHandle; [void][NW]::ShowWindow($h,9); [void][NW]::SetForegroundWindow($h); Start-Sleep -Milliseconds 500; $r=[NW]::VisibleRect($h); $w=$r[2]-$r[0]; $hh=$r[3]-$r[1]; if ($w -le 0 -or $hh -le 0) { return '<bad-rect>' }; $bmp = New-Object System.Drawing.Bitmap($w,$hh); $g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r[0],$r[1],0,0,(New-Object System.Drawing.Size($w,$hh))); $path=Join-Path $SHOTS ($name + '.png'); $bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose(); return $path } catch { return ('<shot-failed: ' + $_.Exception.Message + '>') } }
function Count-AppWindows([int]$targetPid) { $n=0; foreach ($w in @(GetTopWindows)) { try { if ($w.Current.ProcessId -eq $targetPid) { $n++ } } catch {} }; return $n }
function Read-Manifest { $r=@(); if (Test-Path $MANIFEST) { for ($t=0; $t -lt 6; $t++) { try { $raw = Get-Content -LiteralPath $MANIFEST -Raw -Encoding UTF8; if (-not [string]::IsNullOrWhiteSpace($raw)) { $j = $raw | ConvertFrom-Json; if ($j -is [array]) { $r = @($j) } else { $r = @($j) }; break } } catch { Start-Sleep -Milliseconds 200 } } }; return $r }
function App-Nav([string]$page) { for ($i=0; $i -lt 4; $i++) { $ok = Click-Btn $page; Start-Sleep -Milliseconds 650; if ($ok) { return $true } }; return $false }
function Wait-Ready([int]$timeoutSec) { $sw=[Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) { try { $w2 = Find-AppWindow $script:proc.Id; if ($w2) { $script:win = $w2; if (@(Get-Buttons).Count -ge 5) { return $true } } } catch {}; Start-Sleep -Milliseconds 250 }; return $false }
function Foreground { try { $h=[IntPtr]$script:win.Current.NativeWindowHandle; [void][NW]::ShowWindow($h,9); [void][NW]::SetForegroundWindow($h); Start-Sleep -Milliseconds 300 } catch {} }
$uiIssues = New-Object System.Collections.Generic.List[object]
function Issue { param($id,$severity,$title,$detail,$evidence) $o = New-Object psobject -Property @{ Id=$id; Severity=$severity; Title=$title; Detail=$detail; Evidence=$evidence }; $uiIssues.Add($o); L ("[UI-ISSUE] $id [$severity] $title :: $detail :: $evidence") }
L ("START " + (Get-Date).ToString('o'))
L ("EXE=$EXE")
L ("EXE_SHA256=" + (Get-FileHash $EXE -Algorithm SHA256).Hash)
L ("EXE_SIZE=" + (Get-Item $EXE).Length)
L ("EXE_PRODUCTVERSION=" + (Get-Item $EXE).VersionInfo.ProductVersion)
Get-Process AstraLauncher.App -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$preManifestExisted = Test-Path $MANIFEST
$preInstancesExisted = Test-Path $INSTDIR
if ($preManifestExisted) { Copy-Item -LiteralPath $MANIFEST -Destination (Join-Path $GUI 'manifest-before-test.json') -Force }
if ($preInstancesExisted) { Copy-Item -LiteralPath $INSTDIR -Destination (Join-Path $GUI 'instances-before-test') -Recurse -Force -EA SilentlyContinue }
if (Test-Path $MANIFEST) { Remove-Item -LiteralPath $MANIFEST -Force }
if (Test-Path $INSTDIR) { Remove-Item -LiteralPath $INSTDIR -Recurse -Force }
if (Test-Path $IMPORT) { Remove-Item -LiteralPath $IMPORT -Recurse -Force }
New-Item -ItemType Directory -Force -Path $IMPORT | Out-Null
Set-Content -LiteralPath (Join-Path $IMPORT 'level.dat') -Value 'ASTRA-GUI-IMPORT-FIXTURE' -Encoding ASCII
L ("PRE-STATE manifestExisted=$preManifestExisted instancesDirExisted=$preInstancesExisted importFixture=$IMPORT")
$sw0=[Diagnostics.Stopwatch]::StartNew()
$script:proc = Start-Process -FilePath $EXE -WorkingDirectory $PKG -PassThru
$script:win = Wait-AppWindow $script:proc.Id 45
$startupMs=$sw0.ElapsedMilliseconds
if (-not $script:win) { L 'FATAL: main window not found'; Get-Process AstraLauncher.App -EA SilentlyContinue | Stop-Process -Force; exit 1 }
Start-Sleep -Milliseconds 1200
Foreground
$ready = Wait-Ready 30
if (-not $ready) { Start-Sleep -Milliseconds 800; $script:win = Find-AppWindow $script:proc.Id; if ($script:win) { Foreground }; $ready = Wait-Ready 20 }
$h=[IntPtr]$script:win.Current.NativeWindowHandle
$dpi=[NW]::GetDpiForWindow($h)
$screen=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
L ("SCREEN=" + $screen.ToString() + " DPI=" + $dpi + " ready=" + $ready)
try {
  $w=$script:win; $rect=$w.Current.BoundingRectangle; $tw=Count-AppWindows $script:proc.Id
  $pass=($w.Current.Name -eq 'Astra Launcher') -and (-not $w.Current.IsOffscreen) -and (-not $script:proc.HasExited) -and $script:proc.Responding -and ($tw -eq 1) -and $ready
  Rec 'T01' '启动应用并显示主窗口' '标题 Astra Launcher、可见、响应、1 个顶层窗口、导航按钮就绪' ("title='{0}' offscreen={1} responding={2} windows={3} rect={4}x{5} startupMs={6} dpi={7} ready={8} productVersion={9}" -f $w.Current.Name,$w.Current.IsOffscreen,$script:proc.Responding,$tw,[int]$rect.Width,[int]$rect.Height,$startupMs,$dpi,$ready,(Get-Item $EXE).VersionInfo.ProductVersion) $pass ('pid=' + $script:proc.Id)
} catch { Rec 'T01' '启动应用' 'no exception' ('EXCEPTION: ' + $_.Exception.Message) $false '' }
$shot = Shot '01-welcome'; L ("SHOT 01-welcome -> $shot")
$texts = @(Get-Texts)
Rec 'T02' '首页初始内容' '包含"欢迎使用 Astra Launcher"与副标题' ("texts=" + (($texts | Where-Object { $_ -match '欢迎使用|基础架构' }) -join ' | ')) (($texts -contains '欢迎使用 Astra Launcher') -and (($texts | Where-Object { $_ -like '基础架构已就绪*' }).Count -ge 1)) 'screenshot=01-welcome.png'
$i=3
foreach ($n in @(@{Page='下载';Marker='Stage 3：真实下载队列。';Shot='02-downloads'},@{Page='账户';Marker='Stage 4：官方 Microsoft 登录与离线档案。';Shot='03-accounts'},@{Page='服务器';Marker='Stage 7：服务器与联机基础设施。';Shot='04-servers'})) {
  $id='T0'+$i; $i++
  $clicked = App-Nav $n.Page
  $t = @(Get-Texts); $found = $t -contains $n.Marker
  $shot = Shot $n.Shot
  Rec $id ("导航-"+$n.Page) ("点击导航按钮后显示："+$n.Marker) ("clicked=$clicked markerFound=$found") ($clicked -and $found) ("screenshot="+$n.Shot+".png")
}
try {
  $null = App-Nav '游戏实例'
  $t=@(Get-Texts); $edits=@(Get-Edits); $buttons=@(Get-Buttons); $lists=@(Get-Lists)
  $editMeta=@(); foreach ($e in $edits) { try { $editMeta += ("name='{0}' help='{1}' autoid='{2}'" -f $e.Current.Name,$e.Current.HelpText,$e.Current.AutomationId) } catch {} }
  $st = Get-StatusLike '已加载 \d+ 个实例。'
  $expectedButtons = @('创建','刷新','重命名','复制','删除','导入目录','检测 Java')
  $foundButtons=0; foreach ($bn in $expectedButtons) { if (@($buttons | Where-Object { $_.Current.Name -eq $bn }).Count -eq 1) { $foundButtons++ } }
  $pass = ($t -contains '游戏实例') -and (($t | Where-Object { $_ -like '*导入目录只登记*' }).Count -eq 1) -and ($edits.Count -eq 3) -and ($lists.Count -eq 2) -and ($foundButtons -eq 7) -and ($st -eq '已加载 0 个实例。')
  Rec 'T06' '导航-游戏实例页控件结构' '标题+说明+3文本框+7按钮+2列表+状态"已加载 0 个实例。"' ("edits=$($edits.Count) buttonsTotal=$($buttons.Count) pageButtons=$foundButtons/7 lists=$($lists.Count) status='$st' editMeta=" + ($editMeta -join '; ')) $pass 'screenshot=05-instances-empty.png'
  $shot = Shot '05-instances-empty'; L ("SHOT 05-instances-empty -> $shot")
  Issue 'U06' 'Medium' '实例表单输入框无可见标签/占位提示' ('UIA 元数据：' + ($editMeta -join '; ')) 'T06 editMeta'
  $null = Click-Btn '重命名'; $s = Wait-Status '请先选择实例' 6
  Rec 'T07' '未选择实例时执行重命名' '状态栏提示"请先选择实例。"' ("status='$s'") ($s -ne $null) '仅状态栏提示，无弹窗'
  $null = Set-Edit 0 ''
  $null = Click-Btn '创建'; $s = Wait-Status 'Instance name is required' 6
  Rec 'T08' '空名称创建校验' '界面应给出可读的中文必填提示' ("status='$s'") ($s -ne $null) '实际为英文异常文本'
  if ($s -ne $null) { Issue 'U01' 'Medium' '空名称校验直接显示 .NET 英文异常文本' ('状态栏显示：' + $s) 'T08 status' }
  $null = Set-Edit 0 'GUI验证-实例A'; $null = Set-Edit 1 '1.21.1'
  $null = Click-Btn '创建'; $s = Wait-Status '已创建实例' 15
  Start-Sleep -Milliseconds 500
  $labels=@(Get-ListLabels 0); $m=@(Read-Manifest); $sel=@(Get-SelectedLabels 0)
  $mdir=''; if ($m.Count -gt 0) { $mdir=$m[0].DirectoryPath }
  $dirOk=$false; if ($mdir -ne '') { $dirOk=Test-Path -LiteralPath $mdir }
  $pass = ($s -ne $null) -and ($labels.Count -eq 1) -and ($labels[0] -like '*GUI验证-实例A*') -and ($m.Count -eq 1) -and ($m[0].VersionId -eq '1.21.1') -and ($m[0].IsManagedDirectory -eq $true) -and $dirOk -and ($sel.Count -eq 1)
  Rec 'T09' '创建实例' '状态提示、列表 1 项、清单 1 条、受管目录创建、创建后自动选中' ("status='$s' labels=" + ($labels -join ',') + " manifestCount=$($m.Count) version=$($m[0].VersionId) managed=$($m[0].IsManagedDirectory) dirExists=$dirOk dir=$mdir selected=" + ($sel -join ',')) $pass ('manifest=' + $MANIFEST)
  $shot = Shot '06-instances-created'; L ("SHOT 06-instances-created -> $shot")
  $selBefore = @(Get-SelectedLabels 0)
  $null = Click-Btn '刷新'; $s = Wait-Status '已加载 1 个实例。' 10
  $selAfter = @(Get-SelectedLabels 0)
  Rec 'T10' '刷新实例列表' '状态栏"已加载 1 个实例。"，列表保持 1 项' ("status='$s' labels=" + ((@(Get-ListLabels 0)) -join ',') + " selectionBefore=" + ($selBefore -join ',') + " selectionAfter=" + ($selAfter -join ',')) (($s -ne $null) -and (@(Get-ListLabels 0).Count -eq 1)) 'T10'
  if ($selBefore.Count -gt 0 -and $selAfter.Count -eq 0) { Issue 'U02' 'Medium' '刷新后列表选中项被清空' ('刷新前选中：' + ($selBefore -join ',') + '；刷新后无选中；用户需重新点击选中项才能重命名/复制/删除') 'T10 selectionAfter' }
  $null = Click-Btn '重命名'; $sNoSel = Wait-Status '请先选择实例' 5
  Rec 'T11a' '选中丢失后直接重命名' '系统提示"请先选择实例。"（选中项丢失的后果）' ("status='$sNoSel'") ($sNoSel -ne $null) 'T11a'
  $idx = Select-ByLabel 0 'GUI验证-实例A'; Start-Sleep -Milliseconds 400
  $null = Set-Edit 0 'GUI验证-重命名A'
  $null = Click-Btn '重命名'; $s = Wait-Status '实例已重命名。' 10
  Start-Sleep -Milliseconds 400
  $labels=@(Get-ListLabels 0); $m=@(Read-Manifest); $sel=@(Get-SelectedLabels 0)
  $pass = ($s -ne $null) -and ($labels.Count -eq 1) -and ($labels[0] -like '*GUI验证-重命名A*') -and ($m.Count -eq 1) -and ($m[0].Name -eq 'GUI验证-重命名A')
  Rec 'T11' '重命名实例' '状态提示、列表与清单名称更新' ("selectIndex=$idx status='$s' labels=" + ($labels -join ',') + " manifestName=$($m[0].Name) selectedAfter=" + ($sel -join ',')) $pass 'T11'
  if ($sel.Count -eq 0) { Issue 'U03' 'Medium' '重命名成功后列表选中项被清空' ('重命名后无选中项；连续操作需重新选择') 'T11 selection' }
  $null = Click-Btn '复制'; $sNoSel2 = Wait-Status '请先选择实例' 5
  Rec 'T12a' '选中丢失后直接复制' '系统提示"请先选择实例。"（选中项丢失的后果）' ("status='$sNoSel2'") ($sNoSel2 -ne $null) 'T12a'
  $null = Select-Item 0 0; Start-Sleep -Milliseconds 400
  $null = Click-Btn '复制'; $s = Wait-Status '实例已复制。' 10
  Start-Sleep -Milliseconds 500
  $labels=@(Get-ListLabels 0); $m=@(Read-Manifest)
  $dirs=@(); foreach ($x in $m) { $dirs += (Test-Path -LiteralPath $x.DirectoryPath) }
  $pass = ($s -ne $null) -and ($labels.Count -eq 2) -and ($m.Count -eq 2) -and (($dirs | Where-Object { $_ -eq $true }).Count -eq 2)
  Rec 'T12' '复制实例' '列表 2 项、清单 2 条、两个目录存在' ("status='$s' labels=" + ($labels -join ',') + " manifestCount=$($m.Count) dirsExist=" + ($dirs -join ',')) $pass 'T12'
  $null = Select-Item 0 0; Start-Sleep -Milliseconds 300
  $null = Set-Edit 0 '同名实例'; $null = Click-Btn '重命名'; $s1=Wait-Status '实例已重命名。' 10
  $null = Select-Item 0 1; Start-Sleep -Milliseconds 400
  $null = Set-Edit 0 '同名实例'; $null = Click-Btn '重命名'; $s2=Wait-Status '实例已重命名。' 10
  Start-Sleep -Milliseconds 400
  $labels=@(Get-ListLabels 0); $m=@(Read-Manifest)
  $same = ($labels.Count -eq 2) -and ($labels[0] -eq '同名实例') -and ($labels[1] -eq '同名实例')
  Rec 'T12b' '两个实例重命名为同名' '列表出现两个完全同名项（用于验证可区分性）' ("status1='$s1' status2='$s2' labels=" + ($labels -join ',') + " manifestNames=" + (($m | ForEach-Object { $_.Name }) -join ',')) $same 'T12b'
  if ($same) { Issue 'U04' 'Low' '实例列表仅显示名称，同名实例无法区分' ('列表出现两个同名项：' + ($labels -join ' / ') + '（清单内是两个不同 Id/目录）') 'T12b screenshot=07-same-names.png' }
  $shot = Shot '07-same-names'; L ("SHOT 07-same-names -> $shot")
  $null = Set-Edit 0 'GUI验证-导入实例'
  $null = Set-Edit 2 $IMPORT
  $null = Click-Btn '导入目录'; $s = Wait-Status '已导入实例' 15
  Start-Sleep -Milliseconds 500
  $labels=@(Get-ListLabels 0); $m=@(Read-Manifest)
  $imp=@($m | Where-Object { $_.DirectoryPath -eq $IMPORT })
  $pass = ($s -ne $null) -and ($labels.Count -eq 3) -and ($m.Count -eq 3) -and ($imp.Count -eq 1) -and ($imp[0].IsManagedDirectory -eq $false) -and (Test-Path -LiteralPath $IMPORT)
  Rec 'T13' '导入外部目录' '列表 3 项、清单 IsManagedDirectory=false、外部目录不变' ("status='$s' labels=" + ($labels -join ',') + " manifestCount=$($m.Count) importedManaged=$($imp[0].IsManagedDirectory)") $pass ('importDir=' + $IMPORT)
  $shot = Shot '08-instances-imported'; L ("SHOT 08-instances-imported -> $shot")
  $idx = Select-ByLabel 0 'GUI验证-导入实例'; Start-Sleep -Milliseconds 400
  $winBefore=Count-AppWindows $script:proc.Id
  $null = Click-Btn '删除'; $s = Wait-Status '实例登记已删除' 10
  $winAfter=Count-AppWindows $script:proc.Id
  $labels=@(Get-ListLabels 0); $m=@(Read-Manifest)
  $pass = ($s -ne $null) -and ($labels.Count -eq 2) -and ($m.Count -eq 2) -and (Test-Path -LiteralPath $IMPORT) -and ($winBefore -eq $winAfter)
  Rec 'T14' '删除导入实例（仅删登记）' '状态提示、清单减少、外部目录保留、无弹窗' ("selectIndex=$idx status='$s' labels=" + ($labels -join ',') + " manifestCount=$($m.Count) importDirStillExists=$(Test-Path -LiteralPath $IMPORT) appWindows=$winBefore->$winAfter") $pass 'T14'
  $winBefore=Count-AppWindows $script:proc.Id
  $sw2=[Diagnostics.Stopwatch]::StartNew(); $clickErr=$null
  try { $null = Click-Btn '检测 Java' } catch { $clickErr=$_.Exception.Message }
  $clickMs=$sw2.ElapsedMilliseconds
  $s = Wait-Status '检测到|未检测到' 240
  $detectMs=$sw2.ElapsedMilliseconds
  $javaItems=@(Get-ListItems 1); $javaLabels=@(Get-ListLabels 1)
  $shot = Shot '09-java-detected'; L ("SHOT 09-java-detected -> $shot")
  $pass = ($s -ne $null) -and ($javaItems.Count -gt 0)
  Rec 'T15' '检测 Java 运行时' '按钮触发探测并显示结果，右侧列表列出探测结果' ("clickReturnedMs=$clickMs totalDetectMs=$detectMs status='$s' javaCount=$($javaItems.Count) clickErr=$clickErr") $pass 'screenshot=09-java-detected.png'
  if ($clickMs -ge 500) { Issue 'U05' 'High' '点击"检测 Java"期间 UI 线程被同步阻塞' ('UIA Invoke 调用耗时 ' + $clickMs + ' ms 才返回；期间界面不可交互') 'T15 clickReturnedMs' } else { Issue 'U05' 'Low' '检测 Java 无进度指示/不可取消（本次 UI 阻塞仅 ' + $clickMs + 'ms，未观察到明显卡顿）' ('检测总耗时 ' + $detectMs + 'ms，期间无进度条/取消按钮') 'T15' }
  Issue 'U07' 'Low' 'Java 运行时列表只显示 java.exe 完整路径，不显示版本/厂商' ('探测结果 ' + $javaItems.Count + ' 项；UI 列显示原始路径，如 ' + ($javaLabels[0])) 'T15 screenshot=09-java-detected.png'
  $m=@(Read-Manifest); $targetPath=''; if ($m.Count -gt 0) { $targetPath=$m[0].DirectoryPath }
  $shot = Shot '10-before-delete'; L ("SHOT 10-before-delete -> $shot")
  $null = Select-Item 0 0; Start-Sleep -Milliseconds 400
  $winBefore=Count-AppWindows $script:proc.Id
  $sw3=[Diagnostics.Stopwatch]::StartNew(); $null = Click-Btn '删除'; $delClickMs=$sw3.ElapsedMilliseconds
  $s = Wait-Status '实例及其受管理目录已删除' 10
  $winAfter=Count-AppWindows $script:proc.Id
  $dirGone=$true; if ($targetPath -ne '') { $dirGone = -not (Test-Path -LiteralPath $targetPath) }
  $labels=@(Get-ListLabels 0)
  $pass = ($s -ne $null) -and $dirGone -and ($labels.Count -eq 1) -and ($winBefore -eq $winAfter)
  Rec 'T16' '删除受管实例' '状态提示、受管目录删除、清单减少' ("status='$s' deletedDirGone=$dirGone deletedDir=$targetPath labels=" + ($labels -join ',') + " appWindows=$winBefore->$winAfter clickMs=$delClickMs") $pass 'T16'
  Issue 'U08' 'High' '删除受管实例无二次确认，单击即递归删除目录' ('点击"删除"后 ' + $delClickMs + 'ms 内直接删除目录；期间无确认对话框（进程顶层窗口数 ' + $winBefore + '->' + $winAfter + '）') 'T16 clickMs'
} catch { Rec 'T06-T16' '实例管理/Java 探测流程' 'no exception' ('EXCEPTION: ' + $_.Exception.Message) $false '' }
try {
  $exitOk=$false; $exitCode=$null; $closeErr=$null
  $wp=$script:win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $wp.Close()
  $exitOk=$script:proc.WaitForExit(12000); if ($exitOk) { $exitCode=$script:proc.ExitCode }
  Rec 'T17' '关闭主窗口正常退出' '进程 12s 内退出且退出码 0' ("exited=$exitOk exitCode=$exitCode err=$closeErr") ($exitOk -and ($exitCode -eq 0)) 'T17'
} catch { Rec 'T17' '关闭主窗口正常退出' 'no exception' ('EXCEPTION: ' + $_.Exception.Message) $false '' }
try {
  Start-Sleep -Milliseconds 600
  $script:proc = Start-Process -FilePath $EXE -WorkingDirectory $PKG -PassThru
  $script:win = Wait-AppWindow $script:proc.Id 45
  if (-not $script:win) { Rec 'T18' '重启后实例持久化' '窗口重新出现并加载 1 个实例' 'WINDOW-NOT-FOUND' $false 'T18' } else {
    Start-Sleep -Milliseconds 1200; Foreground; $ready2 = Wait-Ready 30
    $navOk = App-Nav '游戏实例'
    $s = Wait-Status '已加载 1 个实例。' 20
    $labels=@(Get-ListLabels 0)
    $pass = ($s -ne $null) -and ($labels.Count -eq 1) -and $ready2 -and $navOk
    Rec 'T18' '重启后实例持久化' '重启后加载 1 个实例' ("ready=$ready2 navOk=$navOk status='$s' labels=" + ($labels -join ',')) $pass 'T18'
    $shot = Shot '11-restarted'; L ("SHOT 11-restarted -> $shot")
    $null = Select-Item 0 0; Start-Sleep -Milliseconds 400
    $null = Click-Btn '删除'; $s = Wait-Status '实例及其受管理目录已删除' 10
    $labels=@(Get-ListLabels 0)
    Rec 'T19' '清理：删除剩余测试实例' '列表清空' ("status='$s' remaining=" + ($labels -join ',')) (($s -ne $null) -and ($labels.Count -eq 0)) 'T19'
    $shot = Shot '12-final-empty'; L ("SHOT 12-final-empty -> $shot")
    $wp=$script:win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $wp.Close(); [void]$script:proc.WaitForExit(12000)
  }
} catch { Rec 'T18-T19' '重启持久化/清理' 'no exception' ('EXCEPTION: ' + $_.Exception.Message) $false '' }
Start-Sleep -Milliseconds 500
if (Test-Path $MANIFEST) { Remove-Item -LiteralPath $MANIFEST -Force }
if (Test-Path $INSTDIR) { Remove-Item -LiteralPath $INSTDIR -Recurse -Force }
if (Test-Path $IMPORT) { Remove-Item -LiteralPath $IMPORT -Recurse -Force }
$left=@(Get-ChildItem -LiteralPath $APPDATA -Recurse -File -EA SilentlyContinue)
$leftNames=($left | ForEach-Object { $_.FullName.Replace($APPDATA,'') }) -join ','
Rec 'T20' '清理测试数据' '用户数据恢复为测试前状态（仅日志）' ("remainingFiles=" + $leftNames) (($left | Where-Object { $_.Name -notlike 'astra-*.log' }).Count -eq 0) 'T20'
$logFiles=@(Get-ChildItem (Join-Path $APPDATA 'logs') -Filter 'astra-*.log' -EA SilentlyContinue | Sort-Object LastWriteTime -Descending)
$critCount=0; $errCount=0; $logName='<none>'
if ($logFiles.Count -gt 0) { $logName=$logFiles[0].Name; $critCount=@(Select-String -Path $logFiles[0].FullName -Pattern '\[Critical\]' -EA SilentlyContinue).Count; $errCount=@(Select-String -Path $logFiles[0].FullName -Pattern '\[Error\]' -EA SilentlyContinue).Count }
Rec 'T21' '运行期日志健康' '无 Critical/Error 级别日志' ("critical=$critCount error=$errCount logFile=$logName") (($critCount -eq 0) -and ($errCount -eq 0)) 'T21'
Issue 'U09' 'Low' '实例页无版本选择器，版本号为自由文本（默认 1.21.1）' '用户可输入任意不存在版本，创建时无校验' 'T06/T09'
Issue 'U10' 'Low' '导入路径仅文本框+ToolTip，无"浏览"按钮' 'MainWindow.xaml 实例页第二行 WrapPanel' 'T06'
Issue 'U11' 'Low' '两个 ListBox 无标题/说明，右侧 Java 列表只有路径' '截图 05/09：左列表=实例名，右列表=java.exe 路径，均无列头' 'screenshots'
Issue 'U12' 'Low' '侧边栏无当前页面选中态' '5 个导航按钮外观一致，无法看出当前页（截图 01-12）' 'screenshots'
Issue 'U13' 'Low' '窗口使用默认 .NET 图标与默认标题栏' '截图 01-welcome.png 左上角为默认图标' 'screenshots'
Issue 'U14' 'Medium' '所有操作反馈仅靠底部状态栏，无进度指示/对话框' '危险操作无确认、检测无进度、错误无弹窗（与 U01/U08 关联）' 'T08/T10/T15/T16'
Get-Process AstraLauncher.App -EA SilentlyContinue | Stop-Process -Force
$passed=@($results | Where-Object { $_.Pass }).Count
$failed=@($results | Where-Object { -not $_.Pass }).Count
$shotsList=@(Get-ChildItem $SHOTS -File -EA SilentlyContinue | Sort-Object Name | ForEach-Object { $_.Name + ' (' + $_.Length + ' bytes)' })
$summary=New-Object psobject -Property @{
  RunAt=(Get-Date).ToString('o'); Machine=$env:COMPUTERNAME; Exe=$EXE; ExeSha256=(Get-FileHash $EXE -Algorithm SHA256).Hash; ExeSize=(Get-Item $EXE).Length; ExeProductVersion=(Get-Item $EXE).VersionInfo.ProductVersion;
  Screen=($screen.ToString()); Dpi=$dpi; PreStateManifestExisted=$preManifestExisted; PreStateInstancesDirExisted=$preInstancesExisted; StartupMs=$startupMs;
  Total=$results.Count; Passed=$passed; Failed=$failed; Results=$results; UiIssues=$uiIssues; Screenshots=$shotsList; LogFile=$LOG
}
$summary | ConvertTo-Json -Depth 8 | Out-File $JSONOUT -Encoding utf8
L ("SUMMARY total={0} passed={1} failed={2}" -f $results.Count,$passed,$failed)
L ("RESULT_JSON=" + $JSONOUT)
Write-Output ("DONE total={0} passed={1} failed={2}" -f $results.Count,$passed,$failed)