// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DocumentStoreAdoptersTests

// ─── The long tail of BlobDocumentStore adopters ─────────────────────
//
// Phase 1061 moved each of these stores off its own `IBlobStorage.Download`
// onto `BlobDocumentStore`. Every store here pins the same observable
// behaviour through its own interface: a document whose bytes do not decode
// is a REFUSAL of the read and of the write — raised where the member has
// no error channel, `Error` where it has one — its bytes copied aside and
// the canonical blob left exactly as it was. Before the move each read the
// document as absent or empty and, for the read-modify-write members, wrote
// that back over everything the document held.

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private run work = Async.RunSynchronously work

let private garbage = Encoding.UTF8.GetBytes "{ not json"

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A fresh backend with undecodable bytes at `container`/`name`.
let private planted (container: string) (name: string) =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    storage.Upload(container, name, garbage) |> run |> ignore
    storage

/// The bytes were copied aside and the canonical blob is untouched.
let private expectQuarantined (storage: IBlobStorage) (container: string) (name: string) =
    let listed = storage.List(container, name) |> run
    Expect.isTrue (listed |> List.exists (fun n -> n.StartsWith(name + ".corrupt-"))) $"{name} was quarantined"
    Expect.equal (storage.Download(container, name) |> run) (Ok garbage) $"{name} was left exactly as it was"

let private expectRefusedRead (label: string) (read: unit -> unit) =
    Expect.throwsT<DocumentUnavailableException> read $"{label} refuses rather than reading the document as absent"

let private expectError (label: string) (result: Result<'a, 'e>) =
    Expect.isError result $"{label} refuses rather than writing over the document"

let private platform = ScopeKey.PlatformContainer

let tests =
    testList "Phase 1061 — the long tail of BlobDocumentStore adopters" [

        test "FeatureFlagStore" {
            let name = $"flags/{FlagScope.slug FlagScope.Platform}.json"
            let storage = planted platform name
            let store = ToolUp.Platform.Config.FeatureFlagStore.create storage

            expectRefusedRead "GetFlag" (fun () -> store.GetFlag(FlagScope.Platform, "beta") |> run |> ignore)
            expectError "SetFlag" (store.SetFlag(FlagScope.Platform, "beta", FlagValue.Bool true) |> run)
            expectQuarantined storage platform name
        }

        test "ModuleVisibilityStore" {
            let name = $"module-visibility/{FlagScope.slug FlagScope.Platform}.json"
            let storage = planted platform name
            let store = ToolUp.Platform.Scope.ModuleVisibilityStore.create storage

            expectRefusedRead "GetProfile" (fun () -> store.GetProfile FlagScope.Platform |> run |> ignore)
            expectQuarantined storage platform name
        }

        test "ConfigStore" {
            let scope: StorageScope = {
                ScopeId = "t-config"
                Container = "team-t-config"
                Persist = true
            }

            let name = $"config/{scope.Container}/billing.json"
            let storage = planted platform name
            let store = ToolUp.Platform.Config.ConfigStore.createWithLogger storage silentLogger

            expectRefusedRead "GetRaw" (fun () -> store.GetRaw(scope, "billing") |> run |> ignore)
            expectQuarantined storage platform name
        }
    ]