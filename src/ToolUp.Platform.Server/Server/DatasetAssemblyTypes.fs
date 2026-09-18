// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 452 — Dataset assembly (transforms-as-data) ──────────────────
//
// Declarative, **algorithm-free** production of model-ready dataset
// versions from platform stores (statistical-modelling substrate plan,
// Stage 1). A `DatasetAssemblySpec` is *serialisable data* — source
// bindings plus a **closed, deliberately small** transform DU (join /
// resample / lag / window / filter / split). The executor
// (`DatasetAssemblyExecutor`) materialises a spec into a new Phase 448
// dataset version, recording the spec + the source identities it read as
// provenance on the produced version. Replaying the same spec against
// moved sources is the mechanical "new vintage" path — the spec hash is
// unchanged, only the source identities move, and the provenance shows
// exactly what moved.
//
// **This is plumbing, not features (GP 1 / plan risk #3).** The transform
// DU computes *structural* rearrangements — joins on declared keys,
// resample by aggregate, panel shift/roll, row filter, subset split. It
// deliberately cannot compute *derived values* beyond the closed aggregate
// list (interactions, decays, encodings, domain features): those are a
// provider / consumer concern, rejected here by construction because the
// DU has no case for them. See `DATASET-ASSEMBLY.md` (scope guard, 452.C).
//
// **Why server-only, not Core/Shared.** Like `ModelFitTypes` (Phase 449):
// a spec's identity is SHA-256-addressed (`System.Security.Cryptography`
// is not Fable-compilable) and assembly is a server-side compute step with
// no client view. The phase key-files named a `Server/Shared/` folder that
// does not exist; the nearest correct home is `Server/` alongside the
// executor (the same deviation Phase 449 recorded).

/// How two frames combine on their declared keys. `Inner` keeps only left
/// rows with a matching right row; `LeftOuter` keeps every left row,
/// filling absent right columns with typed `Null`. Expressed as data so
/// any executor honours it without a caller callback (GP 12 rule 3).
[<RequireQualifiedAccess>]
type AssemblyJoinKind =
    | Inner
    | LeftOuter

/// One aggregate applied to a column when resampling. Reuses the shared
/// `TimeSeriesAggregation` DU (Phase 439) — no new aggregate language — and
/// names the output column so a resample can rename as it aggregates.
type AssemblyAggregation = {
    /// Source column to fold.
    Column: string
    /// Aggregate to apply (`Sum` / `Mean`=`Average` / `Min` / `Max` /
    /// `Last` / `Count`, plus `First` from the shared DU).
    Aggregation: TimeSeriesAggregation
    /// Output column name (may equal `Column`).
    As: string
}

/// A source the assembly reads a typed frame from. Serialisable identity
/// only — the executor resolves each to `(schema, rows)` at run time. The
/// resolved *identity* (version key / range descriptor / query hash) is
/// recorded on the produced version as provenance, so a replay names
/// exactly what it read. `[<RequireQualifiedAccess>]` — the case names are
/// too generic to sit unqualified in the widely-opened namespace.
[<RequireQualifiedAccess>]
type AssemblySource =
    /// An immutable Phase 448 dataset vintage, read whole (paged).
    | DatasetVersion of DatasetVersionRef
    /// A Phase 439 time-series range, optionally downsampled to a grain.
    /// Materialised into a long-format frame: a `PanelUnit` column stamped
    /// with `UnitLabel`, a `PanelPeriod` timestamp column, and a plain
    /// value column.
    | TimeSeriesRange of TimeSeriesSourceBinding
    /// An external table (entity query, ingested table) resolved by a
    /// caller-supplied resolver wired into the executor. Forge does not
    /// interpret the query — it stores the binding's identity as
    /// provenance and hands the binding to the resolver (keeps entity /
    /// ingestion query semantics out of the assembly core, GP 1).
    | ExternalTable of ExternalTableBinding

