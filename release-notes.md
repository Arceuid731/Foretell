Foretell 0.10.2 improves radar stability, combat framing and simultaneous attack collection.

- Keep terrain opacity stable during normal refreshes and remove transparency seams inside the filled map. Published missing-floor changes still replace the map immediately.
- Use a square Auto viewport, fit compact observed rooms or boss combat space, and smooth zoom and panning. The open-world radius no longer forces small boss arenas to zoom out; rectangular corners remain visible.
- Recover still-active enemy casts missed during callback budget pressure, without duplicate predictions or backdating. Apply the shared radar/world display limit to simultaneous attack groups, preserving their individual footprints within a separate 64-shape bound.
- Exclude Lumina backing Excel pages before reflection and cache bounded static row features, addressing oversized actor observations and unnecessary ingestion work.
- Stop turning unmarked circle metadata into unsupported AVOID instructions. Keep ambiguous casts as WATCH; recognize explicit knockback Omen names without inventing a landing point. Migrate affected old derived instructions while retaining observation counts and unrelated knowledge.
- Remove the camera caption and clarify the confidence legend: Learning / Confident / Very high. Include the last update exception and recovered-cast count in analysis diagnostics. Automatic capture remains enabled without extra Replay settings.

Validated with real-engine simultaneous-cast and guidance regressions, radar/core tests, automatic capture/export tests, the telemetry contract and a Release build. Live visual/performance checks remain necessary. The inspected Praetorium capture is partial, so the changes do not claim complete encounter coverage or the same first-occurrence anticipation as authored BMR modules.

See docs/review-praetorium-2026-09-05.md for findings, limits and the next test.
