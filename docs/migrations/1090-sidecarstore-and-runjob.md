# Sidecar records read through an index; platform jobs are one `RunJob` shape

**Ships in:** ToolUp.Platform.Core, ToolUp.Platform.Server and ToolUp.Facts.Server 0.38.0, the next minor after
the tagged 0.37.0. Breaking-class for code that called a removed helper or constructed a handler class directly;
nothing persisted changes shape.

**Affected:** code that built a job payload with one of the per-envelope serialisers, parsed one with a
`tryParse*`, classified a failure with an `isTerminal`, or constructed one of the eight handler classes with `new`.
A deployment that registers the handlers through their `create` functions and schedules them by name changes
nothing: every `create` keeps its signature and every handler name is byte-for-byte the same.

## What changes

1. **Model records read through `SidecarStore<'T>`.** The model registry, the evaluation runner and the promotion
   policy evaluator keep their records in a `SidecarStore` over the same `IDataObjectStore`, under the same data
   types, object ids and sidecar metadata. A read by an indexed key (an artifact's spec hash or dataset version, an
   evaluation's artifact, a fold outcome's run, a decision's artifact) reads an index and one object per match
   instead of listing the scope. The index is new data objects in the same scope (`toolup.sidecarindex`); it is
   built by the first read of each key — one-time, two listings of the scope — and needs no migration step. A
   store that does not implement `IConditionalDataObjectStore` is never indexed and reads by scanning, exactly as
   before.
2. **The job payload codec is `JobPayload`.** It is the same `FableConverters` JSON every handler already used, so
   a job queued before the upgrade parses unchanged.

   | Removed | Use |
   |---|---|
   | `ModelFitEnvelope.serialiseRequest` / `tryParseRequest` | `JobPayload.serialise` / `JobPayload.parse<FitRequest>` |
   | `ModelFitBatch.serialiseItem` / `tryParseItem` | `JobPayload.serialise` / `JobPayload.parse<FitBatchItemPayload>` |
   | `ModelScoringEnvelope.serialiseRequest` / `tryParseRequest` | `JobPayload.serialise` / `JobPayload.parse<ScoreJobRequest>` |
   | `ModelEvaluationEnvelope.serialiseRequest` / `tryParseRequest` | `JobPayload.serialise` / `JobPayload.parse<EvaluationRequest>` |
   | `ReevaluationSweepEnvelope.serialiseRequest` / `tryParseRequest` | `JobPayload.serialise` / `JobPayload.parse<ReevaluationSweepRequest>` |
   | `PromotionEvaluationEnvelope.serialiseRequest` / `tryParseRequest` | `JobPayload.serialise` / `JobPayload.parse<PromotionEvaluationRequest>` |
   | `DatasetRevintage.serialiseJobPayload` / `tryParseJobPayload` | `JobPayload.serialise` / `JobPayload.parse<DatasetRevintageJobPayload>` |
   | `ModelEvaluationEnvelope.isTerminal` | `ModelEvaluationJobHandler.failureOf` (`RunFailure.Terminal` is the terminal case) |
   | `PromotionEvaluationEnvelope.isTerminal` | `ModelPromotionJobHandler.failureOf` |

   `RecomputeJobHandler.payloadFor` is unchanged.
3. **The handler classes are gone; the modules stay.** `ModelFitJobHandler`, `ModelFitBatchItemJobHandler`,
   `ModelScoringJobHandler`, `ModelEvaluationJobHandler`, `VintageReevaluationJobHandler`,
   `ModelPromotionJobHandler`, `DatasetRevintageJobHandler` and `RecomputeJobHandler` were each a class and a module
   of the same name; each is now the module alone, with `create` (unchanged) and a new `job` returning the
   `RunJob<'Req>` value `create` wraps. F# source that calls `X.create` / `X.HandlerName` compiles unchanged; code
   that wrote `new X(...)` calls `X.create` instead. Each module keeps its compiled `XModule` name
   (`CompilationRepresentationFlags.ModuleSuffix`), so a binary that calls a module function still binds.
4. **New:** `RunKind` (Core) with `label` and `handlerName`; `RunJob<'Req>`, `RunFailure`, `JobPayload` and
   `RunJob.handler` / `ofPayload` / `toJobResult`; `SidecarStore<'T>`, `SidecarStoreSpec<'T>` and the
   `SidecarStore` helpers; `ConditionalDataObjectStore.isAtomic`.

## One step for a rolling upgrade

The index is complete only for records saved by 0.38.0. A 0.37.0 replica still writing while a 0.38.0 replica has
backfilled a key saves records the index does not list, and a read by that key would miss them. So once **no
0.37.0 replica is writing**, evict each indexed type's manifest in every scope that was written during the
overlap; the next read by each key backfills it again from a listing (one-time, two listings):

```fsharp skip=fragment
for dataType in [ "toolup.modelartifact"; "toolup.modelevaluation"; "toolup.modelevaluationfold"; "toolup.modelpromotiondecision" ] do
    let! _ = dataObjects.Evict(scopeId, SidecarStore.manifestObjectId dataType)
    ()
```

A deployment upgraded with every replica stopped, or whose scopes were not read between the first 0.38.0 replica
starting and the last 0.37.0 one stopping, has nothing to do: no key was backfilled while an old writer ran.

## Verify

Rebuild against 0.38.0. A job you schedule with `JobPayload.serialise` runs; one already queued runs. A
`QueryBySpecHash` over a scope with many artifacts lists the scope once (the backfill) and never again.

## Roll back

Pin 0.37.0. The index objects left in each scope are inert data objects of type `toolup.sidecarindex`; 0.37.0
neither reads nor removes them. Before returning to 0.38.0, evict the manifests as in the rolling-upgrade step
above — records 0.37.0 saved in the meantime are not in the index, and the eviction is what makes the next read
rebuild it.
