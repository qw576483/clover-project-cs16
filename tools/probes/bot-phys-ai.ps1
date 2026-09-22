# ============================================================================
# slice BK phase A -- ONE Play session that produces the per-frame physics
# evidence for "the bots physically cannot walk" (slice BJ left this as an
# unproven assumption: single-layer 2D bitmap + multi-layer geometry => the
# bots are walled in).
#
# Why Play is unavoidable (playlog 4th column):
#   The three readings that decide it exist only inside the running editor:
#   CsMap.CanStand / CsMap.ResolveMove / CsMap.TrySampleGround all go through
#   real PhysX (MeshCollider raycasts) against the loaded scene, and the bot's
#   current goal lives in the live CsBotBrain.  An offline host has no loaded
#   scene and no Physics, so none of these numbers can be produced offline.
#
# Read-only: the probe never writes state.txt, never injects input and never
# mutates an actor / match; it only reads positions, the map queries and (by
# reflection) the brain's private _goalPos.
#
# This phase STOPS the editor at the end (slice BK does not need to keep it).
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
$outF   = Join-Path $tmp 'bk-bot-phys.tsv'
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

$reason = 'CanStand / ResolveMove / TrySampleGround all go through real PhysX against the loaded scene and the bot goal lives in the live CsBotBrain, so per-frame "which cell is the bot stuck on, what is its foot gap, what is the ground normal, how far does ResolveMove actually move it" cannot be produced offline (an offline host has no scene and no Physics)'
[IO.File]::AppendAllText($playLog, ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + "]`tclover-impl`tcs16-切片BK-bot-phys`t" + $reason + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

if (Test-Path $outF) { Remove-Item $outF -Force }

Mark 'phase 0: leave any session, open Boot, enter Play'
& unity command editor_stop --format tsv 2>&1 | Out-String -Width 200 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 2
& unity command open_scene --path 'Assets/Scenes/Boot.unity' --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 2
& unity command editor_play --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 12

Mark 'phase 1: environment baseline (rendering device decides whether frame-time numbers mean anything)'
$envTxt = Join-Path $tmp 'bk-env.txt'
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

Mark 'phase 4: start the read-only per-frame physics probe (ticks by itself from now on)'
Run $probe 'BotPhys.Begin'

Mark 'phase 5: let the sim run -- poll the probe file (max 150 s / until 3 rounds + stuck rows)'
$deadline = (Get-Date).AddSeconds(150)
$lastLen = -1
while ((Get-Date) -lt $deadline) {
  if (Test-Path $outF) {
    $lines = @(Get-Content $outF -Encoding UTF8)
    if ($lines.Count -ne $lastLen) {
      $lastLen = $lines.Count
      Write-Host ('  lines=' + $lines.Count + '  last: ' + $lines[$lines.Count - 1])
    }
    $rounds = @($lines | Where-Object { $_ -match '^E\tROUND\t' }).Count
    $stuck  = @($lines | Where-Object { $_ -match '^E\tSTUCK\t' }).Count
    if ($rounds -ge 3 -and $stuck -ge 6) { Write-Host '  enough rounds + stuck rows'; break }
  }
  Start-Sleep -Seconds 5
}

Mark 'phase 6: stop the probe and leave Play'
Run $probe 'BotPhys.Stop'
& unity command editor_stop --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }

Mark 'phase 7: probe output summary'
if (Test-Path $outF) {
  Write-Host ('tsv bytes = ' + (Get-Item $outF).Length)
  Write-Host ('B rows = ' + @(Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like 'B*' }).Count)
  Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like 'E*' } | Select-Object -First 40
} else { Write-Host 'NO PROBE OUTPUT' }
Write-Host '== bot-phys-ai done =='
