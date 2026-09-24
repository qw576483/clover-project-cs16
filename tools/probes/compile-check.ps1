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
# -Engine       compile the ENGINE FROM ITS CURRENT SOURCE -- **one assembly per asmdef** -- and drop
#               the prebuilt `CloverEngine.*.dll` (plus, with -Editor, `Cs16.dll`) under
#               Library/ScriptAssemblies from the reference list.  Both halves are needed: those DLLs
#               are machine-local build outputs that can lag the source they are meant to provide
#               (measured 2026-09-22: `Separation2D` / `CloverFirstPersonCamera` / `ILanResponder` are
#               ABSENT from them and `LanProtocol` is internal-only there, while all four are public
#               in Runtime/ source) -- a gate referencing them is permanently red on
#               Module/CameraRig/FirstPersonCamera.cs and Module/Net/CsLanHost.cs; and referencing a
#               stale DLL *while* compiling the same types from source would raise CS0433 instead.
#
#               WHY ONE ASSEMBLY PER ASMDEF (rather than one big compile unit): `internal` visibility
#               follows the assembly boundary, and BOTH directions of error are real:
#                 * FALSE RED -- `Runtime/Core/Timer.cs` declares `internal class CloverEngine.Timer`,
#                   while NetworkManager.cs / Quic/QuicConnection.cs write `Timer` meaning
#                   `System.Threading.Timer`.  In separate assemblies Core's type is invisible
#                   (correct); merged into ONE unit the enclosing-namespace type wins and 9 bogus
#                   CS1061/CS1729 appear.
#                 * FALSE GREEN (the worse one) -- a cross-assembly use of an `internal` type/member is
#                   REJECTED by Unity but ACCEPTED by a merged unit, so the gate would stay green
#                   while the real compile is red.
#               Assembly order follows each asmdef's `references`: Core first, then the four domains
#               that reference it, then (with -Editor) `CloverEngine.Editor`.  That editor assembly's
#               name must be exactly `CloverEngine.Editor` because Runtime/*/AssemblyInfo.cs declares
#               InternalsVisibleTo("CloverEngine.Editor") -- e.g. Editor/MapBake/MapBaker.cs uses the
#               runtime-internal `CloverMapMarker`.
#
#               ⚠️ ROLE: this entry point is the one `tools/verify.ps1` calls -- a lightweight run
#               (one process, several csc passes, products left in %TEMP% and not reusable).  It does
#               compile per asmdef, so its `internal` visibility matches Unity's; the full
#               re-runnable multi-project form (8 assemblies on disk, `dotnet build`) is
#               `tools/probes/hosts/compilecheck/**`, whose entry is
#               `tools/probes/hosts/compilecheck/editor/Cs16EditorCheck.csproj`.
# -Quiet        print nothing except the one-line `errors=<n> exit=<n>` summary (verify.ps1 calls
#               it this way).
# NOTE: these are STRINGS, not [switch], on purpose -- the project calls its gate scripts through
# `powershell -File <script> -Arg val`, and a [switch] parameter is bound from a STRING there
# (measured 2026-09-23: "Cannot convert value "System.String" to type SwitchParameter"). Pass
# `-Editor 1 -Quiet 1`.
param([string]$Editor = '', [string]$EditorDir = '', [string]$Engine = '', [string]$Quiet = '')
$ErrorActionPreference = 'Stop'

# 项目根**从脚本自身位置推导**（本脚本在 <项目根>/tools/probes/ 下）：原来的相对写法
# `$projRoot = 'clover-project-cs16'` 要求调用者的 CWD == 工作区根，而 verify.ps1 / 本脚本
# 现在都由闸门自检从任意 CWD 调起 ⇒ 相对路径会把 csproj 找成
# `<项目根>\clover-project-cs16\client\Cs16.csproj`（2026-09-23 实测）。
$projRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$client   = Join-Path $projRoot 'client'
# 引擎源码根：与 cs16 工程同级的工作区目录 <工作区根>/clover-client-unity-engine。
$engineMode = ($Engine -ne '')
$engineRoot = Join-Path (Split-Path $projRoot -Parent) 'clover-client-unity-engine'
# 编译中间产物（*.rsp / Check*.dll / 现场编出来的引擎程序集）⛔ 不写进 .ai-tmp：verify.ps1 第 43 项
# （tmp-budget）把 <项目根>/.ai-tmp 下的**每个文件**都算进预算、且看到 bin/obj/*-bak-* 目录直接 FAIL，
# 而本脚本每运行一次就重建一批产物 ⇒ 一律写系统临时目录（与 GOCACHE/GOPATH/node_modules 归位系统
# 默认是同一条规则，skill §3.5）。
$cacheDir = Join-Path $env:TEMP 'cs16-ccheck'
if (-not (Test-Path -LiteralPath $cacheDir)) { New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null }

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
# `CloverEngine.*.dll` 在 -Engine 模式下**必须**从引用里去掉：那几份是 2026-09-22 的旧构建产物
# （Separation2D / CloverFirstPersonCamera / ILanResponder / LanProtocol 都不在里面），
# 既参考旧 DLL 又编同一批类型的源码 ⇒ CS0433 重复定义；改用下面现场编出来的程序集。
$refs += Get-ChildItem (Join-Path $client 'Library\ScriptAssemblies') -Filter *.dll |
    Where-Object { ($_.Name -ne 'Cs16.dll') -and ((-not $engineMode) -or ($_.Name -notlike 'CloverEngine.*')) } |
    ForEach-Object { $_.FullName }
