# Foretell

Foretell is an experimental adaptive encounter-intelligence plugin for FFXIV, built on BossMod Reborn.

It keeps the mature BMR world-state/rendering stack while adding local multi-signal observation, causal and structural protocol learning, calibrated forecast validation, persistent contextual encounter memory, timeline/phase/composite prediction, Replay Lab diagnostics, native arena topology, and predictive world/radar/text guidance.

For cast actions, Foretell also consumes useful local FFXIV client metadata as an immediate prior: `CastType`, `EffectRange`, `XAxisModifier`, `TargetArea`, `Omen`/VFX information and actor hitbox. Complete ordinary telegraph shapes from those fields remain authoritative for their Action row, while observed outcomes validate them and learn the ambiguous families; unrelated movement or status traffic cannot rewrite a known rectangle/circle into a different mechanic. Metadata alone still cannot reach the 99% safe-guidance threshold. If metadata identifies a cone but provides no angle, Foretell retains the cone family and range for learning without drawing an invented sector. The gaze VFX becomes a text `LOOK AWAY` instruction instead of a fake circle, while large non-targeted circle families and the family shared by meteor, proximity, raidwide and line-of-sight actions stay non-spatial until outcomes disambiguate them. Long observed casts with no provable spatial shape remain visible as text-only `WATCH` entries.

## Dalamud custom repository

`https://raw.githubusercontent.com/Arceuid731/Foretell/main/repo.json`

## Quick start

1. Install Foretell from the custom repository and disable a separate BossMod Reborn installation while testing; Foretell already contains the BMR stack.
2. Run `/foretell` to open the Foretell cockpit.
3. Start in **Observe** on familiar content. Foretell learns silently while BMR remains your reference.
4. After a few pulls/runs, switch to **Hybrid** to display complete BMR and Foretell guidance together, then review **Learned mechanics**.
5. Use pure **Foretell** only when you intentionally want to hide legacy BMR encounter presentation.

The dedicated in-game cockpit provides Dashboard, Knowledge explorer, Timeline, Live feed, Replay & storage, Settings and Help tabs. The Knowledge explorer is organized as content category → territory/duty → arena/environment/source → mechanic, with confirmed deletion at every useful level. It also exposes learned causal links, raw protocol families, phase transitions and simultaneous patterns individually.

After leaving a duty, expand that content in **Knowledge explorer** and click **Analysis ZIP**. Foretell creates one shareable archive containing the matching sealed raw journal(s), the full cumulative learned encounter snapshot, configuration and health counters, plus a bounded decision audit for the latest completed territory session from accepted trigger through proposed prediction, classification and verification/expiry. The archive records the selected session's plugin version separately from the newer plugin version that may have exported it. The optional readable JSONL replay is included when it was enabled and is safely closed; it is not required for the bundle to contain the authoritative raw input and semantic decisions.

## Confidence visualization

Foretell separates evidence confidence from verified guidance reliability. A candidate can therefore remain inspectable without being allowed into warning or safe-position gates. After forecasts resolve, a conservative lower confidence bound and prediction error update the guidance score. The actual inferred circle, donut, cone, rectangle or cross is drawn with a color that represents **verified reliability**, not damage severity:

- cyan/blue — early visual hypothesis
- yellow — learned
- orange — high-confidence warning-grade inference
- red — safe-guidance-grade danger (at the configured strict threshold)

The radar also prints confidence percentages. Target-relative geometry is withheld from anticipated spatial drawing until repeated offsets are stable, and incomplete geometry is likewise kept out of the world and radar overlays. Safe-position suggestions remain advisory and are only eligible at the strict safe-confidence gate.

## Prediction pipeline

Every eligible encounter signal—not only cast bars—can become a learned trigger: casts, icons, tethers, statuses, action timelines, event objects, map/director state, ActorControl, native VFX paths and NPC calls. Outcomes such as ActionEffect, sequence-linked EffectResult, displacement, statuses and deaths validate or reject the resulting mechanic hypothesis.

