# ============================================================================
#  External delivery gate -- 16 checks (spec: skill reference/verify-template.md).
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
    $script:human++
    Say 'PASS' 'acceptance-table' "$($accRows.Count) acceptance rows (of $rows digit-keyed rows); the summary numbers inside that file must equal the acceptance count - human cross-check"
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
      if ($script:hrHits -eq 0) { Say 'PASS' 'differences-registry' 'registry empty (consistent: hard-rule hits are 0)' }
      else { $script:fail++; Say 'FAIL' 'differences-registry' "registry empty but $($script:hrHits) hard-rule hit(s) exist" }
    } elseif (-not ($header.Contains($cWhy) -and $header.Contains($cSrc) -and $header.Contains($cWhen))) {
      $script:fail++
      Say 'FAIL' 'differences-registry' 'header lacks the why (col 3) / source (col 4) / expiry (col 5) columns'
    } elseif ($bad.Count -gt 0) {
      $script:fail++
      Say 'FAIL' 'differences-registry' "$($bad.Count) row(s) with an empty why/source/expiry cell"
      $bad | ForEach-Object { Sub $_ }
    } else {
      Say 'PASS' 'differences-registry' "$($body.Count) rows, each with why + source + expiry"
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
      Say 'PASS' 'refs-reachable' "$($pngs.Count) screenshot refs + $($refs.Count) file:line refs all resolve"
    } else {
      $script:fail++
      Say 'FAIL' 'refs-reachable' "$($missing.Count) citation(s) do not resolve"
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
      Say 'PASS' 'evidence-freshness' "$cmpRows acceptance row(s) / $cmpPairs (row,shot) pair(s) compared per row: every cited shot is at least as new as its own row implementation file; $noShotRows row(s) without a shot and $noImplRows row(s) without a resolvable implementation file are HUMAN-ONLY (SKILL 1.11 item 11); $goneShots unroutable shot name(s) are left to refs-reachable"
    } else {
      $script:fail++
      Say 'FAIL' 'evidence-freshness' "$($void.Count) of $cmpPairs (row,shot) pair(s) are stale => only those rows are void, the rest stay valid"
      $void | ForEach-Object { Sub $_ }
    }
  } else { $script:human++; Say 'HUMAN-ONLY' 'evidence-freshness' 'screenshot dir, screenshots, acceptance rows or sources missing - needs a manual check' }

  # --- 7) the one-command re-check entry point itself ----------------------
  Say 'PASS' 'gate-present' 'tools\verify.ps1 executed'

  # --- 8) original-value reference table -----------------------------------
  if (Test-Path $refTable) { Say 'PASS' 'reference-table' $refTable }
  else { $script:fail++; Say 'FAIL' 'reference-table' ("missing: " + $refTable + " -- every replicated element needs original / ours / delta") }

  # --- 9) handover / progress documents must not exist ---------------------
  $bad9 = @(Get-ChildItem $root -Recurse -Filter *.md -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\Library\\' } |
            Where-Object { $_.Name -like 'NEXT*' -or $_.Name.Contains($cProg) -or $_.Name.Contains($cHand) } |
            ForEach-Object { $_.Name })
  if ($bad9.Count -eq 0) { Say 'PASS' 'no-handover-docs' 'none' }
  else { $script:fail++; Say 'FAIL' 'handoff-doc-found' ($bad9 -join ', ') }

  # --- 10) engine self-name: the literal, plus the rendered home-screen line
  # The real judgement is the rendered label (runtime UI tree / glyph match), so
  # the computable half only proves the literal exists; the rest is human-only.
  $brand = @(Get-ChildItem $codeDir -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
             Select-String -Pattern 'clover-engine' -Encoding UTF8)
  if ($brand.Count -eq 0) {
    $script:fail++
    Say 'FAIL' 'engine-credit' 'the literal clover-engine appears nowhere in the client sources'
  } else {
    $mentions = @(Get-ChildItem $codeDir -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
                  Select-String -Pattern 'Engine' -Encoding UTF8).Count
    $script:human++
    Say 'HUMAN-ONLY' 'engine-credit' "literal ok ($($brand.Count) line(s)); home-screen signature must be seen rendered and case-exact ($mentions lines mention Engine)"
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

Write-Output ''
Write-Output ("===== SUMMARY: FAIL={0}  HUMAN-ONLY={1} =====" -f $script:fail, $script:human)
if ($script:fail -gt 0) { Write-Output 'RESULT: FAIL present -> the words done / delivered / verified must NOT be used' }
elseif ($script:human -gt 0) { Write-Output 'RESULT: all computable checks passed; the HUMAN-ONLY items still need a human' }
exit $(if ($script:fail -gt 0) { 1 } else { 0 })
