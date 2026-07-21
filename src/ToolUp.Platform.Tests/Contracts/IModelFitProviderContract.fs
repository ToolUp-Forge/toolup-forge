// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IModelFitProviderContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.ModelProviders.Reference

// ─── Phase 449 — model-fit envelope conformance pack ────────────────────
//
// Bound to the in-tree reference provider (a trivial deterministic fitter).
// Asserts the portable contract of the envelope + provider seam:
//   * a registered provider fits through the envelope; the outcome carries
//     diagnostics + gate verdicts + the composite key;
//   * a re-run of the identical seeded request reproduces the outcome
//     exactly (plan D4);
//   * a failed gate is a typed, audited verdict — not an exception, not a
//     judgement (both gate directions + fail-closed on a missing diagnostic);
//   * an unknown provider kind is refused as typed data;
//   * a duplicate provider kind is rejected at registry construction;
//   * every run is audited under the request scope carrying the composite
//     key (GP 4 + GP 6);
//   * the `IJobHandler` maps parse / envelope failures to the right
//     `JobResult`.

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

let private provider: IModelFitProvider = ReferenceModelFitProvider.create ()
let private registry = ModelFitProviderRegistry [ provider ]

/// A `FitRequest` against the reference provider under `scope`, seeded by
/// `seed`, with the given gates.
let private request (scope: string) (seed: int64) (gates: GateSpec list) : FitRequest = {
    ScopeId = scope
    DatasetVersion = {
        ScopeId = scope
        DatasetId = "sales-panel"
        Version = 3
    }
    SpecRef = ModelSpecRef.ofPayload """{"opaque":"provider-spec"}"""
    ProviderKind = ReferenceModelFitProvider.Kind
    Seed = seed
    Gates = gates
}

/// Only the model-fit payloads of the recorded trail, in order.
let private modelFitEvents (audit: RecordingAuditLog) = audit.Events |> List.map snd

