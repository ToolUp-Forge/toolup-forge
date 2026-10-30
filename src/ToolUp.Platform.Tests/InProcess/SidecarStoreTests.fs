// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SidecarStoreTests

// The laws of `SidecarStore`: a read by a declared key costs the index and
// one object per match, not the scope; the first read backfills the index
// once and a second backfill writes nothing; a corrupt index is rebuilt
// from a listing, never trusted; a stale entry is never an answer; and a
// store that cannot claim a version atomically is never indexed.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.Contracts.IModelRegistryContract

type Note = {
    Id: string
    Owner: string
    Body: string
}

[<Literal>]
let private NoteType = "test.note"

[<Literal>]
let private OwnerKey = "note.owner"

[<Literal>]
let private Scope = "team-1"

let private spec = SidecarStore.json<Note> NoteType Versioned [ OwnerKey ]

let private fresh () =
    let inner = DataObjectStore(InMemoryBlobStorage()) :> IDataObjectStore
    let counting = CountingDataObjectStore(inner)
    let store = SidecarStore<Note>(counting, spec)
    inner, counting, store

let private save (store: SidecarStore<Note>) (note: Note) = async {
    match! store.Save(Scope, note.Id, SidecarStore.encode note, "tester", Map [ OwnerKey, note.Owner ]) with
    | Ok _ -> ()
    | Error e -> failtestf "save of %s failed: %A" note.Id e
}

let private note id owner = {
    Id = id
    Owner = owner
    Body = "body of " + id
}

/// Fill the scope with `owners` records each owned by its own owner, plus
/// `noise` objects of another type, so a scan pays for all of them.
let private populate (store: SidecarStore<Note>) (inner: IDataObjectStore) (owners: int) (noise: int) = async {
    for i in 1..owners do
        do! save store (note $"n-{i}" $"owner-{i}")

    for i in 1..noise do
        let! _ = inner.Save(Scope, $"other-{i}", [| byte i |], "test.other", "tester", Map.empty, Versioned)
        ()
}

let private ids (notes: Note list) = notes |> List.map _.Id |> List.sort

/// Every `Note` record whose owner is `owner`, by scanning — the answer an
/// index read must agree with.
let private byScan (store: SidecarStore<Note>) (owner: string) = async {
    let! all = store.Where(Scope, (fun m -> Map.tryFind OwnerKey m = Some owner))
    return ids all
}

let private corrupt (inner: IDataObjectStore) (objectId: string) = async {
    let! versions = inner.ListVersions(Scope, objectId)
    let expected = versions |> List.map _.Version |> List.fold max 0

    match!
        ConditionalDataObjectStore.saveIfVersion
            inner
            Scope
            objectId
            (System.Text.Encoding.UTF8.GetBytes "{not json")
            SidecarStore.IndexDataType
            "tester"
            Map.empty
            Versioned
            expected
    with
    | Ok _ -> ()
    | Error e -> failtestf "could not corrupt %s: %A" objectId e
}

