# Podcast Constrained-Optimizer — Design

**Layman:** a "podcast mode" that squeezes the best-sounding MP3 out of a hard size budget
(192 kbps stereo, or 96 kbps mono). Instead of guessing one setting, it tries many *legal*
LAME settings in parallel across all your CPU cores, checks each actually lands on the target
size, scores how good each sounds, and keeps the winner. Modern cores do the searching; the
MP3 itself stays 100% standard and plays everywhere.

**Engineer:** a constrained optimizer wrapping stock LAME ABR/VBR — *not* a new bitstream. Adapted
from the project owner's external brief, cross-checked against this repo's findings.

## Hard rules (non-negotiable, from the brief)

- **Legal MP3 only.** No freeformat, no frame-syntax tricks, no new decoder requirement, no
  disabling the bit reservoir.
- **Don't cap frames with `-B` to hit an average.** `-B` caps *max frame* bitrate, not the
  average, and robs the reservoir. Control the *final average* instead.
- **ABR is the foundation** for a hard average target (predictable size); true VBR candidates
  are *probed* and only accepted if measured bitrate lands in the window.
- **Parallelize candidates, not frames.** MP3 has frame-to-frame reservoir + psy-state
  dependency; splitting one file's frames across threads risks subtle damage. Running N whole
  candidate encodes in parallel is safe and is exactly where 32 cores help.
- **Measure, never trust the request.** Parse the actual encoded frames for the real average
  bitrate; reject anything above target or below `target - tolerance`.

## Presets

| Preset | Target | Mode | Foundation |
| --- | --- | --- | --- |
| `podcast-stereo` | 192 kbps | joint stereo | ABR, uses ~all of budget |
| `podcast-mono` | 96 kbps | mono (downmix if needed) | ABR |
| `podcast-stereo-efficient` | ≤192 kbps | joint stereo | VBR-probed; smaller if transparent |
| `podcast-mono-efficient` | ≤96 kbps | mono | VBR-probed |

Generalized knobs: `--podcast-target <kbps>`, `--podcast-tolerance <kbps>` (default 0.5),
`--podcast-scope audio|file` (audio frames only vs whole file incl. tags — default audio),
`--podcast-jobs <n>`, `--podcast-strict` (reject outside window), `--podcast-report`.

## Candidate grid (all standards-compliant)

Stereo (192): mode = joint stereo; ABR 192 plus nearby internal means; **q sweep incl.
`--quality-max`** (see the q-note below); lowpass ∈ {auto,16,17,18,19 kHz}; highpass ∈
{off,40,60,80 Hz}; optional fractional-`-V` probes landing near target.

Mono (96): mode = mono; ABR 96 plus nearby means; q sweep; lowpass ∈ {auto,12,14,15.5,16 kHz};
highpass ∈ {off,50,70,90 Hz}; a 32 kHz sample-rate candidate for speech-heavy material;
fractional-`-V` probes.

Always: keep the Xing/LAME info tag on (seeking/duration for VBR-aware players).

## The q-note — validated in *this* repo

The brief says treat `-q4` as the safe ABR/CBR baseline because `-q0..-q3` can regress at
constrained bitrates. **We confirmed that regression and root-caused it** (VBR-tuned noise
shaping misbehaving when the outer loop stops early on a fixed budget) — see `FINDINGS.md`
Finding 1. We also **fixed it**: `--quality-max` (and the standalone CBR/ABR fix) route
bit-constrained modes through the coarse-then-fine `amp=3` path, so high effort no longer
regresses. **Consequence for the optimizer:** include both `-q4` (stock-safe) *and*
`--quality-max` (fixed high-effort) in the candidate grid and let the *measured score* pick —
don't hardcode either assumption.

## Pipeline

1. Decode/read source PCM once.
2. Build the bounded candidate set (above).
3. Encode candidates in parallel (`Parallel.ForEach`, one lame process per candidate).
4. Parse each MP3's frame headers → real average bitrate. Reject > target or < target−tol.
   (Scope = audio-frame or whole-file per `--podcast-scope`.)
5. Decode each survivor; validate it decodes cleanly (ideally with ≥2 decoders).
6. **Score** each with the repo's encoder-independent perceptual meter (`tests/nmr` →
   `meanNMRdb`, lower = better) plus decode-safety checks (no clipping/NaN, sane stereo).
7. Pick the best-scoring survivor; break ties toward the simpler/safer setting.
8. Emit the winner (reuse its file — pipeline is deterministic) + a `--podcast-report`
   (chosen settings, measured bitrate, score, lowpass/HP, sample rate, decoder validation).

## Where it lives

A standalone tool (`tools/podcast/`, .NET, mirroring `tests/nmr`/`corpusgen`) that drives the
built `lame.exe` and reuses the `nmr` scorer. **Not** inside `libmp3lame` — the parallelism is
at the candidate level, and keeping it as a wrapper means it composes with any LAME build
(including `--quality-max`) and never touches the frame chain. A thin `lame --podcast-*` alias
in the frontend can shell out to it later if a single-binary UX is wanted.

## Bitrate-window reality check

The `target - 0.5 kbps` window is realistic for long podcasts. For very short clips, MP3 frame
sizes, padding, and tag bytes are discrete chunks that can make a sub-0.5-kbps landing
impossible — `--podcast-strict` should fail loudly rather than silently miss. `--podcast-scope
file` matters most for short clips (tags move the average).

## Implementation notes (v1 shipped: `tools/podcast/`)

- Frame-bitrate parser: built in-tool (MPEG1 Layer III header walk, ID3v2 skip; the Xing/Info
  frame is counted — it is a real stream frame, identical for every candidate, so it cancels).
- Grid: ~28 candidates (effort sweep, contiguous ±ABR mean nudges, lowpass/highpass sweeps,
  CBR floor, VBR probes). Two things the first real runs taught us:
  1. **ABR undershoots on easy material** (a cappella measured 189 for `--abr 192`), so mean
     nudges must go *both* ways and be contiguous — integer requests move the measured average
     ~0.8 kbps/step, and a gap in the sweep can strand the 0.5-wide window between two requests.
  2. **CBR at the target is the guaranteed-legal floor** (padding holds exactly the nominal
     rate). When no integer ABR request lands in the window — which happens — CBR turns
     "no legal candidate" into a legitimate winner. ABR usually beats it when both land;
     measurement decides.
- Validated behaviors on real material (Tom's Diner): stereo-192 winner was
  `--abr 192 --quality-max --lowpass 16` (+0.38 dB over plain ABR at equal measured bitrate);
  mono-96 winner `-b 96 --quality-max --lowpass 15.5`. The meter correctly *rejects* over-eager
  lowpass (12/14 kHz cuts scored +18 dB NMR on this material — audible dulling), so the filter
  choice is measured per input, exactly as intended. `--quality-max` beat `-q4` by 0.48 dB at
  CBR96 mono — the Finding 3 objective change pays off downstream.

## Open questions / next

- Speech-vs-music detection could auto-pick the lowpass/HP sub-grid (later).
- Second-decoder validation source on Windows (mpg123 build, or ffmpeg).
- 32 kHz sample-rate candidate for speech-heavy mono (deferred: the NMR meter has no
  resampler, so cross-rate scoring would need one first).
