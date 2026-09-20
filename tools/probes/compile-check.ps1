# 一次性自检脚本：用 Unity 自带的 Roslyn 把 Assets/Scripts 编译成临时程序集，
# 只做"能不能编译过"的校验（不跑 unity run / test / batchmode，不碰 Library/ScriptAssemblies）。
# 复用 Unity 生成的 Cs16.csproj 里的引用清单与宏定义，保证与编辑器编译条件一致。
$ErrorActionPreference = 'Stop'

$projRoot = 'clover-project-cs16'
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
    throw ("找不到 Unity 自带 csc.dll。已在 '" + (Join-Path $env:ProgramFiles 'Unity\Hub\Editor') +
           "\<编辑器版本>\Editor\Data\DotNetSdk\sdk\<sdk 版本>\Roslyn\bincore\csc.dll' 下逐个版本搜索，一个都没命中。 " +
           "请先确认 Unity 是通过 Unity Hub 装在本机（含内置 DotNetSdk），再重跑本脚本。")
}
Write-Host ("csc = " + $csc)

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

# ---- 源文件（运行时脚本；Editor 脚本是另一个程序集，不在这里编）----
$sources = Get-ChildItem (Join-Path $client 'Assets\Scripts') -Recurse -Filter *.cs |
    ForEach-Object { $_.FullName }
$sources += Get-ChildItem (Join-Path $client 'Assets\Editor') -Recurse -Filter *.cs |
    ForEach-Object { $_.FullName }

$rsp = Join-Path $tmp 'compile.rsp'
$out = Join-Path $tmp 'Check.dll'
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

Write-Host ("refs=" + $refs.Count + " sources=" + $sources.Count + " rsp=" + $rsp)

& dotnet exec $csc "@$rsp"
$code = $LASTEXITCODE
Write-Host ("csc exit=" + $code)
if (Test-Path $out) {
    Write-Host ("out=" + (Get-Item $out).Length + " bytes " + $out)
}
exit $code
