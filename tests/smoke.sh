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
        # No -t: the info tag must be present or decoded output is misaligned by encoder
        # delay/padding, which wrecks the nmr checks below (found the hard way; see FINDINGS
        # measurement pitfalls).
        if ! "$LAME" --quiet --nohist $s "$wav" "$mp3" 2>/dev/null; then
            echo "ENCODE FAIL: $base [$s]"; fail=1; continue
        fi
        if [ ! -s "$mp3" ]; then echo "EMPTY MP3: $base [$s]"; fail=1; continue; fi
        if ! "$LAME" --quiet --decode "$mp3" "$dec" 2>/dev/null || [ ! -s "$dec" ]; then
            echo "DECODE FAIL: $base [$s]"; fail=1
        fi
    done
done

# Perceptual sanity (needs dotnet + tests/nmr). Three checks on the synthetic corpus:
#   1. self-transparency on a dense file (meter identity: parsing + alignment + ~zero error);
#   2. bitrate monotonicity: 320 kbps must measure clearly cleaner than 128 on dense content
#      (music_mix: ~ -3.5 vs ~ -15.3 with correctly tag-aligned decodes);
#   3. a "not garbage" ceiling on the 128 kbps score.
# Two hard-won caveats. Use a DENSE file: mostly-silent files bottom out at the meter's
# epsilon-vs-tiny-mask floor (~ -59 dB even self-vs-self). And encodes feeding the meter
# must carry the info tag (no -t) or decoded output is misaligned by encoder delay/padding
# and everything reads as error: an earlier version of this script measured +14 dB "NMR"
# that was pure offset artifact and wrongly concluded synthetics were not bitrate-monotone.
if command -v dotnet >/dev/null 2>&1; then
    dotnet build "$HERE/nmr" -c Release -v quiet >/dev/null
    NMR="$HERE/nmr/bin/Release/net8.0/nmr"
    wav="$CORPUS/music_mix.wav"    # densest corpus file: no near-silent stretches
    if [ -f "$wav" ]; then
        base=$(basename "$wav" .wav)
        self=$("$NMR" "$wav" "$wav" | awk '{print $1}')
        awk -v s="$self" 'BEGIN{ exit !(s < -90) }' || { echo "SELF-CHECK FAIL: nmr($wav,$wav)=$self (want < -90 dB)"; fail=1; }
        lo=$("$NMR" "$wav" "$WORK/$base.b128.wav" | awk '{print $1}')
        hi=$("$NMR" "$wav" "$WORK/$base.b320.wav" | awk '{print $1}')
        awk -v a="$hi" -v b="$lo" 'BEGIN{ exit !(a < b - 3) }' || { echo "MONOTONICITY FAIL: 320kbps ($hi) not clearly cleaner than 128kbps ($lo)"; fail=1; }
        awk -v e="$lo" 'BEGIN{ exit !(e < 5) }' || { echo "GARBAGE-CHECK FAIL: nmr(orig, cbr128)=$lo (want < +5 dB; correct tag-aligned decode measures ~ -3.5 here)"; fail=1; }
        echo "nmr sanity: self=$self dB, cbr128=$lo dB, cbr320=$hi dB"
    fi
fi

if [ "$fail" -ne 0 ]; then echo "SMOKE: FAILURES (see above)"; exit 1; fi
echo "SMOKE: all settings encode+decode cleanly."
