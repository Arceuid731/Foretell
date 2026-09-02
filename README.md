# Foretell

Foretell is an experimental adaptive encounter-intelligence plugin for FFXIV, built on BossMod Reborn.

It keeps the mature BMR world-state/rendering stack while adding local multi-signal observation, mechanic inference, confidence/ambiguity tracking, persistent contextual encounter memory, timeline and phase learning, Replay Lab diagnostics, native arena topology, and predictive world/radar/text guidance.

For cast actions, Foretell also consumes useful local FFXIV client metadata as an immediate prior: `CastType`, `EffectRange`, `XAxisModifier`, `TargetArea`, `Omen`/VFX information and actor hitbox. These priors can make ordinary telegraphs useful from the first cast, but they are never treated as unquestionable ground truth: observed outcomes can confirm, refine or override them, and metadata alone cannot reach the 99% safe-guidance threshold.

## Dalamud custom repository

`https://raw.githubusercontent.com/Arceuid731/Foretell/main/repo.json`

## Quick start

1. Install Foretell from the custom repository and disable a separate BossMod Reborn installation while testing; Foretell already contains the BMR stack.
2. Run `/foretell` to open the Foretell cockpit.
3. Start in **Observe** on familiar content. Foretell learns silently while BMR remains your reference.
4. After a few pulls/runs, switch to **Compare** and review **Learned mechanics**.
5. Move to **Hybrid** when the learned results match the fight. Use pure **Foretell** only when you intentionally want to hide legacy BMR encounter presentation.

The dedicated in-game cockpit provides Dashboard, Knowledge explorer, Timeline, Live feed, Replay & storage, Settings and Help tabs. The Knowledge explorer is organized as content category → territory/duty → arena/environment/source → mechanic, with confirmed deletion at every useful level.

## Confidence visualization

Foretell uses the same confidence encoding on the world overlay and mini radar. The actual inferred circle, donut, cone, rectangle or cross is drawn with a color that represents **reliability**, not damage severity:

- cyan/blue — early visual hypothesis
- yellow — learned
- orange — high-confidence warning-grade inference
- red — safe-guidance-grade danger (at the configured strict threshold)

The radar also prints confidence percentages. Safe-position suggestions remain advisory and are only eligible at the strict safe-confidence gate.

## Foretell modes

- **Legacy** — BMR presentation only; Foretell guidance is hidden.
- **Observe** — recommended starting point; Foretell learns silently.
- **Compare** — BMR and Foretell are displayed together for validation.
- **Hybrid** — adaptive Foretell guidance with BMR retained as a reference/safety net.
- **Foretell** — pure adaptive presentation; legacy BMR encounter hints are hidden.

## Inspector and commands

`/foretell` opens the Foretell cockpit. Useful commands include:

- `/foretell inspect` / `stats` / `debug`
- `/foretell mode observe|compare|hybrid|foretell|legacy`
- `/foretell learning on|off`
- `/foretell record on|off`
- `/foretell replay`
- `/foretell export`
- `/foretell save`
- `/foretell help`

`/bmr` remains available for the inherited full BMR/settings UI.

## Replay Lab

Foretell can record a compact normalized event stream locally. Independently of that optional setting, it always writes exact compressed raw journals for server IPC, client IPC and ActorControl. Replay Lab re-injects normalized observations and every raw journal overlapping the recorded session through the same learner in an isolated temporary store. It reports what was rediscovered, ambiguous or rejected, then restores the live learned memory. It is an inference replay, not a video or 3D recreation of FFXIV.

This makes recorded pulls reusable as a regression corpus while the inference engine evolves.

## Safety and privacy

Foretell is advisory. It does not add autonomous character movement. Low-confidence hypotheses are suppressed from stronger guidance, and safe-position suggestions use the deliberately strict **Never Guess Lethal** confidence threshold.

Learned memory, normalized/raw-binary replay streams, and diagnostics stay local; Foretell does not require a remote inference API. Foretell records structured native system messages but deliberately excludes private player chat and process pointer addresses.

## Zero-knowledge telemetry boundary

Foretell starts without authored encounter answers. It learns from:

- exact server/client IPC payloads and complete `ActorControl` parameters, retained in rotating compressed journals and summarized into bounded 250 ms learning windows;
- complete `ActionEffect` bytes and sequence-linked `EffectResult` confirmations;
- typed WorldState deltas: actors, movement, casts, statuses, targets, enmity, party/alliance, cooldowns, gauges, waymarks, map/director events, tethers, icons, timelines and Deep Dungeon state;
- bounded native `Character` snapshots: both tether slots and progress, animation timeline, model, transformation, target/mode and VFX container state;
- native actor/static VFX paths and lifecycles plus object effects, copied into primitive queues before deferred processing;
- native weather/time/transition state, camera matrices/viewport and a budgeted collision sweep used to learn reachable arena contours;
- explicitly classified Dalamud gameplay services, Lumina metadata and observed outcomes.

Continuous state is sampled with change detection and rotating actor slices; unique events remain exact. Heavy compression and normalized replay serialization run off the game thread, while learned-memory autosave is deferred until combat is inactive. Every framework-frame drain has a count and time budget, persistent learning collections have pressure limits, and the Dashboard exposes backlog, rejection, eviction, failure and timing counters. A degraded sensor is reported instead of silently presented as complete.

It must not import BossModule mechanics, state machines, boss components, encounter layouts/presets or equivalent hand-authored safe spots and phase answers. CI checks this boundary and the in-game coverage audit reports any sensor that is truncated, unavailable or not explicitly classified.

The radar can be unlocked and dragged, resized, zoomed in world yalms, or forced to a circle/square. In Auto mode it uses the learned native collision topology once a complete sweep is available and temporarily falls back to a circle while scanning.

## Upstream

Based on FFXIV-CombatReborn/BossmodReborn. Upstream license and attribution are retained in this repository.
