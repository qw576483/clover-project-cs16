# ============================================================================
# slice BH-R phase B -- re-shoot 40_bomb_planted.png through the REAL plant path,
# in the SAME Play session phase A left open.
#
# Why it must be a real Play session (play-log.tsv 4th column):
#   40_bomb_planted is a RENDERED frame -- the planted C4 + the HUD state only
#   exist as pixels.  The gate (tools/verify.ps1 check 6) voids the row because
#   the current png is older than its row's implementation file (CsBotBrain.cs,
#   changed by slice BG), so it has to be re-taken from a frame produced AFTER
#   that change -- not re-dated.
#
# The plant itself is the real one: hold E reaches CsMatch._localUseHeld only
# through CombatModule.FillInput inside PlayerModule.Update (order -200), so the
# driver writes SetUseHeld(true) at order -150 -- after the overwrite, before the
# sim consumes it.  No game code is changed.
#
# Captures go to TEMP names first (bh-r-tmp-*): the canonical png is only
# overwritten after the new frame has actually been looked at.
#
# ASCII-only on purpose (PS 5.1 reads a BOM-less .ps1 as ANSI).
# ============================================================================
$ErrorActionPreference = 'Continue'
$proj   = 'c:\Work\Server\f-v2\clover-project-cs16'
$client = Join-Path $proj 'client'
$drv    = Join-Path $proj '.ai-tmp\drivers\cs16-play-driver.cs'
$tmp    = Join-Path $proj '.ai-tmp\test'
$shots  = Join-Path $proj '.ai-tmp\screenshots'
$stateF = Join-Path $proj '.ai-tmp\drivers\state.txt'
$playLog = Join-Path $tmp 'play-log.tsv'
Set-Location $client

function St([string]$text) {
  [System.IO.File]::WriteAllText($stateF, $text, (New-Object System.Text.UTF8Encoding($false)))
}
function Mark([string]$t) { Write-Host ''; Write-Host ('=========== ' + $t + ' ===========') }
function Run([string]$entry) {
  $o = & unity command run_script --file $drv --entry $entry --format json 2>&1 | Out-String -Width 1000000
  $j = $null
  try { $j = $o | ConvertFrom-Json } catch { }
  if ($null -eq $j -or $j.data.result.success -ne $true) {
    Write-Host ('FAIL ' + $entry + ' :: ' + (($j.data.result.diagnostics | Where-Object { $_.severity -eq 'error' } | ForEach-Object { $_.id + ' ' + $_.message }) -join ' | '))
    return ''
  } else { $r = [string]$j.data.result.result; Write-Host ('OK   ' + $entry + ' -> ' + $r); return $r }
}
function Cap([string]$name, [int]$w, [int]$h) {
  $f = Join-Path $tmp 'bh-r-cap.json'
  & unity command capture_game_view --source screen --width $w --height $h --include_inline_image true --format json |
    Out-String -Width 100000000 | Set-Content -Encoding UTF8 $f
  $j = Get-Content $f -Raw | ConvertFrom-Json
  if ($null -eq $j.data.result.base64) { Write-Host ('CAPFAIL ' + $name); return }
  $png = Join-Path $shots ($name + '.png')
  [IO.File]::WriteAllBytes($png, [Convert]::FromBase64String($j.data.result.base64))
  Write-Host ('saved ' + $name + ' ' + $w + 'x' + $h + ' bytes=' + (Get-Item $png).Length)
}

$reason = 'the planted C4 frame is a rendered pixel state produced by the REAL plant path (hold E -> progress -> BombPlanted); the gate voids row B7 because its png predates CsBotBrain.cs, so it must be re-rendered after that change rather than re-dated'
[IO.File]::AppendAllText($playLog, ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + "]`tclover-impl`tcs16-sliceBH-R-reshoot-40`t" + $reason + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

Mark 'phase 8: 4v4 T match (local = T) -- the plant chain needs both sides populated'
Run 'Cs16Drv.Entry.Remount'
Run 'Cs16Drv.Entry.StartMatchT'
Start-Sleep -Seconds 2
Run 'Cs16Drv.Entry.CloseEndPanels'
St ("godmode=1`n")
Start-Sleep -Seconds 12
Run 'Cs16Drv.Entry.SnapHud'

Mark 'phase 9: hand the local player the C4 and walk him onto the A bombsite marker'
Run 'Cs16Drv.Entry.Slot5C4'
St ("godmode=1`ntpbs=A`n")
Start-Sleep -Seconds 3
Run 'Cs16Drv.Entry.SnapHud'

Mark 'phase 10: hold E at order -150 -> real plant; then look down at the C4 (temp names only)'
St ("godmode=1`ntpbs=A`nuse=1`n")
Start-Sleep -Milliseconds 4200
St ("godmode=1`ntpbs=A`nuse=1`nlook=0,-55`n")
Start-Sleep -Milliseconds 1500
Run 'Cs16Drv.Entry.SnapHud'
Cap 'bh-r-tmp-40_bomb_planted' 1920 1080
Start-Sleep -Seconds 2
Run 'Cs16Drv.Entry.SnapHud'
Cap 'bh-r-tmp-40_bomb_planted_b' 1920 1080

Mark 'phase 11: console dump (the plant must show up in the game log)'
& unity command console --tail 1500 --format json 2>&1 | Out-String -Width 1000000 |
  Set-Content -Encoding UTF8 (Join-Path $tmp 'bh-r-console.json')
Write-Host ('console bytes: ' + (Get-Item (Join-Path $tmp 'bh-r-console.json')).Length)
Write-Host '== hold-plant-shot done (editor left in Play) =='
