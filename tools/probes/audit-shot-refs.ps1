# 判据资产（tools/probes/）：**取证截图引用审计** —— `.ai-tmp/screenshots/` 里的每个 png
# 必须被某个表 / 清单 / manifest 引用（引用计数 >= 1），否则它就是"画完没人看"的中间产物
# （clover-engine SKILL 1.8 第 6 条：证据图是**一次性**产物，只留被判据引用到的那些）。
# 为什么它要是一支脚本：SKILL 0.6「必然性规则必须做成闸门」—— "每个图都有引用"是一个**可计算**的
# 断言，靠人记必然漏。切片 AF 用它查出 176 张里 78 张零引用 + 12 张 `b41_*` 连拍残留（只被
# 运行日志提到，无任何表引用），删掉 90 张后重跑 = 零引用 0 / 86 张全部有引用。
# 【切片AG（2026-09-21）已裁决并接线】本脚本现由 tools\verify.ps1 第 26 条
# `shot-refs-audited` 调用（.ai-tmp/screenshots 下零引用图 > 0 即 FAIL）——
# "图不被引用"从此自己变红；上一版（切片AF）按当时任务书"只许新增 home-credit-rendered 一条"未接。
# ASCII-only on purpose (PS 5.1 reads a BOM-less .ps1 as ANSI).
# Usage (also invoked by tools\verify.ps1 item 26, which parses the summary line):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\audit-shot-refs.ps1   # zero-ref list
#   powershell ... audit-shot-refs.ps1 -Png <name>.png                                      # per-file hits
#   powershell ... audit-shot-refs.ps1 -Matrix                                              # per-png referencing files
param([string]$Png = '', [switch]$Matrix, [switch]$Full)
$ErrorActionPreference = 'Stop'
# Root is derived from this script's own location (tools\probes\ -> tools\ -> project
# root) so the gate stays portable; a hard-coded absolute path would break on move
# (SKILL 8: no machine-local paths in shipped tools).
$root    = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$shotDir = Join-Path $root '.ai-tmp\screenshots'
$exts    = @('.md', '.tsv', '.txt', '.ps1', '.py', '.cs', '.json')

# Whole project EXCEPT the Unity caches and this slice's own audit outputs.
$all = @(Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
  Where-Object { $exts -contains $_.Extension.ToLower() } |
  Where-Object { $_.FullName -notmatch '\\Library\\|\\Temp\\|\\obj\\|\\Logs\\' } |
  Where-Object { $_.Name -notmatch '^af-' })
$all = @($all | Sort-Object FullName -Unique)

$texts = @()
foreach ($f in $all) {
  $t = ''
  try { $t = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8) } catch { continue }
  $texts += [pscustomobject]@{ Path = $f.FullName; Rel = $f.FullName.Substring($root.Length).TrimStart('\'); Text = $t }
}
Write-Output ("scanned files = " + $texts.Count)

function Count-Hits([string]$needle) {
  $n = 0
  foreach ($t in $texts) {
    $i = 0
    while ($true) {
      $i = $t.Text.IndexOf($needle, $i, [StringComparison]::OrdinalIgnoreCase)
      if ($i -lt 0) { break }
      $n++; $i += $needle.Length
    }
  }
  return $n
}

$pngs = @(Get-ChildItem $shotDir -Filter *.png -File | Sort-Object Name)
Write-Output ("screenshots = " + $pngs.Count)

if ($Matrix) {
  foreach ($p in $pngs) {
    $hits = @($texts | Where-Object { $_.Text.IndexOf($p.Name, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { $_.Rel })
    Write-Output ($p.Name + "`t" + $hits.Count + "`t" + ($hits -join ';'))
  }
  exit 0
}

if ($Png -ne '') {
  $pngs = @($pngs | Where-Object { $_.Name -eq $Png })
}
$zero = @()
foreach ($p in $pngs) {
  $c = Count-Hits $p.Name
  if ($c -eq 0) { $zero += $p.Name; Write-Output ("ZERO   " + $p.Name) }
  elseif ($Full) { Write-Output ("REF:" + $c + "  " + $p.Name) }
}
Write-Output ""
Write-Output ("=== unreferenced (count = " + $zero.Count + "): " + $zero.Count + " of " + $pngs.Count + " ===")
$zero | ForEach-Object { Write-Output ("  " + $_) }
if ($Png -ne '') {
  Write-Output ""
  Write-Output ("=== files mentioning " + $Png + " (total occurrences " + (Count-Hits $Png) + ") ===")
  $texts | Where-Object { $_.Text.IndexOf($Png, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { Write-Output ("  " + $_.Rel) }
}
