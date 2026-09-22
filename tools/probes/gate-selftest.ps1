# 判据资产（tools/probes/）：**闸门自身也要被闸** —— clover-engine SKILL 8.3 / anti-gaming.md 第 5 条
# 要求：任何"新增 / 加严"的检查项，必须同时给出**两次自检**的留痕 ——
#   ① 已知正确样本 ⇒ PASS    ② 注入已知缺陷 ⇒ FAIL（然后还原 ⇒ 回到 PASS）
# 本脚本覆盖本项目新造的三条闸门：
#   * differences-source-of-truth  （verify.ps1 第 24 条：差异登记.tsv 与验收表投影须同一行集）
#   * home-credit-rendered         （verify.ps1 第 25 条：首页署名 = 实际文本 + 实际字体，片AH-R 加严）
#   * shot-refs-audited            （verify.ps1 第 26 条：每张取证图都被引用）
#
# 它只做"改 → 跑 verify → 读该条状态 → 还原"，且每处改动都在还原后做**哈希自证**（未被篡改残留）。
# ⛔ 不删 .ai-tmp/screenshots/ 的任何既有文件：临时探针 png 是本脚本新建、用完即删的自己的文件。
#
# ASCII-only on purpose (PS 5.1 reads a BOM-less .ps1 as ANSI).
param([string]$Project = '')
$ErrorActionPreference = 'Continue'
if ($Project -eq '') { $Project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }

$report = New-Object System.Collections.Generic.List[string]
$bad = 0
function Note([string]$s) { $report.Add($s); Write-Output $s }
function HashOf([string]$p) { if (Test-Path $p) { return (Get-FileHash -Algorithm SHA256 $p).Hash } else { return '(missing)' } }
function WriteText([string]$p, [string]$body) { [IO.File]::WriteAllText($p, $body, (New-Object Text.UTF8Encoding($false))) }

# Run the project gate and return the verdict letter of one named check.
function Invoke-Gate([string]$gateName) {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate 2>&1 | ForEach-Object { [string]$_ })
  $rx = '^(PASS|FAIL|HUMAN-ONLY)\s+' + [regex]::Escape($gateName) + '(\s|$)'
  $line = @($out | Where-Object { $_ -match $rx } | Select-Object -First 1)
  if ($line.Count -eq 0) { return 'NO-VERDICT' }
  if ($line[0] -match '^PASS') { return 'PASS' }
  if ($line[0] -match '^FAIL') { return 'FAIL' }
  return 'HUMAN-ONLY'
}
function Expect([string]$what, [string]$got, [string]$want) {
  $ok = ($got -eq $want)
  if (-not $ok) { $script:bad++ }
  Note (($(if ($ok) { 'OK   ' } else { 'MISS ' })) + $what + ' : got=' + $got + ' want=' + $want)
}

Note '=== gate-selftest (two-sample rule: good sample PASSES, injected defect FAILS) ==='

# ---------------------------------------------------------------- 1) home-credit-rendered
$dump = Join-Path $Project 'tools\probes\home-screen-nodetree.txt'
$bak  = Join-Path $Project '.ai-tmp\test\ah-r-selftest-nodetree.bak'
Copy-Item $dump $bak -Force
$h0 = HashOf $dump
Expect 'home-credit-rendered / good (pristine dump)' (Invoke-Gate 'home-credit-rendered') 'PASS'

# injected defect: the font cannot draw lowercase -> the line would render ALL-CAPS.
$t   = [IO.File]::ReadAllText($dump, [Text.Encoding]::UTF8)
$n1  = $t.Replace('hasa=True', 'hasa=False')
if ($n1 -eq $t) { Note 'WARN  home-credit-rendered / bad-font : the hasa column was not found in the dump' }
WriteText $dump $n1
Expect 'home-credit-rendered / bad-font (hasa=False)' (Invoke-Gate 'home-credit-rendered') 'FAIL'

