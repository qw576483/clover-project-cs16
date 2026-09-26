<#
  compile-file.ps1 —— 离线真编译**任意一个 .cs**（临时驱动 / 探针外壳 / 新写的业务文件）。

  为什么要它：临时驱动（.ai-tmp/drivers/*.cs）平时只在 `unity command run_script` 里才被编译，
  编不过的代价是"进了 Play、跑完了、才发现它根本没编进去"。本脚本用 Unity 自带的 Roslyn +
  项目的**真实引用集**离线编一次，秒级出结果。

  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/compile-file.ps1 -File .ai-tmp/drivers/xxx.cs
    powershell ... -File <任意.cs> [-Project <项目根>] [-UnityRoot <编辑器根>]
  -Project 默认 = 本脚本所在目录的上一级；-UnityRoot 只在自动探测失败时需要。

  退出码：0 = 无真错（可能只剩被过滤掉的假错）；1 = 有真错 / 前置条件不满足（原因打在输出里）。

  ⛔ 三条坑（任少一条就会得到**假的结论**）：
  ① 引用集必须三块齐全：
       <项目>/client/Library/ScriptAssemblies/*.dll    业务集：Cs16.dll / CloverEngine.* / Unity.Pipeline*
       <Unity>/Editor/Data/Managed/UnityEngine/*.dll    引擎集：UnityEngine.dll **不在** ScriptAssemblies 里
       <Unity>/Editor/Data/NetStandard/ref/2.1.0/*.dll  netstandard 2.1 引用
     少第三块会报 **28 个 CS0012**（"类型 Object/Enum/ValueType 在未引用的程序集中定义"）——那是**假错**。
  ② 引用必须走响应文件 `@refs.rsp`：379 个 `-r:` 拼在命令行上会直接撞 Windows 命令行长度上限
     （`dotnet.exe … The filename or extension is too long`），而那个失败长得很像"编译器没输出"。
  ③ 判据**只看 `CS1xxx`**（真语法错）；`CS0012` 且消息里含 `netstandard` 的一律当假错过滤并注明
     —— 本脚本已把这条过滤做成默认行为。
  ④ 传给 `dotnet.exe` 的 `csc.dll` 必须是**字符串**：把 `Get-ChildItem` 的 `FileInfo` 直接喂给原生命令，
     会得到 `dotnet.exe : The command could not be loaded, possibly because:` —— 那条报错与源码**毫无关系**
     （实测：同一个 csc 换成 `.FullName` 立刻 PASS）。所以下面 `$csc` 有一行显式 `.FullName`。
#>
param(
  [Parameter(Mandatory = $true)][string]$File,
  [string]$Project = '',
  [string]$UnityRoot = ''
)

function Fail([string]$msg) { Write-Host ('FAIL: ' + $msg); exit 1 }

if ($Project -eq '') { $Project = Split-Path -Parent $PSScriptRoot }
if (!(Test-Path -LiteralPath $Project)) { Fail ('-Project 不存在: ' + $Project) }
$Project = (Resolve-Path -LiteralPath $Project).Path
$client = Join-Path $Project 'client'
if (!(Test-Path -LiteralPath $client)) { Fail ('找不到 client/: ' + $client) }

if (!(Test-Path -LiteralPath $File)) { Fail ('-File 不存在: ' + $File) }
$src = (Resolve-Path -LiteralPath $File).Path

# ---- 编辑器版本 / 安装根 ----
$ver = ''
$verFile = Join-Path $client 'ProjectSettings\ProjectVersion.txt'
if (Test-Path -LiteralPath $verFile) {
  foreach ($ln in [IO.File]::ReadAllLines($verFile)) {
    if ($ln -match '^m_EditorVersion:\s*(\S+)') { $ver = $Matches[1]; break }
  }
}
$roots = @()
if ($UnityRoot -ne '') { $roots += $UnityRoot }
if ($ver -ne '') {
  $roots += (Join-Path 'C:\Program Files' ('Unity\Hub\Editor\' + $ver + '\Editor'))
  $roots += (Join-Path $env:ProgramFiles ('Unity\Hub\Editor\' + $ver + '\Editor'))
}
if ($env:UNITY_ROOT) { $roots += $env:UNITY_ROOT }
$root = ''
foreach ($r in $roots) {
  if ($r -and (Test-Path -LiteralPath (Join-Path $r 'Data\Managed\UnityEngine'))) { $root = $r; break }
}
if ($root -eq '') {
  Fail ('自动探测不到 Unity 编辑器（ProjectVersion 读到 "' + $ver + '"）⇒ 用 -UnityRoot <编辑器根> 显式指定，' +
        '例如 "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor"')
}

$csc = Get-ChildItem -LiteralPath (Join-Path $root 'Data\DotNetSdk\sdk') -Recurse -Filter 'csc.dll' -ErrorAction SilentlyContinue |
       Where-Object { $_.FullName -match 'Roslyn\\bincore' } | Sort-Object FullName | Select-Object -Last 1
if (!$csc) { Fail ('找不到 Roslyn 的 csc.dll（在 ' + (Join-Path $root 'Data\DotNetSdk\sdk') + ' 下）') }
# 必须取字符串：把 FileInfo 喂给原生命令会得到 "The command could not be loaded" 这种与源码无关的报错
$csc = $csc.FullName
$dotnet = Join-Path $root 'Data\NetCoreRuntime\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { Fail ('找不到 dotnet.exe: ' + $dotnet) }
Write-Host ('csc     = ' + $csc)
Write-Host ('dotnet  = ' + $dotnet)

Write-Host ('project = ' + $Project)
Write-Host ('source  = ' + $src)
Write-Host ('unity   = ' + $root)

# ---- 引用集三块（见脚本头 ①）----
$refDirs = @(
  (Join-Path $client 'Library\ScriptAssemblies'),
  (Join-Path $root 'Data\Managed\UnityEngine'),
  (Join-Path $root 'Data\NetStandard\ref\2.1.0')
)
$refs = @()
foreach ($d in $refDirs) {
  if (!(Test-Path -LiteralPath $d)) {
    Fail ('引用目录缺失: ' + $d + '  ⇒ 先在 Unity 里打开并编译过一次本项目（ScriptAssemblies 由编辑器产出）')
  }
  foreach ($f in (Get-ChildItem -LiteralPath $d -Filter *.dll)) { $refs += ('-r:"' + $f.FullName + '"') }
}
if ($refs.Count -eq 0) { Fail '引用集为空' }

# ---- 响应文件（见脚本头 ②）----
$work = Join-Path $Project '.ai-tmp\test\compile-file'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$rsp = Join-Path $work 'refs.rsp'
$outDll = Join-Path $work (((Split-Path -Leaf $src) -replace '\.cs$', '') + '.check.dll')
$lines = @('-nologo', '-t:library', '-langversion:9.0', '-noconfig', ('-out:"' + $outDll + '"'))
$lines += $refs
$lines += ('"' + $src + '"')
$enc = 'ASCII'
if (($lines -join '') -match '[^\x00-\x7F]') { $enc = 'UTF8' }   # 路径含中文时 ASCII 会写坏；csc 认 UTF-8 BOM
if (Test-Path -LiteralPath $outDll) { Remove-Item -LiteralPath $outDll -Force }
Set-Content -LiteralPath $rsp -Value $lines -Encoding $enc
Write-Host ('refs    = ' + $refs.Count + ' 个引用（rsp: ' + $rsp + '）')

# ---- 编译 ----
$raw = & $dotnet $csc ('@' + $rsp) 2>&1 | Out-String
$all = @($raw -split "`r?`n" | Where-Object { $_ -match ': error CS' })
$real = @()
$fake = 0
foreach ($e in $all) {
  if ($e -match 'CS0012' -and $e -match 'netstandard') { $fake++; continue }   # 见脚本头 ③
  $real += $e
}

Write-Host ''
if ($fake -gt 0) {
  Write-Host ('过滤假错: CS0012(netstandard) x' + $fake + ' —— 缺 netstandard 引用会让编译器把 Object/Enum/ValueType 报成"未定义"，与源码无关')
}
if ($real.Count -eq 0) {
  if (!(Test-Path -LiteralPath $outDll)) {
    Write-Host 'FAIL: 没有真错，但也没产出程序集（编译器可能压根没跑起来）—— 原始输出：'
    Write-Host $raw
    exit 1
  }
  Write-Host ('PASS  真错 = 0（产出 ' + (Get-Item -LiteralPath $outDll).Length + ' B: ' + $outDll + '）')
  exit 0
}
Write-Host ('FAIL  真错 = ' + $real.Count)
$real | Select-Object -First 30 | ForEach-Object { Write-Host ('  ' + $_) }
exit 1
