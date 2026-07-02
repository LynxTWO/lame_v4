# Regenerate the ABX listening-test package (see tests/abx/README.md).
# Produces original excerpt + stock/fixed q0 CBR128 encodes (mp3 + decoded wav).
# Audio outputs are git-ignored; this script is the committed, reproducible recipe.
[CmdletBinding()]
param(
    [string]$Source = "$PSScriptRoot\corpus\03 - Tom's Diner (A Cappella).wav",
    [double]$StartSec = 18.0,
    [double]$EndSec = 40.0,
    [string]$Setting = '-q 0 -b 128',
    [string]$Stock = "$PSScriptRoot\..\output\lame_base.exe",
    [string]$Fixed = "$PSScriptRoot\..\output\lame_fix.exe",
    [string]$Out = "$PSScriptRoot\abx"
)
$ErrorActionPreference = 'Continue'
foreach ($p in @($Source, $Stock, $Fixed)) { if (-not (Test-Path $p)) { Write-Error "not found: $p"; exit 2 } }
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
$orig = Join-Path $Out 'original.wav'
[WavTrim]::Run($Source, $orig, $StartSec, $EndSec)
$s = @($Setting -split '\s+' | Where-Object { $_ -ne '' })
& $Stock --quiet @s $orig (Join-Path $Out 'A_stock_q0_b128.mp3') 2>&1 | Out-Null
& $Fixed --quiet @s $orig (Join-Path $Out 'B_fixed_q0_b128.mp3') 2>&1 | Out-Null
& $Stock --quiet --decode (Join-Path $Out 'A_stock_q0_b128.mp3') (Join-Path $Out 'A_stock_decoded.wav') 2>&1 | Out-Null
& $Stock --quiet --decode (Join-Path $Out 'B_fixed_q0_b128.mp3') (Join-Path $Out 'B_fixed_decoded.wav') 2>&1 | Out-Null
Write-Host "ABX package written to $Out (see README.md)"
