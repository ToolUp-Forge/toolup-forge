# Phase 897 — team-to-team fact publication

**What changes for you: nothing, unless you compose it.** A deployment that does not call
`FactsCompose.withFactPublication` registers nothing new, runs no new job and writes nothing new. One
that composes it but records no grant writes nothing anywhere.

**What breaks, and for whom.** `FactEgressSurface` gains a case, `FactTeamPublication`, the way the
browse door (Phase 895) and the federation door (Phase 642) were added. Code that matches the union
**exhaustively** gets an incomplete-match warning (an error under warnings-as-errors) and needs an arm
for it:

```diff
 match surface with
 | FactRetrieval -> …
 | FactToolResult -> …
 | FactNarrativePublication -> …
 | FactExport -> …
 | FactWebhook -> …
 | FactPeerEgress -> …
 | FactBrowse -> …
+| FactTeamPublication -> …
```

A disclosure policy that maps surfaces to permissions sees the new surface by its canonical name,
`"TeamPublication"`. A policy ref permitted "everywhere" by enumerating the older surfaces is **not**
permitted at the publication door until you add it — deliberately: publishing to another team is its
own door, so a restricted fact does not leave its team just because the model may see it. The pinned
vocabulary snapshot (`DisclosurePolicyRefSnapshot`) lists `"TeamPublication"` and its version moves to
`897.1`; a composition tier that mirrors the snapshot updates its mirror in the same step.

Everything else is additive: `ResolvedScopePair` in `ToolUp.Platform.Core`; the publication vocabulary
(`PublicationGrant`, `FactPublicationTarget`, `PublicationRefusal`, …) in `ToolUp.Facts.Core`; and
`IFactPublication`, `FactPublicationJobs` and `FactsCompose.withFactPublication` in
`ToolUp.Facts.Server`.

## Adopting it

After the fact store and the table writer:

```diff
 app
 |> FactsCompose.withFactStore
 |> FactsCompose.withFactTableWriter
+|> FactsCompose.withFactPublication
+    (FactPublicationConfig.create [ FactPublicationTarget.create "group-sales" "region" ])
```

Declare the consolidation table as an ordinary fact table whose hierarchy has the origin team as its
root level. Then, in requests resolved to each team: an owner proposes the grant, both owners consent,
the source's owner schedules the publication with `FactPublicationJobs.schedulePublication` under the
source's scope, and the target's owner schedules the refresh with `FactPublicationJobs.scheduleRefresh`
under the target's. See [the guide](../platform/fact-publication.md).

## Verifying

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 897"`

## Rolling back

Remove the `withFactPublication` call. Grants are held in process, so nothing about them persists. A
consolidation table keeps the rows it last received until it is refreshed without them or dropped.
