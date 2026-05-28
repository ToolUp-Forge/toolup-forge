module ToolUp.Platform.Tests.InProcess.ShareTokenStoreTests

open System
open System.Collections.Concurrent
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

// ─── BlobShareTokenStore — IShareTokenStore contract binding ─────────
//
// Binds the `IShareTokenStore` contract pack to the Phase 21b default
// blob-backed impl over `LocalFileStorage` rooted in a fresh temp
// directory per factory call. The signing key is auto-generated and
// persisted into an `InMemorySecretStore` defined locally — no
// dependency on `FileSecretStore` to keep the test setup minimal.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Trivial in-memory `ISecretStore` for the share-token signing key.
/// Production deployments use `FileSecretStore` / `EncryptedSecretStore`
/// / cloud-vault companions.
type InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let tests =
    let factory () =
        let root =
            Path.Combine(Path.GetTempPath(), "toolup-sharetoken-tests-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore
        let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
        let secrets = InMemorySecretStore() :> ISecretStore

        let store = ShareTokenStore.create storage secrets None silentLogger

        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    IShareTokenStoreContract.tests "BlobShareTokenStore" factory