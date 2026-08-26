module ToolUp.Platform.Tests.InProcess.ServiceAccountStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── BlobServiceAccountStore — contract binding (Phase 527) ──────────
//
// Binds the `IServiceAccountStore` contract pack to the default
// blob-backed impl over `LocalFileStorage` rooted in a fresh temp
// directory per factory call, mirroring `ShareTokenStoreTests`.
//
// The on-disk assertions below are deliberately NOT in the contract
// pack: "the secret is not in the bytes" is only checkable by something
// that can see the bytes, and a distributed implementation's storage is
// not a directory. The pack asserts the property the interface can
// promise (no returned record carries the secret); this binding asserts
// the property THIS implementation can be held to (no persisted byte
// does either).

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private freshRoot () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-serviceaccount-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    root

let tests =
    let factory () =
        let storage = LocalFileStorage.LocalFileStorage(freshRoot ()) :> IBlobStorage
        let store = ServiceAccountStore.create storage None silentLogger
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    IServiceAccountStoreContract.tests "BlobServiceAccountStore" factory

let pureTests = IServiceAccountStoreContract.pureTests

let persistenceTests =
    testList "BlobServiceAccountStore — persistence" [

        testCaseAsync "no persisted byte anywhere under the root contains the minted secret"
        <| async {
            let root = freshRoot ()
            let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
            let store = ServiceAccountStore.create storage None silentLogger
            let scopeId = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

            let! account =
                store.Create {
                    DisplayName = "ci"
                    ScopeId = scopeId
                    Permissions = Map.ofList [ "reports", [ ModulePermission.Read ] ]
                    CreatedBy = "alice"
                }

            let account =
                match account with
                | Ok a -> a
                | Error e -> failtestf "create failed: %A" e

            let! minted =
                store.MintToken {
                    AccountId = account.AccountId
                    ScopeId = scopeId
                    DisplayName = "deploy-key"
                    IssuedBy = "alice"
                    ExpiresAt = None
                }

            let secret =
                match minted with
                | Ok m -> m.Secret
                | Error e -> failtestf "mint failed: %A" e

            // Read every file under the storage root, not just the ones
            // we expect to exist. The point of the test is to catch a
            // secret written somewhere nobody thought about — a log
            // spill, an index blob, a future cache — so enumerating what
            // we already know about would defeat it.
            let offenders =
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun path -> File.ReadAllText(path).Contains secret)
                |> List.ofSeq

            Expect.isEmpty
                offenders
                "the mint response is the ONLY exposure of the secret — nothing on disk may contain it"

            // And prove the probe can fail: the same sweep for a value
            // that IS persisted must find it. Without this the test
            // passes just as happily against a broken enumerator, an
            // empty directory, or a secret that was never minted.
            let accountIdHits =
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun path -> File.ReadAllText(path).Contains account.AccountId)
                |> List.ofSeq

            Expect.isNonEmpty
                accountIdHits
                "control: the account id IS persisted, so a sweep that finds nothing is measuring nothing"
        }

        testCaseAsync "a token surviving a fresh store instance over the same storage still validates"
        <| async {
            let root = freshRoot ()
            let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
            let scopeId = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

            let minting = ServiceAccountStore.create storage None silentLogger

            let! account =
                minting.Create {
                    DisplayName = "ci"
                    ScopeId = scopeId
                    Permissions = Map.ofList [ "reports", [ ModulePermission.Read ] ]
                    CreatedBy = "alice"
                }

            let account =
                match account with
                | Ok a -> a
                | Error e -> failtestf "create failed: %A" e

            let! minted =
                minting.MintToken {
                    AccountId = account.AccountId
                    ScopeId = scopeId
                    DisplayName = "deploy-key"
                    IssuedBy = "alice"
                    ExpiresAt = None
                }

            let secret =
                match minted with
                | Ok m -> m.Secret
                | Error e -> failtestf "mint failed: %A" e

            // A SECOND store instance — the portability rule-4 property:
            // nothing authority-bearing is held between calls, so a
            // different node validates the same token identically.
            let validating = ServiceAccountStore.create storage None silentLogger
            let! result = validating.ValidateToken secret

            match result with
            | Ok principal -> Expect.equal principal.AccountId account.AccountId "a second instance resolves it"
            | Error e -> failtestf "a second store instance could not validate the token: %A" e
        }
    ]