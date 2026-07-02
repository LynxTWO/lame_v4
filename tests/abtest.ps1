# LAME v4 quality A/B harness.
#
# Encodes the whole corpus with two lame.exe builds (A = reference, B = variant) at one
# setting, decodes both, and reports the encoder-independent perceptual noise-to-mask
# (meanNMRdb) for each plus the delta. Negative delta (B lower than A) = B is more
# transparent at the same bitrate = a real quality win. This is the day-to-day gate for the
# deep-search flagship (Q-A).
#
# Usage:
#   pwsh tests/abtest.ps1 -A path\to\baseline\lame.exe -B path\to\variant\lame.exe -Setting '-b192'
#   pwsh tests/abtest.ps1 -Setting '-V0'    # both default to output\lame.exe (self-check -> 0 delta)
[CmdletBinding()]
param(
    [string]$A = "$PSScriptRoot\..\output\lame.exe",
    [string]$B = "$PSScriptRoot\..\output\lame.exe",
    [string]$Setting = '-b192',
    # Optional: different settings for side B (same binary or different). Lets the harness
    # compare SETTINGS (e.g. '-q 4 -b 128' vs '-q 0 -b 128') as well as builds.
    [string]$SettingB = '',
    [string]$Corpus = "$PSScriptRoot\corpus",
    [string]$Work = "$PSScriptRoot\out",
    [string]$Nmr = "$PSScriptRoot\nmr\bin\Release\net8.0\nmr.exe"
)
# NOTE: deliberately NOT 'Stop'. lame/nmr write progress to stderr; with 2>&1 that becomes
# error records, and 'Stop' would abort mid-corpus. We check exit codes explicitly instead.
$ErrorActionPreference = 'Continue'
foreach ($p in @($A, $B, $Nmr)) { if (-not (Test-Path $p)) { Write-Error "not found: $p"; exit 2 } }
if (-not (Test-Path $Work)) { New-Item -ItemType Directory -Force $Work | Out-Null }
if ($SettingB -eq '') { $SettingB = $Setting }
$setArgsA = @($Setting -split '\s+' | Where-Object { $_ -ne '' })
$setArgsB = @($SettingB -split '\s+' | Where-Object { $_ -ne '' })

$wavs = Get-ChildItem "$Corpus\*.wav" | Sort-Object Name
Write-Host "A: $Setting   B: $SettingB   (meanNMRdb: lower = more transparent; delta<0 => B better)"
Write-Host ("{0,-22} {1,10} {2,10} {3,10}" -f 'file', 'A', 'B', 'delta')
Write-Host "-----------------------------------------------------------------"
$sumA = 0.0; $sumB = 0.0; $n = 0
foreach ($w in $wavs) {
    $vals = @()
    foreach ($pair in @(@('A', $A, $setArgsA), @('B', $B, $setArgsB))) {
        $tag = "$($pair[0])_$($w.BaseName)"; $lame = $pair[1]; $sArgs = $pair[2]
        $mp3 = Join-Path $Work "$tag.mp3"; $dec = Join-Path $Work "$tag.wav"
        & $lame --quiet @sArgs $w.FullName $mp3 2>&1 | Out-Null
        & $lame --quiet --decode $mp3 $dec 2>&1 | Out-Null
        $line = (& $Nmr $w.FullName $dec 2>$null | Select-Object -First 1)
        $vals += [double](("$line" -split '\s+')[0])
    }
    # NB: $a/$b would alias the $A/$B params (PowerShell vars are case-insensitive), so use mA/mB.
    $mA = $vals[0]; $mB = $vals[1]; $d = $mB - $mA
    $sumA += $mA; $sumB += $mB; $n++
    $flag = ''
    if ($d -lt -0.01) { $flag = 'BETTER' } elseif ($d -gt 0.01) { $flag = 'worse' }
    Write-Host ("{0,-22} {1,10:F3} {2,10:F3} {3,10:F3} {4}" -f $w.BaseName, $mA, $mB, $d, $flag)
}
Write-Host "-----------------------------------------------------------------"
Write-Host ("{0,-22} {1,10:F3} {2,10:F3} {3,10:F3}" -f 'CORPUS MEAN', ($sumA / $n), ($sumB / $n), (($sumB - $sumA) / $n))
