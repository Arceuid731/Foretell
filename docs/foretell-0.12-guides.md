# Foretell 0.12 — generic guide presentation pipeline

## User flow

Any current ContentFinderCondition with a matching TerritoryType and non-empty official English name can request a Console Games Wiki page. There are no runtime Praetorium/Orbonne/other encounter lists. The bounded parser retains boss, phase, nested condition and source-revision context.

At entry, a non-modal, dismissible panel shows the localized duty title, source, download/parse state, elapsed time, bytes when available and a median estimate from at most 16 actual local preparations. A first run measures rather than inventing an ETA. The usable cached/quick document appears first. Since 0.12.1, the entry panel waits for manual dismissal instead of closing after 15 seconds. Combat hides it temporarily without dismissing it. Users can reopen it from Guides or by clicking the checklist's boss heading.

The live checklist shows one identified current boss, or an upcoming boss while out of combat. A nearby identified boss is preferred to source order; engaging a later boss supports joining in progress. Multiple engaged documented bosses abstain. Death must be observed; despawn is not a kill. A wipe clears per-pull resolutions without forgetting previously defeated bosses and waits for old combat state to clear before rearming. Checkmarks mean a signal resolved, not that the player handled it correctly or that it cannot recur. Other bosses remain browsable in the inspector.

Checklist layout persists separately from radar/text layout: normalized position, unlock/drag/resize, width, maximum height, scale, ordinary text, active, resolved and unresolved colors. Since 0.12.1 the locked overlay has no background, title bar, borders or scrollbar. Only the boss heading and one line per mechanic remain: name, short response, and a countdown when live. A text shadow keeps it readable over the game. Descriptions, context, summary progress and provenance are in hover tooltips; Shift reveals the full English mechanic/context source, and the inspector retains complete document browsing. Tooltip previews are bounded and visibly ellipsized rather than presented as complete source.

Height fits the visible rows automatically. Exceptionally long lists use bounded pages, not a scrollable article; a current signal selects its page. Width reserves room for the response, ellipsizing long names on grapheme boundaries. Unlock restores editing chrome/background and persists the maximum height. Positions are clamped to the viewport. Central alerts reuse the movable text overlay and its enable switch, with an additional guide-alert switch and independent text scale (default 1.4). Checklist and central alert use the same short response for the same live signal, including spatial/target abstention; ambiguous responses remain CHECK RESPONSE. No model-generated prose becomes an executable instruction.

## Evidence and presentation

- A live cast requires exact duty identity, a unique documented English BNpcName match, a unique Action-name match within that boss, and actual nonzero actor/OID/NameID/Action IDs. Localized display names use those contextual IDs, not a globally guessed Action row.
- A helper participates only through an observed explicit owner actor identifying the boss. Unowned helpers are not attached by proximity or similar names. Ownership, source identity, spell occurrence, interruption, resolution, expiry and death are rechecked before drawing.
- A local-player status highlights a unique source mention only when its actual Status ID/name and source boss match. Narrow positive-status condition rules can enable spread/look-away/cleanse instructions after the named status is observed. Unresolved alternatives, sequences and phase conditions remain documentary.
- One combat frame drives the checklist and central alerts. Its associations annotate the existing DecisionFrame used by both radar and world overlay. Existing dimensions, endpoints and confidence do not come from guide prose. Shapes retain the ordinary display thresholds. Guides never increase learned success counts or confidence.
- Unknown geometry is an explicitly non-spatial cross/label annotation, not a circle radius. Advisory guide changes prevent safe-route claims. A cast target alone never establishes a stack/tower anchor. Conditional guide text does not suppress a separately grounded generic hazard warning.
- Central alerts show matched mechanics with a remaining-time bar. Direct personal instructions require relevant target/status/spatial evidence. Otherwise a concise watch/check-response/check-target cue is shown, with the same cue in the highlighted checklist row, rather than guessing a safe direction.

## Local summaries and resource bounds

### Analysis ZIP provenance (0.12.2)

Guide diagnostics now share the automatic capture worker's ordered queue and immutable export barrier, but remain separate from combat observations and learning. `guides/index.json` in each Analysis ZIP links SHA256-verified compressed JSON files: `guide-source-<hash>.json.gz` contains the extracted English document, duty identity, revision, source URL, boss/phase contexts and mechanic text; `guide-snapshot-<number>.json.gz` contains session/time identity, source reference, language, model revision, adapted names and contextual IDs, original rule evidence, conditional flags, translated summaries, short responses, resolved flags, sampled cast/status associations and relevant guide settings.

Preparation stages, attempted/total counts and available summary count are separate. A prepared attempt is not proof of a usable summary or tactical coverage. Snapshots occur on state/summary/name/boss/signal changes, sampled at most every 250 ms, with a forced snapshot before an active-session export. They are not a continuous event trace or proof of which pixels were drawn. Source documents are deduplicated within a session; the capture keeps at most 16 source revisions and 128 adaptation snapshots, 4 MiB expanded per artifact and 16 MiB compressed guide data within the existing 64 MiB session / 256 MiB cache / 14-day retention limits. Queueing, quota, integrity or preparation failures produce explicit guide warnings without invalidating otherwise complete decision observations.

The sidecars are pinned with the selected session, including historical exports after duty changes or plugin restart. Sources and adaptations are never fetched from a later current cache during export. Old ZIPs do not gain historical guide evidence by being re-exported; absent records are reported as unavailable. Original user ZIPs remain untouched. These files retain extracted source text, not the original full wiki HTML/API response. The normal detached learner ignores them entirely; tests verify unchanged decision digests with/without guide attachments.

The guide document and fast classification do not wait for a model. A separate worker progressively translates/condenses source excerpts, prioritizing the current/upcoming boss. It handles attributed text only and cannot query the game, execute tools, choose Action IDs, publish geometry or change live rule semantics.

