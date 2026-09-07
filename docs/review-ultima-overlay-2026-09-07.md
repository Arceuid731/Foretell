# Ultima guide and overlay review — 7 September 2026

## Observed session

The Porta Decumana (content 830, territory 1048), Foretell 0.13.4. The player's screenshots and the still-running capture were inspected read-only; the installed guide and analysis caches were not modified. The source is the current Console Games Wiki page, not the obsolete Praetorium encounter. Historical references in its introduction do not establish source contamination.

The recorded analysis ran from 17:00:56 to 17:03:16 UTC. Its three completed requests used 15.14 s for the outline, 57.08 s for the draft and 61.99 s for review. Draft and review each generated about 5,600 tokens. There was no validation retry or combat pause in this analysis. The outline dropped enclosing phase labels; invalid phase metadata subsequently disabled filtering. The 23 resulting entries therefore occupied the full overlay. Routine cinematic instructions also consumed space.

The recorded Radiant Plume control action (28982) precedes helper action 28983 by roughly 0.8 s. Repeated helpers produce separate, legitimate spatial footprints, but the central renderer displayed them separately too. Its guide and generic alert budgets were independent. The helper actors retain the current boss's exact BNpcName identity, but have no explicit OwnerID, so their instructions previously fell back to generic geometry text after the control cast.

## Changes

- A focused combat list keeps up to six relevant reminders and puts active mechanics first. Requested text size is preserved. Inactive reminders yield space when the selected height is insufficient; there is no paging or automatic scrolling. The full catalogue stays in Sources and guides. Active mechanics are retained beyond the reminder limit.
- Routine source-confirmed transitions are classified as context by the model and retained in the guide, not turned into player alerts. Existing caches receive a conservative presentation-only filter for routine wait/keep-attacking cues; conditional actions remain intact.
- Central notifications share a two-alert budget, prioritize immediate personal warnings and group simultaneous helper occurrences. Every selected world/radar footprint remains independent.
- Exact current-boss named helpers can reuse the matching guide mechanic without inventing ownership, targets or geometry. Unrelated names, explicit foreign ownership, ambiguous encounters and other cast occurrences are rejected.
- The initial model output passes through deterministic evidence, numeric, condition and trigger validation without a mandatory second full rewrite. If validation fails, the model corrects the existing draft. All source text is still presented to the model; mechanic evidence, alternatives and trigger checks remain mandatory. These checks do not prove every natural-language strategy correct.
- Outline selection uses original paragraph IDs rather than requiring the model to recopy text. Normal-sized guides remain complete in subsequent requests, including any heading omitted by the outline. A measured context overflow switches to the complete outline-selected passages and records that change explicitly.
- Invalid optional phase metadata triggers one small phase-only model request. It references existing mechanic IDs and original source heading IDs; it cannot rewrite instructions or invent new mechanics. The restored metadata passes the same source grounding and membership checks. A failed optional repair retains the validated mechanics without claiming known phases.

## Validation

The final real Qwen 3.5 4B/Vulkan preparation completed in **72.54 seconds**, including model startup, using the same 16,514-character cached source and 64K/12 GiB settings. It produced 26 catalogue mechanics with **two grounded phases** (14 in Phase 1, 12 in Phase 2). Request timings were 6.39 s for outline, 54.30 s for the draft and 5.50 s for phase-only repair, totaling 25,288 input and 4,859 output tokens. Prepared-cache round-trip validation passed. This was a game-idle test, not a claim of identical hardware load to the original combat recording or an in-game visual acceptance test.

Intermediate experiments are not release results: preserving headings only in the outline still allowed them to be omitted, and a mandatory sparse audit sometimes rewrote most of the reference anyway. The final path supplies full source context and does not invoke a second full model response after successful validation. No guides or models were downloaded for these tests, and no installed cache was replaced.

Detached runtime and native ImGui tests cover grouping, preserved spatial footprints, named helper identity, role selection, routine/conditional instructions, readable list layout, validation-first preparation, strict evidence, cache compatibility and documented phase transitions. Tests use the noninteractive host and do not open visible process windows.

Existing prepared guides are retained. Re-analyze an existing guide after updating to rebuild its phase metadata and newly classified context entries.