let tests =
    testList "SidecarStore — indexed sidecar reads" [

        testCaseAsync
            "QueryBy after the backfill is the index plus one object read per match — no listing, no content scan"
        <| async {
            let inner, counting, store = fresh ()
            do! populate store inner 12 20
            do! save store (note "n-extra" "owner-3")
            let! _ = store.QueryBy(Scope, OwnerKey, "owner-1")
            counting.Reset()

            let! found = store.QueryBy(Scope, OwnerKey, "owner-3")

            Expect.equal (ids found) [ "n-3"; "n-extra" ] "both of owner-3's notes, and nothing else"
            Expect.equal counting.Listings 0 "an indexed read lists nothing"
            Expect.equal counting.ContentReads 0 "an indexed read scans no content"
            Expect.equal counting.ObjectReads 4 "the manifest, the bucket, then one read per match"
        }

        testCaseAsync "the first read by a key backfills it with two listings and is answered correctly"
        <| async {
            let inner, counting, store = fresh ()
            do! populate store inner 5 3
            counting.Reset()

            let! found = store.QueryBy(Scope, OwnerKey, "owner-2")

            Expect.equal (ids found) [ "n-2" ] "the backfilling read answers from its listing"
            Expect.equal counting.Listings 2 "list, fill, name the key, list again"
        }

        testCaseAsync "the backfill is idempotent — a second one writes no index version"
        <| async {
            let inner, _, store = fresh ()
            do! populate store inner 4 0
            let! _ = store.QueryBy(Scope, OwnerKey, "owner-1")
            let bucket = SidecarStore.bucketObjectId NoteType OwnerKey "owner-2"
            let manifest = SidecarStore.manifestObjectId NoteType
            let! bucketBefore = inner.ListVersions(Scope, bucket)
            let! manifestBefore = inner.ListVersions(Scope, manifest)

            // A second backfill of the same key: drop the key from the
            // manifest by corrupting it, which makes the next read refill.
            // Every bucket already holds its ids, so no bucket is rewritten.
            do! corrupt inner manifest
            let! again = store.QueryBy(Scope, OwnerKey, "owner-2")
            let! bucketAfter = inner.ListVersions(Scope, bucket)
            let! manifestAfter = inner.ListVersions(Scope, manifest)

            Expect.equal (ids again) [ "n-2" ] "the refilled read answers correctly"
            Expect.equal bucketAfter.Length bucketBefore.Length "an unchanged bucket is not rewritten"
            Expect.equal manifestAfter.Length (manifestBefore.Length + 2) "the corruption, then the key named once"
        }

        testCaseAsync "a corrupt bucket is rebuilt from a listing, never trusted"
        <| async {
            let inner, counting, store = fresh ()
            do! populate store inner 6 0
            let! _ = store.QueryBy(Scope, OwnerKey, "owner-4")
            do! corrupt inner (SidecarStore.bucketObjectId NoteType OwnerKey "owner-4")
            counting.Reset()

            let! rebuilt = store.QueryBy(Scope, OwnerKey, "owner-4")
            Expect.equal (ids rebuilt) [ "n-4" ] "the corrupt bucket's read is answered from a listing"
            Expect.isGreaterThan counting.Listings 0 "the rebuild listed the scope"

            counting.Reset()
            let! after = store.QueryBy(Scope, OwnerKey, "owner-4")
            Expect.equal (ids after) [ "n-4" ] "the rebuilt bucket answers"
            Expect.equal counting.Listings 0 "and is trusted again once it is whole"
        }

        testCaseAsync "a corrupt manifest names no key, so every key is backfilled again"
        <| async {
            let inner, _, store = fresh ()
            do! populate store inner 3 0
            let! _ = store.QueryBy(Scope, OwnerKey, "owner-1")
            do! corrupt inner (SidecarStore.manifestObjectId NoteType)
            do! save store (note "n-late" "owner-1")

            let! found = store.QueryBy(Scope, OwnerKey, "owner-1")
            let! scanned = byScan store "owner-1"
            Expect.equal (ids found) scanned "a save made while the manifest was unreadable is still found"
        }

        testCaseAsync "a save after the backfill files itself — the index never misses a record the scan finds"
        <| async {
            let inner, _, store = fresh ()
            do! populate store inner 3 0
            let! _ = store.QueryBy(Scope, OwnerKey, "owner-1")

            for i in 1..5 do
                do! save store (note $"later-{i}" "owner-1")

            let! found = store.QueryBy(Scope, OwnerKey, "owner-1")
            let! scanned = byScan store "owner-1"
            Expect.equal (ids found) scanned "the index and the scan agree"
            Expect.equal found.Length 6 "the original note and five later ones"
        }

        testCaseAsync "a record whose coordinate moved is not an answer under its old value"
        <| async {
            let inner, _, store = fresh ()
            do! populate store inner 2 0
            let! _ = store.QueryBy(Scope, OwnerKey, "owner-1")
            do! save store (note "n-1" "owner-2")

            let! underOld = store.QueryBy(Scope, OwnerKey, "owner-1")
            let! underNew = store.QueryBy(Scope, OwnerKey, "owner-2")
            Expect.isEmpty underOld "the head no longer carries owner-1"
            Expect.equal (ids underNew) [ "n-1"; "n-2" ] "it is filed under its new value"
        }

        testCaseAsync "an undeclared key, or a store that cannot claim a version, reads by scanning"
        <| async {
            let inner = DataObjectStore(InMemoryBlobStorage()) :> IDataObjectStore
            let plain = CountingDataObjectStore(inner)

            let unclaimed =
                { new IDataObjectStore with
                    member _.Save(a, b, c, d, e, f, g) =
                        (plain :> IDataObjectStore).Save(a, b, c, d, e, f, g)

                    member _.Get(a, b) = (plain :> IDataObjectStore).Get(a, b)

                    member _.GetVersion(a, b, c) =
                        (plain :> IDataObjectStore).GetVersion(a, b, c)

                    member _.GetContent(a, b) =
                        (plain :> IDataObjectStore).GetContent(a, b)

                    member _.ListVersions(a, b) =
                        (plain :> IDataObjectStore).ListVersions(a, b)

                    member _.ListObjects a =
                        (plain :> IDataObjectStore).ListObjects a

                    member _.Recover(a, b, c, d) =
                        (plain :> IDataObjectStore).Recover(a, b, c, d)

                    member _.Delete(a, b) =
                        (plain :> IDataObjectStore).Delete(a, b)

                    member _.Evict(a, b) = (plain :> IDataObjectStore).Evict(a, b)
                    member _.Purge a = (plain :> IDataObjectStore).Purge a

                    member _.Erase(a, b, c, d) =
                        (plain :> IDataObjectStore).Erase(a, b, c, d)
                }

            let store = SidecarStore<Note>(unclaimed, spec)
            do! save store (note "n-1" "owner-1")
            plain.Reset()

            let! found = store.QueryBy(Scope, OwnerKey, "owner-1")
            Expect.equal (ids found) [ "n-1" ] "the scan answers"
            Expect.equal plain.Listings 1 "one listing per read, as before there was an index"

            let! objects = inner.ListObjects Scope

            Expect.isFalse
                (objects |> List.exists (fun d -> d.DataType = SidecarStore.IndexDataType))
                "no index object was written over a store without an atomic claim"
        }

        testCaseAsync "Sweep reads every record of the type and nothing else"
        <| async {
            let inner, _, store = fresh ()
            do! populate store inner 4 3
            let! _ = store.QueryBy(Scope, OwnerKey, "owner-1")
            let! all = store.Sweep Scope
            Expect.equal (ids all) [ "n-1"; "n-2"; "n-3"; "n-4" ] "the notes, without the index or the other type"
        }
    ]