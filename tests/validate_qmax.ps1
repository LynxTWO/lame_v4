# LAME v4 --quality-max v2 validation harness.
#
# Compares A = frozen baseline (output\lame_qmax_v1.exe, ignores env) against
# B = the env-aware build (output\lame.exe) under a chosen quant_comp objective / search depth,
# capturing ALL FOUR perceptual-meter fields, not just the mean. This is the guardrail check:
# a mean-NMR win is only real if it does NOT come from inflating the worst band (maxNMRdb) or
# the audible fraction. nmr.exe prints: meanNMRdb audibleFrac audibleNMRdb maxNMRdb.
#
# Usage: pwsh tests\validate_qmax.ps1 -QC 2 -QCS 0 -FOL 0 -Rate 128 [-Corpus ...]
[CmdletBinding()]
param(
    [int]$QC = 2, [int]$QCS = 0, [int]$FOL = 0,
    [string]$Rate = '128',
    [string]$A = "$PSScriptRoot\..\output\lame_qmax_v1.exe",
    [string]$B = "$PSScriptRoot\..\output\lame.exe",
    [string]$Corpus = "$PSScriptRoot\corpus",
    [string]$Work = "$PSScriptRoot\out",
    [string]$Nmr = "$PSScriptRoot\nmr\bin\Release\net8.0\nmr.exe"
)
$ErrorActionPreference = 'Continue'
foreach ($p in @($A, $B, $Nmr)) { if (-not (Test-Path $p)) { Write-Error "not found: $p"; exit 2 } }
if (-not (Test-Path $Work)) { New-Item -ItemType Directory -Force $Work | Out-Null }

# env only affects B (the env-aware build); A is frozen v1 = {huff2, fol0, qc1, qcs0}
$env:LAME_QMAX_QC = [string]$QC; $env:LAME_QMAX_QCS = [string]$QCS; $env:LAME_QMAX_FOL = [string]$FOL
$setA = @('--quality-max', '-b', $Rate)      # A: shipped v1 config
$setB = @('--quality-max', '-b', $Rate)      # B: same CLI; env changes the objective

function Metrics($lame, $sArgs, $wav, $tag) {
    $mp3 = Join-Path $Work "$tag.mp3"; $dec = Join-Path $Work "$tag.wav"
    & $lame --quiet @sArgs $wav $mp3 2>&1 | Out-Null
    & $lame --quiet --decode $mp3 $dec 2>&1 | Out-Null
    $line = (& $Nmr $wav $dec 2>$null | Select-Object -First 1)
    $f = "$line" -split '\s+' | Where-Object { $_ -ne '' }
    [pscustomobject]@{ mean=[double]$f[0]; aud=[double]$f[1]; audNmr=[double]$f[2]; max=[double]$f[3] }
}

$wavs = Get-ChildItem "$Corpus\*.wav" | Sort-Object Name
Write-Host "A=frozen-v1 (qc1/qcs0/fol0)   B=qc$QC/qcs$QCS/fol$FOL   @CBR$Rate   corpus=$(Split-Path $Corpus -Leaf)"
Write-Host ("{0,-22} {1,18} {2,18}  {3}" -f 'file', 'A(mean/aud/max)', 'B(mean/aud/max)', 'dMean')
Write-Host ("-" * 78)
$agg = @{ Amean=0.0; Bmean=0.0; Aaud=0.0; Baud=0.0; Amax=[double]::NegativeInfinity; Bmax=[double]::NegativeInfinity; better=0; worse=0; maxWorse=0.0; n=0 }
foreach ($w in $wavs) {
    $mA = Metrics $A $setA $w.FullName "A_$($w.BaseName)"
    $mB = Metrics $B $setB $w.FullName "B_$($w.BaseName)"
    $d = $mB.mean - $mA.mean
    $agg.Amean += $mA.mean; $agg.Bmean += $mB.mean; $agg.Aaud += $mA.aud; $agg.Baud += $mB.aud
    if ($mA.max -gt $agg.Amax) { $agg.Amax = $mA.max }
    if ($mB.max -gt $agg.Bmax) { $agg.Bmax = $mB.max }
    if ($d -lt -0.01) { $agg.better++ } elseif ($d -gt 0.01) { $agg.worse++; if ($d -gt $agg.maxWorse) { $agg.maxWorse = $d } }
    $agg.n++
    $flag = if ($d -lt -0.01) { 'BETTER' } elseif ($d -gt 0.01) { 'worse' } else { '' }
    Write-Host ("{0,-22} {1,7:F2}/{2,4:F2}/{3,5:F1} {4,7:F2}/{5,4:F2}/{6,5:F1}  {7,7:F3} {8}" -f `
        $w.BaseName, $mA.mean, $mA.aud, $mA.max, $mB.mean, $mB.aud, $mB.max, $d, $flag)
}
$n = $agg.n
Write-Host ("-" * 78)
Write-Host ("MEAN meanNMR : A={0:F3}  B={1:F3}  delta={2:F3}   (delta<0 => B more transparent overall)" -f ($agg.Amean/$n), ($agg.Bmean/$n), (($agg.Bmean-$agg.Amean)/$n))
Write-Host ("MEAN audFrac : A={0:F3}  B={1:F3}   (fraction of band-frames audible; lower better)" -f ($agg.Aaud/$n), ($agg.Baud/$n))
Write-Host ("WORST maxNMR : A={0:F1}  B={1:F1}   (single loudest band across corpus; guardrail - B must not blow up)" -f $agg.Amax, $agg.Bmax)
Write-Host ("Files: {0} better / {1} worse / {2} tie   worst single-file regression: +{3:F3} dB" -f $agg.better, $agg.worse, ($n-$agg.better-$agg.worse), $agg.maxWorse)
Remove-Item Env:LAME_QMAX_QC, Env:LAME_QMAX_QCS, Env:LAME_QMAX_FOL -ErrorAction SilentlyContinue
