# Guide event bindings — 0.13.9

## Behavior

Full-source model analysis can now return multiple independently cited `GuideTrigger` entries per mechanic. Each contains an exact original cast/status name, an event-specific cue, evidence, target restriction and optional minimum status-stack threshold. Existing response/cached formats remain valid with an empty trigger array; no forced reanalysis occurs on entry. Reanalyzing a saved guide is required to extract its new triggers.

The live path resolves English sheet names for observed spell casts and party statuses against the current, engaged, unambiguous boss. Owned helpers and same-boss-name unowned helpers are eligible; unrelated actors are rejected. A target-sensitive instruction requires a known party target. Status stacks use the low byte of `Extra`; other source conditions stay in the instruction. Conflicts, ambiguous matches and unknown names do not become automatic tips.

The selected event cue feeds both the highlighted list row and central alert. Central de-duplication preserves distinct responses while combining repeated helper occurrences. Spatial footprints still come from the existing detection path, not from the trigger text. Priority remains personal danger first, then guide advice before generic text within that priority group.

Phase restrictions apply when known. A uniquely identified event with one documented phase membership can establish the next phase; this prevents the previous phase filter from permanently hiding the transition event. This does not infer transitions from HP alone.

## Observed ID memory

`foretell-guide-bindings.json` stores up to 1,024 observed ID/name associations, within a 2 MiB file limit, with asynchronous loading and debounced atomic persistence. Scope includes duty, source hash, model revision, analysis language and the complete prepared boss. Reanalysis that changes the boss invalidates the scope without trusting previous interpretations.

An ID can recover an unavailable sheet name only when scope, event kind, source OID and source name ID agree and the recorded name is unique. The recovered name must still pass current trigger, phase, target and stack checks. Observation counts are not statistical confidence and never promote a fuzzy match. This iteration does not infer unknown abilities from visual similarity or fix every incorrectly generated source interpretation.

## Analysis ZIP evidence

The existing export action includes:

- prepared advice and per-trigger citations in the adapted guide;
- `BindingAudits` in guide timeline frames: timestamp/sequence, boss/phase, actor identity, action/status ID and name, recipient/stacks, result, candidates, chosen instruction and binding key;
- signal `Trigger` and `BindingKey`, linking a selected instruction to its match;
- presentation frames showing submitted, clipped, hidden or transparent rows/alerts;
- memory readiness/errors and explicit counters for omitted binding evidence.

Audits are change-driven, not emitted every frame. The pending queue is bounded to 64, with eight drained per sample. Long-lived unchanged statuses do not generate continuous duplicates. Raw-event quota exhaustion does not starve the separate guide timeline budget. If the guide frame, timeline or pending queue fills, the export reports incomplete evidence rather than claiming full coverage. Submitted draw data is not proof of visible pixels.

The detached diagnostic command `--guide-binding-report <analysis.zip> <output-directory>` produces `report.json` from these audits and presentation frames. Old ZIPs without binding audits are explicitly reported as unavailable; they cannot establish whether an unrecorded match occurred. The command does not query installed guide caches, run inference or contact a server.

## Validation limits

Detached synthetic tests cover source grounding, multi-event cues, targets/stacks, ambiguity, phase restrictions, ownership, persistence, bounded capture and report parsing. These checks establish pipeline behavior, not Qwen output quality or in-game coverage on an unseen encounter. The next complete run/export is needed to measure actual unmatched events and guide-versus-generic presentation.
