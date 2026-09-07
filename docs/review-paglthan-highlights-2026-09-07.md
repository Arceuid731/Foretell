# Paglth'an: guide matching and missing-highlight investigation

## Recorded evidence

Input: `foretell-analysis-T938-20260907-223433-518.zip`, session `20260907-201430`, plugin 0.13.6.0, territory 938 / content 777. Installed files were inspected read-only. No inference or cache replacement was performed.

The exported guide snapshots contain live matches on Amhuluk, including Critical Rip, Lightning Bolt, Electric Burst, Thundercall, Wide Blaster and Spike Flail. These snapshots are produced from `LiveGuideSignals`, after the live validity checks. The first recorded example is 20:19:10 UTC; the guide was ready at 20:17:00 UTC. A Magitek Fortress signal is also present at 20:24:58 UTC.

This establishes matching activity, not historical rendering. The player completed the dungeon, but normalized event capture stopped at 20:26:02 UTC after its size limit was reached. The last guide snapshot is at 20:25:03 UTC; 59 guide snapshots were rejected by shared quotas. The absence of Lunar Bahamut guide snapshots is therefore not evidence that its mechanics did not match.

The inspected selection path retains active mechanics before filtering reminders by role or phase. No instance-specific display defect was established from this export. Do not equate a successful synthetic rendering test with proof of what appeared during this run.

## Generic correction

Detailed observation recording must not consume the storage reserved for guide diagnostics. Compact timeline entries retain preparation state, boss/phase, live signals and presentation evidence. Full source and adaptation artifacts remain separately referenced and validated. Export completeness and gaps remain explicit; bounded storage is not advertised as unlimited recording.

Presentation evidence is captured in the actual drawing path, with session identity and draw timestamp. It records selected/drawn rows, requested highlighting, instruction text, screen bounds and clip outcomes, plus submitted or priority-omitted central alerts. Disabled modes, hidden windows, guide preparation and drawing exceptions are distinguished. Frames older than two seconds are reported as `NoRecentDraw`, not current rendering. Demo frames are not reported as real combat presentation.

`Submitted` means drawing commands reached the ImGui draw list inside the recorded clip rectangle. It is not a framebuffer screenshot and cannot prove visibility through other windows, game UI or external overlays. `Highlighted` records the requested active style; consult the outcome for transparent or clipped rows.

## Validation

- Pure presentation tests cover visible/transparent/clipped/invalid bounds, stable-frame deduplication, expired highlights, new sessions and central-alert transitions.
- Headless native ImGui tests exercise the real row renderer with healer icons and confirm additional highlight/timer geometry; no application window is opened.
- Capture/export regression tests exercise quota exhaustion, later guide events, compact timelines and immutable export barriers.

No matching rule, model prompt, boss name mapping or encounter-specific behavior is changed by this iteration.
