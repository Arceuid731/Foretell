# Foretell 0.12.4 — guide-focused cockpit and visible local AI

- Reorganize /foretell into Instance, Sources & guides, Local AI, Display and Advanced. Lead with the actual instance/current boss instead of first-run blind-learning metrics. Preserve BMR modes, saved overlay positions, observation memory, timeline, recordings and Analysis ZIP under Advanced.
- Show auxiliary-model activity in the header, entry panel and dedicated AI page: file verification/download, loading, idle, token counting, inference and unloading, with process identity and actual Vulkan/CPU backend. Cache readiness is distinct from a loaded model.
- Explain when missing files download. Keep the pinned Qwen3-1.7B Q8_0 model and prepared cache; do not silently install an unvalidated replacement.
- Make context configurable from 4096 to 32768 tokens (default 16384), and committed process RAM from 4 to 12 GiB (default 6). Retain two CPU threads, aggregate 15% CPU cap, hidden helper and combat cancellation/unload. Larger context costs memory/time; no hard VRAM limit or gameplay performance guarantee is claimed.
- Replace the 7000-character excerpt skip with complete chat-template/tokenizer preflight, output/control reserve and visible overflow reporting. Reject truncated generation; never trim a prompt to make it fit. Resource changes retry incomplete preparation without erasing accepted summaries.
- Include context/RAM settings, sampled model state and the latest excerpt rejection reason in session-bound guide analysis exports. Keep replay learning neutral to guide prose.

Validation covers fake-model lifecycle (cache-only, completion, combat, disable, replacement/context change), long-excerpt preservation and visible rejection, mocked template/tokenizer endpoints, resource bounds, localized state labels and session-time analysis provenance. Non-interactive test-host checks remain enabled. No real-model download or inference benchmark runs in the ordinary suites or during this iteration.

Scope: this is the cockpit/runtime-resource iteration, not the semantic whole-page compiler. AI still translates already-extracted excerpts. Additional guide providers, whole-page boss/mechanic extraction and a validated model selector remain to implement. Compact model candidates and primary sources: docs/foretell-models-2026-09-06.md. No hardcoded duty list or guide-generated geometry is introduced.
