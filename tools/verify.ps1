# ============================================================================
#  External delivery gate -- numbered checks (spec: skill reference/verify-template.md).
#  One PASS / FAIL / HUMAN-ONLY line per check, then a summary; exit 1 if FAIL.
#
#  NOTE: this file must stay ASCII-only. Windows PowerShell 5.1 parses .ps1 as
#  ANSI/GBK when the file has no UTF-8 BOM, so a Chinese path or keyword written
#  literally here can break parsing or make -match fail silently. Every Chinese
#  token below is therefore built from code points.
#
#  Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1
#
#  Adaptations to this project (both follow the template, see its notes):
#   * check 2 -- comment-only hits are filtered and the remaining hits are
#     cross-checked against the "allowed differences" registry of the acceptance
#     table (the registry is the only legal place for exceptions).
#   * check 6 -- freshness is judged PER ACCEPTANCE ROW: a shot is void only
#     when it is older than ITS OWN row's implementation file, never when some
#     unrelated file elsewhere in the project changed (see the note there).
#   * checks 14/15 -- limited to the 24h window (template check 11: a check that
#     reports historical residue as a violation is worse than no check at all).
#   * check 12 -- counts the rows of the A-E acceptance sections. This file also
#     holds two other digit-keyed tables (the differences registry and its own
#     self-check summary); they are not acceptance rows and must not be scored.
#   * check 15 -- "implementation" means hand-written text sources (.cs) only;
#     the Resources tree's ~2k generator outputs are excluded, see the note there.
# ============================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$script:fail = 0
$script:human = 0
$script:hrHits = 0

function Say([string]$status, [string]$name, [string]$detail) {
  Write-Output ("{0,-11} {1}  {2}" -f $status, $name, $detail)
}
function Sub([string]$text) { Write-Output ("            " + $text) }
function Read-Text([string]$path) { return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) }

# Slice a markdown section: from the '#' heading that contains $title up to the
# next '## ' heading. Returns the section's lines (empty array when absent).
function Get-SectionLines([string]$text, [string]$title) {
  $ls = $text -split "`r?`n"
  $s = -1; $e = $ls.Count
  for ($i = 0; $i -lt $ls.Count; $i++) {
    if ($s -lt 0) { if ($ls[$i].StartsWith('#') -and $ls[$i].Contains($title)) { $s = $i } }
    elseif ($ls[$i].StartsWith('## ')) { $e = $i; break }
  }
  if ($s -lt 0) { return @() }
  return @($ls[$s..($e - 1)])
}

# ---- non-ASCII tokens, built from code points (keeps this file ASCII-only) --
$planDir   = Join-Path $root ([char[]]@(0x7B56,0x5212) -join '')                             # ce hua
$specTable = Join-Path $planDir ((([char[]]@(0x9A8C,0x6536,0x8868)) -join '') + '.md')       # yan shou biao
$refTable  = Join-Path $planDir ((([char[]]@(0x5BF9,0x7167,0x8868)) -join '') + '.md')       # dui zhao biao
$cNumeric  = ([char[]]@(0x6570,0x503C,0x7C7B) -join '')                                      # numeric class
$cVisual   = ([char[]]@(0x8868,0x73B0,0x7C7B) -join '')                                      # visual class
$cProg     = ([char[]]@(0x8FDB,0x5EA6) -join '')                                             # progress
$cHand     = ([char[]]@(0x4EA4,0x63A5) -join '')                                             # handover
$cDiffSec  = ([char[]]@(0x5141,0x8BB8,0x7684,0x5DEE,0x5F02) -join '')                        # allowed differences
$cWhy      = ([char[]]@(0x4E3A,0x4EC0,0x4E48) -join '')                                      # why
$cSrc      = ([char[]]@(0x51FA,0x5904) -join '')                                             # where from
$cWhen     = ([char[]]@(0x4F55,0x65F6) -join '')                                             # when removed

# ---- fixed locations -------------------------------------------------------
$codeDir = Join-Path $root 'client\Assets\Scripts'
$editorDir = Join-Path $root 'client\Assets\Editor'
# Evidence shots are ONE-OFF artifacts (SKILL 1.8 item 6): they live under
# .ai-tmp/screenshots/ (gitignored, never in client/Assets/**).
$shotDir = Join-Path $root '.ai-tmp\screenshots'
$tmpRoot = Join-Path $root '.ai-tmp'
$logPath = Join-Path $root '.ai-tmp\test\dispatch-log.tsv'
$wsRoot  = Split-Path $root -Parent
$cut24   = (Get-Date).AddHours(-24)

