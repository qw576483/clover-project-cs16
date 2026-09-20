# ============================================================================
#  Original-asset pull gate (clover-engine SKILL 1.9 items 6 + 7).
#
#  1.9 item 6: moving assets into the Unity project is a PULL, never a bulk copy
#     -- only the files that business code / level data really reference.
#  1.9 item 7: "this must be a gate, not a reminder" -- so it is computed here:
#     every file under client/Assets/Resources/** that is referenced by NO
#     hand-written source (Scripts/**/*.cs, Editor/**/*.cs) and by NO level data
#     (Resources/**/*.txt) is reported and the script exits non-zero.
#
#  ASCII-only on purpose: Windows PowerShell 5.1 parses a BOM-less .ps1 as
#  ANSI/GBK, so a literal non-ASCII keyword here can break parsing or make
#  -match fail silently (see reference/verify-template.md, hard pitfall 1).
#
#  Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-assets.ps1
# ============================================================================
$ErrorActionPreference = 'Stop'
$root  = Split-Path $PSScriptRoot -Parent
$resDir = Join-Path $root 'client\Assets\Resources'
$script:fail = 0

function Say([string]$status, [string]$name, [string]$detail) {
  Write-Output ("{0,-11} {1}  {2}" -f $status, $name, $detail)
}
function Sub([string]$text) { Write-Output ('            ' + $text) }

if (-not (Test-Path $resDir)) {
  $script:fail++
  Say 'FAIL' 'assets-pull-scope' ("missing: " + $resDir)
  exit 1
}

# --- haystack: everything that is allowed to reference a resource -----------
#  Hand-written sources first (Scripts / Editor), then LEVEL DATA (.txt), then
#  the text-serialized Unity assets that legitimately point at other assets:
#  an .anim clip is referenced by its .controller, a sprite by its .prefab, a
#  material by its .mat -- counting only .cs would report every animation clip
#  as unreferenced, and a gate that misfires is worse than no gate
#  (SKILL 1.11 item 11).
$hay = ''
$refRoots = @((Join-Path $root 'client\Assets\Scripts'),
              (Join-Path $root 'client\Assets\Editor'),
              (Join-Path $root 'client\Assets\Scenes'))
foreach ($d in $refRoots) {
  if (-not (Test-Path $d)) { continue }
  foreach ($f in @(Get-ChildItem $d -Recurse -File -ErrorAction SilentlyContinue |
                   Where-Object { @('.cs','.txt','.controller','.asset','.prefab','.unity','.mat','.anim','.json','.overrideController','.mask') -contains $_.Extension.ToLower() })) {
    $hay += [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
  }
}
foreach ($f in @(Get-ChildItem $resDir -Recurse -File -ErrorAction SilentlyContinue |
                 Where-Object { @('.txt','.controller','.asset','.prefab','.mat','.anim','.json') -contains $_.Extension.ToLower() })) {
  $hay += [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
}

# --- every resource file must appear in the haystack ------------------------
#     two accepted spellings (SKILL 1.9: paths converge into Core/ResPaths.cs):
#       a) the path relative to Resources/, without extension  ("Sound/SFX/sfx/jump")
#       b) the bare file name without extension                ("jump")
$all = @(Get-ChildItem $resDir -Recurse -File -ErrorAction SilentlyContinue |
         Where-Object { $_.Extension.ToLower() -ne '.meta' })
$orphan = @()
foreach ($f in $all) {
  $rel = $f.FullName.Substring($resDir.Length).TrimStart('\', '/').Replace('\', '/')
  $noExt = $rel
  if ($f.Extension.Length -gt 0) { $noExt = $rel.Substring(0, $rel.Length - $f.Extension.Length) }
  $base = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
  $hit = ($hay.IndexOf($noExt, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
         ($hay.IndexOf($base,  [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
  if (-not $hit) { $orphan += $rel }
}
$orphan = @($orphan | Sort-Object)

if ($orphan.Count -eq 0) {
  Say 'PASS' 'assets-pull-scope' "$($all.Count) resource file(s) under Resources/, every one referenced by code or level data"
} else {
  $script:fail++
  Say 'FAIL' 'assets-pull-scope' "$($orphan.Count)/$($all.Count) resource file(s) under Resources/ are referenced nowhere => bulk-copied from the original pack (SKILL 1.9 item 6)"
  $orphan | Select-Object -First 25 | ForEach-Object { Sub $_ }
  if ($orphan.Count -gt 25) { Sub ('... and ' + ($orphan.Count - 25) + ' more') }
}

Write-Output ''
Write-Output ("===== SUMMARY: FAIL={0} =====" -f $script:fail)
exit $(if ($script:fail -gt 0) { 1 } else { 0 })
