# Foretell repository instructions

## Product copy

- User-facing pages contain only concise, natural, actionable player information. Do not put development discussions, architecture defenses, negative assurances or debug prose in the product UI.
- Keep implementation details in diagnostics and technical documentation. Review all affected pages, not only newly added tabs.
- Foretell is intended as the player's combat assistant/replacement presentation; BMR compatibility modes remain optional, not the default product pitch.

## Standing release authorization

The repository owner has granted standing authorization for the normal Foretell/Dalamud delivery workflow. After completing and validating a requested version or analysis-report iteration:

- push the finalized commits to `main`;
- monitor the build and release workflows to completion;
- verify the GitHub tag/release artifact and the updated Dalamud `repo.json` manifest;
- do not pause for an additional confirmation before these ordinary push, build, merge, and publication steps.

Ask first only when the requested action would rewrite shared history, delete branches/tags/releases, change repository ownership or scope, require new credentials, or perform another unusually destructive operation.
