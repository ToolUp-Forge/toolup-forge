// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.IdentityBytesAtRestTests

// Typed identity changes the type of every user and team id the platform
// passes around, and must change none of the bytes it writes. This list pins
// those bytes: the blob names the team store mints from a user id and a team
// id, and the documents it writes under them. It was committed against the
// string-typed store before the first wrapper landed, and it passes unchanged
// (the expected strings are literals) once the store takes `UserId` / `TeamId`.

open System.Text
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Config.NotificationChannel
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private run a = Async.RunSynchronously a

/// A timestamp masked out, so the pin is over everything the id decides.
let private masked (text: string) =
    Regex.Replace(text, "\\d{4}-\\d{2}-\\d{2}T[0-9:.]+Z?", "<ts>")

let private blobText (storage: IBlobStorage) (name: string) =
    match storage.Download(ScopeKey.PlatformContainer, name) |> run with
    | Ok bytes -> Encoding.UTF8.GetString bytes
    | Error e -> failtestf "blob %s did not read: %s" name e

let tests =
    testList "typed identity — the bytes at rest do not move" [
        testCase "the team store's blob names and documents"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let store = TeamStore(storage, InMemoryNotificationChannel(None)) :> ITeamStore

            store.CreateTeam("acme", "Acme") |> run |> Result.defaultWith failtest |> ignore
            store.AddMember("acme", "alice", Owner) |> run |> Result.defaultWith failtest

            store.SetActiveTeam("alice", "acme") |> run |> Result.defaultWith failtest

            let names = storage.List(ScopeKey.PlatformContainer, "") |> run |> List.sort

            Expect.equal
                names
                [ "active-team/alice.txt"; "memberships/alice.json"; "teams/acme.json" ]
                "the blob names are the bare ids"

            Expect.equal
                (blobText storage "teams/acme.json" |> masked)
                """{"archived":false,"createdAt":"<ts>","name":"Acme","teamId":"acme"}"""
                "the team document"

            Expect.equal
                (blobText storage "memberships/alice.json" |> masked)
                """[{"joinedAt":"<ts>","role":"Owner","teamId":"acme"}]"""
                "the membership document"

            Expect.equal (blobText storage "active-team/alice.txt") "acme" "the active-team pointer"

            let members = store.GetTeamMembers("acme") |> run
            Expect.equal (members |> List.map _.UserId) [ "alice" ] "read back as the id"
    ]