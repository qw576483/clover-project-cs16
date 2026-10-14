# normalize-registry-eol.ps1 -- one-shot CRLF -> LF normalization of the shared CS16 registry table
# (ca-hua / cha-yi-deng-ji .tsv) plus a byte-level audit readout.
#
# Run this ONLY after the team-lead has declared the table frozen (no other slice is writing it).
#
# WHY THE CODE IS ASCII-ONLY: PowerShell 5.1 decodes a BOM-less .ps1 as ANSI/GBK, so any CJK literal here
# would be mangled. The Chinese folder/file names are assembled from Unicode code points at runtime
# instead (see the table-path block below); that keeps this file pure ASCII, so it needs no BOM.
#
# WHAT IT PRINTS (the four items requested):
#   1. sha256 before / after
#   2. line count + CRLF / loneLF / loneCR counts (before -> after)
#   3. git diff --ignore-cr-at-eol --numstat -- <table>   (real changes, CR noise removed)
#      plus the plain git diff --numstat for contrast
#   4. idempotency: run it twice; the second run must report changed=false and an unchanged sha
#
# DEFAULT = DRY RUN (measure + print only, nothing written). Add -Apply to actually normalize.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\normalize-registry-eol.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\normalize-registry-eol.ps1 -Apply
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\normalize-registry-eol.ps1 -Project D:\other -Table D:\other\x.tsv
[CmdletBinding(PositionalBinding = $false)]
param(
  [string]$Project = 'C:\Work\Server\f-v2\clover-project-cs16',
  [string]$Table   = '',
  [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# table path: <Project>\<U+7B56 U+5212>\<U+5DEE U+5F02 U+767B U+8BB0>.tsv  (ca-hua / cha-yi-deng-ji)
if ([string]::IsNullOrEmpty($Table)) {
  $dir  = [string]([char]0x7B56) + [char]0x5212
  $name = [string]([char]0x5DEE) + [char]0x5F02 + [char]0x767B + [char]0x8BB0 + '.tsv'
  $Table = Join-Path (Join-Path $Project $dir) $name
}
if (-not (Test-Path -LiteralPath $Table)) { throw ('table not found: ' + $Table) }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Get-EolStats([string]$path) {
  $s = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
  $cr = 0; $lf = 0; $crlf = 0; $loneCr = 0
  for ($i = 0; $i -lt $s.Length; $i++) {
    $ch = $s[$i]
    if ($ch -eq "`r") {
      $cr++
      if (($i + 1 -lt $s.Length) -and ($s[$i + 1] -eq "`n")) { $crlf++ } else { $loneCr++ }
    } elseif ($ch -eq "`n") { $lf++ }
  }
  [pscustomobject]@{
    Bytes  = (Get-Item -LiteralPath $path).Length
    Lines  = @(($s -split "`n")).Count
    CRLF   = $crlf
    LoneLF = ($lf - $crlf)
    LoneCR = $loneCr
    CR     = $cr
    LF     = $lf
    Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
  }
}

function Get-RelPath([string]$project, [string]$table) {
  return $table.Substring($project.Length).TrimStart('\', '/').Replace('\', '/')
}

function Show-Stats([string]$tag, $st) {
  Write-Host ($tag + ': bytes=' + $st.Bytes + ' lines=' + $st.Lines +
              ' CRLF=' + $st.CRLF + ' loneLF=' + $st.LoneLF + ' loneCR=' + $st.LoneCR +
              ' (CR=' + $st.CR + ' LF=' + $st.LF + ')')
  Write-Host ($tag + ': sha256=' + $st.Sha256)
}

function Show-GitNumstat([string]$project, [string]$rel, [string]$label, [bool]$ignoreCr) {
  $argv = @('-C', $project, '--no-pager', 'diff')
  if ($ignoreCr) { $argv += '--ignore-cr-at-eol' }
  $argv += @('--numstat', '--', $rel)
  $out = (& git @argv 2>&1 | Out-String).Trim()
  Write-Host ('--- ' + $label + ' ---')
  Write-Host ($(if ([string]::IsNullOrEmpty($out)) { '(no diff)' } else { $out }))
}

Write-Host '=== normalize-registry-eol ==='
Write-Host ('table = ' + $Table)
Write-Host ('mode  = ' + $(if ($Apply) { 'APPLY (will write)' } else { 'DRY-RUN (nothing written; add -Apply)' }))
Write-Host ''

$before = Get-EolStats $Table
Show-Stats 'before' $before

$alreadyLf = (($before.CRLF -eq 0) -and ($before.LoneCR -eq 0))
if ($alreadyLf) {
  Write-Host 'ALREADY-LF: no CRLF and no lone CR -> nothing to normalize.'
} elseif (-not $Apply) {
  Write-Host ('WOULD-CHANGE: ' + $before.CRLF + ' CRLF + ' + $before.LoneCR +
              ' lone CR would become LF. Re-run with -Apply to write.')
}

if ($Apply -and (-not $alreadyLf)) {
  $s  = [IO.File]::ReadAllText($Table, [Text.Encoding]::UTF8)
  $s2 = $s.Replace("`r`n", "`n").Replace("`r", "`n")
  if ($before.LoneCR -gt 0) {
    Write-Host ('WARN: ' + $before.LoneCR + ' lone CR found -> converted to LF as well (report this if it looks wrong).')
  }
  # Whole-file replacement on a SHARED table: write a sibling temp then atomically replace, so a
  # concurrent reader only ever sees the old or the new file (never a half-written one).
  #
  # MEASURED DEFECT (2026-10-14, found by the positive control): [IO.File]::Move(src, dst, overwrite)
  # is a .NET Core 3.0+ overload -- under Windows PowerShell 5.1 (.NET Framework) it throws
  # 'Cannot find an overload for "Move"'. Use Replace(src, dst, backup), which exists since .NET 2.0 and
  # is atomic when both paths live on the same volume (they do: the temp is a sibling of the table).
  # The backup argument must be a REAL path: PowerShell coerces a $null argument to "" and .NET then
  # throws 'path is not of a legal form' (measured 2026-10-14, same day as the Move finding). So pass a
  # sibling backup name and delete both scratch files in the finally block.
  $tmp    = $Table + '.tmp-eol-normalize'
  $backup = $Table + '.tmp-eol-normalize.bak'
  [IO.File]::WriteAllText($tmp, $s2, $utf8NoBom)
  try {
    [IO.File]::Replace($tmp, $Table, $backup)
  } finally {
    foreach ($f in @($tmp, $backup)) {
      if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    }
  }
  Write-Host 'WRITTEN: CRLF -> LF, UTF-8 without BOM (via sibling temp + atomic Replace).'
}

$after = Get-EolStats $Table
Show-Stats 'after ' $after
$changed = ($before.Sha256 -ne $after.Sha256)
Write-Host ('changed = ' + $changed)
if (-not $Apply) {
  Write-Host 'IDEMPOTENCY: re-run with -Apply once, then re-run again -> the second run must print changed=false.'
} elseif ($changed) {
  Write-Host 'IDEMPOTENCY: run this script a SECOND time now -> it must print ALREADY-LF and changed=false.'
} else {
  Write-Host 'IDEMPOTENCY: OK (this run changed nothing -> the file is already canonical LF).'
}

$rel = Get-RelPath $Project $Table
Write-Host ''
Show-GitNumstat $Project $rel 'git diff --ignore-cr-at-eol --numstat (real changes, CR noise removed)' $true
Write-Host ''
Show-GitNumstat $Project $rel 'plain git diff --numstat (for contrast: the CRLF flip inflates this)' $false
Write-Host ''
Write-Host 'DONE.'
