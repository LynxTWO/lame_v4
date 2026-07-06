# Regenerate the ABX listening-test packages (see tests/abx/README.md).
# Audio outputs are git-ignored; this script is the committed, reproducible recipe.
#
#   Finding 1 (Tom's Diner clip): stock -q0 CBR128 vs fixed -q0. Needs the two frozen builds
#       (lame_base/lame_fix); the section is skipped with a note if they're gone.
#   Finding 3 (400 Lux, full track): A = --quality-max v1 vs B = v2 (the objective change),
#       plus C = the current default -q0 for a "does qmax beat the default" ABX. 400 Lux is
#       deliberate: it's the one corpus file where v2's single worst band-frame rose ~4 dB
#       while its mean and audible fraction improved -- if v2 hides an audible artifact
#       anywhere, it's here (the ~21 s onset is the flagged spot). Full track so any passage
#       can be looped in foobar2000's ABX comparator.
[CmdletBinding()]
param(
    [string]$Source = "$PSScriptRoot\corpus\03 - Tom's Diner (A Cappella).wav",
    [double]$StartSec = 18.0,
    [double]$EndSec = 40.0,
    [string]$Setting = '-q 0 -b 128',
    [string]$Stock = "$PSScriptRoot\..\output\lame_base.exe",
    [string]$Fixed = "$PSScriptRoot\..\output\lame_fix.exe",
    # Finding 3 inputs: the frozen pre-Finding-3 build, the current build, and the track.
    [string]$QmaxV1 = "$PSScriptRoot\..\output\lame_qmax_v1.exe",
    [string]$Lame = "$PSScriptRoot\..\output\lame.exe",
    [string]$LuxSource = "$PSScriptRoot\corpus\02 - 400 Lux.wav",
    [string]$Out = "$PSScriptRoot\abx"
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $Out | Out-Null

$code = @'
using System; using System.IO;
public static class WavTrim {
  public static void Run(string inp, string outp, double a, double b){
    var d=File.ReadAllBytes(inp); int pos=12,dp=-1,dl=0; short ch=2,bits=16; int sr=44100;
    while(pos+8<=d.Length){ string id=System.Text.Encoding.ASCII.GetString(d,pos,4); int sz=BitConverter.ToInt32(d,pos+4);
      if(id=="fmt "){ ch=BitConverter.ToInt16(d,pos+10); sr=BitConverter.ToInt32(d,pos+12); bits=BitConverter.ToInt16(d,pos+22);}
      else if(id=="data"){ dp=pos+8; dl=sz; break;} pos+=8+sz+(sz&1);}
    int fr=ch*bits/8; int sF=(int)(a*sr), eF=(int)(b*sr); int tot=dl/fr; if(eF>tot)eF=tot; if(sF<0)sF=0; int n=eF-sF; int ol=n*fr;
    using(var bw=new BinaryWriter(File.Create(outp))){
      bw.Write(new char[]{'R','I','F','F'}); bw.Write(36+ol); bw.Write(new char[]{'W','A','V','E'});
      bw.Write(new char[]{'f','m','t',' '}); bw.Write(16); bw.Write((short)1); bw.Write(ch); bw.Write(sr);
      bw.Write(sr*fr); bw.Write((short)fr); bw.Write(bits); bw.Write(new char[]{'d','a','t','a'}); bw.Write(ol);
      bw.Write(d, dp+sF*fr, ol); } }
}
'@
Add-Type -TypeDefinition $code

# ---- Finding 1: Tom's Diner clip, stock vs fixed -q0 ----
if ((Test-Path $Stock) -and (Test-Path $Fixed) -and (Test-Path $Source)) {
    $orig = Join-Path $Out 'original.wav'
    [WavTrim]::Run($Source, $orig, $StartSec, $EndSec)
    $s = @($Setting -split '\s+' | Where-Object { $_ -ne '' })
    & $Stock --quiet @s $orig (Join-Path $Out 'A_stock_q0_b128.mp3') 2>&1 | Out-Null
    & $Fixed --quiet @s $orig (Join-Path $Out 'B_fixed_q0_b128.mp3') 2>&1 | Out-Null
    & $Stock --quiet --decode (Join-Path $Out 'A_stock_q0_b128.mp3') (Join-Path $Out 'A_stock_decoded.wav') 2>&1 | Out-Null
    & $Stock --quiet --decode (Join-Path $Out 'B_fixed_q0_b128.mp3') (Join-Path $Out 'B_fixed_decoded.wav') 2>&1 | Out-Null
    Write-Host "Finding 1 package written (Tom's Diner clip)."
} else {
    Write-Host "Finding 1 SKIPPED (needs $Stock, $Fixed and the Tom's Diner track)."
}

# ---- Finding 6: Tom's Diner full track, stock -q0 CBR128 vs the auto-tuned candidate ----
# Campaign-7 winner (honest measurement chain; holdout-validated -0.121 dB SQAM, -0.217 dB
# library; audibility-flat per file; cleanest transient profile tested; see FINDINGS).
if (Test-Path $Source) {
    $tuned = @('-q','0','-b','128','--ns-bass','-2.50','--athlower','1.50')
    Copy-Item $Source (Join-Path $Out 'original_diner_full.wav') -Force
    & $Lame --quiet -q 0 -b 128 $Source (Join-Path $Out 'D_stockq0_b128.mp3') 2>&1 | Out-Null
    & $Lame --quiet @tuned $Source (Join-Path $Out 'E_autotuned_b128.mp3') 2>&1 | Out-Null
    foreach ($t in 'D_stockq0_b128', 'E_autotuned_b128') {
        & $Lame --quiet --decode (Join-Path $Out "$t.mp3") (Join-Path $Out "$($t)_decoded.wav") 2>&1 | Out-Null
    }
    Write-Host "Finding 6 pair written (Tom's Diner full track, stock vs auto-tuned)."
} else {
    Write-Host "Finding 6 pair SKIPPED (needs the Tom's Diner track)."
}

# ---- Finding 6 per-rate: CBR 320, stock vs the campaign-8 tuned candidate ----
# The project's largest validated gain (SQAM -1.81, library -2.81 dB; audNMR improves on
# every holdout file). Material: h05 from the library holdout, the excerpt with the largest
# measured audible-error improvement (-2.18 dB); see tests/corpus_holdout/manifest.txt for
# its source. Also 400 Lux full track for a dense-material second opinion.
$W320 = @('-q','0','-b','320','--ns-bass','-8.00','--ns-alto','-8.00','--ns-treble','5.46',
          '--ns-sfb21','4.26','--nsmsfix','1.91','--shortthreshold','5.46,31.01','--athlower','5.22')
$h05 = "$PSScriptRoot\corpus_holdout\h05.wav"
if (Test-Path $h05) {
    Copy-Item $h05 (Join-Path $Out 'original_h05.wav') -Force
    & $Lame --quiet -q 0 -b 320 $h05 (Join-Path $Out 'F_stock320.mp3') 2>&1 | Out-Null
    & $Lame --quiet @W320 $h05 (Join-Path $Out 'G_tuned320.mp3') 2>&1 | Out-Null
    foreach ($t in 'F_stock320', 'G_tuned320') {
        & $Lame --quiet --decode (Join-Path $Out "$t.mp3") (Join-Path $Out "$($t)_decoded.wav") 2>&1 | Out-Null
    }
    Write-Host "Finding 6 per-rate pair written (h05 at CBR 320, stock vs tuned)."
} else {
    Write-Host "Finding 6 per-rate pair SKIPPED (needs tests/corpus_holdout/h05.wav)."
}
if (Test-Path $LuxSource) {
    & $Lame --quiet -q 0 -b 320 $LuxSource (Join-Path $Out 'H_stock320_lux.mp3') 2>&1 | Out-Null
    & $Lame --quiet @W320 $LuxSource (Join-Path $Out 'I_tuned320_lux.mp3') 2>&1 | Out-Null
    foreach ($t in 'H_stock320_lux', 'I_tuned320_lux') {
        & $Lame --quiet --decode (Join-Path $Out "$t.mp3") (Join-Path $Out "$($t)_decoded.wav") 2>&1 | Out-Null
    }
    Write-Host "Finding 6 per-rate second pair written (400 Lux at CBR 320)."
}

# ---- Finding 6 campaign 11: VBR at equal measured 128 kbps, stock -V vs tuned ----
# The winner improves every library holdout file at equal size (mean -2.47 dB; see
# FINDINGS campaign 11). Both sides are bisected in fractional -V to the closest encode
# at or below 128 kbps measured, so the ABX compares equal-size files, not equal settings.
# Material: h10, the largest measured win (-3.52 dB mean NMR), and SQAM track 28, the
# honest worst case (audNMR +1.17, the largest flag the holdout raised). SQAM 17 (+0.73)
# is the runner-up if a third pair is ever wanted.
$W11 = '--ns-bass -4.68 --ns-alto 3.22 --ns-treble 2.47 --ns-sfb21 -7.87 --nsmsfix 1.48 --shortthreshold 8.45,47.99 --athlower 2.86'
$Measure = "$PSScriptRoot\..\tools\podcast\bin\Release\net8.0\podcast.exe"
function VbrBelowTarget([string]$wav, [string]$extra, [string]$outMp3, [double]$target = 128) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("abxv_" + [Guid]::NewGuid().ToString('N').Substring(0, 8) + ".mp3")
    $lo = 0.0; $hi = 9.99; $v = 4.0; $kA = [double]::NaN; $kB = [double]::NaN
    for ($it = 0; $it -lt 14; $it++) {
        $vs = "{0:0.###}" -f $v
        $p = Start-Process -FilePath $Lame -ArgumentList "--quiet --nohist -V $vs --resample 44.1 $extra `"$wav`" `"$tmp`"" -Wait -NoNewWindow -PassThru
        if ($p.ExitCode -ne 0) { return $null }
        $k = [double](& $Measure --measure $tmp)
        if ($k -le 0) { return $null }
        if ($k -gt $target) { $lo = $v; if ([double]::IsNaN($kA) -or $k -lt $kA) { $kA = $k } }
        else { if ([double]::IsNaN($kB) -or $k -gt $kB) { $kB = $k; Copy-Item $tmp $outMp3 -Force }; $hi = $v }
        if (-not [double]::IsNaN($kA) -and -not [double]::IsNaN($kB) -and ($kA - $kB) -lt 0.25) { break }
        if (($hi - $lo) -lt 0.004) { break }
        $v = ($lo + $hi) / 2
    }
    Remove-Item $tmp -ErrorAction SilentlyContinue
    if ([double]::IsNaN($kB)) { return $null }
    $kB
}
$h10 = "$PSScriptRoot\corpus_holdout\h10.wav"
if ((Test-Path $h10) -and (Test-Path $Measure)) {
    Copy-Item $h10 (Join-Path $Out 'original_h10.wav') -Force
    $kJ = VbrBelowTarget $h10 '' (Join-Path $Out 'J_stockvbr128.mp3')
    $kK = VbrBelowTarget $h10 $W11 (Join-Path $Out 'K_tunedvbr128.mp3')
    if ($null -ne $kJ -and $null -ne $kK) {
        foreach ($t in 'J_stockvbr128', 'K_tunedvbr128') {
            & $Lame --quiet --decode (Join-Path $Out "$t.mp3") (Join-Path $Out "$($t)_decoded.wav") 2>&1 | Out-Null
        }
        Write-Host ("Campaign 11 pair written (h10 VBR: stock {0:F1} kbps vs tuned {1:F1} kbps)." -f $kJ, $kK)
    } else { Write-Host "Campaign 11 h10 pair SKIPPED (bisection failed)." }
} else {
    Write-Host "Campaign 11 h10 pair SKIPPED (needs tests/corpus_holdout/h10.wav and the podcast tool)."
}
$sq28 = "$PSScriptRoot\corpus\SQAM\28.wav"
if ((Test-Path $sq28) -and (Test-Path $Measure)) {
    Copy-Item $sq28 (Join-Path $Out 'original_sqam28.wav') -Force
    $kL = VbrBelowTarget $sq28 '' (Join-Path $Out 'L_stockvbr128_sqam28.mp3')
    $kM = VbrBelowTarget $sq28 $W11 (Join-Path $Out 'M_tunedvbr128_sqam28.mp3')
    if ($null -ne $kL -and $null -ne $kM) {
        foreach ($t in 'L_stockvbr128_sqam28', 'M_tunedvbr128_sqam28') {
            & $Lame --quiet --decode (Join-Path $Out "$t.mp3") (Join-Path $Out "$($t)_decoded.wav") 2>&1 | Out-Null
        }
        Write-Host ("Campaign 11 worst-case pair written (SQAM 28: stock {0:F1} kbps vs tuned {1:F1} kbps)." -f $kL, $kM)
    } else { Write-Host "Campaign 11 SQAM 28 pair SKIPPED (bisection failed)." }
} else {
    Write-Host "Campaign 11 SQAM 28 pair SKIPPED (needs tests/corpus/SQAM/28.wav and the podcast tool)."
}

