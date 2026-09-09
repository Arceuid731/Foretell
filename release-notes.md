# Foretell 0.13.13 — keep boss mechanics visible

- Fix the boss list collapsing to a single mechanic when an attack was incorrectly treated as a combat phase.
- Require a cited, standalone source heading before accepting a phase. Detecting an ordinary attack highlights its row while the other boss reminders remain available.
- Apply the correction when loading saved guides, preserving their mechanics and instructions without another AI analysis. Documented combat phases retain their filtering.

Validated against the saved Praetorium guide, repeated-cast and cache regressions, native ImGui checks, runtime/core suites and a Release build.
