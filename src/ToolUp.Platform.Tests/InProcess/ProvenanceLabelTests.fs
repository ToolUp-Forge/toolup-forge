// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ProvenanceLabelTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

// --- Phase 482 -- privacy-provenance labels -----------------------------
//
// Labels travel with the data: a version derived from labelled inputs
// inherits the union of their labels (enforced in the producing executors,
// not left to callers), labels are immutable except by an explicit audited
// declassify, and opt-in policy hooks refuse dispatch/export of labelled
// data. These tests cover round-trip, union propagation through assembly,
// the declassify lifecycle, the policy denials both ways, and the GP-13
// "unlabelled data is unchanged" default.

let private freshStore () : IDatasetStore =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-label-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    BlobDatasetStore.create dataObjects

let private okds =
    function
    | Ok v -> v
    | Error(e: DatasetError) -> failtestf "expected Ok; got %s" (DatasetError.describe e)

let private col name dtype role : DatasetColumn = {
    Name = name
    DType = dtype
    Nullable = false
    Role = role
}

let private keyed (valueCol: string) : DatasetSchema = {
    Columns = [
        col "unit" DatasetDType.Categorical DatasetColumnRole.PanelUnit
        col valueCol DatasetDType.Float DatasetColumnRole.Plain
    ]
}

let private rowsFor (v: float) = [
    for i in 1..3 ->
        {
            Cells = [ DatasetValue.Categorical $"u{i}"; DatasetValue.Float(v + float i) ]
        }
]

let private labelledMeta (labels: DataProvenanceLabel list) =
    DataProvenanceLabels.writeInto labels Map.empty

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

