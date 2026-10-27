# A fact query names a subject branch

**Ships in:** ToolUp.Facts.Server 0.35.0 (`FactQuery`, the new `SubjectBranch` record and module,
`FactQuery.admitsSubject`; `BlobFactStore`, `DelegatedFactStore`, `FactBrowseHandler`) and
ToolUp.FactStores.Postgres 0.35.0 (`PostgresFactStore`). A minor: under SemVer-on-0.x a record gaining a field
is breaking.

**Affected:** every consumer that builds a `FactQuery` as a FULL record literal — one that names every field
rather than starting from `FactQuery.all` (or another query) with `with`. A query built with `with` compiles and
behaves exactly as before, and every store answers it exactly as before. Every external `IFactStore`
implementation must honour the new clause; the contract pack (`IFactStoreContract.tests`) now checks it.

## What changes

1. **`FactQuery` gains `SubjectBranch: SubjectBranch option`** — a hierarchy, an optional path prefix and an
   optional depth, the shape `PopulationQuery` names its subject set in, decided by the same predicate
   (`SubjectBranch.matches` calls `PopulationQuery.matchesSubject`). It AND-combines with every other clause and is
   never exclusive with `Subject`: a subject outside the branch matches nothing. `None` admits every subject.
2. **A series read over one branch downloads that branch.** The fact browse's `QuerySeries` hands its prefix and
   level to the store as the branch, and the blob store's surface rows narrow by it before downloading. Measured
   on a one-brand chart of twenty: 1,050 downloads before and 100 after at 2,000 facts; 10,500 and 1,000 at 20,000.

## What to do

A full literal that stops compiling with FS0764 (`No assignment given for field 'SubjectBranch'`) needs one line:

```fsharp skip=fragment
let query: FactQuery = {
    Subject = Some subject
    SubjectBranch = None // new in 0.35.0: no branch, the read is unchanged
    Metric = Some metric
    PeriodOverlaps = None
    Method = None
    AsOf = None
    IncludeSuperseded = false
}
```

or start from `FactQuery.all` with `with`, which absorbs future fields. To read one branch:

```fsharp skip=fragment
let underBrand =
    { FactQuery.all with
        Metric = Some(MetricRef "revenue")
        SubjectBranch = Some { SubjectBranch.ofHierarchy "product" with PathPrefix = Some [ "acme" ] } }
```

An `IFactStore` implementation filters by `FactQuery.admitsSubject query` (both subject clauses at once), or
narrows by the branch itself, and runs `IFactStoreContract.tests` to confirm.

## Verifying

```powershell
dotnet ./src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Phase 1123"
```

## Rolling back

Pin 0.34.1. Nothing is persisted differently: the clause is a read filter only.
