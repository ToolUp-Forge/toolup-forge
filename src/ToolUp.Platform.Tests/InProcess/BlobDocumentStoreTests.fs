// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.BlobDocumentStoreTests

open System
open System.Text
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── BlobDocumentStore<'T> — the three invariants, List, Delete ────────
//
// Absence is established by `Exists` BEFORE the read; undecodable bytes are
// quarantined and never overwritten; the ETag capability is confirmed by a
// probe, not by a type test. `List` reports a verdict per blob and never runs
// more reads at once than its degree.

type private Doc = { Name: string; Count: int }

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

/// Passes everything through to `inner` except: `Download` of a name
/// `failRead` selects answers `Error`; the conditional seam is implemented
/// only when `conditional` is set, and then forwards to `inner`; `Download`
/// counts how many reads are in flight at once.
type private InstrumentedStorage(inner: InMemoryBlobStorage, failRead: string -> bool, conditional: bool) =
    let mutable inFlight = 0
    let mutable peak = 0
    let plain = inner :> IBlobStorage

    member _.Peak = peak

    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Download(container, blobName) = async {
            let now = Interlocked.Increment &inFlight

            lock inner (fun () -> peak <- max peak now)

            try
                do! Async.Sleep 5

                if failRead blobName then
                    return Error "simulated outage"
                else
                    return! plain.Download(container, blobName)
            finally
                Interlocked.Decrement &inFlight |> ignore
        }

        member _.Upload(container, blobName, content) =
            plain.Upload(container, blobName, content)

        member _.Delete(container, blobName) = plain.Delete(container, blobName)
        member _.List(container, prefix) = plain.List(container, prefix)
        member _.Exists(container, blobName) = plain.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = plain.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            plain.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            plain.Erase(container, prefix, policy, dryRun)

/// Claims the conditional seam and answers every conditional call with a
/// failure: the forwarding-decorator shape the probe exists to catch.
type private LyingConditionalStorage(inner: InMemoryBlobStorage) =
    let plain = inner :> IBlobStorage

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(_, _) = async { return Error "no etags here" }

        member _.UploadWithETag(_, _, _, _) = async {
            return Error(ConditionalWriteFailure "inner store is not conditional")
        }

    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Download(container, blobName) = plain.Download(container, blobName)

        member _.Upload(container, blobName, content) =
            plain.Upload(container, blobName, content)

        member _.Delete(container, blobName) = plain.Delete(container, blobName)
        member _.List(container, prefix) = plain.List(container, prefix)
        member _.Exists(container, blobName) = plain.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = plain.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            plain.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            plain.Erase(container, prefix, policy, dryRun)

let private scope = ScopeKey.Team(TeamId "t-docs")
let private container = ScopeKey.container scope
let private path name = DocPath.ofString $"docs/{name}.json"

let private storeOver (storage: IBlobStorage) =
    BlobDocumentStore<Doc>(storage, BlobCodec.json<Doc>, SilentLogger())

let private run work = Async.RunSynchronously work

let private put (storage: IBlobStorage) (name: string) (bytes: byte[]) =
    storage.Upload(container, DocPath.value (path name), bytes) |> run |> ignore

let private bytesOf (storage: IBlobStorage) (name: string) =
    match storage.Download(container, DocPath.value (path name)) |> run with
    | Ok bytes -> bytes
    | Error e -> failtest $"expected {name} to be readable: {e}"

let private increment =
    function
    | Some doc -> BlobUpdate.Write({ doc with Count = doc.Count + 1 }, ())
    | None -> BlobUpdate.Write({ Name = "fresh"; Count = 1 }, ())

