# ABX listening test — the CBR/ABR -q0 noise-shaping fix

This folder lets you **blind-test** whether the `-q0` CBR/ABR fix (branch
`qa/fix-noiseshaping-cbr`) is audibly better than stock LAME 3.100, so a human ear confirms
what the objective meter measured. (Audio files are git-ignored — copyrighted source. Run
`tests/make_abx.ps1` to (re)generate them from your local corpus.)

## The clip

A ~22-second a cappella passage of *Tom's Diner* — the classic MP3 development killer sample
(solo voice ruthlessly exposes coding artifacts). Encoded at **`-q0 -b128` (CBR 128 kbps)**,
the setting where stock `-q0` regresses worst.

- `original.wav` — the uncompressed reference (X).
- `A_stock_q0_b128.mp3` / `A_stock_decoded.wav` — stock LAME 3.100 `-q0`.
- `B_fixed_q0_b128.mp3` / `B_fixed_decoded.wav` — the fixed `-q0`.

Both MP3s are the **same size** (345 KB) — identical bitrate; only the internal bit allocation
differs. So any audible difference is pure quality, not more data.

## What the meter says (for reference, don't peek before listening)

On this excerpt the fixed encode is **−0.55 dB more transparent** (meanNMRdb −6.03 vs −5.48)
with fewer audible-noise bands (0.261 vs 0.294). Small on paper; the ear is the judge.

## How to ABX

Use foobar2000's **ABX Comparator** (or any ABX tool):

1. **The decisive test — A vs B:** load `A_stock_decoded.wav` and `B_fixed_decoded.wav`. Can
   you reliably tell them apart? If yes, which do you *prefer* (sounds cleaner / less grainy
   / less "swishy" on the sibilants and breaths)?
2. **Transparency test — each vs original:** ABX `original.wav` against `A_stock_decoded.wav`,
   then against `B_fixed_decoded.wav`. The one that's *harder* to distinguish from the original
   is the more transparent encoder. Listen especially to sibilants ("s"/"t"), breaths, and the
   decay/reverb tails — that's where 128 kbps artifacts live.

A result is only meaningful if you score clearly above chance (e.g. ≥ 12/16 correct). Loop a
short tricky phrase rather than the whole clip.

## If you confirm it

Tell the loop and we ship the fix to default `-q0` (and/or fold it into `--quality max`). If
you *can't* hear a difference, the fix is still safe (measured better, VBR untouched,
byte-isolated) — it just means 128 kbps voice is near the meter's floor here; try a harder
track (SQAM glockenspiel/harpsichord/castanets, or `sample20` which measured −1.4 dB).
