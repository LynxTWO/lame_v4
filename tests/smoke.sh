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

# Perceptual sanity (needs dotnet + tests/nmr). Two checks that hold on the SYNTHETIC corpus:
#   1. self-transparency on a dense file (meter identity: parsing + alignment + ~zero error);
#   2. a "not garbage" ceiling on a real encode's score (catches catastrophic breakage:
#      silence output, wrong sample rate, byte-order bugs).
# Deliberately NOT checked here: bitrate monotonicity. It holds on real music (validated in
# FINDINGS §2b) but NOT per-file on synthetic torture signals -- castanets/pink noise/tone
# mixes measure ~equal at 128 and 320 kbps (pre-echo/tonality dominated, not bit-starved),
# and mostly-silent files bottom out at the meter's epsilon-vs-tiny-mask floor (~-59 dB),
# so naive thresholds here produce false failures (found by the first live CI run).
if command -v dotnet >/dev/null 2>&1; then
    dotnet build "$HERE/nmr" -c Release -v quiet >/dev/null
    NMR="$HERE/nmr/bin/Release/net8.0/nmr"
    wav="$CORPUS/music_mix.wav"    # densest corpus file: no near-silent stretches
    if [ -f "$wav" ]; then
        base=$(basename "$wav" .wav)
        self=$("$NMR" "$wav" "$wav" | awk '{print $1}')
        awk -v s="$self" 'BEGIN{ exit !(s < -90) }' || { echo "SELF-CHECK FAIL: nmr($wav,$wav)=$self (want < -90 dB)"; fail=1; }
        enc=$("$NMR" "$wav" "$WORK/$base.b128.wav" | awk '{print $1}')
        awk -v e="$enc" 'BEGIN{ exit !(e < 20) }' || { echo "GARBAGE-CHECK FAIL: nmr(orig, cbr128)=$enc (want < +20 dB; correct decode measures ~+14 on this synthetic)"; fail=1; }
        echo "nmr sanity: self=$self dB, cbr128=$enc dB"
    fi
fi

if [ "$fail" -ne 0 ]; then echo "SMOKE: FAILURES (see above)"; exit 1; fi
echo "SMOKE: all settings encode+decode cleanly."