# ---- Finding 3: 400 Lux full track, qmax v1 vs v2 (+ default -q0 reference) ----
if ((Test-Path $QmaxV1) -and (Test-Path $Lame) -and (Test-Path $LuxSource)) {
    Copy-Item $LuxSource (Join-Path $Out 'original_400lux.wav') -Force
    & $QmaxV1 --quiet --quality-max -b 128 $LuxSource (Join-Path $Out 'A_qmaxv1_b128.mp3') 2>&1 | Out-Null
    & $Lame   --quiet --quality-max -b 128 $LuxSource (Join-Path $Out 'B_qmaxv2_b128.mp3') 2>&1 | Out-Null
    & $Lame   --quiet -q 0 -b 128          $LuxSource (Join-Path $Out 'C_default_q0_b128.mp3') 2>&1 | Out-Null
    # One decoder (the current build) for every file so decode differences can't confound.
    foreach ($t in 'A_qmaxv1_b128', 'B_qmaxv2_b128', 'C_default_q0_b128') {
        & $Lame --quiet --decode (Join-Path $Out "$t.mp3") (Join-Path $Out "$($t)_decoded.wav") 2>&1 | Out-Null
    }
    Write-Host "Finding 3 package written (400 Lux, full track)."
} else {
    Write-Host "Finding 3 SKIPPED (needs $QmaxV1, $Lame and the 400 Lux track)."
}