let tests =
    testList "Phase 1061 — BlobDocumentStore<'T>" [
        test "an absent document reads as Ok None, and Update creates it" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let docs = storeOver storage

            Expect.equal (docs.Read(scope, path "a") |> run) (Ok None) "absent is Ok None"
            Expect.equal (docs.Update(scope, path "a", increment) |> run) (Ok()) "the update over absent writes"

            Expect.equal
                (docs.Read(scope, path "a") |> run)
                (Ok(Some { Name = "fresh"; Count = 1 }))
                "the created document reads back"
        }

        test "a present document that cannot be read is Unreadable, and Update writes nothing" {
            let backing = InMemoryBlobStorage()
            let storage = backing :> IBlobStorage
            let before = BlobCodec.json<Doc>.Encode { Name = "kept"; Count = 7 }
            put storage "a" before

            let docs =
                storeOver (InstrumentedStorage(backing, (fun name -> name = "docs/a.json"), false))

            match docs.Read(scope, path "a") |> run with
            | Error(BlobMapStoreError.Unreadable _) -> ()
            | other -> failtest $"expected Unreadable, got %A{other}"

            match docs.Update(scope, path "a", increment) |> run with
            | Error(BlobMapStoreError.Unreadable _) -> ()
            | other -> failtest $"expected the update to refuse, got %A{other}"

            Expect.equal (bytesOf storage "a") before "the blob was not overwritten"
        }

        test "undecodable bytes are quarantined beside the blob and never overwritten" {
            let backing = InMemoryBlobStorage()
            let storage = backing :> IBlobStorage
            let corrupt = Encoding.UTF8.GetBytes "{ not json"
            put storage "a" corrupt
            let docs = storeOver storage

            match docs.Update(scope, path "a", increment) |> run with
            | Error(BlobMapStoreError.Quarantined(_, quarantinedTo, _)) ->
                Expect.stringStarts quarantinedTo "docs/a.json.corrupt-" "the copy sits beside the blob"

                Expect.isTrue (storage.Exists(container, quarantinedTo) |> run) "the quarantine copy exists"
            | other -> failtest $"expected Quarantined, got %A{other}"

            Expect.equal (bytesOf storage "a") corrupt "the canonical blob is untouched"
        }

        test "the ETag capability is confirmed by probe, not by the type test" {
            let backing = InMemoryBlobStorage()

            Expect.isTrue
                ((storeOver backing).SupportsConditionalWrites(scope, path "a") |> run)
                "a conditional backend answers the probe"

            Expect.isFalse
                ((storeOver (LyingConditionalStorage backing)).SupportsConditionalWrites(scope, path "a")
                 |> run)
                "a decorator claiming the seam over a non-conditional inner fails the probe"

            let lying = storeOver (LyingConditionalStorage backing)
            Expect.equal (lying.Update(scope, path "b", increment) |> run) (Ok()) "it falls back to a plain write"
        }

        test "List reports a verdict per blob: good rows Ok, a bad blob an Error row, none dropped" {
            let backing = InMemoryBlobStorage()
            let storage = backing :> IBlobStorage
            put storage "good" (BlobCodec.json<Doc>.Encode { Name = "good"; Count = 1 })
            put storage "corrupt" (Encoding.UTF8.GetBytes "nope")
            put storage "down" (BlobCodec.json<Doc>.Encode { Name = "down"; Count = 2 })

            let docs =
                storeOver (InstrumentedStorage(backing, (fun name -> name = "docs/down.json"), false))

            let rows =
                docs.List(scope, "docs/", BlobDocumentStoreLimits.DefaultListDegree)
                |> run
                |> List.map (fun (p, verdict) -> DocPath.value p, verdict)
                |> Map.ofList

            Expect.equal rows.Count 3 "every listed blob has a row"
            Expect.equal rows["docs/good.json"] (Ok { Name = "good"; Count = 1 }) "the good row decodes"

            match rows["docs/corrupt.json"] with
            | Error(BlobMapStoreError.Quarantined _) -> ()
            | other -> failtest $"expected the corrupt row to be Quarantined, got %A{other}"

            match rows["docs/down.json"] with
            | Error(BlobMapStoreError.Unreadable _) -> ()
            | other -> failtest $"expected the unreadable row to be Unreadable, got %A{other}"
        }

        test "List never runs more reads at once than its degree" {
            let backing = InMemoryBlobStorage()
            let storage = backing :> IBlobStorage

            for i in 1..40 do
                put storage $"d{i}" (BlobCodec.json<Doc>.Encode { Name = $"d{i}"; Count = i })

            let instrumented = InstrumentedStorage(backing, (fun _ -> false), false)
            let rows = (storeOver instrumented).List(scope, "docs/", 4) |> run
            Expect.equal rows.Length 40 "every document is listed"
            Expect.isLessThanOrEqual instrumented.Peak 4 "at most four reads ran at once"
            Expect.isGreaterThan instrumented.Peak 1 "the reads did run concurrently"

            Expect.equal
                (BlobDocumentStoreLimits.clamp 1000)
                BlobDocumentStoreLimits.MaxListDegree
                "the degree is capped"

            Expect.equal (BlobDocumentStoreLimits.clamp 0) 1 "the degree is at least one"
        }

        test "Delete removes a document, and deleting an absent one is Ok" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let docs = storeOver storage
            docs.Update(scope, path "a", increment) |> run |> ignore
            Expect.equal (docs.Delete(scope, path "a") |> run) (Ok()) "delete succeeds"
            Expect.equal (docs.Read(scope, path "a") |> run) (Ok None) "the document is gone"
            Expect.equal (docs.Delete(scope, path "a") |> run) (Ok()) "deleting it again is Ok"
        }

        test "a document path is relative, '/'-delimited and never climbs" {
            for bad in [ ""; "/rooted.json"; "a\\b.json"; "a/../b.json" ] do
                Expect.throwsT<ArgumentException> (fun () -> DocPath.ofString bad |> ignore) $"'{bad}' is refused"

            Expect.equal (DocPath.value (DocPath.ofString "a/b.json")) "a/b.json" "a well-formed path renders as given"
        }

        test "UpdateOrRestart sets undecodable bytes aside and starts the document again" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let corrupt = Encoding.UTF8.GetBytes "{ not json"
            put storage "a" corrupt
            let docs = storeOver storage

            Expect.equal (docs.UpdateOrRestart(scope, path "a", increment) |> run) (Ok()) "the restart writes"

            Expect.equal
                (docs.Read(scope, path "a") |> run)
                (Ok(Some { Name = "fresh"; Count = 1 }))
                "the transform ran over None"

            let copies = storage.List(container, "docs/a.json.corrupt-") |> run

            Expect.equal copies.Length 1 "the bytes it set aside are kept"
            Expect.equal (storage.Download(container, copies.Head) |> run) (Ok corrupt) "byte for byte"
        }

        test "UpdateOrRestart over a document it cannot read writes nothing" {
            let backing = InMemoryBlobStorage()
            let storage = backing :> IBlobStorage
            let before = BlobCodec.json<Doc>.Encode { Name = "kept"; Count = 7 }
            put storage "a" before

            let docs =
                storeOver (InstrumentedStorage(backing, (fun name -> name = "docs/a.json"), false))

            match docs.UpdateOrRestart(scope, path "a", increment) |> run with
            | Error(BlobMapStoreError.Unreadable _) -> ()
            | other -> failtest $"expected Unreadable, got %A{other}"

            Expect.equal (bytesOf storage "a") before "a read failure is never a restart"
        }

        test "Replace writes the whole document over a good one, an absent one and an undecodable one" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let docs = storeOver storage
            let value = { Name = "replaced"; Count = 42 }
            put storage "good" (BlobCodec.json<Doc>.Encode { Name = "old"; Count = 1 })
            put storage "bad" (Encoding.UTF8.GetBytes "{ not json")

            for name in [ "good"; "absent"; "bad" ] do
                Expect.equal (docs.Replace(scope, path name, value) |> run) (Ok()) $"{name} is replaced"
                Expect.equal (docs.Read(scope, path name) |> run) (Ok(Some value)) $"{name} reads the new value"

            Expect.equal
                (storage.List(container, "docs/bad.json.corrupt-") |> run).Length
                1
                "the bad bytes were kept aside"
        }
    ]

