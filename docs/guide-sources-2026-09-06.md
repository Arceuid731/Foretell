# Guide sources and player-facing direction — 6 September 2026

## Scope and current state

Update, 11 September: the [capacity/source campaign](capacity-stages-2026-09-11.md) verifies the recovered Raven acquisition on recent instances and distinguishes provider availability from encounter coverage and version correctness. Haukke has all four sources in this collection, Fractal and Labyrinth three, Kefka two, and Palace 31–40 one. The following sections document the original 0.13.0 implementation.

Version 0.13.0 keeps guide instructions in English. Application controls can remain localized. It contains aggregated source acquisition, whole-document model analysis, three pinned model profiles, a resizable entry summary, repeatable active mechanic highlighting, and shared list/central instructions. Automated build/runtime/interface checks pass; real-model results still contain semantic errors. Passing schema and source-citation checks does not establish instruction correctness.

Console Games Wiki, Gamer Escape, Raven's Reminders and the public community workbook are integrated acquisition paths. Each provider has an independent result; an unavailable provider does not discard another provider's usable source. This does not establish live availability, complete coverage or correct current strategies for every duty. Foretell is the main combat presentation; BMR compatibility modes remain optional.

Qwen3.5 4B is the default; Gemma 4 E2B and Granite 4.1 3B remain available. An extraction/review run prepared Praetorium in 75.2 seconds (3 bosses, 21 mechanics); a larger Orbonne run took 472.6 seconds including retries (4 bosses, 50 mechanics). These are preparation measurements, not accuracy scores. Direction/name/condition errors remain. Full-source partitions are grouped before one mechanic-analysis sequence per boss. Granite previously reused overlapping wiki sections across bosses, so it is not the recommended profile. See [model configuration and evaluation status](foretell-models-2026-09-06.md).

## Public community workbook

