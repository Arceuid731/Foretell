# Foretell 0.13.4 — analysis logs and reliable phase fallback

- Open **Local AI → Analysis log** to inspect prompts, responses, request durations, tokens, retries, validation errors and engine messages. Recording is enabled by default and can be disabled.
- Keep recent logs across restarts, including manual preparation outside an instance. Export a standalone diagnostic ZIP directly from the log window.
- Show the current analysis step and a useful error message when preparation fails. The log window is resizable.
- Keep validated mechanics when the model supplies invalid optional phase references, instead of repeatedly rebuilding and rejecting the whole boss guide. Uncertain phase filtering is disabled; mechanic evidence and instructions still require validation.
- Preserve partial failed responses and HTTP errors in diagnostics; enforce request timeouts through the complete response read.

Existing prepared guides remain usable. Retry a previously failed analysis after updating.
