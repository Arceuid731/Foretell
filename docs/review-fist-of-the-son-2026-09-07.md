# The Fist of the Son: empty mechanic list

## Evidence

Export: `foretell-analysis-T520-20260907-231521-850.zip`, captured with 0.13.6.0. The local analysis journal completed successfully between 21:01:38 and 21:02:02 UTC. Historical guide snapshots contain one identified boss and six mechanics, not an empty analysis. The recorded player job is 28 (Scholar).

The mechanics' role sets are: tank; tank/melee/ranged; and four melee/ranged entries. None include healer. All six are actionable rather than `ContextOnly`. With no active signals, the former role filter in `GuideCombatListPresentation.Select` removes every entry for Scholar. This deterministically reproduces the reported zero rows before and during combat without needing additional inference.

## Correction

Remove role-based exclusion from reminder selection. The existing relevance rank already assigns lower priority to other-role advice. Keep active mechanics first, then role-relevant/universal reminders, then remaining actionable reminders up to the existing six-row budget. Preserve boss identity, phase filtering, explicit role icons and the complete prepared guide. Do not rewrite cached advice or label unrelated mechanics as active.

Regression tests cover all four healer jobs, upcoming/current boss states, priority ordering, list bounds, active signals, current-phase filtering and unchanged role tags. This is a generic presentation correction, with no duty-specific production rule.

## Separate limitations

The historical guide has five `manual` triggers and one `cast` trigger named Concussion; its snapshots contain no active signals. Displaying these reminders does not establish that their triggers are valid or that they will highlight automatically. The generated tank cue also substitutes an interrupt for a source instruction to provoke. This patch does not claim to correct those analysis/trigger-quality issues.
