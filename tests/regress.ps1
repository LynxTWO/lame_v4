# LAME v4 bit-exact regression harness.
#
# Encodes every corpus WAV at a fixed set of reference settings with the current build's
# lame.exe, then records SHA-256 of (a) the MP3 bytes and (b) the decoded PCM. On the first
# run it writes tests/baseline.json; on later runs it compares and reports any drift.
#
# Purpose: prove that a change is behavior-preserving. Threading, SIMD, and refactors MUST
# reproduce the baseline byte-for-byte. Deliberate quality changes will differ here on
# purpose and are judged by the perceptual-metric harness instead, never by this one.
#
# Usage:
#   pwsh tests/regress.ps1                 # compare against baseline (fail on drift)
#   pwsh tests/regress.ps1 -UpdateBaseline # (re)write the baseline from the current build
[CmdletBinding()]
param(
    [switch]$UpdateBaseline,
    [string]$Lame   = "$PSScriptRoot\..\output\lame.exe",
    # Bit-exact gate uses the deterministic synthetic set only (fast, committed baseline).
    # Real/user tracks live in corpus/ and are used by abtest.ps1 for QUALITY, not here.
    [string]$Corpus = "$PSScriptRoot\regress_corpus",
    [string]$Work   = "$PSScriptRoot\out",
    [string]$BaselineFile = "$PSScriptRoot\baseline.json",
    # Appended to every encode. Lets bit-identical-by-design options prove themselves against
    # the SAME baseline, e.g. -ExtraArgs '--threads 2' must still pass all 70 cases.
    [string]$ExtraArgs = ''
)

$ErrorActionPreference = 'Stop'

# Reference settings that span the quality-relevant code paths. --nores/-S keep output
# deterministic; we fix the tag off so bytes depend only on the audio codec, not metadata.
$settings = @(
    @{ id = 'V0';        args = @('-V','0') },
    @{ id = 'V2';        args = @('-V','2') },
    @{ id = 'V5';        args = @('-V','5') },
    @{ id = 'cbr320';    args = @('-b','320') },
    @{ id = 'cbr128';    args = @('-b','128') },
    @{ id = 'q0_cbr320'; args = @('-q','0','-b','320') },
    @{ id = 'abr192';    args = @('--abr','192') }
)
# Applied to every encode: no bitstream tag, no lame tag frame -> output is pure codec bytes.
$common = @('--quiet','--nohist','-t')
if ($ExtraArgs -ne '') { $common += @($ExtraArgs -split '\s+' | Where-Object { $_ -ne '' }) }

if (-not (Test-Path $Lame)) { throw "lame.exe not found at $Lame - build first (build.cmd)." }
if (-not (Test-Path $Work)) { New-Item -ItemType Directory -Force $Work | Out-Null }

function Sha([string]$path) { (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLower() }

$results = [ordered]@{}
$wavs = Get-ChildItem "$Corpus\*.wav" | Sort-Object Name
foreach ($wav in $wavs) {
    foreach ($s in $settings) {
        $key = "$($wav.BaseName)|$($s.id)"
        $mp3 = Join-Path $Work "$($wav.BaseName).$($s.id).mp3"
        $dec = Join-Path $Work "$($wav.BaseName).$($s.id).dec.wav"
        & $Lame @common @($s.args) $wav.FullName $mp3 2>$null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $mp3)) { throw "encode failed: $key" }
        # Decode the MP3 back to PCM so a change that alters the *decoded* signal is caught
        # even if it somehow produced the same byte length.
        & $Lame --quiet --decode $mp3 $dec 2>$null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dec)) { throw "decode failed: $key" }
        $results[$key] = [ordered]@{ mp3 = (Sha $mp3); pcm = (Sha $dec); mp3len = (Get-Item $mp3).Length }
    }
}

if ($UpdateBaseline) {
    $results | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $BaselineFile
    Write-Host "Baseline written: $($results.Count) cases -> $BaselineFile" -ForegroundColor Green
    exit 0
}

if (-not (Test-Path $BaselineFile)) { throw "No baseline at $BaselineFile. Run with -UpdateBaseline first." }
$baseline = Get-Content $BaselineFile -Raw | ConvertFrom-Json

$drift = @()
foreach ($key in $results.Keys) {
    $b = $baseline.$key
    if ($null -eq $b) { $drift += "NEW    $key (not in baseline)"; continue }
    if ($b.mp3 -ne $results[$key].mp3) { $drift += "MP3    $key  $($b.mp3.Substring(0,12)) -> $($results[$key].mp3.Substring(0,12))" }
    elseif ($b.pcm -ne $results[$key].pcm) { $drift += "PCM    $key (mp3 same, decoded PCM differs)" }
}
foreach ($key in $baseline.PSObject.Properties.Name) { if (-not $results.Contains($key)) { $drift += "MISSING $key" } }

if ($drift.Count -eq 0) {
    Write-Host "BIT-EXACT: all $($results.Count) cases match baseline." -ForegroundColor Green
    exit 0
} else {
    Write-Host "DRIFT in $($drift.Count) case(s):" -ForegroundColor Yellow
    $drift | ForEach-Object { Write-Host "  $_" }
    exit 1
}
