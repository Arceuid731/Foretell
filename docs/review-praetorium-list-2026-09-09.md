# Praetorium boss list — 0.13.13

The September 9 guide capture confirms that the left-hand boss list could collapse to one mechanic. In the recorded Nero fight, `Spine Shatter` became `CurrentPhase`, and the presentation contained only that mechanic both during the cast and afterward. The saved adaptation assigned each ability its own phase: four for Mark II Magitek Colossus and seven for Nero. Its citations were ability descriptions, such as `Spine Shatter: Tankbuster.`, rather than phase headings.

Phase validation previously checked only that a label appeared in a cited source excerpt. The current-phase filter then correctly applied an incorrect phase plan. This was not caused by lost mechanics, failed detection, or the list's height budget.

The validator now requires a standalone source heading and a citation containing that heading. Existing cached guides discard an unsupported boss phase plan and its memberships on load, before live references are constructed. The complete mechanic catalogue and all advice remain intact. Valid documented phase plans keep their existing behavior. This check is conservative: prose-only phase descriptions do not enable phase filtering.

Validation:

- A regression failed on the old code when reading an ability-per-phase cache. It now passes, reuses the guide while analysis is paused without starting the model, and retains all three fixture reminders during successive casts and between casts; only the detected mechanic is active.
- New ability-as-phase output is rejected; existing documented heading, phase transition, LF/CRLF, cache integrity and instruction validation tests pass.
- Reading the actual saved Praetorium guide through the corrected cache and normal prepared-guide validator returns `valid=true`, 20 mechanics and zero phases, removing the 11 unsupported definitions without changing the saved file or rerunning inference.
- Runtime/core suites, native ImGui overlay checks, telemetry contract, noninteractive test-host checks and a local Release build pass.

The real capture establishes the original defect. Post-fix combat behavior is checked with the saved guide and detached regressions; live in-game visual confirmation remains outstanding. The normal reminder count, role ranking and layout limits are unchanged.
