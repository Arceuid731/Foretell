# Local models and guide preparation — 6 September 2026

## Update — 11 September 2026

A [real comparison on five recent recorded source sets](model-comparison-2026-09-11.md) now covers all three installed profiles with identical 64K / 12 GiB / Vulkan settings. Qwen produced five technically accepted guides, Gemma four and Granite three. Qwen remains the default for availability and coverage; accepted outputs still contain incorrect instructions, lost alternatives and unresolved boss identities. These acceptance counts are not semantic accuracy scores. The sections below describe the earlier evaluation.

A subsequent [reasoning and cross-model review experiment](reasoning-review-2026-09-11.md) ran 17 isolated treatments on those sources. Bounded Qwen reasoning and Gemma/Qwen review proposals still introduce or preserve unsafe instructions. Production settings remain unchanged; neither automatic review application nor thinking is enabled by these experiments.

## 0.13.0

Foretell now has three pinned local model profiles and whole-document analysis of aggregated guide sources. Player guide content is English in this iteration; application controls may follow the client language. These are implemented preparation paths, not a claim that all three models have passed real-encounter evaluation.

**Qwen3.5 4B is the default.** A real extraction/review run prepared Praetorium in 75.2 seconds (3 bosses, 21 mechanics). A larger Orbonne run prepared 4 bosses and 50 mechanics in 472.6 seconds, including retries. These measurements exclude the initial weight download and are not gameplay or semantic-accuracy guarantees. Wrong directions, trigger classifications and conditional instructions were observed. Granite and Gemma remain selectable, but their earlier runs did not establish reliable guide quality.

| Profile | Pinned GGUF | Download bytes | Configured maximum context |
| --- | --- | ---: | ---: |
| Qwen3.5 4B (default) | `Qwen3.5-4B-Q4_K_M.gguf` | 2,740,937,888 | 131,072 |
| Gemma 4 E2B | `gemma-4-E2B-it-Q4_K_M.gguf` | 3,106,738,272 | 131,072 |
| Granite 4.1 3B | `granite-4.1-3b-Q4_K_M.gguf` | 2,099,501,664 | 131,072 |

Exact download revisions, SHA-256 hashes and chat options are defined in `BossMod/Foretell/ForetellGuideModelCatalog.cs`. All profiles request disabled thinking and deterministic sampling for this extraction workload. A configured context maximum is an application limit, not a measured usable context or a guarantee that loading will fit alongside FFXIV. A quantized model's download size is not its RAM or VRAM requirement.

## Acquisition, analysis and live use

1. Providers acquire complete source documents with URL, retrieval/cache state and fingerprints. HTML acquisition does not split bosses by heading depth. Workbook acquisition selects a unique relevant worksheet through names, links and labels, retaining the complete worksheet instead of cutting mechanic columns.
2. The local model assigns passages to bosses and extracts mechanics, concise English player responses, conditional alternatives, role metadata and supporting excerpts. Context budgeting includes the chat template and response allowance. If partitioning is necessary, it must cover the complete source; partial preparation is not accepted as a complete cache.
3. Validation checks the response shape, evidence and physical source coverage. Live synchronization then uses contextual boss ownership and observed casts/statuses. The model does not supply game IDs or establish geometry.

These checks do not establish semantic correctness. A model can cite real text while assigning it to the wrong boss, omitting an alternative or combining obsolete and current rosters. Granite's real Praetorium run assigned overlapping wiki sections to three bosses and failed on a fourth partition. Substantially overlapping boss passages are now rejected. Qwen's result improved attribution, but the extra per-boss source review did not eliminate semantic errors. Complete source partitions are now grouped by boss before mechanic analysis, avoiding repeated whole-boss compilation and per-mechanic re-merges. Quotation validation accepts decoded workbook line breaks; source-based movement checks correct the specific class of confusing an arena-edge field with the whole arena. Those guards do not constitute a general natural-language correctness proof.

The mechanic overlay can highlight each repeated occurrence again. The active row and central alert share the concise instruction, while details and alternatives remain accessible. Boss progression, wipe handling and live signal association are deterministic presentation behavior; generated advice remains probabilistic.

## Resources, caching and resumption