/// A time-series range materialised into a long-format frame.
and TimeSeriesSourceBinding = {
    ScopeId: string
    Series: string
    From: DateTimeOffset
    Until: DateTimeOffset
    /// `Some` downsamples to a modelling grain (Phase 439 `Downsample`
    /// surface feeds `Resample`); `None` reads raw points.
    Downsample: TimeSeriesDownsample option
    /// Value stamped into the `PanelUnit` column of every produced row —
    /// the panel identity of this series.
    UnitLabel: string
    /// Output column names for the three produced columns.
    UnitColumn: string
    PeriodColumn: string
    ValueColumn: string
}

/// An opaque external-table binding. `Kind` routes it to a registered
/// resolver; `Ref` + `Query` are resolver-interpreted. Forge hashes the
/// binding for provenance and never parses `Query`.
and ExternalTableBinding = {
    Kind: string
    Ref: string
    Query: string
}

/// A structural transform in the assembly pipeline. **Closed and small by
/// design** (452.A / plan risk #3): every case rearranges rows/columns or
/// folds an existing column with a declared aggregate — none computes a
/// new derived value outside the aggregate list. `[<RequireQualifiedAccess>]`.
[<RequireQualifiedAccess>]
type AssemblyTransform =
    /// Join a right source onto the working frame on declared key columns.
    /// Key lists are positionally aligned (`LeftKeys[i]` matches
    /// `RightKeys[i]`). Non-key right columns are appended; a name clash
    /// with an existing column is a typed error (never a silent overwrite).
    | Join of right: AssemblySource * leftKeys: string list * rightKeys: string list * how: AssemblyJoinKind
    /// Resample to a period grain: group by `PartitionKeys` + the bucket of
    /// `PeriodColumn` (a `Timestamp` column, bucketed by `Bucket` aligned to
    /// the earliest observed period), then fold each `Aggregations` column.
    /// Output columns = partition keys + the (bucketed) period + the
    /// aggregated columns. `[<RequireQualifiedAccess>]`.
    | Resample of
        periodColumn: string *
        bucket: TimeSpan *
        partitionKeys: string list *
        aggregations: AssemblyAggregation list
    /// Lag `Column` by `By` periods within each `PartitionBy` group, ordered
    /// by `PeriodColumn`, into a new `As` column. The first `By` rows of
    /// each partition get typed `Null` (never a silent zero — 452.D).
    | Lag of column: string * by: int * partitionBy: string list * periodColumn: string * as': string
    /// Rolling aggregate of `Column` over a trailing window of `Size` rows
    /// (inclusive of the current row) within each `PartitionBy` group,
    /// ordered by `PeriodColumn`, into a new `As` column. Rows before the
    /// window fills fold over the rows available so far.
    | Window of
        column: string *
        size: int *
        aggregation: TimeSeriesAggregation *
        partitionBy: string list *
        periodColumn: string *
        as': string
    /// Keep rows matching the AND-combined filters. Reuses the typed
    /// `DatasetFilter` (Phase 448) — the dataset-native, already-"as-data"
    /// predicate for typed frames. (Deviation from the literal "Phase 19a
    /// `Predicate`": that DU is string-valued + entity-index-oriented and
    /// would lose the cell types a typed frame carries; `DatasetFilterOp`
    /// is the honest reuse here, still "no new predicate language".)
    | Filter of DatasetFilter list

/// How the produced frame is split into named subsets (452.A). Each subset
/// lands as its own dataset version so train / holdout are independent
/// vintages a fit and an evaluation pin to separately.
[<RequireQualifiedAccess>]
type AssemblySplit =
    /// Rows with `PeriodColumn` strictly before `Cutoff` go to `BeforeName`;
    /// the rest to `AfterName`. The out-of-time train/holdout split.
    | ByPeriodCutoff of periodColumn: string * cutoff: DateTimeOffset * beforeName: string * afterName: string
    /// Assign each row to a named bucket by a stable hash of its
    /// `UnitColumn` value against the `(name, weight)` buckets — deterministic
    /// across replays (452.D). Weights are relative; a value hashes to the
    /// bucket its cumulative-weight band covers.
    | ByUnitHash of unitColumn: string * buckets: (string * int) list

