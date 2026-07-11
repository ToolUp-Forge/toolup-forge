// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IModelScorerContract

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.ModelProviders.Reference

// ─── Phase 454 — model-scoring seam conformance pack ────────────────────
//
// Bound to the in-tree reference score provider (a trivial deterministic
// scorer) over the blob-backed dataset store. Asserts the portable contract
// of the scoring envelope:
//   * a fitted artifact scores a vintage; predictions land as a NEW dataset
//     version whose provenance names the artifact + input version;
//   * the output is an ordinary vintage — queryable through the same store
//     surfaces, joinable on the carried panel keys — with zero new machinery;
//   * re-scoring the same (artifact, input) reproduces byte-identical
//     predictions (plan D4);
//   * schema mismatch, unresolved provider, and (when enabled) non-approved
//     status are typed, AUDITED refusals — never exceptions;
//   * the approved-only guard is off by default (GP 13) and gates both ways
//     when enabled;
//   * scores are structurally scope-isolated (GP 4);
//   * a duplicate provider kind is rejected at registry construction;
//   * the batch `IJobHandler` maps parse / lookup / refusal outcomes to the
//     right `JobResult`.

let private t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private silentLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// A minimal `IModelRegistry` double for the batch-handler tests: it resolves
/// exactly the artifacts it was seeded with (by scope + composite-key hash)
/// and `NotFound` for anything else. Only `Get` is exercised by the handler.
type private StubModelRegistry(artifacts: (string * ModelArtifact) list) =
    let byKey =
        artifacts
        |> List.map (fun (scope, a) -> (scope, a.CompositeKey.Hash), a)
        |> Map.ofList

    interface IModelRegistry with
        member _.Register(_, _, _, _, _) = async { return Error(ModelRegistryError.StorageFailure "not used") }

        member _.Get(scopeId, keyHash) = async {
            match Map.tryFind (scopeId, keyHash) byKey with
            | Some a -> return Ok a
            | None -> return Error ModelRegistryError.NotFound
        }

        member _.QueryBySpecHash(_, _) = async { return [] }
        member _.QueryByDatasetVersion(_, _) = async { return [] }
        member _.QueryByStatus(_, _) = async { return [] }

        member _.TransitionStatus(_, _, _, _, _) = async { return Error(ModelRegistryError.StorageFailure "not used") }

/// A fresh blob-backed dataset store (the full default stack), one temp dir
/// per call so tests never share state.
let private freshStore () : IDatasetStore =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-score-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    BlobDatasetStore.create dataObjects

/// A small panel schema: unit / period / one plain feature / one target.
let private panelSchema: DatasetSchema = {
    Columns = [
        {
            Name = "region"
            DType = DatasetDType.Categorical
            Nullable = false
            Role = DatasetColumnRole.PanelUnit
        }
        {
            Name = "week"
            DType = DatasetDType.Timestamp
            Nullable = false
            Role = DatasetColumnRole.PanelPeriod
        }
        {
            Name = "spend"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
        {
            Name = "sales"
            DType = DatasetDType.Float
            Nullable = true
            Role = DatasetColumnRole.Target
        }
    ]
}

let private row (region: string) (weekOffset: float) (spend: float) (sales: DatasetValue) : DatasetRow = {
    Cells = [
        DatasetValue.Categorical region
        DatasetValue.Timestamp(t0.AddDays(7.0 * weekOffset))
        DatasetValue.Float spend
        sales
    ]
}

let private sampleRows: DatasetRow list = [
    row "north" 0.0 100.0 (DatasetValue.Float 1000.0)
    row "north" 1.0 120.0 (DatasetValue.Float 1100.0)
    row "south" 0.0 80.0 (DatasetValue.Float 900.0)
]

/// A governed artifact fitted by the `reference` provider (so the reference
/// scorer resolves it), in the given lifecycle `status`. Built directly from
/// the Phase 449 composite key — no fit run needed to exercise scoring.
let private artifactOf (providerId: string) (status: ModelArtifactStatus) : ModelArtifact =
    let key =
        FitCompositeKey.compute "spec-hash-x" "team-1/input@v1" 42L providerId ReferenceModelScoreProvider.Version

    {
        CompositeKey = key
        ScopeId = "team-1"
        ArtifactRef = {
            ArtifactId = key.Hash
            ContentHash = "content-hash-abc"
            ByteLength = 16L
        }
        Diagnostics = Map.empty
        GateVerdicts = []
        Status = status
        Annotations = Map.empty
        Notes = ""
        RegisteredBy = "u1"
        RegisteredAt = t0
        Version = 1
    }

/// Seed an input vintage under `scope` and return its ref.
let private seedInput (store: IDatasetStore) (scope: string) : Async<DatasetVersionRef> = async {
    let! created = store.Create(scope, "input", panelSchema, sampleRows, "u1", Map.empty, Versioned)

    match created with
    | Ok v ->
        return {
            ScopeId = scope
            DatasetId = "input"
            Version = v.Version
        }
    | Error e -> return failwithf "seed failed: %s" (DatasetError.describe e)
}

/// The score refusals audited on `audit`, in order.
let private refusedRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | ModelScoreRefused p -> Some p
        | _ -> None)