let tests =
    testList "ProvenanceLabels" [
        testCaseAsync "labels round-trip on a dataset version and are immutable metadata"
        <| async {
            let store = freshStore ()

            let labels = [
                DataProvenanceLabel.CleanRoomDerived("gate-1", 0.5)
                DataProvenanceLabel.Classified "pii"
            ]

            let! _ = store.Create("s", "d", keyed "v", rowsFor 0.0, "u", labelledMeta labels, Versioned)
            let! v = store.GetVersion("s", "d", 1)
            Expect.equal (okds v).Labels labels "the version carries exactly the labels it was created with"
        }

        testCaseAsync "unlabelled data is byte-for-byte unchanged (GP 13)"
        <| async {
            let store = freshStore ()
            let! _ = store.Create("s", "plain", keyed "v", rowsFor 0.0, "u", Map.empty, Versioned)
            let! v = store.GetVersion("s", "plain", 1)
            Expect.isEmpty (okds v).Labels "an unlabelled version carries no labels"

            Expect.isFalse
                (Map.containsKey DataProvenanceLabels.MetadataKey (okds v).Metadata)
                "no label sidecar leaks into user metadata"
        }

        testCaseAsync "assembly propagates the union of its inputs' labels automatically"
        <| async {
            let store = freshStore ()
            let executor = DatasetAssemblyExecutor.create store

            // Two labelled inputs sharing the unit key.
            let! _ =
                store.Create(
                    "s",
                    "a",
                    keyed "va",
                    rowsFor 0.0,
                    "u",
                    labelledMeta [
                        DataProvenanceLabel.Classified "a"
                        DataProvenanceLabel.CleanRoomDerived("g", 1.0)
                    ],
                    Versioned
                )

            let! _ =
                store.Create(
                    "s",
                    "b",
                    keyed "vb",
                    rowsFor 10.0,
                    "u",
                    labelledMeta [ DataProvenanceLabel.Classified "b" ],
                    Versioned
                )

            let aRef: DatasetVersionRef = {
                ScopeId = "s"
                DatasetId = "a"
                Version = 1
            }

            let bRef: DatasetVersionRef = {
                ScopeId = "s"
                DatasetId = "b"
                Version = 1
            }

            let spec: DatasetAssemblySpec = {
                Scope = "s"
                Base = AssemblySource.DatasetVersion aRef
                Transforms = [
                    AssemblyTransform.Join(
                        AssemblySource.DatasetVersion bRef,
                        [ "unit" ],
                        [ "unit" ],
                        AssemblyJoinKind.Inner
                    )
                ]
                Split = None
                OutputDatasetId = "joined"
                OutputRoles = []
                Policy = Versioned
            }

            let! result = executor.Assemble(spec, "assembler")

            let outRef =
                match result with
                | Ok m -> m["all"]
                | Error e -> failtestf "assemble failed: %s" (AssemblyError.describe e)

            let! outV = store.GetVersion(outRef.ScopeId, outRef.DatasetId, outRef.Version)
            let labels = (okds outV).Labels |> List.map DataProvenanceLabel.key |> Set.ofList

            Expect.equal
                labels
                (Set.ofList [
                    DataProvenanceLabel.key (DataProvenanceLabel.Classified "a")
                    DataProvenanceLabel.key (DataProvenanceLabel.CleanRoomDerived("g", 1.0))
                    DataProvenanceLabel.key (DataProvenanceLabel.Classified "b")
                ])
                "the derived version inherits the union of both inputs' labels — no caller code"
        }

        testCaseAsync "declassify is an explicit audited act producing a new unlabelled version; the original is intact"
        <| async {
            let store = freshStore ()
            let audit = RecordingAuditLog()
            let labels = [ DataProvenanceLabel.Classified "restricted" ]
            let! _ = store.Create("s", "d", keyed "v", rowsFor 0.0, "u", labelledMeta labels, Versioned)

            let! declassified = DatasetProvenance.declassify store audit "s" "d" 1 "admin-1" "approved for release"
            let newRef = okds declassified
            Expect.equal newRef.Version 2 "declassify creates a new version"

            let! newV = store.GetVersion("s", "d", 2)
            Expect.isEmpty (okds newV).Labels "the new version is unlabelled"

            let! origV = store.GetVersion("s", "d", 1)
            Expect.equal (okds origV).Labels labels "the original labelled version is left intact (GP 5)"

            let decls =
                audit.Events
                |> List.choose (fun (_, e) ->
                    match e with
                    | DatasetDeclassified p -> Some p
                    | _ -> None)

            Expect.equal decls.Length 1 "one declassify audit row"
            Expect.equal decls.Head.LabelCount 1 "the audit row records how many labels were removed"
            Expect.equal decls.Head.Actor "admin-1" "the audit row records the actor"
            Expect.equal decls.Head.Reason "approved for release" "the audit row records the justification"
        }

        test "dispatch policy refuses labelled data to a non-Isolated target, both ways" {
            let labels = [ DataProvenanceLabel.Classified "restricted" ]

            // Enforced: labelled + non-Isolated -> refused.
            Expect.isError
                (DataProvenancePolicy.checkDispatch DataProvenancePolicy.enforced labels false)
                "labelled data to a Standard worker is refused"
            // Enforced: labelled + Isolated -> allowed.
            Expect.isOk
                (DataProvenancePolicy.checkDispatch DataProvenancePolicy.enforced labels true)
                "labelled data to an Isolated worker is allowed"
            // Enforced: unlabelled -> always allowed.
            Expect.isOk
                (DataProvenancePolicy.checkDispatch DataProvenancePolicy.enforced [] false)
                "unlabelled data dispatches anywhere"
            // Permissive (GP 13 default): allowed regardless.
            Expect.isOk
                (DataProvenancePolicy.checkDispatch DataProvenancePolicy.permissive labels false)
                "the default policy enforces nothing"
        }

        test "export policy refuses raw export of labelled data" {
            let labels = [ DataProvenanceLabel.CleanRoomDerived("g", 0.1) ]

            Expect.isError
                (DataProvenancePolicy.checkExport DataProvenancePolicy.enforced labels)
                "raw export of labelled data is refused when enabled"

            Expect.isOk (DataProvenancePolicy.checkExport DataProvenancePolicy.enforced []) "unlabelled data exports"

            Expect.isOk
                (DataProvenancePolicy.checkExport DataProvenancePolicy.permissive labels)
                "the default policy enforces nothing"
        }

        testCaseAsync "an audited policy denial records a typed DatasetPolicyDenied row"
        <| async {
            let store = freshStore ()
            let audit = RecordingAuditLog()

            let! _ =
                store.Create(
                    "s",
                    "d",
                    keyed "v",
                    rowsFor 0.0,
                    "u",
                    labelledMeta [ DataProvenanceLabel.Classified "pii" ],
                    Versioned
                )

            let! v = store.GetVersion("s", "d", 1)

            let! result = DataProvenancePolicy.checkDispatchAudited audit (okds v) DataProvenancePolicy.enforced false

            Expect.isError result "the labelled version is refused a non-Isolated dispatch"

            let denials =
                audit.Events
                |> List.choose (fun (_, e) ->
                    match e with
                    | DatasetPolicyDenied p -> Some p
                    | _ -> None)

            Expect.equal denials.Length 1 "one policy-denied audit row"
            Expect.equal denials.Head.Policy "dispatch" "the row names which policy fired"
        }
    ]