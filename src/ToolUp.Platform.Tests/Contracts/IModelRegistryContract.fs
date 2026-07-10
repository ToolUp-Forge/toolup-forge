// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IModelRegistryContract

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

// ─── Phase 453 — IModelRegistry conformance pack ────────────────────────
//
// Bound to the blob-backed default (`BlobModelRegistry` over a
// `DataObjectStore` over `LocalFileStorage`). Asserts the portable contract:
//   * a fit outcome registers once under its composite key; a re-register of
//     the identical key is idempotent (no duplicate, no new version);
//   * a changed seed / vintage yields a distinct artifact;
//   * query-by-spec-hash returns an artifact's full vintage history in one
//     call; query-by-dataset-version / -status filter correctly;
//   * status transitions honour the lifecycle graph; a non-admin cannot
//     `Approve` (GP 4); an illegal edge is refused as typed data;
//   * every transition — allowed or denied — is audited (GP 6);
//   * registration emits the artifact → dataset-version lineage edge (8a);
//   * scope isolation (GP 4).

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// Records every lineage link so a test can assert the artifact →
/// dataset-version edge. The query methods return empty graphs — the
/// registry only ever calls `Record`.
type private RecordingLineageStore() =
    let links = ResizeArray<string * LineageLink>()
    member _.Links = List.ofSeq links

    interface ILineageStore with
        member _.Record(scopeId, link) = async {
            links.Add(scopeId, link)
            return Ok()
        }

        member _.GetAncestors(_, objectId) = async {
            return {
                Root = objectId
                Nodes = []
                Edges = []
            }
        }

        member _.GetDescendants(_, objectId) = async {
            return {
                Root = objectId
                Nodes = []
                Edges = []
            }
        }

        member _.GetPath(_, _, _) = async { return None }

        member _.Erase(_, _, _, _) = async {
            return
                Ok {
                    HandlerName = "recording-lineage"
                    RecordsAffected = 0
                    Note = None
                }
        }

/// A fresh blob-backed registry over its own temp directory, plus the audit
/// + lineage recorders wired into it. Each call is fully isolated.
let private freshRegistry () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-mr-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let audit = RecordingAuditLog()
    let lineage = RecordingLineageStore()
    let registry = BlobModelRegistry.createWithLineage dataObjects audit lineage
    registry, audit, lineage

/// A deterministic `FitOutcome` with the given identity components — the
/// input `IModelRegistry.Register` consumes. The composite key is computed
/// exactly as the fit envelope would (Phase 449).
let private outcome (specHash: string) (datasetVersion: string) (seed: int64) : FitOutcome =
    let key = FitCompositeKey.compute specHash datasetVersion seed "reference" "1.0.0"

    {
        CompositeKey = key
        ArtifactRef = {
            ArtifactId = key.Hash
            ContentHash = "content-" + key.Hash
            ByteLength = 128L
        }
        Diagnostics = Map [ "mean", 0.5; "abs_mean", 0.1 ]
        GateVerdicts = [
            {
                Name = "mean"
                Threshold = 0.0
                Direction = GateDirection.AtLeast
                Observed = 0.5
                Passed = true
            }
        ]
        DurationMs = 0L
        CostUnits = 0.0
    }

let private okv =
    function
    | Ok v -> v
    | Error e -> failtestf "expected Ok; got %s" (ModelRegistryError.describe e)

