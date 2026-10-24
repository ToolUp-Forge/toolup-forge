# Phase 1113 — a fact series read serves numbers over a period

**What changes for you: nothing, unless you call it.** `IFactBrowseApi` gains one read, `QuerySeries`,
mounted by the same `FactBrowseHandler.withFactBrowse` that mounts the rest of the browse surface (Phase 895).
A deployment that already composes fact browse serves the new route at `/api/_facts/browse/QuerySeries`
with no further change; one that does not compose it mounts nothing new.

**What breaks, and for whom.** `IFactBrowseApi` is a record, so a full-literal construction of it — a
test fake, a stub for a client built without a server — stops compiling (FS0764) until it carries the
new member:

```diff
 let fakeApi: IFactBrowseApi = {
     ListTables = …
     GetTable = …
     ListRuns = …
     GetRunMovers = …
     QueryRows = …
     GetFact = …
+    QuerySeries = fun _ -> async { return Error "not in this fake" }
 }
```

A client that builds its proxy with `Remoting.buildProxy<IFactBrowseApi>` needs no change: the proxy
gains the member with the type.

## What the read is

A chart needs one metric's value for each subject in a branch at each period across a range, as a
number. The ranked drill-down (`QueryRows`) serves a rendering of each subject's current head and takes
no period range, so a consumer that wanted a deterministic chart of its own facts had no forge read to
build it from — and reading its own stores would bypass the disclosure gate the facts are published
under. `QuerySeries` is that read and nothing else: forge serves the data, and the consumer draws the
chart in whatever UI tier it uses. It adds no chart code to forge.

```fsharp skip=fragment
let! series =
    api.QuerySeries {
        TableId = "trade.site"
        Metric = "revenue"
        SubjectPrefix = []                       // every subject of the table …
        SubjectLevel = None                      // … at the table's declared level
        PeriodFrom = DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc)
        PeriodTo = DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc)
        MaxPoints = FactBrowseApi.MaxSeriesPoints
    }
// series.Points: one FactSeriesPoint per (subject, period), ordered by subject path then
// period start — Value: decimal option, Rendering: string option, FactId, PeriodFrom/To,
// PeriodLabel, AsOf, Freshness, Withheld.
```

- **One fact per point.** A point is the current head the store holds for that subject and period —
  the head for *that* period, not the table's latest run's head. A cell a later run rewrote shows the
  revision; a period a later slice-scoped run left alone keeps the value the earlier run wrote. A
  superseded fact is never a point: it is on the head's supersession chain through `GetFact`.
- **Period membership is overlap.** A fact is in range when its period overlaps `[PeriodFrom, PeriodTo)`,
  so an aligned grain gives exactly the periods inside the window and a period straddling the edge is
  still drawn.
- **Level and prefix filter; nothing aggregates.** `SubjectLevel` names a level of the table's hierarchy
  (`None` is the table's own) and selects facts whose subject path sits at that depth. A level with no
  published facts is an empty series. Every number served is a published fact.
- **Under the same gate.** Every point passes `IFactDisclosureGate` at `FactBrowse`, exactly as a
  drill-down row does. A withheld point keeps its subject, its period and its fact id and carries no
  value and no rendering, with `Withheld = true`, so a chart can show a gap with its reason rather than a
  silently shorter line. `WithheldCount` and `Withheld` (grouped by policy) say how many. The result
  carries no aggregate — no minimum, maximum or mean — so there is no magnitude to reconstruct.
- **Bounded by contract.** `FactBrowseApi.MaxSeriesPoints` (5,000) is the most points one response
  carries. `MaxPoints` asks for a lower cap and is clamped into `[1, MaxSeriesPoints]`; a series that
  would exceed the cap in force is refused with an error naming the count, the cap and the contract's
  ceiling. There is no paging across subjects and periods: narrow the range or the prefix.

**Verify.** With fact browse composed and a table refreshed, call `QuerySeries` for one of its scalar
columns over the refreshed period: each subject's points carry the numbers the drill-down renders, in
subject-then-period order, and `GetFact` on any point's id opens the fact. Deny one fact at the
`FactBrowse` surface and the point stays in place, marked withheld, with no value.

**Rollback.** None needed: the read is additive. A consumer that does not call it is unaffected.
