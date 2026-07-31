# Data subject requests (GDPR Article 15 / 17)

The Platform's persistence model — versioned `IDataObjectStore`, append-only
`IEventStore`, lineage links, audit retention — is built for *"what
happened"* requirements. GDPR Article 17 ("erase my data") and CCPA/DPDPA
equivalents are the opposite requirement. This page documents the bridge:
the **data-subject-request (DSR)** substrate, the **erasure-policy choice
tree**, what each policy preserves and breaks per store, and where the
SDK's responsibility ends and the deploying organisation's begins.

> **Responsibility boundary.** The SDK provides the *tools* — export and
> erasure across every store, opt-in, scope-isolated, auditable. The
> deploying organisation chooses the **policy** per legal review and
> **accepts liability** for that choice. The SDK cannot know your
> jurisdiction, your retention obligations, or whether a given subject's
> request is legally valid. Nothing here is legal advice.

## Opt-in — you pay nothing if you don't need it

DSR is off by default. Nothing is registered, no admin module is injected,
no endpoint exists:

```fsharp
type DataSubjectRequestMode =
    | Disabled                       // default
    | Enabled of policy: ErasurePolicy
```

Set `ServerConfig.DataSubjectRequests = DataSubjectRequestMode.Enabled
ErasurePolicy.Tombstone` to turn it on. The chosen policy is the
deployment default; an admin may override it per request (e.g. a default
of `Tombstone` with a specific verified Article 17 demand forced to
`HardDelete`).

## The three policies

```fsharp
type ErasurePolicy =
    | HardDelete           // remove the row entirely
    | Tombstone            // keep shape + chain, redact identifying content
    | RetainPerCompliance  // keep audit/event records; redact where possible
```

### Choice tree

```
Does a law in your jurisdiction REQUIRE you to retain audit/event
history (financial services SR 11-7, SOX, HIPAA audit, etc.) even
in the face of an erasure request?
│
├─ YES ──────────────────────────────────────────► RetainPerCompliance
│       Audit + event stores refuse erasure and record the refusal;
│       everything else redacts identifying content where it can.
│
└─ NO
   │
   Do you need the *fact that an erasure happened* to remain
   discoverable (most GDPR / CCPA / DPDPA deployments — auditors
   want to see "a subject was erased on date X"), and do you
   need version chains / lineage to stay structurally intact?
   │
   ├─ YES ───────────────────────────────────────► Tombstone   (default)
   │       Identifying fields become `*ERASED*`; record shape,
   │       version numbers, and lineage edges survive.
   │
   └─ NO — you have no compliance-driven retention at all
           (dev, trial-account, ephemeral workflows) ─► HardDelete
           Rows are physically removed. Breaks event-log
           integrity for the subject. Smallest residual data.
```

`Tombstone` is the default because it satisfies the most common regime:
audit retention is bounded, but the deletion itself must be auditable.

## Per-store behaviour matrix

Every persistent store the SDK ships implements an `Erase` surface that
interprets the policy in terms of its own semantics. "Names the subject"
is, unless noted, a substring match of the subject's `userId` within the
record (the SDK has no schema knowledge of module payloads — declared
precision, not a structured query).

| Store | `HardDelete` | `Tombstone` | `RetainPerCompliance` |
|---|---|---|---|
| `IEventStore` | matching events removed | envelope kept (`Id`/`OccurredAt`/`Type`), whole `Payload` → `*ERASED*` | **refuses** — event log is the "what happened" record |
| `IDataObjectStore` | all versions deleted + content GC'd (bypasses `StrictlyVersioned`) | every version sidecar redacted (`CreatedBy` + matching metadata), content repointed to a tombstone blob, chain preserved | redact `CreatedBy` + matching metadata only; **content + chain retained** |
| `ILineageStore` | link events removed (edges vanish) | link events whole-payload tombstoned (edge drops from traversal) — field-level "edge survives, ids tombstoned" is a tracked follow-up | **refuses** — lineage is provenance/audit fabric |
| `IConfigStore` | config document deleted (falls back to schema defaults) | matching field values → JSON-encoded `*ERASED*`, keys + shape kept | same as `Tombstone` (config is operational settings, not audit) |
| `IFeatureFlagStore` | matching flag entries dropped | matching flag value → `Variant([], "*ERASED*")`, key kept | same as `Tombstone` |
| `IBlobStorage` | matching blobs deleted (prefix-scoped) | each blob's content overwritten with `*ERASED*`, key kept | same as `Tombstone` (raw bytes are opaque) |
| `IVectorStore` | matching chunks soft-deleted then vacuumed (physically purged) | matching chunks soft-deleted (filtered from retrieval, purged at vacuum cadence) | same as `Tombstone` (derived KB content) |
| embedding cache | flushed | flushed | flushed (content-hash-keyed — cannot target a subject; full flush is the privacy-correct response) |

