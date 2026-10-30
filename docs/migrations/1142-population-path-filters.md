# A population query matches any of several branches, and leaves branches out: `PathAnyOf` and `PathExclude`

**Ships in:** ToolUp.Facts.Core, ToolUp.Facts.Server and ToolUp.FactStores.Postgres 0.38.0.

**Affected:** code that builds a `PopulationQuery` as a full record literal. That is a source break (FS0764: the
record gained two fields), and it is the only one. A query built from `PopulationQuery.create … with …`, which is the
documented form, compiles unchanged and behaves exactly as before. A third-party `IFactStore` implementation is held
to the two filters by the extended `IFactStoreContract` pack.

## What changes

1. **The record.** `PopulationQuery` gains two optional subject filters beside `PathPrefix`:
   - `PathAnyOf: string list list option` admits a subject whose path starts with any listed prefix. It AND-combines
     with `PathPrefix`. `Some []` lists no prefix and admits no subject.
   - `PathExclude: string list list option` drops a subject whose path starts with any listed prefix, after the
     inclusion clauses. An empty prefix `[]` in the list excludes every subject. `None` or `Some []` excludes nothing.
2. **Where they apply.** Both are clauses of `PopulationQuery.matchesSubject`, so every store applies them where it
   applies `PathPrefix`: before the ranking, the `TopK` ceiling and `PopulationStats`. A member's rank, the
   truncation and the statistics describe the matched population. `PopulationQuery.pathStartsWith` is the prefix test
   all three clauses share.
3. **The stores.** `BlobFactStore` through the shared predicate. `PostgresFactStore` as SQL predicates over the
   indexed path, so the read stays index-only over the covering population index. `DelegatedFactStore` translates the
   clauses into its table's one-prefix query through `PopulationQuery.asOnePrefix` (`OnePrefixPath`: `UnderPrefix`,
   `NoSubject`, `NotOnePrefix`). Where they cannot be stated as one prefix, it refuses and names the filter
   (`PathAnyOf` or `PathExclude`); it never answers over a wider population.
4. **The tool.** `query_metric_population` takes `path_any_of` and `path_exclude`, each an array of `>`-separated
   paths in the `path_prefix` form. It refuses by name a value that is not an array, an empty array, or an entry that
   is not a string or names no member id. The result gains `filters` (`level`, `pathPrefix`, `pathAnyOf`,
   `pathExclude`), echoing what was applied.

## What to do

A full record literal adds the two fields. This is the construction-site diff, applied to each of the five sites
in this repository (`AnswerPlanner`, `FactBrowseHandler`, `PopulationQueryTool`, and the coverage query that
`CoverageTool` and `CoverageNarrative` now share):

```diff
 let query: PopulationQuery = {
     Metric = MetricRef metric
     Hierarchy = hierarchy
     Level = level
     PathPrefix = prefix
+    PathAnyOf = None
+    PathExclude = None
     PeriodOverlaps = None
     Threshold = None
     Ordering = Descending
     TopK = 10
     AsOf = None
     Methods = CanonicalMethodOnly
 }
```

The cheaper fix, and the one that survives the next field, is the `create` form. It is what the coverage query
moved to:

```fsharp skip=fragment
let coverageQuery (metricId: string) (hierarchyId: string) : PopulationQuery = {
    PopulationQuery.create (MetricRef metricId) hierarchyId with
        Ordering = Descending
        TopK = probeK
        Methods = AllCompetingMethods
}
```

Asking for the new filters:

```fsharp skip=fragment
// "The top four countries outside the UK."
{ PopulationQuery.create (MetricRef "revenue") "geography" with
    Level = Some 1
    PathExclude = Some [ [ "uk" ] ]
    Ordering = Descending
    TopK = 4 }

// "These three sites."
{ PopulationQuery.create (MetricRef "footfall") "estate" with
    PathAnyOf = Some [ [ "north"; "leeds" ]; [ "north"; "york" ]; [ "south"; "bath" ] ] }
```

An `IFactStore` implementation that applies `PopulationQuery.matchesSubject` gets both for free. One that filters its
own way (an index, a SQL store) must apply them before ranking and before the statistics. Run `IFactStoreContract`
against it: the any-of, exclusion, exclusion-inside-an-inclusion, exclude-everything and ceiling cases hold it to
that.

## Verify

- `dotnet build ToolUp.Forge.sln` has no FS0764 left.
- The `IFactStore contract` lists pass for your store. For a delegated table, a filter its query cannot express is
  an `Error` naming the filter, not an `Ok`.

## Rollback

Revert to 0.37.0. Remove the two fields from any literal that added them. A query that relied on `PathExclude` or
`PathAnyOf` must then filter the ranking itself, with the rank, ceiling and statistics errors described above.
