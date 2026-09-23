# judgement asset (tools/probes/): two-sample self-test for the checks added by slice BW-S
#   * tools/probes/section-ownership.py   (section ownership + block purity + header warning)
#   * tools/probes/diffs-align.py         (allowed-differences cell alignment)
#   * enumerate-entities.py --inject       (header warning injection + diff-section write guard)
# Rule: every new/changed check needs  (a) a known-good sample that PASSES, (b) an injected
# defect that FAILS.  Every fixture is a COPY under .ai-tmp/test/bws-st/ (anti-gaming 5.3:
# never tamper with a shared artefact); the real acceptance table is hashed before and after.
# NOTE 1: ASCII-only OUTSIDE comments on purpose -- verify.ps1 item `ps1-ascii` measures exactly
#         that (PS 5.1 reads a BOM-less .ps1 as ANSI; a non-ASCII literal silently misbehaves).
#         Chinese paths are therefore built from code points, as done elsewhere in this repo.
# NOTE 2: this file carries a UTF-8 BOM (comments do contain Chinese).
param([string]$Project = '')
$ErrorActionPreference = 'Continue'
$env:PYTHONDONTWRITEBYTECODE = '1'
if ($Project -eq '') { $Project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
Set-Location $Project

# -- plan-dir / file names, built from code points (see NOTE 1) --------------------------------
$cPlan   = ([char[]]@(0x7B56,0x5212) -join '')                     # plan dir
$cAccept = ([char[]]@(0x9A8C,0x6536,0x8868) -join '') + '.md'      # acceptance table
$cCov    = ([char[]]@(0x8986,0x76D6,0x77E9,0x9635,0x5224,0x5B9A) -join '') + '.fragment.md'
$PROD  = Join-Path $Project ($cPlan + '\' + $cAccept)
$COVF  = Join-Path $Project ($cPlan + '\' + $cCov)
$ST    = Join-Path $Project '.ai-tmp\test\bws-st'
$LOG   = Join-Path $Project '.ai-tmp\test\bws-selftests.txt'
$enc   = New-Object Text.UTF8Encoding($false)

python tools\probes\bws-make-fixtures.py | Out-Null
$h0 = (Get-FileHash -Algorithm SHA256 $PROD).Hash
$hdr = 'slice BW-S two-sample self-test (good sample PASSES / injected defect FAILS)' + "`r`n" + '# generated ' + [DateTime]::Now.ToString('o') + "`r`n" + '# REAL acceptance table sha256 = ' + $h0.Substring(0,24) + "`r`n`r`n"
[IO.File]::WriteAllText($LOG, $hdr, $enc)

function Rec([string]$id, [string]$desc, [string]$cmd) {
  $out = Invoke-Expression $cmd 2>&1 | Out-String
  $code = $LASTEXITCODE
  $keep = ($out -split "`r?`n" | Where-Object { $_ -match 'RESULT|FAIL |OK   |\[inject\]|real_norm|drifted ids' } |
           Select-Object -First 4) -join "`r`n      "
  [IO.File]::AppendAllText($LOG, ("[$id] $desc`r`n  cmd  : $cmd`r`n  exit : $code`r`n      $keep`r`n`r`n"), $enc)
  Write-Output "[$id] exit=$code  $desc"
}

Rec 'S1'  'hand-written line inside the COVERAGE block (vs the REAL rebuild output) -> must FAIL' "python tools\probes\section-ownership.py --check --product `"$ST\prod-handedit.md`" --fragment `"$COVF`""
Rec 'S2'  'copy isomorphic to the current tree -> must PASS' "python tools\probes\section-ownership.py --check --product `"$ST\prod-drift.md`" --fragment `"$ST\cov-ok.md`""
Rec 'S3'  'product carries one unregistered section -> must FAIL' "python tools\probes\section-ownership.py --check --product `"$ST\prod-extrahead.md`" --fragment `"$ST\cov-ok.md`""
Rec 'S4'  'a stale rule (matches no section) in the ownership list -> must FAIL' "python `"$ST\bws-st-stale.py`""
Rec 'S5'  'header ownership warning hand-edited -> must FAIL' "python tools\probes\section-ownership.py --check --product `"$ST\prod-warn-edited.md`" --fragment `"$ST\cov-ok.md`""
Rec 'S6'  'header ownership warning deleted -> must FAIL' "python tools\probes\section-ownership.py --check --product `"$ST\prod-warn-dropped.md`" --fragment `"$ST\cov-ok.md`""
Rec 'S7'  'diffs-align render vs render (known-good sample) -> must PASS' "python tools\probes\diffs-align.py `"$ST\diffs-render.md`" `"$ST\diffs-render.md`""
Rec 'S8'  'diffs-align render vs injected defect (row 67 gains one clause) -> must FAIL' "python tools\probes\diffs-align.py `"$ST\diffs-render.md`" `"$ST\diffs-defect.md`""
Rec 'S9'  'injection guard (positive): diffs section already in sync -> takes the IN-SYNC branch' "python tools\probes\enumerate-entities.py --inject `"--spec-path=$ST\prod-synced.md`" 2>&1 | Select-String -Pattern 'inject\]'"
Rec 'S10' 'injection guard (negative): copy with drift -> must NOT inject' "python tools\probes\enumerate-entities.py --inject `"--spec-path=$ST\prod-drift.md`" 2>&1 | Select-String -Pattern 'inject\]|drifted ids'"
Rec 'S11' 'the REAL product --check -> must PASS' "python tools\probes\section-ownership.py --check"
Rec 'S12' 'REAL product hash (must be untouched by every self-test above)' "python -c `"import hashlib;print('sha256='+hashlib.sha256(open(r'$PROD','rb').read()).hexdigest()[:24])`""

$h1 = (Get-FileHash -Algorithm SHA256 $PROD).Hash
$foot = '# end: REAL acceptance table sha256 = ' + $h1.Substring(0,24) + '  untouched = ' + ($h0 -eq $h1) + "`r`n"
[IO.File]::AppendAllText($LOG, $foot, $enc)
Write-Output "REAL product untouched = $($h0 -eq $h1)  ($($h0.Substring(0,24)))"
Write-Output "log = $LOG"
