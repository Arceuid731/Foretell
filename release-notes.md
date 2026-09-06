# Foretell 0.13.2 — readable mechanic lists and boss phases

- Remove mechanic-list pagination. Rows flow into balanced columns, with separate name/action lines, role icons, text shadows and repeatable active highlights. Very constrained layouts scroll automatically and bring active rows into view; conditional alternatives are not truncated.
- Customize list text size, column width, spacing, heading/name/instruction/highlight colors and optional background opacity under Display.
- Let local AI identify documented phases and phase-specific mechanics from complete guides. Follow phases established by observed boss mechanics; retain common and active mechanics, and keep the full reference when the phase is unknown. Matching continues across the entire boss reference so transitions remain detectable. Demo mode also previews phase filtering.
- Reuse a prepared guide when entering its instance. Ignore workbook view/selection metadata when deciding whether analysis content changed, retain verified sources through temporary provider failures, and check for source updates in the background. Real content changes and explicit reanalysis remain supported.
- Preserve older source and prepared caches after integrity checks. Existing guides are not automatically reanalyzed just to add phase metadata: use Reanalyze once if you want phases for an older guide. Guides without documented phases remain usable.

Generated instructions and phase assignments can still be incomplete or mistaken. This update does not certify every strategy.