Local AI exposes model selection, automatic preparation, the requested graphics-card backend, actual helper activity, context and RAM settings. The UI offers 8K–128K tokens (legacy 4K configurations remain readable), default 32K; committed RAM is configurable from 4–12 GiB, default 6. This limit is not a reservation and does not cap VRAM. Larger contexts can require more memory and preparation time; a bounded process can still fail to load or run.

The helper runs without an interactive window. Combat pauses preparation and releases the active worker; an accepted prepared cache can remain usable without a loaded model. Completed analysis responses are retained in bounded in-memory resumption state for matching requests. That state is not a promise of durable checkpoint recovery after restarting the application. Source/model identity and full preparation validation govern accepted cache reuse.

Initial weight/runtime installation, loading and analysis are separate operations. No instantaneous first-use claim follows from a fast source download. The recorded test machine has a Ryzen 7 9800X3D, RTX 5080 with approximately 16 GiB VRAM, and approximately 64 GiB RAM; this does not establish the free resource budget during FFXIV.

## Workbook representation measurements

The inspected XLSX is 527,148 bytes, with 46 worksheets and 189 defined names. Compact transport text preserves every selected-sheet cell's content and coordinate. Empty cells use lossless coordinate ranges; visual styles share a small dictionary; original worksheet XML, rich strings and full style data remain in `Original`.

| Selected duty | Complete worksheet | Cells / nonempty | Model-source characters |
| --- | --- | ---: | ---: |
| The Praetorium | ARR Main Story | 2,438 / 39 | 7,470 |
| The Orbonne Monastery | SB Ivalice | 1,236 / 85 | 23,004 |

Praetorium previously produced 322,556 characters in the verbose representation. The compact result is about 97.7% smaller without semantic trimming. Read-only parser tests reconstruct the exact cell contents and coordinates from the compact representation. These character counts are not tokenizer measurements, model benchmarks or validated preparation times. The compacted Praetorium source still contains obsolete magitek/extended-roster notes and the other duty on the complete sheet. See [source applicability and limitations](guide-sources-2026-09-06.md).

## Historical context and model research

Version 0.12.4 used pinned Qwen3-1.7B Q8_0 for summaries of passages already extracted by the wiki parser. It introduced explicit worker state, a 4K–32K context range with 16K default, full-template token counting and rejection of oversized prompts, replacing an older 7,000-character exclusion. It did not yet provide the current source aggregation and three-profile whole-document analysis. Those historical defaults and limitations should not be presented as current behavior.

The earlier compact-model survey distinguished total weights from active parameters and model release dates from later videos or GGUF uploads. Its primary reference set remains useful for evaluating alternatives; it is not a ranking measured on Foretell guides:

- [IBM Granite 4.1 3B](https://huggingface.co/ibm-granite/granite-4.1-3b) and [IBM architecture notes](https://huggingface.co/blog/ibm-granite/granite-4-1): extraction and structured generation motivated its inclusion.
- [Google Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4) and [release history](https://ai.google.dev/gemma/docs/releases): effective parameter counts should be distinguished from the complete model including embeddings.
- [Qwen3.5 4B](https://huggingface.co/Qwen/Qwen3.5-4B) and [historical Qwen3](https://github.com/QwenLM/Qwen3): the former is the current selectable profile; the latter documents the older family.
- [Phi-4 mini-reasoning](https://huggingface.co/microsoft/Phi-4-mini-reasoning), [SmolLM3](https://huggingface.co/HuggingFaceTB/SmolLM3-3B) and [Ministral 3 3B](https://huggingface.co/mistralai/Ministral-3-3B-Instruct-2512): earlier comparison candidates, not installed profiles or validated fallbacks.
- [Tiny Aya Earth](https://huggingface.co/CohereLabs/tiny-aya-earth): the earlier review recorded restricted licensing/access; it was not selected for automatic installation.
- [North Mini Code](https://cohere.com/blog/north-mini-code) and [Nemotron 3 Nano](https://huggingface.co/nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B-BF16): the earlier review excluded their total weight budgets despite small active-parameter counts.

Further evaluation should compare boss attribution, distinct mechanics, conditional alternatives, current encounter applicability, rejection behavior and measured costs on the same sources. JSON validation and a plausible roster are insufficient. Ordinary parser/runtime regression checks do not launch model or GPU benchmarks; those remain explicit probes.