Source: [public community workbook](https://docs.google.com/spreadsheets/d/1MX0RjPS4gtT6YI5Szxlsin9hcaohnEQQC7zNdrDHBrQ/edit).

The public XLSX export downloaded successfully: 527,148 bytes, 46 worksheets and 189 defined names. It contains ARR through Dawntrail sections, including trials, alliance raids and some harder content. This is a broad catalogue, not proof that every current instance or strategy is covered.

Formatting varies substantially. `SB Level 70` has instance names and advice distributed across columns, while `SB Ivalice` has vertically grouped raid sections and many merged cells. Named ranges frequently identify a starting cell rather than a complete encounter boundary. Transport extraction must preserve sheet names, coordinates, merged ranges, headings, cell text, hyperlinks and relevant formatting. The model must determine boss/mechanic grouping; a fixed column or heading-depth convention is insufficient.

`ForetellWorkbookGuide.Read(byte[] archive, GuideDuty duty)` is a local, network-free OpenXML reader. The provider handles HTTP acquisition and cache metadata separately. The reader follows workbook relationships and internal hyperlinks, resolves local defined names before global names, and compares duty labels without dropping Hard/Extreme/Savage/Ultimate distinctions. A unique conservative spelling correction or generic St./Saint normalization is allowed. Absent or ambiguous candidates return `null`; malformed and excessive inputs are rejected. ZIP paths, expanded sizes, XML depth/node counts, sheets, cells and references are bounded, with external XML entity resolution disabled.

The real Index entry for Orbonne resolves the global name `Orbonne` to `'SB Ivalice'!$B$49:$F$49`; other sheets have local names with the same spelling. The worksheet label is `The Orbonne Monastary`. `Praetorium` points to `'ARR Main Story'!$A$3`, while the current label on that worksheet is at `A5`. Selected candidate references and the entire target worksheet are retained together. The complete Index worksheet is not included merely because it supplied the navigation reference.

The compact model input contains every cell's text and coordinate, all merged ranges and links, and shared visual-style signals. Empty cells are encoded as lossless coordinate ranges. `Original` retains complete selected-sheet structured data, worksheet XML, referenced rich strings and full style/theme data; it is not the entire workbook as base64. Compacting the prompt does not rewrite the original evidence or cut off other duties or late-column mechanics on the selected sheet.

Read-only measurements from the exported workbook:

| Duty | Selected worksheet | Cells / nonempty | Compact text characters | Original characters |
| --- | --- | ---: | ---: | ---: |
| The Praetorium | ARR Main Story | 2,438 / 39 | 7,470 | 677,529 |
| The Orbonne Monastery | SB Ivalice | 1,236 / 85 | 23,004 | 524,341 |

The prior verbose Praetorium representation was 322,556 characters; compaction reduces that by approximately 97.7%. Synthetic layout/index/variant/security tests and real-export checks passed, including reconstruction of exactly the original cells from the compact text. These measurements validate acquisition fidelity and representation size, not token counts, model latency or strategy correctness. The original size includes formatting and provenance that are intentionally kept out of the model prompt.

Freshness must be assessed per encounter. `ARR Main Story!A5:O5` still describes magitek armor and the longer Praetorium sequence including later fights, whereas the supplied current capture identifies Mark II Magitek Colossus, Nero and Gaius. A workbook with recent expansion tabs can retain obsolete older encounter notes. Do not rank the entire workbook above other sources solely because it is maintained or large.

No Raven's Reminders URL appeared in workbook cell values during this inspection. That does not prove independence of authorship or absence of shared material.

## Raven's Reminders

Sources:

- [Orbonne guide](https://ravensreminders.com/tldrguide/orbonne-monastery-the/)
- [Guide catalogue](https://ravensreminders.com/tldr-guides/)
- [FAQ](https://ravensreminders.com/faq/)
- [Updates](https://ravensreminders.com/updates/)

The Orbonne page is a good presentation reference: mechanic names, short responses, conditional weapon instructions and role attention icons. It also explicitly qualifies alliance positioning conventions. Those qualifications must survive aggregation, rather than becoming universally prescribed coordinates.

The FAQ says obvious telegraphed mechanics are intentionally omitted. Its stated focus is common roulette/normal content, not comprehensive Extreme/Savage coverage. The updates page inspected here lists April 2022 posts; this alone does not date every guide, but is insufficient evidence of current comprehensive maintenance.

The earlier research browser could read the guide and index. A direct local HTTP download of the guide returned 403. The integrated catalogue/page fetch therefore has no unconditional live-access guarantee. It reports provider failure or reuses an eligible cached response, without requiring an interactive browser or discarding other providers' evidence.

## Wikis

Console Games Wiki and Gamer Escape both have MediaWiki acquisition paths. Gamer Escape's supplied main page returned 403 to the earlier research fetch; implementation of an adapter does not establish access to every page or endpoint. A page can also exist without a useful strategy. Page identity, strategy availability and supported duty variant are separate checks.

## Aggregation and validation boundaries

1. Resolve the official instance and difficulty. Use provider catalogues/indexes to find candidates, rather than a source-wide fixed instance list.
2. Show a valid prepared cache immediately. Refresh sources independently at entry using conditional requests when supported; only changed source hashes require reanalysis.
3. Preserve complete source material and provenance. Supply complete relevant documents to semantic analysis, with full physical coverage if context partitioning is necessary.
4. Extract a common record: original boss/ability name, short English player response, conditional alternatives, role audience, source references, and detectable trigger information. Role cues use explicit role metadata; arbitrary workbook colors do not determine the player's role. Validation can reject structurally invalid records but does not prove that every alternative was preserved.
5. Reconcile per boss and mechanic while retaining phase variants and disclosed source conflicts. Document ordering is an analysis aid, not a guarantee that one provider is always correct. Freshness and encounter applicability still require semantic assessment; the old Praetorium roster is a concrete failure risk.
6. Bind to live boss-owned casts/statuses and validated observed signals. Add learning-assisted associations only with recorded evidence and regression tests. A named add or general strategy note is not automatically a cast.
7. Reuse the same concise instruction in the active list row and central alert. Hover holds details. Each repeated cast can reactivate its row; a previous occurrence does not permanently check off the mechanic. Emphasize the player's role without hiding group-critical instructions.

## Position suggestions

The existing stack already has `DrawSafeSuggestion`, asynchronous route recommendations and route validation against current hazards and traversable terrain. It draws a line and destination circle when eligible. The current capture has `SafePositionSuggestions` enabled.

This is not yet a general solver for every guide-specific placement instruction. Translating an intent such as moving behind the boss or occupying an assigned platform requires the current orientation, relevant phase/status, other players and available terrain. Guide intent and observed encounter evidence should constrain the existing position solver, not bypass it. Incomplete/ambiguous situations should retain the useful textual cue without claiming a precise destination.

## Praetorium capture inspected

`foretell-analysis-T1044-20260906-175518-852.zip` records version 0.12.4.0 in Hybrid mode. It includes 91 guide snapshots, the session guide source and adapted boss/mechanic information. Snapshot signals contain documented associations for the current boss, including the Colossus laser attacks. They are historical synchronization evidence, not screenshots or proof that the player acted on an alert.

The recorded summary worker reports `Unavailable: IOException` with no completed model summaries. The user's positive experience therefore cannot be attributed to successful AI translation in this run. Existing runtime extraction overwrites installed helper binaries on every start; concurrent use can cause file-sharing failures. The working-tree fix verifies identical installed files and leaves them untouched. The capture contains only the exception class, so this is a repaired reproducible failure path, not a proven diagnosis of that particular exception.
