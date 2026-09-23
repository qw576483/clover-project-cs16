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

# ---- window guardrail (slice BW-G, main-agent ruling 2026-09-23) ----------------------
# (slice BW-G-R: the registry injection, the env-check move and the evidence-shot mtimes are
#  gone -- sections 4/6/9 now run on -PlanDir / -EnvCheck / -ShotDir sandboxes. The ledger stays,
#  because a run still rewrites its own sandboxed copies and takes minutes.)
# A full self-test still REWRITES real artifacts for short windows. A concurrent `verify.ps1` inside such a window
# reads a FALSE verdict -- measured: a teammate's run reported FAIL=6 while all six items
# were self-healing and none of them its own. Until every sample is isolated on a copy, the
# window is made VISIBLE: each run appends a start row here and an end row at the bottom,
# so a reader can tell whether its own run overlapped one.
# SEAM RULE (slice BW-A, 2026-09-23 -- measured the hard way): a test seam only works if it
# actually REACHES the judgement script.  `reference-table-refs` invoked scan-reftable-refs.py with
# NO arguments, so the scanner always read the REAL plan dir while the AX/BB samples injected their
# ghost carrier into a -PlanDir sandbox => those two samples were "testing" the real files and
# reporting a pass for a sandbox NOBODY READ.  That is a FALSE TEST PASS (the mirror of a false
# red): the test believed it measured A while it measured B.  When a script-based item gains a
# seam, pass it through AND prove the item SEES it (inject into the sandbox, require the item to go
# red).  A sample that cannot fail is not a sample.
# THE THREE ENUMERABLE FORMS OF SEAM FAILURE (all three MEASURED 2026-09-23 in slice BW-A; main
# agent asked for them to be enumerable rather than a vague "be careful"):
#   1) NOT PASSED AT ALL -- the call site has no seam argument.  `AX`/`BB` (reference-table-refs)
#      injected a ghost carrier into a -PlanDir sandbox while the judgement script was invoked with
#      no arguments, so it always read the REAL plan dir.
#   2) PASSED BUT OVERWRITTEN -- `audit-shot-refs.ps1` gained `param([string]$ShotDir)` and the next
#      line's local `$shotDir = <default>` is THE SAME VARIABLE, because PowerShell variable names
#      are CASE-INSENSITIVE => the local assignment ate the parameter before the override could read
#      it, so `-ShotDir <sandbox>` silently did nothing.  Fix: rename the internal ($ShotsRoot).
#   3) PASSED BUT THE ASSET CANNOT TAKE IT -- `check-acceptance-sums.py` has NO argument interface at
#      all (no sys.argv / argparse), so `-PlanDir` could never reach it.
#   PROOF PATTERN for any of them: put a defect ONLY in the sandbox copy, run the item through the
#   seam, and require it to go RED.  `A 坏 plan + 零引用 png => 两项 FAIL / B 纯净 => PASS /
#   C 真盘 => PASS` is what that looked like when both scripts were fixed.
# SEAM AUDIT (which call sites in verify.ps1 are confirmed to pass a seam):
#   ✓ 705 refScan --plan-dir | ✓ 977 covScan --plan | ✓ 1780 section-ownership | ✓ 1828 verdict-rows
#   (-PlanDir/-ShotDir/-ProbeRoot) | ✓ 1977 gate-sync -Template | ✓ 209 window-ledger -Ledger
#   ✓ 1099 sumsScript --plan-dir (WAS form 1) | ✓ 1316 audit-shot-refs -ShotDir (WAS form 2)
#   - 1742 compile-check takes no plan/shot seam by design (it compiles Assets/Editor)
# DECLARED-SHAPE CONVENTION (main agent approved 2026-09-23, same family as "a criterion must be
# provably red"): the measurement shapes the ledger may emit are enumerated in
# tools/probes/audit-verdict-rows.py (DECLARED_SHAPES).  A NEW shape (e.g. meta_missing=) will make
# item 40 RED until the declared set is updated -- that is deliberate ("a degraded shape must not
# pass like a normal one"), and it means: whoever adds a shape UPDATES DECLARED_SHAPES in the same
# change.  Observability criteria need maintenance too.
# RULE (main-agent ruling 2026-09-23) -- THIS SCRIPT IS SERIALIZED PER MACHINE: run at most one
# gate-selftest.ps1 at a time. Two concurrent runs make BOTH read false reds, because each one's
# samples perturb the other's real artifacts. Measured twice on 2026-09-23: (1) a run reported
# `MISS reference-table-refs / good` only because another run's window covered the same
# minutes; (2) a run counted 6 unmet expectations, of which 1 was a plan re-injection that a
# third party had been allowed to perform INSIDE the window (that party later self-attributed it
# in the ledger). Before launching: read .ai-tmp/test/gate-selftest-window.tsv and the heartbeats
# and confirm there is no unclosed `start` (a `start` is closed by `end` OR `abort`).
$winLog = Join-Path $Project '.ai-tmp\test\gate-selftest-window.tsv'
if (-not (Test-Path $winLog)) {
  [IO.File]::WriteAllText($winLog, ("# gate-selftest window ledger (ISO time`tstage`tpid`tresult)" + "`r`n" +
    '# while a run is OPEN (a `start` with no matching `end`/`abort`), do NOT trust a concurrent verify.ps1 reading' + "`r`n" +
    '# USAGE: (1) read this file BEFORE launching any run -- gate-selftest.ps1 is SERIALIZED per machine' + "`r`n" +
    '# (two concurrent runs perturb each other and BOTH read false reds; measured twice 2026-09-23);' + "`r`n" +
    '# (2) a `start` is closed by `end` OR `abort`; (3) pairing is by PID when present, else in order;' + "`r`n" +
    '# (4) never rewrite this file, never delete a row -- append only (a slice lost its evidence that way).' + "`r`n"), (New-Object Text.UTF8Encoding($false)))
}
[IO.File]::AppendAllText($winLog, ((Get-Date).ToString('s') + "`tstart`tPID=" + $PID + "`r`n"), (New-Object Text.UTF8Encoding($false)))
Write-Output ('WINDOW  gate-selftest START ' + (Get-Date).ToString('s') + ' PID=' + $PID + ' -- this run rewrites real artifacts while it runs: do NOT trust a concurrent verify.ps1 reading until the matching END row')

# ---- residue self-clean BEFORE the run (main-agent ruling 2026-09-23) --------------------------
# Root cause (found by slice bw-gate, adopted by the main agent): a run that is KILLED never
# reaches its own `finally`, so "assert at the end" cannot protect the NEXT reader.  Measured on
# this project: one killed self-test left `.ai-tmp/test/NEXT.md` plus 60 `gx-selftest-loose-*.png`
# in `.ai-tmp/screenshots/` (which had grown to 244 files) and dragged `shot-refs-audited` and
# `evidence-economy` red FOR THE WHOLE TEAM.  Cleaning BEFORE the run is the only place that
# helps: it also clears a previous run's residue that never got a chance to clean up after itself.
$shotsDir = Join-Path $Project '.ai-tmp\screenshots'
$nextMd   = Join-Path $Project '.ai-tmp\test\NEXT.md'
$pxBefore = @(Get-ChildItem $shotsDir -File -ErrorAction SilentlyContinue).Count
Get-ChildItem $shotsDir -File -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -like 'gx-selftest-*' -or $_.Name -like 'ahrselftest*' } |
  ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction Continue }
# Test-Path guard: Remove-Item on a path that does not exist still writes a non-terminating
# error to stderr (measured in the 09:26 exclusive run: stderr contained a PathNotFound line).
# stderr is EVIDENCE (a crashed run's stderr is the most valuable artifact we have), so it must
# not be filled with expected noise that trains the next reader to ignore it.
if (Test-Path $nextMd) { Remove-Item $nextMd -Force -ErrorAction Continue }
$px0 = @(Get-ChildItem $shotsDir -File -ErrorAction SilentlyContinue).Count
Write-Output ('WINDOW  pre-run residue clean: probe png removed = ' + ($pxBefore - $px0) + ' ; NEXT.md removed = ' + (-not (Test-Path $nextMd)) + ' ; screenshots inventory = ' + $px0)

$report = New-Object System.Collections.Generic.List[string]
$bad = 0
function Note([string]$s) { $report.Add($s); Write-Output $s }
function HashOf([string]$p) { if (Test-Path $p) { return (Get-FileHash -Algorithm SHA256 $p).Hash } else { return '(missing)' } }
function WriteText([string]$p, [string]$body) { [IO.File]::WriteAllText($p, $body, (New-Object Text.UTF8Encoding($false))) }

# Run the project gate and return the verdict letter of one named check.
function Invoke-Gate([string]$gateName, [string[]]$extra = @()) {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate @extra 2>&1 | ForEach-Object { [string]$_ })
  $rx = '^(PASS|FAIL|HUMAN-ONLY)\s+' + [regex]::Escape($gateName) + '(\s|$)'
  $line = @($out | Where-Object { $_ -match $rx } | Select-Object -First 1)
  if ($line.Count -eq 0) { return 'NO-VERDICT' }
  if ($line[0] -match '^PASS') { return 'PASS' }
  if ($line[0] -match '^FAIL') { return 'FAIL' }
  return 'HUMAN-ONLY'
}
# Like Invoke-GateWithLedger, but returns the FULL output lines instead of only the verdict.
# WHY (slice BW-A, 2026-09-23, assigned to fix this sample): the evidence-freshness samples must
# register exactly the (row,shot) pairs the SANDBOX reports as void, and the sandbox set is NOT the
# live set -- forcing one shot's mtime to 2000 voids EVERY acceptance row that cites that shot.
# Measured: 9 sandbox pairs (H1..H9, all citing 22_slot2_pistol.png) vs 1 live pair (G17) => the
# sample registered G17 and asked for HUMAN-ONLY while 8 unregistered pairs correctly FAILed.
function Invoke-GateWithLedgerOut([string]$ledgerPath, [string]$gateName, [string]$planDir = '', [string]$shotDir = '') {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $extra = @('-Ledger', $ledgerPath)
  if ($planDir -ne '') { $extra += @('-PlanDir', $planDir) }
  if ($shotDir -ne '') { $extra += @('-ShotDir', $shotDir) }
  return @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate @extra 2>&1 | ForEach-Object { [string]$_ })
}
# Like Invoke-Gate, but feeds item 25 a COPY of the node-tree dump through the -NodeDump
# test seam, so the self-test never writes the real dump (slice BW-G; see section 1).
function Invoke-GateWithDump([string]$dumpPath, [string]$gateName) {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate -NodeDump $dumpPath 2>&1 | ForEach-Object { [string]$_ })
  $rx = '^(PASS|FAIL|HUMAN-ONLY)\s+' + [regex]::Escape($gateName) + '(\s|$)'
  $line = @($out | Where-Object { $_ -match $rx } | Select-Object -First 1)
  if ($line.Count -eq 0) { return 'NO-VERDICT' }
  if ($line[0] -match '^PASS') { return 'PASS' }
  if ($line[0] -match '^FAIL') { return 'FAIL' }
  return 'HUMAN-ONLY'
}
# Like Invoke-Gate, but points the LEDGER-backed items at a sample ledger through the
# -Ledger test seam, so the samples never rewrite the SHARED .ai-tmp/test/dispatch-log.tsv
# (slice BW-G: the ledger samples used to append to the real ledger and restore a whole-file
# backup afterwards -- any row another slice appends inside that window is silently
# truncated, and the self-test's own hash check cannot see it because it compares against
# the backup it just restored. A gate must never destroy another slice's evidence.)
function Invoke-GateWithLedger([string]$ledgerPath, [string]$gateName, [string]$planDir = '', [string]$shotDir = '') {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $ga = @('-Ledger', $ledgerPath)
  if ($planDir -ne '') { $ga += @('-PlanDir', $planDir) }
  if ($shotDir -ne '') { $ga += @('-ShotDir', $shotDir) }
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate @ga 2>&1 | ForEach-Object { [string]$_ })
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