/// A complete assembly recipe — serialisable data, no functions, no
/// expression language. `specHash` is the stable identity a replay reuses.
type DatasetAssemblySpec = {
    /// Scope the produced version(s) land under (GP 4 — structural
    /// isolation). Dataset / time-series sources are read from their own
    /// scopes; this is where the output is written.
    Scope: string
    /// The base source the pipeline starts from.
    Base: AssemblySource
    /// Transforms applied in order to the base frame.
    Transforms: AssemblyTransform list
    /// Optional subset split. `None` produces a single `"all"` subset.
    Split: AssemblySplit option
    /// Dataset id the produced version(s) land under. With a split, each
    /// subset lands under `{OutputDatasetId}:{subsetName}`.
    OutputDatasetId: string
    /// Role assignments applied to the output schema by column name
    /// (columns not named default to `Plain`). Lets an assembly mark the
    /// panel keys + target on its output without a separate step.
    OutputRoles: (string * DatasetColumnRole) list
    /// Versioning policy for the produced version(s).
    Policy: VersioningPolicy
}

/// Typed failures of the assembly executor. `[<RequireQualifiedAccess>]` —
/// `SourceUnavailable` / `SchemaConflict` are generic enough to collide in
/// the widely-opened namespace.
[<RequireQualifiedAccess>]
type AssemblyError =
    /// A source could not be read (missing vintage, unreachable series,
    /// unresolved external binding). `reason` is diagnostic.
    | SourceUnavailable of reason: string
    /// A transform referenced a column that is not in the working frame, or
    /// a join/split key/column that does not exist. `reason` names it.
    | UnknownColumn of reason: string
    /// A join would produce two columns with the same name, or a resample /
    /// transform produced a structurally invalid frame. `reason` is
    /// diagnostic.
    | SchemaConflict of reason: string
    /// A filter literal's dtype did not match its column, or a transform's
    /// column was of the wrong dtype (e.g. a non-`Timestamp` period column).
    | TypeMismatch of reason: string
    /// No `ExternalTable` resolver was wired but the spec referenced one.
    | ResolverMissing of kind: string
    /// Writing a produced subset as a dataset version failed. `reason` lifts
    /// the underlying `DatasetError`.
    | StorageFailure of reason: string

/// The forge-facing assembly seam. Materialise a spec into new dataset
/// version(s), one per subset (an unsplit spec produces a single `"all"`
/// subset). Forge owns the plumbing; it never computes a derived value
/// outside the closed transform DU (GP 1). Kept as an interface so a
/// deployment can substitute a distributed executor against the same
/// contract.
type IDatasetAssemblyExecutor =
    abstract Assemble:
        spec: DatasetAssemblySpec * createdBy: string -> Async<Result<Map<string, DatasetVersionRef>, AssemblyError>>

module AssemblyError =
    let describe =
        function
        | AssemblyError.SourceUnavailable r -> $"assembly source unavailable: {r}"
        | AssemblyError.UnknownColumn r -> $"assembly references unknown column: {r}"
        | AssemblyError.SchemaConflict r -> $"assembly schema conflict: {r}"
        | AssemblyError.TypeMismatch r -> $"assembly type mismatch: {r}"
        | AssemblyError.ResolverMissing k -> $"no external-table resolver registered for kind '{k}'"
        | AssemblyError.StorageFailure r -> $"assembly output storage failure: {r}"

