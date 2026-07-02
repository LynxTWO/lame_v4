#!/usr/bin/env bash
# Cross-platform smoke test (v4 CI, non-Windows runners).
#
# The bit-exact gate (regress.ps1 + baseline.json) is per-toolchain: hashes recorded with
# MSVC only reproduce with MSVC. On other compilers this script checks the things that must
# hold everywhere instead: every reference setting encodes and decodes cleanly, produces
# sane sizes, and the encoder-independent perceptual meter agrees that (a) a file is
# transparent to itself and (b) more bits mean less audible noise.
#
# Usage: tests/smoke.sh <path-to-lame> [corpus-dir]   (corpus defaults to tests/regress_corpus)
set -euo pipefail

LAME=${1:?usage: smoke.sh <lame> [corpus]}
HERE=$(cd "$(dirname "$0")" && pwd)
CORPUS=${2:-$HERE/regress_corpus}
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

settings=("-V 0" "-V 2" "-V 5" "-b 320" "-b 128" "-q 0 -b 320" "--abr 192" "--quality-max -b 128" "--quality-max --abr 192" "--threads 2 -b 128")

fail=0
for wav in "$CORPUS"/*.wav; do
    base=$(basename "$wav" .wav)
    for s in "${settings[@]}"; do
        id=$(echo "$s" | tr -d ' -')
        mp3="$WORK/$base.$id.mp3"; dec="$WORK/$base.$id.wav"
        if ! "$LAME" --quiet --nohist -t $s "$wav" "$mp3" 2>/dev/null; then
            echo "ENCODE FAIL: $base [$s]"; fail=1; continue
        fi
        if [ ! -s "$mp3" ]; then echo "EMPTY MP3: $base [$s]"; fail=1; continue; fi
        if ! "$LAME" --quiet --decode "$mp3" "$dec" 2>/dev/null || [ ! -s "$dec" ]; then
            echo "DECODE FAIL: $base [$s]"; fail=1
        fi
    done
done

# Perceptual sanity (needs dotnet + tests/nmr): self-transparency and bitrate monotonicity.
if command -v dotnet >/dev/null 2>&1; then
    dotnet build "$HERE/nmr" -c Release -v quiet >/dev/null
    NMR="$HERE/nmr/bin/Release/net8.0/nmr"
    wav=$(ls "$CORPUS"/*.wav | head -1)
    self=$("$NMR" "$wav" "$wav" | awk '{print $1}')
    awk -v s="$self" 'BEGIN{ exit !(s < -90) }' || { echo "SELF-CHECK FAIL: nmr($wav,$wav)=$self (want < -90 dB)"; fail=1; }
    base=$(basename "$wav" .wav)
    lo=$("$NMR" "$wav" "$WORK/$base.b128.wav" | awk '{print $1}')
    hi=$("$NMR" "$wav" "$WORK/$base.b320.wav" | awk '{print $1}')
    awk -v a="$hi" -v b="$lo" 'BEGIN{ exit !(a < b) }' || { echo "MONOTONICITY FAIL: 320kbps ($hi) not cleaner than 128kbps ($lo)"; fail=1; }
    echo "nmr sanity: self=$self dB, cbr128=$lo dB, cbr320=$hi dB"
fi

if [ "$fail" -ne 0 ]; then echo "SMOKE: FAILURES (see above)"; exit 1; fi
echo "SMOKE: all settings encode+decode cleanly."
