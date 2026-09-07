# Qitana guide preparation — 7 September 2026

## Recorded baseline

The Qitana Ravel, content 651 / territory 823, Qwen 3.5 4B with Vulkan, 64K context and a 12 GiB process budget. The 0.13.5 journal `analysis-20260907-191714-abebb2f1` completed successfully in 154.74 seconds including startup. Its requests consumed 148.70 seconds: outline 25.63, Lozatl 46.47, Batsquatch 38.05 and Eros 38.56. There was no retry or combat pause.

All three boss requests repeated the same 62,428-character combined source. Each prompt was about 50,450 tokens. The complete run used 187,050 input tokens. Enumerating all paragraph IDs in several schema locations also enlarged the model-visible instructions.

## Implementation

- Retain the global outline so the model determines the encounter roster and source attribution.
- For a multi-boss guide, request one combined draft against the complete numbered source. No heuristic parser selects or removes boss mechanics.
- Split the returned structured boss records, not the source page. Validate each boss independently against the same full evidence. Preserve valid drafts while correcting invalid, missing or duplicate requested bosses individually.
- If the combined request cannot fit or returns incomplete/malformed JSON, fall back to the existing bounded per-boss path. Canceled requests propagate cancellation; completed responses remain reusable by the existing resume cache.
- Replace repeated paragraph-ID enumerations with compact positive-digit string schemas. The exact ID range, source grounding and phase/condition validation remain runtime requirements; grammar acceptance does not establish evidence validity. The model still receives the full schema and all numbered paragraphs.
- Reject the current boss's own title as a phase label. A failed optional phase repair retains the validated mechanics without phase filtering.

The compact schema uses anchored string patterns, supported by the upstream [llama.cpp grammar documentation](https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md). No server/model version or resource limits change.

## Real trial and limitations

One authorized hidden local inference run used the exact same source fingerprint `A2E0DCFF9C40E3A5CE10F67681827718330FA10B028CFB54DAD74A3F7CD3A8EE`, without downloading sources or replacing installed caches. Journal `analysis-20260907-192905-57b329ec` completed in **96.19 seconds including startup**, approximately 38% below the recorded 154.74-second baseline. Hardware settings were unchanged, but game load is not a controlled benchmark variable.

The outline took 21.56 seconds and the combined draft 41.69 seconds. These two main requests replaced four baseline requests. The model also emitted invalid optional phase metadata, adding three phase-only repairs of 7.37, 7.33 and 9.80 seconds. Thus the measured run used five total requests and 147,998 input tokens, not two total requests. First-time preparation is still not immediate.

The draft contains three bosses and 16 mechanics versus 13 in the baseline, but this is not a semantic quality score. In particular, the model still suggests healing for some avoidable AoEs and stacking for Lozatl's Scorn; these mistakes also occur in the baseline. Heat Up is no longer a separate draft entry, while Subsonics, Rend and Glossolalia are added. This iteration optimizes preparation, not a proof that all natural-language strategies are correct.

The real run additionally exposed boss titles being accepted as single phases. The final guard is validated by replaying the recorded exchanges without another model run; those invalid phase plans must be rejected while all three bosses' mechanics remain available. Private source dumps, model outputs and logs stay under ignored `build/`; no installed guide or model cache is modified.
