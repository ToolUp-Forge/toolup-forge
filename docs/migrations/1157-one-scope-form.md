# 1157 — One scope form: `IFactStore` over `ResolvedScope`, and the typed read fault

**Breaking, 0.38.0.** Every `IFactStore` member takes a `ResolvedScope` and answers
`Result<_, FactStoreError>`; the seven `string` overloads are gone, and so are the string scope forms of the
fact tier's other seams. There is no adapter from a string: a `ResolvedScope` is minted by the platform's
scope resolution and nowhere else, and what crosses a persistence or wire boundary is a `ScopeKey`
(security rule TI-7).

## What changes

| Before | After |
|---|---|
| `store.Query(scopeId, q) : Async<Fact list>` | `store.Query(scope, q) : Async<Result<Fact list, FactStoreError>>` (the same for `Get`, `QueryWithCompetition`, `QuerySupersessionChain`, `QueryPopulation`) |
| `store.Assert(scopeId, d) : Async<Result<Fact, string>>` | `store.Assert(scope, d) : Async<Result<Fact, FactStoreError>>` (and `AssertBatch`) |
| a storage fault on a read: an absent fact, or a raise | `Error(StorageFault(operation, reason))` — never a shorter answer |
| `FactWrite.renderOffenders total offenders` | `FactStoreError.describe refused` (`FactWrite.prepare` answers one `MalformedDrafts`) |
| `IFactDisclosureGate.Check(scopeId, …)`, `IFactClausePlanner.PlanClauses(scopeId, …)`, `IAnswerPlanner.Plan(scopeId, …)`, `IDisclosureTaintIndexSource.ReadTaintIndex(scopeId)` | the `ResolvedScope` overload only |
| `IFactResolver.Resolve`, `IAnswerPlanner.Record`, `IPlannedAnswerRecorder.RecordPlanned`, `IFactEvidenceSource`, `IProvenanceGraph`, `INarrativeGroundingGate`, `IGroundingCertificateIssuer` (and the attested one), `IFactImportDoor.Import`, `IFactTableWriter` / `IFactTableOrphanSweep`, `IDelegatedFactWalks`, `INarrativeIngestor.Ingest`, `ProvenanceApiHandler.create`, `FactWebhookEgress.permittedFactIds`, `ReportApiHandler.applyExportDisclosure` / `createWithDisclosureGate` | take a `ResolvedScope` |
| `CoherenceConfig.HealthScope`; `withCoherenceChecks cadence`; `withCoherenceChecksConfig config cadence scopes` | `CoherenceConfig.HealthPrincipal : DeclaredPrincipal option`; `withCoherenceChecks cadence principal`; `withCoherenceChecksConfig config cadence principals` |
| `CoverageNarrativeOptions.CommitScope` / `sameContainer` / `forScopes principal storageScopes` | `CommitsIn` / `everyScope` / `forScopes principal scopeIds` (the narrative commits under the resolved scope's own container) |
| `PeerEgressRoute` | gains `Scope : ResolvedScope` (the scope its references are judged in) |
| `PublicationRead.latestNarrative` / `latestNarrativeIn` | the disclosure check runs in a `ResolvedScope` the caller resolved for the publishing scope; none, and a narrative citing facts is withheld |
| `RecomputeJobHandler.reactToResolvedChange` / `reactToChange`, `DataChangeScope` | one `reactToDataChange … (scope: ResolvedScope)` |

**Where a scope comes from.** On a request, `ScopeResolution.forRequest ctx`. In a job, `JobContext.Scope`,
which the runner re-mints from the job's sealed carrier; a fact-tier job registered under a shard that carries
no re-minted scope is refused by name (`carries no resolver-minted scope`), never run under its string — so
schedule it through `IJobScheduler.Schedule(scope, registration)`. For standing work the deployment declares, a
principal resolved through the platform's `DeclaredPrincipalResolver`.

## The rewrite

Run the codemod (rules `1157-*`). It rewrites what is mechanical and lists the rest:

```diff
-let! receipt = store.AssertBatch(scope.ScopeId, drafts)
+let! receipt = store.AssertBatch(scope, drafts)
-| Error offenders -> Error(FactWrite.renderOffenders (List.length drafts) offenders)
+| Error offenders -> Error(FactStoreError.describe offenders)
-let options = CoverageNarrativeOptions.sameContainer "coverage-reader"
+let options = CoverageNarrativeOptions.everyScope "coverage-reader"
```

`1157-string-scope-call` lists every fact-store call handed a string scope: there is no mechanical target,
because nothing turns a string into a resolved scope. Replace the string with the scope the caller resolved
(above), and read the answer:

```fsharp skip=fragment
match! store.Query(ScopeResolution.forRequest ctx, query) with
| Ok facts -> …
| Error fault -> … FactStoreError.describe fault …
// or, where the caller has no failure channel:
let! facts = store.Query(scope, query) |> FactStoreResult.orRaiseAsync
```

`1157-fact-store-implementer` lists every implementer of a fact-tier seam: delete the string members, keep
the `ResolvedScope` ones, and wrap a store read in `FactStoreResult.ofRead` so a fault is `StorageFault`.
`1157-coherence-scopes` lists the coherence opt-ins, which name declared principals now.

## Verifying

Run the `IFactStore` contract pack (`src/ToolUp.Platform.Tests/Contracts/IFactStoreContract.fs`) over your
store: every law runs over the resolved scope, and the read-fault cases assert `StorageFault`, never absence.
A consumer test suite reaches a resolved scope the way the platform pack does (`TestScope.resolved`), through
the resolver's internal mint under `InternalsVisibleTo`.

## Rollback

Pin the 0.37 packages. There is no compatibility shim: a string-scope fact read is the shape this release
exists to remove.
