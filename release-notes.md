# Foretell 0.13.0 — instance guides and live mechanic reference

- Fetch the community workbook, Raven’s Reminders, Console Games Wiki and Gamer Escape independently. Refresh at instance entry, share short-lived workbook/catalogue downloads and retain verified copies for offline use. A missing or inaccessible provider does not block the others.
- Analyze complete page text and complete relevant worksheets with local AI. The model selects the current encounter’s bosses and mechanics, reconciles complementary descriptions and retains phase, target and role conditions. Review generated player instructions against the source before publication; unresolved contradictions cannot produce automatic guide alerts.
- Keep guide instructions in English. Select Qwen 3.5 4B (default), Granite 4.1 3B or Gemma 4 E2B under Local AI. Show download/preparation activity; resume completed analysis work after combat. Prepared guides work without loading a model.
- Replace the completed checklist with a transparent mechanic reference: **mechanic — action**, detail and source attribution on hover, role tags, repeatable active highlighting, configurable position, dimensions, scale and colors. Unlock to move or resize it.
- Keep the resizable entry overview until dismissed, with preparation progress and boss summaries as they become available.
- Display the same instruction in the central alert with the cast countdown. Associate named casts, player statuses and observed instant actions only with the current boss or its verified helpers. Keep simultaneous alerts and status-target markers correctly associated with radar/3D observations.
- Simplify the cockpit into Instance, Sources & guides, Local AI, Display and Advanced. Preserve saved overlays, observation memory, recordings and optional presentation compatibility modes. Existing safe-position suggestions remain available when the spatial observations support a route.
- Include provider originals, selected worksheet layout, adapted instructions, conditions, role tags, source attribution and model state in Analysis ZIP. Model-aware caches invalidate when source content changes.
- Keep development tests and model helpers non-interactive. Avoid rewriting an identical installed runtime while its files are in use.

## Availability

Gamer Escape returned HTTP 403 during this release’s network checks. Raven’s Reminders was successfully fetched for Orbonne in one run, but returned 403 in others. Each provider’s current status appears in Sources & guides. The workbook and Console Games Wiki were accessible. The workbook contains some older encounter versions, so fetching it successfully does not establish that every row is current.

Generated guidance can still be incomplete or mistaken. Role tags are text labels in this version. Safe-position suggestions are not a universal encounter solver. Preparation time depends on the guide, selected model and hardware; the first model download is several gigabytes. Combat pauses inference.
