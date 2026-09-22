# ============================================================================
# slice BM -- ONE Play session that captures the SIX NEW COLUMNS slice BM added to
# tools/probes/bot-phys.cs (wantTopGroundY / wantTopNormalY / wantTopDy /
# wantHighGroundY / wantHighNormalY / wantHighDy, appended at the end of every "B" row).
#
# Why this run exists: slice BM decided `want-no-ground` OFFLINE first (edit-mode
# raycast at the very same `want` coordinates, .ai-tmp/test/bm-want-top.cs, 754/754
# rows: 0 holes / 754 faces-above-the-foot / 0 faces-below). This run is the
# in-Play confirmation that the probe itself now writes those six values.
#
# Derived from tools/probes/bot-phys-ai-bl.ps1 (same chain, re-used, not rewritten);
# only changes: (1) output goes to bm-bot-phys.tsv so the frozen BL-R2 product
# bk-bot-phys.tsv is NOT overwritten; (2) the hold-plant probe is left out (slice BM
# changed no business code, so the exit/plant numbers are not being re-measured);
# (3) the poll window is shorter -- the interesting rows (the bot grinding against a
#  1.0-2.4 m face) already appear in the first seconds of the sim.
#
# Read-only: never writes state.txt except for the menu clicks, never mutates an actor
# or the match; it only reads positions / map queries / private brain fields.
#
# ASCII-only on purpose (PS 5.1 reads a BOM-less .ps1 as ANSI).
# ============================================================================
$ErrorActionPreference = 'Continue'
$proj   = 'c:\Work\Server\f-v2\clover-project-cs16'
$client = Join-Path $proj 'client'
$drv    = Join-Path $proj '.ai-tmp\drivers\cs16-play-driver.cs'
$probe  = Join-Path $proj 'tools\probes\bot-phys.cs'
$tmp    = Join-Path $proj '.ai-tmp\test'
$stateF = Join-Path $proj '.ai-tmp\drivers\state.txt'
$outF   = Join-Path $tmp 'bm-bot-phys.tsv'
$playLog = Join-Path $tmp 'play-log.tsv'
Set-Location $client

function St([string]$text) {
  [System.IO.File]::WriteAllText($stateF, $text, (New-Object System.Text.UTF8Encoding($false)))
}
function Mark([string]$t) { Write-Host ''; Write-Host ('=========== ' + $t + ' ===========') }
function Run([string]$file, [string]$entry) {
  $o = & unity command run_script --file $file --entry $entry --format json 2>&1 | Out-String -Width 1000000
  Write-Host ($o.Trim())
}
function Clk([string]$name) { St ("click=$name`n"); Start-Sleep -Milliseconds 800 }

$reason = 'the six slice-BM columns are produced by CsMap.TrySampleGround inside the running editor (real PhysX against the loaded de_dust2 scene + the live bot feet), so the in-Play values can only come from a Play session; the offline edit-mode raycast (.ai-tmp/test/bm-want-top.cs) produced the verdict but not a probe artifact'
[IO.File]::AppendAllText($playLog, ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + "]`tclover-impl`tcs16-sliceBM-bot-phys-tops`t" + $reason + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

if (Test-Path $outF) { Remove-Item $outF -Force }

Mark 'phase 0: leave any session, open Boot, enter Play'
& unity command editor_stop --format tsv 2>&1 | Out-String -Width 200 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 2
& unity command open_scene --path 'Assets/Scenes/Boot.unity' --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 2
& unity command editor_play --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 12

Mark 'phase 1: environment baseline (rendering device decides whether frame-time numbers mean anything)'
$envTxt = Join-Path $tmp 'bm-env.txt'
& unity command eval_file --file (Join-Path $proj 'tools\probes\check-gpu-device.cs') --format tsv 2>&1 |
  Out-String -Width 800 | Set-Content -Encoding UTF8 $envTxt
Get-Content $envTxt -Encoding UTF8

Mark 'phase 2: mount driver + menu chain into the map'
Run $drv 'Cs16Drv.Entry.Setup'
Clk 'Btn_NewGame'
Clk 'Btn_Start'
Start-Sleep -Seconds 6
Clk 'ctbutton'
Start-Sleep -Seconds 16

Mark 'phase 3: launch 4v4 with bots on BOTH sides (local = CT)'
Run $drv 'Cs16Drv.Entry.Remount'
Run $drv 'Cs16Drv.Entry.StartBots'
Start-Sleep -Seconds 2
Run $drv 'Cs16Drv.Entry.CloseEndPanels'
St ("godmode=1`n")
Start-Sleep -Seconds 8

Mark 'phase 4: start the read-only physics probe (it ticks by itself from now on)'
Run $probe 'BotPhys.Begin'

Mark 'phase 5: let the sim run -- poll (max 150 s / until 2 rounds + env row)'
$deadline = (Get-Date).AddSeconds(150)
$lastLen = -1
while ((Get-Date) -lt $deadline) {
  if (Test-Path $outF) {
    $lines = @(Get-Content $outF -Encoding UTF8)
    if ($lines.Count -ne $lastLen) {
      $lastLen = $lines.Count
      Write-Host ('  phys lines=' + $lines.Count + '  last: ' + $lines[$lines.Count - 1])
    }
    $rounds  = @($lines | Where-Object { $_ -match '^E\tROUND\t' }).Count
    $envRows = @($lines | Where-Object { $_ -match '^E\tENV\t' }).Count
    if ($rounds -ge 2 -and $envRows -ge 1) { Write-Host '  phys: enough rounds + env row'; break }
  }
  Start-Sleep -Seconds 5
}

Mark 'phase 6: stop the probe and leave Play'
Run $probe 'BotPhys.Stop'
& unity command editor_stop --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }

Mark 'phase 7: probe output summary (does the row carry the six slice-BM columns?)'
if (Test-Path $outF) {
  Write-Host ('tsv bytes = ' + (Get-Item $outF).Length)
  $brows = @(Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like 'B*' })
  Write-Host ('B rows  = ' + $brows.Count)
  $colcount = @{}
  foreach ($b in $brows) { $n = ($b -split "`t").Count; if ($colcount.ContainsKey($n)) { $colcount[$n]++ } else { $colcount[$n] = 1 } }
  Write-Host ('column counts across B rows: ' + (($colcount.GetEnumerator() | ForEach-Object { $_.Key.ToString() + 'x' + $_.Value }) -join ' '))
  Write-Host '-- first B row (raw) --'
  $brows | Select-Object -First 1
  Write-Host '-- last B row (raw) --'
  $brows | Select-Object -Last 1
  Write-Host '-- E ENV rows --'
  Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like "E`tENV*" }
  Write-Host '-- E REFLECT rows --'
  Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like "E`tREFLECT*" }
} else { Write-Host 'NO PROBE OUTPUT' }
Write-Host '== bot-phys-ai-bm done =='
