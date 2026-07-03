# LAME v4 project instructions

Modernization of the LAME MP3 encoder toward a v4 release. Quality comes first: never trade
quality for speed. FINDINGS.md is the canonical record of what was measured, shipped, and
rejected; read it before proposing encoder changes.

## Non-negotiable engineering rules

- Behavior-preserving changes must be bit-exact. Run `pwsh tests/regress.ps1` (70-case gate)
  after any libmp3lame change; run it again with `-ExtraArgs '--threads 2'` when touching
  anything near the quantization loops. Deliberate quality changes are judged by the
  perceptual harness (`tests/abtest.ps1`), never by the bit-exact gate.
- Every quality claim needs a receipt: corpus measurement, holdout validation for tuning
  work, transient/audibility guardrails, and human ABX before anything ships as a default.
- Opt-in first. New behavior goes behind a flag (`--quality-max`, `--threads`) and must leave
  every existing setting byte-identical.
- Build with `build.cmd` (nmake, canonical) or CMake (`cmake -B build -G Ninja
  -DCMAKE_BUILD_TYPE=Release`; always pass the build type, the VS dev shell injects Debug).
  Makefile.MSVC does not track header dependencies: after editing a widely included header,
  run `build.cmd clean` first or you get silent struct-layout corruption.
- Benchmark traps that already caused wrong conclusions (details in FINDINGS section 2):
  PowerShell `$args`/`$a` are reserved or case-aliased, never assign encode settings to them;
  never time anything while other work saturates the CPU; a green parity gate only counts if
  you also prove the feature engaged.

## Writing rules for all human-facing text

Follow `docs/writing-guide.md` for anything a person will read: FINDINGS.md, docs, READMEs,
commit messages, PR text. The hard rules:

- No em dashes, no en dashes, no typographic Unicode (arrows, checkmarks, unicode minus,
  curly quotes). Use ASCII: " - ", "->", "x", "~", "<=", "...". CI enforces this via
  `tests/lint-docs.sh`; run it before committing Markdown.
- Two readers at once: plain-English claim first, then the receipt sentence with the key
  number, then engineer detail. Tables for three or more clustered numbers, with a
  plain-English lead-in sentence and consistent direction language (lower is better,
  negative means improvement).
- Short sentences. One claim per paragraph. Bold only outcomes, statuses, and numbers that
  matter.
- Precise status verbs: measured, verified, rejected, reverted, bit-exact, opt-in, candidate,
  unmeasured, pending ABX. ABX proves audible difference, not preference. Never promote a
  candidate to a shipped default in prose.
- Keep dead ends in the record, written plainly: what was tried, what measured worse, why it
  was rejected, what it taught.
- Never alter numbers, flags, commit hashes, paths, commands, or function names when editing
  prose. On a status conflict, use the latest explicit status and flag the older line as
  stale.
- Do not call the non-engineer reader a "layman"; use "plain English" or "normal reader".

## Repository layout hints

- `tests/regress.ps1` + `tests/baseline.json`: bit-exact gate (synthetic corpus from
  `tests/corpusgen`).
- `tests/nmr`: encoder-independent perceptual meter (meanNMRdb, audibleFrac, audibleNMRdb,
  maxNMRdb; lower mean is better).
- `tests/abtest.ps1`: corpus A/B harness; `tests/validate_qmax.ps1`: four-metric guardrails;
  `tests/transient`: pre-echo and transient-HF meter; `tests/smoke.sh`: cross-platform CI
  smoke; `tests/lint-docs.sh`: Markdown typography gate.
- `tools/autotune`: end-to-end parameter search (train/val/holdout discipline; corpora live
  in gitignored `tests/corpus*` directories with provenance manifests).
- `tools/podcast`: constrained bitrate-window optimizer.
- `fuzz/`: libFuzzer harness for the mpglib decoder (clang only, `-DLAME_FUZZ=ON`).
- Corpus audio is never committed: it is regenerable, user-supplied, or copyrighted.
