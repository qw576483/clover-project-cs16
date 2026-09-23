# 一次性自检脚本：用 Unity 自带的 Roslyn 把 Assets/Scripts 编译成临时程序集，
# 只做"能不能编译过"的校验（不跑 unity run / test / batchmode，不碰 Library/ScriptAssemblies）。
# 复用 Unity 生成的 Cs16.csproj 里的引用清单与宏定义，保证与编辑器编译条件一致。
# ⛔ param() 必须是脚本的第一条语句（放在 $ErrorActionPreference 之后会把参数绑定打红）。
# -Editor       compile the EDITOR assembly: only Assets/Editor/**, with UNITY_EDITOR defined and
#               Cs16.dll / CloverEngine.Editor.dll among the references (slice BW-G-R).  The
#               default mode compiles the RUNTIME assembly and does NOT cover Assets/Editor/** as
#               the Editor assembly -- which is exactly the blind spot that let a CS0103 sit in
#               Assets/Editor/VisualLeakGuard.cs while every offline check stayed green.
# -EditorDir    override the source root (used by the self-test to compile a defective COPY;
#               the real client/Assets/Editor/** is never written).
# -Quiet        print nothing on success (verify.ps1 calls it this way).
# NOTE: these are STRINGS, not [switch], on purpose -- the project calls its gate scripts through
# `powershell -File <script> -Arg val`, and a [switch] parameter is bound from a STRING there
# (measured 2026-09-23: "Cannot convert value "System.String" to type SwitchParameter"). Pass
# `-Editor 1 -Quiet 1`.
param([string]$Editor = '', [string]$EditorDir = '', [string]$Quiet = '')
$ErrorActionPreference = 'Stop'

# 项目根**从脚本自身位置推导**（本脚本在 <项目根>/tools/probes/ 下）：原来的相对写法
# `$projRoot = 'clover-project-cs16'` 要求调用者的 CWD == 工作区根，而 verify.ps1 / 本脚本
# 现在都由闸门自检从任意 CWD 调起 ⇒ 相对路径会把 csproj 找成
# `<项目根>\clover-project-cs16\client\Cs16.csproj`（2026-09-23 实测）。
$projRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$client   = Join-Path $projRoot 'client'
$tmp      = Join-Path $projRoot '.ai-tmp\test'

# ---- Unity 自带 Roslyn 的自动发现（⛔ 不绑定具体 Unity / SDK 版本）----
# 原先这里写死 `...\Hub\Editor\6000.6.0f1\...\sdk\8.0.318\...\csc.dll`：换一台机器 / 换个编辑器小版本
# 就报"找不到"，而报错里也看不出"该往哪找"。改成在 Hub 的 Editor 根下逐版本搜索：
#   <ProgramFiles>\Unity\Hub\Editor\<editor>\Editor\Data\DotNetSdk\sdk\<sdk>\Roslyn\bincore\csc.dll
# 取**最新**的编辑器（再取该编辑器里**最新**的 SDK）。版本号按数字段比较，不能用字符串序
# （否则 `6000.10.*` 会排在 `6000.6.*` 前面）。
function Get-VersionSortKey([string]$name) {
    $m = [regex]::Match($name, '^(\d+)\.(\d+)\.(\d+)')
    if (-not $m.Success) { return '000000.000000.000000' }
    return ('{0:D6}.{1:D6}.{2:D6}' -f [int]$m.Groups[1].Value,
            [int]$m.Groups[2].Value, [int]$m.Groups[3].Value)
}
function Find-UnityCsc {
    $hubRoot = Join-Path $env:ProgramFiles 'Unity\Hub\Editor'
    if (-not (Test-Path $hubRoot)) { return $null }
    $editors = @(Get-ChildItem -Path $hubRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object { Get-VersionSortKey $_.Name } -Descending)
    foreach ($e in $editors) {
        $sdkRoot = Join-Path $e.FullName 'Editor\Data\DotNetSdk\sdk'
        if (-not (Test-Path $sdkRoot)) { continue }
        $sdks = @(Get-ChildItem -Path $sdkRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object { Get-VersionSortKey $_.Name } -Descending)
        foreach ($s in $sdks) {
            $candidate = Join-Path $s.FullName 'Roslyn\bincore\csc.dll'
            if (Test-Path $candidate) { return $candidate }
        }
    }
    return $null
}
$csc = Find-UnityCsc
if (-not $csc) {
    # ASCII on purpose (slice BW-G-R item 36): a Chinese message in a BOM-less .ps1 is read as
    # ANSI by PS 5.1, so the operator sees mojibake instead of the diagnosis.
    throw ("cannot find Unity's bundled csc.dll. Searched '" + (Join-Path $env:ProgramFiles 'Unity\Hub\Editor') +
           "\<editor version>\Editor\Data\DotNetSdk\sdk\<sdk version>\Roslyn\bincore\csc.dll' for every installed " +
           "editor/SDK and found none. Confirm Unity was installed through Unity Hub (it ships the bundled " +
           "DotNetSdk), then re-run this script.")
}
if ($Quiet -eq '') { Write-Host ("csc = " + $csc) }

