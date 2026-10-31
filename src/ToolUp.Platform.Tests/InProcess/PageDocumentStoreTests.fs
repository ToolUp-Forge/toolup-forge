// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PageDocumentStoreTests

// The page store and the layer fold. Two halves:
//
//   * the fold and its inverse, pure — determinism, a conflict that names
//     both acts and is never dropped, and apply-then-invert as identity over
//     generated trees and ops (a seeded hand-rolled generator, the pack's
//     convention: no FsCheck here);
//   * the store in-process over the real `DataObjectStore`,
//     `PermissionStore` and event-store audit log on in-memory blobs — the
//     role gate, the audit rows, the content hash against the object
//     store's, seed / re-seed / detach, and the permission document's
//     `pages` round trip.

open System
open System.Text
open Expecto
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.Ops
open Fuaran.UI.Ops.Types
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ── Trees ──────────────────────────────────────────────────────────────

let private heading (id: string) (text: string) : Node<unit> =
    Fuaran.heading id {
        Defaults.heading with
            Level = 2
            Text = TextSource.Literal text
    }

let private box (id: string) (children: Node<unit> list) : Node<unit> =
    Fuaran.box id {
        Defaults.box with
            Children = children
    }

let private bytesOf (node: Node<unit>) = PageLayers.encodeDocument node

/// root ─ header(title) ─ body(a, b, c)
let private seedTree: Node<unit> =
    box "root" [
        box "header" [ heading "title" "Daily trade" ]
        box "body" [ heading "a" "Revenue"; heading "b" "Units"; heading "c" "Margin" ]
    ]

let private record (layer: PageLayer) (principal: string) (ops: TreeOp<unit> list) : LayerStream =
    let records =
        ops
        |> List.fold (fun records op -> OpStream.appendChainOnly PageLayers.stream (Actor.Human "author") op records) []

    {
        LayerStream.Layer = layer
        Principal = principal
        Records = records
    }

let private retitle (id: string) (text: string) : TreeOp<unit> =
    TreeOp.EditNode(
        NodeId id,
        NodeKind.Heading {
            Defaults.heading with
                Level = 2
                Text = TextSource.Literal text
        }
    )

// ── The generator ──────────────────────────────────────────────────────

let private randomTree (rng: Random) : Node<unit> =
    let mutable next = 0

    let fresh prefix =
        next <- next + 1
        sprintf "%s%d" prefix next

    // A third of the nodes carry a style of their own, so an inverse that
    // restored the default rather than the node's own style is caught.
    let styled (n: Node<unit>) =
        match rng.Next 3 with
        | 0 -> {
            n with
                Style =
                    Some {
                        Defaults.style with
                            Tone = ToneVariant.Warning
                    }
          }
        | _ -> n

    let rec node depth =
        if depth >= 3 || rng.Next 3 = 0 then
            heading (fresh "h") (fresh "t") |> styled
        else
            box (fresh "b") [ for _ in 1 .. rng.Next(0, 4) -> node (depth + 1) ] |> styled

    box "root" [ for _ in 1 .. rng.Next(1, 4) -> node 1 ]

let private nodesOf (tree: Node<unit>) : Node<unit> list =
    let rec walk (n: Node<unit>) =
        n
        :: (Introspect.getChildren n.Kind |> Option.defaultValue [] |> List.collect walk)

    walk tree

let private styles = [
    {
        Defaults.style with
            Tone = ToneVariant.Success
    }
    {
        Defaults.style with
            Tone = ToneVariant.Critical
    }
    Defaults.style
]