Repeated signal transitions produce branch-aware timeline forecasts. Ambiguous branches cause Foretell to abstain. In parallel, every mechanic occurrence learns its offset from the current phase clock and—only for independently detected boss arenas—the boss HP ratio at which it appeared. Cross-pull variance decides whether elapsed time or HP is the more credible trigger; a stable clock wins ties so similar group DPS cannot manufacture a false HP gate. HP forecasts estimate threshold arrival from the observed health-loss slope, while occurrence indexing keeps repeated cycles distinct.

Stable simultaneous patterns can forecast their other components, while learned causal links improve assignment of later effects to the correct trigger. Each issued mechanic, sequence, phase-clock, HP-threshold and composite forecast records hits and misses so reliability is measured rather than inferred from repetition alone.

## Foretell modes

- **Legacy** — BMR presentation only; Foretell guidance is hidden.
- **Observe** — recommended starting point; Foretell learns silently.
- **Hybrid** — complete BMR and Foretell presentations displayed together for validation.
- **Foretell** — pure adaptive presentation; legacy BMR encounter hints are hidden.

## Inspector and commands

`/foretell` opens the Foretell cockpit. Useful commands include:

- `/foretell inspect` / `stats` / `debug`
- `/foretell mode observe|hybrid|foretell|legacy`
- `/foretell learning on|off`
- `/foretell record on|off`
- `/foretell replay`
- `/foretell export`
- `/foretell save`
- `/foretell help`

`/bmr` remains available for the inherited full BMR/settings UI.

## Replay Lab

Foretell can record a compact normalized event stream locally. Independently of that optional setting, it always writes exact compressed raw journals for server IPC, client IPC and ActorControl. The online raw learner derives bounded per-opcode length, byte-stability, sequence-hash and transition features while retaining the original bytes. Replay Lab re-injects normalized observations and every raw journal overlapping the recorded session through the same learner in an isolated temporary store. It reports what was rediscovered, ambiguous or rejected, then restores the live learned memory. It is an inference replay, not a video or 3D recreation of FFXIV.

The storage page can delete individual inactive recordings or apply a retention/quota cleanup. Automatic cleanup is opt-in, runs outside combat on a background worker, and never deletes the active journal or learned memory.

This makes recorded pulls reusable as a regression corpus while the inference engine evolves.

The standalone `.ftraw.gz` is intentionally only the exact transport/ActorControl layer. It is excellent for protocol reconstruction and traffic/load analysis, but it does not by itself say what Foretell classified or chose to present. Use **Analysis ZIP** for future post-run reports instead of manually gathering raw, knowledge and diagnostics files.

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
- native weather/time/transition state, camera matrices/viewport and a budgeted local collision rasterizer used to reconstruct the walkable surface around the player;
- explicitly classified Dalamud gameplay services, Lumina metadata and observed outcomes.

Continuous state is sampled with change detection and rotating actor slices; unique events remain exact. Heavy compression and normalized replay serialization run off the game thread, while learned-memory autosave is deferred until combat is inactive. Every framework-frame drain has a count and time budget, with a small reserve for sparse enemy casts and resolutions during high-volume alliance-raid frames. Persistent learning collections have pressure limits, and the Dashboard exposes backlog, rejection, eviction, failure and timing counters. A degraded sensor is reported instead of silently presented as complete.

It must not import BossModule mechanics, state machines, boss components, encounter layouts/presets or equivalent hand-authored safe spots and phase answers. CI checks this boundary and the in-game coverage audit reports any sensor that is truncated, unavailable or not explicitly classified.

The radar can be unlocked and dragged, resized independently in pixels, zoomed in world yalms, or forced to a circle/square. In Auto mode Foretell continuously samples nearby collision floors and the traversability of each connection, keeps only the component reachable from the player, and redraws it progressively inside the selected visible radius. The grid is player-centred and resolution-adaptive, so moving through a duty or the open world never accumulates into a full-map reveal. Combat transitions and structural map/object signals invalidate the connections and rescan them; a collision barrier that closes at pull start therefore cuts the generic local surface without a boss or territory rule. A separate radial sweep remains a fast enclosed-arena fallback, and partial wall fans are never persisted as arena boundaries.

## Upstream

Based on FFXIV-CombatReborn/BossmodReborn. Upstream license and attribution are retained in this repository.
