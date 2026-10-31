// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1092 — the run lifecycle as one pure state machine over the run
/// record (`FactTableRun`), and the record's stored form.
///
/// The laws: a run is `Open` from `opened` until exactly one of `commit`,
/// `reject` or `abandon` ends it; every transition is reached only through
/// `tryOpen`, which refuses an ended run by name, so no transition out of a
/// terminal outcome can be written; and a record written before the options
/// were kept on it reads with `RunOptions.empty`. Every writer executes these
/// transitions (`IFactTableWriterContract` binds the writers), so the laws
/// here are the lifecycle every writer keeps.
module ToolUp.Platform.Tests.InProcess.FactTableRunTests

open System
open System.Text.Json
open Expecto
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Grounding

let private t0 = DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc)

let private options: RunOptions = {
    RunOptions.Inputs = [ "sha256:" + String('a', 64) ]
    RunOptions.Slice =
        Some {
            RunSlice.From = DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc)
            RunSlice.To = DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc)
            RunSlice.Label = Some "2026-10-29"
        }
    RunOptions.Method =
        Some {
            RunMethod.MethodId = "dailytrade.derive"
            RunMethod.MethodVersion = "v1"
        }
}

let private opened = FactTableRun.opened "sku-daily" "run-1" 3 t0 7L options

let private commit: FactTableCommit = {
    Watermark = {
        TableId = "sku-daily"
        Sequence = 8L
        CommittedAt = t0.AddMinutes 5.0
        ContentDigest = String('c', 64)
    }
    RowCount = 2
    FactsWritten = 4
    Change = {
        New = 2
        Changed = 0
        Unchanged = 0
        Removed = 0
        LargestMovers = []
    }
    BatchDigest = String('d', 64)
}

let private openOf (run: FactTableRunRecord) : OpenFactTableRun =
    match FactTableRun.tryOpen run with
    | Ok o -> o
    | Error e -> failtestf "expected an open run, got one %s" e.Status

/// Every way a run ends, from the same open run.
let private ended: (string * FactTableRunRecord) list = [
    "committed", FactTableRun.commit commit (openOf opened)
    "rejected", FactTableRun.reject "two rows share a subject" (openOf opened)
    "abandoned", FactTableRun.abandon "the producer stopped" (openOf opened)
]

let private identity (run: FactTableRunRecord) =
    run.TableId, run.RunId, run.SchemaVersion, run.OpenedAt, run.BaseSequence, run.Options, run.RecordVersion

let private jsonOptions = FableConverters.create ()

let private lifecycleTests =
    testList "Phase 1092 — FactTableRun, the run lifecycle as a pure state machine" [

        test "a run is opened Open, with nothing staged, in the current record format, carrying its options" {
            Expect.equal opened.Status FactTableRunStatus.Open "open"
            Expect.equal opened.StagedRows 0 "nothing staged"
            Expect.equal opened.RecordVersion FactTableRunRecord.CurrentVersion "current format"
            Expect.equal opened.Options options "what it declared"
            Expect.isFalse (FactTableRun.hasEnded opened) "an open run has not ended"
        }

        test "staging keeps the run open and counts the rows" {
            let once = FactTableRun.stage 3 (openOf opened)
            let twice = FactTableRun.stage 2 (openOf once)
            Expect.equal twice.Status FactTableRunStatus.Open "still open"
            Expect.equal twice.StagedRows 5 "rows counted across batches"
            Expect.equal (identity twice) (identity opened) "nothing else moves"
        }

        test "commit, reject and abandon each end an open run, and move nothing but its status" {
            for name, run in ended do
                Expect.isTrue (FactTableRun.hasEnded run) $"{name} has ended"
                Expect.equal (identity run) (identity opened) $"{name} keeps the run's identity and options"

            let statuses = ended |> List.map (fun (_, run) -> run.Status)

            Expect.equal
                statuses
                [
                    FactTableRunStatus.Committed commit
                    FactTableRunStatus.Rejected "two rows share a subject"
                    FactTableRunStatus.Abandoned "the producer stopped"
                ]
                "each transition writes its own outcome"
        }

        test "no transition out of a terminal outcome: an ended run is refused at the one door, naming its status" {
            for name, run in ended do
                match FactTableRun.tryOpen run with
                | Ok _ -> failtestf "%s run was admitted to a transition" name
                | Error refused ->
                    Expect.equal refused.RunId run.RunId $"{name}: names the run"
                    Expect.equal refused.Status name $"{name}: names its status"
                    Expect.equal refused.Status (FactTableRun.statusName run.Status) "as the status renders"
        }

        test "only an open run reaches a transition: tryOpen admits exactly the Open status" {
            let all = ("open", opened) :: ended

            let admitted =
                all
                |> List.filter (fun (_, run) -> Result.isOk (FactTableRun.tryOpen run))
                |> List.map fst

            Expect.equal admitted [ "open" ] "open alone"

            Expect.equal
                (all |> List.filter (fun (_, run) -> FactTableRun.hasEnded run) |> List.map fst)
                [ "committed"; "rejected"; "abandoned" ]
                "and hasEnded is its complement — an orphan sweep, which reclaims only ended runs, never touches an open one"
        }

        test "a record round-trips through its stored form with its options" {
            let json =
                JsonSerializer.Serialize(FactTableRun.commit commit (openOf opened), jsonOptions)

            let read =
                JsonSerializer.Deserialize<FactTableRunRecord>(json, jsonOptions)
                |> FactTableRunRecord.ofStored

            Expect.equal read (FactTableRun.commit commit (openOf opened)) "every member survives"
        }

        test "a record written before the options were kept reads with RunOptions.empty and version 0" {
            // The pre-1092 shape: no Options, no RecordVersion.
            let legacy =
                """{"TableId":"sku-daily","RunId":"run-0","SchemaVersion":1,"OpenedAt":"2026-10-01T00:00:00Z","BaseSequence":0,"StagedRows":0,"Status":"Open"}"""

            let read =
                JsonSerializer.Deserialize<FactTableRunRecord>(legacy, jsonOptions)
                |> FactTableRunRecord.ofStored

            Expect.equal read.Options RunOptions.empty "no options"
            Expect.equal read.RecordVersion 0 "the record says it predates them"
            Expect.equal read.RunId "run-0" "and the rest reads as written"
            Expect.equal read.Status FactTableRunStatus.Open "including its status"
        }
    ]

