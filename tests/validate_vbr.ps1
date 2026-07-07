# LAME v4 VBR-campaign validation harness (campaign 11+).
#
# Compares A = stock lame -V against B = stock lame -V plus a candidate tuning config, at
# EQUAL MEASURED BITRATE on a holdout corpus the campaign never saw. Fractional -V is only
# piecewise continuous in measured kbps (cliffs at integer V and psymodel switch points),
# so neither side "lands" a window; instead each side bisects -V until the target is
# bracketed by the nearest encode above and below, both endpoints are decoded and metered,
# and all four nmr.exe fields (meanNMRdb audibleFrac audibleNMRdb maxNMRdb) are
# interpolated linearly to exactly the target kbps -- the same methodology the campaign
# fitness used, applied to fresh files. --resample 44.1 pins MPEG1 output so the meter
# never compares across sample rates (LAME auto-resamples high -V probes to 22/24 kHz).
#
# Files whose stock ceiling sits below the target under either side are scored at that
# side's ceiling with a printed note (best-within-budget); files that cannot bracket at
# all are dropped with a receipt.
#
# Usage: pwsh tests\validate_vbr.ps1 -Cfg "--ns-bass -4.68 ..." [-Target 128] [-Corpus dir]
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Cfg,
    [double]$Target = 128,
    [string]$Corpus = "$PSScriptRoot\corpus_holdout",
    [string]$Lame = "$PSScriptRoot\..\output\lame.exe",
    [string]$Nmr = "$PSScriptRoot\nmr\bin\Release\net8.0\nmr.exe",
    [int]$Throttle = 12
)
$ErrorActionPreference = 'Continue'
$Measure = "$PSScriptRoot\..\tools\podcast\bin\Release\net8.0\podcast.exe"
foreach ($p in @($Lame, $Nmr, $Measure)) { if (-not (Test-Path $p)) { Write-Error "not found: $p"; exit 2 } }
$wavs = Get-ChildItem "$Corpus\*.wav" | Sort-Object Name
Write-Host "A=stock -V   B=stock -V + cfg   interpolated @ $Target kbps measured   corpus=$(Split-Path $Corpus -Leaf) ($($wavs.Count) files)"
Write-Host "cfg: $Cfg"