# ---- static self-check: in THIS file, every function must be DEFINED BEFORE its first call ----
# WHY (measured 2026-09-23, main-agent diagnosis, then reproduced in .ai-tmp/test/hoist-probe.ps1):
# PowerShell registers a script's functions in EXECUTION order -- `Call-Me` one line above
# `function Call-Me {...}` throws CommandNotFoundException.  `Invoke-GateWithPlan` was defined at
# L494 but called at L455/463/471, so those three expectations CRASHED instead of returning a
# verdict, and a crashed `Expect` is silently NOT counted by the summary => the self-test
# UNDER-reported (unmet=6 where at least 3 more expectations had produced no verdict at all).
# A self-test that can silently skip an expectation is worse than no self-test, so the ordering
# rule is now checked by the file itself, before the first sample runs.  Comment lines are
# ignored so that a prose mention of a function name is not mistaken for a call.
$fnOrderBad = @()
$selfLines  = @([IO.File]::ReadAllLines($MyInvocation.MyCommand.Path, [Text.Encoding]::UTF8))
$fnDefLine  = @{}
for ($i = 0; $i -lt $selfLines.Count; $i++) {
  if ($selfLines[$i] -match '^function\s+([A-Za-z_][A-Za-z0-9_-]*)') { $fnDefLine[$Matches[1]] = $i }
}
foreach ($fn in @($fnDefLine.Keys)) {
  $rx = [regex]::Escape($fn) + '(?![\w-])'
  for ($i = 0; $i -lt $fnDefLine[$fn]; $i++) {
    if (($selfLines[$i].TrimStart() -notlike '#*') -and ($selfLines[$i] -match $rx)) {
      $fnOrderBad += ($fn + ' called L' + ($i + 1) + ' but defined L' + ($fnDefLine[$fn] + 1)); break
    }
  }
}
Note ('  static order check: ' + @($fnDefLine.Keys).Count + ' function(s) in this file; defined-after-first-call violations = ' + @($fnOrderBad).Count)
if (@($fnOrderBad).Count -gt 0) { $bad++; Note ('MISS  function defined after its first call (PowerShell will throw, and a crashed Expect is NOT counted): ' + ($fnOrderBad -join ' ; ')) }

Note '=== gate-selftest (two-sample rule: good sample PASSES, injected defect FAILS) ==='

# ---------------------------------------------------------------- 1) home-credit-rendered
# REWRITTEN (slice BW-G): the injections target a COPY fed through the -NodeDump test seam.
# The old shape injected the REAL `tools/probes/home-screen-nodetree.txt` in place and
# restored it afterwards, so every concurrent `verify.ps1` on this machine read the injected
# state -- measured pollution: `bv-r`'s own verify output reported
# `FAIL home-credit-rendered ... has no lowercase glyph` while its real Play capture showed
# lowercase `by clover-engine`. A gate must never manufacture false evidence for another
# slice. The real dump must stay BYTE-IDENTICAL for the whole self-test: asserted by a hash
# self-check AND by a plain (no-seam) run taken inside the injection window, which must still
# read the pristine dump => PASS.
$dump = Join-Path $Project 'tools\probes\home-screen-nodetree.txt'
$h0 = HashOf $dump
$dumpCopy = Join-Path $Project ('.ai-tmp\test\bwgselftest-nodetree-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
Copy-Item $dump $dumpCopy -Force
try {
  Expect 'home-credit-rendered / good (pristine COPY via the seam)' (Invoke-GateWithDump $dumpCopy 'home-credit-rendered') 'PASS'

  # injected defect: the font cannot draw lowercase -> the line would render ALL-CAPS.
  $t   = [IO.File]::ReadAllText($dumpCopy, [Text.Encoding]::UTF8)
  $n1  = $t.Replace('hasa=True', 'hasa=False')
  if ($n1 -eq $t) { Note 'WARN  home-credit-rendered / bad-font : the hasa column was not found in the dump' }
  WriteText $dumpCopy $n1
  Expect 'home-credit-rendered / bad-font (hasa=False) on the COPY' (Invoke-GateWithDump $dumpCopy 'home-credit-rendered') 'FAIL'

  # CONCURRENCY PROOF: with the injected copy on disk, a PLAIN run must still read the real
  # dump (i.e. no other slice can see the injection).
  Expect 'home-credit-rendered / concurrent plain run reads the REAL dump' (Invoke-Gate 'home-credit-rendered') 'PASS'
  $hMid = HashOf $dump
  Note ('  real dump hash INSIDE the injection window = ' + $hMid + ' (must equal ' + $h0 + ')')
  if ($hMid -ne $h0) { $bad++; Note 'MISS  the REAL node-tree dump changed during the injection window' }

  # injected defect: wrong case in the rendered literal.
  $needle = "text='by clover-engine'"
  $seen = ([regex]::Matches($t, [regex]::Escape($needle))).Count
  Note ('  bad-case sample: credit-literal occurrence(s) in the dump = ' + $seen)
  $n2 = $t.Replace($needle, "text='By Clover-Engine'")
  WriteText $dumpCopy $n2
  Expect 'home-credit-rendered / bad-case (By Clover-Engine) on the COPY' (Invoke-GateWithDump $dumpCopy 'home-credit-rendered') 'FAIL'
} finally {
  Remove-Item $dumpCopy -Force -ErrorAction Continue
}
Expect 'home-credit-rendered / restored (real dump, no seam)' (Invoke-Gate 'home-credit-rendered') 'PASS'
Note ('  real dump hash self-check: before=' + $h0 + ' after=' + (HashOf $dump) + ' identical=' + ($h0 -eq (HashOf $dump)))
if ($h0 -ne (HashOf $dump)) { $bad++; Note 'MISS  the REAL node-tree dump was not left byte-identical' }
if (Test-Path $dumpCopy) { $bad++; Note 'MISS  the node-tree copy was left behind' }

# ---------------------------------------------------- 2) differences-source-of-truth
$plan = Join-Path $Project ([char[]]@(0x7B56,0x5212) -join '')
$reg  = Join-Path $plan ((([char[]]@(0x5DEE,0x5F02,0x767B,0x8BB0)) -join '') + '.tsv')
$rbak = Join-Path $Project '.ai-tmp\test\ah-r-selftest-registry.bak'
if (-not (Test-Path $reg)) {
  Note ('FAIL  differences-source-of-truth : registry not found at ' + $reg)
  $bad++
} else {
  Expect 'differences-source-of-truth / good (pristine registry)' (Invoke-Gate 'differences-source-of-truth') 'PASS'
  # injected defect: a registry row whose id exists on NO section row.
  [IO.File]::AppendAllText($reg, ("999`tAH-R selftest bogus row`t`t`t`r`n"), (New-Object Text.UTF8Encoding($false)))
  Expect 'differences-source-of-truth / bad (orphan id 999)' (Invoke-Gate 'differences-source-of-truth') 'FAIL'
  # APPEND-SAFE UNDO (slice BW-G, 2026-09-23): remove exactly the line this section added,
  # instead of restoring a whole-file backup. The registry is SHARED -- another slice was
  # appending rows #79/#80 while this ran -- and a whole-file restore silently DISCARDS a
  # concurrent append, while a before/after whole-file hash check can only report a
  # meaningless red for it. A gate must never write, let alone lose, another slice's work.
  # Byte-preserving: remove exactly that line INCLUDING its own end-of-line and leave every
  # other byte alone. (Re-joining the lines normalises the whole file's line endings, which
  # shows up as a meaningless whole-file diff in `git diff` -- measured 2026-09-23.)
  $raw999 = [IO.File]::ReadAllText($reg, [Text.Encoding]::UTF8)
  $new999 = [regex]::Replace($raw999, '(?m)^999\tAH-R selftest bogus row[^\r\n]*\r?\n?', '')
  [IO.File]::WriteAllText($reg, $new999, (New-Object Text.UTF8Encoding($false)))
  Expect 'differences-source-of-truth / restored (only the injected row removed)' (Invoke-Gate 'differences-source-of-truth') 'PASS'
  $resid999 = @([IO.File]::ReadAllLines($reg, [Text.Encoding]::UTF8) | Where-Object { $_ -match '^999\tAH-R selftest bogus row' }).Count
  Note ('  residue self-check: injected registry row(s) left behind = ' + $resid999 + ' (the undo is line-scoped, never a whole-file restore)')
  if ($resid999 -ne 0) { $bad++; Note 'MISS  the injected registry row was left behind' }
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
  param([string]$ledger = '', [string]$plan = '', [string]$envCheck = '', [string]$shotDir = '')
  $gate = Join-Path $Project 'tools\verify.ps1'
  $extra = @()
  if ($ledger -ne '') { $extra += @('-Ledger', $ledger) }
  if ($plan -ne '') { $extra += @('-PlanDir', $plan) }
  if ($envCheck -ne '') { $extra += @('-EnvCheck', $envCheck) }
  if ($shotDir -ne '') { $extra += @('-ShotDir', $shotDir) }
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate @extra 2>&1 | ForEach-Object { [string]$_ })
  $tbl = @{}
  foreach ($ln in $out) {
    $m = [regex]::Match($ln, '^(PASS|FAIL|HUMAN-ONLY)\s+([a-z0-9\-]+)(\s|$)')
    if ($m.Success) { $tbl[$m.Groups[2].Value] = $m.Groups[1].Value }
  }
  return $tbl
}
function Invoke-GateWithPlan([string]$planDir, [string]$gateName) {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate -PlanDir $planDir 2>&1 | ForEach-Object { [string]$_ })
  $rx = '^(PASS|FAIL|HUMAN-ONLY)\s+' + [regex]::Escape($gateName) + '(\s|$)'
  $line = @($out | Where-Object { $_ -match $rx } | Select-Object -First 1)
  if ($line.Count -eq 0) { return 'NO-VERDICT' }
  if ($line[0] -match '^PASS') { return 'PASS' }
  if ($line[0] -match '^FAIL') { return 'FAIL' }
  return 'HUMAN-ONLY'
}

