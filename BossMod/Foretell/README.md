# Foretell engine

Foretell's adaptive layer lives in this directory.

It observes BossMod Reborn world-state events, learns mechanic geometry and encounter transitions locally, persists confidence-weighted memory, and renders advisory world/radar/text predictions.

For cast actions, Foretell now seeds learning from useful local client metadata before outcome evidence exists: Action `CastType`, `EffectRange`, `XAxisModifier`, `TargetArea`, `Omen`/VFX information and actor hitbox. Complete ordinary telegraph shapes remain authoritative for their Action row, while empirical observations validate them and learn ambiguous families; ambient statuses or player movement cannot silently replace a known shape. Metadata alone is capped below the 99% safe-guidance gate. Cone families without angles, large non-targeted circles, and the family shared by meteor, proximity, raidwide and line-of-sight actions remain non-spatial until evidence disambiguates them. The gaze VFX produces `LOOK AWAY` without an invented danger circle. A long observed cast whose metadata cannot prove a complete spatial shape is still shown as a text-only `WATCH` entry, without invented world/radar geometry or safe-position guidance.

Target icons attached by the game to player actors are normalized as encounter signals whose target is that player, rather than being discarded as player actions. A newly seen symbol is rendered as `MARKER` without inventing stack/spread/avoid semantics; repeated party positions and effects can subsequently disambiguate its behavior. Dynamic line casts with complete client-data width and live endpoints are immediately display-grade, but remain below safe-guidance confidence until outcomes validate them.

World overlay and radar geometry use the same confidence visualization: cyan/blue for early visual hypotheses, yellow for learned, orange for high-confidence warning-grade predictions and red for safe-guidance-grade danger. Color represents confidence, not damage severity. Circle, donut, cone, rectangle and cross geometries are rendered on both surfaces.

Foretell intentionally does not automate player movement. Low-confidence predictions are suppressed by configurable thresholds, and safe-position suggestions are advisory only.

## Local collision mesh

Auto radar mode builds a bounded walkable surface directly from the collision scene already loaded by FFXIV, with no vnavmesh runtime dependency and no authored territory data. Foretell copies nearby PCB and primitive-collider triangles on the framework thread under the native scene's nonblocking shared lock. Layer, visibility and effective material filters select movement collision. An immutable managed snapshot then crosses to a worker, which selects upward floor candidates with headroom, intersects obstacles at the relevant height and commits a reachable layer per cell. This preserves arch openings and avoids independently projecting incompatible floors. It remains a local 2.5D approximation without Detour pathfinding or movement control.

The rolling window is world aligned and preserves its requested radius across refreshes. Complete grids swap atomically, while retained stale geometry is marked and dimmed; it cannot authorize a safe-direction suggestion. Stitched contours and internal blocked edges represent walls and drops, including obstacles that can be walked around. Box, cylinder, sphere and plane primitives supplement PCB geometry. Scene fingerprints, periodic refreshes and structural events request replacement even when stationary. The reachability service checks the full segment of a proposed direction, and world overlays use floor heights. Walking reachability never suppresses an attack outline. A combat radial sweep supplements arena barriers; raycasting remains a compatibility fallback. Complete disappearance of the reachable floor, overlapping connected storeys and narrow passages remain cases requiring live validation.

Analysis ZIP for a live selected session can include the latest completed managed collision snapshot. `ForetellCoreTests --collision <analysis.zip>` rebuilds that raster without a game process. See [the full review](../../docs/review-2026-09-04.md) for current evidence, reproduction commands and remaining architectural work.

## Data Fabric completeness contract

Foretell ingests generic structured evidence available through BMR WorldState, Dalamud runtime gameplay services, actor/target state, raw event payloads, native FFXIVClientStructs memory, and relevant Lumina rows. Reflection discovers scalar/enum/text/binary fields recursively and feeds them through a stable hashed feature space; ActionEffect/status/map/director payload details are retained in replay instead of discarded.

The one deliberate boundary is encounter-authored knowledge: BossModule implementations, state machines, encounter components/layouts/presets and equivalent hand-written answers are excluded. Foretell learns from the raw game data instead. The in-game coverage audit reports discovered, ingested, learner-used, explicitly excluded and unaccounted fields; unaccounted data is treated as a defect rather than silently ignored.

The Data Fabric contract is enforced both in CI and at runtime and is visible in the Inspector coverage counters. Collections are traversed completely within a per-root recursion safety budget: party, client, network, Deep Dungeon, waymarks, Dalamud services and individual actors cannot starve one another. Budget exhaustion becomes an explicit unaccounted capability.

### Raw event hierarchy

Foretell does not require ACT or IINACT for encounter telemetry. The inherited BMR sync layer hooks/decodes native FFXIV client and network surfaces (casts, ActionEffect/EffectResult, ActorControl, statuses, map/director events, system logs, timelines, etc.). Foretell additionally observes non-frame `WorldState.Operation`, raw server/client IPC, EventObject animation/object effects, and native actor/static VFX lifecycles with bounded path copies. Raw journal retention and semantic processing have separate budgets: retaining bytes does not imply that every event reached the learner. Typed damage effects supply hit evidence; target VFX alone do not. Episodes overlapping rejected outcome evidence abstain from training and validation. The dashboard's drop/failure counters are part of interpreting any analysis.

Native character snapshots preserve movement state, soft target, both tether slots with progress, voice ID, animation timeline arrays and speeds, model/skeleton state, and transformation state/timers. Separate environment snapshots preserve time, weather and transition progress; camera snapshots preserve origin, viewport, view/projection matrices and projection parameters. Duty lifecycle, fly-text/toast payloads and structured native system LogMessage events supply additional redundant evidence. Private player chat is deliberately not captured, and process pointer addresses are excluded because they are unstable layout noise rather than gameplay facts.

Every Dalamud `IDalamudService` interface is runtime-audited: gameplay services must identify their ingestion route, non-gameplay services must carry an explicit reason, and unknown future services become unaccounted until reviewed.

BMR/Splatoon encounter-authored answers remain forbidden. Generic BMR primitives and algorithms (geometry, AOE mathematics, arena/pathfinding/constraint utilities, packet decoders and raw sensors) are allowed because they are encounter-agnostic machinery rather than manually authored mechanic knowledge.

This raw-telemetry contract is build-validated in CI before release and is the acceptance boundary for Data Fabric changes.

ActionEffect handling is typed rather than reflection-only: all valid target effects retain Type, Param0..4, Value, derived damage/element fields and the exact original 8-byte effect record. Raw ActorControl retains command, p1..p8, target and replay flag; SystemLog retains every argument without the generic collection sampling cap.

EffectResult is consumed as a first-class semantic stream and correlated back to the originating ActionEffect by the native global action sequence; the generic WorldOperation copy is explicitly de-duplicated. This gives downstream hit/status confirmation an exact causal edge instead of relying only on time proximity.
