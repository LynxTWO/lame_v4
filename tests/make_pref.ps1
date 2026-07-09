# Blind forced-choice preference package for the campaign-11 VBR winner.
#
# ABX (J2 vs K, 16/16) proved the tuning is audibly DIFFERENT at equal size; this package
# asks the remaining question - which one people PREFER - without revealing which file is
# which. For each material, both sides are bisected in fractional -V to equal measured
# size (tuned lands first at the target's below-endpoint, stock is then bisected to the
# tuned side's landed rate, the J2 recipe). Each pair's A/B assignment is randomized; the
# mapping is written to PREFERENCE_ANSWER_KEY_do_not_open.txt. Record a verdict per pair
# (A / B / no preference) plus notes BEFORE opening the key.
#
# Materials: the five h-set files with the largest measured equal-size improvements
# (h10 -3.52, h13 -3.42, h04 -3.27, h14 -3.25, h08 -3.17 dB mean NMR; see FINDINGS
# campaign 11). Five pairs is deliberately light on listening effort; the result is
# reported as small-N blind preference, not a significance claim.
[CmdletBinding()]
param(
    # default = the campaign-12 winner (the campaign-11 config this package was built for
    # is demoted; regenerate its pairs by passing its flags explicitly if ever needed)
    [string]$Cfg = "--ns-bass -1.50",
    [double]$Target = 128,
    [int]$Seed = 20260706,
    [string]$Lame = "$PSScriptRoot\..\output\lame.exe",
    [string]$Out = "$PSScriptRoot\abx\pref",
    [string[]]$Files = @('h10', 'h13', 'h04', 'h14', 'h08')
)
$ErrorActionPreference = 'Continue'
$Measure = "$PSScriptRoot\..\tools\podcast\bin\Release\net8.0\podcast.exe"
foreach ($p in @($Lame, $Measure)) { if (-not (Test-Path $p)) { Write-Error "not found: $p"; exit 2 } }
New-Item -ItemType Directory -Force $Out | Out-Null

function VbrBelowTarget([string]$wav, [string]$extra, [string]$outMp3, [double]$target) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("pref_" + [Guid]::NewGuid().ToString('N').Substring(0, 8) + ".mp3")
    $lo = 0.0; $hi = 9.99; $v = 4.0; $kA = [double]::NaN; $kB = [double]::NaN
    for ($it = 0; $it -lt 14; $it++) {
        $vs = "{0:0.###}" -f $v
        $p = Start-Process -FilePath $Lame -ArgumentList "--quiet --nohist -V $vs --resample 44.1 $extra `"$wav`" `"$tmp`"" -Wait -NoNewWindow -PassThru
        if ($p.ExitCode -ne 0) { return $null }
        $k = [double](& $Measure --measure $tmp)
        if ($k -le 0) { return $null }
        if ($k -gt $target) { $lo = $v; if ([double]::IsNaN($kA) -or $k -lt $kA) { $kA = $k } }
        else { if ([double]::IsNaN($kB) -or $k -gt $kB) { $kB = $k; Copy-Item $tmp $outMp3 -Force }; $hi = $v }
        if (-not [double]::IsNaN($kA) -and -not [double]::IsNaN($kB) -and ($kA - $kB) -lt 0.1) { break }
        if (($hi - $lo) -lt 0.002) { break }
        $v = ($lo + $hi) / 2
    }
    Remove-Item $tmp -ErrorAction SilentlyContinue
    if ([double]::IsNaN($kB)) { return $null }
    $kB
}

$rng = [Random]::new($Seed)
# tolerate both -Files a,b,c (array) and pwsh -File invocation (single comma-joined string)
$files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$key = @("Pair -> which of A/B is the TUNED encode (cfg: $Cfg). Landed rates included.", "")
$i = 0
foreach ($name in $files) {
    $i++
    $wav = "$PSScriptRoot\corpus_holdout\$name.wav"
    if (-not (Test-Path $wav)) { Write-Host "pair $i SKIPPED ($name.wav missing)"; continue }
    $mpT = Join-Path $Out "tmp_tuned.mp3"; $mpS = Join-Path $Out "tmp_stock.mp3"
    $kT = VbrBelowTarget $wav $Cfg $mpT $Target
    if ($null -eq $kT) { Write-Host "pair $i SKIPPED ($name tuned bisection failed)"; continue }
    $kS = VbrBelowTarget $wav '' $mpS $kT
    if ($null -eq $kS) { Write-Host "pair $i SKIPPED ($name stock bisection failed)"; Remove-Item $mpT; continue }
    $tunedIsA = $rng.Next(2) -eq 0
    $aSrc = if ($tunedIsA) { $mpT } else { $mpS }
    $bSrc = if ($tunedIsA) { $mpS } else { $mpT }
    Move-Item $aSrc (Join-Path $Out "P$($i)_A.mp3") -Force
    Move-Item $bSrc (Join-Path $Out "P$($i)_B.mp3") -Force
    foreach ($s in 'A', 'B') {
        & $Lame --quiet --decode (Join-Path $Out "P$($i)_$s.mp3") (Join-Path $Out "P$($i)_$($s)_decoded.wav") 2>&1 | Out-Null
    }
    Copy-Item $wav (Join-Path $Out "P$($i)_original.wav") -Force
    $key += ("P{0} ({1}): TUNED = {2}   (tuned {3:F2} kbps, stock {4:F2} kbps)" -f $i, $name, $(if ($tunedIsA) { 'A' } else { 'B' }), $kT, $kS)
    Write-Host ("pair P{0} written ({1}: tuned {2:F2} kbps, stock {3:F2} kbps)" -f $i, $name, $kT, $kS)
}
$key | Set-Content (Join-Path $Out 'PREFERENCE_ANSWER_KEY_do_not_open.txt')
Write-Host "Answer key sealed in PREFERENCE_ANSWER_KEY_do_not_open.txt - record all verdicts first."
