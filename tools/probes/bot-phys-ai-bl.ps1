# ============================================================================
# slice BL phase A -- ONE Play session that produces the SAME per-frame physics
# evidence as slice BK, but with the probe's brain hook actually working:
#
#   slice BK outcome: goalValid==1 rows = 0 / 10656, state=- / planRoute=- / rwp=-1
#   => the whole brain read returned null and there was no anchor in the product
#      explaining WHICH of the four candidate causes it was.
#
# What changed in tools/probes/bot-phys.cs for this slice (slice BL):
#   1. reflection is now built BEFORE ReadBrains (slice BK built it lazily from
#      EnsureFields, which was only reachable AFTER a successful ReadBrains =>
#      deadlock: _fBrains==null => ReadBrains null => never EnsureFields => ...);
#   2. an "E ENV" self-diagnosis row is written into the SAME tsv whenever the
#      environment changes (bots / fBrains / brainsCount / module / mode / match /
#      map / which reflected fields resolved);
#   3. every failure path of the brain read writes an "E REFLECT reason=..." row;
#   4. BotModule lookup has a second road: MatchModule.GetComponent<BotModule>().
#
# Why Play is unavoidable (playlog 4th column): the readings live only inside the
# running editor -- CsMap.CanStand / ResolveMove / TrySampleGround go through real
# PhysX against the loaded scene, and the bot goal lives in the live CsBotBrain
# (a plain C# object owned by BotModule, not reachable from an offline host).
#
# Read-only: never writes state.txt, never injects input, never mutates an actor
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

$reason = 'CanStand / ResolveMove / TrySampleGround go through real PhysX against the loaded scene and the bot goal lives in the live CsBotBrain (plain C# object owned by BotModule), so per-frame "cell / foot gap / ground normal / ResolveMove step / goal / planRoute / rwp" cannot be produced offline (an offline host has no scene and no Physics and no live brain)'
[IO.File]::AppendAllText($playLog, ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + "]`tclover-impl`tcs16-sliceBL-bot-phys-env`t" + $reason + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

if (Test-Path $outF) { Remove-Item $outF -Force }

Mark 'phase 0: leave any session, open Boot, enter Play'
& unity command editor_stop --format tsv 2>&1 | Out-String -Width 200 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 2
& unity command open_scene --path 'Assets/Scenes/Boot.unity' --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 2
& unity command editor_play --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }
Start-Sleep -Seconds 12

Mark 'phase 1: environment baseline (rendering device decides whether frame-time numbers mean anything)'
$envTxt = Join-Path $tmp 'bl-env.txt'
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

Mark 'phase 5: let the sim run -- poll the probe file (max 165 s / until 3 rounds + brain rows)'
$deadline = (Get-Date).AddSeconds(165)
$lastLen = -1
while ((Get-Date) -lt $deadline) {
  if (Test-Path $outF) {
    $lines = @(Get-Content $outF -Encoding UTF8)
    if ($lines.Count -ne $lastLen) {
      $lastLen = $lines.Count
      Write-Host ('  lines=' + $lines.Count + '  last: ' + $lines[$lines.Count - 1])
    }
    $rounds  = @($lines | Where-Object { $_ -match '^E\tROUND\t' }).Count
    $envRows = @($lines | Where-Object { $_ -match '^E\tENV\t' }).Count
    $goalOn  = @($lines | Where-Object { $_ -match '^B\t' } | Where-Object { $_ -match '\t1\t' }).Count
    if ($rounds -ge 3 -and $envRows -ge 1) { Write-Host '  enough rounds + env row'; break }
  }
  Start-Sleep -Seconds 5
}

Mark 'phase 6: stop the probe and leave Play'
Run $probe 'BotPhys.Stop'
& unity command editor_stop --format tsv 2>&1 | Out-String -Width 300 | ForEach-Object { $_.Trim() }

Mark 'phase 7: probe output summary (E ENV / E REFLECT first -- that is the answer)'
if (Test-Path $outF) {
  Write-Host ('tsv bytes = ' + (Get-Item $outF).Length)
  Write-Host ('B rows  = ' + @(Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like 'B*' }).Count)
  Write-Host '-- E ENV rows --'
  Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like "E`tENV*" }
  Write-Host '-- E REFLECT rows --'
  Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like "E`tREFLECT*" }
  Write-Host '-- other E rows (first 20) --'
  Get-Content $outF -Encoding UTF8 | Where-Object { $_ -like 'E*' } |
    Where-Object { $_ -notlike "E`tENV*" } | Where-Object { $_ -notlike "E`tREFLECT*" } |
    Select-Object -First 20
} else { Write-Host 'NO PROBE OUTPUT' }
Write-Host '== bot-phys-ai-bl done =='