let rec private randomOp (rng: Random) (tree: Node<unit>) (serial: int) : TreeOp<unit> =
    let nodes = nodesOf tree
    let pick (xs: 'a list) = List.item (rng.Next(List.length xs)) xs

    let boxes =
        nodes |> List.filter (fun n -> Introspect.getChildren n.Kind |> Option.isSome)

    let nonRoot = nodes |> List.filter (fun n -> n.Id <> "root")

    let headings =
        nodes |> List.filter (fun n -> Introspect.getChildren n.Kind |> Option.isNone)

    match rng.Next 8 with
    | 0 -> TreeOp.InsertChild(NodeId (pick boxes).Id, heading (sprintf "new%d" serial) "inserted")
    | 1 when not nonRoot.IsEmpty -> TreeOp.RemoveNode(NodeId (pick nonRoot).Id)
    | 2 when not nonRoot.IsEmpty -> TreeOp.MoveNode(NodeId (pick nonRoot).Id, NodeId (pick boxes).Id)
    | 3 ->
        let parent = pick boxes
        let children = Introspect.getChildren parent.Kind |> Option.defaultValue []

        let shuffled =
            children |> List.map (fun c -> NodeId c.Id) |> List.sortBy (fun _ -> rng.Next())

        TreeOp.ReorderChildren(NodeId parent.Id, shuffled)
    | 4 -> TreeOp.UpdateStyle(NodeId (pick nodes).Id, pick styles)
    | 5 when not headings.IsEmpty -> retitle (pick headings).Id (sprintf "retitled %d" serial)
    | 6 ->
        let first = randomOp rng tree (serial * 10 + 1)

        match Apply.apply first tree with
        | Ok after -> TreeOp.Batch [ first; randomOp rng after (serial * 10 + 2) ]
        | Error _ -> TreeOp.Batch [ first ]
    | _ -> TreeOp.InsertChild(NodeId "root", box (sprintf "nb%d" serial) [ heading (sprintf "nh%d" serial) "x" ])

// ── The fold ───────────────────────────────────────────────────────────

let private foldTests =
    testList "the fold" [

        test "platform, team and individual fold to one document, deterministically" {
            let platform =
                record PageLayer.Platform "platform" [ retitle "title" "Daily trade, by site" ]

            let team =
                record PageLayer.Team "t1" [
                    TreeOp.ReorderChildren(NodeId "body", [ NodeId "c"; NodeId "a"; NodeId "b" ])
                ]

            let own = record PageLayer.Individual "alice" [ retitle "a" "Revenue (mine)" ]
            let first = PageLayers.fold seedTree [ own; team; platform ]
            let second = PageLayers.fold seedTree [ platform; team; own ]

            Expect.isEmpty first.Conflicts "nothing conflicts"
            Expect.equal (bytesOf first.Document) (bytesOf second.Document) "the order passed in does not matter"

            let expected =
                box "root" [
                    box "header" [ heading "title" "Daily trade, by site" ]
                    box "body" [ heading "c" "Margin"; heading "a" "Revenue (mine)"; heading "b" "Units" ]
                ]

            Expect.equal (bytesOf first.Document) (bytesOf expected) "every layer applied, platform first"
        }

        test "an individual op whose target the team removed is a conflict naming both acts, never dropped" {
            let team =
                record PageLayer.Team "t1" [ retitle "b" "Volume"; TreeOp.RemoveNode(NodeId "a") ]

            let own =
                record PageLayer.Individual "alice" [ retitle "a" "Revenue (mine)"; retitle "c" "Margin %" ]

            let fold = PageLayers.fold seedTree [ team; own ]

            match fold.Conflicts with
            | [ conflict ] ->
                Expect.equal conflict.Act.Layer PageLayer.Individual "the conflicting op is the individual's"
                Expect.equal conflict.Act.Seq 0 "its first act"

                Expect.equal
                    (PageLayers.encodeOp conflict.Op)
                    (PageLayers.encodeOp (retitle "a" "Revenue (mine)"))
                    "the op itself is carried"

                match conflict.Cause with
                | ConflictCause.Removed(NodeId "a", by) ->
                    Expect.equal
                        by
                        {
                            LayerAct.Layer = PageLayer.Team
                            Principal = "t1"
                            Seq = 1
                        }
                        "the team's removing act is named"
                | other -> failtestf "expected Removed naming the team's act, got %A" other
            | other -> failtestf "expected exactly one conflict, got %A" other

            Expect.isNone (Introspect.findNode (NodeId "a") fold.Document) "the removal stands"

            Expect.equal
                (Introspect.findNode (NodeId "c") fold.Document
                 |> Option.map (fun n -> bytesOf n))
                (Some(bytesOf (heading "c" "Margin %")))
                "the individual's other op still applies"
        }

        test "an op on a node the seed no longer holds is NotInDocument" {
            let own = record PageLayer.Individual "alice" [ retitle "gone" "x" ]
            let fold = PageLayers.fold seedTree [ own ]

            match fold.Conflicts with
            | [ {
                    Cause = ConflictCause.NotInDocument(NodeId "gone")
                } ] -> ()
            | other -> failtestf "expected NotInDocument, got %A" other
        }

        test "a conflicting member of a batch is diagnosed at the member" {
            let team = record PageLayer.Team "t1" [ TreeOp.RemoveNode(NodeId "b") ]

            let own =
                record PageLayer.Individual "alice" [ TreeOp.Batch [ retitle "a" "ok"; retitle "b" "removed below" ] ]

            match (PageLayers.fold seedTree [ team; own ]).Conflicts with
            | [ {
                    Cause = ConflictCause.Removed(NodeId "b",
                                                  {
                                                      LayerAct.Layer = PageLayer.Team
                                                      Seq = 0
                                                  })
                } ] -> ()
            | other -> failtestf "expected the batch's second member named, got %A" other
        }

        test "ops outside the stored vocabulary are refused, by name" {
            Expect.equal (PageLayers.outsideVocabulary (TreeOp.ReplaceRoot seedTree)) (Some "ReplaceRoot") "ReplaceRoot"

            Expect.equal
                (PageLayers.outsideVocabulary (TreeOp.Batch [ retitle "a" "x"; TreeOp.ReplaceRoot seedTree ]))
                (Some "ReplaceRoot")
                "inside a batch too"

            match PageLayers.invert (TreeOp.ReplaceRoot seedTree) seedTree with
            | Error(NotInvertible.OutsideVocabulary "ReplaceRoot") -> ()
            | other -> failtestf "expected OutsideVocabulary, got %A" other
        }

        test "a stored op round-trips through the canonical JSON" {
            let op =
                TreeOp.Batch [ retitle "a" "x"; TreeOp.MoveNode(NodeId "a", NodeId "header") ]

            match PageLayers.decodeOp (PageLayers.encodeOp op) with
            | Ok back -> Expect.equal (PageLayers.encodeOp back) (PageLayers.encodeOp op) "same bytes"
            | Error e -> failtestf "did not decode: %s" e
        }
    ]

let private inverseTests =
    testList "the inverse" [

        test "apply then invert is identity over generated trees and ops" {
            let rng = Random 1165
            let mutable applied = 0
            let mutable kinds = Set.empty

            for trial in 1..400 do
                let tree = randomTree rng
                let op = randomOp rng tree trial

                match Apply.apply op tree with
                | Error _ -> ()
                | Ok after ->
                    applied <- applied + 1
                    kinds <- kinds.Add(TreeOp.kindName op)

                    match PageLayers.invert op tree with
                    | Error e -> failtestf "trial %d: no inverse for %s: %A" trial (PageLayers.encodeOp op) e
                    | Ok inverse ->
                        match Apply.apply inverse after with
                        | Error e -> failtestf "trial %d: the inverse did not apply: %A" trial e
                        | Ok back ->
                            Expect.equal
                                (bytesOf back)
                                (bytesOf tree)
                                (sprintf "trial %d: %s undone" trial (PageLayers.encodeOp op))

            // The property is vacuous if the generator stopped producing ops
            // that apply, or stopped covering the vocabulary.
            Expect.isGreaterThan applied 250 "most generated ops apply"

            Expect.equal
                kinds
                (set [
                    "InsertChild"
                    "RemoveNode"
                    "MoveNode"
                    "ReorderChildren"
                    "UpdateStyle"
                    "EditNode"
                    "Batch"
                ])
                "every generated kind was exercised"
        }

        test "a script inverts to its members' inverses in reverse order" {
            let rng = Random 51165

            for trial in 1..100 do
                let tree = randomTree rng

                let script, after =
                    [ 1..4 ]
                    |> List.fold
                        (fun (ops, state) i ->
                            let op = randomOp rng state (trial * 100 + i)

                            match Apply.apply op state with
                            | Ok next -> ops @ [ op ], next
                            | Error _ -> ops, state)
                        ([], tree)

                match PageLayers.invertAll script tree with
                | Error(i, e) -> failtestf "trial %d: op %d has no inverse: %A" trial i e
                | Ok inverses ->
                    let back =
                        inverses
                        |> List.fold (fun state op -> state |> Result.bind (Apply.apply op)) (Ok after)

                    match back with
                    | Ok back -> Expect.equal (bytesOf back) (bytesOf tree) (sprintf "trial %d undone" trial)
                    | Error e -> failtestf "trial %d: the inverse script did not apply: %A" trial e
        }

        test "an op that does not apply has no inverse" {
            match PageLayers.invert (TreeOp.RemoveNode(NodeId "absent")) seedTree with
            | Error(NotInvertible.DoesNotApply _) -> ()
            | other -> failtestf "expected DoesNotApply, got %A" other
        }
    ]

// ── The store ──────────────────────────────────────────────────────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

type private Rig = {
    Store: IPageDocumentStore
    Objects: IDataObjectStore
    Permissions: IPermissionStore
    Audit: IAuditLog
}

let private roleTable =
    Map.ofList [
        ("t1", "owner"), TeamRole.Owner
        ("t1", "lead"), TeamRole.Admin
        ("t1", "member"), TeamRole.Member
        ("t1", "alice"), TeamRole.Member
        ("t2", "owner2"), TeamRole.Owner
    ]

let private rig (options: PageStoreOptions) : Rig =
    let objects = DataObjectStore(InMemoryBlobStorage()) :> IDataObjectStore
    let permissions = PermissionStore(InMemoryBlobStorage()) :> IPermissionStore

    let audit =
        AuditLog.EventStoreAuditLog(InMemoryEventStore.InMemoryEventStore(), silentLogger) :> IAuditLog

    let roles: TeamProviderResolver.TeamRoleLookup =
        fun team user -> async { return Map.tryFind (team, user) roleTable }

    {
        Store = BlobPageDocumentStore(objects, permissions, roles, audit, options) :> IPageDocumentStore
        Objects = objects
        Permissions = permissions
        Audit = audit
    }

let private teamScope (teamId: string) =
    StorageScopeResolver.ScopeResolution.ofStorageScope (StorageScope.ofKey true (ScopeKey.Team teamId))

let private member' (userId: string) (teamId: string) =
    AccessContext.unrestricted (TeamMember(userId, teamId))

let private admin (teamId: string) = {
    member' "operator" teamId with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private page = {
    PageRef.Module = "trade"
    Page = "daily"
}

let private seedOf (tree: Node<unit>) : PageSeed = {
    PageSeed.Document = Encoding.UTF8.GetBytes(bytesOf tree)
    Sidecar = Some(Encoding.UTF8.GetBytes """{"demanded":"opaque"}""")
}

let private run (a: Async<'T>) = Async.RunSynchronously a

let private expectOk (label: string) (r: Result<'T, PageStoreError>) : 'T =
    match r with
    | Ok v -> v
    | Error e -> failtestf "%s: %A" label e

let private trail (rig: Rig) (stream: string) (kind: string) =
    rig.Audit.GetAuditTrail(stream, None, Some kind) |> run

let private seeded (options: PageStoreOptions) =
    let rig = rig options

    rig.Store.Seed(teamScope "t1", admin "t1", page, seedOf seedTree)
    |> run
    |> expectOk "seed"
    |> ignore

    rig

let private platformTrail = AuditScope.streamKey AuditScope.PlatformScope

let private storeTests =
    testList "the store" [

        test "the seed is written once, by a platform admin, and audited" {
            let rig = rig PageStoreOptions.defaults

            match rig.Store.Seed(teamScope "t1", admin "t1", page, seedOf seedTree) |> run with
            | Ok(SeedOutcome.Seeded(1, hash)) ->
                Expect.equal hash (PageLayers.digest (bytesOf seedTree)) "the seed hash is the canonical bytes'"
            | other -> failtestf "expected Seeded at version 1, got %A" other

            match rig.Store.Seed(teamScope "t1", admin "t1", page, seedOf seedTree) |> run with
            | Ok(SeedOutcome.AlreadySeeded _) -> ()
            | other -> failtestf "expected AlreadySeeded, got %A" other

            let changed = box "root" [ heading "title" "A new default" ]

            match rig.Store.Seed(teamScope "t1", admin "t1", page, seedOf changed) |> run with
            | Ok(SeedOutcome.Diverged _) -> ()
            | other -> failtestf "a differing seed is never applied silently; got %A" other

            match trail rig platformTrail "PageReseeded" with
            | [ PageReseeded p ] ->
                Expect.equal p.PreviousSeedHash None "the first seed"
                Expect.equal p.ActorId "operator" "attributed"
            | other -> failtestf "expected one PageReseeded row, got %A" other
        }

        test "a team owner's seed is refused and the refusal audited" {
            let rig = rig PageStoreOptions.defaults

            match
                rig.Store.Seed(teamScope "t1", member' "owner" "t1", page, seedOf seedTree)
                |> run
            with
            | Error(PageStoreError.GrantMissing(PageGrant.SavePlatform, PageLayer.Platform)) -> ()
            | other -> failtestf "expected GrantMissing SavePlatform, got %A" other

            match trail rig "t1" "PageWriteRefused" with
            | [ PageWriteRefused p ] ->
                Expect.equal (p.Act, p.Reason, p.MissingGrant) ("seed", "grant-missing", Some "save-platform") "named"
            | other -> failtestf "expected one refusal row, got %A" other
        }

        test "a member's save to the team layer is refused and audited; an owner's lands with a PageSaved row" {
            let rig = seeded PageStoreOptions.defaults

            let ops = [
                TreeOp.ReorderChildren(NodeId "body", [ NodeId "b"; NodeId "a"; NodeId "c" ])
            ]

            match
                rig.Store.AppendOps(teamScope "t1", member' "member" "t1", page, PageLayer.Team, ops, None)
                |> run
            with
            | Error(PageStoreError.GrantMissing(PageGrant.SaveTeam, PageLayer.Team)) -> ()
            | other -> failtestf "expected GrantMissing SaveTeam, got %A" other

            match trail rig "t1" "PageWriteRefused" with
            | [ PageWriteRefused p ] ->
                Expect.equal
                    (p.Layer, p.ActorId, p.Act)
                    ("team", "member", "append")
                    "the refusal names layer and actor"

                Expect.equal p.OpsHash (PageLayers.opsHash ops) "and the ops it carried"
            | other -> failtestf "expected one refusal row, got %A" other

            let receipt =
                rig.Store.AppendOps(teamScope "t1", member' "owner" "t1", page, PageLayer.Team, ops, None)
                |> run
                |> expectOk "owner save"

            match trail rig "t1" "PageSaved" with
            | [ PageSaved p ] ->
                Expect.equal
                    (p.Layer, p.Principal, p.ActorId, p.Act, p.OpCount)
                    ("team", "t1", "owner", "append", 1)
                    "named"

                Expect.equal p.OpsHash (PageLayers.opsHash ops) "the ops hash"
                Expect.equal p.DocumentHash receipt.DocumentHash "the resulting document hash"
                Expect.equal p.ChainHead receipt.ChainHead "the chain head"
            | other -> failtestf "expected one PageSaved row, got %A" other

            let lead =
                rig.Store.AppendOps(
                    teamScope "t1",
                    member' "lead" "t1",
                    page,
                    PageLayer.Team,
                    [ retitle "a" "x" ],
                    None
                )
                |> run

            Expect.isOk lead "a team Admin saves the team layer too"
        }

        test "an explicit SaveTeam grant lets a member save; a SavePlatform grant in a team's own document does not" {
            let rig = seeded PageStoreOptions.defaults

            rig.Permissions.SetTeamPermissions(
                "t1",
                {
                    TeamPermissions.empty with
                        Pages = Map.ofList [ "member", [ PageGrant.SaveTeam; PageGrant.SavePlatform ] ]
                }
            )
            |> run
            |> ignore

            let ops = [ retitle "a" "Granted" ]

            rig.Store.AppendOps(teamScope "t1", member' "member" "t1", page, PageLayer.Team, ops, None)
            |> run
            |> expectOk "granted member"
            |> ignore

            match
                rig.Store.AppendOps(teamScope "t1", member' "member" "t1", page, PageLayer.Platform, ops, None)
                |> run
            with
            | Error(PageStoreError.GrantMissing(PageGrant.SavePlatform, _)) -> ()
            | other -> failtestf "a team's document cannot grant the platform layer; got %A" other
        }

        test "SavePlatform is honoured from the deployment's platform-grant team" {
            let rig =
                seeded {
                    PageStoreOptions.defaults with
                        PlatformGrantTeam = Some "ops"
                }

            rig.Permissions.SetTeamPermissions(
                "ops",
                {
                    TeamPermissions.empty with
                        Pages = Map.ofList [ "member", [ PageGrant.SavePlatform ] ]
                }
            )
            |> run
            |> ignore

            rig.Store.AppendOps(
                teamScope "t1",
                member' "member" "t1",
                page,
                PageLayer.Platform,
                [ retitle "title" "x" ],
                None
            )
            |> run
            |> expectOk "platform-grant team member"
            |> ignore
        }

        test "the individual layer is off by default, refused by name" {
            let rig = seeded PageStoreOptions.defaults

            match
                rig.Store.AppendOps(
                    teamScope "t1",
                    member' "alice" "t1",
                    page,
                    PageLayer.Individual,
                    [ retitle "a" "x" ],
                    None
                )
                |> run
            with
            | Error(PageStoreError.LayerDisabled PageLayer.Individual) -> ()
            | other -> failtestf "expected LayerDisabled, got %A" other

            match trail rig "t1" "PageWriteRefused" with
            | [ PageWriteRefused p ] -> Expect.equal p.Reason "layer-disabled" "audited"
            | other -> failtestf "expected one refusal row, got %A" other
        }

        test "platform, team and individual fold to one document whose content hash is the object store's" {
            let rig =
                seeded {
                    PageStoreOptions.defaults with
                        IndividualLayer = IndividualLayer.EveryViewer
                }

            let save who layer ops =
                rig.Store.AppendOps(teamScope "t1", who, page, layer, ops, None)
                |> run
                |> expectOk "save"
                |> ignore

            save (admin "t1") PageLayer.Platform [ retitle "title" "Daily trade, by site" ]
            save (member' "owner" "t1") PageLayer.Team [ TreeOp.RemoveNode(NodeId "b") ]
            save (member' "alice" "t1") PageLayer.Individual [ retitle "c" "Margin %" ]

            let folded =
                rig.Store.ReadFolded(teamScope "t1", member' "alice" "t1", page)
                |> run
                |> expectOk "fold"

            let again =
                rig.Store.ReadFolded(teamScope "t1", member' "alice" "t1", page)
                |> run
                |> expectOk "fold"

            Expect.isEmpty folded.Conflicts "nothing conflicts"
            Expect.equal folded.ContentHash again.ContentHash "deterministic"

            Expect.equal
                (folded.Layers |> List.map _.Key.Layer)
                [ PageLayer.Platform; PageLayer.Team; PageLayer.Individual ]
                "three layers"

            let stored =
                rig.Objects.Save(
                    "t1",
                    "probe/folded",
                    folded.Document,
                    "probe",
                    "test",
                    Map.empty,
                    VersioningPolicy.Versioned
                )
                |> run

            match stored with
            | Ok meta ->
                Expect.equal folded.ContentHash meta.ContentHash "the object store hashes the folded bytes identically"
            | Error e -> failtestf "probe save failed: %A" e

            let expected =
                box "root" [
                    box "header" [ heading "title" "Daily trade, by site" ]
                    box "body" [ heading "a" "Revenue"; heading "c" "Margin %" ]
                ]

            Expect.equal (Encoding.UTF8.GetString folded.Document) (bytesOf expected) "the folded document"

            // Another member of the team sees the team layer, not alice's.
            let other =
                rig.Store.ReadFolded(teamScope "t1", member' "member" "t1", page)
                |> run
                |> expectOk "fold"

            Expect.equal (other.Layers |> List.length) 2 "platform and team only"
        }

        test "an individual op whose target the team removed later is reported, naming both acts" {
            let rig =
                seeded {
                    PageStoreOptions.defaults with
                        IndividualLayer = IndividualLayer.EveryViewer
                }

            rig.Store.AppendOps(
                teamScope "t1",
                member' "alice" "t1",
                page,
                PageLayer.Individual,
                [ retitle "a" "Mine" ],
                None
            )
            |> run
            |> expectOk "alice"
            |> ignore

            rig.Store.AppendOps(
                teamScope "t1",
                member' "owner" "t1",
                page,
                PageLayer.Team,
                [ TreeOp.RemoveNode(NodeId "a") ],
                None
            )
            |> run
            |> expectOk "owner"
            |> ignore

            let folded =
                rig.Store.ReadFolded(teamScope "t1", member' "alice" "t1", page)
                |> run
                |> expectOk "fold"

            match folded.Conflicts with
            | [ {
                    Act = {
                              LayerAct.Layer = PageLayer.Individual
                              Principal = "alice"
                              Seq = 0
                          }
                    Cause = ConflictCause.Removed(NodeId "a",
                                                  {
                                                      LayerAct.Layer = PageLayer.Team
                                                      Principal = "t1"
                                                      Seq = 0
                                                  })
                } ] -> ()
            | other -> failtestf "expected one conflict naming both acts, got %A" other

            // A save whose ops would not apply is refused, never stored.
            match
                rig.Store.AppendOps(
                    teamScope "t1",
                    member' "alice" "t1",
                    page,
                    PageLayer.Individual,
                    [ retitle "a" "again" ],
                    None
                )
                |> run
            with
            | Error(PageStoreError.OpsDoNotApply [ { Cause = ConflictCause.Removed _ } ]) -> ()
            | other -> failtestf "expected OpsDoNotApply, got %A" other
        }

        test "re-seed re-folds the overlays and reports their conflicts; detach stops a seed being offered" {
            let rig = seeded PageStoreOptions.defaults

            rig.Store.AppendOps(
                teamScope "t1",
                member' "owner" "t1",
                page,
                PageLayer.Team,
                [ retitle "b" "Volume" ],
                None
            )
            |> run
            |> expectOk "team"
            |> ignore

            let release =
                box "root" [
                    box "header" [ heading "title" "Daily trade" ]
                    box "body" [ heading "a" "Revenue" ]
                ]

            match
                rig.Store.Reseed(teamScope "t1", member' "owner" "t1", page, seedOf release)
                |> run
            with
            | Error(PageStoreError.GrantMissing(PageGrant.SavePlatform, _)) -> ()
            | other -> failtestf "re-seed is a platform act; got %A" other

            let receipt =
                rig.Store.Reseed(teamScope "t1", admin "t1", page, seedOf release)
                |> run
                |> expectOk "reseed"

            match receipt.Folded.Conflicts with
            | [ {
                    Act = { LayerAct.Layer = PageLayer.Team }
                    Cause = ConflictCause.NotInDocument(NodeId "b")
                } ] -> ()
            | other -> failtestf "expected the team's op on the dropped node reported, got %A" other

            match trail rig platformTrail "PageReseeded" with
            | latest :: _ ->
                match latest with
                | PageReseeded p ->
                    Expect.equal p.ConflictCount 1 "the conflict counted"
                    Expect.isSome p.PreviousSeedHash "the replaced seed named"
                | other -> failtestf "unexpected %A" other
            | [] -> failtest "no PageReseeded row"

            rig.Store.Detach(teamScope "t1", admin "t1", page)
            |> run
            |> expectOk "detach"
            |> ignore

            match rig.Store.Seed(teamScope "t1", admin "t1", page, seedOf seedTree) |> run with
            | Ok(SeedOutcome.Detached _) -> ()
            | other -> failtestf "a detached page is not offered a seed; got %A" other

            Expect.equal (trail rig platformTrail "PageDetached" |> List.length) 1 "the detach audited"
        }

        test "reset removes an overlay; the layer folds to the one below" {
            let rig = seeded PageStoreOptions.defaults

            rig.Store.AppendOps(
                teamScope "t1",
                member' "owner" "t1",
                page,
                PageLayer.Team,
                [ TreeOp.RemoveNode(NodeId "a") ],
                None
            )
            |> run
            |> expectOk "team"
            |> ignore

            let receipt =
                rig.Store.ResetLayer(teamScope "t1", member' "owner" "t1", page, PageLayer.Team)
                |> run
                |> expectOk "reset"

            let folded =
                rig.Store.ReadFolded(teamScope "t1", member' "owner" "t1", page)
                |> run
                |> expectOk "fold"

            Expect.equal (Encoding.UTF8.GetString folded.Document) (bytesOf seedTree) "back to the seed"
            Expect.equal receipt.DocumentHash folded.ContentHash "the receipt names the document it left"

            match
                rig.Store.ReadLayer(teamScope "t1", member' "owner" "t1", page, PageLayer.Team)
                |> run
            with
            | Ok(Some layer) ->
                Expect.isEmpty layer.Records "the stream is empty"
                Expect.equal layer.Version 2 "history is kept in the earlier version"
            | other -> failtestf "expected the reset layer, got %A" other
        }

        test "an op outside the stored vocabulary and an empty save are refused" {
            let rig = seeded PageStoreOptions.defaults

            match
                rig.Store.AppendOps(
                    teamScope "t1",
                    admin "t1",
                    page,
                    PageLayer.Team,
                    [ TreeOp.ReplaceRoot seedTree ],
                    None
                )
                |> run
            with
            | Error(PageStoreError.OpOutsideVocabulary "ReplaceRoot") -> ()
            | other -> failtestf "expected OpOutsideVocabulary, got %A" other

            match
                rig.Store.AppendOps(teamScope "t1", admin "t1", page, PageLayer.Team, [], None)
                |> run
            with
            | Error PageStoreError.NoOps -> ()
            | other -> failtestf "expected NoOps, got %A" other
        }

        test "the anonymous scope holds no layers, and an unsafe page name is refused" {
            let rig = seeded PageStoreOptions.defaults
            let anon = AccessContext.unrestricted (AnonymousSession "s1")

            match rig.Store.ReadFolded(ResolvedScope.anonymous, anon, page) |> run with
            | Error(PageStoreError.ScopeRefused _) -> ()
            | other -> failtestf "expected ScopeRefused, got %A" other

            match
                rig.Store.ReadFolded(teamScope "t1", admin "t1", { page with Page = "../x" })
                |> run
            with
            | Error(PageStoreError.InvalidPageRef _) -> ()
            | other -> failtestf "expected InvalidPageRef, got %A" other
        }

        test "a stored layer's chain is verified on read: a tampered op is refused by name" {
            let rig = seeded PageStoreOptions.defaults

            rig.Store.AppendOps(
                teamScope "t1",
                member' "owner" "t1",
                page,
                PageLayer.Team,
                [ retitle "a" "Honest" ],
                None
            )
            |> run
            |> expectOk "team"
            |> ignore

            let objectId = "pages/trade/daily/team/t1"

            let meta, bytes =
                rig.Objects.Get("t1", objectId)
                |> run
                |> Result.defaultWith (fun e -> failtestf "%A" e)

            let tampered =
                Encoding.UTF8.GetString(bytes).Replace("Honest", "Forged")
                |> Encoding.UTF8.GetBytes

            rig.Objects.Save(
                "t1",
                objectId,
                tampered,
                meta.DataType,
                "intruder",
                meta.Metadata,
                VersioningPolicy.Versioned
            )
            |> run
            |> ignore

            match rig.Store.ReadFolded(teamScope "t1", member' "owner" "t1", page) |> run with
            | Error(PageStoreError.StoredLayerUnreadable(key, _)) ->
                Expect.equal key.Layer PageLayer.Team "the team layer named"
            | other -> failtestf "expected StoredLayerUnreadable, got %A" other
        }
    ]

// ── The permission document ────────────────────────────────────────────

let private documentTests =
    testList "the permission document's pages" [

        // Through the blob-backed store, so the bytes compared are the bytes
        // at rest.
        let blobName = "permissions/t1.json"

        let roundTrip (stored: string) : TeamPermissions * string =
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let store = PermissionStore(blobs) :> IPermissionStore

            blobs.Upload(ScopeKey.PlatformContainer, blobName, Encoding.UTF8.GetBytes stored)
            |> run
            |> ignore

            let doc = store.GetTeamPermissions "t1" |> run
            store.SetTeamPermissions("t1", doc) |> run |> ignore

            match blobs.Download(ScopeKey.PlatformContainer, blobName) |> run with
            | Ok bytes -> doc, Encoding.UTF8.GetString bytes
            | Error e -> failtestf "download failed: %s" e

        test "a document without pages reads and writes back byte-identically" {
            let legacy =
                """{"defaults":{"m":["Read"]},"exposure":{},"grants":{},"hidden":[],"members":{}}"""

            let doc, written = roundTrip legacy
            Expect.isEmpty doc.Pages "no pages"
            Expect.equal written legacy "same bytes"
        }

        test "pages round-trip, and an unknown token grants nothing" {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let store = PermissionStore(blobs) :> IPermissionStore

            let doc = {
                TeamPermissions.empty with
                    Pages = Map.ofList [ "u1", [ PageGrant.SaveTeam; PageGrant.SaveOwn ] ]
            }

            store.SetTeamPermissions("t1", doc) |> run |> ignore
            Expect.equal (store.GetTeamPermissions "t1" |> run).Pages doc.Pages "round trip"

            let foreign, _ =
                roundTrip
                    """{"defaults":{},"members":{},"pages":{"u1":["save-everything","save-team"],"u2":["nope"]}}"""

            Expect.equal
                foreign.Pages
                (Map.ofList [ "u1", [ PageGrant.SaveTeam ] ])
                "unknown tokens dropped, never read as a grant"
        }
    ]

[<Tests>]
let tests =
    testList "Page store and layer fold" [ foldTests; inverseTests; storeTests; documentTests ]