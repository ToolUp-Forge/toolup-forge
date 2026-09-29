# Phase 895 — the fact browse surface

**What changes for you: nothing, unless you compose it.** A deployment that does not call
`FactBrowseHandler.withFactBrowse` on the server and does not add the fact browse module on the
client mounts no route, registers nothing, and renders every page exactly as before. The knowledge
base list and the conversation panel only offer their new links when the fact browse module is in
the shell's module list.

**What breaks, and for whom.** `FactEgressSurface` gains a case, `FactBrowse`, the way exports and
webhooks (Phase 564) and the federation door (Phase 642) were added. Code that matches the union
**exhaustively** gets an incomplete-match warning (an error under warnings-as-errors) and needs an
arm for it:

```diff
 match surface with
 | FactRetrieval -> …
 | FactToolResult -> …
 | FactNarrativePublication -> …
 | FactExport -> …
 | FactWebhook -> …
 | FactPeerEgress -> …
+| FactBrowse -> …
```

A disclosure policy resolver that maps surfaces to permissions sees the new surface by its canonical
name, `"Browse"`. A policy ref permitted "everywhere" by enumerating the six older surfaces is **not**
permitted at the browse door until you add it — deliberately: browsing is its own door, so a
restricted fact is not shown to a browsing user just because the model may see it. The pinned
vocabulary snapshot (`DisclosurePolicyRefSnapshot`) lists `"Browse"` and its version moves to
`895.1`; a composition tier that mirrors the snapshot updates its mirror in the same step.

## What the surface is

Three pages, summary first. The client never receives a population.

1. **Tables** — one row per declared fact table: owning module, metrics, subject level, row count,
   last refresh, freshness against the declared cadence.
2. **Table detail** — the declaration, a population summary, and the run history, one row per
   refresh with rows written, the validation outcome and the change summary (new, changed,
   unchanged, removed). A run's largest movers load on request, each one gated.
3. **Drill-down** — rank a table's metric column, top or bottom, optionally under a subject path
   prefix, one server page at a time; open any row's fact to see its table, the run that wrote it,
   its evidence and its supersession chain.

**Bounded by contract.** `FactBrowseApi.MaxPageSize` (50) is the most rows or runs any response
carries, and every page reports it. A larger request is clamped, never refused. The deepest rank a
page can reach is the population read's own ceiling (`PopulationQuery.MaxTopK`), reported as
`RankCeiling`.

**One query surface.** A drill-down page is the population read the assistant's
`query_metric_population` tool runs, with `TopK` at the end of the page, gated through the same
disclosure fold and then sliced — so a person and the model asking one question see one ranking.

**Its own egress door.** Every served fact passes `IFactDisclosureGate` at `FactBrowse`. A row the
viewer may not see is absent; its rank stays a gap; the page reports a count grouped by policy and
never a value, a subject or an id. The population magnitudes are withheld on the same condition the
tool withholds them.

**Per run, never per fact.** A committed run publishes one `CustomNotification` keyed
`FactBrowseLinks.RunCommittedNotificationKey` to its scope, carrying the run's counts. The browse
module refreshes on it. Nothing notifies per fact.

**No bulk extraction here.** Taking a whole table out of the deployment is an export through the
gated reporting path; the drill-down's empty state says so.

## Adopting it

The contract lives in a new Fable-safe package, `ToolUp.Facts.Shared`, which the server tier
(`ToolUp.Facts.Server`) and the new client companion (`ToolUp.Facts.Client`) both reference.

Server — after the fact store and the table writer:

```diff
 app
 |> FactsCompose.withFactStore
 |> FactsCompose.withFactTableWriter
+|> FactBrowseHandler.withFactBrowse
```

`withFactBrowse` mounts `IFactBrowseApi` at `/api/_facts/browse/*` and decorates the composed
`IFactTableWriter` so a committed run publishes its notification. Compose it after
`withFactTableWriter`; without a writer the surface still mounts and every table reads as never
refreshed. Under `NoFactStore` it returns the app unchanged.

Client — add the package reference and the module:

```diff
+<PackageReference Include="ToolUp.Facts.Client" />
```

```diff
 let modules =
     myModules
     |> KnowledgeBaseClientConfig.appendKnowledgeBaseModule kbMode
+    |> FactBrowseView.appendFactBrowseModule None
```

`appendFactBrowseModule` takes an optional `ModuleLabel` to rename the sidebar entry. The module's
id is `FactBrowseLinks.ModuleId` (`_facts.Browse`), outside any application's RBAC-managed names.

## The two links, and when they appear

- **Knowledge base list.** A coverage narrative (Phase 707) shows as one item with a fact-table
  badge naming its metric. The badge opens the tables that carry that metric, and the table itself
  when exactly one does.
- **Conversation panel.** A cited source that carries a fact id offers "Open fact row", which opens
  that fact's row and provenance on the drill-down page.

Both appear only when the fact browse module is composed, and both go through the cross-module event
bus (`FactBrowseLinks.OpenMetricTopic` / `OpenFactTopic`) and the shell's navigation request, so
neither the knowledge base client nor the assistant client depends on the fact client.

## Verifying

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 895"`
  runs the server pack: the page ceiling over a 100,000-subject table, the browse door, the ranking
  agreement with the population tool, runs and movers, provenance, and the per-run notification.
- `dotnet run --project Build.fsproj -- VerifyFable` transpiles the client companion and runs its
  model and page cases under Node.

## Rolling back

Remove the `withFactBrowse` call and the client module. Nothing is persisted by the surface itself:
the run records and facts it reads belong to the table writer and the fact store.
