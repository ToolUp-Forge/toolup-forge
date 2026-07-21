// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DatasetRevintageTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tracing

// ─── Phase 601 — assembly re-vintage trigger + scheduling ───────────────
//
// Asserts the mechanical new-vintage path: a consumer holding only a
// spec-carrying produced version triggers `DatasetRevintage.revintage`
// and receives new immutable version(s) with full provenance —
//   * unchanged sources → the replay is deterministic (content-identical
//     output; the store's content addressing dedups the blob) and still
//     yields a new immutable version;
//   * a changed source (a new upstream vintage) → the replay re-binds to
//     the latest source version and produces a distinct output whose
//     provenance names the fresh source;
//   * a source that no longer resolves → a typed
//     `RevintageError.SourceUnresolvable` refusal;
//   * a version with no recorded spec → `NoRecordedSpec` (typed, not a
//     guess);
//   * the `_platform.dataset.revintage` job handler round-trips the
//     payload and maps refusal classes to the right `JobResult`;
//   * success is audited with spec hash + source + produced versions.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

let private freshStores () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-revintage-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let blob = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let datasets = BlobDatasetStore.create dataObjects
    let executor = DatasetAssemblyExecutor.create datasets
    datasets, executor

let private sourceSchema: DatasetSchema = {
    Columns = [
        {
            Name = "region"
            DType = DatasetDType.Categorical
            Nullable = false
            Role = DatasetColumnRole.PanelUnit
        }
        {
            Name = "spend"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
    ]
}

let private rowsV1: DatasetRow list = [
    {
        Cells = [ DatasetValue.Categorical "north"; DatasetValue.Float 100.0 ]
    }
    {
        Cells = [ DatasetValue.Categorical "south"; DatasetValue.Float 80.0 ]
    }
]

let private rowsV2: DatasetRow list =
    rowsV1
    @ [
        {
            Cells = [ DatasetValue.Categorical "east"; DatasetValue.Float 65.0 ]
        }
    ]

/// Seed the upstream source dataset (Versioned, so a "weekly refresh" can
/// append v2) and assemble it once; returns the spec-carrying produced ref.
let private seedAndAssemble (datasets: IDatasetStore) (executor: IDatasetAssemblyExecutor) (scope: string) = async {
    let! seeded = datasets.Create(scope, "source", sourceSchema, rowsV1, "seed", Map.empty, Versioned)

    match seeded with
    | Error e -> return failtestf "seed failed: %s" (DatasetError.describe e)
    | Ok _ ->
        let spec: DatasetAssemblySpec = {
            Scope = scope
            Base =
                AssemblySource.DatasetVersion {
                    ScopeId = scope
                    DatasetId = "source"
                    Version = 1
                }
            Transforms = [
                AssemblyTransform.Filter [
                    {
                        Column = "spend"
                        Op = DatasetFilterOp.Gte
                        Value = DatasetValue.Float 60.0
                    }
                ]
            ]
            Split = None
            OutputDatasetId = "assembled"
            OutputRoles = []
            Policy = Versioned
        }

        match! executor.Assemble(spec, "assembler") with
        | Error e -> return failtestf "assemble failed: %s" (AssemblyError.describe e)
        | Ok produced -> return produced["all"]
}

let tests =
    testList "DatasetRevintage" [
        testCaseAsync "unchanged sources — deterministic replay yields a new immutable version with dedup'd content"
        <| async {
            let datasets, executor = freshStores ()
            let audit = RecordingAuditLog()
            let! producedRef = seedAndAssemble datasets executor "team-1"

            let! original = datasets.GetVersion("team-1", producedRef.DatasetId, producedRef.Version)

            let originalVersion =
                match original with
                | Ok v -> v
                | Error e -> failtestf "read failed: %s" (DatasetError.describe e)

            match! DatasetRevintage.revintage executor datasets (audit :> IAuditLog) producedRef "operator-1" with
            | Error e -> failtestf "revintage refused: %s" (RevintageError.describe e)
            | Ok produced ->
                let replayRef = produced["all"]
                Expect.equal replayRef.DatasetId producedRef.DatasetId "replay lands under the same dataset id"
                Expect.isTrue (replayRef.Version > producedRef.Version) "the replay is a NEW immutable version"

                let! replay = datasets.GetVersion("team-1", replayRef.DatasetId, replayRef.Version)

                match replay with
                | Error e -> failtestf "replay read failed: %s" (DatasetError.describe e)
                | Ok replayVersion ->
                    Expect.equal
                        replayVersion.ContentHash
                        originalVersion.ContentHash
                        "identical-content replay dedups through content addressing"

                    Expect.equal
                        (Map.tryFind AssemblyProvenance.SpecHashKey replayVersion.Metadata)
                        (Map.tryFind AssemblyProvenance.SpecHashKey originalVersion.Metadata)
                        "unchanged sources — same spec hash"
        }

        testCaseAsync "changed source — the replay re-binds to the latest vintage and provenance names it"
        <| async {
            let datasets, executor = freshStores ()
            let audit = RecordingAuditLog()
            let! producedRef = seedAndAssemble datasets executor "team-1"

            // The "weekly refresh": upstream appends v2 with a new row.
            let! refreshed = datasets.Create("team-1", "source", sourceSchema, rowsV2, "refresh", Map.empty, Versioned)

            match refreshed with
            | Error e -> failtestf "refresh failed: %s" (DatasetError.describe e)
            | Ok v -> Expect.equal v.Version 2 "upstream refresh is v2"

            match! DatasetRevintage.revintage executor datasets (audit :> IAuditLog) producedRef "operator-1" with
            | Error e -> failtestf "revintage refused: %s" (RevintageError.describe e)
            | Ok produced ->
                let replayRef = produced["all"]

                let! replay = datasets.GetVersion("team-1", replayRef.DatasetId, replayRef.Version)

                match replay with
                | Error e -> failtestf "replay read failed: %s" (DatasetError.describe e)
                | Ok replayVersion ->
                    // Filter keeps spend >= 60: v1 → 2 rows; v2 adds
                    // east @ 65 → 3 rows. The distinct output proves the
                    // replay read the fresh vintage.
                    Expect.equal replayVersion.RowCount 3L "replay of the fresh vintage carries the new row"

                    Expect.stringContains
                        replayVersion.Metadata[AssemblyProvenance.SourcesKey]
                        "@v2"
                        "provenance names the fresh source vintage"

            // Audit carries spec ref + produced versions (GP 6).
            let rows =
                audit.Events
                |> List.choose (fun (_, e) ->
                    match e with
                    | DatasetRevintaged p -> Some p
                    | _ -> None)

            Expect.equal (List.length rows) 1 "one revintage audit row"
            Expect.stringContains rows.Head.SourceVersion "assembled" "audit names the spec-carrying source version"
            Expect.isNonEmpty rows.Head.ProducedVersions "audit names the produced versions"
        }

        testCaseAsync "a source that no longer resolves is a typed refusal"
        <| async {
            let datasets, executor = freshStores ()
            let audit = RecordingAuditLog()
            let! producedRef = seedAndAssemble datasets executor "team-1"

            // Delete the upstream source (Versioned → delete allowed).
            match! datasets.Delete("team-1", "source") with
            | Ok() -> ()
            | Error e -> failtestf "delete failed: %s" (DatasetError.describe e)

            match! DatasetRevintage.revintage executor datasets (audit :> IAuditLog) producedRef "operator-1" with
            | Error(RevintageError.SourceUnresolvable _) -> ()
            | other -> failtestf "expected SourceUnresolvable; got %A" other
        }

        testCaseAsync "a version with no recorded spec is NoRecordedSpec, and a missing version is VersionNotFound"
        <| async {
            let datasets, executor = freshStores ()
            let audit = RecordingAuditLog()

            let! created = datasets.Create("team-1", "plain", sourceSchema, rowsV1, "u1", Map.empty, Versioned)

            match created with
            | Error e -> failtestf "create failed: %s" (DatasetError.describe e)
            | Ok _ -> ()

            let plainRef: DatasetVersionRef = {
                ScopeId = "team-1"
                DatasetId = "plain"
                Version = 1
            }

            match! DatasetRevintage.revintage executor datasets (audit :> IAuditLog) plainRef "operator-1" with
            | Error(RevintageError.NoRecordedSpec _) -> ()
            | other -> failtestf "expected NoRecordedSpec; got %A" other

            let missingRef: DatasetVersionRef = {
                ScopeId = "team-1"
                DatasetId = "nope"
                Version = 1
            }

            match! DatasetRevintage.revintage executor datasets (audit :> IAuditLog) missingRef "operator-1" with
            | Error(RevintageError.VersionNotFound _) -> ()
            | other -> failtestf "expected VersionNotFound; got %A" other

            Expect.isEmpty audit.Events "refusals audit nothing here (success-only event)"
        }

        testCaseAsync "the revintage job handler round-trips its payload and maps refusals to JobResults"
        <| async {
            let datasets, executor = freshStores ()
            let audit = RecordingAuditLog()
            let! producedRef = seedAndAssemble datasets executor "team-1"

            let handler =
                DatasetRevintageJobHandler.create executor datasets (audit :> IAuditLog) silentLogger

            let ctx (payload: string) : JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = "team-1"
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "cron")
                Attempt = 1
                Trigger = CronTrigger "0 3 * * *"
                TriggerSource = ScheduledByCron
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = payload
                DeadLetterDestination = None
            }

            // Success path — a new version lands on cadence.
            let goodPayload = DatasetRevintage.serialiseJobPayload { SpecRef = producedRef }

            let! ok = handler.Execute(ctx goodPayload)
            Expect.equal ok Success "a well-formed payload replays to Success"

            let! versions = datasets.ListVersions("team-1", "assembled")
            Expect.equal (List.length versions) 2 "the scheduled replay produced a new version"

            // Malformed payload → PermanentFailure.
            match! handler.Execute(ctx "{ not json") with
            | PermanentFailure _ -> ()
            | other -> failtestf "expected PermanentFailure for a malformed payload; got %A" other

            // Unresolvable source → TransientFailure (a lagging upstream may
            // land before the retry).
            match! datasets.Delete("team-1", "source") with
            | Ok() -> ()
            | Error e -> failtestf "delete failed: %s" (DatasetError.describe e)

            match! handler.Execute(ctx goodPayload) with
            | TransientFailure _ -> ()
            | other -> failtestf "expected TransientFailure for an unresolvable source; got %A" other

            // No recorded spec → PermanentFailure.
            let! plain = datasets.Create("team-1", "plain", sourceSchema, rowsV1, "u1", Map.empty, Versioned)

            match plain with
            | Error e -> failtestf "create failed: %s" (DatasetError.describe e)
            | Ok _ -> ()

            let plainPayload =
                DatasetRevintage.serialiseJobPayload {
                    SpecRef = {
                        ScopeId = "team-1"
                        DatasetId = "plain"
                        Version = 1
                    }
                }

            match! handler.Execute(ctx plainPayload) with
            | PermanentFailure _ -> ()
            | other -> failtestf "expected PermanentFailure for an unrecorded spec; got %A" other
        }
    ]