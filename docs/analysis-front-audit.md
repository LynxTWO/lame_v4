# Analysis-front parallelism audit (psymodel + MDCT)

Shared-state audit of the encode pipeline's analysis stages, done before any threading work
on them - the same discipline that made the quantization threading (Finding 4) bit-exact by
construction. All claims below are from reading the 3.100 source at the cited locations, on
top of measured stage costs.

## Where encode time goes (measured, Tom's Diner, stage instrumentation)

| Setting | psymodel | MDCT | quantization | bitstream |
| --- | --- | --- | --- | --- |
| default `-q0` CBR128 | 26% | 13% | 58% | 3% |
| `--quality-max` CBR128 | 2% | 1% | 97% | 0% |
| `-V 2` | 24% | 11% | 61% | 4% |

Quantization dominates everywhere and is already channel-parallel (`--threads 2`, Finding 4:
1.5x on `--quality-max` CBR320/ABR192). The analysis front (psymodel + MDCT ~ 39%) is the
remaining lever **for default/VBR settings only**.

## Pipeline shape (encoder.c, `lame_encode_mp3_frame`)

1. **Stage 1 - psymodel**: `L3psycho_anal_vbr` per granule (used by ALL modes in 3.100,
   including CBR/ABR). Outputs masking ratios, perceptual entropy, block types.
2. `adjust_ATH` (reads loudness the psymodel just wrote).
3. **Stage 2 - MDCT**: `mdct_sub48` (polyphase filterbank + MDCT, both channels).
4. Stage 3 - MS/LR decision; Stage 4 - iteration loop (quantization); Stage 5 - bitstream.

## Coupling inventory - psymodel (`psymodel.c: L3psycho_anal_vbr`)

Channels processed per granule: `n_chn_psy` = 4 for joint stereo (0=L, 1=R, 2=Mid, 3=Side).

**Cross-channel couplings (all verified):**

- **M/S FFT is derived in place from L/R** (`vbrpsy_compute_fft_l`, chn==2 branch): the L and
  R FFT work buffers `wsamp_L[0..1]` are *overwritten* with (L+R)/sqrt(2) and (L-R)/sqrt(2). Consequences: L and R
  (chn 0,1) are mutually independent (disjoint buffer slots, `fft_long(chn)`); chn 2 requires
  both finished AND destroys their data; chn 3 reads the converted slot 1. Same structure for
  short blocks (`vbrpsy_compute_fft_s`).
- **`vbrpsy_compute_MS_thresholds`** mixes eb/thr of all four channels (joint stereo only).
- **Block-type synchronization** (`vbrpsy_compute_block_type`) couples `uselongblock[0..1]`.
- **Attack detection** (`vbrpsy_attack_detection`) computes M/S attack signals from L/R
  samples - cross-channel at derivation, per-channel afterwards.

**Per-channel state (disjoint slots - parallel-safe):** `psv->tot_ener[chn]`,
`psv->loudness_sq_save[chn<2]`, `psv->thm[chn]` / `psv->en[chn]` (written by
`convert_partition2scalefac_*`), `psv->last_attacks[chn]`, masking history `nb_l1/nb_l2[chn]`.

**Shared locals needing privatization under any threading:** `fftenergy[HBLKSIZE]` and
`fftenergy_s` are single buffers recomputed per channel iteration.

**Cross-frame couplings (sequential chain, pipeline-relevant):** previous-frame `psv->thm`
(pre-echo control reads `last_thm`), `last_attacks`, loudness save/restore, and - discovered
during Finding 4 - `sv_qnt.masking_lower`, written by the *quantization* loop per (gr,ch) and
read by the *next frame's* psymodel (`pecalc_s/l`, threshold scaling). Its value depends only
on the last channel's block type (a psymodel output), not on quantization results, so a
pipelined design could compute it without waiting for quantization - but it is the kind of
coupling that makes frame pipelining invasive.

## Coupling inventory - MDCT (`newmdct.c: mdct_sub48`)

Filterbank + MDCT per channel over `sv_enc` per-channel state (`sb_sample[ch]`); no
cross-channel data flow until the (later) MS conversion in the iteration stage. Channel-
parallel split is structurally straightforward; needs the same private-scratch treatment.

