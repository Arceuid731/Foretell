# Stone Vigil capture review — 0.13.11

## Recorded symptoms

Input: `foretell-analysis-T1042-20260908-222806-777.zip`, captured with 0.13.10.0. The raw observation stream contains 47,453 records and reports complete. Guide capture reports 13 omitted full-source snapshots; the available timelines retain the relevant boss and mechanic matches.

All three Typhoon casts (28730) matched their official game ID to Koshchei's guide mechanic. Six sampled list-row submissions have `Highlighted=true`; 116 sampled Typhoon rows were retained overall. These are change-triggered presentation samples, not durations or proof of rendered pixels. The highlight was limited to the cast, while the mechanic continued afterwards.

The cast is a zero-radius visual. The subsequent effects use a separate, untargetable actor and action 28731. The recording contains 161 such ActionResolved events. Their installed-game prior is CastType 2, radius 3, and repeated source positions show stationary and advancing lanes at approximately 0.6-second intervals. A guide ID lookup alone does not establish this lifecycle or movement.

The cached advice has an unconditional short cue about avoiding tornadoes, but presentation preferred the concatenation of all conditional responses. That produced the paragraph both in the list and at the centre of the screen.

## Generic changes

- Short cues have a separate complete/shared scope. Model instructions keep all conditional alternatives in responses and description, not in the central cue. Both list and centre use the same short-cue selection. Legacy cached cues are shortened only when their evidence supports the common response; otherwise a neutral reminder replaces the paragraph without selecting an arbitrary branch. Full details remain available.
- The pulse tracker consumes the existing observation stream. It requires an enemy, untargetable self-targeting emitter, a supported circular game-data footprint and three consistent repeated impacts. It estimates cadence and velocity per source/action identity. Stationary-to-moving changes must be reacquired. Forecasts stop on expiry, identity/lifecycle changes, gaps or inconsistent motion.
- The renderer advances moving shapes to the current frame time. These are short, advisory forecasts, not scripted trajectories or certified safe routes. They do not classify an unmarked circle as avoid/stack/soak from its radius.
- A guide continuation requires the current unambiguous boss, an official candidate ID resolving to the same mechanic as a recent boss cast, a nearby untargetable emitter, and fresh pulse evidence. Once established, fresh continuous pulses can retain that association. It expires with the emitter, guide/boss context or pulse stream. This is explicitly a temporal association, not a fabricated owner ID.
- Guide exports retain motion identity, sample count, last position, velocity, cadence and readiness; pulse signals retain RelatedBossID and RelatedCastID. Capture limits have explicit omission counts. The existing guide details and raw observations remain available for review.

No production duty IDs, actor IDs, lane coordinates, direction or encounter-specific travel speed were added. The IDs above identify the inspected fixture only. Unlike an encounter script, the generic tracker cannot draw a known trajectory before observing enough impacts.

## Offline verification

`ForetellRuntimeTests.dll --moving-hazard-review <analysis.zip> <output-directory>` reviews the captured observation stream without loading the model or changing installed configuration. It reports emitter/action groups, short forecasts and errors against the next recorded impact. This is not a live game rendering test.

On the full retained stream, the tracker identifies four emitter/action groups and produces forecasts at 2,715 observation samples. For 127 predictions checked against the next impact within a 200 ms timing tolerance, average centre error is 0.089 yalms and maximum error 1.470 yalms (the footprint radius is 3). The maximum includes motion transitions: this remains a short-horizon hypothesis, not a guaranteed trajectory. First forecasts appear 1.17 seconds after the first recorded impacts, not before the initial spawn. Late unrelated hook observations do not reset the tracker or rewind rendering; relevant source discontinuities still invalidate it.
