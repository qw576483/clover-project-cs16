# one-shot self-test (slice BW-G-R), item 37 `editor-assembly-compiles` in BOTH directions,
# on REAL samples (SKILL 8.3 / anti-gaming 5):
#   (1) PASS  : the real client/Assets/Editor/**   -> csc exit 0, 0 `error CS`
#   (2) FAIL  : a COPY of the same tree with the historical defect re-injected
#               (bare `InternalEditorUtility` instead of the fully qualified name) -> CS0103
# The real client tree is only ever READ (it is copied out).
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$cc = Join-Path $root 'tools\probes\compile-check.ps1'
$src = Join-Path $root 'client\Assets\Editor'
$copy = Join-Path $root '.ai-tmp\test\bwgr-editor-src'
$outDir = Join-Path $root '.ai-tmp\test'
$bad = 0

function Run-CC([string]$editorDir) {
  if ($editorDir -eq '') {
    $o = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $cc -Editor 1 -Quiet 1 2>&1 | ForEach-Object { [string]$_ })
  } else {
    $o = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $cc -Editor 1 -Quiet 1 -EditorDir $editorDir 2>&1 | ForEach-Object { [string]$_ })
  }
  return @{ Rc = $LASTEXITCODE; Out = $o }
}

# ---- (1) real tree: must PASS -------------------------------------------------
$r1 = Run-CC ''
$e1 = @($r1.Out | Where-Object { $_ -match ': error CS\d+' })
$ok1 = (($r1.Rc -eq 0) -and ($e1.Count -eq 0))
if (-not $ok1) { $bad++ }
Write-Output ('SAMPLE editor-compile / real tree            rc=' + $r1.Rc + ' errors=' + $e1.Count + ' want rc=0 errors=0')
$r1.Out | Set-Content -Encoding UTF8 (Join-Path $outDir 'bwgr-editor-compile-A.txt')

# ---- (2) defective COPY: must FAIL with CS0103 --------------------------------
if (Test-Path $copy) { Remove-Item $copy -Recurse -Force }
New-Item -ItemType Directory -Force -Path $copy | Out-Null
Copy-Item (Join-Path $src '*') $copy -Recurse -Force
$vl = Join-Path $copy 'VisualLeakGuard.cs'
$txt = [IO.File]::ReadAllText($vl, [Text.Encoding]::UTF8)
$fixed = 'UnityEditorInternal.InternalEditorUtility.RepaintAllViews()'
$broken = 'InternalEditorUtility.RepaintAllViews()'
if ($txt.IndexOf($fixed) -lt 0) {
  Write-Output ('MISS editor-compile / defect injection : the fixed call was not found in ' + $vl)
  $bad++
} else {
  $txt2 = $txt.Replace($fixed, $broken)
  [IO.File]::WriteAllText($vl, $txt2, (New-Object Text.UTF8Encoding($false)))
  $r2 = Run-CC $copy
  $e2 = @($r2.Out | Where-Object { $_ -match ': error CS\d+' })
  $cs0103 = @($e2 | Where-Object { $_ -match 'CS0103' -and $_ -match 'VisualLeakGuard\.cs' })
  if (($r2.Rc -eq 0) -or ($e2.Count -eq 0) -or ($cs0103.Count -eq 0)) { $bad++ }
  Write-Output ('SAMPLE editor-compile / defective copy       rc=' + $r2.Rc + ' errors=' + $e2.Count + ' CS0103-in-VisualLeakGuard=' + $cs0103.Count + ' want rc<>0 / errors>=1 / CS0103>=1')
  $r2.Out | Set-Content -Encoding UTF8 (Join-Path $outDir 'bwgr-editor-compile-B.txt')
  $cs0103 | Select-Object -First 3 | ForEach-Object { Write-Output ('   ' + $_) }
}
Remove-Item $copy -Recurse -Force -ErrorAction Continue
Write-Output ('RESULT editor-compile-selftest: bad=' + $bad)
exit $(if ($bad -gt 0) { 1 } else { 0 })
