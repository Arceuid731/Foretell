# Foretell 0.13.15 — Raven guides, Palace of the Dead and arena framing

- Negotiate HTTP/2 for guide downloads, falling back to HTTP/1.1 when required. Live checks retrieve Raven guides for Orbonne, Alexander A3 and Dusk Vigil; Gamer Escape also succeeds for Dusk Vigil, although some pages still return 403.
- Read Raven's public WordPress guide catalogue with bounded pagination, shared caching, preserved source text and role icons, and HTML fallback. Recognize optional leading articles and Alexander labels such as (A3), without removing difficulty variants.
- Resolve Palace of the Dead floor-set wiki titles without the extra leading "The" used by the game. Keep the current floor range and in-game identity intact.
- Fit a recognized boss room independently of party spread, including solo combat. Include room corners in circular radar views; retain the configured maximum radius and reject corridors or rooms outside the fight.

Existing recent guide caches remain valid for seven days. To add Raven immediately to an already prepared instance, use **Sources & guides → Refresh selected guide**.

Validated with live source acquisition, provider/cache/variant regressions, radar geometry tests, the complete runtime/core suites and the non-interactive test-host checks. Guide preparation with the local model and the radar's appearance in a new live run still need in-game validation. See `docs/review-sources-palace-radar-2026-09-11.md`.