let tests =
    testList "ModelFit — IModelFitProvider envelope contract" [

        testCaseAsync "a registered provider fits; the outcome carries the composite key + diagnostics + verdicts"
        <| async {
            let audit = RecordingAuditLog()

            let req =
                request "team-1" 42L [
                    {
                        Name = "mean"
                        Threshold = 0.0
                        Direction = GateDirection.AtLeast
                    }
                ]

            let! result = ModelFitEnvelope.runFit registry audit req

            match result with
            | Error e -> failtestf "expected a fitted outcome; got %s" (ModelFitError.describe e)
            | Ok outcome ->
                Expect.isNonEmpty outcome.Diagnostics "the provider must report diagnostics"
                Expect.equal outcome.GateVerdicts.Length 1 "one verdict per requested gate"
                Expect.equal outcome.CompositeKey.SpecHash req.SpecRef.SpecHash "composite key carries the spec hash"

                Expect.equal
                    outcome.CompositeKey.DatasetVersion
                    (DatasetVersionRef.key req.DatasetVersion)
                    "composite key carries the dataset-version identity"

                Expect.equal outcome.CompositeKey.Seed 42L "composite key carries the seed"

                Expect.equal
                    outcome.CompositeKey.ProviderId
                    ReferenceModelFitProvider.Kind
                    "composite key carries the provider id"

                Expect.equal
                    outcome.CompositeKey.ProviderVersion
                    ReferenceModelFitProvider.Version
                    "composite key carries the provider version"

                Expect.isNotEmpty outcome.CompositeKey.Hash "the composite key is hashed"
                Expect.isNotEmpty outcome.ArtifactRef.ContentHash "the artifact is content-addressed"
        }

        testCaseAsync "re-running the identical seeded request reproduces the outcome exactly (plan D4)"
        <| async {
            let gates = [
                {
                    Name = "mean"
                    Threshold = 0.0
                    Direction = GateDirection.AtLeast
                }
            ]

            let! first = ModelFitEnvelope.runFit registry (RecordingAuditLog()) (request "team-1" 7L gates)
            let! second = ModelFitEnvelope.runFit registry (RecordingAuditLog()) (request "team-1" 7L gates)

            match first, second with
            | Ok a, Ok b -> Expect.equal a b "an identical seeded request must reproduce a byte-identical outcome"
            | _ -> failtest "both fits must succeed"
        }

        testCaseAsync "a different seed produces a different outcome"
        <| async {
            let! a = ModelFitEnvelope.runFit registry (RecordingAuditLog()) (request "team-1" 1L [])
            let! b = ModelFitEnvelope.runFit registry (RecordingAuditLog()) (request "team-1" 2L [])

            match a, b with
            | Ok oa, Ok ob ->
                Expect.notEqual oa.CompositeKey.Hash ob.CompositeKey.Hash "the seed is part of the composite identity"
            | _ -> failtest "both fits must succeed"
        }

        testCaseAsync "gates evaluate in both directions against the reported diagnostics"
        <| async {
            // Fit once to read the deterministic `mean` diagnostic, then
            // build gates around the observed value so both directions are
            // exercised with known pass / fail outcomes.
            let! baseline = ModelFitEnvelope.runFit registry (RecordingAuditLog()) (request "team-1" 99L [])

            let observed =
                match baseline with
                | Ok o -> o.Diagnostics.["mean"]
                | Error e -> failtestf "baseline fit failed: %s" (ModelFitError.describe e)

            let gates = [
                {
                    Name = "mean"
                    Threshold = observed - 0.1
                    Direction = GateDirection.AtLeast
                } // passes
                {
                    Name = "mean"
                    Threshold = observed + 0.1
                    Direction = GateDirection.AtLeast
                } // fails
                {
                    Name = "mean"
                    Threshold = observed + 0.1
                    Direction = GateDirection.AtMost
                } // passes
                {
                    Name = "mean"
                    Threshold = observed - 0.1
                    Direction = GateDirection.AtMost
                } // fails
            ]

            let! result = ModelFitEnvelope.runFit registry (RecordingAuditLog()) (request "team-1" 99L gates)

            match result with
            | Ok outcome ->
                let passed = outcome.GateVerdicts |> List.map (fun v -> v.Passed)
                Expect.equal passed [ true; false; true; false ] "AtLeast / AtMost must evaluate in both directions"
            | Error e -> failtestf "gate failure must be a verdict, not an error; got %s" (ModelFitError.describe e)
        }

        testCaseAsync "a failed gate is a typed, audited verdict — not an error"
        <| async {
            let audit = RecordingAuditLog()
            // `mean` is in [0,1); an AtMost -1.0 gate can never pass.
            let gates = [
                {
                    Name = "mean"
                    Threshold = -1.0
                    Direction = GateDirection.AtMost
                }
            ]

            let! result = ModelFitEnvelope.runFit registry audit (request "team-9" 5L gates)

            match result with
            | Error e -> failtestf "a failed gate must NOT surface as an error; got %s" (ModelFitError.describe e)
            | Ok outcome ->
                Expect.isFalse (FitOutcome.gatesPassed outcome) "the gate must have failed"
                Expect.equal (FitOutcome.failedGates outcome).Length 1 "exactly one failed verdict"

                let gateFailedRows =
                    modelFitEvents audit
                    |> List.choose (function
                        | ModelFitGateFailed p -> Some p
                        | _ -> None)

                Expect.equal gateFailedRows.Length 1 "exactly one ModelFitGateFailed audit row"
                Expect.equal gateFailedRows.[0].FailedGates [ "mean" ] "the failed gate name is audited"

                Expect.equal
                    gateFailedRows.[0].CompositeKeyHash
                    outcome.CompositeKey.Hash
                    "the gate-failed row carries the composite key"
        }

        testCaseAsync "no ModelFitGateFailed row when every gate passes"
        <| async {
            let audit = RecordingAuditLog()

            let gates = [
                {
                    Name = "mean"
                    Threshold = -1.0
                    Direction = GateDirection.AtLeast
                }
            ] // always passes

            let! _ = ModelFitEnvelope.runFit registry audit (request "team-1" 5L gates)

            let gateFailedRows =
                modelFitEvents audit
                |> List.filter (function
                    | ModelFitGateFailed _ -> true
                    | _ -> false)

            Expect.isEmpty gateFailedRows "a clean pass emits no gate-failed row"
        }

        testCaseAsync "a gate on a diagnostic the provider does not report fails closed"
        <| async {
            let gates = [
                {
                    Name = "not-a-real-diagnostic"
                    Threshold = 0.0
                    Direction = GateDirection.AtLeast
                }
            ]

            let! result = ModelFitEnvelope.runFit registry (RecordingAuditLog()) (request "team-1" 3L gates)

            match result with
            | Ok outcome ->
                let v = outcome.GateVerdicts.[0]
                Expect.isFalse v.Passed "a gate forge cannot confirm must fail closed"
                Expect.isTrue (Double.IsNaN v.Observed) "a missing diagnostic reports nan"
            | Error e -> failtestf "missing diagnostic is a verdict, not an error; got %s" (ModelFitError.describe e)
        }

        testCaseAsync "every run is audited under the request scope carrying the composite key"
        <| async {
            let audit = RecordingAuditLog()
            let! result = ModelFitEnvelope.runFit registry audit (request "team-iso" 11L [])

            let outcome =
                match result with
                | Ok o -> o
                | Error e -> failtestf "fit failed: %s" (ModelFitError.describe e)

            // Started + Completed, both under the request scope.
            let scopes = audit.Events |> List.map fst |> List.distinct
            Expect.equal scopes [ "team-iso" ] "audit rows are scoped to the request scope (GP 4)"

            let started =
                modelFitEvents audit
                |> List.tryPick (function
                    | ModelFitStarted p -> Some p
                    | _ -> None)

            let completed =
                modelFitEvents audit
                |> List.tryPick (function
                    | ModelFitCompleted p -> Some p
                    | _ -> None)

            match started, completed with
            | Some s, Some c ->
                Expect.equal s.CompositeKeyHash outcome.CompositeKey.Hash "started row carries the composite key"
                Expect.equal c.CompositeKeyHash outcome.CompositeKey.Hash "completed row carries the composite key"
            | _ -> failtest "both a ModelFitStarted and a ModelFitCompleted row must be emitted"
        }

        testCaseAsync "an unknown provider kind is refused as typed data"
        <| async {
            let audit = RecordingAuditLog()

            let req = {
                request "team-1" 1L [] with
                    ProviderKind = "no-such-provider"
            }

            let! result = ModelFitEnvelope.runFit registry audit req

            match result with
            | Error(ModelFitError.ProviderNotFound k) -> Expect.equal k "no-such-provider" "the refused kind is named"
            | other -> failtestf "expected ProviderNotFound; got %A" other

            Expect.isEmpty audit.Events "an unresolved fit emits no lifecycle rows"
            Expect.isNone (registry.TryResolve "no-such-provider") "the registry does not resolve an unknown kind"
        }

        test "a duplicate provider kind is rejected at registry construction" {
            Expect.throws
                (fun () ->
                    ModelFitProviderRegistry [
                        ReferenceModelFitProvider.create ()
                        ReferenceModelFitProvider.create ()
                    ]
                    |> ignore)
                "two providers of the same kind must fail loudly at construction"
        }

        testCaseAsync "the job handler maps a good payload to Success and a malformed payload to PermanentFailure"
        <| async {
            let audit = RecordingAuditLog()
            let handler = ModelFitJobHandler.create registry audit silentLogger

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

            let goodPayload = ModelFitEnvelope.serialiseRequest (request "team-1" 21L [])
            let! ok = handler.Execute(ctx goodPayload)
            Expect.equal ok Success "a well-formed FitRequest payload runs to Success"

            let! bad = handler.Execute(ctx "{ not json")

            match bad with
            | PermanentFailure _ -> ()
            | other -> failtestf "a malformed payload must be a PermanentFailure; got %A" other
        }

        testCaseAsync "the job handler maps an unknown provider kind to PermanentFailure"
        <| async {
            let handler = ModelFitJobHandler.create registry (RecordingAuditLog()) silentLogger

            let req = {
                request "team-1" 1L [] with
                    ProviderKind = "no-such-provider"
            }

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = "team-1"
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
                Attempt = 1
                Trigger = Manual
                TriggerSource = ScheduledManually "system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = ModelFitEnvelope.serialiseRequest req
                DeadLetterDestination = None
            }

            let! result = handler.Execute ctx

            match result with
            | PermanentFailure _ -> ()
            | other -> failtestf "an unknown kind is terminal; expected PermanentFailure, got %A" other
        }
    ]