/// Reserved metadata keys the assembly executor stamps onto every produced
/// dataset version so the vintage names the recipe that built it and every
/// source identity it read (452.B). Because `DatasetVersion.Metadata` is an
/// existing field, provenance is queryable/joinable with zero new machinery
/// (the same idiom as `ScoreProvenance`, Phase 454).
module AssemblyProvenance =
    /// Stable SHA-256 hex of the canonical spec — the replay identity.
    [<Literal>]
    let SpecHashKey = "assembly.specHash"

    /// The subset name this version materialises (`"all"` when unsplit).
    [<Literal>]
    let SubsetKey = "assembly.subset"

    /// Newline-joined source identity tokens the assembly read (version
    /// keys, time-series range descriptors, external-binding hashes) — the
    /// "exactly what moved" record a replay diff reads.
    [<Literal>]
    let SourcesKey = "assembly.sources"

    /// Phase 601 — the full canonical spec JSON recorded on every produced
    /// version, which is what makes "re-materialise assembly spec X against
    /// current sources" a first-class operation: a spec-carrying version IS
    /// the assembly-spec ref a consumer hands `DatasetRevintage.revintage`
    /// (it never authors or edits the spec itself — the transform DU stays
    /// closed, the 452 scope guard holds).
    [<Literal>]
    let SpecJsonKey = "assembly.spec"

    let private jsonOptions = FableConverters.create ()

    /// The canonical serialised form of a spec — the bytes `specHash`
    /// hashes and `SpecJsonKey` records.
    let specJson (spec: DatasetAssemblySpec) : string =
        JsonSerializer.Serialize(spec, jsonOptions)

    /// Parse a recorded spec back from its `SpecJsonKey` value. `Error` for
    /// a foreign / future writer's unreadable record.
    let tryParseSpec (json: string) : Result<DatasetAssemblySpec, string> =
        try
            Ok(JsonSerializer.Deserialize<DatasetAssemblySpec>(json, jsonOptions))
        with ex ->
            Error ex.Message

    /// Lowercase SHA-256 hex of a spec's canonical JSON (the FableConverters
    /// wire is stable field-order for records / DUs, so the hash is
    /// reproducible across processes). Two specs with identical structure
    /// share a hash → replay reuses it.
    let specHash (spec: DatasetAssemblySpec) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes(specJson spec))
        |> Convert.ToHexStringLower

    /// The stable identity token for one source — the value that enters the
    /// `SourcesKey` provenance record.
    let sourceIdentity (source: AssemblySource) : string =
        match source with
        | AssemblySource.DatasetVersion r -> "dataset:" + DatasetVersionRef.key r
        | AssemblySource.TimeSeriesRange b -> sprintf "timeseries:%s/%s@[%O,%O)" b.ScopeId b.Series b.From b.Until
        | AssemblySource.ExternalTable b ->
            let h =
                SHA256.HashData(Encoding.UTF8.GetBytes(b.Kind + "\u0000" + b.Ref + "\u0000" + b.Query))
                |> Convert.ToHexStringLower

            sprintf "external:%s:%s" b.Kind (h.Substring(0, 16))
// ─── Phase 794 — the labelled transform algebra ──────────────────────
//
// The transform DU above says exactly which inputs each case reads: every
// case but one folds or rearranges the working frame alone, and `Join`
// additionally reads a second source — the point in the algebra where two
// parties' data meet. That is a structural fact about the computation, and
// it is the fact a label must be propagated along.
//
// So the labelling lives here, with the algebra it describes, and it is
// **generic in the label**: this tier supplies the SHAPE of the
// computation and knows nothing about disclosure, while the caller
// supplies the lattice — `bottom`, `join`, and a label per source. There is
// deliberately no join defined in this file. A second join would be a
// second algebra to keep honest, and abolishing the unnamed, untested one
// is the whole point of the phase; the one join lives in the facts tier
// with its executable laws, and this fold is a client of it.
//
// The fold is a `match` over every case of the closed DU, so a transform
// added later cannot acquire a label by accident — it fails to compile
// until someone says what it reads.

/// The lattice a labelling runs over, supplied by the caller: the identity,
/// the join, and the label each source's rows carry. `LabelOfSource` is the
/// declared part — which party a source belongs to is compose-time data,
/// never something inferred from the frame.
type TransformLabelling<'Label> = {
    Bottom: 'Label
    Join: 'Label -> 'Label -> 'Label
    LabelOfSource: AssemblySource -> 'Label
}

