# Equal-measured-size A/B harness.
#
# Equal-setting NMR cannot judge VBR fairly: two encoders given '-V 2' can land at different
# average bitrates, so the cleaner one may simply be spending more bits. This harness makes
# the comparison honest: for each corpus file it BISECTS each side's rate control until the
# measured average bitrate (tools/podcast --measure, audio-frame walk) lands inside
# [Target - Tol, Target], then scores both sides with the independent meter at (nearly)
# equal measured size. Negative delta means B is more transparent at equal bitrate.
#
# Rate controls: -Mode vbr bisects fractional -V (0..9.99, measured kbps falls as V rises);
# -Mode abr bisects fractional --abr (the v4 float API; measured kbps rises with the
# request). Sides may use different extra args (effort flags) via -ExtraA / -ExtraB.
#
# Usage:
#   pwsh tests/eqsize-abtest.ps1 -Target 128 -Mode vbr -ExtraB '--quality-max' -Corpus tests\corpus_train
[CmdletBinding()]
param(
    [double]$Target = 128,
    [double]$Tol = 0.5,
    [ValidateSet('vbr','abr')] [string]$Mode = 'vbr',
    [string]$A = "$PSScriptRoot\..\output\lame.exe",
    [string]$B = "$PSScriptRoot\..\output\lame.exe",
    [string]$ExtraA = '',
    [string]$ExtraB = '',
    [string]$Corpus = "$PSScriptRoot\corpus_train",
    [string]$Nmr = "$PSScriptRoot\nmr\bin\Release\net8.0\nmr.exe",
    [string]$Measure = "$PSScriptRoot\..\tools\podcast\bin\Release\net8.0\podcast.exe"
)
$ErrorActionPreference = 'Continue'
foreach ($p in @($A, $B, $Nmr, $Measure)) { if (-not (Test-Path $p)) { Write-Error "not found: $p"; exit 2 } }

# Bisect one side's rate control on one file until measured kbps lands in the window.
# Returns @{kbps=..; score=..; ctl=..} or $null when the window is unreachable.
function Land([string]$exe, [string]$extra, [string]$wav) {
    $mp3 = "$env:TEMP\eqs.mp3"; $dec = "$env:TEMP\eqs.wav"
    $extraArgs = @($extra -split '\s+' | Where-Object { $_ -ne '' })
    if ($Mode -eq 'vbr') { $lo = 0.0; $hi = 9.99; $ctl = 4.0 } else { $lo = 8.0; $hi = 320.0; $ctl = $Target }
    for ($i = 0; $i -lt 14; $i++) {
        $ctlS = $ctl.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)
        if ($Mode -eq 'vbr') { $rate = @('-V', $ctlS) } else { $rate = @('--abr', $ctlS) }
        & $exe --quiet --nohist @rate @extraArgs $wav $mp3 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { return $null }
        $kbps = [double](& $Measure --measure $mp3 2>$null)
        if ($kbps -ge ($Target - $Tol) -and $kbps -le $Target) {
            & $exe --quiet --decode $mp3 $dec 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { return $null }
            $f = ((& $Nmr $wav $dec 2>$null | Select-Object -First 1) -split '\s+')
            return @{ kbps = $kbps; score = [double]$f[0]; aud = [double]$f[2]; ctl = $ctlS }
        }
        # vbr: kbps falls as V rises; abr: kbps rises with the request
        if ($Mode -eq 'vbr') { if ($kbps -gt $Target) { $lo = $ctl } else { $hi = $ctl } }
        else                 { if ($kbps -gt $Target) { $hi = $ctl } else { $lo = $ctl } }
        if (($hi - $lo) -lt 0.005) { return $null }
        $ctl = ($lo + $hi) / 2
    }
    return $null
}

$wavs = Get-ChildItem "$Corpus\*.wav" | Sort-Object Name
Write-Host "equal-size $Mode @ $Target kbps   A: '$ExtraA'   B: '$ExtraB'   (delta<0 => B better at equal measured size)"
Write-Host ("{0,-14} {1,8} {2,9} {3,8} {4,9} {5,9}" -f 'file','kbpsA','meanA','kbpsB','meanB','delta')
$sumA=0.0; $sumB=0.0; $n=0; $skipped=0
foreach ($w in $wavs) {
    $ra = Land $A $ExtraA $w.FullName
    $rb = Land $B $ExtraB $w.FullName
    if ($null -eq $ra -or $null -eq $rb) { $skipped++; Write-Host ("{0,-14} window unreachable, skipped" -f $w.BaseName); continue }
    $d = $rb.score - $ra.score
    $sumA += $ra.score; $sumB += $rb.score; $n++
    Write-Host ("{0,-14} {1,8:F2} {2,9:F3} {3,8:F2} {4,9:F3} {5,9:F3}" -f $w.BaseName,$ra.kbps,$ra.score,$rb.kbps,$rb.score,$d)
}
if ($n -gt 0) {
    Write-Host ("{0,-14} {1,8} {2,9:F3} {3,8} {4,9:F3} {5,9:F3}  ({6} files, {7} skipped)" -f 'MEAN','',($sumA/$n),'',($sumB/$n),(($sumB-$sumA)/$n),$n,$skipped)
} else { Write-Host "no files landed"; exit 1 }