$rows = $wavs | ForEach-Object -ThrottleLimit $Throttle -Parallel {
    $lame = $using:Lame; $nmr = $using:Nmr; $target = $using:Target; $cfg = $using:Cfg
    $measure = $using:Measure
    $wav = $_.FullName
    $work = Join-Path ([IO.Path]::GetTempPath()) ("vvbr_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $work | Out-Null
    try {
        # returns 4 interpolated meter fields + landing info, or $null (unbracketable)
        function SideMetrics([string]$extra, [string]$tag) {
            $mp3 = Join-Path $work "$tag.mp3"
            $lo = 0.0; $hi = 9.99; $v = 4.0
            $kA = [double]::NaN; $kB = [double]::NaN
            for ($it = 0; $it -lt 14; $it++) {
                $vs = "{0:0.###}" -f $v
                $enc = "--quiet --nohist -V $vs --resample 44.1 $extra `"$wav`" `"$mp3`""
                $p = Start-Process -FilePath $lame -ArgumentList $enc -Wait -NoNewWindow -PassThru
                if ($p.ExitCode -ne 0) { return $null }
                # measured audio-frame kbps via the podcast tool's (fixed, all-MPEG) walker
                $k = [double](& $measure --measure $mp3)
                if ($k -le 0) { return $null }
                if ($k -gt $target) { if ([double]::IsNaN($kA) -or $k -lt $kA) { $kA = $k; Copy-Item $mp3 (Join-Path $work "$tag.a.mp3") -Force }; $lo = $v }
                else                { if ([double]::IsNaN($kB) -or $k -gt $kB) { $kB = $k; Copy-Item $mp3 (Join-Path $work "$tag.b.mp3") -Force }; $hi = $v }
                if (-not [double]::IsNaN($kA) -and -not [double]::IsNaN($kB) -and ($kA - $kB) -lt 0.25) { break }
                if (($hi - $lo) -lt 0.004) { break }
                $v = ($lo + $hi) / 2
            }
            if ([double]::IsNaN($kA) -and [double]::IsNaN($kB)) { return $null }
            function Meter([string]$m) {
                $dec = Join-Path $work "$tag.dec.wav"
                Start-Process -FilePath $lame -ArgumentList "--quiet --decode `"$m`" `"$dec`"" -Wait -NoNewWindow | Out-Null
                $f = ((& $nmr $wav $dec 2>$null | Select-Object -First 1) -split '\s+') | Where-Object { $_ }
                if ($f.Count -lt 5) { return $null }   # field 5 = hfStabDb (campaign 12)
                ,@([double]$f[0], [double]$f[1], [double]$f[2], [double]$f[3], [double]$f[4])
            }
            if ([double]::IsNaN($kA)) {   # ceiling below target: best-within-budget
                $r = Meter (Join-Path $work "$tag.b.mp3"); if ($null -eq $r) { return $null }
                return [pscustomobject]@{ f = $r; note = ("ceiling {0:F1}" -f $kB) }
            }
            if ([double]::IsNaN($kB)) {   # floor above target: overspends
                $r = Meter (Join-Path $work "$tag.a.mp3"); if ($null -eq $r) { return $null }
                return [pscustomobject]@{ f = $r; note = ("floor {0:F1}" -f $kA) }
            }
            $rA = Meter (Join-Path $work "$tag.a.mp3"); if ($null -eq $rA) { return $null }
            $rB = Meter (Join-Path $work "$tag.b.mp3"); if ($null -eq $rB) { return $null }
            $t = ($target - $kB) / ($kA - $kB)
            $f = 0..4 | ForEach-Object { $rB[$_] + ($rA[$_] - $rB[$_]) * $t }
            [pscustomobject]@{ f = $f; note = '' }
        }
        $mA = SideMetrics '' 'A'
        $mB = SideMetrics $cfg 'B'
        if ($null -eq $mA -or $null -eq $mB) {
            [pscustomobject]@{ name = $_.BaseName; skip = $true }
        }
        else {
            [pscustomobject]@{
                name = $_.BaseName; skip = $false
                Amean = $mA.f[0]; Aaud = $mA.f[1]; AaudNmr = $mA.f[2]; Amax = $mA.f[3]; Astab = $mA.f[4]; Anote = $mA.note
                Bmean = $mB.f[0]; Baud = $mB.f[1]; BaudNmr = $mB.f[2]; Bmax = $mB.f[3]; Bstab = $mB.f[4]; Bnote = $mB.note
            }
        }
    }
    finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

Write-Host ("{0,-24} {1,20} {2,20} {3,8} {4,8} {5,8}  {6}" -f 'file', 'A(mean/audN/max)', 'B(mean/audN/max)', 'dMean', 'dAudN', 'dStab', 'notes')
Write-Host ("-" * 104)
$agg = @{ Amean=0.0; Bmean=0.0; AaudNmr=0.0; BaudNmr=0.0; Astab=0.0; Bstab=0.0
          Amax=[double]::NegativeInfinity; Bmax=[double]::NegativeInfinity
          better=0; worse=0; maxWorse=0.0; maxWorseAud=0.0; maxWorseStab=0.0; n=0 }
foreach ($r in ($rows | Sort-Object name)) {
    if ($r.skip) { Write-Host ("{0,-24} skipped (unbracketable at {1} kbps)" -f $r.name, $Target); continue }
    $d = $r.Bmean - $r.Amean
    $dAud = $r.BaudNmr - $r.AaudNmr
    $dStab = $r.Bstab - $r.Astab
    $agg.Amean += $r.Amean; $agg.Bmean += $r.Bmean; $agg.AaudNmr += $r.AaudNmr; $agg.BaudNmr += $r.BaudNmr
    $agg.Astab += $r.Astab; $agg.Bstab += $r.Bstab
    if ($r.Amax -gt $agg.Amax) { $agg.Amax = $r.Amax }
    if ($r.Bmax -gt $agg.Bmax) { $agg.Bmax = $r.Bmax }
    if ($d -lt -0.01) { $agg.better++ } elseif ($d -gt 0.01) { $agg.worse++; if ($d -gt $agg.maxWorse) { $agg.maxWorse = $d } }
    if ($dAud -gt $agg.maxWorseAud) { $agg.maxWorseAud = $dAud }
    if ($dStab -gt $agg.maxWorseStab) { $agg.maxWorseStab = $dStab }
    $agg.n++
    $flag = if ($d -lt -0.01) { 'BETTER' } elseif ($d -gt 0.01) { 'worse' } else { '' }
    $notes = (@($r.Anote, $r.Bnote) | Where-Object { $_ }) -join ' / '
    Write-Host ("{0,-24} {1,7:F2}/{2,5:F2}/{3,5:F1} {4,7:F2}/{5,5:F2}/{6,5:F1} {7,8:F3} {8,8:F3} {9,8:F3}  {10} {11}" -f `
        $r.name, $r.Amean, $r.AaudNmr, $r.Amax, $r.Bmean, $r.BaudNmr, $r.Bmax, $d, $dAud, $dStab, $flag, $notes)
}
$n = $agg.n
if ($n -eq 0) { Write-Error 'no scorable files'; exit 2 }
Write-Host ("-" * 104)
Write-Host ("MEAN meanNMR : A={0:F3}  B={1:F3}  delta={2:F3}   (delta<0 => B more transparent at equal size)" -f ($agg.Amean/$n), ($agg.Bmean/$n), (($agg.Bmean-$agg.Amean)/$n))
Write-Host ("MEAN audNMR  : A={0:F3}  B={1:F3}  delta={2:F3}   (audible-band loudness; the campaign veto watches this)" -f ($agg.AaudNmr/$n), ($agg.BaudNmr/$n), (($agg.BaudNmr-$agg.AaudNmr)/$n))
Write-Host ("MEAN hfStab  : A={0:F3}  B={1:F3}  delta={2:F3}   (HF temporal instability, the campaign-12 swirl meter; caught pairs measured +0.15..+1.52 per file)" -f ($agg.Astab/$n), ($agg.Bstab/$n), (($agg.Bstab-$agg.Astab)/$n))
Write-Host ("WORST maxNMR : A={0:F1}  B={1:F1}   (single loudest band across corpus; B must not blow up)" -f $agg.Amax, $agg.Bmax)
Write-Host ("Files: {0} better / {1} worse / {2} tie   worst regression: mean +{3:F3} dB, audNMR +{4:F3} dB, hfStab +{5:F3}" -f $agg.better, $agg.worse, ($n-$agg.better-$agg.worse), $agg.maxWorse, $agg.maxWorseAud, $agg.maxWorseStab)
