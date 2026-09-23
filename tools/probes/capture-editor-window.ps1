# ============================================================================
# 判据资产（tools/probes/）：**采「用户真正看到的那个画面」** —— 编辑器窗口级截屏。
#
# 为什么必须有它（片BV 的根因所在）：
#   `unity command capture_game_view --source camera|screen` 采的是**游戏自己渲染出来
#   的后缓冲**（source=screen 也只是含 Overlay 画布的合成后缓冲）。Unity 编辑器在
#   Game view 上**叠加绘制**的东西（组件图标 = AudioSource 的喇叭 / Light 的太阳、
#   DrawGizmos 线框、选中高亮）**不在那个后缓冲里**，所以既有取证路径全部采不到它们 ——
#   这就是「开火时的喇叭/太阳图标」长期没被截图抓到、却能被用户看见的原因。
#   本脚本走 OS 层（PrintWindow / 桌面合成）采**窗口像素**，才和用户的眼睛同源。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File capture-editor-window.ps1 `
#       -Out <abs.png> -EditorPid 11364 [-X 301 -Y 177 -W 1362 -H 553] [-Focus] [-Desktop]
#
#   -EditorPid   Unity 编辑器进程 id（`unity status --format json` 的 data.instances[].pid）
#   -X/-Y/-W/-H  可选：只裁窗口内的一块（编辑器窗口内相对坐标，= EditorWindow.position 减去窗口左上角）
#   -Desktop     改走桌面合成（Graphics.CopyFromScreen），用于 PrintWindow 采到黑图的场合
#
# ASCII-only（PS 5.1 读无 BOM 的 .ps1 按 ANSI 解析）。
# ============================================================================
param(
  [Parameter(Mandatory=$true)][string]$Out,
  [Parameter(Mandatory=$true)][int]$EditorPid,
  [int]$X = -1,
  [int]$Y = -1,
  [int]$W = 0,
  [int]$H = 0,
  [switch]$Focus,
  [switch]$TopMost,
  [switch]$Desktop
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace CloverCap -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr hWnd, System.IntPtr hdcBlt, uint nFlags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr lp);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(System.IntPtr hWnd, System.Text.StringBuilder s, int n);
public delegate bool EnumProc(System.IntPtr hWnd, System.IntPtr lp);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

# Unity 的 Process.MainWindowHandle 会指到一个 160x28 的隐藏辅助窗口（实测 -32000,-32000）
# => 必须自己枚举 pid 名下的可见顶层窗口，取面积最大的那个（= 编辑器主窗口）。
function Get-BestWindow([int]$wantPid) {
  $cb = [CloverCap.Win32+EnumProc]{
    param($h, $l)
    $wpid = 0
    [void][CloverCap.Win32]::GetWindowThreadProcessId($h, [ref]$wpid)
    if ($wpid -eq $wantPid -and [CloverCap.Win32]::IsWindowVisible($h)) {
      $r = New-Object CloverCap.Win32+RECT
      if ([CloverCap.Win32]::GetWindowRect($h, [ref]$r)) {
        $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        $sbT = New-Object System.Text.StringBuilder 512
        [void][CloverCap.Win32]::GetWindowTextW($h, $sbT, 512)
        if ($area -gt $bestArea -and $sbT.Length -gt 0) {
          $script:bestH = $h; $script:bestArea = $area; $script:bestRect = $r
        }
      }
    }
    return $true
  }
  [void][CloverCap.Win32]::EnumWindows($cb, [IntPtr]::Zero)
  return @{ hwnd = $script:bestH; rect = $script:bestRect }
}

$script:bestH = [IntPtr]::Zero; $script:bestArea = 0; $script:bestRect = $null
$found = Get-BestWindow $EditorPid
$hwnd = $found.hwnd
if ($hwnd -eq [IntPtr]::Zero) { Write-Error ("no visible top-level window for pid " + $EditorPid); exit 2 }

if ($Focus) {
  [void][CloverCap.Win32]::ShowWindow($hwnd, 9)   # SW_RESTORE
  [void][CloverCap.Win32]::SetForegroundWindow($hwnd)
  Start-Sleep -Milliseconds 600
}
# 实测：本机 AI 宿主的 IDE 窗口常年 topmost，SetForegroundWindow 抢不过它 ⇒ 桌面合成会采到 IDE。
# SetWindowPos(HWND_TOPMOST) 不受前台锁限制，是唯一能把编辑器真正放到最上面的办法；
# 采完立刻恢复 NOTOPMOST（幂等、无残留）。
$hwndTM = -1
$hwndNTM = -2
if ($TopMost) {
  [void][CloverCap.Win32]::SetWindowPos($hwnd, [IntPtr]$hwndTM, 0, 0, 0, 0, 0x0043)  # NOMOVE|NOSIZE|SHOWWINDOW
  Start-Sleep -Milliseconds 900
}

$rect = New-Object CloverCap.Win32+RECT
if (-not [CloverCap.Win32]::GetWindowRect($hwnd, [ref]$rect)) { Write-Error 'GetWindowRect failed'; exit 2 }
$ww = $rect.Right - $rect.Left
$wh = $rect.Bottom - $rect.Top
Write-Host ("window hwnd=" + $hwnd + " rect=" + $rect.Left + "," + $rect.Top + " " + $ww + "x" + $wh + " visible=" + [CloverCap.Win32]::IsWindowVisible($hwnd))

if ($Desktop) {
  # 桌面合成：拿到的就是合成器上真正显示的像素（含其它窗口遮挡）。
  $bmp = New-Object System.Drawing.Bitmap($ww, $wh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($ww, $wh)))
  $g.Dispose()
} else {
  $bmp = New-Object System.Drawing.Bitmap($ww, $wh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  # 2 = PW_RENDERFULLCONTENT (DWM/GPU 渲染窗口也能采到内容；1 = PW_CLIENTONLY 会丢内容)
  $ok = [CloverCap.Win32]::PrintWindow($hwnd, $hdc, 2)
  $g.ReleaseHdc($hdc)
  $g.Dispose()
  Write-Host ("PrintWindow ok=" + $ok)
}

if ($X -ge 0 -and $Y -ge 0 -and $W -gt 0 -and $H -gt 0) {
  $crop = New-Object System.Drawing.Bitmap($W, $H)
  $gc = [System.Drawing.Graphics]::FromImage($crop)
  $gc.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0, 0, $W, $H)),
                (New-Object System.Drawing.Rectangle($X, $Y, $W, $H)), [System.Drawing.GraphicsUnit]::Pixel)
  $gc.Dispose()
  $bmp.Dispose()
  $bmp = $crop
}

if ($TopMost) {
  [void][CloverCap.Win32]::SetWindowPos($hwnd, [IntPtr]$hwndNTM, 0, 0, 0, 0, 0x0043)
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$fi = Get-Item $Out
Write-Host ("saved " + $fi.FullName + " bytes=" + $fi.Length)