function Invoke-Checks {
  $specTxt = ''
  if (Test-Path $specTable) { $specTxt = Read-Text $specTable }

  # The real acceptance body = the digit-keyed rows inside the A-E sections.
  # The other digit-keyed tables in that file are the differences registry and
  # the file's own self-check summary, so they must not be counted as rows.
  $accRows = @()
  $inAcc = $false
  foreach ($ln in @($specTxt -split "`r?`n")) {
    if ($ln.StartsWith('## ')) { $inAcc = ($ln -match '^##\s+[A-E]\.'); continue }
    if ($inAcc -and $ln -match '^\|\s*[A-Z]?\d+\s*\|') { $accRows += $ln }
  }

  # --- 1) stray one-off .cs outside the sanctioned temp area ----------------
  $n = @(Get-ChildItem $root -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
         Where-Object { $_.FullName -match '\\(_dev|_assets_src|_assets_tmp)\\' }).Count
  if ($n -eq 0) { Say 'PASS' 'stray-temp-files' '0 hits' }
  else { $script:fail++; Say 'FAIL' 'stray-temp-files' "$n stray .cs under _dev/_assets_src/_assets_tmp" }

  # --- 2) hard-rule hits: every non-comment hit must sit in the registry ----
  # ('Input\.' is intentionally not in the pattern list: Select-String is
  #  case-insensitive here, so it would flag every local variable named 'input'.)
  $pat = 'Debug\.Log', 'Resources\.Load', 'PlayerPrefs', 'GameObject\.Find', 'FindObjectOfType', 'Instantiate\('
  $hits = @(Get-ChildItem $codeDir -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
            Select-String -Pattern $pat -Encoding UTF8)
  $diffLines = @(Get-SectionLines $specTxt $cDiffSec)
  $regTxt = ($diffLines -join "`n")
  $regFiles = @([regex]::Matches($regTxt, '([A-Za-z_0-9]+)\.cs') |
                ForEach-Object { $_.Groups[1].Value + '.cs' } | Sort-Object -Unique)
  $regLines = @()
  foreach ($m in [regex]::Matches($regTxt, '([A-Za-z_0-9]+)\.cs:([0-9]+(?:/[0-9]+)*)')) {
    foreach ($ln in @($m.Groups[2].Value.Split('/'))) { $regLines += ($m.Groups[1].Value + '.cs:' + $ln) }
  }
  $regLines = @($regLines | Sort-Object -Unique)

  $real = @(); $unreg = @()
  foreach ($group in @($hits | Group-Object Path)) {
    $fileLines = @([System.IO.File]::ReadAllLines($group.Name))
    $short = $group.Name.Substring($root.Length).TrimStart('\', '/')
    $base = Split-Path $group.Name -Leaf
    foreach ($h in @($group.Group)) {
      $txt = ''
      if ($h.LineNumber - 1 -lt $fileLines.Count) { $txt = $fileLines[$h.LineNumber - 1].TrimStart() }
      if ($txt.StartsWith('//') -or $txt.StartsWith('*') -or $txt.StartsWith('/*')) { continue }
      $real += $h
      $key = $base + ':' + $h.LineNumber
      if ($regLines -contains $key) { Sub ($short + ':' + $h.LineNumber + '   registered') }
      elseif ($regFiles -contains $base) { Sub ($short + ':' + $h.LineNumber + '   registered (by file; the registry lists older line numbers)') }
      else { $unreg += $h; Sub ($short + ':' + $h.LineNumber + '   UNREGISTERED') }
    }
  }
  $script:hrHits = $real.Count
  if ($unreg.Count -eq 0) {
    Say 'PASS' 'hard-rules' "$($real.Count) non-comment hits out of $($hits.Count) raw hits; all covered by the differences registry"
  } else {
    $script:fail++
    Say 'FAIL' 'hard-rules' "$($unreg.Count) hit(s) with no row in the differences registry"
  }

  # --- 3) acceptance table: table-body row count (aggregate must equal it) --
  if ($specTxt.Length -gt 0) {
    $rows = ([regex]::Matches($specTxt, '(?m)^\|\s*[A-Z]?\d+\s*\|')).Count
    # Item 3 used to end in "human cross-check" and bump $script:human, i.e. a HUMAN-ONLY
    # verdict with nothing behind it.  Item 23 (acceptance-sums) now computes the very
    # comparison this sentence promises: every aggregate the file states about itself is
    # reconciled against the table BODY.  So the cross-check is no longer dangling -- it is
    # delegated to item 23 (a rule that cannot be tested red is not a gate; SKILL 0.6).
    Say 'PASS' 'acceptance-table' "$($accRows.Count) acceptance rows (of $rows digit-keyed rows); the summary numbers inside that file must equal the acceptance count - cross-checked by item 23 (acceptance-sums), which reconciles every aggregate against the table body"
  } else {
    $script:fail++
    Say 'FAIL' 'acceptance-table' ("missing: " + $specTable)
  }

  # --- 4) every "allowed differences" row carries why / source / expiry -----
  if ($specTxt.Length -gt 0) {
    $block = @($diffLines | Where-Object { $_.TrimStart().StartsWith('|') })
    $dataRows = @($block | Where-Object { $_.TrimStart() -notmatch '^\|[\s\-:|]+\|$' })
    $header = ''
    $body = @()
    if ($dataRows.Count -gt 0) {
      $header = $dataRows[0]
      if ($dataRows.Count -gt 1) { $body = @($dataRows[1..($dataRows.Count - 1)]) }
    }
    $bad = @()
    foreach ($r in $body) {
      $cells = @($r.Split('|') | ForEach-Object { $_.Trim() })
      if ($cells.Count -lt 6 -or $cells[3].Length -eq 0 -or $cells[4].Length -eq 0 -or $cells[5].Length -eq 0) { $bad += $r.Trim() }
    }
    if ($block.Count -eq 0) {
      if ($script:hrHits -eq 0) { Say 'PASS' 'allowed-diff' 'registry empty (consistent: hard-rule hits are 0)' }
      else { $script:fail++; Say 'FAIL' 'allowed-diff' "registry empty but $($script:hrHits) hard-rule hit(s) exist" }
    } elseif (-not ($header.Contains($cWhy) -and $header.Contains($cSrc) -and $header.Contains($cWhen))) {
      $script:fail++
      Say 'FAIL' 'allowed-diff' 'header lacks the why (col 3) / source (col 4) / expiry (col 5) columns'
    } elseif ($bad.Count -gt 0) {
      $script:fail++
      Say 'FAIL' 'allowed-diff' "$($bad.Count) row(s) with an empty why/source/expiry cell"
      $bad | ForEach-Object { Sub $_ }
    } else {
      Say 'PASS' 'allowed-diff' "$($body.Count) rows, each with why + source + expiry"
    }
  }

  # --- 5) every path / file:line cited by the acceptance table exists -------
  if ($specTxt.Length -gt 0) {
    $missing = @()
    $pngs = @([regex]::Matches($specTxt, '[0-9A-Za-z_\-]+\.png') | ForEach-Object { $_.Value } | Sort-Object -Unique)
    foreach ($p in $pngs) { if (-not (Test-Path (Join-Path $shotDir $p))) { $missing += ('shot: ' + $p) } }
    $refs = @([regex]::Matches($specTxt, '[0-9A-Za-z_/\\.]+\.cs:[0-9]+(?:/[0-9]+)*') |
              ForEach-Object { $_.Value } | Sort-Object -Unique)
    $byName = @{}
    foreach ($r in $refs) {
      $i = $r.IndexOf('.cs:') + 3
      $rel = $r.Substring(0, $i).Replace('\', '/').TrimStart('.', '/')
      $nums = @($r.Substring($i + 1).Split('/'))
      $cand = @()
      foreach ($prefix in @('', 'client/Assets/', 'client/Assets/Scripts/')) {
        $cand += (Join-Path $root (($prefix + $rel) -replace '/', '\'))
      }
      $found = @($cand | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
      if ($found.Count -eq 0) {
        $bn = Split-Path $rel -Leaf
        if (-not $byName.ContainsKey($bn)) {
          # keep plain strings here: a FileInfo would stringify to the bare file
          # name and then be resolved against the process CWD, not the project.
          $byName[$bn] = @(Get-ChildItem $codeDir -Recurse -Filter $bn -File -ErrorAction SilentlyContinue |
                           ForEach-Object { $_.FullName })
        }
        $found = @($byName[$bn] | Select-Object -First 1)
      }
      if ($found.Count -eq 0) { $missing += ('code: ' + $rel); continue }
      $total = @([System.IO.File]::ReadAllLines($found[0])).Count
      foreach ($ln in $nums) {
        if ([int]$ln -gt $total) { $missing += ('code: ' + $r + ' (but that file has only ' + $total + ' lines)') }
      }
    }
    if ($missing.Count -eq 0) {
      Say 'PASS' 'screenshot-refs' "$($pngs.Count) screenshot refs + $($refs.Count) file:line refs all resolve"
    } else {
      $script:fail++
      Say 'FAIL' 'screenshot-refs' "$($missing.Count) citation(s) do not resolve"
      $missing | ForEach-Object { Sub $_ }
    }
  }

  # --- 6) evidence freshness, judged PER ACCEPTANCE ROW ---------------------
  # WHY this changed (it used to be "every shot vs the project's last code
  # edit anywhere", which voided 44 of 54 shots the moment a single unrelated
  # .cs file was touched):
  #   clover-engine SKILL section 1.11 item 6 / 1.12 item 3 / 1.13 item 6, verbatim:
  #     "the void scope = only the rows this change can really affect"
  #     "NOT: any file in the project changes -> everything is void"
  #     "make the affected rows / screens into a scope table, and let gate item 6
  #      judge expiry against that same table -- rule and check share one source"
  # So the row is the unit: take each A-E acceptance row (the same row set as
  # check 12), take the shots THAT ROW references (backticked *.png names) and
  # that row's own code (backticked file:line refs, class names XxxPanel /
  # XxxModule / CsXxx, or *.cs file names -- resolved to a real .cs under
  # Scripts / Editor), and require, per row, that every shot that row cites is
  # at least as new as that row's own (newest) implementation file.
  # Rows whose implementation file cannot be resolved, and rows that cite no
  # shot at all, stay HUMAN-ONLY and must never FAIL: the two mistakes are not
  # equivalent, and a check that only ever reports red is worse than no check
  # (SKILL section 1.11 item 11). Nothing is relaxed: every comparable
  # (row, shot) pair is still scored.
  $shotFiles = @{}
  if (Test-Path $shotDir) {
    foreach ($f in @(Get-ChildItem $shotDir -Filter *.png -File -ErrorAction SilentlyContinue)) {
      $shotFiles[$f.Name.ToLower()] = $f
    }
  }
  if ((Test-Path $shotDir) -and ($shotFiles.Count -gt 0) -and ($accRows.Count -gt 0)) {
    $csIndex = @{}
    foreach ($d in @($codeDir, $editorDir)) {
      if (-not (Test-Path $d)) { continue }
      foreach ($f in @(Get-ChildItem $d -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue)) {
        $k = $f.Name.ToLower()
        if (-not $csIndex.ContainsKey($k)) { $csIndex[$k] = @() }
        $csIndex[$k] += $f.FullName
      }
    }
    $clsPat = '([A-Za-z_][A-Za-z0-9_]*Panel|[A-Za-z_][A-Za-z0-9_]*Module|Cs[A-Za-z0-9_]+)'
    $cmpRows = 0; $cmpPairs = 0; $noShotRows = 0; $noImplRows = 0; $goneShots = 0
    $void = @()
    foreach ($row in $accRows) {
      $rid = ($row -split '\|')[1].Trim()
      $tokens = @([regex]::Matches($row, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value })

      $shots = @()
      foreach ($t in $tokens) {
        if ($t -match '\.png$') {
          $bn = ($t -replace '.*[\\/]', '').Trim()
          if (($bn.Length -gt 0) -and ($shots -notcontains $bn)) { $shots += $bn }
        }
      }
      if ($shots.Count -eq 0) { $noShotRows++; continue }

      $names = @()
      foreach ($t in $tokens) {
        foreach ($m in [regex]::Matches($t, '([A-Za-z_][A-Za-z0-9_]*\.cs)')) {
          $b = $m.Groups[1].Value.ToLower()
          if ($names -notcontains $b) { $names += $b }
        }
        foreach ($m in [regex]::Matches($t, $clsPat)) {
          $b = ($m.Groups[1].Value + '.cs').ToLower()
          if ($names -notcontains $b) { $names += $b }
        }
      }
      $impls = @()
      foreach ($b in $names) { if ($csIndex.ContainsKey($b)) { $impls += @($csIndex[$b]) } }
      $impls = @($impls | Sort-Object -Unique)
      if ($impls.Count -eq 0) { $noImplRows++; continue }

      $newest = @($impls | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending)[0]
      $tImpl = (Get-Item -LiteralPath $newest).LastWriteTime
      $cmpRows++
      foreach ($p in $shots) {
        if (-not $shotFiles.ContainsKey($p.ToLower())) { $goneShots++; continue }
        $sf = $shotFiles[$p.ToLower()]
        $cmpPairs++
        if ($sf.LastWriteTime -lt $tImpl) {
          $void += ('row ' + $rid + ': shot ' + $sf.Name + ' (' + $sf.LastWriteTime.ToString('MM-dd HH:mm') +
                   ') is older than that row implementation file ' + (Split-Path $newest -Leaf) +
                   ' (' + $tImpl.ToString('MM-dd HH:mm') + ')')
        }
      }
    }
    if (($cmpRows -eq 0) -or ($cmpPairs -eq 0)) {
      # Never PASS on an empty comparison: an unmatched row is unjudged, not fresh.
      $script:human++
      Say 'HUMAN-ONLY' 'evidence-freshness' "no (row,shot) pair could be compared (rows without a shot: $noShotRows; rows without a resolvable implementation file: $noImplRows; cited shot names that do not resolve to a file: $goneShots) - needs a manual check"
    } elseif ($void.Count -eq 0) {
      Say 'PASS' 'evidence-freshness' "$cmpRows acceptance row(s) / $cmpPairs (row,shot) pair(s) compared per row: every cited shot is at least as new as its own row implementation file; $noShotRows row(s) without a shot and $noImplRows row(s) without a resolvable implementation file are HUMAN-ONLY (SKILL 1.11 item 11); $goneShots unroutable shot name(s) are left to screenshot-refs"
    } else {
      $script:fail++
      Say 'FAIL' 'evidence-freshness' "$($void.Count) of $cmpPairs (row,shot) pair(s) are stale => only those rows are void, the rest stay valid"
      $void | ForEach-Object { Sub $_ }
    }
  } else { $script:human++; Say 'HUMAN-ONLY' 'evidence-freshness' 'screenshot dir, screenshots, acceptance rows or sources missing - needs a manual check' }

  # --- 7) the one-command re-check entry point itself ----------------------
  # Item name aligned to the template's `verify-entry` (slice AI; name only -- the
  # judgement is unchanged and stays strict): the entry script must be
  # on disk, non-empty, AND its two companion scripts (the ones copied from the
  # skill by SKILL 0.7 item 1) must be there too -- a bare "I am running, so I
  # exist" line could never be tested red from inside itself, which is why the
  # entry SURFACE (3 scripts) is what gets judged.  The red sample lives in
  # tools/probes/gate-selftest.ps1 (hide tools/env-check.ps1 => FAIL).
  $selfPath = Join-Path $root 'tools\verify.ps1'
  $companions = @()
  foreach ($cp in @('tools\gate-sync.ps1', 'tools\env-check.ps1')) {
    $pp = Join-Path $root $cp
    if (-not (Test-Path $pp)) { $companions += $cp }
    elseif ((Get-Item $pp).Length -le 0) { $companions += ($cp + ' (empty)') }
  }
  if (-not (Test-Path $selfPath)) {
    $script:fail++
    Say 'FAIL' 'verify-entry' 'tools\verify.ps1 is not on disk -- the one-command re-check entry is missing'
  } elseif ($companions.Count -gt 0) {
    $script:fail++
    Say 'FAIL' 'verify-entry' ('entry surface incomplete, missing/empty: ' + ($companions -join ', '))
  } else {
    Say 'PASS' 'verify-entry' ('tools\verify.ps1 executed (' + (Get-Item $selfPath).Length + ' bytes) with both companion scripts present (gate-sync.ps1 / env-check.ps1)')
  }

  # --- 8) original-value reference table -----------------------------------
  if (Test-Path $refTable) { Say 'PASS' 'reference-table' $refTable }
  else { $script:fail++; Say 'FAIL' 'reference-table' ("missing: " + $refTable + " -- every replicated element needs original / ours / delta") }

  # --- 8b) reference table: every cited carrier reachable OR registered ------
  # WHY (slice AX, 2026-09-22): item 8 above only ever asserted that the table FILE
  # exists -- it never looked INSIDE it, and item 5 (screenshot-refs) resolves the
  # ACCEPTANCE table's citations only.  So every dangling carrier path written in the
  # reference table was untested for the whole life of the project: measured that day,
  # section 0 alone cited 4 carrier paths that were not on disk, one of them labelled
  # `Test-Path = True`.  SKILL 0.6: a rule that cannot be tested red is not a gate.
  # Judgement (computable, no eyeball): tools/probes/scan-reftable-refs.py resolves every
  # yuan-ban-zi-yuan path / CL path / `file:line` citation (line <= file line count,
  # 0x offset <= file size) and FAILs on any carrier that is neither on disk NOR
  # registered (with the four elements what / why / source / expiry) in the
  # plan-dir carrier-reachability registry (path built from code points below).
  # A registry row whose carrier is reachable again FAILs too (a silencer left behind
  # is worse than no gate at all).
  # Scope (slice BB, 2026-09-22): the reference table AND the differences registry
  # (ce hua/cha yi deng ji.tsv).  The registry cites carriers and `file:line` in the very
  # same two shapes, and nothing resolved them before -- measured that day it carried 10+
  # carriers under `yuan-ban/cs16src/cs16game/...` that are not on disk, so that whole class
  # lived outside the gate.  Widening the scope can only add failures, never remove one.
  # The acceptance table stays item 5's territory, otherwise the same citation is counted
  # twice.  The two-sample proof for the NEW scope lives in tools/probes/gate-selftest.ps1
  # section 6 (inject a dangling citation inside the registry => FAIL, restore => PASS).
  # The two-sample proof lives in tools/probes/gate-selftest.ps1 (inject a dangling path
  # => FAIL, restore => PASS).
  $cCarrierReg = ([char[]]@(0x8F7D, 0x4F53, 0x53EF, 0x8FBE, 0x6027, 0x767B, 0x8BB0) -join '') + '.tsv'
  $refScan = Join-Path $root 'tools\probes\scan-reftable-refs.py'
  if (-not (Test-Path $refScan)) {
    $script:fail++
    Say 'FAIL' 'reference-table-refs' ('missing ' + $refScan + ' -- the reference table is unverifiable')
  } elseif ($null -eq (Get-Command python -ErrorAction SilentlyContinue)) {
    $script:fail++
    Say 'FAIL' 'reference-table-refs' 'python is not on PATH -- cannot run scan-reftable-refs.py'
  } else {
    $refTmp = Join-Path $tmpRoot 'verify-reftable-refs.txt'
    & python $refScan | Set-Content -Encoding UTF8 $refTmp
    $refRc = $LASTEXITCODE
    $refOut = @(Get-Content $refTmp -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 })
    $refLast = if ($refOut.Count -gt 0) { $refOut[$refOut.Count - 1] } else { '(no output)' }
    $refHead = if ($refOut.Count -gt 0) { $refOut[0] } else { '(no output)' }
    if ($refRc -eq 0) {
      Say 'PASS' 'reference-table-refs' $refLast
      Sub $refHead
      $refOut | Where-Object { $_ -match '^\s+index:|^\s+reachable-or-registered:' } | Select-Object -First 3 | ForEach-Object { Sub $_.Trim() }
    } else {
      $script:fail++
      Say 'FAIL' 'reference-table-refs' $refLast
      Sub $refHead
      $refOut | Where-Object { $_ -match '^\s+(DANGLING|STALE-REGISTRY)' } | ForEach-Object { Sub $_.Trim() }
      Sub ('registry = ' + (Join-Path $planDir $cCarrierReg) + ' -- every dangling carrier needs a row with what / why / source / expiry')
    }
  }

  # --- 9) handover / progress documents must not exist ---------------------
  $bad9 = @(Get-ChildItem $root -Recurse -Filter *.md -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\Library\\' } |
            Where-Object { $_.Name -like 'NEXT*' -or $_.Name.Contains($cProg) -or $_.Name.Contains($cHand) } |
            ForEach-Object { $_.Name })
  if ($bad9.Count -eq 0) { Say 'PASS' 'no-handoff-docs' 'none' }
  else { $script:fail++; Say 'FAIL' 'no-handoff-docs' ($bad9 -join ', ') }

  # --- 10) engine self-name: the literal `clover-engine` must exist in sources ---
  # This item is the COMPUTABLE half only: the literal is present in the sources.
  # The rendered verdict (the home-screen line, case-exact, bottom-most on screen)
  # is judged by item 25 `home-credit-rendered`, which reads a runtime UI-tree dump
  # taken from a REAL Play session -- so it belongs there, not here.
  #
  # Until slice AF this branch ALSO bumped $script:human UNCONDITIONALLY in the
  # success path, i.e. a check whose computable half had already PASSED still
  # emitted a HUMAN-ONLY verdict -- a dangling verdict with nothing behind it, and
  # moreover a DUPLICATE of item 25's judgement.  SKILL 0.5/0.6: a rule that cannot
  # be tested red is not a gate ("a system prompt is a request, a hook is a
  # guarantee").  Ruling (slice AG, main-agent decision): the literal item PASSES /
  # FAILS on its own, and the rendered half is delegated to item 25 -- so a passing
  # literal no longer manufactures a HUMAN-ONLY.  This is why the gate total went
  # from HUMAN-ONLY=1 to HUMAN-ONLY=0 without any judgement being dropped: the
  # judgement moved to the item that can actually make it (item 25).
  $brand = @(Get-ChildItem $codeDir -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
             Select-String -Pattern 'clover-engine' -Encoding UTF8)
  if ($brand.Count -eq 0) {
    $script:fail++
    Say 'FAIL' 'engine-credit' 'the literal clover-engine appears nowhere in the client sources'
  } else {
    $mentions = @(Get-ChildItem $codeDir -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
                  Select-String -Pattern 'Engine' -Encoding UTF8).Count
    Say 'PASS' 'engine-credit' "literal clover-engine present ($($brand.Count) line(s)); the rendered home-screen signature is judged by item 25 home-credit-rendered ($mentions lines mention Engine)"
  }

  # --- 11) scope / time window: historical residue must not be reported ----
  Say 'PASS' 'scoping-window' "checks 13/14/15 judge only traces from inside the 24h window (since $($cut24.ToString('MM-dd HH:mm'))); older residue is printed as a note, never as FAIL"

  # --- 12) every acceptance row must carry a category tag ------------------
  # Category is the only criterion for "does this row need a contact sheet?", so
  # it has to live in the table rather than in the executor's head.
  if ($specTxt.Length -gt 0) {
    $noCat = @($accRows | Where-Object { -not ($_.Contains($cNumeric) -or $_.Contains($cVisual)) })
    if ($noCat.Count -eq 0) { Say 'PASS' 'row-category' "$($accRows.Count) acceptance rows, every one of them tagged" }
    else { $script:fail++; Say 'FAIL' 'row-category' "$($noCat.Count) of $($accRows.Count) acceptance rows carry no numeric/visual category tag (SKILL section 2)" }
  }

  # --- 13) one-off artifacts outside the project (only .ai-tmp/test/ is ok) -
  $rootName = Split-Path $root -Leaf
  $projTokens = @($rootName)
  if ($rootName.StartsWith('clover-project-')) { $projTokens += $rootName.Substring(15) }
  $brainZones = @()
  if ($env:APPDATA) {
    $brainZones = @(Get-ChildItem (Join-Path $env:APPDATA '*\User\globalStorage\*\brain') -Directory -ErrorAction SilentlyContinue |
                    ForEach-Object { $_.FullName })
  }
  $esc = @()
  foreach ($t in $projTokens) {
    $esc += @(Get-ChildItem $wsRoot -File -Filter ("*" + $t + "*") -ErrorAction SilentlyContinue |
              Where-Object { $_.CreationTime -gt $cut24 })
    foreach ($z in $brainZones) {
      $esc += @(Get-ChildItem $z -Recurse -File -Filter ("*" + $t + "*") -ErrorAction SilentlyContinue |
                Where-Object { $_.CreationTime -gt $cut24 })
    }
  }
  $esc = @($esc | Sort-Object FullName -Unique)
  if ($esc.Count -eq 0) { Say 'PASS' 'no-escaped-artifacts' "0 hits (workspace root + $($brainZones.Count) host artifact dir(s))" }
  else {
    $script:fail++
    Say 'FAIL' 'no-escaped-artifacts' "$($esc.Count) project file(s) created outside the project => move them into .ai-tmp/test/"
    $esc | ForEach-Object { Sub $_.FullName }
  }

  # --- 14) sampler self-check: .ai-tmp .ps1 files must parse, no ANSI trap --
  $badPs = @(); $oldPs = @(); $nPs = 0
  if (Test-Path $tmpRoot) {
    foreach ($f in @(Get-ChildItem $tmpRoot -Recurse -Filter *.ps1 -File -ErrorAction SilentlyContinue)) {
      $b = [System.IO.File]::ReadAllBytes($f.FullName)
      $bom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
      $nonAscii = @($b | Where-Object { $_ -gt 127 }).Count
      $inWindow = ($f.LastWriteTime -gt $cut24)
      $issues = @()
      if ((-not $bom) -and $nonAscii -gt 0) { $issues += ("ANSI trap: $nonAscii non-ASCII byte(s) without BOM") }
      $enc = [System.Text.Encoding]::UTF8
      if (-not $bom) { $enc = [System.Text.Encoding]::Default }
      $text = $enc.GetString($b)
      if ($bom) { $text = $text.TrimStart([char]0xFEFF) }
      $psk = $null; $per = $null
      [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$psk, [ref]$per)
      if (@($per).Count -gt 0) { $issues += ("$(@($per).Count) syntax error(s)") }
      if ($issues.Count -gt 0) {
        if ($inWindow) { $badPs += ($f.Name + ': ' + ($issues -join '; ')) }
        else { $oldPs += ($f.Name + ' (outside window, not counted): ' + ($issues -join '; ')) }
      }
      if ($inWindow) { $nPs++ }
    }
  }
  if ($badPs.Count -eq 0) { Say 'PASS' 'sampler-selfcheck' "$nPs driver script(s) inside the window: syntax OK, no ANSI trap" }
  else { $script:fail++; Say 'FAIL' 'sampler-selfcheck' ($badPs -join '; ') }
  $oldPs | ForEach-Object { Sub $_ }

  # --- 15) implementation must come from an executor (dispatch log) --------
  # Scope stays the three implementation trees (Scripts / Editor / Resources),
  # but only HAND-WRITTEN TEXT SOURCES inside them are reconciled. The Resources
  # tree also holds the generator outputs (prefab / anim / controller / asset /
  # mat / unity / png / wav / bytes, plus every .meta): those are produced by the
  # Assets/Editor/** generators from the original-asset source tree, so judging
  # them one by one against per-file dispatch records can only ever be a false
  # positive --
  # and a check that misfires is worse than no check at all
  # (clover-engine SKILL section 1.11, item 11). Reconciliation of the .cs files
  # under Scripts / Editor is NOT relaxed: that is the actual "did the main agent
  # edit implementation itself" signal and every one of them is still counted.
  # PowerShell 5.1 treats '**' like '*', so the roots are walked recursively
  # instead of trusting globs to reach nested files.
  $implTextExt = @('.cs')
  $implRoots = @((Join-Path $root 'client\Assets\Scripts'),
                 (Join-Path $root 'client\Assets\Editor'),
                 (Join-Path $root 'client\Assets\Resources'))
  $implFiles = @()
  foreach ($r in $implRoots) {
    if (Test-Path $r) {
      $implFiles += @(Get-ChildItem $r -Recurse -File -ErrorAction SilentlyContinue |
                      Where-Object { $implTextExt -contains $_.Extension.ToLower() -and $_.LastWriteTime -gt $cut24 })
    }
  }
  $implFiles = @($implFiles | Sort-Object FullName -Unique)
  $dispatched = @()
  if (Test-Path $logPath) {
    foreach ($line in @([System.IO.File]::ReadAllLines($logPath))) {
      if ($line.Trim().Length -eq 0 -or $line.TrimStart().StartsWith('#')) { continue }
      $c = $line -split "`t"
      if ($c.Count -ge 4) {
        $t = [datetime]::MinValue
        [void][datetime]::TryParse($c[0], [ref]$t)
        $dispatched += [pscustomobject]@{ At = $t; Scope = $c[3] }
      }
    }
  }
  $logStart = $null
  if ($dispatched.Count -gt 0) { $logStart = ($dispatched | Sort-Object At | Select-Object -First 1).At }
  $orphan = @(); $preLog = @(); $covered = 0
  foreach ($f in $implFiles) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
    $hit = @($dispatched | Where-Object {
      $scopes = @($_.Scope -split '[,;]') | ForEach-Object { $_.Trim().Replace('\', '/') } | Where-Object { $_.Length -gt 0 }
      $inScope = @($scopes | Where-Object { $rel -like ($_ + '*') }).Count -gt 0
      $inScope -and ($_.At -le $f.LastWriteTime)
    })
    if ($hit.Count -gt 0) { $covered++; continue }
    if ($logStart -and $f.LastWriteTime -lt $logStart) { $preLog += ($rel + '  (' + $f.LastWriteTime.ToString('MM-dd HH:mm') + ')') }
    else { $orphan += $rel }
  }
  if ($implFiles.Count -eq 0) {
    Say 'HUMAN-ONLY' 'impl-by-executor' 'no implementation file changed inside the window (nothing to reconcile)'
  } elseif ($orphan.Count -eq 0) {
    Say 'PASS' 'impl-by-executor' "$($implFiles.Count) changed impl file(s): $covered covered by a dispatch record, $($preLog.Count) pre-log residue excluded (check 11)"
    $preLog | ForEach-Object { Sub $_ }
  } else {
    $script:fail++
    Say 'FAIL' 'impl-by-executor' "$($orphan.Count)/$($implFiles.Count) changed impl file(s) have no dispatch record => the main agent edited implementation itself"
    $orphan | ForEach-Object { Sub $_ }
    $preLog | ForEach-Object { Sub ('(pre-log residue, not counted) ' + $_) }
  }
}

# A crashing gate must never look green: catch everything and FAIL loudly.
try { Invoke-Checks }
catch {
  $script:fail++
  Say 'FAIL' 'verify-script-crash' $_.Exception.Message
}

# --- 16) graphics-device: WARP software rendering voids every frame-time number ---
#  (template check 19; SKILL 6 gate 2 / experience/perf-triage.md)
#  Rationale: on 2026-09-20 the "game is stuck at 1 fps" report took 6 Play sessions to
#  trace, and the root cause (Microsoft Basic Render Driver = CPU software rasterizer)
#  was visible in this log line the whole time. Whenever this fires, no frame-time
#  number produced on this machine may be used as evidence.
$editorLog = Join-Path $root 'client\Logs\Editor.log'
if (-not (Test-Path $editorLog)) {
  Say 'HUMAN-ONLY' 'graphics-device' 'no client/Logs/Editor.log -- check SystemInfo.graphicsDeviceName by hand'
} else {
  $devLine = @(Select-String -Path $editorLog -Pattern 'Device Name:\s*(.+)$' -ErrorAction SilentlyContinue | Select-Object -First 1)
  if ($devLine.Count -eq 0) {
    Say 'HUMAN-ONLY' 'graphics-device' 'no "D3D12 Device Filter" line in Editor.log'
  } else {
    $dev = $devLine[0].Matches[0].Groups[1].Value.Trim()
    if ($dev -match 'Basic Render Driver|Basic Display|WARP') {
      $script:fail++
      Say 'FAIL' 'graphics-device' ('render device = "' + $dev + '" => software rendering; every frame-time number on this machine is void')
    } else {
      Say 'PASS' 'graphics-device' ('render device = ' + $dev)
    }
  }
}

# --- 20) coverage-matrix -- SKILL T0: "checked" must be a NUMBER -------------
#  Source of the verdict rows = section G of the acceptance table (the coverage
#  matrix written by tools/probes/enumerate-entities.py). Sections A-F hold the
#  hand-written acceptance rows, the differences registry and this file's own
#  self-check summary -- those are NOT verdict rows and must not be scored
#  (same reasoning as check 3/12 above).
#  Five sub-criteria: entity rows == verdict rows; zero blank verdicts;
#  zero "mismatch"; all 12+3 dimensions present; and the mismatch rows are
#  printed (they ARE this slice's product: a red list).
$cListName = ([char[]]@(0x5B9E,0x4F53,0x6E05,0x5355) -join '') + '.tsv'      # entity list
$cList     = Join-Path $planDir $cListName
$cAgree    = ([char[]]@(0x4E00,0x81F4) -join '')                              # consistent
# NOTE: the skill template writes this as (0x4E0D,0x81F4) = "not"+"dense" ("bu zhi"),
# i.e. it DROPS 0x4E00 ("yi") -- copying it verbatim makes coverage-diff a permanent
# false PASS. Three code points are needed for the real "mismatch" token.
$cDis      = ([char[]]@(0x4E0D,0x4E00,0x81F4) -join '')                       # mismatch
$cPend     = ([char[]]@(0x5F85,0x91C7) -join '')                              # to-be-captured (visual)
$cAllowed  = ([char[]]@(0x5141,0x8BB8,0x7684,0x5DEE,0x5F02) -join '')         # allowed difference
$dims = @()
1..12 | ForEach-Object { $dims += ('D' + $_) }
1..3  | ForEach-Object { $dims += ('S' + $_) }
if (-not (Test-Path $cList)) {
  $script:fail++
  Say 'FAIL' 'coverage-rows' ('missing ' + $cListName + ' -- enumerate it with tools/probes/enumerate-entities.py, never by hand (T0)')
} else {
  $listRows = @([System.IO.File]::ReadAllLines($cList, [Text.Encoding]::UTF8) |
                Where-Object { $_.Trim().Length -gt 0 -and $_ -notmatch '^\s*#' }).Count
  $gRows = @()
  if (Test-Path $specTable) {
    $inG = $false
    foreach ($ln in @((Read-Text $specTable) -split "`r?`n")) {
      if ($ln.StartsWith('## ')) { $inG = ($ln -match '^##\s+G\.'); continue }
      if ($inG -and $ln -match '^\s*\|\s*\d+\s*\|') { $gRows += $ln }
    }
  }
  if ($gRows.Count -eq 0) {
    $script:fail++
    Say 'FAIL' 'coverage-rows' 'no verdict rows in acceptance section G -- run: python tools/probes/enumerate-entities.py --inject'
  } elseif ($listRows -ne $gRows.Count) {
    $script:fail++
    Say 'FAIL' 'coverage-rows' ('entity rows = ' + $listRows + ' vs verdict rows = ' + $gRows.Count + ' -> rows were skipped (T0)')
  } else {
    Say 'PASS' 'coverage-rows' ('entity rows == verdict rows (' + $listRows + ')')
  }
  $blank = @($gRows | Where-Object { -not ($_.Contains($cAgree)) -and -not ($_.Contains($cDis)) -and -not ($_.Contains($cPend)) -and -not ($_.Contains($cAllowed)) })
  if ($gRows.Count -gt 0) {
    if ($blank.Count -gt 0) {
      $script:fail++
      Say 'FAIL' 'coverage-filled' ('' + $blank.Count + ' verdict row(s) with no verdict')
      $blank | Select-Object -First 5 | ForEach-Object {
        $bl = [string]$_
        if ($bl.Length -gt 120) { $bl = $bl.Substring(0, 120) }
        Sub $bl
      }
    } else {
      Say 'PASS' 'coverage-filled' ('every verdict row carries a verdict (' + $gRows.Count + ' rows; ' + $cPend + ' counts as registered-but-visual)')
    }
    $bad = @($gRows | Where-Object { $_.Contains($cDis) })
    if ($bad.Count -gt 0) {
      $script:fail++
      Say 'FAIL' 'coverage-diff' ('' + $bad.Count + ' row(s) marked mismatch -> not deliverable (T0); first 40:')
      $bad | Select-Object -First 40 | ForEach-Object {
        $cells = @(([string]$_).Split('|') | ForEach-Object { $_.Trim() })
        $txt = [string]$_
        if ($cells.Count -ge 6) { $txt = ($cells[1] + ' ' + $cells[2] + ' :: ' + $cells[4]) }
        if ($txt.Length -gt 150) { $txt = $txt.Substring(0, 150) }
        Sub $txt
      }
    } else {
      Say 'PASS' 'coverage-diff' 'zero mismatch'
    }
  }
  $listTxt = [System.IO.File]::ReadAllText($cList, [Text.Encoding]::UTF8)
  $missDim = @($dims | Where-Object { $listTxt -notmatch ('\b' + $_ + '\b') })
  if ($missDim.Count -gt 0) {
    $script:fail++
    Say 'FAIL' 'coverage-dimensions' ('missing dimension(s): ' + ($missDim -join ','))
  } else {
    Say 'PASS' 'coverage-dimensions' 'all 12+3 dimensions present in the entity list'
  }
}

# --- 21) scale-tier -- SKILL T0: sampling density declared ONCE, never downgraded
$cTier   = ([char[]]@(0x6863,0x4F4D) -join '')                                # tier
$specDir = Join-Path $planDir (([char[]]@(0x7B56,0x5212,0x6848) -join ''))    # plan/spec dir
$tierRx  = '\b(S|M|L)\s*(' + $cTier.Substring(0,1) + '|tier)'
$tierHit = @()
if (Test-Path $specDir) {
  foreach ($f in @(Get-ChildItem $specDir -Filter *.md -File -ErrorAction SilentlyContinue)) {
    $tx = [System.IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    if ($tx.Contains($cTier) -and ($tx -match $tierRx)) { $tierHit += $f.Name }
  }
}
if ($tierHit.Count -gt 0) { Say 'PASS' 'scale-tier' ('declared in ' + ($tierHit -join ',')) }
else { $script:fail++; Say 'FAIL' 'scale-tier' 'no tier (S/M/L) declared under plan/spec/*.md -- declare once, never downgrade (T0)' }

# --- 22) impact-radius -- a BUG FIX must enumerate its blast radius ----------
$irPath = Join-Path $root '.ai-tmp/test/impact-radius.tsv'
if (-not (Test-Path $irPath)) {
  Say 'HUMAN-ONLY' 'impact-radius' 'no .ai-tmp/test/impact-radius.tsv -- for a bug fix, list the blast radius (dim / cause chain / affected rows)'
} else {
  $irBad = @([System.IO.File]::ReadAllLines($irPath, [Text.Encoding]::UTF8) |
             Where-Object { $_.Trim().Length -gt 0 -and $_ -notmatch '^\s*#' } |
             Where-Object { ($_ -split "`t").Count -lt 3 })
  if ($irBad.Count -eq 0) { Say 'PASS' 'impact-radius' 'every row lists dim / cause chain / affected rows' }
  else { $script:fail++; Say 'FAIL' 'impact-radius' ('' + $irBad.Count + ' row(s) missing columns (dim / cause chain / affected rows)') }
}

# --- 23) acceptance-table aggregates must equal the table body ---------------
#  Check 3 above is deliberately left as it was; what changes is that its verdict is
#  no longer HUMAN-ONLY: tools/probes/check-acceptance-sums.py counts the table BODY
#  (rows / per-section rows / status tally / allowed-difference rows) and compares every
#  aggregate the file states about itself against that count.  SKILL 0.6: a rule that
#  cannot be tested red is not a gate -- and on 2026-09-21 this entry found 8 stale
#  aggregates out of 21 (68 vs 72 ok, 4 vs 0 blocked, 36 vs 46 differences ...), i.e. the
#  old wording really was hiding a drift.
#  Python is used because the parse is markdown-structure work; the script is a JUDGEMENT
#  ASSET (tools/probes/) and prints an ASCII-only PASS/FAIL with the per-claim detail.
$sumsScript = Join-Path $root 'tools\probes\check-acceptance-sums.py'
if (-not (Test-Path $sumsScript)) {
  $script:fail++
  Say 'FAIL' 'acceptance-sums' ('missing ' + $sumsScript + ' -- the acceptance aggregates are unverifiable')
} elseif ($null -eq (Get-Command python -ErrorAction SilentlyContinue)) {
  $script:fail++
  Say 'FAIL' 'acceptance-sums' 'python is not on PATH -- cannot run check-acceptance-sums.py'
} else {
  $sumTmp = Join-Path $tmpRoot 'verify-acceptance-sums.txt'
  & python $sumsScript | Set-Content -Encoding UTF8 $sumTmp
  $sumRc = $LASTEXITCODE
  $sumOut = @(Get-Content $sumTmp -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 })
  $sumFirst = if ($sumOut.Count -gt 0) { $sumOut[0] } else { '(no output)' }
  $sumLast = if ($sumOut.Count -gt 0) { $sumOut[$sumOut.Count - 1] } else { '(no output)' }
  if ($sumRc -eq 0) {
    Say 'PASS' 'acceptance-sums' $sumLast
    Sub $sumFirst
  } else {
    $script:fail++
    Say 'FAIL' 'acceptance-sums' $sumLast
    Sub $sumFirst
    $sumOut | Where-Object { $_ -match '^\s*BAD\s' } | ForEach-Object { Sub $_ }
  }
}

# --- 24) differences: registry file and table section must be ONE row set -----
#  The acceptance table's "allowed differences" section and the four-element
#  registry (ce hua / cha yi deng ji .tsv) describe the same rows: the section is a
#  projection of the registry. Before this check they could drift in silence -- on
#  2026-09-21 the section had 46 rows while the registry had 22, i.e. 24 differences
#  had never been screened by the "four elements present" rule of item 4. A rule that
#  cannot be tested red is not a gate (SKILL 0.6). This one matches rows by their
#  leading id, so it fails loudly AND prints exactly which ids exist on only one side.
$cRegName = ([char[]]@(0x5DEE,0x5F02,0x767B,0x8BB0) -join '') + '.tsv'   # cha yi deng ji
$regPath  = Join-Path $planDir $cRegName
if (-not (Test-Path $regPath)) {
  $script:fail++
  Say 'FAIL' 'differences-source-of-truth' ('missing the registry: ' + $regPath)
} elseif (-not (Test-Path $specTable)) {
  $script:fail++
  Say 'FAIL' 'differences-source-of-truth' ('missing the acceptance table: ' + $specTable)
} else {
  $regIds = @(); $regDup = @()
  foreach ($line in @([System.IO.File]::ReadAllLines($regPath, [Text.Encoding]::UTF8))) {
    if ($line.Trim().Length -eq 0) { continue }
    if ($line.TrimStart().StartsWith('#')) { continue }
    $cells = @($line -split "`t")
    if ($cells.Count -lt 2) { continue }
    $idTxt = $cells[0].Trim()
    if ($idTxt -match '^\d+$') {
      $iv = [int]$idTxt
      if ($regIds -contains $iv) { $regDup += $iv } else { $regIds += $iv }
    }
  }
  $secIds = @(); $secDup = @()
  foreach ($r in @(Get-SectionLines (Read-Text $specTable) $cDiffSec)) {
    $m = [regex]::Match($r.TrimStart(), '^\|\s*(\d+)\s*\|')
    if ($m.Success) {
      $iv = [int]$m.Groups[1].Value
      if ($secIds -contains $iv) { $secDup += $iv } else { $secIds += $iv }
    }
  }
  $onlyReg = @($regIds | Where-Object { $secIds -notcontains $_ } | Sort-Object)
  $onlySec = @($secIds | Where-Object { $regIds -notcontains $_ } | Sort-Object)
  if (($regIds.Count -eq $secIds.Count) -and ($onlyReg.Count -eq 0) -and ($onlySec.Count -eq 0) -and ($regDup.Count -eq 0) -and ($secDup.Count -eq 0)) {
    Say 'PASS' 'differences-source-of-truth' ("registry rows = section rows = $($regIds.Count); every id matches on both sides")
  } else {
    $script:fail++
    Say 'FAIL' 'differences-source-of-truth' ("registry rows = $($regIds.Count) vs section rows = $($secIds.Count) -> the two lists drifted")
    if ($onlyReg.Count -gt 0) { Sub ('only in registry: ' + ($onlyReg -join ',')) }
    if ($onlySec.Count -gt 0) { Sub ('only in section: ' + ($onlySec -join ',')) }
    if ($regDup.Count -gt 0) { Sub ('duplicate id(s) in registry: ' + ($regDup -join ',')) }
    if ($secDup.Count -gt 0) { Sub ('duplicate id(s) in section: ' + ($secDup -join ',')) }
  }
}

# --- 25) home-credit-rendered -- the brand line must be SEEN RENDERED ---------
#  SKILL section 8 (brand): the judgement for `by clover-engine` is
#  "seen rendered / runtime UI tree", NOT a source grep.  Check 10 above only
#  proves the literal exists in the sources, so it ends in HUMAN-ONLY; this
#  check closes that gap with the dump written by
#  tools/probes/probe-home-nodetree.cs from a REAL Play session: every on-screen
#  Text node's hierarchy path / text / font size / screen rect (screen origin =
#  top-left, y grows downward, so "lowest on screen" = largest rect bottom).
#  Verdict (all must hold, else FAIL):
#    a) the dump declares the y-down origin and says the main menu was up;
#    b) some on-screen text node reads exactly `by clover-engine` (case sensitive,
#       whitespace stripped);
#    c) that node IS the bottom-most text node on screen;
#    d) the dump is at least as new as the panel source that renders it
#       (an edited panel voids the dump -- SKILL 2.4 capture-then-freeze).
#  No dump at all => HUMAN-ONLY (starting Play Mode needs a human-visible step),
#  and the message says who can give it.
$cCredit    = 'by clover-engine'
$probeDump  = Join-Path $root 'tools\probes\home-screen-nodetree.txt'
$brandPanel = Join-Path $codeDir 'UI\Flow\MainMenuPanel.cs'
if (-not (Test-Path $probeDump)) {
  $script:human++
  Say 'HUMAN-ONLY' 'home-credit-rendered' ('missing ' + $probeDump + ' -- it is written by a REAL Play session: run tools/probes/probe-home-nodetree.cs through .ai-tmp/drivers/af-play.ps1 -Phase open then -Phase tree. Who can give it: whoever can let the Unity editor enter Play Mode on this machine')
} else {
  # Parser, version 2 (slice AH-R): the judgement is the ACTUAL text AND the
  # ACTUAL font (skill section 6.9 -- a pixel font that only carries uppercase
  # glyphs renders `by clover-engine` as `BY CLOVER-ENGINE`, which is just as
  # non-compliant as a wrong string). The probe therefore also dumps, per node:
  # font=<asset name> fontDyn=<bool> glyphs=<baked glyph count> hasA= hasa=.
  # A node whose font cannot draw lowercase is a FAIL, not a PASS.
  $dumpTxt = Read-Text $probeDump
  $head = @(); $nodes = @(); $txtLines = 0
  foreach ($ln in @($dumpTxt -split "`r?`n")) {
    if ($ln.StartsWith('#')) { $head += $ln; continue }
    if ($ln.StartsWith('TEXT ')) {
      $txtLines++
      $m = [regex]::Match($ln, "text='(.*)' \| fontSize=([0-9]+) \| bestFit=([A-Za-z]+) \| rect=(-?[0-9.]+),(-?[0-9.]+),(-?[0-9.]+),(-?[0-9.]+) \| font=(\S*) \| fontDyn=(True|False) \| glyphs=(-?[0-9]+) \| hasA=(True|False) \| hasa=(True|False)")
      if (-not $m.Success) { continue }
      $pEnd = $ln.IndexOf(' | text=')
      $nodes += [pscustomobject]@{
        Path     = $ln.Substring(5, $pEnd - 5)
        Text     = $m.Groups[1].Value
        FontSize = [int]$m.Groups[2].Value
        Bottom   = [double]$m.Groups[5].Value + [double]$m.Groups[7].Value
        Font     = $m.Groups[8].Value
        FontDyn  = ($m.Groups[9].Value -eq 'True')
        Glyphs   = [int]$m.Groups[10].Value
        HasUpper = ($m.Groups[11].Value -eq 'True')
        HasLower = ($m.Groups[12].Value -eq 'True')
        Kind     = 'UnityEngine.UI.Text'
      }
      continue
    }
    if ($ln.StartsWith('TMPTEXT ')) {
      $m2 = [regex]::Match($ln, "text='(.*)' \| fontAsset=(\S*) \| hasA=(True|False) \| hasa=(True|False) \| rect=(-?[0-9.]+),(-?[0-9.]+),(-?[0-9.]+),(-?[0-9.]+)")
      if (-not $m2.Success) { continue }
      $pEnd2 = $ln.IndexOf(' | text=')
      $nodes += [pscustomobject]@{
        Path     = $ln.Substring(8, $pEnd2 - 8)
        Text     = $m2.Groups[1].Value
        FontSize = 0
        Bottom   = [double]$m2.Groups[6].Value + [double]$m2.Groups[8].Value
        Font     = $m2.Groups[2].Value
        FontDyn  = $false
        Glyphs   = -1
        HasUpper = ($m2.Groups[3].Value -eq 'True')
        HasLower = ($m2.Groups[4].Value -eq 'True')
        Kind     = 'TMPro'
      }
    }
  }
  $headTxt = ($head -join "`n")
  $problems = @()
  if ($txtLines -gt 0 -and $nodes.Count -eq 0) {
    $problems += ('the ' + $txtLines + ' TEXT line(s) in the dump carry no font-evidence columns -- re-take the dump with tools/probes/probe-home-nodetree.cs (skill 6.9 judges the actual text AND the actual font)')
  }
  if ($nodes.Count -eq 0) { $problems += 'the dump holds no TEXT/TMPTEXT line (nothing was on screen when it was taken)' }
  if ($headTxt -notmatch 'origin=top-left') { $problems += 'the dump does not declare `origin=top-left` -- its rect column cannot be read as a screen rect' }
  if ($headTxt -notmatch 'activePanels=[^\r\n]*MainMenuPanel') { $problems += 'the dump was NOT taken on the home screen (activePanels does not contain MainMenuPanel)' }
  $ordered = @($nodes | Sort-Object Bottom -Descending)
  $cCreditBare = ($cCredit -replace '\s', '')
  $hit = @($nodes | Where-Object { ($_.Text -replace '\s', '') -ceq $cCreditBare })
  if ($hit.Count -eq 0) {
    $problems += ('no on-screen text node reads exactly "' + $cCredit + '" (case sensitive comparison after stripping whitespace)')
  } elseif ($ordered.Count -gt 0 -and [math]::Abs($hit[0].Bottom - $ordered[0].Bottom) -gt 0.01) {
    $problems += ('the signature IS rendered but is not the bottom-most text node: bottom-most = ' + $ordered[0].Path + ' text="' + $ordered[0].Text + '" bottom=' + $ordered[0].Bottom.ToString('F1') + '; signature bottom=' + $hit[0].Bottom.ToString('F1'))
  } else {
    # --- font half of the skill-6.9 judgement (slice AH-R: this is the tightening) --
    # The literal is right; now prove the font can actually DRAW lowercase. A font
    # asset with no 'a' glyph renders the line as BY CLOVER-ENGINE => FAIL.
    $s = $hit[0]
    if (-not $s.HasLower) {
      $problems += ('the signature font "' + $s.Font + '" (' + $s.Kind + ') has NO lowercase glyph: HasCharacter(a)=False, dynamic=' + $s.FontDyn + ', bakedGlyphs=' + $s.Glyphs + ' => the line is rendered ALL-CAPS => NOT compliant (skill 6.9); supply a font that carries lowercase glyphs')
    }
    if (-not $s.HasUpper) {
      $problems += ('the signature font "' + $s.Font + '" has no uppercase glyph either (HasCharacter(A)=False) -- the font asset looks broken/empty')
    }
    # A dynamic font rasterizes any system glyph, so lowercase is guaranteed only
    # when dynamic=True; a static asset must prove it by the hasa column.
    if ((-not $s.FontDyn) -and ($s.Glyphs -le 0) -and $s.HasLower) {
      $problems += ('the signature font "' + $s.Font + '" is static but declares 0 baked glyphs while claiming lowercase -- the font evidence is self-contradictory, re-take the dump')
    }
  }
  if (Test-Path $brandPanel) {
    $tDump = (Get-Item $probeDump).LastWriteTime
    $tPanel = (Get-Item $brandPanel).LastWriteTime
    if ($tDump -lt $tPanel) {
      $problems += ('the dump (' + $tDump.ToString('MM-dd HH:mm') + ') is OLDER than the panel that renders the signature, ' + (Split-Path $brandPanel -Leaf) + ' (' + $tPanel.ToString('MM-dd HH:mm') + ') => re-take the dump')
    }
  } else { $problems += ('cannot judge freshness: ' + $brandPanel + ' is missing') }
  if ($problems.Count -eq 0) {
    $s = $hit[0]
    Say 'PASS' 'home-credit-rendered' ($nodes.Count.ToString() + ' on-screen text node(s); bottom-most = ' + $s.Path + ' text="' + $s.Text + '" fontSize=' + $s.FontSize + ' bottom=' + $s.Bottom.ToString('F1') + ' font=' + $s.Font + ' dynamic=' + $s.FontDyn + ' hasa=' + $s.HasLower + ' (lowercase renderable, so the line is NOT drawn all-caps); dump is newer than MainMenuPanel.cs')
  } else {
    $script:fail++
    Say 'FAIL' 'home-credit-rendered' ($problems.Count.ToString() + ' problem(s) in ' + (Split-Path $probeDump -Leaf))
    $problems | ForEach-Object { Sub $_ }
  }
}

# --- 26) shot-refs-audited -- every evidence screenshot must be REFERENCED ------
#  SKILL 1.8 item 6: an evidence screenshot is a ONE-OFF artifact; only the shots
#  that some table / manifest / script actually cites are worth keeping, because a
#  shot nobody reads is a draw-only artifact (it proves nothing and hides the fact
#  that a row was never judged).  "Every shot under .ai-tmp/screenshots is cited"
#  is a COMPUTABLE assertion, so by SKILL 0.6 it must be a gate, not a habit --
#  slice AF proved this the hard way: tools\probes\audit-shot-refs.ps1 found 176
#  shots of which 78 were cited by nothing plus 12 b41_* burst leftovers (mentioned
#  only in a runtime log, cited by no table), and 90 were deleted.  Slice AF left
#  the audit as a tool but (per its own task book) did not wire it a gate; slice AG
#  wires it here.
#  Judgement: run the audit in its default (zero-ref list) mode, parse its ASCII
#  summary line, and FAIL when any screenshot is unreferenced.  An empty screenshot
#  set is HUMAN-ONLY (never a vacuous PASS: an unjudged set is not a judged one).
#  The audit is invoked as a CHILD process so that its own variables cannot clobber
#  this script's state, and its stdout is captured in memory -- deliberately NOT
#  written to a file inside the project, because such a file would name the very
#  pngs under audit and, on the next run, be counted as a reference to them (the
#  gate would silently heal its own violations).
$auditScript = Join-Path $root 'tools\probes\audit-shot-refs.ps1'
if (-not (Test-Path $auditScript)) {
  $script:fail++
  Say 'FAIL' 'shot-refs-audited' ('missing ' + $auditScript + ' -- the "every screenshot is cited" rule cannot be tested')
} else {
  $auditOut = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $auditScript 2>&1 | ForEach-Object { [string]$_ })
  $sumLine = @($auditOut | Where-Object { $_ -match 'unreferenced \(count = (\d+)\): (\d+) of (\d+)' } | Select-Object -Last 1)
  if ($sumLine.Count -eq 0) {
    $script:fail++
    Say 'FAIL' 'shot-refs-audited' 'the audit produced no parsable summary line -- inspect ' + $auditScript
    $auditOut | Select-Object -Last 6 | ForEach-Object { Sub $_ }
  } else {
    $am = [regex]::Match($sumLine[0], 'unreferenced \(count = (\d+)\): (\d+) of (\d+)')
    $zeroRef = [int]$am.Groups[1].Value
    $totalShots = [int]$am.Groups[3].Value
    if ($totalShots -eq 0) {
      $script:human++
      Say 'HUMAN-ONLY' 'shot-refs-audited' 'no evidence screenshots exist under .ai-tmp/screenshots -- an empty set is unjudged, not a pass'
    } elseif ($zeroRef -eq 0) {
      Say 'PASS' 'shot-refs-audited' ($totalShots.ToString() + ' screenshot(s), every one cited by a table / manifest / script (zero-ref = 0)')
    } else {
      $script:fail++
      Say 'FAIL' 'shot-refs-audited' ($zeroRef.ToString() + ' of ' + $totalShots.ToString() + ' screenshot(s) under .ai-tmp/screenshots are cited by nothing (draw-only artifacts)')
      $auditOut | Where-Object { $_ -match '^ZERO ' } | ForEach-Object { Sub $_ }
    }
  }
}

# --- 27) spec-doc -- the reference spec document must exist -------------------
#  (template item `spec-doc`, required; slice AH-R) Every acceptance verdict is a
#  "does it match the original" claim, so without the reference spec there is
#  nothing to match against. Existence is computable => it is a gate (SKILL 0.6).
if (Test-Path $specDir) {
  $specDocs = @(Get-ChildItem $specDir -Filter *.md -File -ErrorAction SilentlyContinue)
  if ($specDocs.Count -gt 0) { Say 'PASS' 'spec-doc' ($specDocs.Count.ToString() + ' reference spec document(s) under plan/spec/ (e.g. ' + $specDocs[0].Name + ')') }
  else { $script:fail++; Say 'FAIL' 'spec-doc' ('no *.md reference spec under ' + $specDir) }
} else { $script:fail++; Say 'FAIL' 'spec-doc' ('missing the reference spec directory: ' + $specDir) }

# --- 28) asset-research-doc -- the asset research log must exist --------------
#  (template item `asset-research-doc`, required; SKILL 1 item 5) Exhausting the
#  original's own assets is a RULE; "no research file" means it was never done.
$cResearch = ([char[]]@(0x7D20,0x6750,0x8C03,0x7814) -join '') + '.md'
$researchPath = Join-Path $planDir $cResearch
if (Test-Path $researchPath) { Say 'PASS' 'asset-research-doc' ($cResearch + ' present') }
else { $script:fail++; Say 'FAIL' 'asset-research-doc' ('missing ' + $cResearch + ' -- the asset research log is required before generic fallback assets') }

# --- 29) baseline-images -- reference-side baselines must exist --------------
#  (template item `baseline-images`, required) A visual verdict needs a reference
#  side; a project with no baseline images cannot judge "looks like the original".
$cBaseline = ([char[]]@(0x57FA,0x7EBF,0x56FE) -join '')
$baseDir = Join-Path $planDir $cBaseline
$baseImgs = @()
if (Test-Path $baseDir) { $baseImgs = @(Get-ChildItem $baseDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { @('.png','.jpg','.jpeg','.bmp') -contains $_.Extension.ToLower() }) }
if ($baseImgs.Count -ge 2) { Say 'PASS' 'baseline-images' ($baseImgs.Count.ToString() + ' reference baseline image(s) under plan/' + $cBaseline) }
else { $script:fail++; Say 'FAIL' 'baseline-images' ('only ' + $baseImgs.Count + ' baseline image(s) under ' + $baseDir + ' -- the reference side of every visual row is missing') }

# --- 30) no-assets-screenshots -- no forensic shots inside the Unity project --
#  (template item `no-assets-screenshots`, required) Evidence shots are one-off
#  artifacts; inside client/Assets they would also be imported as game assets.
$forensic = @()
foreach ($nm in @('Screenshots', 'Screenshots_', '_Screenshots')) {
  $d = Join-Path $root ('client\Assets\' + $nm)
  if (Test-Path $d) { $forensic += $d }
}
if ($forensic.Count -eq 0) { Say 'PASS' 'no-assets-screenshots' 'no forensic screenshot dir under client/Assets (evidence shots live in .ai-tmp/screenshots/)' }
else { $script:fail++; Say 'FAIL' 'no-assets-screenshots' ('forensic screenshot dir(s) inside the Unity project: ' + ($forensic -join ', ')) }

# --- 31) play-ledger -- every Play session logged WITH a reason --------------
#  (template item `play-ledger` / alias `play-budget`, required) The ledger exists
#  to answer "was this Play session justified?" -- a row with an empty column 4
#  answers nothing, so an empty reason is a FAIL, not a note. The ledger is never
#  scored by ROW COUNT (a budget reading was retired; see reference/anti-gaming.md).
$playLedger = Join-Path $root '.ai-tmp\test\play-log.tsv'
if (-not (Test-Path $playLedger)) {
  $script:fail++
  Say 'FAIL' 'play-ledger' 'no .ai-tmp/test/play-log.tsv -- every Play session must be logged with a non-empty reason'
} else {
  $pRows = 0; $pBad = 0
  foreach ($line in @([System.IO.File]::ReadAllLines($playLedger, [Text.Encoding]::UTF8))) {
    $l = [string]$line
    if ($l.Trim().Length -eq 0 -or $l.TrimStart().StartsWith('#')) { continue }
    $pRows++
    $c = $l -split "`t"
    if ($c.Count -lt 4 -or $c[3].Trim().Length -eq 0) { $pBad++ }
  }
  if ($pRows -eq 0) { $script:human++; Say 'HUMAN-ONLY' 'play-ledger' 'the play ledger carries no session row yet' }
  elseif ($pBad -eq 0) { Say 'PASS' 'play-ledger' ($pRows.ToString() + ' Play session(s) logged, every one with a non-empty reason') }
  else { $script:fail++; Say 'FAIL' 'play-ledger' ($pBad.ToString() + ' of ' + $pRows.ToString() + ' session row(s) carry no reason (column 4)') }
}

# --- 32) numeric-log-only -- template item `numeric-log-only` (added slice AI) --
#  SKILL 4 item 9: a `numeric` acceptance row is judged by a RUNTIME LOG LINE /
#  ASSERTION OUTPUT, never by a screenshot ("biao xian" rows are the ones that
#  need a picture).  The failure mode this item exists to block is a numeric row
#  whose whole evidence is a png: a number resting on a picture is unverifiable
#  and, worse, silently "green" -- the picture is never re-read.
#  Judgement:
#    * FAIL  : a numeric row whose backticked evidence set is a screenshot only
#              (i.e. it cites a png and no non-png token at all).
#    * PASS  : every numeric row cites at least one non-screenshot evidence token.
#    * listed: numeric rows that cite NO backticked token whatsoever (bare text).
#              They are printed so the residual stays visible, but they are NOT
#              failed here: this item's rule is "log/assertion, not a picture",
#              while "a verdict row must anchor to a machine-produced artifact"
#              is template item `evidence-anchor`, which is still `planned` and
#              not enforced by this project yet.  HARD RULE: deliberately not conflated.
#  All acceptance rows of the A-F sections are scanned (not just A-E): the F
#  resource section carries numeric rows too.
$probesDir = Join-Path $root 'tools\probes'
$afRows = @()
$inAF = $false
foreach ($ln in @((Read-Text $specTable) -split "`r?`n")) {
  if ($ln.StartsWith('## ')) { $inAF = ($ln -match '^##\s+[A-F]\.'); continue }
  if ($inAF -and $ln -match '^\|\s*[A-Z]?\d+\s*\|') { $afRows += $ln }
}
$numRows = @($afRows | Where-Object { $_.Contains($cNumeric) })
$numShotOnly = @(); $numTextOnly = @(); $numAnchored = 0
foreach ($r in $numRows) {
  $rid = ($r -split '\|')[1].Trim()
  $toks = @([regex]::Matches($r, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value })
  $shotT = @($toks | Where-Object { $_ -match '\.png$' })
  $nonT = @($toks | Where-Object { $_ -notmatch '\.png$' })
  if ($nonT.Count -gt 0) { $numAnchored++ }
  elseif ($shotT.Count -gt 0) { $numShotOnly += ($rid + ' -- evidence is a screenshot only') }
  else { $numTextOnly += ($rid + ' -- no backticked evidence token at all') }
}
if ($numRows.Count -eq 0) {
  $script:human++
  Say 'HUMAN-ONLY' 'numeric-log-only' 'no numeric-class acceptance row -- an empty set is unjudged, not a pass'
} elseif ($numShotOnly.Count -gt 0) {
  $script:fail++
  Say 'FAIL' 'numeric-log-only' ('' + $numShotOnly.Count + ' of ' + $numRows.Count + ' numeric-class row(s) rest on a screenshot only -- a number must rest on a runtime log line / assertion output, never on a picture (SKILL 4 item 9)')
  $numShotOnly | ForEach-Object { Sub $_ }
} else {
  Say 'PASS' 'numeric-log-only' ('' + $numRows.Count + ' numeric-class row(s): ' + $numAnchored + ' cite a non-screenshot log / assertion token, 0 rest on a screenshot only; ' + $numTextOnly.Count + ' cite no backticked token at all (listed below; that residual belongs to template item evidence-anchor, still planned)')
  $numTextOnly | ForEach-Object { Sub $_ }
}

# --- 33) no-sync-subagents -- template item `no-sync-subagents` (was `no-team-sessions`) --
#  SKILL 2 item 2 -- POLICY REVERSED 2026-09-22: dispatch goes ONLY through team
#  members (async: subagent_name + name + team_name). The SYNC channel stalls
#  (code=10003 This operation was aborted / No result found) and leaves the caller
#  with no report, so the channel form is part of the contract, not a style choice.
#
#  Do NOT try to check the model here: a member's model string is an injected
#  self-description it cannot verify, and the host's member metadata records no
#  model. That criterion was measured twice and discarded (2026-09-22).
#
#  What IS computable is the trace a team dispatch leaves:
#    (a) a team/member directory under the workspace host dir, created or modified
#        INSIDE the 24h window  ->  that is the SANCTIONED form (informational);
#    (b) dispatch-log rows (the ledger is mandatory anyway, SKILL 2 item 5) must
#        name a team / member.
#  => the check is INVERTED: a dispatch row carrying NO team/member name means the
#     work went out over the sync channel.
#  Residue older than the window is printed, never failed: template item 11 --
#  a check that reports historical residue as a violation is worse than no check.
$teamRels = @('.codebuddy\teams', '.codebuddy\team', '.codebuddy\members', '.codebuddy\sessions', '.codebuddy\agents')
$teamLive = @(); $teamStale = @()
foreach ($rel in $teamRels) {
  $d = Join-Path $wsRoot $rel
  if (-not (Test-Path $d)) { continue }
  $inWin = @(Get-ChildItem $d -Recurse -Force -ErrorAction SilentlyContinue |
             Where-Object { $_.CreationTime -gt $cut24 -or $_.LastWriteTime -gt $cut24 })
  if ($inWin.Count -gt 0) { $teamLive += ($rel + ' (' + $inWin.Count + ' in-window item(s))') }
  else { $teamStale += $rel }
}
$dispatchRows = @()
if (Test-Path $logPath) {
  foreach ($line in @([System.IO.File]::ReadAllLines($logPath))) {
    $l = [string]$line
    if ($l.Trim().Length -eq 0 -or $l.TrimStart().StartsWith('#')) { continue }
    $dispatchRows += $l
  }
}
$noTeamTrace = @($dispatchRows | Where-Object { $_ -notmatch '(?i)\b(teams?|members?)\b' })
$note = ''
if ($teamStale.Count -gt 0) { $note = '; pre-window residue, NOT a violation (template item 11): ' + ($teamStale -join ', ') }
if ($dispatchRows.Count -eq 0) {
  Say 'PASS' 'no-sync-subagents' ('no dispatch in this window -- nothing to classify' + $note)
} elseif ($noTeamTrace.Count -gt 0) {
  $script:fail++
  $first = $noTeamTrace[0]
  Say 'FAIL' 'no-sync-subagents' ('' + $noTeamTrace.Count + ' dispatch row(s) name no team/member => those went out over the SYNC channel, which stalls (code=10003) and leaves no report; first: ' + $first.Substring(0, [Math]::Min(100, $first.Length)) + $note)
} else {
  Say 'PASS' 'no-sync-subagents' ('all ' + $dispatchRows.Count + ' dispatch row(s) name a team member; in-window team trace: ' + $(if ($teamLive.Count -gt 0) { $teamLive -join ', ' } else { 'none (ledger-only)' }) + $note)
}

# --- 34) freeze-before-capture -- template item `freeze-before-capture` --------
#  SKILL 4 item 6 (capture after freeze) + item 8 (expiry is CAUSAL).  This item
#  pins the BATCH: the evidence of the newest contact-sheet manifest is the batch,
#  its earliest png is the freeze point T0, and every (row, batch shot) pair is
#  judged -- a shot is void only when it is older than ITS OWN row implementation
#  file.  HARD RULE: NOT "any file in the project changed => everything is void": that
#  global reading is exactly what made one edited .cs void 59 shots.
#  Main-agent ruling (slice AI): do NOT merge this into `evidence-freshness`.
#  That item scores every cited shot of every row; this one records the batch and
#  prints T0 so the freeze point itself is auditable.
#  The row set and the implementation resolver are intentionally the SAME as item
#  6 (A-E sections, backticked file / class tokens): this item must never be
#  looser than item 6, and reusing the resolver keeps the two comparable.
$shotMap2 = @{}
if (Test-Path $shotDir) {
  foreach ($f in @(Get-ChildItem $shotDir -Filter *.png -File -ErrorAction SilentlyContinue)) { $shotMap2[$f.Name.ToLower()] = $f }
}
$newestManifest = @(Get-ChildItem $probesDir -Filter '*.manifest.tsv' -File -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime | Select-Object -Last 1)
if (($newestManifest.Count -eq 0) -or ($shotMap2.Count -eq 0)) {
  # an empty batch is unjudged, never a vacuous PASS
  $script:human++
  Say 'HUMAN-ONLY' 'freeze-before-capture' 'no contact-sheet manifest / no evidence png -> the batch freeze point cannot be computed'
} else {
  $batchNames = @(([regex]::Matches((Read-Text $newestManifest[0].FullName), '([0-9A-Za-z_\-\.]+\.png)') |
                   ForEach-Object { $_.Groups[1].Value.ToLower() }) | Sort-Object -Unique)
  $batch = @($batchNames | Where-Object { $shotMap2.ContainsKey($_) })
  if ($batch.Count -eq 0) {
    $script:human++
    Say 'HUMAN-ONLY' 'freeze-before-capture' ('the newest manifest (' + $newestManifest[0].Name + ') names no png that is on disk -> batch cannot be pinned')
  } else {
    $t0 = ($batch | ForEach-Object { $shotMap2[$_].LastWriteTime } | Sort-Object | Select-Object -First 1)
    # implementation resolver -- same shapes as item 6 (keep the two in step)
    $csIndex2 = @{}
    foreach ($d in @($codeDir, $editorDir)) {
      if (-not (Test-Path $d)) { continue }
      foreach ($f in @(Get-ChildItem $d -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue)) {
        $k2 = $f.Name.ToLower()
        if (-not $csIndex2.ContainsKey($k2)) { $csIndex2[$k2] = @() }
        $csIndex2[$k2] += $f.FullName
      }
    }
    $clsPat2 = '([A-Za-z_][A-Za-z0-9_]*Panel|[A-Za-z_][A-Za-z0-9_]*Module|Cs[A-Za-z0-9_]+)'
    $aeRows = @()
    $inAE = $false
    foreach ($ln in @((Read-Text $specTable) -split "`r?`n")) {
      if ($ln.StartsWith('## ')) { $inAE = ($ln -match '^##\s+[A-E]\.'); continue }
      if ($inAE -and $ln -match '^\|\s*[A-Z]?\d+\s*\|') { $aeRows += $ln }
    }
    $voidPairs = @(); $compared2 = 0; $noImpl2 = 0; $batchRows = 0
    foreach ($row in $aeRows) {
      $toks = @([regex]::Matches($row, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value })
      $cited = @()
      foreach ($t in $toks) {
        if ($t -match '\.png$') {
          $bn = ($t -replace '.*[\\/]', '').Trim().ToLower()
          if (($bn.Length -gt 0) -and ($cited -notcontains $bn)) { $cited += $bn }
        }
      }
      $inBatch = @($cited | Where-Object { $batch -contains $_ })
      if ($inBatch.Count -eq 0) { continue }
      $batchRows++
      $names2 = @()
      foreach ($t in $toks) {
        foreach ($m in [regex]::Matches($t, '([A-Za-z_][A-Za-z0-9_]*\.cs)')) {
          $b2 = $m.Groups[1].Value.ToLower()
          if ($names2 -notcontains $b2) { $names2 += $b2 }
        }
        foreach ($m in [regex]::Matches($t, $clsPat2)) {
          $b2 = ($m.Groups[1].Value + '.cs').ToLower()
          if ($names2 -notcontains $b2) { $names2 += $b2 }
        }
      }
      $impls2 = @()
      foreach ($b2 in $names2) { if ($csIndex2.ContainsKey($b2)) { $impls2 += @($csIndex2[$b2]) } }
      $impls2 = @($impls2 | Sort-Object -Unique)
      if ($impls2.Count -eq 0) { $noImpl2++; continue }
      $newestImpl = @($impls2 | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending)[0]
      $tImpl2 = (Get-Item -LiteralPath $newestImpl).LastWriteTime
      foreach ($b2 in $inBatch) {
        $compared2++
        if ($shotMap2[$b2].LastWriteTime -lt $tImpl2) {
          $voidPairs += ('batch png ' + $shotMap2[$b2].Name + ' (' + $shotMap2[$b2].LastWriteTime.ToString('MM-dd HH:mm') + ') is older than its own row implementation file ' + (Split-Path $newestImpl -Leaf) + ' (' + $tImpl2.ToString('MM-dd HH:mm') + ')')
        }
      }
    }
    if ($compared2 -eq 0) {
      $script:human++
      Say 'HUMAN-ONLY' 'freeze-before-capture' ('batch = ' + $batch.Count + ' png from ' + $newestManifest[0].Name + ' but no (batch row, batch shot) pair could be compared -> unjudged, never a pass')
    } elseif ($voidPairs.Count -eq 0) {
      Say 'PASS' 'freeze-before-capture' ('batch freeze point T0 = ' + $t0.ToString('MM-dd HH:mm:ss') + ' (' + $batch.Count + ' png of ' + $newestManifest[0].Name + '); ' + $batchRows + ' batch row(s) / ' + $compared2 + ' (row,shot) pair(s) compared causally: every batch shot is at least as new as its OWN row implementation file; ' + $noImpl2 + ' batch row(s) without a resolvable implementation file are HUMAN-ONLY; no global invalidation (SKILL 4 item 6/8)')
    } else {
      $script:fail++
      Say 'FAIL' 'freeze-before-capture' ('' + $voidPairs.Count + ' of ' + $compared2 + ' (row, batch shot) pair(s) were captured BEFORE their own row implementation was frozen => re-capture only those rows')
      $voidPairs | ForEach-Object { Sub $_ }
    }
  }
}

# --- 35) evidence-economy -- template item `evidence-economy` (added slice AI) --
#  Template wording: "contact-sheet index exists; loose png count within budget".
#  SKILL 4 item 2 / 1.13 T0: N evidence points are compressed into ONE contact
#  sheet -- one screenshot per row is the anti-pattern this item prices.
#  Judgement (computable, no eyeball):
#    (a) if the acceptance table has visual-class rows, at least one contact-sheet
#        index must exist (tools/probes/*.manifest.tsv or .ai-tmp/screenshots/*.index.tsv);
#    (b) the number of pngs that appear in NO index ("loose" pngs) must stay within
#        max(12, visual rows * 2) -- the template's own budget formula, restated as
#        this project's criterion in plan/spec (see the spec doc, section on
#        evidence economy).
#  The "zero-referenced png = 0" half is deliberately NOT duplicated here: item 26
#  `shot-refs-audited` already enforces it (main-agent ruling: state the residue,
#  do not double-implement the same assertion).
$indexFiles = @()
$indexFiles += @(Get-ChildItem (Join-Path $root 'tools\probes') -Filter '*.manifest.tsv' -File -ErrorAction SilentlyContinue)
if (Test-Path $shotDir) { $indexFiles += @(Get-ChildItem $shotDir -Filter '*.index.tsv' -File -ErrorAction SilentlyContinue) }
$visRows2 = @($afRows | Where-Object { $_.Contains($cVisual) })
if ($visRows2.Count -eq 0) {
  $script:human++
  Say 'HUMAN-ONLY' 'evidence-economy' 'no visual-class acceptance row -- nothing to price (unjudged, not a pass)'
} elseif ($indexFiles.Count -eq 0) {
  $script:fail++
  Say 'FAIL' 'evidence-economy' ('' + $visRows2.Count + ' visual-class row(s) but no contact-sheet index (' + $probesDir + '\*.manifest.tsv) -- visuals must live in ONE sheet, not one png per row (T0)')
} else {
  $idxTxt = ''
  foreach ($f in $indexFiles) { $idxTxt += (Read-Text $f.FullName) + "`n" }
  $allPngs = @(Get-ChildItem $shotDir -Filter '*.png' -File -ErrorAction SilentlyContinue)
  $loose = @($allPngs | Where-Object { $idxTxt.IndexOf($_.Name, [StringComparison]::OrdinalIgnoreCase) -lt 0 })
  $budget = [Math]::Max(12, $visRows2.Count * 2)
  if ($loose.Count -gt $budget) {
    $script:fail++
    Say 'FAIL' 'evidence-economy' ('' + $loose.Count + ' loose png (not in any contact-sheet index) vs budget ' + $budget + ' for ' + $visRows2.Count + ' visual-class row(s) => per-row screenshotting (T0 forbids)')
    $loose | Select-Object -First 10 | ForEach-Object { Sub $_.Name }
  } else {
    Say 'PASS' 'evidence-economy' ('' + $visRows2.Count + ' visual-class row(s); index files = ' + $indexFiles.Count + '; indexed png = ' + ($allPngs.Count - $loose.Count) + ', loose = ' + $loose.Count + ' within budget ' + $budget + ' (= max(12, visual rows x 2)); the zero-referenced-png half is enforced by item 26 shot-refs-audited')
  }
}

Write-Output ''
Write-Output ("===== SUMMARY: FAIL={0}  HUMAN-ONLY={1} =====" -f $script:fail, $script:human)
if ($script:fail -gt 0) { Write-Output 'RESULT: FAIL present -> the words done / delivered / verified must NOT be used' }
elseif ($script:human -gt 0) { Write-Output 'RESULT: all computable checks passed; the HUMAN-ONLY items still need a human' }
exit $(if ($script:fail -gt 0) { 1 } else { 0 })
