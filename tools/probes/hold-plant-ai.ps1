# ============================================================================
# slice BH-R phase A -- ONE Play session that produces the AI RUNTIME evidence
# (CT hold-spot distribution / hold swap / T bot actually planting the C4).
#
# Why Play is unavoidable (play-log.tsv 4th column):
#   The three things judged all exist only inside the running simulation:
#   CsBotBrain's hold site table + swap timer and CsMatch's plant settlement run
#   in Play's Tick.  The offline assert (tools/probes/bot-hold-plant-check.py)
#   can only judge that the hold table is constructible; it cannot judge where
#   this round's CT bots actually walked, whether they swapped, or whether a T
#   bot really put the C4 down.
#
# It also leaves the editor IN PLAY on purpose: the second call
# (hold-plant-shot.ps1) re-shoots 40_bomb_planted through the real plant path in
# the SAME session, so one Play session carries both.
#
# ASCII-only on purpose (PS 5.1 reads a BOM-less .ps1 as ANSI).
# ============================================================================
$ErrorActionPreference = 'Continue'
$proj   = 'c:\Work\Server\f-v2\clover-project-cs16'
$client = Join-Path $proj 'client'
$drv    = Join-Path $proj '.ai-tmp\drivers\cs16-play-driver.cs'
$probe  = Join-Path $proj 'tools\probes\bot-hold-plant.cs'
$tmp    = Join-Path $proj '.ai-tmp\test'
$stateF = Join-Path $proj '.ai-tmp\drivers\state.txt'
$outF   = Join-Path $tmp 'bh-hold-plant.tsv'
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

$reason = 'the CT hold-spot distribution, the 8 s hold swap and "a T bot really plants the C4" all live only inside the running Tick (CsBotBrain / CsMatch); offline asserts cover the hold table only, so this round''s walked positions, swaps and plant event have no offline equivalent'
[IO.File]::AppendAllText($playLog, ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + "]`tclover-impl`tcs16-sliceBH-R-hold-plant-ai`t" + $reason + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

if (Test-Path $outF) { Remove-Item $outF -Force }

Mark 'phase 0: leave any session, open Boot, enter Play'
& unity command editor_stop --format tsv 2>&1 | Out-String -Width 200 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 2
& unity command open_scene --path 'Assets/Scenes/Boot.unity' --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 2
& unity command editor_play --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 12

Mark 'phase 1: environment baseline (rendering device decides whether frame-time numbers mean anything)'
$envTxt = Join-Path $tmp 'bh-r-env.txt'
& unity command eval_file --file (Join-Path $proj 'tools\probes\check-gpu-device.cs') --format tsv 2>&1 |
  Out-String -Width 800 | Set-Content -Encoding UTF8 $envTxt
Get-Content $envTxt

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
Run $drv 'Cs16Drv.Entry.SnapHud'

Mark 'phase 4: start the read-only AI probe (ticks by itself from now on)'
Run $probe 'BotHoldPlant.Begin'

Mark 'phase 5: let the sim run -- poll the probe file until a bot really plants (or 260 s)'
$deadline = (Get-Date).AddSeconds(260)
$lastLen = -1
$planted = $false
while ((Get-Date) -lt $deadline) {
  if (Test-Path $outF) {
    $lines = @(Get-Content $outF -Encoding UTF8)
    if ($lines.Count -ne $lastLen) {
      $lastLen = $lines.Count
      Write-Host ('  lines=' + $lines.Count + '  last: ' + $lines[$lines.Count - 1])
    }
    if (($lines | Where-Object { $_ -match '^E\tTPLANTED\t' }).Count -gt 0) { $planted = $true; break }
    if (($lines | Where-Object { $_ -match '^E\tROUND\t' }).Count -ge 3) { Write-Host '  three rounds seen (no bot plant so far)'; break }
  }
  Start-Sleep -Seconds 5
}
Write-Host ('bot plant observed = ' + $planted)

Mark 'phase 6: stop the probe (keep the editor IN PLAY for the 40_bomb_planted re-shoot)'
Run $probe 'BotHoldPlant.Stop'

Mark 'phase 7: probe output summary'
if (Test-Path $outF) {
  Write-Host ('tsv bytes = ' + (Get-Item $outF).Length)
  Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like 'E*' } | Select-Object -First 60
} else { Write-Host 'NO PROBE OUTPUT' }
Write-Host '== hold-plant-ai done (editor left in Play) =='
