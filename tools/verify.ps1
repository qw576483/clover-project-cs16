# ============================================================================
#  External delivery gate -- CUT TO 6 ITEMS (2026-09-24; it was 47 items, then 7).
#
#  WHY SO FEW: the user's ruling -- "too many gates, cut them; small things are not
#  checked, things that can be reasoned out are not checked, simple pixels are not
#  checked -- those go to AI eyeballing."  The gate is now the THREE REQUIREMENTS a
#  machine has to settle -- (1) a REAL build, (2) delivery hygiene, (3) references
#  reachable incl. image freshness -- plus verify-entry, which is a precondition
#  (the gate must not be a lie about itself).  Every item kept below can name a
#  CONCRETE defect it caught on this project (each item header says which one).
#
#  The previous 47-item version is preserved verbatim at
#  .ai-tmp/test/sink4-verify-backup.ps1
#  (SHA256 88A0598938FEA5BC379834EF9FE580D9FF77E909110F836A783392C0198A1ACA).
#
#  ITEMS KEPT (6):
#    1 verify-entry              framework does not crash + the one-command entry surface runs
#    2 delivery-hygiene          file/size budget, no bin|obj|*-bak-*, no stray temp or
#                                handoff doc, every .ps1 parses and carries no ANSI trap
#    3 editor-assembly-compiles  the only real compile judgement (Assets/Editor/**)
#    4 evidence-freshness        every cited shot is at least as new as ITS OWN row's code
#                                (also prints the batch freeze point T0)
#    5 reference-table-refs      cited carrier paths / file:line exist on disk (REAL dangling only)
#    6 shot-citations            every evidence shot cited by the acceptance table exists
#
#  REMOVED in the 2026-09-24 gate trim: brand-credit (the `clover-engine` source
#  grep + the rendered home-screen dump -- a look-at-the-screen judgement, not a
#  script's) and the gate self-test window warning (a read-only notice, never a
#  gate row).
#
#  NOTE: this file must stay ASCII-only.  Windows PowerShell 5.1 parses a .ps1 as
#  ANSI/GBK when the file has no UTF-8 BOM, so a Chinese path written literally here
#  can break parsing or make -match fail silently.  Every Chinese token below is
#  therefore built from code points.
#
#  Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1
# ============================================================================
param([string]$PlanDir = '', [string]$NodeDump = '', [string]$CodeRoots = '', [string]$Ledger = '',
      [string]$EnvCheck = '', [string]$ShotDir = '', [string]$Ps1Root = '', [string]$ProbeRoot = '',
      [string]$GateTemplate = '')
$ErrorActionPreference = 'Stop'
# The nine test seams of the 47-item version are GONE (they existed only so the gate
# self-test could inject a defect into a sandbox path).  The parameters are still
# ACCEPTED so an existing caller does not die on parameter binding, but an overridden
# run says out loud that the override is ignored -- otherwise a reader would believe a
# sandbox was judged while the REAL project paths were.
$seam = ''
foreach ($sp in @($PlanDir, $NodeDump, $CodeRoots, $Ledger, $EnvCheck, $ShotDir, $Ps1Root, $ProbeRoot, $GateTemplate)) {
  if (($sp -ne '') -and ($seam -eq '')) { $seam = $sp }
}
$root = Split-Path $PSScriptRoot -Parent
$script:fail = 0
$script:human = 0

function Say([string]$status, [string]$name, [string]$detail) {
  Write-Output ("{0,-11} {1}  {2}" -f $status, $name, $detail)
}
function Sub([string]$text) { Write-Output ("            " + $text) }
function Read-Text([string]$path) { return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) }

# ---- non-ASCII tokens, built from code points (keeps this file ASCII-only) --
$planDir   = Join-Path $root ([char[]]@(0x7B56, 0x5212) -join '')                             # ce hua
$specTable = Join-Path $planDir ((([char[]]@(0x9A8C, 0x6536, 0x8868)) -join '') + '.md')      # yan shou biao
$cProg     = ([char[]]@(0x8FDB, 0x5EA6) -join '')                                             # progress
$cHand     = ([char[]]@(0x4EA4, 0x63A5) -join '')                                             # handover
$cEntity   = ([char[]]@(0x5B9E, 0x4F53, 0x6E05, 0x5355) -join '') + '.tsv'                    # entity list

