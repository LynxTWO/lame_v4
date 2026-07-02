# LAME v4 test / measurement harness

The quality-first v4 work (see the CUETools repo `docs/review/lame-v4-quality-plan.md`) is
gated by two independent measurements. This folder holds the first one; the perceptual
metric harness is added in a later phase.

## Bit-exact regression (`regress.ps1`)

Proves a change is **behavior-preserving**. Threading, SIMD, and refactors must keep this
green byte-for-byte. Deliberate quality changes are expected to break it — they are judged
by the perceptual metrics, never by this harness.

```
build.cmd                                  # build output\lame.exe (VS2022, 64-bit)
dotnet run --project tests\corpusgen -- tests\corpus    # (re)generate the critical-signal corpus
pwsh tests\regress.ps1 -UpdateBaseline     # lock a baseline from the CURRENT build
pwsh tests\regress.ps1                     # compare a later build; exit 1 on any drift
```

- **Corpus** (`corpusgen/`): deterministic, seeded WAVs that stress pre-echo/transients,
  tonal masking, HF handling, noise allocation, and stereo decisions. Regenerable, so the
  `.wav` files are git-ignored. Real user-supplied killer samples can be dropped into
  `tests/corpus/` and are picked up automatically.
- **Baseline** (`baseline.json`, committed): SHA-256 of the MP3 bytes and of the decoded
  PCM for every corpus item × reference setting (V0/V2/V5, CBR 320/128, q0 CBR320, ABR192).
  The pristine 3.100 baseline is 70 cases and is the reference every bit-exact change must
  reproduce.

## Perceptual noise-to-mask meter (`nmr/`) + A/B harness (`abtest.ps1`)

Judges **quality**, encoder-independently. `nmr` (a net8 tool) takes an original WAV and a
decoded WAV and measures how much coding noise sits **above** a masking threshold it derives
itself (critical-band energy + spreading + absolute threshold) — deliberately not LAME's own
model, so improving LAME cannot trivially game the score. Primary output is `meanNMRdb`
(lower = more transparent). Validated: it is monotone with bitrate (128k -3.5 → 320k -15.3 dB
on `music_mix`).

`abtest.ps1` encodes the whole corpus with two `lame.exe` builds (A = reference, B = variant)
at one setting, decodes both, and reports per-file and corpus-mean `meanNMRdb` plus delta.
**delta < 0 means B is more transparent than A at the same bitrate — a real quality win.**

```
dotnet build tests\nmr -c Release
pwsh tests\abtest.ps1 -A output\lame.exe -B ..\variant\output\lame.exe -Setting '-V0'
pwsh tests\abtest.ps1 -Setting '-b192'    # A=B self-check -> 0.000 delta everywhere
```

This is the day-to-day gate for the deep-search flagship (Q-A): a change that lands must show
a negative corpus-mean delta at equal bitrate, and (for behavior-preserving changes) keep
`regress.ps1` green.

## Rule

If a commit is supposed to preserve output, `regress.ps1` must stay green. If it changes
output on purpose (a quality improvement), it must show a negative `abtest.ps1` delta at equal
bitrate — measured, not hand-waved — and ultimately survive human ABX before shipping default-on.
