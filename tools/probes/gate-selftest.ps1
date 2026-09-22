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

# =====================================================================
#  4) slice AI: the 3 renamed checks (+ the no-handoff-docs name) and the
#     4 added checks (numeric-log-only / no-sync-subagents /
#     freeze-before-capture / evidence-economy).
#     Two samples, batched: every defect is injected at once so the whole
#     thing costs THREE gate runs (good / injected / restored) instead of
#     24 -- a gate run is ~8 s here, and per-item runs would be minutes.
#     The old item names must be GONE: a stale name has to yield NO-VERDICT.
# =====================================================================
function Invoke-GateTable {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate 2>&1 | ForEach-Object { [string]$_ })
  $tbl = @{}
  foreach ($ln in $out) {
    $m = [regex]::Match($ln, '^(PASS|FAIL|HUMAN-ONLY)\s+([a-z0-9\-]+)(\s|$)')
    if ($m.Success) { $tbl[$m.Groups[2].Value] = $m.Groups[1].Value }
  }
  return $tbl
}

$plan   = Join-Path $Project ([char[]]@(0x7B56,0x5212) -join '')                              # ce hua
$sp     = Join-Path $plan ((([char[]]@(0x9A8C,0x6536,0x8868)) -join '') + '.md')              # yan shou biao
$cNum   = ([char[]]@(0x6570,0x503C,0x7C7B) -join '')                                          # numeric class
$cVis   = ([char[]]@(0x8868,0x73B0,0x7C7B) -join '')                                          # visual class
$shots  = Join-Path $Project '.ai-tmp\screenshots'
$dlog   = Join-Path $Project '.ai-tmp\test\dispatch-log.tsv'
$nextMd = Join-Path $Project '.ai-tmp\test\NEXT.md'
$envChk = Join-Path $Project 'tools\env-check.ps1'
$envBak = Join-Path $Project '.ai-tmp\test\gx-selftest-env-check.bak'
$spBak  = $sp + '.gx-selftest.bak'
$dgBak  = $dlog + '.gx-selftest.bak'
$loose  = @()
1..60 | ForEach-Object { $loose += (Join-Path $shots ('gx-selftest-loose-' + $_ + '.png')) }

$renamed = @('allowed-diff', 'screenshot-refs', 'verify-entry', 'no-handoff-docs')
$added   = @('numeric-log-only', 'no-sync-subagents', 'freeze-before-capture', 'evidence-economy')
$oldNames = @('differences-registry', 'refs-reachable', 'gate-present', 'no-handover-docs', 'handoff-doc-found')

Note ''
Note '--- slice AI: renamed + added items ---'
$good = Invoke-GateTable
foreach ($n in ($renamed + $added)) { Expect ('good sample / ' + $n) ([string]$good[$n]) 'PASS' }
foreach ($o in $oldNames) { Expect ('renamed away (must be gone) / ' + $o) ([string]$good[$o]) '' }

