module ToolUp.Platform.Tests.InProcess.PeerBearerAuthTests

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.PeerBearerAuthMiddleware
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

// ─── PeerBearerAuthMiddleware contract binding ───────────────────────
//
// Binds `IPeerBearerAuthContract` against an in-memory `ISecretStore`
// so the pack exercises the real validator (`authenticate`) without
// pulling in a blob substrate. The peer-bearer middleware has no
// substrate state of its own — it's a pure function over the
// `ISecretStore` lookup — so the in-memory store is the canonical
// binding. Cloud / Vault bindings would compose the same pack with
// their own seeded backend.

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
        let store = InMemorySecretStore() :> ISecretStore

        let seed (peerName, token) =
            // Mirrors the production seeding shape: peer bearers live
            // under the reserved `_platform` scope at
            // `peers/{peerName}/bearer`.
            store.SetSecret(SecretStoreScope, secretKeyFor peerName, token)
            |> Async.RunSynchronously
            |> ignore

        {
            IPeerBearerAuthContract.SecretStore = store
            IPeerBearerAuthContract.SeedBearer = seed
        }

    IPeerBearerAuthContract.tests "PeerBearerAuthMiddleware (in-memory secret store)" factory