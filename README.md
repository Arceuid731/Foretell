# Foretell

Foretell is an experimental adaptive encounter-intelligence plugin for FFXIV, built on BossMod Reborn.

It keeps the mature BMR world-state/rendering stack while adding local multi-signal observation, mechanic inference, confidence/ambiguity tracking, persistent contextual encounter memory, timeline learning, Replay Lab diagnostics, and predictive world/radar/text guidance.

For cast actions, Foretell also consumes useful local FFXIV client metadata as an immediate prior: `CastType`, `EffectRange`, `XAxisModifier`, `TargetArea`, `Omen`/VFX information and actor hitbox. These priors can make ordinary telegraphs useful from the first cast, but they are never treated as unquestionable ground truth: observed outcomes can confirm, refine or override them, and metadata alone cannot reach the 99% safe-guidance threshold.

## Dalamud custom repository

`https://raw.githubusercontent.com/Arceuid731/Foretell/main/repo.json`

## Quick start

1. Install Foretell from the custom repository and disable a separate BossMod Reborn installation while testing; Foretell already contains the BMR stack.
2. Run `/foretell` to open the Foretell cockpit.
3. Start in **Observe** on familiar content. Foretell learns silently while BMR remains your reference.
4. After a few pulls/runs, switch to **Compare** and review **Learned mechanics**.
5. Move to **Hybrid** when the learned results match the fight. Use pure **Foretell** only when you intentionally want to hide legacy BMR encounter presentation.

The in-game **Help** tab explains every Foretell mode, confidence threshold, data source, local file and slash command.

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

Foretell records a compact normalized event stream locally. Replay Lab re-injects that stream through the same learner in an isolated temporary store, including the same client-metadata prior stage used during live play. It reports what was rediscovered/ambiguous/rejected, then restores the live learned memory. It is an inference replay, not a video or 3D recreation of FFXIV.

This makes recorded pulls reusable as a regression corpus while the inference engine evolves.

## Safety and privacy

Foretell is advisory. It does not add autonomous character movement. Low-confidence hypotheses are suppressed from stronger guidance, and safe-position suggestions use the deliberately strict **Never Guess Lethal** confidence threshold.

Learned memory, normalized replay streams, and diagnostics stay local; Foretell does not require a remote inference API.

## Upstream

Based on FFXIV-CombatReborn/BossmodReborn. Upstream license and attribution are retained in this repository.
