$ErrorActionPreference='Continue'
$EXE='F:\MC\AstraLauncher.exe'
$BASE='C:\Users\admin\AppData\Local\Temp\astra-audit\m7'
$LOG=Join-Path $BASE 'final2.log'
if(Test-Path $LOG){Remove-Item $LOG -Force}
function L($s){ $line=(Get-Date).ToString('HH:mm:ss')+' '+$s; Add-Content -Path $LOG -Value $line -Encoding utf8; Write-Host $line }
Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes; Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public class F2W {
 public delegate bool P(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(P cb, IntPtr l);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
 [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 private struct RECT { public int L; public int T; public int R; public int B; }
 public static List<IntPtr> All(){ var x=new List<IntPtr>(); EnumWindows((h,l)=>{ if(IsWindowVisible(h)) x.Add(h); return true; }, IntPtr.Zero); return x; }
 public static string Cls(IntPtr h){ var b=new StringBuilder(256); GetClassName(h,b,256); return b.ToString(); }
 public static string Ttl(IntPtr h){ var b=new StringBuilder(512); GetWindowText(h,b,512); return b.ToString(); }
 public static int[] Rect(IntPtr h){ RECT r; GetWindowRect(h,out r); return new[]{r.L,r.T,r.R,r.B}; }
}
"@
[void][F2W]::SetProcessDPIAware()
$AE=[System.Windows.Automation.AutomationElement]; $CT=[System.Windows.Automation.ControlType]; $TS=[System.Windows.Automation.TreeScope]
function W{ $c=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,$CT::Window); foreach($x in @($AE::RootElement.FindAll($TS::Children,$c))){ try{ if($x.Current.ClassName -eq 'Window' -and $x.Current.Name -eq 'Astra Launcher'){ return $x } }catch{} }; return $null }
function AllOf($r,$t){ $c=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,$t); return @($r.FindAll($TS::Descendants,$c)) }
function Txt{ $r=@(); $w=W; if($w){ foreach($e in @(AllOf $w $CT::Text)){ try{ $r+=$e.Current.Name }catch{} } }; return $r }
function Click($name){ $w=W; if(-not $w){ return $false }; foreach($b in @(AllOf $w $CT::Button)){ try{ if($b.Current.Name -eq $name){ $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } }catch{} }; return $false }
function WaitTxt($pat,$sec){ $sw=[Diagnostics.Stopwatch]::StartNew(); while($sw.Elapsed.TotalSeconds -lt $sec){ $t=@(Txt); foreach($x in $t){ if($x -match $pat){ return $x } }; Start-Sleep -Milliseconds 300 }; return $null }
function Shot($name){ $w=W; if(-not $w){ return '<none>' }; try{ $h=[IntPtr]$w.Current.NativeWindowHandle; [void][F2W]::ShowWindow($h,9); [void][F2W]::SetForegroundWindow($h); Start-Sleep -Milliseconds 700; $r=[F2W]::Rect($h); $b=New-Object System.Drawing.Bitmap(($r[2]-$r[0]),($r[3]-$r[1])); $g=[System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($r[0],$r[1],0,0,$b.Size); $p=Join-Path $BASE ('shots\'+$name+'.png'); $b.Save($p,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose(); return $p }catch{ return '<fail>' } }
function GameWin{ foreach($h in @([F2W]::All())){ try{ if([F2W]::Cls($h) -eq 'GLFW30' -or [F2W]::Ttl($h) -like '*Minecraft*'){ return $h } }catch{} }; return [IntPtr]::Zero }
function ShotGame($name){ try{ $h=GameWin; if($h.ToInt64() -eq 0){ return '<none>' }; [void][F2W]::ShowWindow($h,9); [void][F2W]::SetForegroundWindow($h); Start-Sleep -Milliseconds 800; $r=[F2W]::Rect($h); $b=New-Object System.Drawing.Bitmap(($r[2]-$r[0]),($r[3]-$r[1])); $g=[System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($r[0],$r[1],0,0,$b.Size); $p=Join-Path $BASE ('shots\'+$name+'.png'); $b.Save($p,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose(); return $p }catch{ return '<fail>' } }
L 'FINAL2 START'
Get-Process AstraLauncher,AstraLauncher.App,java,javaw -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Remove-Item 'F:\MC\.minecraft\AstraLauncher\servers.json' -Force -EA SilentlyContinue
$p=Start-Process -FilePath $EXE -WorkingDirectory 'F:\MC' -PassThru
$app=$null; $sw=[Diagnostics.Stopwatch]::StartNew(); while($sw.Elapsed.TotalSeconds -lt 90 -and -not $app){ $app=W; Start-Sleep -Milliseconds 300 }
L ('window=' + [bool]$app + ' after=' + [int]$sw.Elapsed.TotalSeconds + 's')
Start-Sleep -Seconds 6
L ('home=' + ((@(Txt) | Where-Object { $_ -match '本地版本' }) -join ' || '))
L ('SHOT ' + (Shot 'final2-home'))
$null=Click '服务器'; Start-Sleep -Seconds 1
L ('servers=' + (WaitTxt '服务器列表' 10))
$w=W; $e=@(AllOf $w $CT::Edit); L ('edits=' + $e.Count)
if($e.Count -ge 3){ $e[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('本地测试服'); $e[1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('25565'); $e[2].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('127.0.0.1') }
L ('save=' + (Click '保存服务器'))
L ('saveStatus=' + (WaitTxt '已保存服务器' 15))
L ('SHOT ' + (Shot 'final2-servers'))
$null=Click '游戏实例'; Start-Sleep -Seconds 2
$w=W; $c=New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,$CT::ListItem); $list=$null; $best=0
foreach($l in @(AllOf $w $CT::List)){ $cnt=@($l.FindAll($TS::Descendants,$c)).Count; if($cnt -gt $best){ $best=$cnt; $list=$l } }
L ('instances=' + $best)
if($list){ try{ $sp=$list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); if($sp.Current.VerticallyScrollable){ $sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,[System.Windows.Automation.ScrollAmount]::LargeIncrement); Start-Sleep -Milliseconds 500 } }catch{}; foreach($it in @($list.FindAll($TS::Descendants,$c))){ $n=''; try{ $n=$it.Current.Name }catch{}; if($n -match '1\.21\.1'){ $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); L ('selected=' + $n); break } } }
Start-Sleep -Milliseconds 500
$null=Click '服务器'; Start-Sleep -Seconds 1
L ('join=' + (Click '启动并加入'))
Start-Sleep -Seconds 2
$pt=@(Txt) | Where-Object { $_ -match '准备启动|加入服务器|启动游戏|请先|失败' } | Select-Object -Last 6
L ('post=' + ($pt -join ' || '))
$gh=[IntPtr]::Zero; $sw=[Diagnostics.Stopwatch]::StartNew()
while($sw.Elapsed.TotalSeconds -lt 300){ Start-Sleep -Seconds 2; $gh=GameWin; if($gh.ToInt64() -ne 0){ break } }
L ('game=' + [bool]($gh.ToInt64() -ne 0) + ' after=' + [int]$sw.Elapsed.TotalSeconds + "s title='" + [F2W]::Ttl($gh) + "'")
if($gh.ToInt64() -ne 0){ Start-Sleep -Seconds 15; L ('SHOT ' + (ShotGame 'final2-game')); [void][F2W]::PostMessage($gh,0x0010,[IntPtr]::Zero,[IntPtr]::Zero); Start-Sleep -Seconds 4; $ex=WaitTxt '游戏已退出' 120; L ('exit=' + $ex) }
$app=W; if($app){ try{ $app.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }catch{} }
Start-Sleep -Seconds 4; Get-Process AstraLauncher,AstraLauncher.App -EA SilentlyContinue | Stop-Process -Force
$lf=Get-ChildItem 'F:\MC\.minecraft\AstraLauncher\logs' -Filter 'astra-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if($lf){ foreach($j in @(Select-String -Path $lf.FullName -Pattern 'Joining server' | Select-Object -Last 2 | ForEach-Object { $_.Line })){ L ('applog: ' + $j) }; $c1=@(Select-String -Path $lf.FullName -Pattern '\[Critical\]').Count; $c2=@(Select-String -Path $lf.FullName -Pattern '\[Error\]').Count; L ('applog critical=' + $c1 + ' error=' + $c2) }
L 'FINAL2 DONE'