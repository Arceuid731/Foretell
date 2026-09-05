Foretell 0.10.1 makes development capture automatic and bounded.

- Record accepted decision inputs, client priors and bounded world context automatically; the optional readable Replay Lab switch is no longer required for Analysis ZIP capture.
- Compress and seal small independent parts on a background worker. Bound the new cache to 64 MiB per territory session, 256 MiB total and 14 days; report oversized events, backlog losses and quota stops explicitly.
- Include the selected session capture in Analysis ZIP, including a safely sealed snapshot of an active session. Limit the ZIP to 128 MiB and disclose supplemental files omitted for size. Fix exclusion of readable recordings already closed with record off.
- Inspect and evaluate ZIPs progressively without retaining the whole recording. Verify part hashes and event counts, preserve missing-evidence abstention, and expose an --inspect summary command.
- Clarify English UI labels and show automatic capture status. Existing raw/readable retention settings, learned memory and exported ZIPs keep their existing policy.

Validated with detached real-engine capture/export regressions, core tests, telemetry contract and release build. Live UI/performance validation remains necessary. Semantic replay does not reconstruct rendered pixels, historical collision changes or a complete initial learned-memory checkpoint.

See docs/foretell-0.10.1.md for limits and analysis commands.
