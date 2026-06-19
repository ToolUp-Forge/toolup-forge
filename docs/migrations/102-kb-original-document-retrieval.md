# Phases 102 / 103 / 104 / 106 / 107 — KB original-document retrieval cluster

Forge commits `7943a70` (Platform.Core lineage types + audit substrate) + `aa1138e` (KnowledgeBase surface + resolver + audit emission). Fully **additive and opt-in** — existing consumers compile and run byte-for-byte unchanged until they build against the new surface.

## What changes

A citation can now be opened at its source. Five additive pieces:

- **`KnowledgeApi.GetOriginalDocument: string -> Async<Result<OriginalDocument, KnowledgeBaseError>>`** (Phase 102) — fetch the original ingested bytes for a `KnowledgeDocument` id. Scope-gated structurally: the lookup runs against the caller's resolved scope only; an out-of-scope id returns `Error NotInScope` (indistinguishable from nonexistent — no existence oracle) with a denial audit.
- **`IOriginalSourceResolver`** (Phase 104) — per-`KnowledgeSource` resolution: `UploadedFile` → raw blob + MIME content type; `Note` → `note.md` as `text/markdown`; `FromNarrative` → `Error NoOriginalAvailable` (synthetic — its home is the producing module's page). Swap via `KnowledgeBase.Server.withOriginalSourceResolver` on the base `ServerApp`.
- **`RetrievedSource.OriginalRef: OriginalDocumentRef option`** (Phase 103) — citations advertise the fetchable original (id / name / type / size). Stamped as structured `_originalRef` chunk metadata at upload-ingestion; `None` for note / narrative / AI-context chunks **and for chunks ingested before this release** (re-ingest to backfill).
- **`OriginalDocumentRef.Location: SourceLocator option`** (Phase 106) — neutral page / slide / sheet / section / row-group locator so a Sources panel can deep-link the cited spot.
- **`KnowledgeOriginalRetrieved` / `KnowledgeOriginalRetrievalDenied` audit events** (Phase 107) — every fetch and every refusal lands in the `IAuditLog` trail (identifiers + source kind only, no content).

## Diff to apply (consumers, opt-in)

No required change. To add a "view original" affordance in a Sources panel:

```fsharp
// Client — RetrievedSource now carries the handle:
match source.OriginalRef with
| Some r -> // render affordance; fetch via the KnowledgeApi proxy
    let! result = knowledgeApi.GetOriginalDocument r.DocumentId
    // r.Location : SourceLocator option → deep-link/scroll target
| None -> () // note / narrative / pre-103 chunk — no affordance
```

Server side, only if custom resolution is wanted:

```fsharp
serverApp |> KnowledgeBase.Server.withOriginalSourceResolver myResolver
```

## Verification

- `dotnet build` — unchanged consumers compile without edits (additive record field + API method).
- Persisted conversations / wire payloads from before this release deserialise with `OriginalRef = None` (pinned by `KnowledgeOriginalRetrievalTests` "pre-OriginalRef wire payloads…").
- Out-of-scope fetch returns `NotInScope` and a `KnowledgeOriginalRetrievalDenied` audit row.

## Client tail shipped (view-original affordance)

The server substrate above landed without any client affordance, so the capability was invisible end-user-side. The client tail now ships it in the SDK's citation/Sources surface — **purely additive, opt-in by data** (a citation with `OriginalRef = None` renders exactly as before, GP 11 / GP 13):

- **AI Sources panel** (`ToolUp.AI.Client/Client/ConversationPanel.fs`) — each retrieved source whose `OriginalRef` is `Some r` gets a `ViewOriginalButton` (name / type / size in its tooltip). Fetch status (`Idle` / `Loading` / `Failed msg`) lives in React-local state; the fetch runs in the click handler, never in render.
- **Cross-companion bridge** (`ToolUp.Platform.Client/Client/OriginalDocumentBridge.fs`) — the AI client must not import KnowledgeBase types, so the opener is brokered through `ToolUp.Platform` exactly as `NarrativeCommit` brokers the reverse direction. The Sources panel reads `OriginalDocumentBridge.current ()`; a deployment with no Knowledge Base composed in registers no opener and the affordance never renders.
- **KnowledgeBase opener** (`ToolUp.KnowledgeBase.Client/Client/ClientModel.fs`, registered in `ClientView.register`) — fetches via `knowledgeApi.GetOriginalDocument r.DocumentId`, opens the original in a new tab (PDF page locator honoured as a `#page=N` deep link; other `SourceLocator` kinds and popup-blocked tabs degrade to a plain open / download). `NotInScope` is reported generically ("not available") so the affordance can't be used as an existence oracle (GP 4); `NoOriginalAvailable` / `OriginalRetrievalFailed` surface benign inline messages (GP 9).

Consumers get the affordance automatically by bumping the SDK — no wiring change. **One-time re-ingest:** chunks ingested *before* Phase 103 carry no `_originalRef`, so their citations surface `OriginalRef = None` and show no button; re-ingest existing corpora to backfill the ref (new uploads already stamp it).

## Rollback

Revert `aa1138e` then `7943a70`. No data migration in either direction — the `_originalRef` metadata key is ignored by older readers, and documents ingested without it simply surface `OriginalRef = None`. The client tail is additive on top; reverting it removes the affordance and leaves the server substrate intact.
