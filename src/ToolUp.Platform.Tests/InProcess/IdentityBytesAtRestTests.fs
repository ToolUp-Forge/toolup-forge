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
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Config.NotificationChannel
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Remoting.Json.SystemTextJson

let private run a = Async.RunSynchronously a

/// A timestamp masked out, so the pin is over everything the id decides.
let private masked (text: string) =
    Regex.Replace(text, "\\d{4}-\\d{2}-\\d{2}T[0-9:.]+Z?", "<ts>")

let private blobText (storage: IBlobStorage) (name: string) =
    match storage.Download(ScopeKey.PlatformContainer, name) |> run with
    | Ok bytes -> Encoding.UTF8.GetString bytes
    | Error e -> failtestf "blob %s did not read: %s" name e

/// A persisted record carrying typed ids, and its string-typed twin: the
/// converter set must write both to the same bytes.
type private Typed = {
    Owner: UserId
    Team: TeamId
    Delegate: UserId option
    Members: UserId list
}

type private Untyped = {
    Owner: string
    Team: string
    Delegate: string option
    Members: string list
}

let private json = FableConverters.create ()

let tests =
    testList "typed identity — the bytes at rest do not move" [
        testCase "a typed id field is written as the bare string it wraps"
        <| fun _ ->
            let typed: Typed = {
                Owner = UserId "alice"
                Team = TeamId "acme"
                Delegate = Some(UserId "bob")
                Members = [ UserId "alice"; UserId "carol" ]
            }

            let untyped: Untyped = {
                Owner = "alice"
                Team = "acme"
                Delegate = Some "bob"
                Members = [ "alice"; "carol" ]
            }

            let bytes = JsonSerializer.Serialize(typed, json)
            Expect.equal bytes (JsonSerializer.Serialize(untyped, json)) "byte-identical to the string twin"
            Expect.equal (JsonSerializer.Deserialize<Typed>(bytes, json)) typed "and it reads back"

            Expect.equal
                (JsonSerializer.Deserialize<Typed>(JsonSerializer.Serialize(untyped, json), json))
                typed
                "a document written before the retype reads as the typed record"

        testCase "the smart constructors refuse empty and padded ids"
        <| fun _ ->
            Expect.equal (UserId.tryCreate "alice") (Ok(UserId "alice")) "well formed"
            Expect.equal (TeamId.tryParse "acme") (Some(TeamId "acme")) "well formed"

            for bad in [ null; ""; " alice"; "alice "; "	" ] do
                Expect.isError (UserId.tryCreate bad) (sprintf "user id %A refused" bad)
                Expect.isError (TeamId.tryCreate bad) (sprintf "team id %A refused" bad)
                Expect.isNone (UserId.tryParse bad) "tryParse agrees"

        testCase "a typed id renders as its string on .NET"
        <| fun _ ->
            let user = UserId "alice"
            Expect.equal $"memberships/{user}.json" "memberships/alice.json" "interpolation"
            Expect.equal (string (TeamId "acme")) "acme" "string"
            Expect.equal user.Value "alice" "Value"

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