# injected defect: wrong case in the rendered literal. Re-read the pristine copy
# so this sample does not depend on the state the previous sample left behind.
$needle = "text='by clover-engine'"
$seen = ([regex]::Matches($t, [regex]::Escape($needle))).Count
Note ('  bad-case sample: credit-literal occurrence(s) in the dump = ' + $seen)
$n2 = $t.Replace($needle, "text='By Clover-Engine'")
WriteText $dump $n2
Expect 'home-credit-rendered / bad-case (By Clover-Engine)' (Invoke-Gate 'home-credit-rendered') 'FAIL'

Copy-Item $bak $dump -Force
Expect 'home-credit-rendered / restored' (Invoke-Gate 'home-credit-rendered') 'PASS'
Note ('  hash self-check: before=' + $h0 + ' after=' + (HashOf $dump) + ' identical=' + ($h0 -eq (HashOf $dump)))
# the sample's backup has served its purpose -- one-off artifacts must not pile up.
Remove-Item $bak -Force -ErrorAction Continue

# ---------------------------------------------------- 2) differences-source-of-truth
$plan = Join-Path $Project ([char[]]@(0x7B56,0x5212) -join '')
$reg  = Join-Path $plan ((([char[]]@(0x5DEE,0x5F02,0x767B,0x8BB0)) -join '') + '.tsv')
$rbak = Join-Path $Project '.ai-tmp\test\ah-r-selftest-registry.bak'
if (-not (Test-Path $reg)) {
  Note ('FAIL  differences-source-of-truth : registry not found at ' + $reg)
  $bad++
} else {
  Copy-Item $reg $rbak -Force
  $rh0 = HashOf $reg
  Expect 'differences-source-of-truth / good (pristine registry)' (Invoke-Gate 'differences-source-of-truth') 'PASS'
  # injected defect: a registry row whose id exists on NO section row.
  [IO.File]::AppendAllText($reg, ("999`tAH-R selftest bogus row`t`t`t`r`n"), (New-Object Text.UTF8Encoding($false)))
  Expect 'differences-source-of-truth / bad (orphan id 999)' (Invoke-Gate 'differences-source-of-truth') 'FAIL'
  Copy-Item $rbak $reg -Force
  Expect 'differences-source-of-truth / restored' (Invoke-Gate 'differences-source-of-truth') 'PASS'
  Note ('  hash self-check: before=' + $rh0 + ' after=' + (HashOf $reg) + ' identical=' + ($rh0 -eq (HashOf $reg)))
  Remove-Item $rbak -Force -ErrorAction Continue
}

# ----------------------------------------------------------------- 3) shot-refs-audited
# NOTE: the file name is assembled at RUNTIME on purpose. A literal name here
# would appear in this script's own text, the audit would count this file as a
# reference to it, and the injection would silently heal itself (exactly the
# trap verify.ps1 item 26 documents). A fresh random name is cited by nothing.
$pngName  = 'ahr' + 'selftest' + [guid]::NewGuid().ToString('N').Substring(0, 10) + '.png'
$probePng = Join-Path $Project ('.ai-tmp\screenshots\' + $pngName)
if (Test-Path $probePng) { Remove-Item $probePng -Force }
Expect 'shot-refs-audited / good (no stray png)' (Invoke-Gate 'shot-refs-audited') 'PASS'
# injected defect: a screenshot that no table / manifest / script cites.
[IO.File]::WriteAllBytes($probePng, [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D))
Expect 'shot-refs-audited / bad (zero-ref png added)' (Invoke-Gate 'shot-refs-audited') 'FAIL'
Remove-Item $probePng -Force
Expect 'shot-refs-audited / restored' (Invoke-Gate 'shot-refs-audited') 'PASS'

Note ''
Note ('===== gate-selftest summary: unmet-expectations=' + $bad + ' =====')
exit $(if ($bad -gt 0) { 1 } else { 0 })