// ─── The one run envelope ────────────────────────────────────────────

let private holdout: DatasetVersionRef = {
    ScopeId = "team-a"
    DatasetId = "sales"
    Version = 3
}

let private evaluation (scopeId: string) : EvaluationRun = {
    RunId = EvaluationRun.id "artifact-1" holdout
    ScopeId = scopeId
    ArtifactKeyHash = "artifact-1"
    ProviderId = "reference"
    ProviderVersion = "1"
    Holdout = holdout
    Predictions = {
        holdout with
            DatasetId = "sales-predictions"
    }
    Metrics = Map.empty
    Plan = None
    EvaluatedBy = "test"
    EvaluatedAt = DateTimeOffset(t0)
}

let private runRecordTests =
    testList "RunRecord — one envelope for every kind of run" [

        test "a run's id is the canonical frame's digest of its kind's label and its fields" {
            Expect.equal
                (RunRecord.id RunKind.Evaluation [ "x" ])
                (CanonicalFrame.digest [ "evaluation"; "x" ])
                "the label first, then the fields"

            let ids = RunKind.all |> List.map (fun kind -> RunRecord.id kind [ "x" ])
            Expect.equal (List.distinct ids) ids "two kinds over the same fields never share an id"
        }

        test "a fact-table run projects onto the envelope, outcome for outcome" {
            let scope = TestScope.resolved "team-a"
            let run = FactTableRun.toRunRecord scope RunProvenance.Computed

            let record = run opened
            Expect.equal record.Id (RunRecord.id RunKind.FactTableRun [ "sku-daily"; "run-1" ]) "the id"
            Expect.equal record.Kind RunKind.FactTableRun "the kind"
            Expect.equal record.Scope scope "the reader's scope"
            Expect.equal record.Subject "sku-daily" "the table"
            Expect.equal record.StartedAt t0 "opened at"
            Expect.equal record.Options options "what it declared"
            Expect.equal record.Outcome RunOutcome.Open "open"

            let ended (f: OpenFactTableRun -> FactTableRunRecord) =
                match FactTableRun.tryOpen opened with
                | Ok o -> (run (f o)).Outcome
                | Error e -> failtestf "open: %A" e

            Expect.equal (ended (FactTableRun.reject "bad rows")) (RunOutcome.Failed "bad rows") "rejected"
            Expect.equal (ended (FactTableRun.abandon "stopped")) (RunOutcome.Abandoned "stopped") "abandoned"
        }

        test "a modelling envelope projects in the scope it is stored under, and refuses any other" {
            let run = evaluation "team-a"

            match EvaluationRun.toRunRecord (TestScope.resolved "team-a") run with
            | Ok record ->
                Expect.equal record.Id (RunRecord.id RunKind.Evaluation [ run.RunId ]) "the id"
                Expect.equal record.Subject "artifact-1" "the artifact"
                Expect.equal record.Outcome (RunOutcome.Succeeded(DatasetVersionRef.key run.Predictions)) "its result"
            | Error e -> failtestf "projection: %s" e

            match EvaluationRun.toRunRecord (TestScope.resolved "team-b") run with
            | Error reason -> Expect.stringContains reason "stored under shard 'team-a'" "refused by name"
            | Ok _ -> failtest "a run stored under another shard was read"

            Expect.isError (EvaluationRun.toRunRecord ResolvedScope.anonymous run) "the anonymous scope reads no shard"
        }
    ]

let tests = testList "FactTableRun and RunRecord" [ lifecycleTests; runRecordTests ]