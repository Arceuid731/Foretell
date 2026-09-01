# Foretell engine

Foretell's adaptive layer lives in this directory.

It observes BossMod Reborn world-state events, learns mechanic geometry and encounter transitions locally, persists confidence-weighted memory, and renders advisory world/radar/text predictions.

For cast actions, Foretell now seeds learning from useful local client metadata before outcome evidence exists: Action `CastType`, `EffectRange`, `XAxisModifier`, `TargetArea`, `Omen`/VFX information and actor hitbox are treated as revocable priors. Empirical observations can confirm, refine or override those priors; metadata alone is capped below the 99% safe-guidance gate.

World overlay and radar geometry use the same confidence visualization: cyan/blue for early visual hypotheses, yellow for learned, orange for high-confidence warning-grade predictions and red for safe-guidance-grade danger. Color represents confidence, not damage severity. Circle, donut, cone, rectangle and cross geometries are rendered on both surfaces.

Foretell intentionally does not automate player movement. Low-confidence predictions are suppressed by configurable thresholds, and safe-position suggestions are advisory only.
