# Writing Guide for LAME v4 Technical Docs

Use these rules when writing or rewriting LAME v4 project notes, changelogs, findings, reports, or README sections. The voice belongs to the project owner; follow it.

## Core voice

Write like a sharp technical investigator explaining hard work to two readers at once.

One reader is smart but not a codec engineer.

The other reader is an engineer who needs exact receipts.

The tone should be clear, direct, calm, human, and evidence-first.

No corporate fog. No hype. No fake humility. No victory lap without the measurement.

## The governing pattern

Use this structure whenever possible:

1. Plain-English claim.
2. One receipt sentence.
3. Engineer details.
4. Measurement table.
5. Safety or scope note.
6. Status.

A good section sounds like this:

> The old path looked smarter, but it spent bits in the wrong place.
>
> At CBR 128, the measured result was +0.333 dB worse than `-q4`, and the ABX test later confirmed the fixed path was audibly different.

That is the style. Zinger first. Receipt immediately after.

## Sentence rules

Use short sentences. Use one claim per paragraph. Keep most paragraphs to one to three sentences.

Use grade-10 readability unless the code itself requires precision.

Use active voice when it is cleaner. Prefer concrete mechanics over vague evaluation.

Say what changed, where it changed, how it was measured, and what moved.

## Punctuation rules

Do not use em dashes. Do not use en dashes. Use periods, commas, parentheses, colons, semicolons, or a simple hyphen instead. (`tests/lint-docs.sh` enforces this in CI.)

Use straight quotes unless the surrounding file already requires typographic quotes.

Do not use all caps except acronyms such as MP3, CBR, ABR, VBR, NMR, SQAM, ABX, MDCT, CI, API, and ASAN.

## Emphasis rules

Use bold only for outcomes, status, or numbers that matter. Do not bold decorations. Do not italicize every clever phrase. One emphasized phrase should earn its place.

## Technical fidelity rules

Never change numbers. Never change flags. Never change branch names, commit hashes, file paths, commands, function names, or config names.

Never infer a result that is not in the source. Never turn a candidate into a shipped default.

Never turn an ABX difference into a preference claim unless the source explicitly says preference was tested.

If the source says two conflicting status things, resolve from the latest explicit status section, then flag the older line as stale.

## Measurement language

Use precise status verbs: Measured. Verified. Rejected. Reverted. Bit-exact. Opt-in. Candidate. Unmeasured. Pending ABX. Scoped to CBR/ABR. VBR unchanged.

Do not say: proves better (unless the evidence really proves that exact claim), revolutionary, massive, perfect, obviously, guaranteed (unless byte-exactness or an actual guarantee is being discussed).

For ABX, say it proves audible difference. Do not say it proves preference unless preference was tested.

## Tables

Use tables for deltas, guardrails, status, and selection-rule failures. Do not bury three or more numbers in a paragraph.

Every table should have a plain-English sentence before it telling the reader what to look for.

Every table should use the same direction language: lower is better, negative means improvement, positive means regression.

## Section order for long reports

1. One-line read.
2. What this project is.
3. Measurement foundation.
4. Findings.
5. Other landed work.
6. Reproduction steps.
7. Reviewer decisions.
8. Current status.
9. Glossary.

Do not let the roadmap become a junk drawer. Move completed technical findings into findings. Move operational proof into status. Move next work into next.

## Finding format

```markdown
### Finding X. Clear claim.

Plain-English explanation.

Receipt sentence with the key number.

#### Root cause.

Engineer explanation.

#### Fix.

Code-path explanation.

#### Results.

Table.

#### Status.

Merged, candidate, rejected, pending, or unmeasured.
```

Skip subsections only when the finding is small.

## Dead-end rules

Keep the failures. The failures build trust. But write them cleanly:

- What was tried.
- Why it seemed reasonable.
- What measured worse.
- Why it was rejected.
- What the failure taught.

Do not dramatize the failure. Do not bury the lesson.

## Goodhart and overfitting language

When a search improves the scalar mean but worsens audible guardrails, name the mechanism plainly.

Good line:

> The mean got better because the errors became fewer and louder. That is not a win. That is the metric getting played.

Use that style sparingly. The bite works because the next sentence carries the data.

## Code and command blocks

Keep code blocks exact. Do not wrap paths in prose if a command block is cleaner. Do not add syntax that was not in the source. Use `powershell`, `cmd`, `c`, or `text` fences only when helpful.

## Roadmap rules

Split roadmap into two tables: Done and Next.

Completed items should not be rewritten as future work. Pending items should include why they matter. Avoid long checklist bullets that repeat the whole findings section.

## Things to remove or reduce

Reduce repeated phrases like: quality-first effort, honest path, living record, as it should be, this matters because. Use them once when they land. Cut the second version.

Reduce parentheticals. Convert nested parentheticals into sentences. Reduce long compound sentences. Break them.

## Banned style moves

Do not write like a press release, a grant application, or an academic abstract.

Do not overuse "we discovered" or "we built."

Do not use moral language for technical failures.

Do not say "simply" when the work is not simple.

Do not call the non-engineer reader a "layman." Prefer "plain English" or "normal reader."

## Final self-check

- No em dashes or en dashes.
- No unnecessary all caps.
- Numbers preserved.
- Flags and code names preserved.
- Status conflicts resolved or flagged.
- Campaigns in chronological order.
- Every major claim has a receipt.
- No ABX overclaim.
- Tables used where numbers cluster.
- Roadmap not repeating the findings section.
- The reader knows what shipped, what failed, what is pending, and what to test next.