# --- one batch injection, then one gate run --------------------------------
$hSp0 = HashOf $sp
$hDg0 = HashOf $dlog
$agedPng = ''
$agedAt = $null
Copy-Item $sp $spBak -Force
Copy-Item $dlog $dgBak -Force
try {
  $txt = [IO.File]::ReadAllText($sp, [Text.Encoding]::UTF8)
  $ls  = @($txt -split "`r?`n")

  # (a) allowed-diff: a differences row with empty why / source / expiry cells.
  $i1 = -1
  for ($i = 0; $i -lt $ls.Count; $i++) { if ($ls[$i] -match '^\|\s*1\s*\|') { $i1 = $i; break } }
  if ($i1 -lt 0) { Note 'WARN  allowed-diff / bad : no "| 1 |" differences row found' }
  else { $ls = @($ls[0..($i1 - 1)]) + @('| 999 | gx-selftest bogus difference |  |  |  |') + @($ls[$i1..($ls.Count - 1)]) }

  # (b) numeric-log-only: a numeric-class row whose evidence is a screenshot only.
  $i2 = -1
  for ($i = 0; $i -lt $ls.Count; $i++) { if ($ls[$i] -match '^\|\s*B8\s*\|') { $i2 = $i; break } }
  if ($i2 -lt 0) { Note 'WARN  numeric-log-only / bad : no "| B8 |" row found' }
  else {
    $bogus = '| B9 | gx-selftest | ' + $cNum + ' | x | y | ' + [char]0x2705 + ' | ' + [char]0x56FE + ' `gx-selftest-shot.png` |'
    $ls = @($ls[0..$i2]) + @($bogus) + @($ls[($i2 + 1)..($ls.Count - 1)])
  }

  # (c) screenshot-refs: a cited png that does not exist.
  $ls += @('', '<!-- gx-selftest --> gx-selftest-missing-shot.png')
  [IO.File]::WriteAllText($sp, ($ls -join "`n"), (New-Object Text.UTF8Encoding($false)))

  # (d) no-handoff-docs: a NEXT*.md anywhere inside the project.
  [IO.File]::WriteAllText($nextMd, 'gx-selftest', (New-Object Text.UTF8Encoding($false)))

  # (e) verify-entry: hide one companion script of the entry surface.
  Move-Item $envChk $envBak -Force

  # (f) no-sync-subagents (was no-team-sessions; REVERSED 2026-09-22):
  #     the defect is now a dispatch row that names NO team/member (= sync channel).
  [IO.File]::AppendAllText($dlog, ((Get-Date).ToString('yyyy-MM-dd HH:mm') + "`tgx-selftest`tgx-selftest sync-channel probe`t" + $Project + "`r`n"), (New-Object Text.UTF8Encoding($false)))

  # (g) freeze-before-capture: age the oldest png of the newest contact sheet,
  #     so its own row implementation file is newer than the shot.
  $mf = @(Get-ChildItem (Join-Path $Project 'tools\probes') -Filter '*.manifest.tsv' -File | Sort-Object LastWriteTime | Select-Object -Last 1)
  if ($mf.Count -eq 0) { Note 'WARN  freeze-before-capture / bad : no contact-sheet manifest found' }
  else {
    $mn = @([regex]::Matches([IO.File]::ReadAllText($mf[0].FullName, [Text.Encoding]::UTF8), '([0-9A-Za-z_\-\.]+\.png)') | ForEach-Object { $_.Groups[1].Value })
    foreach ($n in $mn) { $cand = Join-Path $shots $n; if (Test-Path $cand) { $agedPng = $cand; break } }
    if ($agedPng -eq '') { Note 'WARN  freeze-before-capture / bad : no manifest png is on disk' }
    else { $agedAt = (Get-Item -LiteralPath $agedPng).LastWriteTime; (Get-Item -LiteralPath $agedPng).LastWriteTime = [datetime]'2000-01-01' }
  }

  # (h) evidence-economy: 60 loose pngs push the count past max(12, visual*2).
  foreach ($p in $loose) { [IO.File]::WriteAllBytes($p, [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00)) }

  $badRun = Invoke-GateTable
  foreach ($n in ($renamed + $added)) { Expect ('injected defect / ' + $n) ([string]$badRun[$n]) 'FAIL' }
} finally {
  Copy-Item $spBak $sp -Force
  Copy-Item $dgBak $dlog -Force
  Remove-Item $spBak, $dgBak -Force -ErrorAction Continue
  Remove-Item $nextMd -Force -ErrorAction Continue
  if (Test-Path $envBak) { Move-Item $envBak $envChk -Force }
  if (($agedPng -ne '') -and ($agedAt -ne $null)) { (Get-Item -LiteralPath $agedPng).LastWriteTime = $agedAt }
  foreach ($p in $loose) { Remove-Item $p -Force -ErrorAction Continue }
}

$back = Invoke-GateTable
foreach ($n in ($renamed + $added)) { Expect ('restored sample / ' + $n) ([string]$back[$n]) 'PASS' }
Note ('  hash self-check: spec ' + ($hSp0 -eq (HashOf $sp)) + ' / dispatch-log ' + ($hDg0 -eq (HashOf $dlog)))
if ($hSp0 -ne (HashOf $sp)) { $bad++; Note 'MISS  the acceptance table was not restored byte-identically' }
if ($hDg0 -ne (HashOf $dlog)) { $bad++; Note 'MISS  the dispatch log was not restored byte-identically' }
if (Test-Path $nextMd) { $bad++; Note 'MISS  .ai-tmp/test/NEXT.md was left behind' }
if (-not (Test-Path $envChk)) { $bad++; Note 'MISS  tools/env-check.ps1 was not restored' }
foreach ($p in $loose) { if (Test-Path $p) { $bad++; Note ('MISS  loose probe png left behind: ' + (Split-Path $p -Leaf)) } }

Note ''
Note ('===== gate-selftest summary: unmet-expectations=' + $bad + ' =====')
exit $(if ($bad -gt 0) { 1 } else { 0 })
