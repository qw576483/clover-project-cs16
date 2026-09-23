# 判据资产（tools/probes/）：**取证截图引用审计** —— `.ai-tmp/screenshots/` 里的每个 png
# 必须被某个表 / 清单 / manifest 引用（引用计数 >= 1），否则它就是"画完没人看"的中间产物
# （clover-engine SKILL 1.8 第 6 条：证据图是**一次性**产物，只留被判据引用到的那些）。
# 为什么它要是一支脚本：SKILL 0.6「必然性规则必须做成闸门」—— "每个图都有引用"是一个**可计算**的
# 断言，靠人记必然漏。切片 AF 用它查出 176 张里 78 张零引用 + 12 张 `b41_*` 连拍残留（只被
# 运行日志提到，无任何表引用），删掉 90 张后重跑 = 零引用 0 / 86 张全部有引用。
# 【切片AG（2026-09-21）已裁决并接线】本脚本现由 tools\verify.ps1 第 26 条
# `shot-refs-audited` 调用（.ai-tmp/screenshots 下零引用图 > 0 即 FAIL）——
# "图不被引用"从此自己变红；上一版（切片AF）按当时任务书"只许新增 home-credit-rendered 一条"未接。
# ASCII-only on purpose (PS 5.1 reads a BOM-less .ps1 as ANSI).
# Usage (also invoked by tools\verify.ps1 item 26, which parses the summary line):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\audit-shot-refs.ps1   # zero-ref list
#   powershell ... audit-shot-refs.ps1 -Png <name>.png                                      # per-file hits
#   powershell ... audit-shot-refs.ps1 -Matrix                                              # per-png referencing files
param([string]$Png = '', [switch]$Matrix, [switch]$Full, [string]$ShotDir = '')
$ErrorActionPreference = 'Stop'
# Root is derived from this script's own location (tools\probes\ -> tools\ -> project
# root) so the gate stays portable; a hard-coded absolute path would break on move
# (SKILL 8: no machine-local paths in shipped tools).
$root    = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
# -ShotDir is the seam that makes this asset SAMPLABLE (added 2026-09-23).  WHY IT EXISTS: verify.ps1
# invoked this script with no arguments while `screenshot-refs` samples run against a -ShotDir
# sandbox; measured: with a sandbox holding ONE loose png the audit still reported "186
# screenshot(s), every one cited" -- i.e. it read the REAL dir, so the sample proved nothing about
# the sandbox (a FALSE TEST PASS, the mirror of a false red).
# NB the internal variable is $ShotsRoot, NOT $shotDir: PowerShell variable names are
# CASE-INSENSITIVE, so `$shotDir` and the parameter `$ShotDir` are the SAME variable -- measured
# 2026-09-23: with `$shotDir = ...` on the next line the parameter was silently overwritten by its
# own default before the override could read it, so `-ShotDir <sandbox>` did NOTHING and the audit
# kept reporting the real dir (a seam that silently self-destructs is worse than no seam: it looks
# wired).
$ShotsRoot = Join-Path $root '.ai-tmp\screenshots'
if ($ShotDir -ne '') { $ShotsRoot = (Get-Item -LiteralPath $ShotDir).FullName }
$exts    = @('.md', '.tsv', '.txt', '.ps1', '.py', '.cs', '.json')

