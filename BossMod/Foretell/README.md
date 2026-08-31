# Foretell engine

Foretell's adaptive layer lives in this directory.

It observes BossMod Reborn world-state events, learns mechanic geometry and encounter transitions locally, persists confidence-weighted memory, and renders advisory world/radar/text predictions.

The V1 intentionally does not automate player movement. Low-confidence predictions are suppressed by configurable thresholds.
