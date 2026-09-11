# Foretell 0.13.17 — larger local models and measured guide analysis

- Add pinned Qwen3.5 9B Q4_K_M and Gemma 4 12B QAT Q4_K_XL profiles in **Local AI**. Qwen3.5 4B remains the default. The manager offers a recommended 12 GiB RAM limit for the larger profiles without changing the player's limit automatically.
- Share vocabulary instructions across drafting and repair, distinguishing player/enemy actions, stack markers, unavoidable damage and conditional telegraphs. These are model instructions, not encounter-specific runtime rules; incorrect generated advice remains possible.
- Publish comparisons on five recent instances, memory measurements with FFXIV open, and controlled additions of the recovered Raven source. At 32K on the tested RTX 5080, the Palace trial left about 2.6 GiB free with Qwen 9B and 1.1 GiB with Gemma 12B. These are whole-device observations, not FPS or memory guarantees.
- Retain fact extraction and progressive boss preparation as an explicit experimental probe. Citation and branch-preservation checks improve rejection of incomplete drafts, but observed semantic errors do not justify enabling this pipeline by default. Thinking and automatic cross-model corrections remain disabled.

See [the complete report](https://github.com/Arceuid731/Foretell/blob/main/docs/capacity-stages-2026-09-11.md) for source coverage, failures, remaining identity issues and which trials ran after the game closed. Runtime/core tests, citation/branch regressions and the BMR knowledge-separation contract pass.
