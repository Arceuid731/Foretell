# Official game ID resolution — 0.13.10

## Data and runtime path

Foretell loads Action, Status and BNpcName from installed Lumina game data in English, French, German and Japanese. No duty whitelist, external database download or model inference is involved. Loading and guide plan compilation run on background tasks. The catalogue fingerprint includes canonical rows, official aliases, action metadata and available game repository versions.

Each prepared guide compiles grounded trigger names into all matching official IDs. Typed triggers remain exclusive; cached guides can use the existing exact-name and quoted-heading grounding rules. Same-name rows are candidates, not a reason to select the first row. Boss names resolve through official BNpcName aliases. Ambiguous boss identities and conflicting mechanic instructions are rejected.

Once ready, combat selection looks up boss, event kind and observed numeric ID. It retains the existing current-duty/boss, source/owner, phase, party-target and status-stack checks. Action-effect recipients cannot substitute for cast targets, so instant events only use unconditional triggers. The selected event-specific cue feeds the existing signal, highlight, central presentation and hazard-association paths. CastType and EffectRange are diagnostic metadata, not sufficient evidence to manufacture a safe position.

A new guide object rebuilds its plan; the full guide scope and catalogue fingerprint scope remembered observations. A stale plan cannot apply to another document. While loading, or on catalogue failure, the existing name-based path remains available. Diagnostics expose the state and an explicit reload action. ID preparation does not require rerunning the guide model.

## Diagnostics and exports

Advanced diagnostics show catalogue state, candidate coverage and unresolved names. Prepared-guide artifacts contain the complete ID candidate plan, including unresolved and ungrounded triggers. Timeline frames reference catalogue and plan hashes instead of copying that plan every frame. Binding audits retain observed ID, official name, selected mechanic/cue, result, phase and source identity. Up to 64 alternative IDs accompany a timeline audit, with an explicit omission count; full candidates remain in the adaptation artifact. Existing capture quotas and gap reporting still apply.

The offline tool `ForetellRuntimeTests.dll --guide-id-review <analysis.zip> <game-sqpack> <output-directory>` compares retained historical guide snapshots against grouped captured casts/statuses using installed game data. It does not replace historical guides with current caches. Input artifacts are hash-checked and bounded. Missing raw records, missing source snapshots and review limits are reported.

## Validation on 2026-09-08

- Installed game catalogue: 72,353 rows; initial load 1.615 seconds in the detached test process. This measures ID preparation, not LLM analysis time. All four languages and repeat fingerprint stability passed.
- Catalogue fingerprint: `E5D32A9101684EC336E2E100CDE1F23EC0E54B3A48A81AB3EC68C5B6E2F146A6`.
- Synthetic tests cover official translated aliases with unavailable runtime name lookups, multiple same-name IDs, unrelated IDs, foreign documents/bosses, owner attribution, phases, target/stack conditions, legacy grounding, conflicts and export snapshots.
- T520 export `foretell-analysis-T520-20260907-231521-850.zip`: 17 retained historical adaptations, 24 grouped observed events. Last retained adaptation matched one group under both the prior name matcher and the ID plan (Concussion status 996). Some early adaptations lacked retained sources.
- T938 export `foretell-analysis-T938-20260907-223433-518.zip`: 32 adaptations reviewed, 37 grouped observed events. Last reviewed adaptation matched six groups under both matchers. Raw capture was incomplete, 59 guide snapshots were omitted by capture quotas, and the review hit its 32-version bound.

These retrospective comparisons are not a time-aligned replay of party targets, phases or helper ownership, and do not prove live highlight coverage. They show no additional matched groups for those retained English snapshots. The demonstrated improvement is stable multilingual numeric matching, independent of runtime sheet-name availability, with explicit candidate provenance and version invalidation.

## Remaining limits

Official tables do not provide complete boss strategies or a unique boss-to-action ownership map. A guide's unnamed visual mechanic, an inaccurate cue or a missing phase cannot be repaired simply by looking up an ID. Unknown and ambiguous triggers remain unresolved rather than being guessed. Event icons, tethers and environmental controls still require their own grounded signal mappings; this iteration covers action and status IDs plus boss name IDs.
