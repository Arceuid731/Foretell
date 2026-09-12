# Foretell 0.13.18 — Dragon's Neck guide validation

- Fix guide preparation failing when the source explicitly distinguishes ability names by capitalization, as with `Fungah` and `FUNGAH` in the Dragon's Neck.
- Preserve both entries only when each spelling has its own exact-case source label in its cited evidence. Repeated entries and unsupported capitalization changes still fail validation, with the affected ability now named in the diagnostic.
- Keep existing ambiguity checks for live alerts. This fixes the preparation error; it does not establish the accuracy of generated advice or resolve ambiguous boss/action identities.

Validated by replaying the player's recorded first draft without new inference: all 15 mechanics pass structural and citation validation and survive cache validation. Regression tests also cover real duplicates, fabricated case changes, wrong citations and ambiguous live events. See [the diagnosis](https://github.com/Arceuid731/Foretell/blob/main/docs/dragons-neck-2026-09-12.md).
