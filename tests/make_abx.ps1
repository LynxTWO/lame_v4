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
