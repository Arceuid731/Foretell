# Foretell engine

Foretell's adaptive layer lives in this directory.

It observes BossMod Reborn world-state events, learns mechanic geometry and encounter transitions locally, persists confidence-weighted memory, and renders advisory world/radar/text predictions.

For cast actions, Foretell now seeds learning from useful local client metadata before outcome evidence exists: Action `CastType`, `EffectRange`, `XAxisModifier`, `TargetArea`, `Omen`/VFX information and actor hitbox are treated as revocable priors. Empirical observations can confirm, refine or override those priors; metadata alone is capped below the 99% safe-guidance gate.

World overlay and radar geometry use the same confidence visualization: cyan/blue for early visual hypotheses, yellow for learned, orange for high-confidence warning-grade predictions and red for safe-guidance-grade danger. Color represents confidence, not damage severity. Circle, donut, cone, rectangle and cross geometries are rendered on both surfaces.

Foretell intentionally does not automate player movement. Low-confidence predictions are suppressed by configurable thresholds, and safe-position suggestions are advisory only.

## Data Fabric completeness contract

Foretell ingests generic structured evidence available through BMR WorldState, Dalamud runtime gameplay services, actor/target state, raw event payloads, and relevant Lumina rows. Reflection discovers scalar/enum/text fields recursively and feeds them through a stable hashed feature space; ActionEffect/status/map/director payload details are retained in replay instead of discarded.

The one deliberate boundary is encounter-authored knowledge: BossModule implementations, state machines, encounter components/layouts/presets and equivalent hand-written answers are excluded. Foretell learns from the raw game data instead. The in-game coverage audit reports discovered, ingested, learner-used, explicitly excluded and unaccounted fields; unaccounted data is treated as a defect rather than silently ignored.

The Data Fabric contract is enforced at runtime and visible in the Inspector coverage counters.

### Raw event hierarchy

Foretell does not require ACT or IINACT for encounter telemetry. The inherited BMR sync layer already hooks/decodes native FFXIV client and network surfaces (casts, ActionEffect/EffectResult, ActorControl, statuses, map/director events, system logs, timelines, etc.). Foretell additionally consumes every non-frame `WorldState.Operation`, an unconditional lossless server/client IPC tap, and a direct EventObject animation/object-effect surface. Full binary payloads are retained in Foretell replay and every byte contributes to the compressed hashed learner feature space.

BMR/Splatoon encounter-authored answers remain forbidden. Generic BMR primitives and algorithms (geometry, AOE mathematics, arena/pathfinding/constraint utilities, packet decoders and raw sensors) are allowed because they are encounter-agnostic machinery rather than manually authored mechanic knowledge.

This raw-telemetry contract is build-validated in CI before release and is the acceptance boundary for Data Fabric changes.

ActionEffect handling is typed rather than reflection-only: all valid target effects retain Type, Param0..4, Value, derived damage/element fields and the exact original 8-byte effect record. Raw ActorControl retains command, p1..p8, target and replay flag; SystemLog retains every argument without the generic collection sampling cap.