## Decompositions considered, with honest expected gains (Amdahl from the table above)

1. **Channel-parallel psymodel within a granule** (bit-exact candidate): run L and R analysis
   on two workers (private `fftenergy`, disjoint psv slots), join, do the M/S in-place
   derivation + `MS_thresholds` sequentially, then fan out M/S masking and the per-channel
   conversions again. FP order per channel is preserved -> bit-exact by the same argument as
   Finding 4. Parallelizable share ~ 3/4 of psymodel -> psy 26% -> ~15% of total: **~1.13x** at
   default settings (~nothing at `--quality-max`). Roughly six fork/join points per granule.
2. **+ channel-parallel MDCT**: 13% -> ~7%: combined **~1.2x** at default.
3. **Frame pipelining** (analysis of frame N+1 concurrent with quantization of frame N):
   theoretical ~1.6x at default when combined with (1)+(2) and threaded quantization, but
   requires double-buffering every psymodel->iteration interface (masking arrays, pe, block
   types written into `l3_side.tt` during Stage 1) plus the `masking_lower` handoff. Most
   invasive option in a 25-year-old global-state codebase; deferred.
4. **Do nothing here; deepen the search instead**: quality-max v3 (wider search) concentrates
   even more cost in quantization, where `--threads 2` already scales. Default encodes run
   ~100x realtime single-threaded; per-file speed there is nobody's bottleneck (batch
   throughput is already process-parallel via CUETools/the podcast tool).

## Verdict

Implement (1)+(2) as an opt-in increment under the existing `--threads` flag *only if*
default/VBR single-file latency matters to a real workflow; otherwise the next
quality-per-effort win is quality-max v3, which makes the already-shipped quantization
threading more valuable automatically. Either way, nothing in the analysis front blocks the
v3 work. (Also worth cutting: the per-granule worker join overhead. ABR precomputes all
granule bit targets per frame, so its worker can run channel 1's *whole frame* - both
granules, in order, preserving the CurrentStep chain - amortizing channel imbalance across
granules and halving sync points. CBR cannot: granule 1's targets depend on granule 0's
reservoir accounting, so its 1.17x at 128 kbps is close to structural.)

## Implemented and measured (2026-07-10): a dead end, reverted with receipts

Decompositions (1)+(2) were built exactly as designed: the Finding 4 worker gained a
generic function-pointer job, the psymodel forked L/R (private `fftenergy`, M/S and
`MS_thresholds` sequential after the join), the MDCT forked per channel, and the worker
start was widened to every stereo session with the quantize dispatch keeping its own
CBR/ABR-and-no-substep eligibility.

The correctness argument held completely: bit-exact gate 70 of 70 on the sequential path,
and threads-on vs threads-off hash-identical on 21 of 21 setting-x-file combinations
(V2/V5/CBR128/CBR320/ABR192/quality-max/mono across music, castanets and mix material -
the short-block fork included).

The performance did not. Best-of-three wall times on an idle 5950X, full 400 Lux track:

| Setting | sequential | `--threads 2` | speedup |
| --- | --- | --- | --- |
| default `-V4` | 2.84 s | 5.48 s | 0.52x |
| `-V2` | 2.99 s | 5.68 s | 0.53x |
| `-q0` CBR128 | 3.42 s | 6.09 s | 0.56x |
| CBR320 | 2.83 s | 5.78 s | 0.49x |
| quality-max CBR320 | 84.2 s | 55.3 s | 1.52x (quantize threading, unharmed) |

The arithmetic closes the case: ~9,400 granules x ~4 fork/joins x ~70 us of Win32
auto-reset-event wake latency is ~2.6 s of pure synchronization - the entire observed
gap - against an Amdahl ceiling of ~0.4 s of parallelizable analysis work. Kernel-event
handoff at analysis granularity costs roughly six times the work it parallelizes. A
user-space spin handoff could plausibly reach the predicted ~1.15x, but that buys four
seconds per album on encodes already running ~90x realtime, in exchange for a lock-free
protocol inside 25-year-old global-state code. Option 4's judgment stands measured: per-
file default-speed is nobody's bottleneck. The fork wiring was reverted the same session
(gates re-verified green); this section is the receipt, and the audit above remains the
map for anyone who someday has a real latency requirement.
