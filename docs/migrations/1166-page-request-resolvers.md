# Page request resolvers — the data a stored page renders, under the reader's gate

**Ships in:** ToolUp.Facts.Presentation, ToolUp.Facts.Presentation.Server and ToolUp.Facts.Server 0.38.0. Rides the
0.38.0 draft. The class here is additive: new types and functions, and nothing existing changes shape.

**Affected:** nobody until they opt in. A deployment that renders stored pages composes the resolvers with one call.

## What changes

1. **The request a stored page names** (`PageRequests.fs`, ToolUp.Facts.Presentation, both compilers):
   `PageRequest = Presentation of PresentationRequest * dependsOn | Pipeline of PipelineBlockRequest * dependsOn`.
   The pipeline request is the one a pipeline block part already persists. `dependsOn` names, by key, the requests
   whose outputs this one reads.
2. **Its canonical JSON is the contract.** A page tier that knows nothing about either kind carries a request as an
   opaque payload under a kind name, with its `dependsOn` keys. The canonical text is
   `{"dependsOn":[…],"kind":"presentation"|"pipeline","payload":{…}}`, written by the cross-host canonical writer
   (`Canonical.write Rendering.CrossHost`: members sorted Ordinal, no whitespace, the pinned float layout), with
   `dependsOn` sorted, distinct and omitted when empty. The presentation payload is `PresentationRequest.toJsonValue`.
   The pipeline payload is the evaluate_pipeline argument shape (`table`, `period_from`, `period_to`, `path_prefix`,
   `metrics`, `pipeline`, `pipeline_hash`, and `run_stamp` when recorded), so the server reads it with the tool's own
   parser. One request has one text: `PageRequest.toJson` / `ofJson` / `toJVal` / `ofJVal`.
3. **A resolution is data, never a node.** `PageResolution` carries the rows as a `Fuaran.Core.Column` table in its
   canonical codec (`PageResolution.table` decodes it), the row count, the column names, what the gate withheld
   counted by policy, the notes naming each point not shown, the fact ids read and, for a pipeline, the run stamp.
   This is the value a `Binding.Query` source binds. A withheld point is absent from the rows, never zero: a chart's
   row leaves it out, a table's cell and a pipeline's withheld column are null. A request whose every point is withheld
   is a refusal naming the count, never an empty table. `PageResolution.toJson` is its canonical text.
4. **`Presentation.data`** exposes the builders' series-to-table step. `Presentation.build` draws from the same step,
   so a page and a built block draw the same rows.
5. **The resolver** (`PageRequestResolver`, ToolUp.Facts.Presentation.Server). It is created for one caller: the
   scope the platform resolved for the request and its principal. A presentation request is answered through the
   fact browse's series read and `Presentation.data`. A pipeline request is answered through the pipeline tool's
   re-run over the gated slice (`PipelineAnsweringTool.rerun`). Both are judged at the `FactBrowse` door. Its values are
   `Resolve`, `ResolvePresentation`, `ResolvePipeline`, and `ByKind kind`, a function from the canonical payload to
   the canonical resolution. `PageRequestResolver.byKind` gives the map by kind that a stored-page view takes. A
   client page and a server-rendered report are handed the same values, so one request resolves to the same bytes
   through either. A refusal is a `PageRefusal` typed by cause, with `PageRefusal.cause` as a stable name: malformed
   request, unknown kind, unknown table, withheld, no facts, budget, read refused, evaluation, no fact store, or
   defect.
6. **The per-session cache** (`PageResolutionCache`, one per process). A caller's session keeps a resolution under
   the request's canonical hash, without its `dependsOn` keys, and the gate fingerprint
   (`PageGateViewer.fingerprint`: principal, scope, active team, platform-admin flag, team role and the team's output
   level). Nothing is shared between callers, and a viewer whose gate facts moved misses. It is dropped when the
   fact-table change signal reports its table changed.
7. **The fact-table change signal** (`IFactTableChangeSignal`, ToolUp.Facts.Server). A committed run raises its table,
   through the writer `FactsCompose.withFactTableWriter` composes. A data-object arrival the fact tier reacts to
   raises every table of its scope. Other processes hear a raise through the composed notification channel; with the
   no-op channel each process hears only its own writes. `FactsCompose.withFactStore` composes it dormant: nothing is
   raised, and the data-change decorator is not armed for it, until a reader registers its demand
   (`FactTableChangeSignal.demand`). The fact tier gains no presentation type.
8. **`PresentationCompose.withPageRequests`** registers `PageRequestResolvers`, `PageResolutionCache` and the demand,
   only under `EnabledFactStore`. Under `NoFactStore` the app is returned unchanged.

The resolver lives in ToolUp.Facts.Presentation.Server, beside the render tools, and not in the fact tier.
ToolUp.Facts.Server stays free of the Fuaran-UI dependency, as Phase 1132 decided and its tests pin.

## What to do

Compose it after the fact store, and resolve through the caller's resolver:

```fsharp skip=fragment
ServerApp.empty
|> FactsCompose.withFactStore
|> FactsCompose.withFactTableWriter
|> PresentationCompose.withPageRequests
|> ServerApp.run

// In a handler: the resolver for this request's caller.
let! resolver = ctx.RequestServices.GetRequiredService<PageRequestResolvers>().ForRequest ctx

match resolver with
| Ok resolver ->
    // Hand the same map to the client page and to the server-rendered report.
    let resolvers = PageRequestResolver.byKind resolver
    let! answer = resolvers.[PageRequest.PresentationKind] payloadJson
    ()
| Error refusal -> PageRefusal.describe refusal |> ignore
```

A page tier that takes no forge dependency receives `PageRequestResolver.byKind resolver`, functions from the
canonical payload text to the canonical resolution text, and decodes the resolution's `table` member with
`ColumnCodec.decode`.

## Verify

- `ToolUp.Platform.Tests`, list "page request resolvers": the canonical form, withheld points absent and counted for
  a trend, a table and a pipeline, refusals by cause, equal bytes through the typed and canonical entry points, the
  cache's hit, invalidation and isolation, and composition only under a fact store.
- The Phase 1132 test "the fact tier stays free of the Fuaran-UI dependency" stays green.

## Rollback

Remove the `withPageRequests` call. The signal goes dormant again, and nothing else changes.