let tests =
    testList "ModelRegistry — IModelRegistry contract" [

        testCaseAsync "a fit outcome registers once under its composite key; the artifact carries the fit core"
        <| async {
            let registry, audit, _ = freshRegistry ()
            let o = outcome "spec-a" "team-1/sales@v1" 42L

            let! result = registry.Register("team-1", o, "alice", Map [ "branch", "main" ], "first fit")
            let artifact = okv result

            Expect.equal artifact.CompositeKey o.CompositeKey "the artifact is keyed by the fit composite key"
            Expect.equal artifact.Status ModelArtifactStatus.Fitted "a registered artifact is born Fitted"
            Expect.equal artifact.Version 1 "the first registration is version 1"
            Expect.equal artifact.ArtifactRef o.ArtifactRef "the fitted-parameter blob reference is carried"
            Expect.equal artifact.Diagnostics o.Diagnostics "the fit diagnostics are carried"
            Expect.equal artifact.GateVerdicts o.GateVerdicts "the gate verdicts are carried"
            Expect.equal artifact.Annotations (Map [ "branch", "main" ]) "structured annotations are stored"
            Expect.equal artifact.Notes "first fit" "the free-text note is stored"

            let registered =
                audit.Events
                |> List.choose (function
                    | _, ModelArtifactRegistered p -> Some p
                    | _ -> None)

            Expect.equal registered.Length 1 "exactly one ModelArtifactRegistered row"
            Expect.equal registered.[0].CompositeKeyHash o.CompositeKey.Hash "the audit row carries the composite key"
        }

        testCaseAsync "re-registering an identical composite key is idempotent — not a new artifact, no new version"
        <| async {
            let registry, audit, _ = freshRegistry ()
            let o = outcome "spec-a" "team-1/sales@v1" 42L

            let! first = registry.Register("team-1", o, "alice", Map.empty, "")
            let a = okv first
            let! second = registry.Register("team-1", o, "bob", Map [ "branch", "other" ], "re-run")
            let b = okv second

            Expect.equal b.Version 1 "a re-register does not append a version"
            Expect.equal b.RegisteredBy a.RegisteredBy "the original registrar is preserved (idempotent)"
            Expect.equal b.Annotations a.Annotations "the re-register does not overwrite annotations"

            let registered =
                audit.Events
                |> List.filter (function
                    | _, ModelArtifactRegistered _ -> true
                    | _ -> false)

            Expect.equal registered.Length 1 "an idempotent re-register emits no second audit row"
        }

        testCaseAsync "a changed seed or vintage yields a distinct artifact"
        <| async {
            let registry, _, _ = freshRegistry ()
            let! a = registry.Register("team-1", outcome "spec-a" "team-1/sales@v1" 1L, "alice", Map.empty, "")
            let! b = registry.Register("team-1", outcome "spec-a" "team-1/sales@v1" 2L, "alice", Map.empty, "")
            let! c = registry.Register("team-1", outcome "spec-a" "team-1/sales@v2" 1L, "alice", Map.empty, "")

            let ka = (okv a).CompositeKey.Hash
            let kb = (okv b).CompositeKey.Hash
            let kc = (okv c).CompositeKey.Hash

            Expect.notEqual ka kb "a changed seed is a different artifact"
            Expect.notEqual ka kc "a changed vintage is a different artifact"
            Expect.notEqual kb kc "seed and vintage are both part of the identity"
        }

        testCaseAsync "query-by-spec-hash returns the full vintage history of one modelling decision in one call"
        <| async {
            let registry, _, _ = freshRegistry ()
            // Same spec, three vintages/seeds — one modelling decision.
            let! _ = registry.Register("team-1", outcome "spec-a" "team-1/sales@v1" 1L, "alice", Map.empty, "")
            let! _ = registry.Register("team-1", outcome "spec-a" "team-1/sales@v2" 1L, "alice", Map.empty, "")
            let! _ = registry.Register("team-1", outcome "spec-a" "team-1/sales@v3" 1L, "alice", Map.empty, "")
            // A different modelling decision in the same scope.
            let! _ = registry.Register("team-1", outcome "spec-b" "team-1/sales@v1" 1L, "alice", Map.empty, "")

            let! bySpec = registry.QueryBySpecHash("team-1", "spec-a")
            Expect.equal bySpec.Length 3 "all three vintages of spec-a are returned"
            Expect.isTrue (bySpec |> List.forall (fun a -> a.CompositeKey.SpecHash = "spec-a")) "only spec-a artifacts"

            let! byVintage = registry.QueryByDatasetVersion("team-1", "team-1/sales@v1")
            Expect.equal byVintage.Length 2 "two models were fit on vintage v1 (spec-a + spec-b)"
        }

        testCaseAsync "query-by-status filters on the lifecycle state"
        <| async {
            let registry, _, _ = freshRegistry ()
            let o1 = outcome "spec-a" "team-1/sales@v1" 1L
            let o2 = outcome "spec-b" "team-1/sales@v1" 1L
            let! _ = registry.Register("team-1", o1, "alice", Map.empty, "")
            let! _ = registry.Register("team-1", o2, "alice", Map.empty, "")

            // Approve o1 (Owner).
            let! _ =
                registry.TransitionStatus("team-1", o1.CompositeKey.Hash, ModelArtifactStatus.Approved, Owner, "alice")

            let! fitted = registry.QueryByStatus("team-1", ModelArtifactStatus.Fitted)
            let! approved = registry.QueryByStatus("team-1", ModelArtifactStatus.Approved)

            Expect.equal fitted.Length 1 "one artifact remains Fitted"
            Expect.equal approved.Length 1 "one artifact was Approved"
            Expect.equal approved.[0].CompositeKey.Hash o1.CompositeKey.Hash "the approved artifact is o1"
        }

        testCaseAsync "an Owner can Approve; the transition appends a version and is audited"
        <| async {
            let registry, audit, _ = freshRegistry ()
            let o = outcome "spec-a" "team-1/sales@v1" 1L
            let! _ = registry.Register("team-1", o, "alice", Map.empty, "")

            let! result =
                registry.TransitionStatus("team-1", o.CompositeKey.Hash, ModelArtifactStatus.Approved, Admin, "carol")

            let updated = okv result
            Expect.equal updated.Status ModelArtifactStatus.Approved "the status advanced to Approved"
            Expect.equal updated.Version 2 "a transition appends a version (GP 5)"

            let transitioned =
                audit.Events
                |> List.choose (function
                    | _, ModelArtifactTransitioned p -> Some p
                    | _ -> None)

            Expect.equal transitioned.Length 1 "exactly one ModelArtifactTransitioned row"
            Expect.equal transitioned.[0].FromStatus "Fitted" "the from-status is audited"
            Expect.equal transitioned.[0].ToStatus "Approved" "the to-status is audited"
            Expect.equal transitioned.[0].ActorUserId "carol" "the actor is audited"
        }

        testCaseAsync "a non-admin cannot Approve — refused as Forbidden and audited"
        <| async {
            let registry, audit, _ = freshRegistry ()
            let o = outcome "spec-a" "team-1/sales@v1" 1L
            let! _ = registry.Register("team-1", o, "alice", Map.empty, "")

            let! result =
                registry.TransitionStatus(
                    "team-1",
                    o.CompositeKey.Hash,
                    ModelArtifactStatus.Approved,
                    Member,
                    "mallory"
                )

            match result with
            | Error(ModelRegistryError.Forbidden _) -> ()
            | other -> failtestf "a Member Approve must be Forbidden; got %A" other

            // The artifact stays Fitted.
            let! current = registry.Get("team-1", o.CompositeKey.Hash)

            Expect.equal
                (okv current).Status
                ModelArtifactStatus.Fitted
                "a refused transition does not change the status"

            let denied =
                audit.Events
                |> List.choose (function
                    | _, ModelArtifactTransitionDenied p -> Some p
                    | _ -> None)

            Expect.equal denied.Length 1 "the refusal is audited (GP 6)"
            Expect.equal denied.[0].AttemptedStatus "Approved" "the attempted status is audited"
        }

        testCaseAsync "an illegal lifecycle edge is refused as typed data"
        <| async {
            let registry, _, _ = freshRegistry ()
            let o = outcome "spec-a" "team-1/sales@v1" 1L
            let! _ = registry.Register("team-1", o, "alice", Map.empty, "")

            // Fitted → Draft is not a legal edge; even as an Owner.
            let! result =
                registry.TransitionStatus("team-1", o.CompositeKey.Hash, ModelArtifactStatus.Draft, Owner, "alice")

            match result with
            | Error(ModelRegistryError.IllegalTransition(ModelArtifactStatus.Fitted, ModelArtifactStatus.Draft)) -> ()
            | other -> failtestf "expected IllegalTransition Fitted → Draft; got %A" other
        }

        testCaseAsync "Retire is legal from Fitted and available without an elevated role"
        <| async {
            let registry, _, _ = freshRegistry ()
            let o = outcome "spec-a" "team-1/sales@v1" 1L
            let! _ = registry.Register("team-1", o, "alice", Map.empty, "")

            let! result =
                registry.TransitionStatus("team-1", o.CompositeKey.Hash, ModelArtifactStatus.Retired, Member, "dan")

            Expect.equal (okv result).Status ModelArtifactStatus.Retired "Retire is a legal non-elevated transition"
        }

        testCaseAsync "registration emits the artifact → dataset-version lineage edge (8a)"
        <| async {
            let registry, _, lineage = freshRegistry ()
            let o = outcome "spec-a" "team-1/sales@v1" 1L
            let! _ = registry.Register("team-1", o, "alice", Map.empty, "")

            Expect.equal lineage.Links.Length 1 "exactly one lineage edge is emitted on registration"
            let scope, link = lineage.Links.[0]
            Expect.equal scope "team-1" "the edge is recorded under the artifact's scope"
            Expect.equal link.FromObjectId "team-1/sales@v1" "the edge originates at the dataset vintage"
            Expect.equal link.ToObjectId o.CompositeKey.Hash "the edge terminates at the artifact"
        }

        testCaseAsync "artifacts are scope-isolated (GP 4)"
        <| async {
            let registry, _, _ = freshRegistry ()
            let o = outcome "spec-a" "team-1/sales@v1" 1L
            let! _ = registry.Register("team-a", o, "alice", Map.empty, "")

            let! crossScope = registry.Get("team-b", o.CompositeKey.Hash)

            match crossScope with
            | Error ModelRegistryError.NotFound -> ()
            | other -> failtestf "another scope must not see the artifact; got %A" other

            let! bySpecOtherScope = registry.QueryBySpecHash("team-b", "spec-a")
            Expect.isEmpty bySpecOtherScope "a query in another scope returns nothing"
        }

        testCaseAsync "Get on an unknown key is NotFound"
        <| async {
            let registry, _, _ = freshRegistry ()
            let! result = registry.Get("team-1", "no-such-hash")

            match result with
            | Error ModelRegistryError.NotFound -> ()
            | other -> failtestf "expected NotFound; got %A" other
        }
    ]