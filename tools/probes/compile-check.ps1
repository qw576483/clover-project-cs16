# 一次性自检脚本：用 Unity 自带的 Roslyn 把 Assets/Scripts 编译成临时程序集，
# 只做"能不能编译过"的校验（不跑 unity run / test / batchmode，不碰 Library/ScriptAssemblies）。
# 复用 Unity 生成的 Cs16.csproj 里的引用清单与宏定义，保证与编辑器编译条件一致。
$ErrorActionPreference = 'Stop'

$projRoot = 'clover-project-cs16'
$client   = Join-Path $projRoot 'client'
$tmp      = Join-Path $projRoot '.ai-tmp\test'
$csc      = 'C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Data\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll'

if (-not (Test-Path $csc)) { throw "找不到 Unity 自带 csc.dll：$csc" }

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