$csproj = Get-Content (Join-Path $client 'Cs16.csproj') -Raw

# ---- 引用 ----
$refs = [regex]::Matches($csproj, '<HintPath>([^<]+)</HintPath>') |
    ForEach-Object { $_.Groups[1].Value.Trim() } |
    Where-Object { Test-Path $_ } |
    Sort-Object -Unique

# 包内的程序集（UGUI / TextMeshPro / CloverEngine …）走 asmdef 引用，不写 HintPath：
# 直接取 Library/ScriptAssemblies 里的产物，但**排除 Cs16.dll 自己**（否则与被编译的源码重复）。
$refs += Get-ChildItem (Join-Path $client 'Library\ScriptAssemblies') -Filter *.dll |
    Where-Object { $_.Name -ne 'Cs16.dll' } |
    ForEach-Object { $_.FullName }
$refs = $refs | Sort-Object -Unique

# ---- 宏 ----
$defs = [regex]::Match($csproj, '<DefineConstants>([^<]+)</DefineConstants>').Groups[1].Value.Trim()

# ---- 源文件 ----
# DEFAULT mode = the Runtime assembly (Assets/Scripts).  It does NOT cover Assets/Editor/** as
# Unity's Editor assembly (that is a separate assembly compiled with UNITY_EDITOR): the previous
# version appended the Editor sources to this runtime compile, which is neither contract and is
# why a broken Editor script could pass.  Use -Editor for the Editor assembly.
$rsp = Join-Path $tmp 'compile.rsp'
$out = Join-Path $tmp 'Check.dll'
if ($Editor -ne '') {
    $srcRoot = $EditorDir
    if (($srcRoot -eq '') -or (-not (Test-Path -LiteralPath $srcRoot))) { $srcRoot = Join-Path $client 'Assets\Editor' }
    $sources = @(Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
    # the Editor assembly references the RUNTIME assembly (Cs16.dll) and the engine's editor
    # assembly, so unlike the default mode Cs16.dll must NOT be excluded.  Assembly-CSharp-Editor
    # IS excluded: it is Unity's own build of exactly these sources, so referencing it only
    # produces duplicate-type warnings (CS0436) -- and a warning is not what this check judges.
    $refs += @(Get-ChildItem (Join-Path $client 'Library\ScriptAssemblies') -Filter *.dll |
        Where-Object { $_.Name -ne 'Assembly-CSharp-Editor.dll' } |
        ForEach-Object { $_.FullName })
    $refs = @($refs | Sort-Object -Unique)
    $defs = ($defs + ';UNITY_EDITOR').Trim(';')
    $rsp = Join-Path $tmp 'compile-editor.rsp'
    $out = Join-Path $tmp 'CheckEditor.dll'
} else {
    $sources = @(Get-ChildItem (Join-Path $client 'Assets\Scripts') -Recurse -Filter *.cs |
        ForEach-Object { $_.FullName })
}
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('-target:library')
$lines.Add('-langversion:9.0')
$lines.Add('-nullable:disable')
$lines.Add('-warn:4')
$lines.Add('-nologo')
$lines.Add("-out:`"$out`"")
$lines.Add("-define:$defs")
foreach ($r in $refs) { $lines.Add("-r:`"$r`"") }
foreach ($s in $sources) { $lines.Add("`"$s`"") }
Set-Content -Path $rsp -Value $lines -Encoding UTF8

if ($Quiet -eq '') { Write-Host ("refs=" + $refs.Count + " sources=" + $sources.Count + " rsp=" + $rsp) }

& dotnet exec $csc "@$rsp"
$code = $LASTEXITCODE
if ($Quiet -eq '') {
    Write-Host ("csc exit=" + $code)
    if (Test-Path $out) {
        Write-Host ("out=" + (Get-Item $out).Length + " bytes " + $out)
    }
}
exit $code