$refs = $refs | Sort-Object -Unique

# ---- 宏 ----
$defs = [regex]::Match($csproj, '<DefineConstants>([^<]+)</DefineConstants>').Groups[1].Value.Trim()

# ---- 源文件 ----
# DEFAULT mode = the Runtime assembly (Assets/Scripts).  It does NOT cover Assets/Editor/** as
# Unity's Editor assembly (that is a separate assembly compiled with UNITY_EDITOR): the previous
# version appended the Editor sources to this runtime compile, which is neither contract and is
# why a broken Editor script could pass.  Use -Editor for the Editor assembly.
$rsp = Join-Path $cacheDir 'compile.rsp'
$out = Join-Path $cacheDir 'Check.dll'
$editorSrcDir = $null
if ($Editor -ne '') {
    $srcRoot = $EditorDir
    if (($srcRoot -eq '') -or (-not (Test-Path -LiteralPath $srcRoot))) { $srcRoot = Join-Path $client 'Assets\Editor' }
    $editorSrcDir = $srcRoot
    $sources = @(Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
    # the Editor assembly references the RUNTIME assembly (Cs16.dll) and the engine's editor
    # assembly, so unlike the default mode Cs16.dll must NOT be excluded.  Assembly-CSharp-Editor
    # IS excluded: it is Unity's own build of exactly these sources, so referencing it only
    # produces duplicate-type warnings (CS0436) -- and a warning is not what this check judges.
    # In -Engine mode the stale `Cs16.dll` goes too: the editor assembly then references the runtime
    # assembly compiled a few lines below from the CURRENT Assets/Scripts sources, so a stale
    # runtime build cannot mask (or invent) a mismatch.
    $refs += @(Get-ChildItem (Join-Path $client 'Library\ScriptAssemblies') -Filter *.dll |
        Where-Object { ($_.Name -ne 'Assembly-CSharp-Editor.dll') -and ((-not $engineMode) -or (($_.Name -notlike 'CloverEngine.*') -and ($_.Name -ne 'Cs16.dll'))) } |
        ForEach-Object { $_.FullName })
    $refs = @($refs | Sort-Object -Unique)
    $defs = ($defs + ';UNITY_EDITOR').Trim(';')
    $rsp = Join-Path $cacheDir 'compile-editor.rsp'
    $out = Join-Path $cacheDir 'CheckEditor.dll'
} else {
    $sources = @(Get-ChildItem (Join-Path $client 'Assets\Scripts') -Recurse -Filter *.cs |
        ForEach-Object { $_.FullName })
}

# ---- 写 csc 参数文件 ----
function Write-CscArgs([string]$rspPath, [string[]]$srcList, [string[]]$refList, [string]$outFile, [string]$defines) {
    $l = New-Object System.Collections.Generic.List[string]
    $l.Add('-target:library')
    $l.Add('-langversion:9.0')
    $l.Add('-nullable:disable')
    $l.Add('-warn:4')
    $l.Add('-nologo')
    $l.Add("-out:`"$outFile`"")
    $l.Add("-define:$defines")
    foreach ($r in $refList) { $l.Add("-r:`"$r`"") }
    foreach ($s in $srcList) { $l.Add("`"$s`"") }
    Set-Content -Path $rspPath -Value $l -Encoding UTF8
}

# -Engine：引擎当前源码**按 asmdef 逐程序集**编（见文件头「WHY ONE ASSEMBLY PER ASMDEF」）。
# 依赖顺序照各 asmdef 的 references：Core → {Data,Network,Resource,Presentation} →（-Editor 时）Editor。
$engineCount = 0
$cscOut = @()
$code = 0
$ranFinalPass = $false
$engineDlls = @()
if ($engineMode) {
    $engineRuntime = Join-Path $engineRoot 'Runtime'
    $engineOutDir = Join-Path $cacheDir 'engine'
    if (-not (Test-Path -LiteralPath $engineOutDir)) { New-Item -ItemType Directory -Path $engineOutDir -Force | Out-Null }
    $engineAsm = @(
        @{ Name = 'CloverEngine.Core';         Dir = (Join-Path $engineRuntime 'Core');         Refs = @() },
        @{ Name = 'CloverEngine.Data';         Dir = (Join-Path $engineRuntime 'Data');         Refs = @('CloverEngine.Core') },
        @{ Name = 'CloverEngine.Network';      Dir = (Join-Path $engineRuntime 'Network');      Refs = @('CloverEngine.Core') },
        @{ Name = 'CloverEngine.Resource';     Dir = (Join-Path $engineRuntime 'Resource');     Refs = @('CloverEngine.Core') },
        @{ Name = 'CloverEngine.Presentation'; Dir = (Join-Path $engineRuntime 'Presentation'); Refs = @('CloverEngine.Core') }
    )
    if ($Editor -ne '') {
        $engineAsm += @{ Name = 'CloverEngine.Editor'; Dir = (Join-Path $engineRoot 'Editor');
            Refs = @('CloverEngine.Core', 'CloverEngine.Data', 'CloverEngine.Network', 'CloverEngine.Resource', 'CloverEngine.Presentation') }
    }
    foreach ($a in $engineAsm) {
        $src = @(Get-ChildItem -LiteralPath $a.Dir -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName })
        if ($src.Count -eq 0) {
            throw ("-Engine requested but engine source is missing under '" + $a.Dir +
                   "'. Expected clover-client-unity-engine/**/*.cs next to the project root.")
        }
        $asmRefs = @($refs)
        foreach ($r in $a.Refs) { $asmRefs += (Join-Path $engineOutDir ($r + '.dll')) }
        $asmOut = Join-Path $engineOutDir ($a.Name + '.dll')
        $asmRsp = Join-Path $cacheDir ('compile-' + $a.Name + '.rsp')
        Write-CscArgs $asmRsp $src $asmRefs $asmOut $defs
        if ($Quiet -eq '') { Write-Host ("engine pass: " + $a.Name + " sources=" + $src.Count + " refs=" + $asmRefs.Count) }
        $cscOut += @(& dotnet exec $csc "@$asmRsp" 2>&1)
        $code = $LASTEXITCODE
        $engineCount += $src.Count
        if ($code -ne 0) { break }
        $engineDlls += $asmOut
    }
    # -Editor：业务运行时程序集（Assets/Scripts）先单独编一份，供编辑器程序集引用
    # （Unity 里就是这个方向：Assembly-CSharp-Editor → Cs16）。
    if (($code -eq 0) -and ($Editor -ne '')) {
        $rtSrc = @(Get-ChildItem (Join-Path $client 'Assets\Scripts') -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
        $rtOut = Join-Path $engineOutDir 'Cs16.dll'
        $rtRsp = Join-Path $cacheDir 'compile-runtime.rsp'
        Write-CscArgs $rtRsp $rtSrc (@($refs) + $engineDlls) $rtOut $defs
        if ($Quiet -eq '') { Write-Host ("runtime pass: sources=" + $rtSrc.Count + " out=" + $rtOut) }
        $cscOut += @(& dotnet exec $csc "@$rtRsp" 2>&1)
        $code = $LASTEXITCODE
        if ($code -eq 0) { $engineDlls += $rtOut }
    }
    if ($code -eq 0) { $refs = @($refs) + $engineDlls }
}

if ($code -eq 0) {
    $ranFinalPass = $true
    Write-CscArgs $rsp $sources $refs $out $defs
    if ($Quiet -eq '') {
        Write-Host ("final pass: refs=" + $refs.Count + " sources=" + $sources.Count + " engineSources=" + $engineCount + " rsp=" + $rsp)
    }
    $cscOut += @(& dotnet exec $csc "@$rsp" 2>&1)
    $code = $LASTEXITCODE
}

# csc 的诊断**始终回放**（成功时它本来就不输出任何行）：-Quiet 只压掉本脚本自己的那几行说明，
# 不隐藏编译错误。
$cscOut | ForEach-Object { Write-Host $_ }
$errCount = @($cscOut | Where-Object { $_ -match 'error CS\d+' -or $_ -match '^error ' }).Count

if (($Quiet -eq '') -and $ranFinalPass -and (Test-Path $out)) {
    Write-Host ("out=" + (Get-Item $out).Length + " bytes " + $out)
}
# 闸门判据行（机器可读，任何模式下都打印）：errors=<编译错误数> exit=<csc 退出码>
Write-Host ("errors=" + $errCount + " exit=" + $code)
exit $code
