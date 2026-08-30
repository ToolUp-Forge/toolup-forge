# Migration — Phase 723: ingestion scope-enumeration seam + async enqueue adoption

**Status:** additive. Two new opt-in surfaces plus one internal convergence. **No consumer action is required to upgrade** — a deployment that composes neither the enumerator nor a recovery sweep is byte-for-byte unchanged (GP 11 / GP 13).

## What changes

**1. `IScopeEnumerator` — the SDK can answer "what scopes exist" (new, opt-in).**
Nothing in the SDK could enumerate storage scopes, so every per-scope sweep took a hand-written container list from the consumer. That inverted who carried the risk: the deployments large enough to need a restart sweep are exactly the ones whose container list is longest, changes as teams are created, and goes stale silently — so the sweep that mattered most was the one least often wired.

`ToolUp.Platform.IScopeEnumerator` (`ToolUp.Platform.Server`) is a two-member seam: `Name` and `ListScopes : unit -> Async<string list>`. `ScopeEnumeration.fromTeamStore` is the SDK default; `ScopeEnumeration.ofScopes` is the fixed-set form.

**`ITeamStore` did NOT change.** The default is an *adapter* over the existing `ITeamStore.ListTeams`, not a new abstract member — adding a member to a shipped interface breaks every external implementation, and `ListTeams` already returns exactly the enumeration required. If you implement `ITeamStore` yourself, nothing about it moved.

**2. The restart-recovery sweep runs off the seam (new, opt-in).**
`RAGServerApp.withScopeEnumerator` composes an enumerator; the startup sweep then visits the enumerated scopes with no container list at all. `withIngestionRecoverySweep` still works and still means what it meant — the two **union**, so a deployment can migrate to the enumerator and drop its list without a coverage gap in between.

**3. The two sweeps converged onto one implementation (internal).**
`RAGServerApp.withIngestionRecoverySweep` and KB's `recoverStuckDocumentsAtStartup` were near-identical routines in two companions. The traversal, the per-scope error isolation, the reason string and the logging shape are now `ToolUp.Platform.IngestionRecoverySweep`, once; each companion supplies a small `IIngestionRecoverySurface` adapter naming its own durable surface. Both public entry points keep their signatures.

They did **not** become one traversal, and that is a finding rather than a shortfall: they read two genuinely different durable surfaces — KB's `knowledge/index.json` document index and RAG's per-file `IIngestionStatusStore` — and a KB deployment holds both. Sweeping only one would leave the other's badge stuck. A third surface is now an adapter, not a third sweep.

**4. The KB upload handlers await their enqueue (internal, behaviour-preserving).**
`UploadDocument` / `SetDocumentTags` / `AddNote` / `UpdateNote` / `IngestNarrative` called the synchronous `IngestionQueue.Enqueue`. On the in-memory default that is a lock-free channel write and free; on a queue backed by an `IIngestionQueueStore` it is `Async.RunSynchronously` over a store round-trip **taken on the request thread**. They now call `EnqueueAsync` — the shape the RAG post-save hook already used. Same acceptance semantics, same capacity refusal, same `Failed` status on rejection.

**Log-line note:** the sweep's log messages moved from `[RAGCompose] event=ingestion_recovery_*` / `[KnowledgeBase.Recovery]` to `[IngestionRecoverySweep] event=*`, and now name the surface. If you alert on those literals, update the pattern.

## Adoption

Two lines, both optional. Copy-pasteable into a composition root that already composes RAG:

```fsharp skip=fragment
open ToolUp.Platform

// BEFORE — a hand-written container list that has to be kept current,
// and a KB recovery hook called by hand with the same list.
let app =
    ragApp
    |> RAGServerApp.withIngestionRecoverySweep [ "_platform"; "team-alpha"; "team-beta" ]

// AFTER — the scopes are enumerated at every start, so a team created
// since the last one is swept on this one.
let app =
    ragApp
    |> RAGServerApp.withScopeEnumerator (ScopeEnumeration.fromTeamStore teamStore)
```

To enrol the KnowledgeBase document index in the same automatic sweep (previously: call `recoverStuckDocumentsAtStartup` yourself, before `RAGServerApp.run`):

```fsharp skip=fragment
// AFTER — declares that KB's index should be swept. The sweep itself is
// the hosted service composeWithRAG registers, so this composes in
// either order with withScopeEnumerator.
let app = app |> KnowledgeBase.Server.withIngestionRecovery
```

`withIngestionRecovery` on its own sweeps nothing: the hosted service exists only where the deployment also composed an enumerator or an explicit list. The declaration and the decision to run are deliberately separate — a surface registration says KB's index *should* be swept, never "start rewriting document statuses at the next restart".

**What the default enumerator cannot see, stated plainly.** Personal (`user-{id}`) containers are not enumerable from `ITeamStore` — there is no user directory in the SDK core to enumerate. A personal-mode deployment composes `ScopeEnumeration.ofScopes` or its own `IScopeEnumerator`. Archived teams **are** included: their documents are still stored, and a document left `Pending` in one is exactly as stuck as any other.

`recoverStuckDocumentsAtStartup (storage) (containers) (logger)` is unchanged and still supported for deployments that enumerate their own scopes.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — 0 failures.
- New coverage (`src/ToolUp.Platform.Tests/InProcess/ScopeEnumerationSweepTests.fs`, 20 cases): the default enumerator drives a fake `ITeamStore` **whose every other member raises**, which is the evidence that no member was added; a document left `Pending` by a simulated dead process is recovered at startup by a deployment that declared no container list; explicit scopes and the enumeration union; a throwing enumerator degrades to the explicit list rather than taking startup down; both surfaces are swept in one traversal with one reason string; the uncomposed path is asserted against a **recording** surface (with a positive control beside it) rather than a returned zero; and `EnqueueAsync` yields to its caller against a gated durable store where the synchronous `Enqueue` holds its thread — gated on a `TaskCompletionSource`, so neither arm depends on timing.

## Rollback

Everything is additive. To revert:

- Drop the `withScopeEnumerator` / `withIngestionRecovery` calls from your composition root — the sweep falls back to the explicit `withIngestionRecoverySweep` list, exactly as before.
- To revert the convergence in-tree, restore the inline sweep body in `RAGCompose.fs` (`composeWithRAG`, the Phase 509.C block) and the loop in `KnowledgeBase.ServerRecovery.recoverStuckDocumentsAtStartup`; `ToolUp.Platform.IngestionRecoverySweep` and `IScopeEnumerator` then have no callers and can be removed.
- The `EnqueueAsync` adoption is a one-line revert per call site; the synchronous `IngestionQueue.Enqueue` is unchanged and still shipped.
