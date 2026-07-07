# Phase 14r — Tool-aware RAG framing

**Ships in:** `ToolUp.RAG.Server` (`RAGCompose`, `RAGPromptBuilder`). **Additive / backward-compatible
— no consumer change required, and the improvement auto-applies.**

## What changes

The RAG retrieval framing is knowledge-base-first: it tells the model the search already ran, that
KB content is authoritative, and that an empty result means "found nothing — don't invent facts".
That is right for a KB-only deployment but backfires when the deployment *also* loads live-interface
tools (the `_platform.ui.*` inspection / mutation family, or any `ClientResident` tool): a question
like *"what filters do I currently have applied?"* has no KB answer, so a KB-first model reads the
empty block and refuses / speculates instead of calling the inspection tool.

Phase 14r makes the framing **tool-aware**, in the SDK, so consumers stop patching this at their own
system-prompt layer:

- `RAGCompose.composeRAG` derives a `RAGPromptBuilder.ToolFraming` from the deployment's aggregated
  `ServerApp.AITools` at compose time (`ToolFraming.fromTools`). A tool is *live-interface* when its
  `Location = ClientResident` or its name is in the `_platform.ui.*` family. Server-resident
  analytical tools (incl. the `_platform.ai.*` read family) are **not** live-interface — they read
  persisted data, so KB-first framing still applies to them.
- When live-interface tools are present **and** `GroundingMode = Preferred` (the default),
  `resolveFramingWithTools` appends `uiToolFramingCompanion` — one paragraph naming the capability by
  *purpose* ("interface inspection tool"), never by ID — after the KB framing. `Permissive` (no
  framing) and `StrictlyGrounded` (refuses on a miss by contract) are unaffected.
- The per-request empty-retrieval message is tool-aware: `withRetrievalToolAware`'s empty branch
  redirects to the inspection tool when live-interface tools are present, instead of the neutral
  "returned no relevant matches".

### New surface (all additive)

- `RAGPromptBuilder.ToolFraming` record (`HasLiveUiTools`) + `ToolFraming.none` / `ToolFraming.fromTools`.
- `RAGPromptBuilder.withRetrievalToolAware` — tool-aware sibling of `withRetrieval` (which is retained,
  unchanged, and now delegates with `ToolFraming.none`).
- `RAGCompose.uiToolFramingCompanion` + `RAGCompose.resolveFramingWithTools` (`resolveFraming` is
  retained, unchanged).

## How to adopt

**Nothing to do.** A deployment that has live-interface tools composed gets the improved framing
automatically on the new SDK version. A deployment with no such tools (or `GroundingMode ≠ Preferred`)
is byte-for-byte unchanged (GP 11).

The one optional cleanup: a consumer that hand-wrote a system-prompt nudge to route on-screen
questions to `_platform_ui_inspect_active_module` can **remove** it — the SDK framing now covers the
case. (A downstream consumer's `platformPromptPrefix` is the reference example; removal is optional
and does not change model behaviour.)

## Verification

- `dotnet build src/ToolUp.RAG.Server/ToolUp.RAG.Server.fsproj`.
- `dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Tool-aware"` — the Phase 14r
  pack: live-interface detection (UI / client-resident vs server-read), the `Preferred`-only
  companion (`Permissive` / `StrictlyGrounded` / no-UI-tools unchanged), and the tool-aware
  empty-retrieval redirect (historical wording preserved without UI tools).

## Rollback

No opt-in to remove. The behaviour only differs from pre-14r when a deployment has live-interface
tools **and** runs `GroundingMode.Preferred`; a consumer that wants the exact prior framing can set a
different grounding mode or supply its own `RetrievalFraming`. The back-compat `withRetrieval` and the
unchanged `resolveFraming` / `defaultRetrievalFraming` preserve every prior call site.