/// One transform paired with the label its output frame carries — the join
/// of the labels of everything that flowed into it. `Contributed` is the
/// part THIS node brought in that the incoming frame did not already carry,
/// which for a `Join` is the right source's label and for every other case
/// is the identity: the record of where in a pipeline a second party
/// entered.
type LabelledTransform<'Label> = {
    Transform: AssemblyTransform
    Label: 'Label
    Contributed: 'Label
}

/// A labelled pipeline: the base frame's label, one labelled node per
/// transform in order, and the label of the frame the pipeline produces.
/// `OutputLabel` is what any consumer of the assembled dataset inherits.
type LabelledAssembly<'Label> = {
    BaseLabel: 'Label
    Nodes: LabelledTransform<'Label> list
    OutputLabel: 'Label
}

module AssemblyLabelling =

    /// The label a single transform CONTRIBUTES: the labels of its inputs
    /// OTHER than the working frame. Only `Join` has one — it names a second
    /// source, so a joined frame carries both parties' policies from that
    /// node onward. `Resample` / `Lag` / `Window` / `Filter` read the
    /// working frame and nothing else, so they contribute the identity and
    /// carry whatever reached them: an information-losing fold is not a
    /// declassifier, and only `narrowsTo` under a declared routine may lower
    /// a label.
    let contributedBy (labelling: TransformLabelling<'Label>) (transform: AssemblyTransform) : 'Label =
        match transform with
        | AssemblyTransform.Join(right, _, _, _) -> labelling.LabelOfSource right
        | AssemblyTransform.Resample _
        | AssemblyTransform.Lag _
        | AssemblyTransform.Window _
        | AssemblyTransform.Filter _ -> labelling.Bottom

    /// The fold: run the labelling down the pipeline, each node's label the
    /// join of the incoming frame's label and what the node contributed.
    let label
        (labelling: TransformLabelling<'Label>)
        (baseSource: AssemblySource)
        (transforms: AssemblyTransform list)
        : LabelledAssembly<'Label> =
        let baseLabel = labelling.LabelOfSource baseSource

        let nodes, outputLabel =
            transforms
            |> List.mapFold
                (fun incoming transform ->
                    let contributed = contributedBy labelling transform
                    let nodeLabel = labelling.Join incoming contributed

                    {
                        Transform = transform
                        Label = nodeLabel
                        Contributed = contributed
                    },
                    nodeLabel)
                baseLabel

        {
            BaseLabel = baseLabel
            Nodes = nodes
            OutputLabel = outputLabel
        }

    /// The labelling of a whole spec — its base source and its transforms.
    /// The split is deliberately not labelled: a split partitions rows, and
    /// every subset of a labelled frame carries the frame's label.
    let ofSpec (labelling: TransformLabelling<'Label>) (spec: DatasetAssemblySpec) : LabelledAssembly<'Label> =
        label labelling spec.Base spec.Transforms

    /// The label of the frame a spec produces — what a consumer of any of
    /// its output versions inherits.
    let outputLabelOf (labelling: TransformLabelling<'Label>) (spec: DatasetAssemblySpec) : 'Label =
        (ofSpec labelling spec).OutputLabel

    /// Every source a spec reads, base first then each `Join`'s right side
    /// in pipeline order — what a caller enumerates to build its
    /// `LabelOfSource` binding without re-walking the DU.
    let sourcesOf (spec: DatasetAssemblySpec) : AssemblySource list =
        spec.Base
        :: (spec.Transforms
            |> List.choose (function
                | AssemblyTransform.Join(right, _, _, _) -> Some right
                | AssemblyTransform.Resample _
                | AssemblyTransform.Lag _
                | AssemblyTransform.Window _
                | AssemblyTransform.Filter _ -> None))