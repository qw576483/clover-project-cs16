# aim-blend-compile-units.ps1 - offline TRUE-compile of the two units that the aim-blend work touches.
#
#   editor-unit  = Cs16AnimData.cs + AnimSetup.cs + CsAimBlend.cs   (the export/controller chain)
#   runtime-full = EVERY Assets/Scripts/**/*.cs  (the Cs16.asmdef source set) with Cs16.dll dropped
#                  from the reference set, so a cross-file call such as
#                  ActorView -> CsViewTuning.PlayerAimStates is judged by the compiler instead of by
#                  a single-file extraction.
#
# Reference set + fake-error filtering come from tools/compile-file.ps1, which writes the response
# file <root>/.ai-tmp/test/compile-file/refs.rsp.  This script reuses it; if it is missing it runs
# compile-file.ps1 once to generate it (that is a project-local generated file, not a source).
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File tools/probes/aim-blend-compile-units.ps1 [-Root <repo root>]
# Exit:   0 = both units real errors = 0 ; 1 = a unit failed ; 2 = preflight / missing input.
# ASCII only (a BOM-less .ps1 is decoded as ANSI by PS 5.1).
param([string]$Root = '')
$ErrorActionPreference = 'Stop'
if ($Root -eq '') { $Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
if (!(Test-Path -LiteralPath $Root)) { Write-Host ('FAIL: root not found: ' + $Root); exit 2 }
$Root = (Resolve-Path -LiteralPath $Root).Path
$cf = Join-Path $Root 'tools\compile-file.ps1'
$ver = Join-Path $Root 'client\ProjectSettings\ProjectVersion.txt'
$scripts = Join-Path $Root 'client\Assets\Scripts'
foreach ($p in @($cf, $ver, $scripts)) {
    if (!(Test-Path -LiteralPath $p)) {
        Write-Host ('FAIL: missing input ' + $p)
        Write-Host ('  (expected a clover-project-cs16 checkout at ' + $Root + '; pass -Root <repo root>)')
        exit 2
    }
}

# ---- Unity editor root, same probing as tools/compile-file.ps1 ----
$verText = ''
foreach ($ln in [IO.File]::ReadAllLines($ver)) { if ($ln -match '^m_EditorVersion:\s*(\S+)') { $verText = $Matches[1]; break } }
$roots = @()
if ($verText -ne '') { $roots += (Join-Path 'C:\Program Files' ('Unity\Hub\Editor\' + $verText + '\Editor')) }
if ($env:UNITY_ROOT) { $roots += $env:UNITY_ROOT }
$unity = ''
foreach ($r in $roots) { if (Test-Path -LiteralPath (Join-Path $r 'Data\Managed\UnityEngine')) { $unity = $r; break } }
if ($unity -eq '') {
    Write-Host ('FAIL: Unity editor not found (ProjectVersion = ' + $verText + '); set UNITY_ROOT')
    exit 2
}
$dotnet = Join-Path $unity 'Data\NetCoreRuntime\dotnet.exe'
$cscG = Get-ChildItem -LiteralPath (Join-Path $unity 'Data\DotNetSdk\sdk') -Recurse -Filter 'csc.dll' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
if (!(Test-Path -LiteralPath $dotnet) -or $cscG -eq $null) { Write-Host 'FAIL: dotnet.exe / csc.dll not found'; exit 2 }
$csc = $cscG.FullName

# ---- reference response file (generate it via the in-repo tool when absent) ----
$base = Join-Path $Root '.ai-tmp\test\compile-file'
$rsp = Join-Path $base 'refs.rsp'
if (!(Test-Path -LiteralPath $rsp)) {
    Write-Host ('note: generating the reference set with tools/compile-file.ps1 -> ' + $rsp)
    & powershell -NoProfile -ExecutionPolicy Bypass -File $cf -File (Join-Path $Root 'client\Assets\Scripts\Module\View\CsAimBlend.cs') -Project $Root -UnityRoot $unity | Out-Null
}
if (!(Test-Path -LiteralPath $rsp)) {
    Write-Host ('FAIL: reference set not produced at ' + $rsp)
    Write-Host '  (run: powershell -File tools/compile-file.ps1 -File client/Assets/Scripts/Module/View/CsAimBlend.cs)'
    exit 2
}
$lines = [IO.File]::ReadAllLines($rsp)
$refs = $lines[0..($lines.Length - 2)]          # drop the trailing single source line
$work = Join-Path $base 'aimwire-units'
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Compile([string]$name, [string[]]$srcs, [string[]]$refsIn) {
    $outDll = Join-Path $work ($name + '.dll')
    if (Test-Path -LiteralPath $outDll) { Remove-Item -LiteralPath $outDll -Force }
    $l = @()
    foreach ($x in $refsIn) {
        if ($x -like '-out:*') { $l += ('-out:"' + $outDll + '"') } else { $l += $x }
    }
    foreach ($s in $srcs) { $l += ('"' + $s + '"') }
    $unitRsp = Join-Path $work ($name + '.rsp')
    [IO.File]::WriteAllLines($unitRsp, $l, (New-Object Text.UTF8Encoding($false)))
    Write-Host ('=== ' + $name + ' (' + $srcs.Count + ' source(s)) ===')
    $raw = & $dotnet $csc ('@' + $unitRsp) 2>&1 | Out-String
    # CS0436 = "type in the unit shadows the one in the referenced assembly": an artefact of compiling
    # a subset of one assembly.  CS0012 mentioning netstandard is the documented fake error.
    $real = @($raw -split "`r?`n" | Where-Object { $_ -match ': error CS' -and -not ($_ -match 'CS0436') })
    $fake = @($raw -split "`r?`n" | Where-Object { $_ -match 'CS0012' -and $_ -match 'netstandard' }).Count
    if ($fake -gt 0) { Write-Host ('  filtered fake CS0012(netstandard) x' + $fake) }
    if ($real.Count -eq 0) {
        if (Test-Path -LiteralPath $outDll) {
            Write-Host ('  PASS real errors = 0 (' + (Get-Item -LiteralPath $outDll).Length + ' B)')
            return $true
        }
        Write-Host '  FAIL: no real error but no dll produced'; Write-Host $raw; return $false
    }
    Write-Host ('  FAIL real errors = ' + $real.Count)
    $real | Select-Object -First 30 | ForEach-Object { Write-Host ('    ' + $_) }
    return $false
}

$ok1 = Compile 'editor-unit' @((Join-Path $Root 'client\Assets\Editor\Views\Cs16AnimData.cs'),
                               (Join-Path $Root 'client\Assets\Editor\Views\AnimSetup.cs'),
                               (Join-Path $Root 'client\Assets\Scripts\Module\View\CsAimBlend.cs')) $refs
$refsNoCs16 = @($refs | Where-Object { $_ -notmatch 'ScriptAssemblies\\Cs16\.dll' })
$all = Get-ChildItem -LiteralPath $scripts -Recurse -File -Filter '*.cs'
$ok2 = Compile 'runtime-full' ($all | ForEach-Object { $_.FullName }) $refsNoCs16
if ($ok1 -and $ok2) { Write-Host 'AIM-BLEND-COMPILE PASS'; exit 0 }
Write-Host 'AIM-BLEND-COMPILE FAIL'; exit 1