# ---- fixed locations -------------------------------------------------------
$codeDir   = Join-Path $root 'client\Assets\Scripts'
$editorDir = Join-Path $root 'client\Assets\Editor'
$probesDir = Join-Path $root 'tools\probes'
# Evidence shots are ONE-OFF artifacts: they live under .ai-tmp/screenshots/.
$shotDir   = Join-Path $root '.ai-tmp\screenshots'
$tmpRoot   = Join-Path $root '.ai-tmp'
$logPath   = Join-Path $root '.ai-tmp\test\dispatch-log.tsv'
$wsRoot    = Split-Path $root -Parent
$cut24     = (Get-Date).AddHours(-24)

if ($seam -ne '') {
  Write-Output ('NOTE        seams-removed  an override was passed (' + $seam + ') but the gate self-test seams were cut on 2026-09-24; this run judges the REAL project paths (NOT a sandbox)')
  Write-Output ''
}

function Invoke-Checks {
  $specTxt = ''
  if (Test-Path $specTable) { $specTxt = Read-Text $specTable }

  # The acceptance body = the digit-keyed rows inside the A-E sections.
  $accRows = @()
  $inAcc = $false
  foreach ($ln in @($specTxt -split "`r?`n")) {
    if ($ln.StartsWith('## ')) { $inAcc = ($ln -match '^##\s+[A-E]\.'); continue }
    if ($inAcc -and $ln -match '^\|\s*[A-Z]?\d+\s*\|') { $accRows += $ln }
  }

  # --- 1) verify-entry -- the framework runs and the entry surface is complete -------------
  # CAUGHT: the whole gate can die on an unhandled throw (measured 2026-09-23: a locked
  # ledger made it exit after 10 lines, judging NOTHING) -- hence the try/catch below,
  # which reports `verify-script-crash` and still prints a summary.
  $selfPath  = Join-Path $root 'tools\verify.ps1'
  $companions = @()
  foreach ($cp in @('tools\env-check.ps1')) {
    $pp = Join-Path $root ($cp -replace '/', '\')
    if (-not (Test-Path -LiteralPath $pp)) { $companions += $cp }
    elseif ((Get-Item -LiteralPath $pp).Length -le 0) { $companions += ($cp + ' (empty)') }
  }
  if (-not (Test-Path -LiteralPath $selfPath)) {
    $script:fail++
    Say 'FAIL' 'verify-entry' 'tools\verify.ps1 is not on disk -- the one-command re-check entry is missing'
  } elseif ($companions.Count -gt 0) {
    $script:fail++
    Say 'FAIL' 'verify-entry' ('entry surface incomplete, missing/empty: ' + ($companions -join ', '))
  } else {
    Say 'PASS' 'verify-entry' ('tools\verify.ps1 executed (' + (Get-Item -LiteralPath $selfPath).Length + ' bytes) with companion script present (env-check.ps1)')
  }

  # --- 2) delivery-hygiene (merged: tmp-budget + stray-temp-files + no-escaped-artifacts +
  #        no-handoff-docs + ps1-ascii + sampler-selfcheck) ---------------------------------
  # Each half CAUGHT a real defect on this project:
  #   * budget: .ai-tmp measured at 3372 files / 990.7 MB on 2026-09-24 (11x the file budget);
  #   * stray .cs: `client/_dev/` accumulated ~100 probe files;
  #   * escaped artifacts: a project file created OUTSIDE the project tree (workspace root / brain zone);
  #   * handoff docs: files named docs/<handover>-*.md / NEXT.md were actually written
  #     (a future agent was told to pick up the rest -- exactly what the ruling forbids);
  #   * ps1-ascii: a Chinese string literal inside a BOM-less .ps1 -- PS 5.1 reads it as ANSI,
  #     the pattern silently never matches, and "nothing found" looks exactly like PASS;
  #   * sampler self-check: a driver .ps1 under .ai-tmp with 138 non-ASCII bytes and no BOM.
  $hy = @(); $hySub = @()

  # (a) stray one-off .cs outside the sanctioned temp area
  $nStray = @(Get-ChildItem $root -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match '\\(_dev|_assets_src|_assets_tmp)\\' }).Count
  if ($nStray -gt 0) { $hy += ('' + $nStray + ' stray .cs under _dev/_assets_src/_assets_tmp') }

  # (b) handover / progress documents must not exist
  $badDoc = @(Get-ChildItem $root -Recurse -Filter *.md -File -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -notmatch '\\Library\\' } |
              Where-Object { $_.Name -like 'NEXT*' -or $_.Name.Contains($cProg) -or $_.Name.Contains($cHand) } |
              ForEach-Object { $_.Name })
  if ($badDoc.Count -gt 0) { $hy += ('' + $badDoc.Count + ' handoff/progress doc(s)'); $hySub += ($badDoc -join ', ') }

  # (c) one-off artifacts outside the project (only .ai-tmp/test/ is ok)
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
  if ($esc.Count -gt 0) { $hy += ('' + $esc.Count + ' project file(s) created outside the project'); $esc | ForEach-Object { $hySub += $_.FullName } }

  # (d) .ai-tmp size budget + cache/backup dirs.  The number is a CONSTANT here (one place).
  #     NOTE (main-agent ruling, 2026-09-24 cut round): the FILE-COUNT threshold (300 files) is
  #     REMOVED on purpose -- a file count is not a defect indicator: one evidence shot IS one
  #     file, so deleting one means losing evidence, and the red line only pushed people into
  #     deleting evidence or writing ceremony.  Size + cache dirs + the .ps1 half below are what
  #     this item judges.  The file count is still PRINTED as information, never as a verdict.
  $tbMaxMB    = 200
  $tbCacheNames = @('gocache', 'gopath', 'gotmp', 'node_modules', 'bin', 'obj')
  $tbFiles = @(Get-ChildItem -LiteralPath $tmpRoot -Recurse -File -Force -ErrorAction SilentlyContinue)
  $tbBytes = 0.0
  foreach ($tbF in $tbFiles) { $tbBytes += $tbF.Length }
  $tbMB = [math]::Round($tbBytes / 1MB, 1)
  $tbBad = @(Get-ChildItem -LiteralPath $tmpRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue |
             Where-Object { ($tbCacheNames -contains $_.Name.ToLower()) -or ($_.Name -like '*-bak-*') })
  if ($tbMB -gt $tbMaxMB) { $hy += ('' + $tbMB + ' MB .ai-tmp > ' + $tbMaxMB + ' MB') }
  if ($tbBad.Count -gt 0) {
    $hy += ('' + $tbBad.Count + ' cache/backup dir(s) inside .ai-tmp: ' + (($tbBad | Select-Object -First 5 | ForEach-Object { $_.Name }) -join ', '))
    $tbBad | Select-Object -First 5 | ForEach-Object { $hySub += ('dir: ' + $_.FullName.Substring($tmpRoot.Length + 1)) }
  }
  if ($tbMB -gt $tbMaxMB) {
    $tbFiles | Sort-Object Length -Descending | Select-Object -First 5 | ForEach-Object { $hySub += ('' + $_.Length + ' B  ' + $_.FullName.Substring($tmpRoot.Length + 1)) }
  }

  # (e) ps1-ascii -- PS 5.1 reads a BOM-less .ps1 as ANSI: a mojibake COMMENT is cosmetic,
  #     Chinese inside a string literal or a pattern silently changes behaviour.
  $ps1Root = Join-Path $root 'tools'
  $ps1Files = @(Get-ChildItem $ps1Root -Recurse -Filter '*.ps1' -File -ErrorAction SilentlyContinue)
  $ps1NonAscii = @()
  foreach ($pf in $ps1Files) {
    $pn = 0; $inBlock = $false
    foreach ($pln in @([System.IO.File]::ReadAllLines($pf.FullName))) {
      $pn++
      $pt = $pln.Trim()
      if ($inBlock) { if ($pt.Contains('#>')) { $inBlock = $false }; continue }
      if ($pt.StartsWith('<#')) { if (-not $pt.Contains('#>')) { $inBlock = $true }; continue }
      if ($pt.StartsWith('#')) { continue }
      foreach ($pch in $pln.ToCharArray()) {
        if ([int]$pch -gt 127) { $ps1NonAscii += ($pf.FullName.Substring($root.Length).TrimStart('\') + ':' + $pn); break }
      }
    }
  }
  if ($ps1NonAscii.Count -gt 0) {
    $hy += ('' + $ps1NonAscii.Count + ' tools/**/*.ps1 line(s) carry non-ASCII bytes OUTSIDE comments')
    $ps1NonAscii | Select-Object -First 5 | ForEach-Object { $hySub += $_ }
  }

  # (f) sampler self-check -- .ai-tmp driver scripts inside the window must parse and must
  #     not be ANSI-trapped.
  $badPs = @(); $nPs = 0
  if (Test-Path $tmpRoot) {
    foreach ($f in @(Get-ChildItem $tmpRoot -Recurse -Filter *.ps1 -File -ErrorAction SilentlyContinue)) {
      if (-not ($f.LastWriteTime -gt $cut24)) { continue }
      $nPs++
      $b = [System.IO.File]::ReadAllBytes($f.FullName)
      $bom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
      $nonAscii = @($b | Where-Object { $_ -gt 127 }).Count
      $issues = @()
      if ((-not $bom) -and $nonAscii -gt 0) { $issues += ("ANSI trap: $nonAscii non-ASCII byte(s) without BOM") }
      $enc = [System.Text.Encoding]::UTF8
      if (-not $bom) { $enc = [System.Text.Encoding]::Default }
      $text = $enc.GetString($b)
      if ($bom) { $text = $text.TrimStart([char]0xFEFF) }
      $psk = $null; $per = $null
      [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$psk, [ref]$per)
      if (@($per).Count -gt 0) { $issues += ("$(@($per).Count) syntax error(s)") }
      if ($issues.Count -gt 0) { $badPs += ($f.Name + ': ' + ($issues -join '; ')) }
    }
  }
  if ($badPs.Count -gt 0) { $hy += ('' + $badPs.Count + ' .ai-tmp driver script(s) with issues'); $badPs | ForEach-Object { $hySub += $_ } }

  if ($hy.Count -eq 0) {
    Say 'PASS' 'delivery-hygiene' ('' + $tbMB + ' MB in .ai-tmp (size budget ' + $tbMaxMB + ' MB; the ' + $tbFiles.Count + ' file(s) are printed as information, a file-count threshold is deliberately NOT judged); ' + $ps1Files.Count + ' tools/**/*.ps1 ASCII-clean outside comments; ' + $nPs + ' .ai-tmp driver script(s) parse; no stray .cs / handoff doc / escaped artifact / cache-or-backup dir')
  } else {
    $script:fail++
    Say 'FAIL' 'delivery-hygiene' (($hy -join '; ') + ' -- fixes: delete one-off shots / per-frame TSVs, point GOCACHE/GOPATH/GOTMPDIR at the system default, move any outside file into .ai-tmp/test/, and see the lines below')
    $hySub | Select-Object -First 12 | ForEach-Object { Sub $_ }
  }

  # --- 3) editor-assembly-compiles -- the only real compile judgement ----------------------
  # CAUGHT (2026-09-24): client\Assets\Editor\MapGen\MapBakeRunner.cs(122,21): error CS0117:
  # "MapBakeOptions" has no "MarkerRootName" -- a broken Editor script is a SILENT killer:
  # Unity stays on "Scripts still have compile errors" and Play never starts.
  # NOTE on the trap: tools/probes/compile-check.ps1 in its DEFAULT mode compiles the Runtime
  # assembly and does NOT cover Assets/Editor/** as Unity's Editor assembly; this item runs
  # `-Editor -Engine`, which sets UNITY_EDITOR, compiles Assets/Editor/** as its own assembly
  # and compiles the engine SOURCE in (the prebuilt CloverEngine.*.dll under
  # Library\ScriptAssemblies is machine-local and can lag the source it is meant to provide --
  # e.g. it lacks MapBakeOptions.MarkerRootName while Editor\MapBake\MapBakeOptions.cs:119
  # declares it public).  A red here therefore means the current sources disagree.
  $ccScript = Join-Path $root 'tools\probes\compile-check.ps1'
  if (-not (Test-Path $ccScript)) {
    $script:fail++
    Say 'FAIL' 'editor-assembly-compiles' ('missing ' + $ccScript + ' -- the Editor assembly is unverifiable offline')
  } else {
    $ccTmp = Join-Path $tmpRoot 'verify-editor-compile.txt'
    $ccOut = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $ccScript -Editor 1 -Engine 1 -Quiet 1 2>&1 | ForEach-Object { [string]$_ })
    $ccRc = $LASTEXITCODE
    $ccOut | Set-Content -Encoding UTF8 $ccTmp
    $ccErrs = @($ccOut | Where-Object { $_ -match ': error CS\d+' })
    if (($ccRc -eq 0) -and ($ccErrs.Count -eq 0)) {
      Say 'PASS' 'editor-assembly-compiles' ('Assets/Editor/** compiles as the Editor assembly (UnityEditor.dll + UnityEditor.CoreModule, UNITY_EDITOR defined), 0 error -- raw output: ' + $ccTmp)
    } else {
      $script:fail++
      Say 'FAIL' 'editor-assembly-compiles' ('' + $ccErrs.Count + ' compile error(s) in Assets/Editor/** (csc exit=' + $ccRc + ') -- raw output: ' + $ccTmp)
      $ccErrs | Select-Object -First 10 | ForEach-Object { Sub $_ }
    }
  }

  # --- 4) evidence-freshness -- judged PER ACCEPTANCE ROW ----------------------------------
  # CAUGHT (2026-09-24): 29 of 70 (row,shot) pairs stale, e.g. `row M2: shot 33_mainmenu.png
  # (09-22 13:39) is older than that row implementation file MainMenuPanel.cs (09-24 18:15)`
  # -- the picture a reader trusts was taken two days before the code it is supposed to show.
  # Scope = the ROW: a shot is void only when it is older than ITS OWN row's implementation
  # file, never when some unrelated file elsewhere changed (a global reading once voided 59
  # shots after one .cs edit).  This merged item also prints the batch freeze point T0 of the
  # newest contact-sheet manifest (the old `freeze-before-capture` row pinned the same
  # comparison; keeping one row keeps rule and check on one source).
  $shotFiles = @{}
  if (Test-Path $shotDir) {
    foreach ($f in @(Get-ChildItem $shotDir -Filter *.png -File -ErrorAction SilentlyContinue)) { $shotFiles[$f.Name.ToLower()] = $f }
  }
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
  if ($accRows.Count -gt 0) {
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
        foreach ($m in [regex]::Matches($t, '([A-Za-z_][A-Za-z0-9_\-]*\.cs)')) {
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
          $void += [pscustomobject]@{ Row = $rid; Text = ('row ' + $rid + ': shot ' + $sf.Name + ' (' + $sf.LastWriteTime.ToString('MM-dd HH:mm') +
                   ') is older than that row implementation file ' + (Split-Path $newest -Leaf) +
                   ' (' + $tImpl.ToString('MM-dd HH:mm') + ')') }
        }
      }
    }
  }
  # batch freeze point (merged from `freeze-before-capture`), printed, never double counted
  $batchNote = 'batch freeze point: no *.manifest.tsv under tools\probes (nothing to pin)'
  $mf = @(Get-ChildItem $probesDir -Filter '*.manifest.tsv' -File -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime | Select-Object -Last 1)
  if (($mf.Count -gt 0) -and ($shotFiles.Count -gt 0)) {
    $bNames = @([regex]::Matches((Read-Text $mf[0].FullName), '([0-9A-Za-z_\-\.]+\.png)') |
                ForEach-Object { $_.Groups[1].Value.ToLower() } | Sort-Object -Unique)
    $bOn = @($bNames | Where-Object { $shotFiles.ContainsKey($_) })
    if ($bOn.Count -gt 0) {
      $t0 = ($bOn | ForEach-Object { $shotFiles[$_].LastWriteTime } | Sort-Object | Select-Object -First 1)
      $batchNote = 'batch freeze point T0 = ' + $t0.ToString('MM-dd HH:mm:ss') + ' (' + $bOn.Count + ' png of ' + $mf[0].Name + '); the batch shots are scored by the same per-row rule above'
    }
  }
  # row-scoped NAMED ADJUDICATION: a void (row,shot) pair may be REGISTERED as known/owned:
  #   # adjudicated: evidence-freshness -- row <id> -- <reason >= 12 chars>
  # every void pair registered -> HUMAN-ONLY (the owning slice re-captures that row only);
  # any void pair NOT registered -> FAIL; a registration that outlived the void -> FAIL.
  $adj = @{}
  if (Test-Path $logPath) {
    foreach ($line in @([System.IO.File]::ReadAllLines($logPath, [Text.Encoding]::UTF8))) {
      $l = [string]$line
      if (-not $l.TrimStart().StartsWith('#')) { continue }
      $mAdj = [regex]::Match($l, '(?i)#\s*adjudicated:\s*evidence-freshness\s*--\s*(.*)$')
      if (-not $mAdj.Success) { continue }
      $reason = $mAdj.Groups[1].Value.Trim()
      $mRow = [regex]::Match($reason, '(?i)\brow\s+([A-Za-z]?\d+)\b')
      if ((-not $mRow.Success) -or ($reason.Length -lt 12)) { continue }
      $adj[$mRow.Groups[1].Value] = $reason
    }
  }
  $voidIds = @($void | ForEach-Object { [string]$_.Row })
  $freeVoid = @($void | Where-Object { -not $adj.ContainsKey([string]$_.Row) })
  $adjVoid = @($void | Where-Object { $adj.ContainsKey([string]$_.Row) })
  $staleAdj = @($adj.Keys | Where-Object { $voidIds -notcontains [string]$_ })
  if (($cmpRows -eq 0) -or ($cmpPairs -eq 0)) {
    $script:human++
    Say 'HUMAN-ONLY' 'evidence-freshness' "no (row,shot) pair could be compared (rows without a shot: $noShotRows; rows without a resolvable implementation file: $noImplRows; cited shot names that do not resolve to a file: $goneShots) - needs a manual check"
    Sub $batchNote
  } elseif ($freeVoid.Count -gt 0) {
    $script:fail++
    Say 'FAIL' 'evidence-freshness' "$($freeVoid.Count) of $cmpPairs (row,shot) pair(s) are stale and NOT adjudicated => only those rows are void, the rest stay valid (to register a known/owned void: '# adjudicated: evidence-freshness -- row <id> -- <reason >= 12 chars>' in .ai-tmp/test/dispatch-log.tsv)"
    $freeVoid | ForEach-Object { Sub $_.Text }
    if ($adjVoid.Count -gt 0) { Sub ('also stale but adjudicated (not counted here): ' + (($adjVoid | ForEach-Object { [string]$_.Row }) -join ',')) }
    Sub $batchNote
  } elseif ($staleAdj.Count -gt 0) {
    $script:fail++
    Say 'FAIL' 'evidence-freshness' "$($staleAdj.Count) adjudicated row(s) are NOT stale any more ('$($staleAdj -join ',')') => the registration outlived the void, remove it from .ai-tmp/test/dispatch-log.tsv"
    Sub $batchNote
  } elseif ($void.Count -eq 0) {
    Say 'PASS' 'evidence-freshness' "$cmpRows acceptance row(s) / $cmpPairs (row,shot) pair(s) compared per row: every cited shot is at least as new as its own row implementation file; $noShotRows row(s) without a shot and $noImplRows row(s) without a resolvable implementation file are HUMAN-ONLY; $goneShots unroutable shot name(s) are left to shot-citations"
    Sub $batchNote
  } else {
    $script:human++
    Say 'HUMAN-ONLY' 'evidence-freshness' "$($adjVoid.Count) of $cmpPairs (row,shot) pair(s) are stale and ADJUDICATED: the other $($cmpPairs - $adjVoid.Count) pair(s) are fresh. The void shot(s) are NOT valid evidence -- a human must have the owning slice re-capture those rows; remove the registration once re-captured"
    $adjVoid | ForEach-Object { Sub ($_.Text + '  || adjudication: ' + $adj[[string]$_.Row]) }
    Sub $batchNote
  }

  # --- 5) reference-table-refs -- cited carrier paths / file:line must exist on disk -------
  # CAUGHT (2026-09-22): section 0 of the reference table cited 4 carrier paths that were not
  # on disk, one of them labelled `Test-Path = True`; the differences registry carried 10+
  # more under `yuan-ban/cs16src/cs16game/...`.  Judged by tools/probes/scan-reftable-refs.py
  # (NOT re-implemented here) over the reference table AND the differences registry, so the
  # gate and the audit slice cannot disagree about the same data.
  # REAL DANGLING ONLY: when the cited file is missing but a same-name COPY sits next to it
  # (`X.orig` / `X.bak` / `X-bak`), that is an ambiguity about WHICH copy the citation means
  # and it is printed as a HINT with an actionable fix -- not a FAIL.  A citation whose file
  # and whose backup variants are all absent is a true dangling and FAILs.
  $refScan = Join-Path $root 'tools\probes\scan-reftable-refs.py'
  if (-not (Test-Path $refScan)) {
    $script:fail++
    Say 'FAIL' 'reference-table-refs' ('missing ' + $refScan + ' -- the reference table is unverifiable')
  } elseif ($null -eq (Get-Command python -ErrorAction SilentlyContinue)) {
    $script:fail++
    Say 'FAIL' 'reference-table-refs' 'python is not on PATH -- cannot run scan-reftable-refs.py'
  } else {
    $refTmp = Join-Path $tmpRoot 'verify-reftable-refs.txt'
    & python $refScan --plan-dir $planDir | Set-Content -Encoding UTF8 $refTmp
    $refRc = $LASTEXITCODE
    $refOut = @(Get-Content $refTmp -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 })
    $refLast = if ($refOut.Count -gt 0) { $refOut[$refOut.Count - 1] } else { '(no output)' }
    $refDang = @($refOut | Where-Object { $_ -match '^\s+DANGLING' })
    $realDang = @(); $hintDang = @()
    foreach ($dl in $refDang) {
      $dm = [regex]::Match([string]$dl, 'DANGLING\s+\[[^\]]*\]\s+lines\s+([^\[]*)\[([^\]]*)\]')
      $tok = if ($dm.Success) { $dm.Groups[1].Value } else { '' }
      $vars = @()
      if ($tok -ne '') {
        foreach ($suf in @('.orig', '.bak', '-bak')) {
          if (Test-Path -LiteralPath ($tok + $suf)) { $vars += (Split-Path ($tok + $suf) -Leaf) }
        }
      }
      if ($vars.Count -gt 0) { $hintDang += ([string]$dl).Trim() + '  HINT: a same-name copy exists (' + ($vars -join ', ') + ') -- re-point the citation at the file that is really meant, or restore the missing one' }
      else { $realDang += [string]$dl }
    }
    if (($refRc -eq 0) -and ($realDang.Count -eq 0)) {
      Say 'PASS' 'reference-table-refs' ($refLast + $(if ($hintDang.Count -gt 0) { ' (' + $hintDang.Count + ' ambiguous citation(s) reported as HINT, not FAIL)' } else { '' }))
      $refOut | Where-Object { $_ -match '^\s+index:|^\s+reachable-or-registered:' } | Select-Object -First 3 | ForEach-Object { Sub $_.Trim() }
      $hintDang | ForEach-Object { Sub $_ }
    } else {
      $script:fail++
      Say 'FAIL' 'reference-table-refs' ('' + $realDang.Count + ' citation(s) are TRULY dangling (file and every backup variant absent); scan verdict: ' + $refLast)
      $realDang | Select-Object -First 10 | ForEach-Object { Sub $_.Trim() }
      $hintDang | Select-Object -First 5 | ForEach-Object { Sub $_ }
      Sub 'every truly dangling carrier needs a row with what / why / source / expiry in the plan-dir carrier-reachability registry'
    }
  }

  # --- 6) shot-citations -- every evidence shot cited by the acceptance table exists --------
  # CAUGHT (2026-09-24): the acceptance table cited `_sheet.png`, `x.png`, `z-decal-pair.png`
  # and `z-decal-pair-zoom.png`, none of which exists under .ai-tmp/screenshots -- a cited
  # picture that is not there can never be re-opened, so the row's evidence is a dead link.
  # ASSET names are NOT evidence citations: the entity list registers ~319 asset names as
  # entities, and those files live under client/Assets/**.  A shape rule cannot separate the
  # two (a real contact sheet is cited as a BARE file name too), so the criterion is REGISTRY
  # MEMBERSHIP, and the exemption is printed rather than silent.
  if ($specTxt.Length -gt 0) {
    $entListPath = Join-Path $planDir $cEntity
    $assetNames = @{}
    $regNote = 'exemption registry missing: ' + $entListPath
    if (Test-Path $entListPath) {
      foreach ($ln in @([System.IO.File]::ReadAllLines($entListPath, [Text.Encoding]::UTF8))) {
        foreach ($m in [regex]::Matches([string]$ln, '[0-9A-Za-z_\-]+\.png')) { $assetNames[$m.Value] = $true }
      }
      $regNote = 'registry = ' + (Split-Path $entListPath -Leaf)
    }
    # The token is captured WITH the path it was written with (`.ai-tmp/test/mf-frames/_sheet.png`)
    # and resolved in order, because a BASENAME-ONLY lookup against .ai-tmp/screenshots reported
    # on-disk diagnostics as dangling (measured 2026-09-24: `_sheet.png` lives at
    # .ai-tmp/test/mf-frames/_sheet.png and `z-decal-pair.png` at .ai-tmp/test/z-decal-pair.png --
    # both cited by their real path, both flagged red by the old rule).  Resolution paths:
    #   1) project-relative     2) .ai-tmp-relative / .ai-tmp/test-relative
    #   3) .ai-tmp/screenshots-relative   4) BASENAME fallback anywhere under .ai-tmp
    # Which path a citation resolved through is COUNTED and printed, so the fallback is never a
    # silent way to pass, and a citation whose file and backup variants are all absent is the
    # only thing that FAILs (true dangling).
    $pngsAll = @([regex]::Matches($specTxt, '[0-9A-Za-z_/\\.\-]+\.png') | ForEach-Object { $_.Value } | Sort-Object -Unique)
    $pngs = @(); $exempt = @()
    foreach ($t in $pngsAll) {
      $b = Split-Path ($t -replace '/', '\') -Leaf
      if ($assetNames.ContainsKey([string]$b)) { $exempt += $t } else { $pngs += $t }
    }
    $pngs = @($pngs | Sort-Object -Unique)
    $byPathN = 0; $byBaseN = 0
    $missShot = @(); $hintShot = @()
    foreach ($p in $pngs) {
      $rel = ($p -replace '/', '\')
      if ($rel.StartsWith('.\')) { $rel = $rel.Substring(2) }
      if ($rel.StartsWith('\')) { $rel = $rel.Substring(1) }
      $cand = @((Join-Path $root $rel), (Join-Path $tmpRoot $rel), (Join-Path $shotDir $rel), (Join-Path $tmpRoot ('test\' + $rel)))
      $ok = (@($cand | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)).Count -gt 0
      if ($ok) { $byPathN++ } else {
        $bn = Split-Path $rel -Leaf
        # -Filter takes a wildcard pattern: a name carrying [ ] * ? would turn the fallback into a
        # match-anything lookup, so it only runs for plain file names.
        if ($bn -match '^[0-9A-Za-z_\.\-]+$') {
          $hitBn = @(Get-ChildItem $tmpRoot -Recurse -Filter $bn -File -ErrorAction SilentlyContinue | Select-Object -First 1)
          if ($hitBn.Count -gt 0) { $byBaseN++; $ok = $true }
        }
      }
      if ($ok) { continue }
      $bn2 = Split-Path $rel -Leaf
      $vars = @()
      foreach ($suf in @('.orig', '.bak', '-bak')) {
        if (Test-Path -LiteralPath (Join-Path $shotDir ($bn2 + $suf))) { $vars += ($bn2 + $suf) }
      }
      if ($vars.Count -gt 0) { $hintShot += ('shot: ' + $bn2 + '  HINT: a same-name copy exists under .ai-tmp/screenshots (' + ($vars -join ', ') + ') -- cite the copy that is meant') }
      else { $missShot += $p }
    }
    $resNote = '; resolution = ' + $byPathN + ' by the cited path / ' + $byBaseN + ' by basename under .ai-tmp'
    $exNote = ''
    if ($exempt.Count -gt 0) {
      $exNote = '; ' + $exempt.Count + ' cited name(s) exempt as asset names registered in the entity list'
    }
    if ($missShot.Count -eq 0) {
      Say 'PASS' 'shot-citations' ('' + $pngs.Count + ' cited evidence shot(s) all exist on disk' + $resNote + $exNote + ' (' + $regNote + ')' + $(if ($hintShot.Count -gt 0) { '; ' + $hintShot.Count + ' ambiguous citation(s) reported as HINT' } else { '' }))
      $hintShot | ForEach-Object { Sub $_ }
    } else {
      $script:fail++
      Say 'FAIL' 'shot-citations' ('' + $missShot.Count + ' cited evidence shot(s) are TRULY dangling (file absent at the cited path and no backup variant)' + $resNote + $exNote + ' (' + $regNote + ')')
      $missShot | ForEach-Object { Sub ('shot: ' + $_) }
      $hintShot | Select-Object -First 5 | ForEach-Object { Sub $_ }
    }
  } else {
    $script:fail++
    Say 'FAIL' 'shot-citations' ('missing ' + $specTable + ' -- the cited shots are unjudged, never a pass')
  }
}

# A crashing gate must never look green: catch everything and FAIL loudly.
try { Invoke-Checks }
catch {
  $script:fail++
  Say 'FAIL' 'verify-script-crash' $_.Exception.Message
}

Write-Output ''
Write-Output ("===== SUMMARY: FAIL={0}  HUMAN-ONLY={1}  ITEMS=6 =====" -f $script:fail, $script:human)
if ($script:fail -gt 0) { Write-Output 'RESULT: FAIL present -> the words done / delivered / verified must NOT be used' }
elseif ($script:human -gt 0) { Write-Output 'RESULT: all computable checks passed; the HUMAN-ONLY items still need a human' }
exit $(if ($script:fail -gt 0) { 1 } else { 0 })