# --- CARRIER SCOPE (slice BW-G, 2026-09-22): who counts as a DECLARATION ------------
# This used to scan EVERY text file in the project. That made the audit SELF-HEALING,
# and it was measured live: `bv-r` left its Play output on disk
# (`.ai-tmp/test/bvr4-play-out.txt`, a raw console dump) and three captured frames
# suddenly had "1 reference" each (`bvr-home-mainmenu-gameview.png`,
# `bvr-after-hide-audiolisten.png`, `bvr-screen-fire-gizmosON.png`) => the gate healed
# itself with an operator output file. Worse: this audit's OWN zero-ref list, saved to
# disk by any operator, is by construction a list of png names and would count as their
# references on the next run (SKILL 0.6 / anti-gaming.md: a gate that can be satisfied by
# its own output is not a gate).
# The item's own precedent (slice AF) already says what a reference is: 12 `b41_*` frames
# were deleted as zero-ref because they were "only mentioned in a runtime log, cited by no
# table". So only DECLARATIVE carriers are scanned:
#   * plan/**               -- the acceptance / coverage / diff tables (the judgement side)
#   * tools/probes/**       -- judgement assets: .ps1/.py drivers, *.manifest.tsv, *.txt/o.tsv
#                              produced by a judgement probe
#   * .ai-tmp/drivers/**    -- the drivers that NAME the frames they capture
#   * *.index.tsv / *.manifest.tsv anywhere -- contact-sheet indexes are declarations
# Everything else (runtime logs, saved `verify.ps1` outputs, operator notes under
# .ai-tmp/test/**, scratch copies) is NOT a citation carrier. The exclusion is reported in
# the header line so it can never be silent, and tools/probes/gate-selftest.ps1 section 3
# proves both directions (a saved output must NOT heal a zero-ref frame; a manifest entry
# must).
$carrierRoots = @((Join-Path $root ([char[]]@(0x7B56, 0x5212) -join '')),          # plan/
                  (Join-Path $root 'tools\probes'),
                  (Join-Path $root '.ai-tmp\drivers'))
$all = @()
foreach ($cr in $carrierRoots) {
  if (Test-Path $cr) {
    $all += @(Get-ChildItem $cr -Recurse -File -ErrorAction SilentlyContinue |
              Where-Object { $exts -contains $_.Extension.ToLower() } |
              Where-Object { $_.FullName -notmatch '\\Library\\|\\Temp\\|\\obj\\|\\Logs\\' } |
              Where-Object { $_.Name -notmatch '^af-' })
  }
}
foreach ($iname in @('*.index.tsv', '*.manifest.tsv')) {
  $all += @(Get-ChildItem $root -Recurse -File -Filter $iname -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\Library\\|\\Temp\\|\\obj\\|\\Logs\\' })
}
$all = @($all | Sort-Object FullName -Unique)

$texts = @()
foreach ($f in $all) {
  $t = ''
  try { $t = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8) } catch { continue }
  $texts += [pscustomobject]@{ Path = $f.FullName; Rel = $f.FullName.Substring($root.Length).TrimStart('\'); Text = $t }
}
Write-Output ("scanned files = " + $texts.Count)
Write-Output ("carrier scope = plan/** + tools/probes/** + .ai-tmp/drivers/** + *.index.tsv/*.manifest.tsv; runtime logs and saved gate/probe outputs are NOT citation carriers (otherwise this audit heals itself with its own output -- see the note above)")

function Count-Hits([string]$needle) {
  $n = 0
  foreach ($t in $texts) {
    $i = 0
    while ($true) {
      $i = $t.Text.IndexOf($needle, $i, [StringComparison]::OrdinalIgnoreCase)
      if ($i -lt 0) { break }
      $n++; $i += $needle.Length
    }
  }
  return $n
}

$pngs = @(Get-ChildItem $ShotsRoot -Filter *.png -File | Sort-Object Name)
Write-Output ("screenshots = " + $pngs.Count)

if ($Matrix) {
  foreach ($p in $pngs) {
    $hits = @($texts | Where-Object { $_.Text.IndexOf($p.Name, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { $_.Rel })
    Write-Output ($p.Name + "`t" + $hits.Count + "`t" + ($hits -join ';'))
  }
  exit 0
}

if ($Png -ne '') {
  $pngs = @($pngs | Where-Object { $_.Name -eq $Png })
}
$zero = @()
foreach ($p in $pngs) {
  $c = Count-Hits $p.Name
  if ($c -eq 0) { $zero += $p.Name; Write-Output ("ZERO   " + $p.Name) }
  elseif ($Full) { Write-Output ("REF:" + $c + "  " + $p.Name) }
}
Write-Output ""
Write-Output ("=== unreferenced (count = " + $zero.Count + "): " + $zero.Count + " of " + $pngs.Count + " ===")
$zero | ForEach-Object { Write-Output ("  " + $_) }
if ($Png -ne '') {
  Write-Output ""
  Write-Output ("=== files mentioning " + $Png + " (total occurrences " + (Count-Hits $Png) + ") ===")
  $texts | Where-Object { $_.Text.IndexOf($Png, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { Write-Output ("  " + $_.Rel) }
}