/// Raises from `Download`; `Exists` answers from `inner`, so the blob may be
/// absent as far as `Exists` knows.
type private RaisingStorage(inner: IBlobStorage) =
    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Download(_, _) = async { return raise (InvalidOperationException "storage read exploded") }

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

let raisingReadTests =
    testList "Phase 1061 — BlobDocumentStore<'T> — a read that raises" [
        test "is Unreadable even where Exists says the blob is absent, and nothing is written" {
            let backing = InMemoryBlobStorage() :> IBlobStorage
            let docs = storeOver (RaisingStorage backing)

            match docs.Update(scope, path "a", increment) |> run with
            | Error(BlobMapStoreError.Unreadable _) -> ()
            | other -> failtest $"expected Unreadable, got %A{other}"

            Expect.isFalse (backing.Exists(container, DocPath.value (path "a")) |> run) "nothing was written"
        }
    ]
// The documents whose codec predates `PlatformJson` keep it: these are the
// bytes at rest, written through the serializer's default encoder, and a
// store moved onto `BlobDocumentStore` must go on producing exactly them.
let byteFixtureTests =
    testList "Phase 1061 — BlobDocumentStore<'T> — byte fixtures of the documents at rest" [
        test "the runtime config document" {
            let codec = PlatformRuntimeConfigStore.codec FeatureMode.Disabled
            let bytes = codec.Encode FeatureMode.Enabled

            Expect.equal
                (Encoding.UTF8.GetString bytes)
                """{"platformKnowledgeBase":"Enabled"}"""
                "the documented format"

            Expect.equal (codec.Decode bytes) (Ok FeatureMode.Enabled) "round trip"

            // Every document written before Phase 1061 is `{}` (a private DTO
            // the serializer could not see); it has always meant the default.
            Expect.equal (codec.Decode(Encoding.UTF8.GetBytes "{}")) (Ok FeatureMode.Disabled) "the bytes at rest"

            Expect.equal
                (codec.Decode(Encoding.UTF8.GetBytes """{"platformKnowledgeBase":"Later"}"""))
                (Ok FeatureMode.Disabled)
                "a mode a later SDK wrote reads as the default"

            Expect.isError (codec.Decode(Encoding.UTF8.GetBytes "not json")) "bytes that are not the document"
            Expect.isError (codec.Decode(Encoding.UTF8.GetBytes "[]")) "a document that is not an object"
        }

        test "the runtime config override survives a restart, and an unreadable one refuses the load" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let first = PlatformRuntimeConfigStore.create storage FeatureMode.Disabled |> run

            Expect.equal (first.SetPlatformKnowledgeBase FeatureMode.Enabled |> run) (Ok()) "the override is set"

            let second = PlatformRuntimeConfigStore.create storage FeatureMode.Disabled |> run
            Expect.equal (second.Snapshot()) FeatureMode.Enabled "and read back by the next process"

            storage.Upload(ScopeKey.PlatformContainer, "runtime-config.json", Encoding.UTF8.GetBytes "not json")
            |> run
            |> ignore

            Expect.throwsT<DocumentUnavailableException>
                (fun () -> PlatformRuntimeConfigStore.create storage FeatureMode.Disabled |> run |> ignore)
                "an undecodable override refuses the load rather than reading as the default"
        }

        test "an event document" {
            let event: ModuleEvent = {
                Id = Guid "0b9c3a5e-6d1f-4e2a-9c8b-7a6f5e4d3c2b"
                OccurredAt = DateTime(2026, 11, 9, 8, 30, 15, DateTimeKind.Utc)
                ScopeId = "team-a+b"
                SourceModule = "billing"
                EventType = "InvoiceIssued"
                Payload = """{"amount":"<10>"}"""
            }

            let bytes = PersistentEventStore.eventCodec.Encode event

            Expect.equal
                (Encoding.UTF8.GetString bytes)
                """{"eventType":"InvoiceIssued","id":"0b9c3a5e-6d1f-4e2a-9c8b-7a6f5e4d3c2b","occurredAt":"2026-11-09T08:30:15.0000000Z","payload":"{\u0022amount\u0022:\u0022\u003C10\u003E\u0022}","scopeId":"team-a\u002Bb","sourceModule":"billing"}"""
                "today's bytes: the anonymous record's fields in name order, the default encoder's escapes"

            Expect.equal (PersistentEventStore.eventCodec.Decode bytes) (Ok event) "round trip"

            Expect.isError
                (PersistentEventStore.eventCodec.Decode(Encoding.UTF8.GetBytes "{}"))
                "a document missing its fields"
        }

        test "the event store reads its events through the document store, and an undecodable one is set aside" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let events =
                PersistentEventStore.PersistentEventStore(storage, EventRetentionPolicy.unlimited) :> IEventStore

            let event = Events.create "team-x" "billing" "InvoiceIssued" "{}"
            EventStore.writeOrRaise events event |> run

            let name = $"events/team-x/garbage-{Guid.NewGuid():N}.json"

            storage.Upload(ScopeKey.PlatformContainer, name, Encoding.UTF8.GetBytes "not json")
            |> run
            |> ignore

            let read = events.ReadAll "team-x" |> run
            Expect.equal (read |> List.map _.Id) [ event.Id ] "the readable event, and not the garbage"

            let listed = storage.List(ScopeKey.PlatformContainer, "events/team-x/") |> run

            Expect.isTrue
                (listed |> List.exists (fun n -> n.StartsWith(name + ".corrupt-")))
                "the garbage was quarantined"

            Expect.isTrue (listed |> List.contains name) "and its canonical blob was not touched"
        }
    ]