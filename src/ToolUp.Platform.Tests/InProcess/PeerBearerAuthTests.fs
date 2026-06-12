module ToolUp.Platform.Tests.InProcess.PeerBearerAuthTests

open System
open System.Collections.Concurrent
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
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

let private contractTests =
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

// ─── Phase 137 — middleware robustness ──────────────────────────────

let private hardeningTests =
    testList "Phase 137 — peer-bearer middleware robustness" [
        testCaseAsync "rejects an X-Peer-Name that would traverse the secret key path"
        <| async {
            // `../evil` would build the secret key `peers/../evil/bearer`;
            // the charset guard must reject it before the lookup.
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Request.Headers["X-Peer-Name"] <- StringValues "../evil"
            ctx.Request.Headers["Authorization"] <- StringValues "Bearer whatever"

            let store = InMemorySecretStore() :> ISecretStore
            let! outcome = authenticate store ctx

            match outcome with
            | Rejected(Some "../evil", reason) ->
                Expect.equal reason RejectionReason.InvalidPeerName "invalid_peer_name reason"
            | other -> failtestf "expected InvalidPeerName rejection, got %A" other
        }

        testCaseAsync "no ISecretStore registered → 401 (not 500), next not invoked"
        <| async {
            // The composition defect must fail closed as an audited 401,
            // not an NRE→500 that never emits PeerCallRejected.
            let services = ServiceCollection()
            let sp = services.BuildServiceProvider()

            let ctx = DefaultHttpContext() :> HttpContext
            ctx.RequestServices <- sp
            ctx.Request.Path <- PathString "/peer/v1/ping"
            ctx.Response.Body <- new System.IO.MemoryStream()

            let mutable nextCalled = false

            let next =
                RequestDelegate(fun _ ->
                    nextCalled <- true
                    System.Threading.Tasks.Task.CompletedTask)

            let config = {
                ServerConfig.defaults with
                    PeerRoutePrefixes = [ "/peer/" ]
            }

            let mw = PeerBearerAuthMiddleware(next, config)
            do! mw.InvokeAsync(ctx) |> Async.AwaitTask

            Expect.isFalse nextCalled "next must not run when peer auth fails closed"
            Expect.equal ctx.Response.StatusCode 401 "fail closed as 401, not 500"
        }
    ]

let tests = testList "PeerBearerAuth" [ contractTests; hardeningTests ]