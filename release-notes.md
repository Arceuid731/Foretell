Foretell 0.9.6 fixes several causes of fragmented terrain and incorrect confidence feedback.

- Preserve arch openings and stair risers, reject ceiling undersides and non-walkable materials, and select a coherent reachable floor layer.
- Keep the requested collision radius stable across refreshes and render internal walls that can be walked around.
- Copy native collision geometry under the game's nonblocking shared scene lock; include sphere colliders and object transforms in refresh detection.
- Require fresh terrain and a directly traversable segment for safe-direction suggestions. Retained stale terrain is dimmed and marked as refreshing.
- Keep attack outlines visible independently of walking reachability. Structural animations remain temporary terrain cues rather than permanent forbidden sectors.
- Stop map reconstruction from creating combat phases or predictive timeline signals.
- Separate confirmed hits from heals, source effects and visual cues; abstain from outcome learning after semantic evidence loss.
- Train from independent episode outcomes rather than the classifier's own guesses, and validate geometry against the current episode. Schema 23 resets old classifier weights and mechanic forecast scores while retaining observed samples and learned mechanics.
- Distinguish tankbuster guidance and avoid unsupported dispel instructions; improve the compact radar caption and confidence legend.
- Add live collision snapshots to Analysis ZIP and an offline collision/raw diagnostic reader.
- Add terrain, routing, outcome and snapshot regressions, and require a compatible .NET 10 SDK.

The full review, evidence and remaining work are in [docs/review-2026-09-04.md](https://github.com/Arceuid731/Foretell/blob/main/docs/review-2026-09-04.md).

Validation covers deterministic core tests, the static telemetry contract and the complete Windows build. The original in-game route still needs visual confirmation after updating; historical raw journals do not contain its collision triangles. Multi-storey projection, Replay Lab isolation, pre-impact ML evaluation and frame-time measurements remain open work.

No authored encounter identifiers, layouts or mechanic answers were added. Foretell remains advisory and does not automate movement.
