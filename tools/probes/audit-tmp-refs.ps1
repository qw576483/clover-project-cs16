# 判据资产（tools/probes/）：**临时产物引用审计** —— `.ai-tmp/test/` 与 `.ai-tmp/drivers/`
# 里的每个文件，必须要么被"权威文件"（策划/**、tools/**、client/**、项目根）引用，
# 要么被一份显式白名单收编；否则它就是"没人再看的中间产物"。
#
# 为什么它要是一支脚本：clover-engine SKILL 0.6「必然性规则必须做成闸门」/ SKILL 8「临时文件」
# —— "一次性产物不许堆积"是一个**可计算**的断言，靠人记必然漏。切片AH 用它做删除前的
# "引用计数 = 0"取证：先逐文件出引用矩阵，再按类别处置（保留 / 移 tools/probes / 删除）。
#
# 口径（三段分别报告，避免"目录内自引用"把死文件洗白）：
#   AUTH = 引用它的文件在 策划/ tools/ client/ docs/ 或项目根（= 权威文件，计数 >= 1 就值得留）
#   TMP  = 引用它的文件只在 .ai-tmp/ 下（= 同类兄弟产物；AUTH=0 且 TMP>=1 需人工看引用方是否保留）
#   SELF = 它自己提到自己名字（恒存在，不计入上面两段）
#
# ASCII-only on purpose (PS 5.1 reads a BOM-less .ps1 as ANSI).
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\audit-tmp-refs.ps1 -Matrix
#   powershell ... audit-tmp-refs.ps1 -Matrix -Dir .ai-tmp\test
#   powershell ... audit-tmp-refs.ps1 -Name <file name>      # 逐条列出提到它的文件
param([switch]$Matrix, [string]$Dir = '', [string]$Name = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exts = @('.md', '.tsv', '.txt', '.ps1', '.py', '.cs', '.json', '.yml', '.yaml', '.cfg', '.manifest')

# The two temp dirs this slice audits.
$targets = @()
if ($Dir -ne '') {
  $targets += (Join-Path $root $Dir)
} else {
  $targets += (Join-Path $root '.ai-tmp\test')
  $targets += (Join-Path $root '.ai-tmp\drivers')
}

# Whole project EXCEPT Unity caches / VCS / python caches / the screenshot png's.
$all = @(Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
  Where-Object { $exts -contains $_.Extension.ToLower() } |
  Where-Object { $_.FullName -notmatch '\\Library\\|\\Temp\\|\\obj\\|\\Logs\\|\\.git\\|\\__pycache__\\' } |
  Where-Object { $_.Name -notmatch '^ah-refs' })
$all = @($all | Sort-Object FullName -Unique)

$texts = @()
foreach ($f in $all) {
  $t = ''
  try { $t = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8) } catch { continue }
  $rel = $f.FullName.Substring($root.Length).TrimStart('\')
  $zone = 'OTHER'
  if ($rel -match '^(策划|tools|client|docs)\\') { $zone = 'AUTH' }
  elseif ($rel -match '^\.ai-tmp\\') { $zone = 'TMP' }
  else { $zone = 'AUTH' }   # project-root files (README / verify logs) count as authoritative
  $texts += [pscustomobject]@{ Path = $f.FullName; Rel = $rel; Text = $t; Zone = $zone }
}
Write-Output ("scanned files = " + $texts.Count)

if ($Name -ne '') {
  $hit = @($texts | Where-Object { $_.Rel -ne $Name -and $_.Text.IndexOf($Name, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
  Write-Output ("=== files mentioning " + $Name + " (count = " + $hit.Count + ") ===")
  $hit | ForEach-Object { Write-Output ("  [" + $_.Zone + "] " + $_.Rel) }
  exit 0
}

$files = @()
foreach ($d in $targets) {
  if (-not (Test-Path $d)) { continue }
  $files += @(Get-ChildItem $d -Recurse -File -ErrorAction SilentlyContinue)
}
$files = @($files | Sort-Object FullName)

Write-Output ("candidate files = " + $files.Count)
Write-Output ""
Write-Output ("name`tAUTH`tTMP`tsize`tauth-refs")
foreach ($p in $files) {
  $rel = $p.FullName.Substring($root.Length).TrimStart('\')
  $au = @(); $tp = @()
  foreach ($t in $texts) {
    if ($t.Path -eq $p.FullName) { continue }
    if ($t.Text.IndexOf($p.Name, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
    if ($t.Zone -eq 'AUTH') { $au += $t.Rel } else { $tp += $t.Rel }
  }
  Write-Output ($rel + "`t" + $au.Count + "`t" + $tp.Count + "`t" + $p.Length + "`t" + ($au -join ';'))
  if ($Matrix) {
    foreach ($a in $au) { Write-Output ("    AUTH " + $a) }
    foreach ($a in $tp) { Write-Output ("    TMP  " + $a) }
  }
}
