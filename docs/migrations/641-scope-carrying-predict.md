# Scope-carrying `Predict` — `IModelScoreProvider` receives a `ScoreContext`

**Ships in:** ToolUp.Platform.Server (`ToolUp.Platform.IModelScoreProvider`,
`ToolUp.Platform.ScoreContext`, `ToolUp.Platform.IModelSpecStore`,
`ToolUp.Platform.ModelScorer`).

**Who is affected:** anyone who **implements** `IModelScoreProvider`. Callers of
`IModelScorer.Score` are unaffected; so is every existing `ModelScorer.create`
call site.

## What changes

`IModelScoreProvider.Predict` took a bare `ArtifactRef`:

```fsharp
abstract Predict:
    artifact: ArtifactRef * schema: DatasetSchema * rows: DatasetRow list ->
        Async<Result<ScorePrediction, ScoreError>>
```

An `ArtifactRef` is an id, a content hash, and a byte length. It is
deliberately opaque to the platform (GP 1) — but it was opaque to the
*provider* too, because nothing on the call said **which scope's store to read
it from**. A provider composed once and shared across tenants therefore could
not fetch its own fitted parameters, and had no way to reach the model's
specification either. The first real consumer of the evaluation harness hit
exactly this and bypassed `ModelEvaluationRunner` altogether, which also cost
it the intermediate predictions vintage the scoring seam exists to produce.

`Predict` now receives a **`ScoreContext`** carrying that reference plus
everything needed to resolve the model:

```fsharp
abstract Predict:
    context: ScoreContext * schema: DatasetSchema * rows: DatasetRow list ->
        Async<Result<ScorePrediction, ScoreError>>

type ScoreContext = {
    ScopeId: string                    // the CALLER's scope (GP 4)
    CompositeKey: FitCompositeKey      // spec hash, training vintage, seed, provider, Hash
    Artifact: ArtifactRef              // what Predict used to receive
    Status: ModelArtifactStatus
    Spec: ModelSpecRef option          // resolved when a spec store is composed
    Annotations: Map<string, string>
    Input: DatasetVersionRef
}
```

### Why a context record rather than a widened signature

Both shapes break the same implementers, so the choice was made on what the
seam looks like *afterwards*:

- **Symmetry with the fit side.** `IModelFitProvider.Fit` already receives a
  whole `FitRequest` (scope + spec + vintage + seed). `Predict` receiving three
  loose arguments — one of them a naked reference — was the asymmetry that
  produced the defect. One record on each side restores it.
- **The next widening is free.** Adding a field to `ScoreContext` is an
  additive record change; adding a parameter is another arity break for every
  implementer. A seam that has already learned one thing it did not know will
  learn another.
- **GP 12 rule 4 made explicit.** "A provider receives the whole request per
  call" is easier to honour when the request *is* one value.
- **Values only — no live handle.** The context carries no store, no registry,
  and no callback. The platform resolves what a provider might need and passes
  results by value; substrate a provider needs (a data-object store, a model
  runtime) arrives at its `create` function, per the companion-authoring
  convention. Handing a live registry across the seam would be precisely the
  violation GP 12 rules 1 and 4 exist to prevent — a distributed score worker
  cannot be given one.

### Scope discipline (GP 4)

`ScoreContext.ScopeId` is taken from the `ScoreRequest`, **never** from the
artifact record's own `ScopeId`. The context cannot widen the scope its caller
was operating in: a provider handed an artifact record belonging to another
tenant still sees only the requesting scope, and any read it attempts fails
closed rather than reaching across. The optional spec lookup is scoped by the
same value. Both are pinned by tests in the `IModelScorer` contract pack.

### `IModelSpecStore` — new, optional

The registry stores a spec **hash** as identity, not the payload. A deployment
that also keeps the payload can compose an `IModelSpecStore`, and every
`Predict` then receives it as `context.Spec`:

```fsharp
type IModelSpecStore =
    abstract TryGet: scopeId: string * specHash: string -> Async<ModelSpecRef option>
```

Composing one is opt-in (GP 13). A deployment that does not is byte-for-byte
unchanged and gets `Spec = None`.

## Diff to apply

**Provider implementations** — take `context.Artifact` where you took
`artifact`:

```fsharp
// Before
member _.Predict(artifact, schema, rows) = async {
    let seedValue = artifact.ContentHash
    …
}

// After
member _.Predict(context, schema, rows) = async {
    let seedValue = context.Artifact.ContentHash
    …
}
```

A provider that previously *could not* resolve its model now can:

```fsharp
member _.Predict(context, schema, rows) = async {
    // The fitted parameters this provider persisted at Fit time, read under
    // the caller's scope — the read that was impossible before.
    match! dataObjects.Get(context.ScopeId, context.Artifact.ArtifactId) with
    | Error _ ->
        return Error(ScoreError.ProviderFailed(Kind, "fitted parameters not resolvable"))
    | Ok(_, bytes) ->
        // The specification, if a spec store is composed …
        let spec = context.Spec
        // … and the training vintage, straight off the composite key.
        let training = ScoreContext.trainingVintage context
        …
}
```

**Composition** — nothing changes:

```fsharp
// Still valid, unchanged, and still gets Spec = None:
let scorer = ModelScorer.create providers datasets audit ModelScorePolicy.permissive

// Opt in to spec resolution:
let scorer =
    ModelScorer.createWithSpecStore providers datasets audit ModelScorePolicy.permissive mySpecStore
```

`ModelScorer`'s four-argument constructor is preserved alongside the new
five-argument one, so class construction sites are unaffected too.

## Also added

`DatasetVersionRef.tryParseKey` — the inverse of `DatasetVersionRef.key`. The
composite key records the training vintage as a `{scope}/{dataset}@v{version}`
token, and consumers were hand-rolling the parse. `ScoreContext.trainingVintage`
is the same read spelled for a provider.

## Verification

- `dotnet build ToolUp.Forge.sln` — surfaces every `Predict` implementation.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
  — the `IModelScorer` contract pack pins the context's contents and its scope
  discipline; the evaluation pack drives the full runner path with a provider
  that resolves its artifact through the context and lands predictions as a
  dataset vintage.

## Rollback

Revert the SDK version pin. The change is source-level on the provider seam
only — no wire format, no persisted record, and no stored artifact shape
changes, so a rollback needs no data migration.
