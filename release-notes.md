# Foretell 0.12.2 — session-bound guides in Analysis ZIP

- Analysis ZIP now includes extracted source documents with duty/revision/hash and session-time adapted checklist snapshots: conditions, localized names and contextual IDs, local summaries, short responses, guide settings and sampled live associations.
- Guide sidecars share the automatic capture worker and immutable export barrier, survive leaving the duty/restarting, and remain isolated from learner input. Source documents are deduplicated; artifacts are bounded and SHA256-verified.
- Add `guides/index.json` with explicit availability/completeness and omission warnings. Historical exports use the selected session's recorded sidecars, never a later current cache. Pre-0.12.2 recordings cannot recover missing historical guide evidence through re-export.
- Add a generic retrospective guide-review command and an Arboretum 0.12.0 report. That capture contains all three boss deaths but no guide state. Current-cache comparison is explicitly distinguished from actual historical matching/rendering.
- Preserve the compact 0.12.1 overlay and existing BMR modes. No encounter-specific runtime IDs, new model downloads or automatic cache-to-learner imports are introduced.

Validation: source/adaptation contents, historical/same-duty session isolation, immutable snapshots, restart recovery, integrity/path rejection, quotas, missing-guide disclosure and unchanged detached decision digests; existing runtime/core tests, telemetry contract and Release build. The Arboretum review records parser attribution, overly broad condition gating, missing helper ownership and a separate topology exception as unresolved findings, not fixes delivered by this release.

Details: `docs/foretell-0.12-guides.md` and `docs/review-arboretum-2026-09-06.md`.
