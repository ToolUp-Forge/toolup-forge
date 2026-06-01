module ToolUp.Platform.Tests.InProcess.BlobPlatformAIKeyStoreTests

open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.AI
open ToolUp.AI.BlobPlatformAIKeyStore
open ToolUp.Platform.Tests.Contracts

// ─── In-memory ISecretStore for parametrising the contract ───────
//
// Mirrors the in-memory stub used by ShareTokenStoreTests /
// InProcessOAuthTokenRefresherTests. Production deployments back
// the platform AI key store with `FileSecretStore` /
// `EncryptedSecretStore` / cloud-vault companions — none of which
// is in scope for an in-process contract run.

type private InMemorySecretStore() =
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
        let secretStore = InMemorySecretStore() :> ISecretStore
        create secretStore

    IPlatformAIKeyStoreContract.tests "BlobPlatformAIKeyStore over in-memory ISecretStore" factory