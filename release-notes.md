# Foretell 0.12.3 — accept narrative boss guides

- Fix the guide preparation failure reproduced on Kugane Castle in 0.12.2: valid boss sections written entirely as paragraphs were rejected because no named mechanics were extracted.
- Accept and cache narrative-only boss documents generically. Preserve each boss's source text, conditions, revision and optional local summary. Keep rejecting empty/non-boss pages and mismatched duty variants.
- Clearly label narrative guides in the entry panel and transparent overlay. Advice is available by hovering the current/upcoming boss; prose mentions do not invent named mechanics, cast/status matches, central alerts or geometry.
- Preserve specific bounded parser failure details instead of displaying only InvalidDataException; wrap diagnostics and the entry-panel explanation, and add a retry button for failed downloads/refreshes.
- Preserve the 0.12.2 session-bound guide analysis exports, compact overlay, existing model/cache and BMR modes. No encounter-specific runtime list or new model download is introduced.

Validation: synthetic multi-boss narrative parsing, preserved conditions and source isolation, no invented cast association, upcoming-boss tracking, empty/wrong-variant rejection, cache/restart/offline fallback, detailed failures and narrative-only summary/cache tests. Runtime/core tests, telemetry contract, native ImGui smoke and Release build pass. Live wiki probes: Kugane Castle revision 1488386 loads three narrative boss sections; Sastasha revision 1489668 still loads five named mechanics.

This fixes guide availability, not full live mechanic coverage. Narrative-to-ability extraction, the previously reported Arboretum paragraph fusion/condition gating and model selection remain separate work. Details: `docs/foretell-0.12-guides.md`.
