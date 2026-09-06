# Foretell 0.13.3 — guide preparation and boss tracking

- Guide analysis now continues during combat by default. Enable **Pause analysis during combat** under Local AI if preferred.
- When pausing, keep completed bosses, responses and progress. Only an interrupted request needs to restart.
- Remember defeated enemies even while the guide is still preparing. The mechanic list follows the remaining bosses instead of returning to a boss already defeated.
- Show preparation status for bosses whose mechanics are not ready yet, rather than displaying an empty mechanic count.
- Display the observed spell name instead of a generic “WATCH” alert when no specific instruction is available. Prepared guide instructions still take priority.

Validated with automated source, combat-lifecycle, pause/resume and overlay tests. Live in-game acceptance remains to be checked.