/// Phase 603 — SpecHash opacity contract. The composite key assumes
/// `SpecHash` is **submitter-minted and opaque**: forge stores, keys, and
/// compares exactly the hash it was handed — never re-derives, normalises,
/// or validates it against the payload. These cases make that restraint
/// executable: a future "helpful" server-side re-hash (or payload
/// canonicalisation) fails them by construction, because every case pins a
/// declared hash that deliberately does NOT match any canonical hash of
/// its payload.
let opacityTests =
    let freshRegistry () =
        let root =
            IO.Path.Combine(IO.Path.GetTempPath(), "toolup-opacity-tests-" + Guid.NewGuid().ToString("N"))

        IO.Directory.CreateDirectory root |> ignore

        let blob = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
        let dataObjects = DataObjectStore(blob) :> IDataObjectStore

        BlobModelRegistry.create dataObjects (RecordingAuditLog() :> IAuditLog)

    /// A request whose declared spec hash is the caller's own token — by
    /// construction NOT the SHA-256 of the payload.
    let opaqueRequest (scope: string) (payload: string) (declaredHash: string) (seed: int64) : FitRequest = {
        ScopeId = scope
        DatasetVersion = {
            ScopeId = scope
            DatasetId = "sales-panel"
            Version = 3
        }
        SpecRef = {
            Payload = payload
            SpecHash = declaredHash
        }
        ProviderKind = ReferenceModelFitProvider.Kind
        Seed = seed
        Gates = []
    }

    testList "ModelFit — SpecHash opacity (Phase 603)" [
        testCaseAsync "a declared hash that matches no canonical hash of the payload is stored and keyed verbatim"
        <| async {
            let payload = """{"opaque":"spec"}"""
            let declared = "my-own-canonicalisation-rule-hash-1"

            Expect.notEqual
                declared
                (ModelSpecRef.hashOf payload)
                "the case is only meaningful when the declared hash differs from forge's own hashing rule"

            let! result =
                ModelFitEnvelope.runFit registry (RecordingAuditLog()) (opaqueRequest "team-1" payload declared 7L)

            match result with
            | Error e -> failtestf "fit refused: %s" (ModelFitError.describe e)
            | Ok outcome ->
                Expect.equal
                    outcome.CompositeKey.SpecHash
                    declared
                    "the composite key carries the declared hash verbatim"

                // Registered + retrievable by exactly that hash.
                let modelRegistry = freshRegistry ()

                let! registered = modelRegistry.Register("team-1", outcome, "u1", Map.empty, "")

                match registered with
                | Error e -> failtestf "register failed: %s" (ModelRegistryError.describe e)
                | Ok artifact ->
                    Expect.equal artifact.CompositeKey.SpecHash declared "the registry keys the declared hash"

                let! byDeclared = modelRegistry.QueryBySpecHash("team-1", declared)
                Expect.equal (List.length byDeclared) 1 "queryable by exactly the declared hash"

                // A re-hashing decorator would surface here: the canonical
                // hash of the payload must match NOTHING.
                let! byCanonical = modelRegistry.QueryBySpecHash("team-1", ModelSpecRef.hashOf payload)
                Expect.isEmpty byCanonical "forge never re-derives the hash from the payload"
        }

        testCaseAsync
            "identical payloads under distinct declared hashes are distinct artifacts — the hash is the identity"
        <| async {
            let payload = """{"opaque":"same-payload"}"""
            let modelRegistry = freshRegistry ()

            let fitAndRegister (declared: string) = async {
                let! result =
                    ModelFitEnvelope.runFit registry (RecordingAuditLog()) (opaqueRequest "team-1" payload declared 7L)

                match result with
                | Error e -> return failtestf "fit refused: %s" (ModelFitError.describe e)
                | Ok outcome ->
                    match! modelRegistry.Register("team-1", outcome, "u1", Map.empty, "") with
                    | Error e -> return failtestf "register failed: %s" (ModelRegistryError.describe e)
                    | Ok artifact -> return artifact
            }

            let! a = fitAndRegister "declared-hash-A"
            let! b = fitAndRegister "declared-hash-B"

            Expect.notEqual a.CompositeKey.Hash b.CompositeKey.Hash "distinct declared hashes → distinct composite keys"

            let! byA = modelRegistry.QueryBySpecHash("team-1", "declared-hash-A")
            let! byB = modelRegistry.QueryBySpecHash("team-1", "declared-hash-B")
            Expect.equal (byA |> List.map ModelArtifact.id) [ ModelArtifact.id a ] "query by A returns exactly A"
            Expect.equal (byB |> List.map ModelArtifact.id) [ ModelArtifact.id b ] "query by B returns exactly B"
        }
    ]