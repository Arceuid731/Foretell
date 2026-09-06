# Arboretum (Hard) — 0.12.0 capture review

## Evidence and limits

- Input: `foretell-analysis-T788-20260906-141558-160.zip`, SHA256 `D7D141A93C999C3C78E59C0FED8A60C579991B47DA4C9BC13141500DA74D6FEF`. The original archive was read, not modified or uploaded.
- Capture/exporter version: 0.12.0.0. Selected session: `20260906-115631`, territory 788 / content 584, 13:56:31–14:13:15 Europe/Paris. Mode: Hybrid; world/radar/text enabled; optional readable replay disabled.
- The verified automatic capture contains 22,765 accepted observations, 25 hash-verified compressed parts, and reports complete capture with no capture rejection. One raw-feature window belongs to the previous territory (154); the guide comparison excludes it.
- The archive has no guide document, adapted summary, guide configuration or guide-match journal. Its empty warning list did not disclose this gap in 0.12.0. The capture therefore cannot prove what checklist text, match highlights or central guide alerts actually appeared.
- A separately read **current local cache** contains source revision 1489641, source hash `74F82129EEBDEE16B8F28238ECD216BFF9BE65290807EE26F7CEADF1399B13E0`, retrieved at 13:56:32 Europe/Paris. It has three bosses and 17 named mechanics. The matching French summary cache currently contains 16 summaries out of 19 possible mechanic/context entries, model revision `qwen3-1.7b-q8-b10809-v2`. These caches corroborate preparation around entry, but are not immutable historical evidence of availability during each cast.

## Boss and cast observations

All three boss deaths and duty completion are recorded: Nullchu at 14:04:15, Lakhamu at 14:08:51, Tokkapchi at 14:12:35; duty completion follows the last death. These are not inferred from despawn. The 15 recorded pulls include trash; they do not mean 15 boss attempts.

There are 38 enemy/helper cast-start records. Of those, 12 come directly from the three documented bosses and nine from helpers carrying a boss name. Comparing the recorded contextual IDs against the installed English/French client sheets and the current cached document gives **11/12 direct-boss casts with a unique documented name match**. This is retrospective name eligibility, not measured live matching or tactical accuracy.

| Boss | Direct cast starts | Unique name candidates | Main limitation |
| --- | ---: | ---: | --- |
| Nullchu | 4 | 3 | Devour is embedded in Fault Warren's extracted prose, not represented as its own mechanic. |
| Lakhamu | 5 | 5 | Introductory context is flagged conditional; even simple tankbuster/gaze responses are withheld by the current broad context gate. |
| Tokkapchi | 3 | 3 | General arena context is conditional; context/sequence interpretation remains unresolved. |

All nine boss-named helper casts have recorded owner ID zero. Current strict ownership matching correctly does not promote them into boss-confirmed guide matches solely because their displayed name resembles the boss. Eight are Landslip helper casts and one is Sludge Bomb. This is an evidence limitation, not proof that those casts should be ignored tactically by every independent pipeline.

The reviewing build recognizes plain-language cone damage for Odious Air; that recognition was added in 0.12.1 and must not be retroactively credited to 0.12.0. Name matching, a usable short response, and known geometry are separate checks. Complex conditions are not resolved merely because the source name matches.

## Other diagnostics

The bounded decision audit has 330 entries. Its 43 `Verified`-stage entries comprise 42 outcome records with `Verified = null` and one successful trigger-timing record. The stage name alone does not establish 43 successful mechanic predictions, much less guide accuracy. `DisplayEligible` likewise records eligibility, not pixels.

`runtimeAtExport` reports zero draw failures and 22 accumulated update failures; the last is a `KeyNotFoundException` in `CompleteArenaBoundarySweep`. These counters were exported after leaving the instance, in territory 819, so they are not a clean per-Arboretum performance measurement. This separate topology issue is recorded here, not silently described as fixed by the guide-export change.

## Delivered correction and remaining work

0.12.2 adds immutable session-time source/adaptation sidecars to Analysis ZIP with provenance, sampled live associations, integrity hashes and explicit availability/omission warnings. It does not rewrite this old recording, substitute current caches for historical state, or claim to fix the parser's Devour attribution, the overly broad context gate, unknown helper ownership or the topology exception.

The generic detached review command is:

```text
dotnet run --project ForetellRuntimeTests -c Release -- --guide-review <analysis.zip> <foretell-guides-directory> <game-sqpack-directory> <report-directory>
```

It writes a retrospective comparison plus separately labeled current-cache source/summary copies. It does not run the local model, download a new guide, mutate learned memory or modify the input ZIP. Local outputs for this review are under `build/arboretum-0.12-review/`; personal recordings and complete cached wiki text are not included in the repository publication.
