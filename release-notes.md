# Foretell 0.13.14 — complete boss lists, weekly cache and clearer alerts

- Keep every mechanic for the current boss/phase in the left list, in source order. Remove the six-reminder limit and role/actionability exclusions. Scroll with the mouse wheel, including while locked; a newly active mechanic is brought into view automatically.
- Accept an unambiguous observed attack even when it belongs to several possible later phases. This fixes the recorded Wash Away rejection in Alexander - The Arm of the Father; uncertain phase state returns to the complete boss list.
- Reuse valid source caches for seven days, including after restarting the plugin. Entering recent content no longer downloads sources again; compatible prepared guides load without AI analysis. Manual refresh remains available.
- Add outlined central text, warning icons on both sides, a configurable dark background and an orange default style. Apply **Display → Central alerts → High contrast style** to existing color settings.

Validated with the Arm of the Father 0.13.12 export and saved guide, source-cache and phase regressions, native ImGui scrolling/rendering checks, and runtime/core suites. Missing source-to-event associations such as the +/- mechanic remain separate from list visibility; see `docs/review-arm-of-the-father-2026-09-09.md`.