Source-context sections are also summarized, including sections with no named ability (for example general boss strategy or tank notes). They are not silently dropped or turned into fictitious mechanics. Context and per-ability excerpts have distinct source-bound cache keys.

Pinned assets, fetched only when summaries need them:

| Asset | Size | SHA256 |
| --- | ---: | --- |
| Qwen3-1.7B-Q8_0.gguf, commit `90862c4b9d2787eaed51d12237eafdfe7c5f6077` | 1,834,426,016 B | `061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a` |
| llama.cpp b10809 Windows CPU x64 | 18,407,457 B | `9df3158ed228a641a4b127942d7f459f24c9e13f04682659d05c00c80099b6b5` |
| llama.cpp b10809 Windows Vulkan x64 | 35,221,385 B | `97e50b3ef0cdd2cb4d5afd446a9006b3496bee6c0d0ba7083d32f36075771870` |

Primary upstream references: [official Qwen model and Apache-2.0 license](https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/tree/90862c4b9d2787eaed51d12237eafdfe7c5f6077), [pinned llama.cpp release](https://github.com/ggml-org/llama.cpp/releases/tag/b10809), [server configuration/API](https://github.com/ggml-org/llama.cpp/blob/b10809/tools/server/README.md).

Windows limit semantics follow Microsoft's [CPU-rate control](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information) and [committed-memory limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information) definitions; the memory cap is not a working-set or VRAM guarantee.

The plugin manages installation and startup; no external launcher or URL entry is required. Resume requires validated byte ranges; final files require exact pinned length/SHA256. ZIP extraction rejects traversal and oversized archives. The server binds to `127.0.0.1` with a random per-start API key, no web UI/shell/inherited LLAMA overrides, and no proxy/redirects on inference. A Windows Job Object enforces kill-on-close, one process, 15% aggregate CPU hard cap and 4 GiB committed process memory. The server uses two CPU threads, one request slot, 4096-token context, 128-token batches and at most 600 output tokens. Vulkan uses the fixed small model/layer count; **no hard VRAM quota is claimed**. CPU mode is selectable and is the startup fallback if Vulkan fails.

Inference is canceled and the helper killed on combat, disable, duty change or disposal. Completed summaries survive in a separate cache (128 entries / 64 MiB); cached summaries load even in combat without constructing a model. A retry control resumes unfinished excerpts. Long, truncated, invalid, untranslated-identical or numerically ungrounded output is rejected. Additional French checks retain explicit condition/alternative vocabulary. These checks do not prove semantic accuracy: summaries remain labeled automatic and the complete English source stays accessible. Nothing from a generated summary becomes an executable combat rule.

## Validation and limits

Deterministic runtime tests cover current/upcoming boss selection, joining in progress, wipe/death/despawn, multi-boss ambiguity, per-pull resolutions, owned helpers, status prerequisites, independent geometry/confidence preservation, cross-caster/occurrence rejection, non-spatial annotations, measured ETA persistence, ZIP traversal, summary cancellation on combat, replacement, disable, and cached summaries without a model. Existing parser/HTTP/cache tests, core geometry tests and the telemetry contract remain required.

0.12.1 adds short localized cue tests, unconfirmed spatial/group-target abstention, active-page selection, page reset for shorter lists, bounded hover previews and Unicode-safe name truncation. Generic source rules now also recognize standalone party-wide damage and cone/line attack wording; conditions and multi-response rejection still apply. The optional `dotnet run --project ForetellRuntimeTests -c Release -- --guide-ui-smoke` creates an isolated native ImGui context and checks the actual overlay-line drawing, one-line height, transparent/locked flags, editing flags, hover and tooltip emission. This is a headless native UI smoke test, not an in-game visual acceptance test.

Explicit local CPU/Vulkan probes download pinned assets if missing:

```powershell
dotnet run --project ForetellRuntimeTests -c Release -- --guide-model-smoke <owned-cache-directory> gpu
dotnet run --project ForetellRuntimeTests -c Release -- --guide-model-smoke <owned-cache-directory> cpu
dotnet run --project ForetellRuntimeTests -c Release -- --wiki-smoke 'Sastasha' 'The Bowl of Embers (Hard)'
```

Three synthetic French probes cover marked/unmarked alternatives, ordered cone attacks and rotation rather than movement. Initial probes exposed English copying and weak alternatives; prompting/rejection checks were tightened. Corrected Vulkan probes took approximately 0.3–0.4 seconds per short excerpt after startup on the development machine; CPU probes took 2.7–5.7 seconds under the same process limits. These are not timings for long raid guides or promises for modest PCs. The UI uses its own measurements. Initial model download took roughly two minutes on this connection, separate from quick wiki acquisition. Repeated headings/proper-name translation can still be imperfect; runtime summaries omit those input headings and display official names separately.

An actual auxiliary-process pipeline smoke test verified process termination on combat, resumption, three French cached summaries and cache reload in combat with a model factory that throws if invoked. Real wiki probes for Sastasha, Aurum Vale and The Dead Ends retrieved and parsed their documents in approximately 0.60–0.68 seconds each on this connection; parsing/rule preparation took 17–35 ms on the background worker. These timings are measurements, not latency guarantees or encounter coverage metrics.

Not claimed: a universal tactical compiler, reliable translation of every condition, arbitrary icon/tether-ID interpretation, all phase/variant resolution, phase transitions inferred from source order, invisible helper ownership, guessed stack targets, guide-generated precise geometry, rendered-game replay, or completed in-game visual/combat acceptance. Existing learner marker/tether predictions remain available independently, not falsely declared guide-confirmed. No authored BMR/Splatoon encounter modules or GitHub synchronization helper were introduced.
