# Foretell 0.13.16 — recover incorrect guide citations

- Give analysis retries the exact incorrectly cited paragraph and candidate paragraphs containing the requested event name. This addresses the Palace of the Dead 31–40 failure where Scream and Shadow Flare cited "Level 59" instead of the adjacent combat paragraph. Citation and trigger validation remain mandatory.
- Continue each review from the latest corrected response instead of restarting from the original draft.
- Use a boss's visible wiki label when the source explicitly distinguishes it from a link title, such as `[Ixtab (Palace of the Dead)] Ixtab`.

After updating, use **Local AI → Analyze again** for the failed Palace guide. Its downloaded wiki source is already available.

Validated with a deterministic reproduction of the paragraph-number error, staged repair and prepared-cache checks, plus the runtime/core and publication checks. No new real-model analysis or in-game rendering validation is claimed. Details: `docs/review-palace-citations-2026-09-11.md`.
