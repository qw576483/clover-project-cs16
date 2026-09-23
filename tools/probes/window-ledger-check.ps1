# window-ledger-check.ps1 -- is a gate self-test / plan-injection window still OPEN?
#
# WHY THIS IS ITS OWN FILE (not inline in verify.ps1)
# --------------------------------------------------
# The window guard is consulted by every reader, so its pairing rules must be provable in BOTH
# directions: a closed window must stay silent, and a genuinely unclosed one must speak. Proving
# that with the logic inlined in verify.ps1 would mean MUTATING the shared ledger
# (.ai-tmp/test/gate-selftest-window.tsv) -- i.e. manufacturing a false warning for every other
# slice on the machine. With the `-Ledger` parameter the samples run on sandbox copies and the
# real ledger is only ever read.
#
# ROW SHAPES THAT REALLY EXIST (both must work -- half the ledger was invisible before):
#   A) written by gate-selftest.ps1 :  ISO-TIME <TAB> start <TAB> PID=12345
#   B) written by hand              :  start <TAB> ISO-TIME <TAB> slice BW-S-R2 verify.ps1 ...
#   plus `note` rows and rows whose stage token is `abort`.
#
# PAIRING RULES (main-agent ruling 2026-09-23, after a forced `abort` row degraded the whole
# team's readings):
#   * `abort` CLOSES a window exactly like `end`; before this, an aborted run left the window
#     counted OPEN forever => every later verify.ps1 carried "may be perturbed".
#   * rows WITH a PID pair by identity; rows WITHOUT a PID pair IN ORDER (oldest first), because
#     their free text differs between the start and the end row.
#   * counting-only pairing is gone: one surplus `end` used to cancel a later real `start`, so the
#     guard silently lost its warning.
#   * a >60 min old start whose PID is GONE gets a `STALE?` hint. We NEVER auto-close: measured
#     2026-09-23, "low CPU + no log growth for 15 s" was read as "dead" for a self-test that was
#     merely waiting on an external CLI (main agent retracted that ruling), so the alive-but-idle
#     case must stay silent while the PID-gone case may only be hinted at.
#
# Output: nothing when every window is closed, else one `NOTE` line (never a FAIL: a reader must
# not be turned red by somebody else's window).
param([string]$Ledger = '')

if ($Ledger -eq '' -or -not (Test-Path -LiteralPath $Ledger)) { exit 0 }

$startSeen  = @{}
$closedKeys = @{}
$startOrder = New-Object System.Collections.Generic.List[string]
$queueNoPid = New-Object System.Collections.Generic.List[string]
$rowNo = 0
# The ledger is SHARED: another process may hold it open while appending, and an unhandled read
# error here would propagate into every `verify.ps1` reading (measured 2026-09-23: the gate died
# after 10 lines and judged nothing).  Fail LOUD but SAFE: say the guard could not read, then exit
# cleanly so the rest of the gate still runs and the reader knows to look by hand.
$rows = @()
try { $rows = @([System.IO.File]::ReadAllLines($Ledger, [Text.Encoding]::UTF8)) }
catch {
  Write-Output 'NOTE the gate self-test window ledger could not be read (locked by another process) -- treat this reading with caution and check .ai-tmp/test/gate-selftest-window.tsv by hand'
  exit 0
}
foreach ($ln in $rows) {
  $rowNo++
  $l = [string]$ln
  if ($l.Trim().Length -eq 0 -or $l.TrimStart().StartsWith('#')) { continue }
  $c = @($l -split "`t")
  $stage = ''
  $at = ''
  foreach ($f in @($c[0], $c[1])) {
    if ($f -match '^(start|end|abort|note)$') { $stage = $Matches[1] }
    elseif ($f -match '^\d{4}-\d\d-\d\dT') { $at = $f }
  }
  if ($stage -eq '') { continue }
  # PID= must be a WHOLE FIELD, never a free-text mention.  Measured 2026-09-23 (my own slice-name
  # row): the end row read "…closed: unmet-expectations=2 (PID=127952 run 09:26:33-10:21:38)" and a
  # line-wide regex made it PID-bearing, so it closed the WRONG run and left the slice-name start
  # row open forever -- a guard that silently mispairs is worse than no guard.
  $km = $null
  foreach ($f in $c) { $mm = [regex]::Match($f.Trim(), '^PID=(\d+)$'); if ($mm.Success) { $km = $mm; break } }
  if ($stage -eq 'start') {
    $key = $(if ($km.Success) { 'PID=' + $km.Groups[1].Value } else { 'nopid#' + $rowNo })
    $startSeen[$key] = $at
    if (-not $startOrder.Contains($key)) { $startOrder.Add($key) }
    if (-not $km.Success) { $queueNoPid.Add($key) }
  } elseif ($stage -eq 'end' -or $stage -eq 'abort') {
    if ($km.Success) { $closedKeys['PID=' + $km.Groups[1].Value] = $true }
    elseif ($queueNoPid.Count -gt 0) { $closedKeys[$queueNoPid[0]] = $true; $queueNoPid.RemoveAt(0) }
  }
}
$openKeys = @($startOrder | Where-Object { -not $closedKeys.ContainsKey($_) })
if ($openKeys.Count -eq 0) { exit 0 }

$stale = ''
foreach ($k in $openKeys) {
  $pidM = [regex]::Match($k, '^PID=(\d+)$')
  if ($pidM.Success -and ($startSeen[$k] -match '^\d{4}-')) {
    $p = Get-Process -Id ([int]$pidM.Groups[1].Value) -ErrorAction SilentlyContinue
    if (($null -eq $p) -and (((Get-Date) - [datetime]$startSeen[$k]).TotalMinutes -gt 60)) {
      $stale = ' [STALE? start ' + $startSeen[$k] + ' has no live PID and is >60 min old -- NOT auto-closed, verify by hand]'
    }
  }
}
# wording kept as the literal the task book asks for (slice BW-G-R): a reader who does not open
# the ledger must still be told, in one line, that this reading may be perturbed.
Write-Output ('NOTE a gate self-test window is OPEN (started ' + [string]$startSeen[$openKeys[0]] + '; ' + $openKeys.Count + ' unclosed run(s))' + $stale + '; this reading may be intentionally perturbed -- see .ai-tmp/test/gate-selftest-window.tsv')
exit 0