### Why only the event / lineage stores refuse `RetainPerCompliance`

`RetainPerCompliance` exists for jurisdictions where audit-trail retention
law *overrides* an Article 17 request. The event store (and lineage,
which is projected from it) is that audit trail — under this policy it
records the refusal and the run continues. Every other store holds the
subject's *content*, not the audit of what happened, so under the same
policy it still redacts the identifying fields it can structurally see.

### Scope isolation (non-negotiable)

Every `Erase` is scoped. If a subject is a member of teams T1 and T2,
erasing in T1's scope never touches T2's data even though the same
`userId` exists in both. Scope isolation is structural (scope-derived
container / blob prefix), the same trust boundary as every other
store operation. Team A's request can never reach Team B.

## Export (Article 15)

`RequestExport(userId)` streams a single archive: every registered
`IDataExporter`'s contribution, concatenated alphabetically by name for
deterministic bytes. Each store that holds the subject's *records* ships
an exporter (events, data objects). Stores that hold derived or
operational data (lineage, config, feature flags, raw blob trees, vector
chunks) do not ship a separate exporter — their content is either already
covered by the owning record store's export or is not an Article-15
"copy of your data".

Export is scope-isolated the same way erasure is: T1's export and T2's
export are separate archives.

## Composition — the extension-point pattern

The orchestrator (`ErasurePipeline`) never names a concrete store. Stores
opt into DSR at compose time via two extension points:

```fsharp
type IDataExporter =
    abstract Name: string
    abstract Export: scopeId: string * subjectUserId: string -> Async<ExportSegment list>

type IErasureHandler =
    abstract Name: string
    abstract Erase:    scopeId: string * subjectUserId: string * policy: ErasurePolicy -> Async<Result<ErasureSummary, ErasureError>>
    abstract Preview:  scopeId: string * subjectUserId: string * policy: ErasurePolicy -> Async<ErasureSummary>
```

Each store ships a thin adapter with a compose-time helper, e.g.:

```fsharp skip=fragment
let exporters = [ EventStoreErasureHandler.exporter eventStore
                  DataObjectStoreErasureHandler.exporter dataObjectStore ]

let handlers  = [ EventStoreErasureHandler.erasureHandler eventStore
                  DataObjectStoreErasureHandler.erasureHandler dataObjectStore
                  LineageStoreErasureHandler.erasureHandler lineageStore
                  ConfigStoreErasureHandler.erasureHandler configStore
                  FeatureFlagStoreErasureHandler.erasureHandler flagStore
                  BlobStorageErasureHandler.erasureHandler blobStorage
                  VectorStoreErasureHandler.erasureHandler vectorStore embeddingCache ]
```

A deployment registers exactly the handlers for the stores it runs;
registration is append-only and requires no edit to the composition root.

## Two-phase commit + audit

Erasure is preview → confirm. `PreviewErasure` runs every handler's
`Preview` (no mutation) and returns the per-handler affected count for
admin review; `ConfirmErasure` runs the actual erasure. The orchestrator
emits `RequestStarted` / `PreviewCompleted` / `ErasureCompleted` /
`ErasureFailed` audit events; a handler that refuses (`HandlerRefused`,
e.g. the event store under `RetainPerCompliance`) records the refusal and
does **not** abort the run — other stores still erase. A
`StoreUnreachable` failure is retried per the caller's `RetryPolicy`; a
`HandlerPartialFailure` marks the run resumable.

Ordering note: handlers run in stable name order in the MVP. Deployments
needing strict ordering (descendants-before-ancestors per lineage, audit
last) register handler names that sort accordingly (`01-events`,
`02-data`, …, `99-audit`).

## Portability

Every `Erase` method passes the six-rule portability audit
(see [`portability-rules.md`](portability-rules.md)): identity by value,
async at the boundary, failure as `ErasureError` data (not exceptions or
callbacks), stateless between calls, single-scope (no cross-shard
ordering), and declared match precision.

## See also

- [`../security/PLATFORM-SECURITY-RULES.md`](../security/PLATFORM-SECURITY-RULES.md)
  — §6 states the data-subject rules in procurement-facing form, with an
  evidence pointer per rule and the known limitations (declared-precision
  matching, whole-payload lineage tombstoning) stated up front.
- [`events.md`](events.md) — the event store + audit replication this
  builds on.
- [`storage.md`](storage.md) — `IBlobStorage`, the substrate under most
  blob-backed stores.
- [`portability-rules.md`](portability-rules.md) — the six-rule audit
  every `Erase` method satisfies.
