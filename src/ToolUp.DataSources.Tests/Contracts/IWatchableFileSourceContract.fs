// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Contracts.IWatchableFileSourceContract

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.DataSources.Common
open ToolUp.DataSources.Tests.Support

// ─── Phase 1015 — the IWatchableFileSource contract pack ─────────────
//
// What every file connector a watched delivery lists through must do. A
// watch ingests each object whose identity it has not recorded, so the
// laws are the ones that keep it from ingesting the wrong thing, missing
// the right thing, or ingesting one thing forever:
//
//   1. It lists exactly the connector's own files directly under the
//      configured prefix — not a sidecar of another extension, not a blob
//      in a nested folder, not a blob outside the prefix — each named as
//      the table the connector reads it as.
//   2. An object's identity is stable while the object is unchanged, so a
//      poll that finds nothing new ingests nothing.
//   3. An object rewritten in place has a new identity, and two objects
//      never share one.
//   4. A location that cannot be read is an `Error`, never a throw and
//      never an empty listing that reads as "nothing landed".
//   5. The listing is ordered by blob name, so two polls compare.
//
// Bound by every connector that implements the seam: the pack takes the
// connector's `create` over a blob store and its file extension.

[<Literal>]
let private Container = "watched"

let private config (scope: (string * string) list) : DataSourceConfig = {
    Id = "watched-source"
    Name = "watched-source"
    Kind = "watched"
    ConnectionScope = Map.ofList ([ "container", Container; "prefix", "incoming" ] @ scope)
    CredentialKey = "unused"
    Tables = None
    Tags = Map.empty
}

let private context (config: DataSourceConfig) : DataSourceCallContext = {
    ScopeId = "watch-scope"
    Config = config
    Credential = None
}

let private watchable (create: IBlobStorage -> IDataSource) (storage: IBlobStorage) : IWatchableFileSource =
    match create storage with
    | :? IWatchableFileSource as source -> source
    | other -> failtestf "%s does not implement IWatchableFileSource" other.Kind

let private list (source: IWatchableFileSource) =
    match source.ListObjects(context (config [])) |> Async.RunSynchronously with
    | Ok objects -> objects
    | Error e -> failtestf "the location could not be listed: %A" e

/// A blob store whose listing fails, as an offline container does.
let private offline (inner: IBlobStorage) =
    { new IBlobStorage with
        member _.Upload(c, n, b) = inner.Upload(c, n, b)
        member _.Download(c, n) = inner.Download(c, n)
        member _.Delete(c, n) = inner.Delete(c, n)
        member _.List(_, _) = async { return raise (IOException "the container is offline") }
        member _.Exists(c, n) = inner.Exists(c, n)
        member _.GetMetadata(c, n) = inner.GetMetadata(c, n)
        member _.DownloadRange(c, n, o, l) = inner.DownloadRange(c, n, o, l)
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            composeNotSupported "offline test storage"

        member _.Erase(c, p, policy, dryRun) = inner.Erase(c, p, policy, dryRun)
    }

/// The five-point pack for a connector whose files carry `extension`
/// (dot-prefixed, as the connector's default).
let tests (name: string) (create: IBlobStorage -> IDataSource) (extension: string) =
    let bytes (n: int) = Array.create n 7uy

    testList $"{name} — IWatchableFileSource contract" [

        test "lists exactly its own files directly under the prefix, as tables" {
            let storage = FakeBlobStorage.InMemoryBlobStorage()
            storage.Put(Container, $"incoming/a{extension}", bytes 3)
            storage.Put(Container, $"incoming/b{extension}", bytes 4)
            storage.Put(Container, "incoming/a.sidecar-json", bytes 5)
            storage.Put(Container, $"incoming/nested/c{extension}", bytes 6)
            storage.Put(Container, $"elsewhere/d{extension}", bytes 7)
            storage.Put("another-container", $"incoming/e{extension}", bytes 8)

            let objects = list (watchable create storage)

            Expect.equal
                (objects |> List.map (fun o -> o.Table, o.BlobName))
                [ "a", $"incoming/a{extension}"; "b", $"incoming/b{extension}" ]
                "only the connector's files directly under the prefix, each as its table"
        }

        test "an unchanged object keeps its identity" {
            let storage = FakeBlobStorage.InMemoryBlobStorage()
            storage.Put(Container, $"incoming/a{extension}", bytes 3)
            let source = watchable create storage

            Expect.equal
                (list source |> List.map _.Identity)
                (list source |> List.map _.Identity)
                "two polls of an unchanged location agree"
        }

        test "a rewritten object has a new identity, and objects never share one" {
            let storage = FakeBlobStorage.InMemoryBlobStorage()
            storage.Put(Container, $"incoming/a{extension}", bytes 3)
            storage.Put(Container, $"incoming/b{extension}", bytes 3)
            let source = watchable create storage
            let before = list source |> List.map _.Identity

            Expect.equal (before |> List.distinct |> List.length) 2 "two objects, two identities"

            storage.Put(Container, $"incoming/a{extension}", bytes 3)
            let after = list source |> List.map _.Identity

            Expect.notEqual after[0] before[0] "a rewrite in place is a new object, even at the same size"
            Expect.equal after[1] before[1] "the untouched object keeps its identity"
        }

        test "an unreadable location is an Error, not a throw or an empty listing" {
            let source = watchable create (offline (FakeBlobStorage.InMemoryBlobStorage()))

            match source.ListObjects(context (config [])) |> Async.RunSynchronously with
            | Error _ -> ()
            | Ok objects -> failtestf "an offline location listed %A" objects
        }

        test "the listing is ordered by blob name" {
            let storage = FakeBlobStorage.InMemoryBlobStorage()

            for table in [ "m"; "b"; "z"; "a" ] do
                storage.Put(Container, $"incoming/{table}{extension}", bytes 1)

            Expect.equal
                (list (watchable create storage) |> List.map _.Table)
                [ "a"; "b"; "m"; "z" ]
                "ordered by blob name"
        }
    ]