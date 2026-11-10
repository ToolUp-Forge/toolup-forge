# 874 — Audit events are an open registry; Core stops naming its companions

## What changes

| Surface | Before | After |
|---|---|---|
| `AuditEvent` (Core) | a closed union of 206 cases naming Forms, the knowledge base, AI, the asset store, inter-platform peers, facts, RAG, passkeys and artefact signing | the platform's own cases, plus `Opaque of OpaqueAuditEvent` (`Kind` + `Payload` JSON): a companion's event, or a stored kind no registrant on this deployment declares |
| Companion events (35 kinds) | union cases with payload records in `Shared/Audit/*AuditPayloads.fs` | each companion's own module: `ToolUp.Forms.FormsAuditEvents`, `KnowledgeBase.ServerKnowledgeAuditEvents`, `ToolUp.AI.AIAuditEvents`, `ToolUp.AssetStore.AssetAuditEvents`, `ToolUp.InterPlatform.PeerAuditEvents`, `ToolUp.Facts.FactsAuditEvents`, `ToolUp.RAG.RagAuditEvents`, `ToolUp.AuthProviders.Passkey.PasskeyAuditEvents`, `ToolUp.ArtefactSigning.ArtefactAuditEvents` — one union, its payload records, its `AuditEventFamily` and an active pattern |
| Declaring a kind | edit Core's union, its codec row in `AuditLog.fs`, regenerate the Core baseline | `AuditEventFamily<'E>` (Core, `AuditEventRegistry.fs`): `Registrant`, `Kinds` (`AuditEventKindInfo`: wire id, registrant, optional `AuditSeverity`), `Encode` (one compiler-checked `match` answering kind and payload) and `Decode`; `AuditPayloadJson.write` / `read` (Server) is the one payload serialisation |
| Registering | — | `ServerApp.withAuditEvents (AuditEventFamily.registration family)` from the compose step; `run` builds `AuditEventRegistry` (DI singleton) and refuses a kind two registrants declare, naming both. Each companion above registers its own (`ArtefactSigning` has no compose step: a deployment composing a signer registers its family) |
| The codec (`AuditLog.fs`) | `auditEventCodecs`, scanned with `List.tryPick` per write | one `match` over the union (`serialiseAuditEvent`) and a decode table keyed by kind (`auditDecoderByType`); `AuditEventCodec` / `auditEventCodecs` deleted |
| A stored row of an unknown kind | `Error "unknown event type"`; the replicator counted it in `AuditEventDecodeFailed` | `Opaque` with its id and its payload (without the row's `_actor`); the trail reads it, the replicator forwards it, re-recording writes the same bytes |
| Archive / sink JSON of a whole event | `JsonSerializer.Serialize(event, PlatformJson.options)` | `AuditLog.AuditEventJson.options`, which writes an `Opaque` event in the historical shape `{"<Kind>": payload}`; every shipped sink uses it. **A reader of an archive or a ledger deserialises with these options** |

The persisted bytes of every existing kind are unchanged: the stored row, the row with its `_actor`, and the archive line.
`src/ToolUp.Platform.Tests/fixtures/audit-wire-corpus/` holds one fixture per kind, captured before the change.

## The diff, per shape

```fsharp
do! AuditRow.emit log scope actor (AuditEvent.FormSubmitted payload)                 // before
do! AuditRow.emit log scope actor (FormsAuditEvent.event (FormSubmitted payload))     // after

match evt with | WorkflowTransitioned p -> Some p | _ -> None                          // before
match evt with | FormsAudit(WorkflowTransitioned p) -> Some p | _ -> None              // after (or FormsAuditEvent.tryRead evt)

match audit with ... (every platform case)                                             // before: exhaustive
match audit with ... | Opaque o -> handleByKind o.Kind o.Payload                       // after: one arm more

let readArchive (line: string) = JsonSerializer.Deserialize<ArchiveLine>(line, PlatformJson.options)       // before
let readArchive (line: string) = JsonSerializer.Deserialize<ArchiveLine>(line, AuditLog.AuditEventJson.options) // after
```

A module recording its own kind (the research-ethics-review-board recipe's `EthicsDecided`):

```fsharp
type EthicsReviewAuditEvent = EthicsDecided of EthicsDecidedPayload
let family: AuditEventFamily<EthicsReviewAuditEvent> = {
    Registrant = "research-ethics-review-board"
    Kinds = [ { Kind = "EthicsDecided"; Registrant = "research-ethics-review-board"; Severity = Some AuditMedium } ]
    Encode = function EthicsDecided p -> "EthicsDecided", AuditPayloadJson.write p
    Decode = fun kind json -> match kind with "EthicsDecided" -> EthicsDecided(AuditPayloadJson.read json) | k -> failwithf "%s" k }
app |> ServerApp.withAuditEvents (AuditEventFamily.registration family)
do! AuditRow.emit log scope actor (AuditEventFamily.event family (EthicsDecided payload))
```

`dotnet run --project Build.fsproj -- Codemod --phase 874` reports every line naming a moved case (`874-moved-audit-case`)
and every archive read through `PlatformJson.options` (`874-archive-read`); neither is rewritten.

## Verify

`dotnet build ToolUp.Forge.sln`; the Platform pack's `Phase 114` list (the corpus round-trips every kind's bytes, through the
platform codec or the one companion family that declares it, and a module registers and round-trips its own kind).

## Rollback

Revert the phase's commits. Rows written meanwhile are byte-identical to the pre-874 shape, so nothing at rest migrates.