let private scoredRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | ModelScored p -> Some p
        | _ -> None)

let tests =
    testList "ModelScore — IModelScorer scoring-seam contract" [

        testCaseAsync "a fitted artifact scores a vintage; predictions land as a new dataset version with provenance"
        <| async {
            let store = freshStore ()
            let audit = RecordingAuditLog()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]
            let scorer = ModelScorer.create registry store audit ModelScorePolicy.permissive

            let! input = seedInput store "team-1"

            let artifact =
                artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Fitted

            let request: ScoreRequest = {
                ScopeId = "team-1"
                Artifact = artifact
                Input = input
                OutputDatasetId = "input-scored"
                ScoredBy = "u1"
            }

            match! scorer.Score request with
            | Error e -> failtestf "expected a scored output; got %s" (ScoreError.describe e)
            | Ok outputRef ->
                Expect.equal outputRef.DatasetId "input-scored" "predictions land under the requested output dataset id"
                Expect.equal outputRef.Version 1 "first scored vintage is version 1"

                // The output is an ordinary, immediately-readable vintage.
                let! got = store.GetVersion(outputRef.ScopeId, outputRef.DatasetId, outputRef.Version)

                match got with
                | Error e -> failtestf "scored output not readable: %s" (DatasetError.describe e)
                | Ok v ->
                    Expect.equal v.RowCount (int64 (List.length sampleRows)) "one prediction row per input row"

                    let colNames = v.Schema.Columns |> List.map (fun c -> c.Name)

                    Expect.equal
                        colNames
                        [ "region"; "week"; "prediction" ]
                        "panel keys carried forward + prediction column"

                    // Provenance names the artifact + input version.
                    Expect.equal
                        (Map.tryFind ScoreProvenance.ScoredByKey v.Metadata)
                        (Some artifact.CompositeKey.Hash)
                        "output provenance names the scoring artifact"

                    Expect.equal
                        (Map.tryFind ScoreProvenance.InputVersionKey v.Metadata)
                        (Some(DatasetVersionRef.key input))
                        "output provenance names the input vintage"

                // Exactly one ModelScored audit row, carrying the identity.
                let scored = scoredRows audit
                Expect.equal scored.Length 1 "one ModelScored audit row"
                Expect.equal scored.[0].CompositeKeyHash artifact.CompositeKey.Hash "audit names the artifact"
                Expect.equal scored.[0].OutputVersion (DatasetVersionRef.key outputRef) "audit names the output version"
                Expect.isEmpty (refusedRows audit) "a successful score emits no refusal row"
        }

        testCaseAsync "re-scoring the same artifact + input reproduces byte-identical predictions (plan D4)"
        <| async {
            let store = freshStore ()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

            let scorer =
                ModelScorer.create registry store (RecordingAuditLog()) ModelScorePolicy.permissive

            let! input = seedInput store "team-1"

            let artifact =
                artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Fitted

            let scoreInto (outId: string) : Async<DatasetRow list> = async {
                let request: ScoreRequest = {
                    ScopeId = "team-1"
                    Artifact = artifact
                    Input = input
                    OutputDatasetId = outId
                    ScoredBy = "u1"
                }

                match! scorer.Score request with
                | Error e -> return failwithf "score failed: %s" (ScoreError.describe e)
                | Ok ref ->
                    let! page = store.ReadPage(ref.ScopeId, ref.DatasetId, ref.Version, DatasetPageQuery.firstPage 100)

                    match page with
                    | Ok p -> return p.Rows
                    | Error e -> return failwithf "read failed: %s" (DatasetError.describe e)
            }

            let! first = scoreInto "scored-a"
            let! second = scoreInto "scored-b"
            Expect.equal first second "identical (artifact, input) must produce byte-identical predictions"
        }

        testCaseAsync "an input schema missing a provider-required column is a typed, audited refusal"
        <| async {
            let store = freshStore ()
            let audit = RecordingAuditLog()

            // A reference scorer that requires a column the input lacks.
            let registry =
                ModelScoreProviderRegistry [ ReferenceModelScoreProvider.createRequiring [ "not-a-column" ] ]

            let scorer = ModelScorer.create registry store audit ModelScorePolicy.permissive

            let! input = seedInput store "team-1"

            let artifact =
                artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Fitted

            let request: ScoreRequest = {
                ScopeId = "team-1"
                Artifact = artifact
                Input = input
                OutputDatasetId = "input-scored"
                ScoredBy = "u1"
            }

            match! scorer.Score request with
            | Error(ScoreError.InputSchemaMismatch _) ->
                let refused = refusedRows audit
                Expect.equal refused.Length 1 "a schema-mismatch refusal is audited"
                Expect.equal refused.[0].CompositeKeyHash artifact.CompositeKey.Hash "the refusal names the artifact"
            | other -> failtestf "expected InputSchemaMismatch; got %A" other

            // No output vintage was written.
            let! got = store.GetLatest("team-1", "input-scored")

            match got with
            | Error DatasetError.NotFound -> ()
            | other -> failtestf "a refused score must write no output; got %A" other
        }

        testCaseAsync "the approved-only guard is off by default (a Fitted artifact scores)"
        <| async {
            let store = freshStore ()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

            let scorer =
                ModelScorer.create registry store (RecordingAuditLog()) ModelScorePolicy.permissive

            let! input = seedInput store "team-1"

            let artifact =
                artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Fitted

            let request: ScoreRequest = {
                ScopeId = "team-1"
                Artifact = artifact
                Input = input
                OutputDatasetId = "input-scored"
                ScoredBy = "u1"
            }

            match! scorer.Score request with
            | Ok _ -> ()
            | Error e -> failtestf "default policy must not gate on status; got %s" (ScoreError.describe e)
        }

        testCaseAsync "the approved-only guard gates both ways when enabled"
        <| async {
            let store = freshStore ()
            let audit = RecordingAuditLog()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]
            let scorer = ModelScorer.create registry store audit ModelScorePolicy.approvedOnly

            let! input = seedInput store "team-1"

            let request (artifact: ModelArtifact) : ScoreRequest = {
                ScopeId = "team-1"
                Artifact = artifact
                Input = input
                OutputDatasetId = "input-scored"
                ScoredBy = "u1"
            }

            // Fitted → refused (typed + audited).
            let fitted = artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Fitted

            match! scorer.Score(request fitted) with
            | Error(ScoreError.NotApproved s) -> Expect.equal s "Fitted" "the observed status is named"
            | other -> failtestf "a non-approved artifact must be refused; got %A" other

            Expect.equal (refusedRows audit).Length 1 "the non-approved refusal is audited"

            // Approved → scores.
            let approved =
                artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Approved

            match! scorer.Score(request approved) with
            | Ok _ -> ()
            | Error e -> failtestf "an approved artifact must score; got %s" (ScoreError.describe e)
        }

        testCaseAsync "an artifact whose provider is not registered is a typed, audited refusal"
        <| async {
            let store = freshStore ()
            let audit = RecordingAuditLog()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]
            let scorer = ModelScorer.create registry store audit ModelScorePolicy.permissive

            let! input = seedInput store "team-1"
            let artifact = artifactOf "no-such-provider" ModelArtifactStatus.Fitted

            let request: ScoreRequest = {
                ScopeId = "team-1"
                Artifact = artifact
                Input = input
                OutputDatasetId = "input-scored"
                ScoredBy = "u1"
            }

            match! scorer.Score request with
            | Error(ScoreError.ProviderNotFound k) ->
                Expect.equal k "no-such-provider" "the unresolved provider is named"
            | other -> failtestf "expected ProviderNotFound; got %A" other

            Expect.equal (refusedRows audit).Length 1 "the unresolved-provider refusal is audited"
        }

        testCaseAsync "scores are structurally scope-isolated (GP 4)"
        <| async {
            let store = freshStore ()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

            let scorer =
                ModelScorer.create registry store (RecordingAuditLog()) ModelScorePolicy.permissive

            let! input = seedInput store "team-1"

            let artifact =
                artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Fitted

            let request: ScoreRequest = {
                ScopeId = "team-1"
                Artifact = artifact
                Input = input
                OutputDatasetId = "input-scored"
                ScoredBy = "u1"
            }

            let! _ = scorer.Score request

            // The scored vintage exists in team-1 but not in team-2.
            let! here = store.GetLatest("team-1", "input-scored")
            Expect.isTrue (Result.isOk here) "the scored vintage exists in its own scope"

            let! there = store.GetLatest("team-2", "input-scored")

            match there with
            | Error DatasetError.NotFound -> ()
            | other -> failtestf "the scored vintage must not leak across scopes; got %A" other
        }

        test "a duplicate provider kind is rejected at registry construction" {
            Expect.throws
                (fun () ->
                    ModelScoreProviderRegistry [
                        ReferenceModelScoreProvider.create ()
                        ReferenceModelScoreProvider.create ()
                    ]
                    |> ignore)
                "two score providers of the same kind must fail loudly at construction"
        }

        testCaseAsync "the job handler maps a good payload to Success and a malformed payload to PermanentFailure"
        <| async {
            let store = freshStore ()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

            let scorer =
                ModelScorer.create registry store (RecordingAuditLog()) ModelScorePolicy.permissive

            let! input = seedInput store "team-1"

            let artifact =
                artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Fitted

            let registryStub = StubModelRegistry [ "team-1", artifact ]
            let handler = ModelScoringJobHandler.create registryStub scorer silentLogger

            let ctx (payload: string) : JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = "team-1"
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
                Attempt = 1
                Trigger = Manual
                TriggerSource = ScheduledManually "system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = payload
                DeadLetterDestination = None
            }

            let goodPayload =
                ModelScoringEnvelope.serialiseRequest {
                    ScopeId = "team-1"
                    ArtifactKeyHash = artifact.CompositeKey.Hash
                    Input = input
                    OutputDatasetId = "input-scored"
                    ScoredBy = "system"
                }

            let! ok = handler.Execute(ctx goodPayload)
            Expect.equal ok Success "a well-formed ScoreJobRequest runs to Success"

            let! bad = handler.Execute(ctx "{ not json")

            match bad with
            | PermanentFailure _ -> ()
            | other -> failtestf "a malformed payload must be a PermanentFailure; got %A" other
        }

        testCaseAsync "the job handler maps an unknown artifact to PermanentFailure"
        <| async {
            let store = freshStore ()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

            let scorer =
                ModelScorer.create registry store (RecordingAuditLog()) ModelScorePolicy.permissive

            let! input = seedInput store "team-1"

            let handler =
                ModelScoringJobHandler.create (StubModelRegistry []) scorer silentLogger

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = "team-1"
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
                Attempt = 1
                Trigger = Manual
                TriggerSource = ScheduledManually "system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload =
                    ModelScoringEnvelope.serialiseRequest {
                        ScopeId = "team-1"
                        ArtifactKeyHash = "no-such-artifact"
                        Input = input
                        OutputDatasetId = "input-scored"
                        ScoredBy = "system"
                    }
                DeadLetterDestination = None
            }

            let! result = handler.Execute ctx

            match result with
            | PermanentFailure _ -> ()
            | other -> failtestf "an unknown artifact is terminal; expected PermanentFailure, got %A" other
        }

        testCaseAsync "the job handler maps a non-approved refusal to PermanentFailure under an approved-only policy"
        <| async {
            let store = freshStore ()

            let registry = ModelScoreProviderRegistry [ ReferenceModelScoreProvider.create () ]

            let scorer =
                ModelScorer.create registry store (RecordingAuditLog()) ModelScorePolicy.approvedOnly

            let! input = seedInput store "team-1"

            let artifact =
                artifactOf ReferenceModelScoreProvider.Kind ModelArtifactStatus.Fitted

            let handler =
                ModelScoringJobHandler.create (StubModelRegistry [ "team-1", artifact ]) scorer silentLogger

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = "team-1"
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
                Attempt = 1
                Trigger = Manual
                TriggerSource = ScheduledManually "system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload =
                    ModelScoringEnvelope.serialiseRequest {
                        ScopeId = "team-1"
                        ArtifactKeyHash = artifact.CompositeKey.Hash
                        Input = input
                        OutputDatasetId = "input-scored"
                        ScoredBy = "system"
                    }
                DeadLetterDestination = None
            }

            let! result = handler.Execute ctx

            match result with
            | PermanentFailure _ -> ()
            | other -> failtestf "a non-approved refusal is terminal; expected PermanentFailure, got %A" other
        }
    ]