# Foretell engine

Foretell's adaptive layer lives in this directory.

It observes BossMod Reborn world-state events, learns mechanic geometry and encounter transitions locally, persists confidence-weighted memory, and renders advisory world/radar/text predictions.

For cast actions, Foretell now seeds learning from useful local client metadata before outcome evidence exists: Action `CastType`, `EffectRange`, `XAxisModifier`, `TargetArea`, `Omen`/VFX information and actor hitbox. Complete ordinary telegraph shapes remain authoritative for their Action row, while empirical observations validate them and learn ambiguous families; ambient statuses or player movement cannot silently replace a known shape. Metadata alone is capped below the 99% safe-guidance gate. Cone families without angles, large non-targeted circles, and the family shared by meteor, proximity, raidwide and line-of-sight actions remain non-spatial until evidence disambiguates them. The gaze VFX produces `LOOK AWAY` without an invented danger circle. A long observed cast whose metadata cannot prove a complete spatial shape is still shown as a text-only `WATCH` entry, without invented world/radar geometry or safe-position guidance.

Target icons attached by the game to player actors are normalized as encounter signals whose target is that player, rather than being discarded as player actions. A newly seen symbol is rendered as `MARKER` without inventing stack/spread/avoid semantics; repeated party positions and effects can subsequently disambiguate its behavior. Dynamic line casts with complete client-data width and live endpoints are immediately display-grade, but remain below safe-guidance confidence until outcomes validate them.

World overlay and radar geometry use the same confidence visualization: cyan/blue for early visual hypotheses, yellow for learned, orange for high-confidence warning-grade predictions and red for safe-guidance-grade danger. Color represents confidence, not damage severity. Circle, donut, cone, rectangle and cross geometries are rendered on both surfaces.

Foretell intentionally does not automate player movement. Low-confidence predictions are suppressed by configurable thresholds, and safe-position suggestions are advisory only.

## Local collision mesh

Auto radar mode builds a bounded walkable surface directly from the live collision scene, with no vnavmesh dependency and no authored territory data. Vertical probes classify nearby floor samples; horizontal clearance probes classify the four connections around every sample; height limits and a flood from the player reject other floors, disconnected platforms and space behind closed collision barriers. Sampling is ordered from the player outward, runs in small watchdog-limited framework-thread slices, and sends only copied managed arrays to the background contour analysis.

The sampled window follows the player and is derived from the radar's visible-radius slider with a small scan margin. Rendering is clipped to that visible radius and does not retain previously visited parts of a raid as one global map. Both combat-condition transitions and observed structural effects invalidate connection evidence, allowing generic pull barriers and changing platforms to be recomputed while the fight is active. Collision sampling keeps running under the independent bounded topology watchdog even when optional semantic drains are adaptively throttled, and floor/barrier rays are interleaved so the useful surface grows promptly. A previous useful component remains visible while a progressive replacement catches up. Fast radial wall polygons are restricted to active combat with a credible boss candidate; corridors and courtyards are represented by the walkable floor mesh instead.

## Data Fabric completeness contract

Foretell ingests generic structured evidence available through BMR WorldState, Dalamud runtime gameplay services, actor/target state, raw event payloads, native FFXIVClientStructs memory, and relevant Lumina rows. Reflection discovers scalar/enum/text/binary fields recursively and feeds them through a stable hashed feature space; ActionEffect/status/map/director payload details are retained in replay instead of discarded.

The one deliberate boundary is encounter-authored knowledge: BossModule implementations, state machines, encounter components/layouts/presets and equivalent hand-written answers are excluded. Foretell learns from the raw game data instead. The in-game coverage audit reports discovered, ingested, learner-used, explicitly excluded and unaccounted fields; unaccounted data is treated as a defect rather than silently ignored.

The Data Fabric contract is enforced both in CI and at runtime and is visible in the Inspector coverage counters. Collections are traversed completely within a per-root recursion safety budget: party, client, network, Deep Dungeon, waymarks, Dalamud services and individual actors cannot starve one another. Budget exhaustion becomes an explicit unaccounted capability.

### Raw event hierarchy

Foretell does not require ACT or IINACT for encounter telemetry. The inherited BMR sync layer already hooks/decodes native FFXIV client and network surfaces (casts, ActionEffect/EffectResult, ActorControl, statuses, map/director events, system logs, timelines, etc.). Foretell additionally consumes every non-frame `WorldState.Operation`, an unconditional lossless server/client IPC tap, direct EventObject animation/object effects, and native actor/static VFX creation and destruction with complete paths. Full binary payloads are retained in Foretell replay and every byte contributes to the compressed hashed learner feature space.

Native character snapshots preserve movement state, soft target, both tether slots with progress, voice ID, animation timeline arrays and speeds, model/skeleton state, and transformation state/timers. Separate environment snapshots preserve time, weather and transition progress; camera snapshots preserve origin, viewport, view/projection matrices and projection parameters. Duty lifecycle, fly-text/toast payloads and structured native system LogMessage events supply additional redundant evidence. Private player chat is deliberately not captured, and process pointer addresses are excluded because they are unstable layout noise rather than gameplay facts.

Every Dalamud `IDalamudService` interface is runtime-audited: gameplay services must identify their ingestion route, non-gameplay services must carry an explicit reason, and unknown future services become unaccounted until reviewed.

BMR/Splatoon encounter-authored answers remain forbidden. Generic BMR primitives and algorithms (geometry, AOE mathematics, arena/pathfinding/constraint utilities, packet decoders and raw sensors) are allowed because they are encounter-agnostic machinery rather than manually authored mechanic knowledge.

This raw-telemetry contract is build-validated in CI before release and is the acceptance boundary for Data Fabric changes.

ActionEffect handling is typed rather than reflection-only: all valid target effects retain Type, Param0..4, Value, derived damage/element fields and the exact original 8-byte effect record. Raw ActorControl retains command, p1..p8, target and replay flag; SystemLog retains every argument without the generic collection sampling cap.

EffectResult is consumed as a first-class semantic stream and correlated back to the originating ActionEffect by the native global action sequence; the generic WorldOperation copy is explicitly de-duplicated. This gives downstream hit/status confirmation an exact causal edge instead of relying only on time proximity.
