# Manual Wreath of Snakes analysis — 0.13.4

## Reproduction

The user reported a 226-second manual preparation failure outside an instance, using Qwen 3.5 4B, Vulkan, 64K context and a 12 GiB process-memory limit. The previous plugin did not persist its model exchanges, so that original conversation cannot be reconstructed.

Reproduced with the user's validated `C637-T824.json` source cache copied to the workspace, without refreshing its source pages or modifying installed plugin/model caches. The cached duty is the Wreath of Snakes (Seiryu); inputs include Console Games Wiki and the community workbook, 28,164 assembled characters. The user authorized a hidden local GPU test while the game was idle.

Before the correction, the instrumented run failed after 167.8 seconds (seven model requests). All completions finished normally. The failures were validation errors in optional phase definitions and memberships: citations omitted the phase label or referenced a label absent from the cited paragraph. Each failure triggered another full draft and review. Increasing context or RAM was not the remedy for this reproduced error.

Replaying the exact captured model outputs with the corrected pipeline produced one boss and 21 mechanics in three recorded exchanges without inference. A second real Qwen/Vulkan run with the same sources and settings then completed successfully in 73.2 seconds, again with three requests and 21 mechanics. Request-level totals: 18,721 input tokens and 8,124 output tokens. This is one local measurement, not a general latency guarantee or an in-game strategy acceptance test.

## Phase fallback

Only optional phase-filtering metadata is removed when its grounding is invalid. All mechanics remain in the canonical boss list; instructions, conditions, exact trigger names, source evidence and duplicate checks still pass through the existing validators. Valid phase metadata is unchanged. Cache validation remains strict; existing prepared guides are not invalidated by a model-revision bump. Each removed phase filter is explained in the diagnostic event log.

## Diagnostics

Local AI → Analysis log provides recent analyses, per-request system/user messages and JSON request parameters, responses (including incomplete/malformed failures), available token usage, duration, attempts, validation events and bounded engine output. The detailed window is resizable and only opened explicitly. Normal preparation UI shows the current step and a concise failure reason.

Reports persist under `foretell-guide-summaries/diagnostics`, independently of combat/instance capture. A standalone ZIP can be exported from the report window; it includes the source document when conversation recording was enabled. Conversation recording defaults to enabled and is adjustable for subsequent analyses. It does not retroactively recover previous exchanges.

Retention is limited to six reports of at most 32 MiB each and six corresponding exports. Over-limit content is explicitly marked omitted and does not alter the source sent to the model. Engine output is a marked rolling tail; loopback authorization secrets are redacted. Disk errors remain visible in the in-memory report. Partial writes are cleaned up, stale temporary files are pruned, and a temporary-file cap prevents accumulating failed recordings.

Transport diagnostics include bounded non-success HTTP bodies, endpoints/status codes, incomplete completion content, JSON errors and caller cancellation distinct from timeouts. The four-minute request deadline now covers response body consumption as well as headers.

## Validation

Automated coverage includes transcript persistence/reload and standalone export; opt-out, malformed stored reports, quotas, recovered disk failures and failure isolation; fake HTTP responses, token usage, truncation, partial-body timeouts and secret redaction; conservative phase fallback without bypassing mechanic validation; existing guide, lifecycle, cache, core and overlay suites. All command-line runs use the noninteractive test host. No game window is opened or controlled.
