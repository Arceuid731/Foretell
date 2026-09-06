# Dusk Vigil analysis review — 0.13.3

## Captured evidence

Input: `foretell-analysis-T1366-20260906-223542-517.zip`, plugin 0.13.2.0, duty C36/T1366, captured 2026-09-06 20:18–20:35 UTC.

- 89 guide snapshots record 14 transitions into `PausedInCombat`.
- The first prepared boss appears at 20:24:07 UTC: Towering Oliphant has six mechanics; Ser Yuhelmeric and Opinicus remain placeholders with no prepared phases. The final snapshot still reports analysis at 1/3, not completed analysis.
- The first boss was already defeated before that partial guide appeared. Live observation records its death, but the old guide tracker only tracked enemies after preparation. It therefore never marked this boss completed and selected it again after later boss deaths.
- Ser Yuhelmeric dies at 20:28:56.929868 UTC; Opinicus dies at 20:34:31.5786342 UTC. The old overlay falls back to Towering Oliphant after each.
- Opinicus also appears in non-combat vignettes with a different object ID. Their despawns must not be treated as boss completion.

## Sources

The archived sources include Console Games Wiki and Community Workbook. The workbook retains the `Third Boss` heading at I2 and the Whirlwind/rubble instruction at I3. The wiki includes five separate Opinicus abilities, including Whirling Gaol and Winds of Winter. Raven's catalogue fetch failed with HTTP 403; that source was unavailable to this analysis, although the website can have the corresponding guide. No absence of strategy in these two successfully fetched sources explains the empty final-boss list.

## Corrections

- `GuidePauseInCombat` defaults to false for both new and existing settings. Actual combat state remains separate from analysis pausing, including popup and demo behavior.
- Paused analysis retains completed responses and prepared-boss counts. A request canceled before returning its full response must run again; token-stream continuation is not implemented. Resumed work does not calculate an ETA from nearly instant memoized responses.
- Guide encounter progress records combat participants before preparation. Confirmed deaths are reconciled with uniquely named bosses when the outline/partial guide arrives. Source refreshes preserve this duty-local evidence; zone/duty changes clear it. Despawn, unrelated actor identity and non-combat vignette disappearance do not imply completion.
- Unprepared boss placeholders show analysis status. Generic unknown-action central alerts use the actual spell label; source-backed guide instructions retain priority.
- Analysis ZIP options now include whether combat pausing was enabled.

## Validation boundary

Regression tests reproduce late first-boss preparation, subsequent deaths, non-combat vignettes, source refresh, duty changes, wipe/reset and recycled IDs. Source fixtures retain the real workbook ordinal context and separate Opinicus abilities, exercising preparation and boss-specific matching with a stub model. Pause tests cover repeated interruptions, progress preservation and completed-response cancellation races. These tests do not certify the local model's reasoning or pixels rendered in the user's game; no real inference benchmark is run for this correction.