$plan   = Join-Path $Project ([char[]]@(0x7B56,0x5212) -join '')                              # ce hua
$sp     = Join-Path $plan ((([char[]]@(0x9A8C,0x6536,0x8868)) -join '') + '.md')              # yan shou biao
# slice BW-G: this section INJECTS defects into the acceptance table, so it must work on a
# SANDBOX copy of the whole plan dir and feed every run through verify.ps1's -PlanDir seam.
# The real plan dir is shared with every other slice -- measured 2026-09-23 while another
# slice was appending registry rows #79/#80: the in-place backup/restore produced
# "MISS the acceptance table was not restored byte-identically" AND could discard the
# concurrent edit (the restore overwrites the whole file). A gate must never write, let
# alone lose, another slice's deliverable.
$planCopy  = Join-Path $Project ('.ai-tmp\test\bwggx-plan-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $planCopy | Out-Null
Copy-Item (Join-Path $plan '*') $planCopy -Force
$spReal    = $sp
$sp        = Join-Path $planCopy (Split-Path $sp -Leaf)
$hSpReal0  = HashOf $spReal
$cNum   = ([char[]]@(0x6570,0x503C,0x7C7B) -join '')                                          # numeric class
$cVis   = ([char[]]@(0x8868,0x73B0,0x7C7B) -join '')                                          # visual class
$shots  = Join-Path $Project '.ai-tmp\screenshots'
$dlog   = Join-Path $Project '.ai-tmp\test\dispatch-log.tsv'
$nextMd = Join-Path $Project '.ai-tmp\test\NEXT.md'
$envChk = Join-Path $Project 'tools\env-check.ps1'
# slice BW-G-R: the entry-surface sample no longer MOVES the real companion script (that writes a
# shared deliverable and a crash in between leaves the project without it).  It points the gate
# at a path that does not exist, through verify.ps1's -EnvCheck seam -- same branch, zero writes.
$envChkMissing = Join-Path $Project ('.ai-tmp\test\gx-selftest-envcheck-missing-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.ps1')
$hEnv0 = HashOf $envChk
$spBak  = $sp + '.gx-selftest.bak'
# The ledger-backed samples of this section run on a COPY of the fixture ledger through
# verify.ps1's -Ledger test seam (slice BW-G): the real .ai-tmp/test/dispatch-log.tsv is
# SHARED with every other slice, so appending to it and then restoring a whole-file backup
# can silently truncate a row another slice appends inside the window -- and the section's
# own hash check cannot see that (it compares against the backup it just restored).
# This section therefore NEVER writes the real ledger; the copy is deleted at the end.
$lgFix  = Join-Path $Project 'tools\probes\ledger-selftest\ledger.tsv'
$lgCopy = Join-Path $Project ('.ai-tmp\test\bwgselftest-tableledger-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.tsv')
$loose  = @()
1..60 | ForEach-Object { $loose += (Join-Path $shots ('gx-selftest-loose-' + $_ + '.png')) }

$renamed = @('allowed-diff', 'screenshot-refs', 'verify-entry', 'no-handoff-docs')
$added   = @('numeric-log-only', 'no-sync-subagents', 'freeze-before-capture', 'evidence-economy')
$oldNames = @('differences-registry', 'refs-reachable', 'gate-present', 'no-handover-docs', 'handoff-doc-found')

Note ''
Note '--- slice AI: renamed + added items ---'
Copy-Item $lgFix $lgCopy -Force
$good = Invoke-GateTable -ledger $lgCopy -plan $planCopy
foreach ($n in ($renamed + $added)) { Expect ('good sample / ' + $n) ([string]$good[$n]) 'PASS' }
foreach ($o in $oldNames) { Expect ('renamed away (must be gone) / ' + $o) ([string]$good[$o]) '' }

# --- one batch injection, then one gate run --------------------------------
$hSp0 = HashOf $sp
$hDg0 = HashOf $dlog
$agedPng = ''
$agedAt = $null
Copy-Item $sp $spBak -Force
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

  # (e) verify-entry: hide one companion script of the entry surface -- through the -EnvCheck
  #     seam, never by moving the real file (slice BW-G-R; see the note where it is defined).

  # (f) no-sync-subagents (was no-team-sessions; REVERSED 2026-09-22):
  #     the defect is now a dispatch row that names NO team/member (= sync channel).
  #     Injected into the ledger COPY, never into the shared real ledger (slice BW-G).
  [IO.File]::AppendAllText($lgCopy, ((Get-Date).ToString('yyyy-MM-dd HH:mm') + "`tgx-selftest`tgx-selftest sync-channel probe`t" + $Project + "`r`n"), (New-Object Text.UTF8Encoding($false)))

  # (g) freeze-before-capture: age a shot OF THE NEWEST MANIFEST so it is older than its OWN
  #     row implementation file.  WHICH shot matters: the item only compares (batch row,
  #     batch shot) pairs, i.e. a shot creates a void pair only when an acceptance row CITES
  #     it.  Measured 2026-09-23: the newest manifest is written by whichever slice captured
  #     last and most of its entries are diagnostic frames no row cites -- ageing the first
  #     png of the file left the item PASS (the injected defect was never exercised).
  #     So the candidates are ordered cited-first, and the ageing below is RETRIED on the
  #     next candidate when the item does not turn red (bounded by the candidate count).
  $mf = @(Get-ChildItem (Join-Path $Project 'tools\probes') -Filter '*.manifest.tsv' -File | Sort-Object LastWriteTime | Select-Object -Last 1)
  $fzCands = @()
  if ($mf.Count -eq 0) { Note 'WARN  freeze-before-capture / bad : no contact-sheet manifest found' }
  else {
    $mn = @([regex]::Matches([IO.File]::ReadAllText($mf[0].FullName, [Text.Encoding]::UTF8), '([0-9A-Za-z_\-\.]+\.png)') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $pristineSp = ''
    if (Test-Path $spBak) { $pristineSp = [IO.File]::ReadAllText($spBak, [Text.Encoding]::UTF8) }
    foreach ($n in $mn) {
      $cand = Join-Path $shots $n
      if (-not (Test-Path $cand)) { continue }
      if (($pristineSp -ne '') -and ($pristineSp.Contains($n))) { $fzCands += $cand }
    }
    foreach ($n in $mn) {
      $cand = Join-Path $shots $n
      if (((Test-Path $cand)) -and ($fzCands -notcontains $cand)) { $fzCands += $cand }
    }
    Note ('  freeze-before-capture / bad : ' + $fzCands.Count + ' candidate shot(s) from ' + $mf[0].Name + ' (cited ones first)')
    if ($fzCands.Count -eq 0) { Note 'WARN  freeze-before-capture / bad : no manifest png is on disk' }
  }

  # (h) evidence-economy: 60 loose pngs push the count past max(12, visual*2).
  foreach ($p in $loose) { [IO.File]::WriteAllBytes($p, [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00)) }

  $badRun = Invoke-GateTable -ledger $lgCopy -plan $planCopy -envCheck $envChkMissing
  # (g) retry: age the next candidate until freeze-before-capture actually turns red (a
  # shot no row cites can never produce a void pair, so the first pick may be inert).
  $fzi = 0
  while (($badRun['freeze-before-capture'] -ne 'FAIL') -and ($fzi -lt $fzCands.Count)) {
    if ($agedPng -ne '') { (Get-Item -LiteralPath $agedPng).LastWriteTime = $agedAt }
    $agedPng = $fzCands[$fzi]
    $agedAt = (Get-Item -LiteralPath $agedPng).LastWriteTime
    (Get-Item -LiteralPath $agedPng).LastWriteTime = [datetime]'2000-01-01'
    $fzi++
    Note ('  freeze-before-capture / bad : ageing candidate ' + $fzi + ' of ' + $fzCands.Count + ' -> ' + (Split-Path $agedPng -Leaf))
    $badRun = Invoke-GateTable -ledger $lgCopy -plan $planCopy -envCheck $envChkMissing
  }
  foreach ($n in ($renamed + $added)) { Expect ('injected defect / ' + $n) ([string]$badRun[$n]) 'FAIL' }
} finally {
  Copy-Item $spBak $sp -Force
  Remove-Item $spBak -Force -ErrorAction Continue
  Remove-Item $nextMd -Force -ErrorAction Continue
  if (Test-Path $envChkMissing) { Remove-Item $envChkMissing -Force -ErrorAction Continue }
  if (($agedPng -ne '') -and ($agedAt -ne $null)) { (Get-Item -LiteralPath $agedPng).LastWriteTime = $agedAt }
  foreach ($p in $loose) { Remove-Item $p -Force -ErrorAction Continue }
}

Copy-Item $lgFix $lgCopy -Force
$back = Invoke-GateTable -ledger $lgCopy -plan $planCopy
foreach ($n in ($renamed + $added)) { Expect ('restored sample / ' + $n) ([string]$back[$n]) 'PASS' }
# hash the sandbox BEFORE deleting it: HashOf returns '(missing)' for a deleted path, so
# comparing after the Remove-Item can only ever report a red that means nothing (measured
# 2026-09-23: the first version of this assertion did exactly that).
$hSpAfter = HashOf $sp
Remove-Item $lgCopy -Force -ErrorAction Continue
Remove-Item $planCopy -Recurse -Force -ErrorAction Continue
Note ('  hash self-check: plan-sandbox restored=' + ($hSp0 -eq $hSpAfter) + ' / REAL plan untouched=' + ($hSpReal0 -eq (HashOf $spReal)) + ' / REAL dispatch-log untouched=' + ($hDg0 -eq (HashOf $dlog)))
if ($hSp0 -ne $hSpAfter) { $bad++; Note 'MISS  the plan sandbox was not restored byte-identically' }
if ($hSpReal0 -ne (HashOf $spReal)) { $bad++; Note 'MISS  the REAL acceptance table was written by the self-test (this section must inject into the -PlanDir sandbox)' }
if ($hDg0 -ne (HashOf $dlog)) { $bad++; Note 'MISS  the REAL dispatch ledger was written by the self-test (the samples must use the -Ledger copy)' }
if (Test-Path $lgCopy) { $bad++; Note 'MISS  the sample ledger copy was left behind' }
if (Test-Path $planCopy) { $bad++; Note 'MISS  the plan sandbox was left behind' }
if (Test-Path $nextMd) { $bad++; Note 'MISS  .ai-tmp/test/NEXT.md was left behind' }
if (-not (Test-Path $envChk)) { $bad++; Note 'MISS  the REAL tools/env-check.ps1 is gone' }
if ((HashOf $envChk) -ne $hEnv0) { $bad++; Note 'MISS  the REAL tools/env-check.ps1 was written by the self-test (it must only ever be READ)' }
foreach ($p in $loose) { if (Test-Path $p) { $bad++; Note ('MISS  loose probe png left behind: ' + (Split-Path $p -Leaf)) } }

# =====================================================================
#  5) slice AX: reference-table-refs (verify.ps1 item 8b)
#     Two samples, per SKILL 8.3: (1) the pristine reference table PASSES,
#     (2) an INJECTED dangling carrier FAILS,
#     (3) a REGISTERED carrier made reachable again (stale silencer) FAILS,
#     (4) restored => PASS again, with a hash self-check.
#     The injected carrier name is assembled at RUNTIME: a literal here would sit in
#     this script's own text, and a future grep-style check would count this file as a
#     citation of it -- the defect would then heal itself (the trap verify.ps1 item 26
#     documents).
# =====================================================================
Note ''
Note '--- slice AX: reference-table-refs (reachable OR registered) ---'
$refTbl = Join-Path $plan ((([char[]]@(0x5BF9, 0x7167, 0x8868)) -join '') + '.md')
$cYbzy = ([char[]]@(0x539F, 0x7248, 0x8D44, 0x6E90) -join '')
$regTsv = Join-Path $plan ((([char[]]@(0x8F7D, 0x4F53, 0x53EF, 0x8FBE, 0x6027, 0x767B, 0x8BB0)) -join '') + '.tsv')
$tblBak = Join-Path $Project '.ai-tmp\test\ax-selftest-reftable.bak'
$ghostCarrier = $cYbzy + '/cs16src/ax-selftest-' + [guid]::NewGuid().ToString('N').Substring(0, 10) + '.md'
$regProbe = Join-Path $Project ($cYbzy + '\cs16src\cs16_anim.py')   # a registered (unavailable) carrier
if (-not (Test-Path $refTbl)) {
  Note ('FAIL  reference-table-refs : reference table not found at ' + $refTbl)
  $bad++
} else {
  Copy-Item $refTbl $tblBak -Force
  $hT0 = HashOf $refTbl
  Expect 'reference-table-refs / good (pristine table)' (Invoke-Gate 'reference-table-refs') 'PASS'

  # (a) injected defect: one carrier path that resolves to nothing and has no row.
  $t = [IO.File]::ReadAllText($refTbl, [Text.Encoding]::UTF8)
  [IO.File]::WriteAllText($refTbl, $t + "`r`n<!-- ax-selftest --> " + $ghostCarrier + "`r`n",
                          (New-Object Text.UTF8Encoding($false)))
  Note ('  injected ghost carrier: ' + $ghostCarrier)
  Expect 'reference-table-refs / bad (dangling carrier, no row)' (Invoke-Gate 'reference-table-refs') 'FAIL'
  Copy-Item $tblBak $refTbl -Force

  # (b) injected defect: a registered-but-unavailable carrier is now ON DISK =>
  #     the registry row is a stale silencer and must be reported.
  if (Test-Path $regProbe) {
    Note ('WARN  reference-table-refs / stale : ' + $regProbe + ' already exists -- sample skipped')
  } elseif (-not (Test-Path $regTsv)) {
    Note ('WARN  reference-table-refs / stale : registry not found at ' + $regTsv)
  } else {
    [IO.File]::WriteAllText($regProbe, 'ax-selftest', (New-Object Text.UTF8Encoding($false)))
    Expect 'reference-table-refs / bad (registered carrier reachable again)' (Invoke-Gate 'reference-table-refs') 'FAIL'
    Remove-Item $regProbe -Force -ErrorAction Continue
  }

  Expect 'reference-table-refs / restored' (Invoke-Gate 'reference-table-refs') 'PASS'
  Note ('  hash self-check: before=' + $hT0 + ' after=' + (HashOf $refTbl) + ' identical=' + ($hT0 -eq (HashOf $refTbl)))
  if ($hT0 -ne (HashOf $refTbl)) { $bad++; Note 'MISS  the reference table was not restored byte-identically' }
  if ((Test-Path $regProbe)) { $bad++; Note 'MISS  the ax-selftest carrier probe file was left behind' }
  Remove-Item $tblBak -Force -ErrorAction Continue
}

# =====================================================================
#  6) slice BB: reference-table-refs now ALSO scans the differences registry
#     (ce hua/cha yi deng ji.tsv) -- verify.ps1 item 8b widened, never loosened.
#     Same two-sample rule as section 5: the pristine registry PASSES, an
#     injected dangling citation INSIDE it FAILS, restore => PASS again.
#     Why this sample is not redundant with section 5: section 5 proves the
#     reference table is judged; this one proves the NEWLY SCANNED table is
#     judged.  Without it, "widened the scope" would be an unverified claim.
#     The ghost carrier name is assembled at RUNTIME for the reason item 26
#     documents: a literal would make this very script a citation of it.
# =====================================================================
Note ''
Note '--- slice BB: reference-table-refs also scans the differences registry ---'
# slice BW-G-R: this sample used to append a ghost carrier to the REAL differences registry
# and restore it from a whole-file backup.  That registry is SHARED state (the generator rewrites
# it from its DIF source), so a concurrent append could be truncated -- and the section's own hash
# check could not see it, because it compared against the backup it had just restored.  The ghost
# now goes into a SANDBOX copy of the plan dir fed through -PlanDir, and the REAL registry is
# asserted unchanged (hash AND line count) at the end of the sample.
$planCopy6 = Join-Path $Project ('.ai-tmp\test\bwggx-planreg-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $planCopy6 | Out-Null
Copy-Item (Join-Path $plan '*') $planCopy6 -Force
$diffRegReal = Join-Path $plan ((([char[]]@(0x5DEE, 0x5F02, 0x767B, 0x8BB0)) -join '') + '.tsv')
$diffReg = Join-Path $planCopy6 ((([char[]]@(0x5DEE, 0x5F02, 0x767B, 0x8BB0)) -join '') + '.tsv')
$hRegReal0 = HashOf $diffRegReal
$nRegReal0 = @([IO.File]::ReadAllLines($diffRegReal, [Text.Encoding]::UTF8)).Count
if (-not (Test-Path $diffReg)) {
  Note ('FAIL  reference-table-refs / diff-registry : differences registry not found at ' + $diffReg)
  $bad++
} else {
  $hD0 = HashOf $diffReg
  $regLen0 = (Get-Item -LiteralPath $diffReg).Length
  Expect 'reference-table-refs / good (pristine differences registry)' (Invoke-GateWithPlan $planCopy6 'reference-table-refs') 'PASS'

  # injected defect: one carrier path inside the differences registry that resolves
  # to nothing and has no registry row.
  $ghost2 = $cYbzy + '/cs16src/bb-selftest-' + [guid]::NewGuid().ToString('N').Substring(0, 10) + '.md'
  Note ('  injected ghost carrier (in the differences registry): ' + $ghost2)
  [IO.File]::AppendAllText($diffReg, ("`r`n# bb-selftest " + $ghost2 + "`r`n"),
                           (New-Object Text.UTF8Encoding($false)))
  Expect 'reference-table-refs / bad (dangling citation inside the differences registry)' (Invoke-GateWithPlan $planCopy6 'reference-table-refs') 'FAIL'

  # undo = TRUNCATE back to the exact pre-append length: append-safe and byte-exact by
  # construction (a read-join-rewrite could not reproduce the byte layout -- measured twice,
  # with CRLF and with LF, so the ending is not the thing to guess at).  Still inside the sandbox.
  $regFs = [IO.File]::Open($diffReg, [IO.FileMode]::Open, [IO.FileAccess]::Write)
  $regFs.SetLength($regLen0)
  $regFs.Close()
  Expect 'reference-table-refs / restored (differences registry)' (Invoke-GateWithPlan $planCopy6 'reference-table-refs') 'PASS'
  Note ('  sandbox hash self-check: before=' + $hD0 + ' after=' + (HashOf $diffReg) + ' identical=' + ($hD0 -eq (HashOf $diffReg)))
  if ($hD0 -ne (HashOf $diffReg)) { $bad++; Note 'MISS  the differences-registry sandbox was not restored byte-identically' }
  $hRegReal1 = HashOf $diffRegReal
  $nRegReal1 = @([IO.File]::ReadAllLines($diffRegReal, [Text.Encoding]::UTF8)).Count
  Note ('  REAL differences registry self-check: hash identical=' + ($hRegReal0 -eq $hRegReal1) + ' / lines before=' + $nRegReal0 + ' after=' + $nRegReal1)
  if ($hRegReal0 -ne $hRegReal1) { $bad++; Note 'MISS  the REAL differences registry was written by the self-test' }
  if ($nRegReal0 -ne $nRegReal1) { $bad++; Note 'MISS  the REAL differences registry changed its line count' }
  Remove-Item $planCopy6 -Recurse -Force -ErrorAction Continue
}

# =====================================================================
#  7) slice BW-G: coverage-rows now judges the COVERAGE RELATION
#     (every listed entity has >= 1 verdict row; every verdict row points at a listed
#     entity), NOT row-count equality.  Two samples, per SKILL 8.3 / anti-gaming 5:
#       * tools/probes/coverage-selftest/good   (15 entities == 15 verdict rows)   => PASS
#       * tools/probes/coverage-selftest/bad    (ONE entity name drifted)          => FAIL
#     The gate is fed the fixture through its -PlanDir test seam, which ANNOUNCES itself
#     in the run's first line, so an overridden run can never pass as a real verdict.
#     Part (c) is the anti-drift half: the gate item must agree with the judgement asset
#     (audit-coverage-reconcile.py) on the REAL plan dir -- that disagreement is exactly
#     what made the old row-count item a false GREEN (PASS while 350 names had drifted).
# =====================================================================
function Invoke-Py([string]$scriptPath, [string[]]$extraArgs) {
  $a = @($scriptPath) + $extraArgs
  $out = @(& python $a 2>&1 | ForEach-Object { [string]$_ })
  return @{ Rc = $LASTEXITCODE; Out = $out }
}
function NamesOf($res) {
  $n = @($res.Out | Where-Object { $_ -match '^entity names: ' } | Select-Object -Last 1)
  if ($n.Count -eq 0) { return '' }
  return [string]$n[0]
}
function NumOf([string]$line, [string]$key) {
  return [string]([regex]::Match($line, $key + '=(\d+)').Groups[1].Value)
}

Note ''
Note '--- slice BW-G: coverage-rows = the coverage relation, never row-count equality ---'
$fixRoot   = Join-Path $Project 'tools\probes\coverage-selftest'
$goodPlan  = Join-Path $fixRoot 'good'
$badPlan   = Join-Path $fixRoot 'bad'
$reconcile = Join-Path $Project 'tools\probes\audit-coverage-reconcile.py'
if ((-not (Test-Path $goodPlan)) -or (-not (Test-Path $badPlan))) {
  Note ('FAIL  coverage-rows / fixtures : missing good/bad fixture dirs under ' + $fixRoot)
  $bad++
} else {
  $gRes = Invoke-Py $reconcile @('--plan', $goodPlan)
  $gNm = NamesOf $gRes
  Note ('  good fixture / judgement asset: rc=' + $gRes.Rc + ' :: ' + $gNm)
  Expect 'coverage-rows / judgement asset, good fixture rc' ([string]$gRes.Rc) '0'
  Expect 'coverage-rows / judgement asset, good fixture unjudged' (NumOf $gNm 'unjudged') '0'
  Expect 'coverage-rows / judgement asset, good fixture phantom' (NumOf $gNm 'phantom') '0'
  $bRes = Invoke-Py $reconcile @('--plan', $badPlan)
  $bNm = NamesOf $bRes
  Note ('  bad  fixture / judgement asset: rc=' + $bRes.Rc + ' :: ' + $bNm)
  Expect 'coverage-rows / judgement asset, drifted fixture rc' ([string]$bRes.Rc) '1'
  Expect 'coverage-rows / judgement asset, drifted fixture unjudged' (NumOf $bNm 'unjudged') '1'
  Expect 'coverage-rows / judgement asset, drifted fixture phantom' (NumOf $bNm 'phantom') '1'
  Expect 'coverage-rows / GATE, good fixture' (Invoke-GateWithPlan $goodPlan 'coverage-rows') 'PASS'
  Expect 'coverage-rows / GATE, drifted fixture' (Invoke-GateWithPlan $badPlan 'coverage-rows') 'FAIL'
  $rRes = Invoke-Py $reconcile @()
  $rNm = NamesOf $rRes
  $rWant = 'PASS'
  if (((NumOf $rNm 'unjudged') -ne '0') -or ((NumOf $rNm 'phantom') -ne '0')) { $rWant = 'FAIL' }
  $rGot = Invoke-Gate 'coverage-rows'
  Note ('  real plan dir / judgement asset: ' + $rNm)
  Expect 'coverage-rows / GATE agrees with the judgement asset on the real data' $rGot $rWant
}

# =====================================================================
#  8) slice BW-G: no-sync-subagents is INCREMENTAL -- in scope = rows dispatched BELOW
#     the ledger's own marker line ('# team-member-required-below-this-line'), and an
#     in-scope row must carry >= 6 tab-separated columns with non-empty team + member.
#     Samples: pristine => PASS; a new 4-column row => FAIL; 6 columns with an EMPTY
#     member => FAIL; a proper 6-column row => PASS; restored => PASS + hash self-check.
#     The samples run on a COPY of the fixture ledger (tools/probes/ledger-selftest/)
#     through verify.ps1's -Ledger test seam: the real ledger is SHARED mutable state, and
#     restoring a whole-file backup over it silently truncates any row another slice appends
#     inside the window -- and the section's own hash check cannot see that, because it
#     compares against the backup it just restored (slice BW-G, 2026-09-23).
# =====================================================================
Note ''
Note '--- slice BW-G: no-sync-subagents = incremental scope (marker line in the ledger) ---'
$dlog   = Join-Path $Project '.ai-tmp\test\dispatch-log.tsv'
$nsFix  = Join-Path $Project 'tools\probes\ledger-selftest\ledger.tsv'
$nsCopy = Join-Path $Project ('.ai-tmp\test\bwgselftest-nosync-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.tsv')
$hReal0 = HashOf $dlog
$hFix0  = HashOf $nsFix
if (-not (Test-Path $nsFix)) {
  Note ('FAIL  no-sync-subagents : fixture ledger missing at ' + $nsFix)
  $bad++
} else {
  Copy-Item $nsFix $nsCopy -Force
  try {
    Expect 'no-sync-subagents / good (fixture: history above the marker + one in-scope 6-column row)' (Invoke-GateWithLedger $nsCopy 'no-sync-subagents') 'PASS'
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm')
    [IO.File]::AppendAllText($nsCopy, ($stamp + "`tbwg-selftest`tprobe`t" + $Project + "`r`n"), (New-Object Text.UTF8Encoding($false)))
    Expect 'no-sync-subagents / bad (in-scope row without team/member)' (Invoke-GateWithLedger $nsCopy 'no-sync-subagents') 'FAIL'
    Copy-Item $nsFix $nsCopy -Force
    [IO.File]::AppendAllText($nsCopy, ($stamp + "`tbwg-selftest`tprobe`t" + $Project + "`tcs16-fix`t`r`n"), (New-Object Text.UTF8Encoding($false)))
    Expect 'no-sync-subagents / bad (6 columns, member cell empty)' (Invoke-GateWithLedger $nsCopy 'no-sync-subagents') 'FAIL'
    Copy-Item $nsFix $nsCopy -Force
    [IO.File]::AppendAllText($nsCopy, ($stamp + "`tbwg-selftest`tprobe`t" + $Project + "`tcs16-fix`tbw-gate`r`n"), (New-Object Text.UTF8Encoding($false)))
    Expect 'no-sync-subagents / good (in-scope row WITH team + member)' (Invoke-GateWithLedger $nsCopy 'no-sync-subagents') 'PASS'
  } finally {
    Remove-Item $nsCopy -Force -ErrorAction Continue
  }
  Note ('  fixture-asset hash self-check: before=' + $hFix0 + ' after=' + (HashOf $nsFix) + ' identical=' + ($hFix0 -eq (HashOf $nsFix)))
  if ($hFix0 -ne (HashOf $nsFix)) { $bad++; Note 'MISS  the fixture ledger (judgement asset) was modified' }
  if (Test-Path $nsCopy) { $bad++; Note 'MISS  the sample ledger copy was left behind' }
  Note ('  REAL ledger untouched: before=' + $hReal0 + ' after=' + (HashOf $dlog) + ' identical=' + ($hReal0 -eq (HashOf $dlog)))
  if ($hReal0 -ne (HashOf $dlog)) { $bad++; Note 'MISS  the REAL dispatch ledger was written by the self-test' }
}

# =====================================================================
#  9) slice BW-G: evidence-freshness -- a void (row,shot) pair is voided PER ROW, and
#     a known/owned void may be registered as a ROW-SCOPED named adjudication
#     ('# adjudicated: evidence-freshness -- row <id> -- <reason >= 12 chars>', template
#     item 6c) which drops the item to HUMAN-ONLY.
#     The void pair is DETECTED from the gate's own output on the real plan dir (so the
#     samples cannot silently go stale), while the ledger they need is a COPY fed through
#     the -Ledger test seam: the real ledger is shared state and is never written here.
#     Samples:
#       (b) copy without a registration, stale pair        => FAIL   (baseline, MEASURED)
#       (a) + the row-scoped registration for that row     => HUMAN-ONLY
#       (c) png refreshed + registration still there       => FAIL   (it is a silencer)
#       (d) png refreshed + registration removed           => PASS
#     The baseline is measured, never hard-coded: whether the project's own ledger carries a
#     registration is the OWNER's decision, and a hard-coded expectation would make this
#     section fail by design (measured 2026-09-23: the previous version asserted HUMAN-ONLY
#     while the owner had ruled that row B7 must stay RED and unregistered).
#     Every mutation happens inside a SANDBOX (plan-dir copy + shot copies): the real ledger,
#     the real evidence pngs and the real plan dir are only ever READ (slice BW-G-R).
# =====================================================================
Note ''
Note '--- slice BW-G: evidence-freshness = per-row void + row-scoped name adjudication ---'
$efFix   = Join-Path $Project 'tools\probes\ledger-selftest\ledger.tsv'
$efCopy  = Join-Path $Project ('.ai-tmp\test\bwgselftest-ef-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.tsv')
$hRealE0 = HashOf $dlog
$hFixE0  = HashOf $efFix
$efOut = @(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Project 'tools\verify.ps1') 2>&1 | ForEach-Object { [string]$_ })
$efPairs = @()
foreach ($l in @($efOut | Where-Object { $_ -match 'row\s+([A-Za-z]?\d+):\s+shot\s+(\S+\.png)' })) {
  $m = [regex]::Match($l, 'row\s+([A-Za-z]?\d+):\s+shot\s+(\S+\.png)')
  $efPairs += [pscustomobject]@{ Row = $m.Groups[1].Value; Shot = $m.Groups[2].Value }
}
if ($efPairs.Count -eq 0) {
  Note '  (the gate reports no void (row,shot) pair -- nothing LIVE to build the samples from)'
  Note ('  gate says: ' + (Invoke-Gate 'evidence-freshness'))
  # slice BW-G-R: an unexercised sample is not a self-test.  The sandbox forces the shot mtime,
  # so the void condition does not have to exist in the owner's data.  Fall back to the first
  # acceptance row that cites BOTH a resolvable implementation `.cs` (the item needs one on the
  # real code roots) and a shot that exists under .ai-tmp/screenshots (it is copied into the
  # sandbox, so its real mtime/hash is never touched).
  $implIdx = @{}
  foreach ($idir in @((Join-Path $Project 'client\Assets\Scripts'), (Join-Path $Project 'client\Assets\Editor'))) {
    foreach ($if2 in @(Get-ChildItem $idir -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue)) { $implIdx[$if2.Name.ToLower()] = $true }
  }
  $atPath = Join-Path $plan ((([char[]]@(0x9A8C,0x6536,0x8868)) -join '') + '.md')
  $atTxt = [IO.File]::ReadAllText($atPath, [Text.Encoding]::UTF8)
  foreach ($rl in @($atTxt -split "`n")) {
    if (-not $rl.TrimStart().StartsWith('|')) { continue }
    $rc2 = @($rl.Split('|'))
    if ($rc2.Count -lt 7) { continue }
    $rid2 = $rc2[1].Trim()
    if ($rid2 -notmatch '^[A-Za-z]?\d+$') { continue }
    $csHit = $false
    foreach ($cm2 in [regex]::Matches($rl, '([0-9A-Za-z_\-]+\.cs)')) {
      if ($implIdx.ContainsKey($cm2.Groups[1].Value.ToLower())) { $csHit = $true; break }
    }
    if (-not $csHit) { continue }
    $ms2 = [regex]::Matches($rl, '([0-9A-Za-z_\-\.]+\.png)')
    if ($ms2.Count -eq 0) { continue }
    $shReady = ''
    foreach ($sm2 in $ms2) {
      $sp2 = Join-Path $Project ('.ai-tmp\screenshots\' + $sm2.Groups[1].Value)
      if (Test-Path $sp2) { $shReady = $sm2.Groups[1].Value; break }
    }
    if ($shReady -eq '') { continue }
    $efPairs = @([pscustomobject]@{ Row = $rid2; Shot = $shReady })
    Note ('  no live void pair -> forcing one INSIDE the sandbox: row ' + $rid2 + ' / shot ' + $shReady)
    break
  }
  if ($efPairs.Count -eq 0) { Note '  (no acceptance row cites both a resolvable .cs and an existing shot -- samples stay unbuilt)' }
}
if ($efPairs.Count -eq 0) {
  Note '  evidence-freshness / samples : NOT built (see the two notes above) -- this section is INERT, not green'
} elseif (-not (Test-Path $efFix)) {
  Note ('FAIL  evidence-freshness : fixture ledger missing at ' + $efFix)
  $bad++
} else {
  # ALL live void pairs are handled, not just the first one: the item judges the whole set,
  # so a one-pair sample turns red for the others the moment any slice edits another row's
  # implementation file (measured 2026-09-23: two live pairs -> "(a) got=FAIL want=HUMAN-ONLY"
  # and "(d) got=FAIL want=PASS", i.e. a sample that was correct only on quiet data).
  Note ('  detected void pair(s): ' + $efPairs.Count + ' -> ' + (($efPairs | ForEach-Object { $_.Row + '/' + $_.Shot }) -join ', '))
  # slice BW-G-R: the void pair is produced ENTIRELY inside a sandbox -- the plan copy supplies
  # the row/implementation side, a shot copy supplies the evidence side.  Before, the samples
  # rewrote the mtime of REAL evidence pngs (restored in the finally block; a crash in between
  # leaves real evidence timestamped wrong, and a reader inside the window sees a perturbed
  # reading).  The real pngs are now only READ, and are hash+mtime asserted at the end.
  $planCopy9 = Join-Path $Project ('.ai-tmp\test\bwggx-planef-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  $shotCopy9 = Join-Path $Project ('.ai-tmp\test\bwggx-shots-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  New-Item -ItemType Directory -Force -Path $planCopy9 | Out-Null
  Copy-Item (Join-Path $plan '*') $planCopy9 -Force
  New-Item -ItemType Directory -Force -Path $shotCopy9 | Out-Null
  $realShot = [ordered]@{}
  $sandboxShot = @()
  foreach ($p in $efPairs) {
    $pth = Join-Path $Project ('.ai-tmp\screenshots\' + $p.Shot)
    if ((Test-Path $pth) -and (-not $realShot.Contains($pth))) {
      $realShot[$pth] = @{ T = (Get-Item -LiteralPath $pth).LastWriteTime; H = (HashOf $pth) }
      $dst = Join-Path $shotCopy9 $p.Shot
      Copy-Item $pth $dst -Force
      (Get-Item -LiteralPath $dst).LastWriteTime = [datetime]'2000-01-01'
      $sandboxShot += $dst
    }
  }
  Note ('  sandbox: plan=' + (Split-Path $planCopy9 -Leaf) + ' / shot copy(ies)=' + $sandboxShot.Count + ' with mtime forced to 2000-01-01 (the void pair is guaranteed inside the sandbox)')
  Copy-Item $efFix $efCopy -Force
  try {
    # (b) baseline: MEASURED on the sample ledger (no registration) -- a stale pair that is
    # not registered must FAIL, i.e. the gate must not quietly ignore it.
    Expect 'evidence-freshness / (b) sample ledger, no registration' (Invoke-GateWithLedger $efCopy 'evidence-freshness' $planCopy9 $shotCopy9) 'FAIL'
    # one registration line per SANDBOX void pair; >= 12 chars and it names the row, exactly what
    # the item's parser requires (a shorter reason or a missing "row <id>" is ignored).
    # The rows are read back from the item's OWN detail output on the sandbox (step (b) above ran
    # it once already): the sandbox void set is a function of which shot copy was aged, not of the
    # live set -- measured 9 sandbox pairs vs 1 live pair, and registering the live set left the
    # other 8 unregistered, so the sample asked for HUMAN-ONLY on data that must FAIL.
    $sbxOut = Invoke-GateWithLedgerOut $efCopy 'evidence-freshness' $planCopy9 $shotCopy9
    # (built in two statements: PowerShell does not allow a pipeline directly after a
    #  `foreach` STATEMENT -- measured here, it is a PARSE error, not a runtime one)
    $voidRows = @()
    foreach ($ol in $sbxOut) {
      $mv = [regex]::Match([string]$ol, '^\s+row\s+([A-Za-z]?\d+):\s+shot\s+')
      if ($mv.Success) { $voidRows += $mv.Groups[1].Value }
    }
    $voidRows = @($voidRows | Sort-Object -Unique)
    if ($voidRows.Count -eq 0) {
      $bad++
      Note 'MISS  evidence-freshness / (a) : the sandbox reported no void row to register -- the sample cannot prove the HUMAN-ONLY path'
    }
    foreach ($vr in $voidRows) {
      [IO.File]::AppendAllText($efCopy, ('# adjudicated: evidence-freshness -- row ' + $vr + ' -- BW-A selftest sample: the owning slice must re-capture that row' + "`r`n"), (New-Object Text.UTF8Encoding($false)))
    }
    Expect ('evidence-freshness / (a) all ' + $voidRows.Count + ' SANDBOX void pair(s) registered') (Invoke-GateWithLedger $efCopy 'evidence-freshness' $planCopy9 $shotCopy9) 'HUMAN-ONLY'
    if ($sandboxShot.Count -eq 0) {
      Note 'WARN  evidence-freshness / (c)(d) : no void shot resolves to a file on disk -- refresh samples skipped'
    } else {
      # (c) refresh ONE pair's evidence while its registration stays: the registration has
      # outlived its void and the item must say so (a silencer left behind).
      $one = [string]$sandboxShot[0]
      (Get-Item -LiteralPath $one).LastWriteTime = (Get-Date)
      Expect 'evidence-freshness / (c) one pair refreshed, registration left behind' (Invoke-GateWithLedger $efCopy 'evidence-freshness' $planCopy9 $shotCopy9) 'FAIL'
      # (d) refresh EVERY void pair AND drop every registration => the item must go green.
      foreach ($f in @($sandboxShot)) { (Get-Item -LiteralPath $f).LastWriteTime = (Get-Date) }
      $keep = @([IO.File]::ReadAllLines($efCopy, [Text.Encoding]::UTF8) | Where-Object { $_ -notmatch '^\s*#\s*adjudicated:\s*evidence-freshness' })
      [IO.File]::WriteAllText($efCopy, (($keep -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding($false)))
      Expect ('evidence-freshness / (d) all ' + $sandboxShot.Count + ' pair(s) refreshed, no registration') (Invoke-GateWithLedger $efCopy 'evidence-freshness' $planCopy9 $shotCopy9) 'PASS'
    }
  } finally {
    Remove-Item $efCopy -Force -ErrorAction Continue
    Remove-Item $planCopy9 -Recurse -Force -ErrorAction Continue
    Remove-Item $shotCopy9 -Recurse -Force -ErrorAction Continue
  }
  Note ('  fixture-asset hash self-check: before=' + $hFixE0 + ' after=' + (HashOf $efFix) + ' identical=' + ($hFixE0 -eq (HashOf $efFix)))
  if ($hFixE0 -ne (HashOf $efFix)) { $bad++; Note 'MISS  the fixture ledger (judgement asset) was modified' }
  if (Test-Path $efCopy) { $bad++; Note 'MISS  the sample ledger copy was left behind' }
  Note ('  REAL ledger untouched: before=' + $hRealE0 + ' after=' + (HashOf $dlog) + ' identical=' + ($hRealE0 -eq (HashOf $dlog)))
  if ($hRealE0 -ne (HashOf $dlog)) { $bad++; Note 'MISS  the REAL dispatch ledger was written by the self-test' }
  foreach ($f in @($realShot.Keys)) {
    $nowAt = (Get-Item -LiteralPath $f).LastWriteTime
    $nowH = HashOf $f
    Note ('  REAL png untouched: ' + (Split-Path $f -Leaf) + ' mtime identical=' + ($realShot[$f].T -eq $nowAt) + ' hash identical=' + ($realShot[$f].H -eq $nowH))
    if (($realShot[$f].T -ne $nowAt) -or ($realShot[$f].H -ne $nowH)) { $bad++; Note 'MISS  a REAL evidence png was written by the self-test (the samples must use the -ShotDir sandbox)' }
  }
  foreach ($sbx in @($shotCopy9, $planCopy9)) { if (Test-Path $sbx) { $bad++; Note ('MISS  an evidence-freshness sandbox was left behind: ' + (Split-Path $sbx -Leaf)) } }
}

# =====================================================================
# 10) slice BW-G phase 2: screenshot-refs exempts the `.png` names that the entity list
#     registers as ASSETS (map textures under client/Assets/**).  They are not evidence
#     screenshots and can never resolve under .ai-tmp/screenshots/, so citing them is not
#     a defect -- 307 registered asset names would otherwise redden the item with
#     unfixable citations (measured 2026-09-22: 79 refs -> 387 when the mangled names are
#     restored).  The exemption is REGISTRY-based on purpose: a shape rule cannot separate
#     `MapTex.png` from a bare evidence name like `contact-sheet-4-menu.png`, and skipping
#     the COVERAGE block would also exempt the ~365 real citations inside it.
#     It must never blanket-exempt a file, so there are three samples (all via -PlanDir):
#       asset-ok            (the cited name IS registered)              => PASS
#       missing-shot        (unregistered name, not on disk)            => FAIL
#       asset-plus-missing  (registered + unregistered in ONE file)     => FAIL, 1 citation
# =====================================================================
Note ''
Note '--- slice BW-G phase 2: screenshot-refs = registry-based asset exemption ---'
$srRoot = Join-Path $Project 'tools\probes\screenshot-refs-selftest'
foreach ($pair in @(@('asset-ok', 'PASS'), @('missing-shot', 'FAIL'), @('asset-plus-missing', 'FAIL'))) {
  $d = Join-Path $srRoot $pair[0]
  if (-not (Test-Path $d)) {
    Note ('FAIL  screenshot-refs / ' + $pair[0] + ' : fixture missing under ' + $srRoot)
    $bad++
    continue
  }
  Expect ('screenshot-refs / ' + $pair[0]) (Invoke-GateWithPlan $d 'screenshot-refs') $pair[1]
}

# =====================================================================
# 11) slice BW-G: the shot-reference audit must NOT accept a saved operator output as a
#     citation.  Measured live before the fix: leaving a Play output on disk
#     (.ai-tmp/test/<slice>-play-out.txt) made three captured frames count as "referenced",
#     and this audit's OWN zero-ref list, once saved, is a list of png names and would
#     count as their references on the next run (a gate satisfied by its own output).
#     Fixed by scanning DECLARATIVE carriers only (plan/** + tools/probes/** +
#     .ai-tmp/drivers/** + *.index.tsv/*.manifest.tsv).  Samples, all with a temp png
#     created here (so they do not depend on any slice's current evidence):
#       (a) temp png alone                    -> audit counts it zero-ref
#       (b) + a saved Play output naming it   -> STILL zero-ref  (no self-heal)   [red-proof]
#       (c) + a contact-sheet index naming it -> no longer zero-ref (a declaration counts)
#       (d) both removed                      -> back to (a); png removed -> baseline
#     Names are assembled at RUNTIME on purpose (a literal in this file would itself be a
#     citation of it -- the same trap verify.ps1 item 26 documents).
# =====================================================================
Note ''
Note '--- slice BW-G: audit-shot-refs -- a saved output must not count as a citation ---'
$shotDir2 = Join-Path $Project '.ai-tmp\screenshots'
$testDir2 = Join-Path $Project '.ai-tmp\test'
$auditPs1 = Join-Path $Project 'tools\probes\audit-shot-refs.ps1'
if ((-not (Test-Path $shotDir2)) -or (-not (Test-Path $auditPs1))) {
  Note 'FAIL  shot-refs-audited / self-heal sample : screenshots dir or audit script missing'
  $bad++
} else {
  $tag  = [guid]::NewGuid().ToString('N').Substring(0, 8)
  $pngN = 'bwgselftest' + $tag + '.png'
  $pngP = Join-Path $shotDir2 $pngN
  $outP = Join-Path $testDir2 ('bwgselftest' + $tag + '-play-out.txt')
  $idxP = Join-Path $shotDir2 ('bwgselftest' + $tag + '.index.tsv')
  function Audit-Zero {
    $o = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $auditPs1 2>&1 | ForEach-Object { [string]$_ })
    $s = @($o | Where-Object { $_ -match 'unreferenced \(count = (\d+)\): (\d+) of (\d+)' } | Select-Object -Last 1)
    if ($s.Count -eq 0) { return -1 }
    return [int]([regex]::Match($s[0], 'unreferenced \(count = (\d+)\)').Groups[1].Value)
  }
  function Audit-Mentions([string]$name) {
    $o = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $auditPs1 -Png $name 2>&1 | ForEach-Object { [string]$_ })
    $s = @($o | Where-Object { $_ -match '\(total occurrences ' })
    if ($s.Count -eq 0) { return -1 }
    return [int]([regex]::Match($s[0], '\(total occurrences (\d+)\)').Groups[1].Value)
  }
  try {
    $z0 = Audit-Zero
    Note ('  baseline zero-ref count = ' + $z0)
    [IO.File]::WriteAllBytes($pngP, [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D))
    $z1 = Audit-Zero
    Note ('  (a) temp png alone: zero-ref = ' + $z1)
    Expect 'audit-shot-refs / (a) a brand-new png is unreferenced' ([string]($z1 - $z0)) '1'
    # (b) the RED-proof: a saved operator output that names the png must not make it cited
    [IO.File]::WriteAllText($outP, ("===== SUMMARY: FAIL=1  HUMAN-ONLY=0 =====`r`nZERO   " + $pngN + "`r`n"), (New-Object Text.UTF8Encoding($false)))
    $z2 = Audit-Zero
    $m2 = Audit-Mentions $pngN
    Note ('  (b) with a saved Play output naming it: zero-ref = ' + $z2 + ' ; files mentioning it = ' + $m2)
    Expect 'audit-shot-refs / (b) a saved output does NOT heal it (still unreferenced)' ([string]($z2 - $z0)) '1'
    Expect 'audit-shot-refs / (b) the output file is not even a carrier' ([string]$m2) '0'
    # (c) a real declaration (contact-sheet index) DOES count
    [IO.File]::WriteAllText($idxP, ($pngN + "`tsheet cell 1`r`n"), (New-Object Text.UTF8Encoding($false)))
    $z3 = Audit-Zero
    $m3 = Audit-Mentions $pngN
    Note ('  (c) with a contact-sheet index naming it: zero-ref = ' + $z3 + ' ; files mentioning it = ' + $m3)
    Expect 'audit-shot-refs / (c) a declaration DOES count (no longer unreferenced)' ([string]($z3 - $z0)) '0'
    Expect 'audit-shot-refs / (c) the index is a carrier' ([string]$m3) '1'
  } finally {
    Remove-Item $pngP -Force -ErrorAction Continue
    Remove-Item $outP -Force -ErrorAction Continue
    Remove-Item $idxP -Force -ErrorAction Continue
  }
  $z4 = Audit-Zero
  Note ('  (d) restored: zero-ref = ' + $z4 + ' (must equal the baseline ' + $z0 + ')')
  Expect 'audit-shot-refs / (d) restored to baseline' ([string]$z4) ([string]$z0)
  foreach ($p in @($pngP, $outP, $idxP)) { if (Test-Path $p) { $bad++; Note ('MISS  sample artifact left behind: ' + (Split-Path $p -Leaf)) } }
}

# =====================================================================
# 12) slice BW-G: the `.cs` branch of screenshot-refs must resolve citations inside
#     client/Packages/** (the engine package directory is a junction INSIDE the project, and
#     engine sources are the provenance of many verdict rows) -- while staying strict.
#     ROOT CAUSE found while adding this (not a prefix problem at all): the reference regex
#     character class had NO HYPHEN, so
#       `client/Packages/com.clover.unity-engine/Runtime/Presentation/Sound.cs:112`
#     matched only from `engine/Runtime/...` onwards.  Same shape, one passes and one fails:
#     the passing citation carried no `:line` at all and was therefore never scanned.
#     Samples (via -PlanDir):
#       cs-pkg-ok        a real package source + a valid line   => PASS
#       cs-pkg-missing   a package path that is NOT on disk      => FAIL
#       cs-pkg-lineover  a real file, line beyond its length     => FAIL (line guard intact)
# =====================================================================
Note ''
Note '--- slice BW-G: screenshot-refs .cs branch = client/Packages/** supported, still strict ---'
$srRoot2 = Join-Path $Project 'tools\probes\screenshot-refs-selftest'
foreach ($pair in @(@('cs-pkg-ok', 'PASS'), @('cs-pkg-missing', 'FAIL'), @('cs-pkg-lineover', 'FAIL'))) {
  $d = Join-Path $srRoot2 $pair[0]
  if (-not (Test-Path $d)) {
    Note ('FAIL  screenshot-refs / ' + $pair[0] + ' : fixture missing under ' + $srRoot2)
    $bad++
    continue
  }
  Expect ('screenshot-refs / ' + $pair[0]) (Invoke-GateWithPlan $d 'screenshot-refs') $pair[1]
}
# Mechanism guard for the hyphen (this is what stops the defect from coming back): the
# PRE-fix pattern truncates a hyphenated path, the CURRENT one does not.  Kept as an
# expectation rather than a verdict sample because the by-name fallback makes the two
# patterns agree behaviourally whenever the cited file's BASENAME exists under
# client/Assets/Scripts -- the truncation's silent-acceptance window is exactly
# "{captured tail resolves} and {citation as written does not}", and in this project's
# current layout no real `.cs` sits outside the by-name roots with a hyphen-free tail
# (Unity cache paths carry `@`, package paths carry `-`, Scripts/Editor ARE the roots).
# => the measured effect of the truncation is the FALSE RED (Sound.cs) plus the risk of
# checking a line number against a same-named file; the fix removes both.  Narrowing the
# by-name fallback itself is a separate ruling (deliberately NOT touched here).
$oldCsRx = [regex]'[0-9A-Za-z_/\\.]+\.cs:[0-9]+'
$newCsRx = [regex]'[0-9A-Za-z_/\\.\-]+\.cs:[0-9]+'
$truncProbe = 'Q-client/Assets/Scripts/Module/Match/CsMatch.cs:10'
Expect 'screenshot-refs / pre-fix pattern TRUNCATES at the hyphen' ($oldCsRx.Match($truncProbe).Value) 'client/Assets/Scripts/Module/Match/CsMatch.cs:10'
Expect 'screenshot-refs / current pattern captures the WHOLE citation' ($newCsRx.Match($truncProbe).Value) $truncProbe
# Chinese explicit line-number form (main-agent ruling 2026-09-22): it states the same intent
# as `X.cs:N`, so it is scanned; the range is checked against its UPPER bound.  Three samples:
#   cn-line-ok          real file + a valid line            => PASS
#   cn-line-missing     a .cs that is not on disk           => FAIL
#   cn-line-range-over  real file, range beyond its length  => FAIL (upper bound)
foreach ($pair in @(@('cn-line-ok', 'PASS'), @('cn-line-missing', 'FAIL'), @('cn-line-range-over', 'FAIL'))) {
  $d = Join-Path $srRoot2 $pair[0]
  if (-not (Test-Path $d)) {
    Note ('FAIL  screenshot-refs / ' + $pair[0] + ' : fixture missing under ' + $srRoot2)
    $bad++
    continue
  }
  Expect ('screenshot-refs / ' + $pair[0]) (Invoke-GateWithPlan $d 'screenshot-refs') $pair[1]
}

# --- by-name fallback (main-agent ruling 2026-09-22): KEPT, but it must be visible and it
#     must fail on AMBIGUITY.  The fallback roots are re-pointed at a temp tree through the
#     -CodeRoots test seam so these run through the REAL gate code (no duplicate basename
#     exists in the project's own Scripts/Editor):
#       byname-unique     one file with that name   => PASS, counted in "BASENAME fallback 1"
#       byname-ambiguous  two files share the name  => FAIL + both candidate paths printed
function Invoke-GateWithPlanRoots([string]$planDir, [string]$codeRoots, [string]$gateName) {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate -PlanDir $planDir -CodeRoots $codeRoots 2>&1 | ForEach-Object { [string]$_ })
  $rx = '^(PASS|FAIL|HUMAN-ONLY)\s+' + [regex]::Escape($gateName) + '(\s|$)'
  $line = @($out | Where-Object { $_ -match $rx } | Select-Object -First 1)
  if ($line.Count -eq 0) { return 'NO-VERDICT' }
  if ($line[0] -match '^PASS') { return 'PASS' }
  if ($line[0] -match '^FAIL') { return 'FAIL' }
  return 'HUMAN-ONLY'
}
Note ''
Note '--- slice BW-G: by-name fallback = grouped counting + ambiguity FAILs ---'
# The synthetic fallback roots are CREATED and REMOVED inside this block: leaving .cs files
# under .ai-tmp/test would show up as "same-name candidates" in OTHER judgement assets
# (measured: tools/probes/scan-reftable-refs.py started reporting them for 差异登记.tsv's
# carrier citations). One-off artifacts must not pile up -- the fixtures themselves
# (byname-unique / byname-ambiguous) hold no .cs, so they stay.
$rootsBase = Join-Path $Project '.ai-tmp\test\byname-roots'
foreach ($pair in @(@('byname-unique', 'unique', 'PASS'), @('byname-ambiguous', 'dup', 'FAIL'))) {
  $d = Join-Path $srRoot2 $pair[0]
  $rr = Join-Path $rootsBase $pair[1]
  if (-not (Test-Path $d)) {
    Note ('FAIL  screenshot-refs / ' + $pair[0] + ' : fixture missing under ' + $srRoot2)
    $bad++
    continue
  }
  $body = ((1..20 | ForEach-Object { '// line ' + $_ }) -join "`r`n")
  try {
    New-Item -ItemType Directory -Force -Path (Join-Path $rr 'p') | Out-Null
    if ($pair[0] -eq 'byname-unique') {
      [IO.File]::WriteAllText((Join-Path $rr 'p\CsMatch.cs'), $body, (New-Object Text.UTF8Encoding($false)))
    } else {
      New-Item -ItemType Directory -Force -Path (Join-Path $rr 'q') | Out-Null
      [IO.File]::WriteAllText((Join-Path $rr 'p\CsMatch.cs'), $body, (New-Object Text.UTF8Encoding($false)))
      [IO.File]::WriteAllText((Join-Path $rr 'q\CsMatch.cs'), $body, (New-Object Text.UTF8Encoding($false)))
    }
    Expect ('screenshot-refs / ' + $pair[0]) (Invoke-GateWithPlanRoots $d $rr 'screenshot-refs') $pair[2]
  } finally {
    Remove-Item $rr -Recurse -Force -ErrorAction Continue
  }
}
# The per-sample finally above removes $rr, but its PARENT is created implicitly by
# New-Item -Force when the first sample runs, so the base dir must be removed as well --
# otherwise the check below stays red for a directory that holds nothing (measured
# 2026-09-23: the first run of this section left .ai-tmp/test/byname-roots/ behind).
if (Test-Path $rootsBase) { Remove-Item $rootsBase -Recurse -Force -ErrorAction Continue }
if (Test-Path $rootsBase) { $bad++; Note 'MISS  the synthetic by-name roots were left behind' }

Note ''
# =====================================================================
# 13) slice BW-G-R: ps1-ascii (verify.ps1 item 36) -- a Chinese character in a .ps1 LITERAL
#     must be caught.  PS 5.1 reads a BOM-less .ps1 as ANSI: the literal becomes mojibake, and
#     a pattern built from it silently never matches (the documented trap of this project).
#     Two samples through the NEW -Ps1Root seam, so nothing is ever written under the real
#     tools/** (a defect there would make every concurrent verify report a false FAIL):
#       (a) sandbox holding an ASCII-only script                => PASS
#       (b) + a script with a Chinese character inside a literal => FAIL
# =====================================================================
Note ''
Note '--- slice BW-G-R: ps1-ascii = non-ASCII outside comments ---'
$ps1Sandbox = Join-Path $Project ('.ai-tmp\test\bwggx-ps1-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $ps1Sandbox | Out-Null
Set-Content -Path (Join-Path $ps1Sandbox 'good.ps1') -Encoding ASCII -Value @('# a comment may hold any byte', "Write-Output 'ascii only'")
Expect 'ps1-ascii / good sample (ASCII outside comments)' (Invoke-Gate 'ps1-ascii' @('-Ps1Root', $ps1Sandbox)) 'PASS'
$cnChar = [char]0x4E2D
Set-Content -Path (Join-Path $ps1Sandbox 'bad.ps1') -Encoding UTF8 -Value @("Write-Output 'literal $cnChar inside a string'")
Expect 'ps1-ascii / bad sample (Chinese literal outside comments)' (Invoke-Gate 'ps1-ascii' @('-Ps1Root', $ps1Sandbox)) 'FAIL'
Remove-Item $ps1Sandbox -Recurse -Force -ErrorAction Continue

# =====================================================================
# 14) slice BW-G-R: the `.cs` NAME class must carry the hyphen.  A citation of a hyphenated
#     file (this project really has tools/probes/bodyheight-walkline.cs) was matched as
#     `walkline.cs`, so the row silently lost its implementation file -- an unjudged row that
#     looks like a clean one.  The assertions bind to the LIVE verify.ps1 source, then show the
#     two directions on a real fixture (a copy of the file, indexed the way the gate indexes):
#       (a) the full hyphenated name resolves            => True
#       (b) the truncated name does NOT resolve          => False  (i.e. the old hit was真 red)
# =====================================================================
Note ''
Note '--- slice BW-G-R: `.cs` name class accepts the hyphen ---'
$vSrc = [IO.File]::ReadAllText((Join-Path $Project 'tools\verify.ps1'), [Text.Encoding]::UTF8)
$patFixed = '([A-Za-z_][A-Za-z0-9_\-]*\.cs)'
$patOld   = '([A-Za-z_][A-Za-z0-9_]*\.cs)'
Expect 'cs-name / verify.ps1 carries the hyphen in the .cs name class' ([string]$vSrc.Contains($patFixed)) 'True'
Expect 'cs-name / the hyphen-less form is gone from verify.ps1' ([string]$vSrc.Contains($patOld)) 'False'
$cited = 'bodyheight-walkline.cs'
$mFix = [regex]::Match('tools/probes/' + $cited + ':12', $patFixed)
$mOld = [regex]::Match('tools/probes/' + $cited + ':12', $patOld)
Expect 'cs-name / the fixed class extracts the full name' ([string]$mFix.Groups[1].Value) $cited
Expect 'cs-name / the hyphen-less class truncates it (what the old code did)' ([string]$mOld.Groups[1].Value) 'walkline.cs'
$csNameFix = Join-Path $Project ('.ai-tmp\test\bwggx-csname-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $csNameFix | Out-Null
Copy-Item (Join-Path $Project 'tools\probes\bodyheight-walkline.cs') $csNameFix -Force
$csIdx = @{}
foreach ($cf in @(Get-ChildItem $csNameFix -Filter '*.cs' -File)) { $csIdx[$cf.Name.ToLower()] = $cf.FullName }
Expect 'cs-name / the full name resolves in the index' ([string]$csIdx.ContainsKey($cited.ToLower())) 'True'
Expect 'cs-name / the truncated sample FAILS to resolve in the index' ([string]$csIdx.ContainsKey('walkline.cs')) 'False'
Remove-Item $csNameFix -Recurse -Force -ErrorAction Continue

# =====================================================================
# 15) slice BW-G-R: editor-assembly-compiles (verify.ps1 item 37) in BOTH directions, on REAL
#     samples.  The criterion script compiles the real client/Assets/Editor/** (must be 0 error)
#     and then the SAME tree with the historical defect re-injected in a COPY (bare
#     `InternalEditorUtility` => CS0103).  client/** is only ever read.
# =====================================================================
Note ''
Note '--- slice BW-G-R: editor-assembly-compiles (Assets/Editor/**) ---'
$ecScript = Join-Path $Project 'tools\probes\bwgr-editor-selftest.ps1'
if (-not (Test-Path $ecScript)) {
  $bad++
  Note ('MISS  editor-compile self-test script missing: ' + $ecScript)
} else {
  $ecOut = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $ecScript 2>&1 | ForEach-Object { [string]$_ })
  $ecRc = $LASTEXITCODE
  $ecOut | Set-Content -Encoding UTF8 (Join-Path $Project '.ai-tmp\test\bwgr-editor-selftest-out.txt')
  foreach ($el in $ecOut) { Note ('  ' + $el) }
  Expect 'editor-compile / two-direction self-test rc' ([string]$ecRc) '0'
  Expect 'editor-compile / GATE item agrees on the real tree' (Invoke-Gate 'editor-assembly-compiles') 'PASS'
}

# =====================================================================
# 16) slice BW-A: evidence-anchor (item 39) / coverage-hit (item 40) / state-matrix (item 41).
#     Two samples each (three for coverage-hit: a genuine ledger, a ledger one row id short, and
#     a ledger that is a TABLE ECHO), per SKILL 8.3 / anti-gaming section 5 -- and the criterion
#     each one judges is a PROCESS, not a result:
#       evidence-anchor : every verdict row must quote a CHECKABLE anchor (a file:line, a
#                         guid=, an on-disk path, or a number + path) rather than prose;
#       coverage-hit    : every verdict row must be HIT BY ROW ID inside a probe output
#                         (row-count equality is the gameable criterion this replaces);
#       state-matrix    : every one of the 12+3 dimensions must have a state row.
#     ISOLATION (anti-gaming section 5 rule 3): the plan-dir samples are FIXTURES fed through
#     -PlanDir; the hit ledgers are written into a sandbox and fed through the NEW -ProbeRoot
#     seam, so nothing under the real probe roots or the real plan dir is written.  A ledger
#     declares `plan=<plan dir>` in its header, which is also what keeps a fixture ledger from
#     faking hits for the real table (it lives under tools/probes/**, a default probe root).
#     Fixture provenance: tools/probes/verdict-rows-selftest/{good,bad-anchor} and
#     tools/probes/coverage-selftest/statemx-bad were derived from coverage-selftest/good
#     (15 rows / 15 dims) by copying it, adding a 3-line run.txt, turning row 1's evidence into
#     prose (bad-anchor) and deleting the S2 row of the state matrix (statemx-bad).
# =====================================================================
function Invoke-GateWithPlanProbe([string]$planDir, [string]$probeRoot, [string]$gateName) {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate -PlanDir $planDir -ProbeRoot $probeRoot 2>&1 | ForEach-Object { [string]$_ })
  $rx = '^(PASS|FAIL|HUMAN-ONLY)\s+' + [regex]::Escape($gateName) + '(\s|$)'
  $line = @($out | Where-Object { $_ -match $rx } | Select-Object -First 1)
  if ($line.Count -eq 0) { return 'NO-VERDICT' }
  if ($line[0] -match '^PASS') { return 'PASS' }
  if ($line[0] -match '^FAIL') { return 'FAIL' }
  return 'HUMAN-ONLY'
}
Note ''
Note '--- slice BW-A: evidence-anchor / coverage-hit / state-matrix ---'
$vrRoot   = Join-Path $Project 'tools\probes\verdict-rows-selftest'
$vrGood   = Join-Path $vrRoot 'good'
$vrBadA   = Join-Path $vrRoot 'bad-anchor'
$smBad    = Join-Path $Project 'tools\probes\coverage-selftest\statemx-bad'
if ((-not (Test-Path $vrGood)) -or (-not (Test-Path $vrBadA)) -or (-not (Test-Path $smBad))) {
  Note ('FAIL  BW-A fixtures : missing under ' + $vrRoot + ' and/or statemx-bad')
  $bad++
} else {
  # the six REAL plan artifacts must stay byte-identical for the whole section
  $planReal = Join-Path $Project ([char[]]@(0x7B56, 0x5212) -join '')          # ce hua
  $artReal = [ordered]@{}
  $artNames = @(
    ((([char[]]@(0x9A8C, 0x6536, 0x8868)) -join '') + '.md'),
    ((([char[]]@(0x5B9E, 0x4F53, 0x6E05, 0x5355)) -join '') + '.tsv'),
    ((([char[]]@(0x72B6, 0x6001, 0x77E9, 0x9635)) -join '') + '.tsv'),
    ((([char[]]@(0x5DEE, 0x5F02, 0x767B, 0x8BB0)) -join '') + '.tsv'),
    ((([char[]]@(0x8986, 0x76D6, 0x77E9, 0x9635, 0x5224, 0x5B9A)) -join '') + '.fragment.md'),
    ((([char[]]@(0x5DEE, 0x5F02, 0x767B, 0x8BB0)) -join '') + '.fragment.md'))
  foreach ($n in $artNames) {
    $p = Join-Path $planReal $n
    $artReal[$p] = @{ H = (HashOf $p); N = (@([IO.File]::ReadAllLines($p, [Text.Encoding]::UTF8))).Count }
  }
  $hitDir = Join-Path $Project ('.ai-tmp\test\bwga-hits-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  New-Item -ItemType Directory -Force -Path $hitDir | Out-Null
  $led    = Join-Path $hitDir 'hits.tsv'
  $goodFn = (Get-Item -LiteralPath $vrGood).FullName
  $TAB    = [char]9
  # The sample ledger must satisfy the HIT CONTRACT itself (main-agent ruling 2026-09-23, (b)):
  # enumeration-class rows (D1,D5) are the enumerator's own output (a measurement it took), while
  # behaviour-class rows need a real probe (`probe=<name>` + `measured=/run=`) -- otherwise the
  # "good" sample would go red for the very reason the contract was written, and the sample would
  # be proving the opposite of what it says.  Built from the fixture's own rows so the two cannot
  # drift apart.
  $lsFull = @('# probe-hits plan=' + $goodFn)
  foreach ($rl in @([IO.File]::ReadAllLines((Join-Path $vrGood $artNames[0]), [Text.Encoding]::UTF8))) {
    if ($rl -match '^\|\s*\d+\s*\|') {
      $cc = @($rl.Split('|') | ForEach-Object { $_.Trim() })
      if (@('D1', 'D5') -contains $cc[2]) {
        $lsFull += ($cc[1] + $TAB + 'F1:run.txt:1' + $TAB + 'bytes=3 lines=3')
      } else {
        $lsFull += ($cc[1] + $TAB + 'F1:run.txt:1' + $TAB + 'probe=bw-a-fixture measured=rows=6')
      }
    }
  }
  try {
    [IO.File]::WriteAllLines($led, $lsFull, (New-Object Text.UTF8Encoding($false)))
    Expect 'evidence-anchor / good fixture (every evidence cell is run.txt:1, on disk)' (Invoke-GateWithPlanProbe $vrGood $hitDir 'evidence-anchor') 'PASS'
    Expect 'coverage-hit / good fixture (ledger hits every row id)' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'PASS'
    Expect 'state-matrix / good fixture (15 of 15 dimensions)' (Invoke-GateWithPlan $vrGood 'state-matrix') 'PASS'

    # injected defect 1: ONE row's evidence cell is prose -> the anchor item must go red
    Expect 'evidence-anchor / bad fixture (ONE row evidence is prose, no anchor)' (Invoke-GateWithPlanProbe $vrBadA $hitDir 'evidence-anchor') 'FAIL'
    # injected defect 2: the ledger stops one row id short -> the hit item must go red
    [IO.File]::WriteAllLines($led, $lsFull[0..14], (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / bad sample (ledger misses ONE row id, 15)' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'FAIL'
    # injected defect 3: the state matrix loses its S2 row -> the state-matrix item must go red
    Expect 'state-matrix / bad fixture (S2 row deleted)' (Invoke-GateWithPlan $smBad 'state-matrix') 'FAIL'

    # restored: the untouched ledger is green again (proves the red came from the injection)
    [IO.File]::WriteAllLines($led, $lsFull, (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / restored (ledger hits every row id again)' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'PASS'
    Expect 'evidence-anchor / restored' (Invoke-GateWithPlanProbe $vrGood $hitDir 'evidence-anchor') 'PASS'

    # injected defect 4 (anti-gaming section 8, MEASURED on this project 2026-09-23): a ledger
    # that merely RE-RENDERS the fixture's own verdict rows -- it carries the declaration and
    # every row id, so a naive "the id appears in an output" item goes green while no probe has
    # run at all (the item satisfied by its own input).  The item must reject it as a TABLE ECHO.
    Remove-Item $led -Force -ErrorAction Continue
    # NB the name matters: the judgement asset collects a file as a ledger only when its NAME
    # contains "hits" (the scope rule that stops a self-test sample from polluting the real
    # criterion), so the echo sample must be named like a ledger too -- otherwise it would be
    # rejected for the wrong reason and the sample would pass/fail by accident.
    $echoLed  = Join-Path $hitDir 'echo-hits.tsv'
    $echoRows = @('# probe-hits plan=' + $goodFn)
    foreach ($rl in @([IO.File]::ReadAllLines((Join-Path $vrGood $artNames[0]), [Text.Encoding]::UTF8))) {
      if ($rl -match '^\|\s*\d+\s*\|') {
        $cc = @($rl.Split('|') | ForEach-Object { $_.Trim() })
        $echoRows += (($cc[1..6]) -join $TAB)
      }
    }
    [IO.File]::WriteAllLines($echoLed, $echoRows, (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / bad sample (ledger is a RE-RENDER of the rows = table echo)' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'FAIL'
    Remove-Item $echoLed -Force -ErrorAction Continue
    [IO.File]::WriteAllLines($led, $lsFull, (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / restored after the table-echo sample' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'PASS'

    # HIT CONTRACT samples (main-agent ruling 2026-09-23, option (b) -- frozen).  The good ledger
    # above already satisfies the contract (enumeration rows = the enumerator's own output;
    # behaviour rows carry probe=<name> + measured=).  The two negatives below are the contract's
    # two teeth; without them the contract would only be asserted in prose.
    Remove-Item $led -Force -ErrorAction Continue
    $lsNB = @('# probe-hits plan=' + $goodFn)
    foreach ($ql in @($lsFull[1..($lsFull.Count - 1)])) {
      $col = $ql.Split($TAB)
      if ($col[$col.Count - 1] -like 'bytes=*') { $lsNB += $ql } else { $lsNB += ($col[0] + $TAB + $col[1]) }
    }
    [IO.File]::WriteAllLines($led, $lsNB, (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / bad sample (behaviour rows lose probe=+measured=)' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'FAIL'
    $lsLY = @($lsFull)
    $idx1 = -1
    for ($z = 1; $z -lt $lsLY.Count; $z++) { if ($lsLY[$z].Split($TAB)[0] -eq '1') { $idx1 = $z; break } }
    if ($idx1 -gt 0) { $lsLY[$idx1] = $lsLY[$idx1] + $TAB + 'unresolved=1' }
    [IO.File]::WriteAllLines($led, $lsLY, (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / bad sample (anchor present but unresolved=1 lies)' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'FAIL'
    [IO.File]::WriteAllLines($led, $lsFull, (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / restored after the (b) contract samples' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'PASS'

    # Negative 3: an UNDECLARED measurement shape.  WHY (the relay slice's first P4 attempt): it
    # looked correct in every visible way while 1180 rows silently fell into a degradation shape --
    # "every field has a value" is NOT "every field is right".  So a shape outside the declared set
    # must be visible AND red, otherwise a silent failure hides behind a shape that looks normal.
    $lsSH = @($lsFull)
    # ⚠ the target MUST be an ENUMERATION row (D1/D5), whose shape is `bytes=/lines=`: the first
    # version of this sample targeted row id 2 -- which is D2, a BEHAVIOUR row carrying
    # `probe=/measured=` -- so the replace below was a silent NO-OP, the ledger stayed valid, and
    # the terminal run reported `MISS ... got=PASS want=FAIL`.  That MISS was this sample's own bug,
    # NOT a disproof of the rule (and the assertion below makes the same mistake impossible).
    $idx2 = -1
    for ($z = 1; $z -lt $lsSH.Count; $z++) {
      if (($lsSH[$z].Split($TAB)[0] -eq '5') -and ($lsSH[$z] -match 'bytes=3 lines=3')) { $idx2 = $z; break }
    }
    if ($idx2 -le 0) {
      $bad++
      Note 'MISS  the undeclared-shape sample could not find an enumeration row to degrade -- the sample would test nothing'
    } else {
      $lsSH[$idx2] = ($lsSH[$idx2] -replace 'bytes=3 lines=3', 'silently_degraded=1')
    }
    [IO.File]::WriteAllLines($led, $lsSH, (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / bad sample (undeclared measurement shape)' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'FAIL'
    [IO.File]::WriteAllLines($led, $lsFull, (New-Object Text.UTF8Encoding($false)))
    Expect 'coverage-hit / restored after the shape sample' (Invoke-GateWithPlanProbe $vrGood $hitDir 'coverage-hit') 'PASS'
  } finally {
    Remove-Item $hitDir -Recurse -Force -ErrorAction Continue
  }
  if (Test-Path $hitDir) { $bad++; Note 'MISS  the BW-A hit-ledger sandbox was left behind' }
  foreach ($p in @($artReal.Keys)) {
    $hNow = HashOf $p; $nNow = (@([IO.File]::ReadAllLines($p, [Text.Encoding]::UTF8))).Count
    $leaf = Split-Path $p -Leaf
    Note ('  REAL plan artifact untouched: ' + $leaf + ' hash identical=' + ($artReal[$p].H -eq $hNow) + ' lines ' + $artReal[$p].N + ' -> ' + $nNow)
    if ($artReal[$p].H -ne $hNow) {
      # Kept RED on purpose (main-agent ruling 2026-09-23: "we want 'can turn red', not 'red
      # hidden'") -- but with the attribution spelled out, because this assertion cannot tell WHO
      # wrote the file: any concurrent writer trips it.  Measured 2026-09-23: slice BW-E's
      # `enumerate-entities.py --inject` ran at 08:56:24 INSIDE this run (the main agent had
      # wrongly declared the window void from a 15 s sample and retracted it later) => that MISS
      # was KNOWN EXTERNAL POLLUTION, not a gate defect.  Do not "fix" it by weakening the check.
      $bad++
      Note ('MISS  the REAL plan artifact changed during section 16: ' + $leaf + ' [attribution: any concurrent writer trips this, the self-test itself never writes the real plan dir (ce-hua); 2026-09-23 08:56:24 = slice BW-E --inject inside this window = known external pollution, NOT a gate defect]')
    }
    if ($artReal[$p].N -ne $nNow) { $bad++; Note ('MISS  the REAL plan artifact changed its line count: ' + $leaf) }
  }
  # the fixtures / the judgement asset must not be modified by a self-test run either
  foreach ($p in @((Join-Path $vrGood 'run.txt'), (Join-Path $vrBadA 'run.txt'),
                   (Join-Path $Project 'tools\probes\audit-verdict-rows.py'))) {
    if (-not (Test-Path $p)) { $bad++; Note ('MISS  a BW-A judgement asset is gone: ' + $p) }
  }
  $hitStill = @(Get-ChildItem (Join-Path $Project '.ai-tmp\test') -Directory -Filter 'bwga-hits-*' -ErrorAction SilentlyContinue)
  Note ('  hit-ledger sandbox left behind = ' + $hitStill.Count)
  if ($hitStill.Count -gt 0) { $bad++; Note 'MISS  a bwga-hits-* sandbox was left behind' }
}

# =====================================================================
# 17) slice BW-A: gate-sync (verify item 42) -- the gate vs the template's declared item list.
#     Two samples: the TEMPLATE ITSELF (a pristine copy) must PASS, and a template with one EXTRA
#     declared item that no project implements must FAIL.  The real verify-template.md is never
#     written here -- it belongs to the skill owner and every project on the machine shares it --
#     which is exactly why the -GateTemplate seam exists (the bad sample runs on a sandbox copy).
# =====================================================================
function Invoke-GateWithTemplate([string]$tplPath, [string]$gateName) {
  $gate = Join-Path $Project 'tools\verify.ps1'
  $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $gate -GateTemplate $tplPath 2>&1 | ForEach-Object { [string]$_ })
  $rx = '^(PASS|FAIL|HUMAN-ONLY)\s+' + [regex]::Escape($gateName) + '(\s|$)'
  $line = @($out | Where-Object { $_ -match $rx } | Select-Object -First 1)
  if ($line.Count -eq 0) { return 'NO-VERDICT' }
  if ($line[0] -match '^PASS') { return 'PASS' }
  if ($line[0] -match '^FAIL') { return 'FAIL' }
  return 'HUMAN-ONLY'
}
Note ''
Note '--- slice BW-A: gate-sync (gate vs template) ---'
$gsProbe = @(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Project 'tools\gate-sync.ps1') -Project $Project 2>&1 | ForEach-Object { [string]$_ })
$tplLine = @($gsProbe | Where-Object { $_ -match '^INFO\s+gate-template\s' } | Select-Object -Last 1)
if ($tplLine.Count -eq 0) {
  $bad++
  Note 'MISS  gate-sync did not report which template it used -- item 42 cannot be sampled'
} else {
  $tplPath  = (([string]$tplLine[0]) -replace '^INFO\s+gate-template\s+comparing against\s+', '').Trim()
  $tplHash0 = HashOf $tplPath
  $tplTxt0  = [IO.File]::ReadAllText($tplPath, [Text.Encoding]::UTF8)
  $tplDir   = Join-Path $Project '.ai-tmp\test\bwga-tpl'
  New-Item -ItemType Directory -Force -Path $tplDir | Out-Null
  $tGood = Join-Path $tplDir 'good.md'
  $tBad  = Join-Path $tplDir 'bad.md'
  WriteText $tGood $tplTxt0
  $fake   = 'bw-a-selftest-fake-item||required|fixture: an item no project implements' + [char]13 + [char]10 + '$1'
  WriteText $tBad ($tplTxt0 -replace '(?s)(<!--\s*GATE-ITEMS-END\s*-->)', $fake)
  Expect 'gate-sync / good (pristine template copy)' (Invoke-GateWithTemplate $tGood 'gate-sync') 'PASS'
  Expect 'gate-sync / bad (one EXTRA declared item no project implements)' (Invoke-GateWithTemplate $tBad 'gate-sync') 'FAIL'
  Expect 'gate-sync / restored (pristine copy again)' (Invoke-GateWithTemplate $tGood 'gate-sync') 'PASS'
  Remove-Item $tplDir -Recurse -Force -ErrorAction Continue
  Note ('  REAL template untouched: ' + (Split-Path $tplPath -Leaf) + ' hash identical=' + ((HashOf $tplPath) -eq $tplHash0) + ' (' + $tplPath + ')')
  if ((HashOf $tplPath) -ne $tplHash0) { $bad++; Note 'MISS  the REAL verify-template.md was written by the self-test' }
}

# ---- residue assertion AFTER the run (main-agent ruling 2026-09-23) ----------------------------
# Complement of the pre-run clean above: the clean protects the next reader even if THIS run is
# killed, while these assertions catch a run that finished but left junk behind.  The inventory is
# compared as a whole too, but an INCREASE only gets a note: another slice capturing a real
# screenshot while this run is open is expected, whereas a DECREASE means evidence disappeared
# during my window and must be reported.
$a1 = @(Get-ChildItem $shotsDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'gx-selftest-*' -or $_.Name -like 'ahrselftest*' }).Count
if ($a1 -ne 0) { $bad++; Note ('MISS  the self-test left ' + $a1 + ' probe png(s) in .ai-tmp/screenshots/') }
if (Test-Path $nextMd) { $bad++; Note 'MISS  the self-test left .ai-tmp/test/NEXT.md behind' }
$px1 = @(Get-ChildItem $shotsDir -File -ErrorAction SilentlyContinue).Count
if ($px1 -lt $px0) { $bad++; Note ('MISS  the screenshots inventory SHRANK during the self-test: ' + $px0 + ' -> ' + $px1 + ' (files disappeared)') }
Note ('  inventory self-check: probe-png-left=' + $a1 + ' / NEXT.md-left=' + (Test-Path $nextMd) + ' / screenshots ' + $px0 + ' -> ' + $px1)

Note ('===== gate-selftest summary: unmet-expectations=' + $bad + ' =====')
# window guardrail: CLOSE the window (see the header at the top of this file). A reader that
# saw a start row without this end row must treat its own gate reading as polluted.
[IO.File]::AppendAllText($winLog, ((Get-Date).ToString('s') + "`tend`tPID=" + $PID + "`tunmet-expectations=" + $bad + "`r`n"), (New-Object Text.UTF8Encoding($false)))
Write-Output ('WINDOW  gate-selftest END   ' + (Get-Date).ToString('s') + ' PID=' + $PID + ' unmet-expectations=' + $bad + ' -- the window is closed; gate readings taken after this row are trustworthy again')
exit $(if ($bad -gt 0) { 1 } else { 0 })
