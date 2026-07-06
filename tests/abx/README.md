# ABX listening tests

Blind-test packages so a human ear confirms what the objective meter measured. (Audio files
are git-ignored - copyrighted source. Run `tests/make_abx.ps1` to (re)generate them from your
local corpus.) The packages, with verdicts where tested:

1. **Finding 1** - the `-q0` CBR/ABR noise-shaping fix (Tom's Diner clip). **CONFIRMED
   2026-07-02: 15/16, p = 0.0003** (log in this folder).
2. **Finding 3** - `--quality-max` v2, the search-objective change (400 Lux, full track).
   Tested 2026-07-04: 9/16, no audible difference.
3. **Finding 6** - campaign-7 CBR128 pair (D/E, 7/16), campaign-8 CBR320 pairs (F/G and
   H/I, both 9/16), and the campaign-11 VBR pairs (J/K and L/M, untested), all documented
   below.

## Finding 1 - the CBR/ABR -q0 noise-shaping fix

## The clip

A ~22-second a cappella passage of *Tom's Diner* - the classic MP3 development killer sample
(solo voice ruthlessly exposes coding artifacts). Encoded at **`-q0 -b128` (CBR 128 kbps)**,
the setting where stock `-q0` regresses worst.

- `original.wav` - the uncompressed reference (X).
- `A_stock_q0_b128.mp3` / `A_stock_decoded.wav` - stock LAME 3.100 `-q0`.
- `B_fixed_q0_b128.mp3` / `B_fixed_decoded.wav` - the fixed `-q0`.

Both MP3s are the **same size** (345 KB) - identical bitrate; only the internal bit allocation
differs. So any audible difference is pure quality, not more data.

## What the meter says (for reference, don't peek before listening)

On this excerpt the fixed encode is **-0.55 dB more transparent** (meanNMRdb -6.03 vs -5.48)
with fewer audible-noise bands (0.261 vs 0.294). Small on paper; the ear is the judge.

## How to ABX

Use foobar2000's **ABX Comparator** (or any ABX tool):

1. **The decisive test - A vs B:** load `A_stock_decoded.wav` and `B_fixed_decoded.wav`. Can
   you reliably tell them apart? If yes, which do you *prefer* (sounds cleaner / less grainy
   / less "swishy" on the sibilants and breaths)?
2. **Transparency test - each vs original:** ABX `original.wav` against `A_stock_decoded.wav`,
   then against `B_fixed_decoded.wav`. The one that's *harder* to distinguish from the original
   is the more transparent encoder. Listen especially to sibilants ("s"/"t"), breaths, and the
   decay/reverb tails - that's where 128 kbps artifacts live.

A result is only meaningful if you score clearly above chance (e.g. >= 12/16 correct). Loop a
short tricky phrase rather than the whole clip.

*(Outcome: confirmed 15/16, p = 0.0003 - the fix shipped to the default `-q0` and the log is
archived in this folder.)*

## Finding 3 - `--quality-max` v2 (the search-objective change)

**The claim to test:** v2 (aggregate noise objective + exhaustive search) sounds at least as
good as v1 everywhere, and better where the meter says so (-0.94 dB mean at CBR128).

**The track - 400 Lux, full length, CBR 128:** chosen *adversarially*. It is the one corpus
file where v2's single worst band-frame got ~4 dB **louder** (33.5 -> 37.7 dB over mask) even
though its overall mean and audible fraction improved. If the objective change hides an
audible artifact anywhere, it's here. The flagged spot is the bass-heavy onset around
**0:21**; also try the busy passages.

- `original_400lux.wav` - uncompressed reference.
- `A_qmaxv1_b128.mp3` / `A_qmaxv1_b128_decoded.wav` - `--quality-max` **v1** (pre-Finding-3).
- `B_qmaxv2_b128.mp3` / `B_qmaxv2_b128_decoded.wav` - `--quality-max` **v2** (current master).
- `C_default_q0_b128.mp3` / `C_default_q0_b128_decoded.wav` - today's default `-q0`, as the
  "does qmax beat the default" bonus comparison.

**How to listen:**

1. **A vs B (the decisive one):** can you tell v1 from v2 at all? If yes, which sounds
   cleaner - especially around 0:21 and in dense sections? (Meter says B: mean -5.68 vs
 -5.00, fewer audible bands 0.286 vs 0.321 - but B's single worst moment is hotter.)
2. **B vs C (bonus):** the full `--quality-max` v2 payoff over the everyday default.
3. If any spot in B sounds *worse* than A, note the timestamp - that's the worst-band
   trade-off surfacing, and exactly what this package exists to catch.

## Finding 6 - the auto-tuned psymodel constants (bonus pair)

`D_stockq0_b128` vs `E_autotuned_b128` (Tom's Diner, full track, CBR128): the campaign-7
winner, `--ns-bass -2.50 --athlower 1.50`. Two knobs, found under the corrected measurement
chain, holdout-validated (-0.121 dB SQAM, -0.217 dB library tracks), audibility-flat per
file, and the cleanest transient profile of any config tested. If E sounds at least as good
as D, and especially if you prefer it, the candidate earns consideration beyond opt-in.
Listen for overall cleanliness; the tuning tightens bass masking slightly and spends a
little more near the threshold of hearing.

## Finding 6 per-rate - the CBR 320 candidate (the big one)

`F_stock320` vs `G_tuned320` (h05, the library-holdout excerpt where the tuned config's
audible errors improved most, -2.18 dB), and `H_stock320_lux` vs `I_tuned320_lux` (400 Lux,
full track) as a dense second opinion. The candidate is the campaign-8 winner: bass and mid
masking tightened hard, treble relaxed, ATH raised. Holdout receipts: -1.81 dB on SQAM,
-2.81 dB on library tracks, audible errors quieter on every file checked, transients clean.

Fair warning for calibration: 320 kbps is deeply transparent territory, so even a 2 dB
measured improvement may be inaudible. A null here would not weaken the measured result; a
positive would make this the first tuning change with a human-audible receipt.

*(Outcome 2026-07-05/06: 9/16 on both pairs, p = 0.40 - no audible difference demonstrated,
as the calibration note anticipated. Logs archived in this folder.)*

## Finding 6 campaign 11 - the VBR candidate at equal measured size

`J_stockvbr128` vs `K_tunedvbr128` (h10, the library-holdout excerpt with the campaign's
largest measured win, -3.52 dB mean NMR), and `L_stockvbr128_sqam28` vs
`M_tunedvbr128_sqam28` (SQAM track 28) as the honest worst case: it is the one holdout
file where the tuned config's audible-band loudness rose most (+1.17 dB audNMR) even
though its mean improved. If the config hides an audible artifact anywhere, it's there.

Both sides of each pair are bisected in fractional `-V` to the closest encode at or below
128 kbps measured (the script prints the landed rates). Measured VBR bitrate cliffs near
the target can leave the two sides apart - on h10 the stock side landed 125.0 kbps and
the tuned side 119.2 - but the at-or-below rule means the size gap can only handicap the
candidate, never favor it: the tuned encode never has more data, sometimes noticeably
less. SQAM 28 landed clean (127.9 vs 128.0). The candidate is the campaign-11 winner: bass and sfb21 masking
tightened, alto/treble relaxed slightly, fewer short blocks, ATH lowered. Holdout receipts:
16 of 16 library files better (mean -2.47 dB), 60 of 64 equal-size SQAM files better
(-0.86 dB), transients clean. 128 kbps VBR is NOT transparent territory - this is the pair
where an audible difference is most plausible of anything the tuning campaigns have
produced.

## If you confirm it

Finding 1 is already shipped. For Finding 3: v2 is opt-in and all objective guardrails passed,
so it stays merged either way - but an audible *regression* anywhere in B would reopen the
worst-band question (we'd consider a hybrid objective that caps max_noise while minimizing the
aggregate). A "can't tell them apart" result is also fine: it means the measured gains are
below audibility on this material, and the mode still wins on the meter corpus-wide.
