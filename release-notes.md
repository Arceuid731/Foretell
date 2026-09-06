# Foretell 0.12.1 — compact combat checklist

- Replace the document-style checklist with a transparent, borderless overlay: boss heading, then one line per mechanic with its short response. Active mechanics highlight with a countdown. Unlock temporarily restores drag/resize controls; position, width, maximum height, scale and colors persist.
- Move descriptions, conditions, translated summaries and preparation details into hover tooltips. Hold Shift over a mechanic for full English source. The inspector still offers the full guide. Ordinary boss lists fit without scrolling; unusually long lists use pages and active signals bring their page into view.
- Use the same concise response for the highlighted row and central alert, with independent central text scaling. Unconfirmed targets/geometry and conditional responses remain watch/check cues rather than invented orders.
- Keep the instance-entry panel open until dismissed. It hides temporarily in combat and can be reopened from Guides or by clicking the boss heading.
- Recognize generic cone-attack and standalone party-wide damage phrasing without encounter-specific IDs or exceptions. Existing boss isolation, guide caches, 2D/3D geometry and BMR modes remain unchanged.

Validation: detached runtime and core tests, telemetry contract, Release build, plus a native ImGui headless smoke test for compact row geometry, transparent/locked flags, drag/resize flags and tooltip hover. In-game visual acceptance remains to be checked after updating. Guide coverage and unresolved conditions are not made universal by this UI change.

Validation and limits: `docs/foretell-0.12-guides.md`.
