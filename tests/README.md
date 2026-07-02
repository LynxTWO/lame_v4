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

## Rule

If a commit is supposed to preserve output, `regress.ps1` must stay green. If it changes
output on purpose (a quality improvement), that must be stated in the commit and backed by
the perceptual-metric harness — not hand-waved.
