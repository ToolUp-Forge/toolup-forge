module ToolUp.Platform.Tests.InProcess.ProviderProfileApiTests

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

// ─── Phase 44 — IProviderProfileApi + the reusable BYOK component ────
//
// Four things are pinned here, and each one is a claim the phase makes
// that nothing else in the tree would notice going false:
//
//   1. **The component carries no `ToolUp.AI` dependency.** Asserted of
//      the BUILT `ToolUp.Platform.Client` assembly, which is where
//      `ProviderProfileUI` lives — an fsproj-level check could be
//      satisfied while a transitive reference crept back in, and a
//      sample can drift. The whole Phase 42.B decoupling is this line.
//   2. **GP 13 — no store, no route.** `ServerApp` mounts the handler on
//      the same gate that registers the store, so an app that never
//      composes a provider profile mounts nothing at all.
//   3. **The handler round-trips the store's own semantics** — routing
//      resolves the way `ProviderProfile.resolveEntry` does (a context
//      rule beats the surface default), a rule or fallback position
//      naming nothing is REFUSED rather than silently inert, and an
//      edit that pastes no key leaves the stored credential alone.
//   4. **The component's save gate is honest about what it knows.** With
//      no verify delegate nothing is ever blocked (the no-AI consumer's
//      case); with one, a pasted key must pass first.

// ─── Fixtures ────────────────────────────────────────────────────────

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

/// Handler over a fresh blob-backed store and a fresh secret store, in
/// the user scope of an authenticated caller (no team gate in play —
/// the team arm is `AISettingsHandler`'s, verbatim, and is covered by
/// that handler's own pack).
let private build () =
    let services = ServiceCollection()
    let secrets = InMemorySecretStore() :> ISecretStore
    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let store = BlobProviderProfile.create storage

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser "user-1"))
    |> ignore

    services.AddSingleton<ISecretStore>(secrets) |> ignore

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp

    ProviderProfileApiHandler.providerProfileApi store ctx, store, secrets

let private scopeOf () =
    AccessContext.configScope (AccessContext.unrestricted (AuthenticatedUser "user-1"))
    |> Option.get

let private input (label: string) (providerId: string) (key: string option) : ProviderEntryInput = {
    Label = label
    ProviderId = providerId
    Model = None
    Tags = []
    ApiKey = key
}

let private expectOk (label: string) (result: Result<'a, string>) : 'a =
    match result with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got Error %s" label e

/// A do-nothing transport for the component tests. The component's own
/// default is a browser proxy, so an in-process test supplies its own —
/// which `ProviderProfileConfig.forApi` exists for.
let private stubApi: IProviderProfileApi = {
    GetProfile = fun () -> async { return Ok ProviderProfileView.empty }
    SaveEntry = fun _ -> async { return Ok() }
    RemoveEntry = fun _ -> async { return Ok() }
    SetRoute = fun _ -> async { return Ok() }
    ClearRoute = fun _ -> async { return Ok() }
    SetFallback = fun _ -> async { return Ok() }
    GetHealth = fun () -> async { return Ok [] }
    RecordVerification = fun _ -> async { return Ok() }
}

// ─── 1. The zero-AI-dependency proof ─────────────────────────────────

let private layeringTests =
    testList "layering" [
        testCase "GP 1 / Phase 42.B — the client tier holding ProviderProfileUI references no ToolUp.AI assembly"
        <| fun () ->
            // `ProviderProfileUI` lives in ToolUp.Platform.Client, so the
            // question "does the reusable BYOK component depend on the AI
            // assistant?" is exactly "does that assembly reference
            // ToolUp.AI*?". Asserted of the built assembly rather than of
            // an fsproj: a transitive reference is a dependency too, and
            // only the emitted reference set sees it.
            let references =
                typeof<ProviderProfileUI.Model>.Assembly.GetReferencedAssemblies()
                |> Array.map _.Name
                |> Array.filter (fun n -> not (isNull n))

            let aiReferences = references |> Array.filter (fun n -> n.StartsWith "ToolUp.AI")

            Expect.isEmpty aiReferences (sprintf "no ToolUp.AI* reference, got: %A" aiReferences)

        testCase "the component's config defaults to no verify delegate"
        <| fun () ->
            // The no-AI consumer's case IS the default. A delegate that
            // arrived by default would be a dependency wearing a hat.
            let config =
                ProviderProfileUI.ProviderProfileConfig.forApi stubApi [ "rental.gateway" ]

            Expect.isNone config.Verify "no verifier unless the app supplies one"
            Expect.equal config.Surfaces [ "rental.gateway" ] "surfaces are the app's"
    ]

// ─── 2. GP 13 — the composition gate ─────────────────────────────────

let private compositionTests =
    testList "composition gate" [
        testCase "GP 13 — no registered store mounts no route at all"
        <| fun () ->
            // This is the exact function `compose` calls, so the claim is
            // executed rather than read off a composition root nothing
            // can invoke.
            Expect.isEmpty (ProviderProfileApiHandler.routeHandlers None) "nothing mounted"

        testCase "a registered store mounts exactly one handler"
        <| fun () ->
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let store = BlobProviderProfile.create storage

            Expect.equal
                (List.length (ProviderProfileApiHandler.routeHandlers (Some store)))
                1
                "one handler, on the same gate that registers the store"

        testCase "ServerApp.empty registers no provider profile, and withProviderProfile is the gate"
        <| fun () ->
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            Expect.isNone ServerApp.empty.ProviderProfile "no store by default"
            Expect.isEmpty ServerApp.empty.Extensions.Handlers "and no handlers by default"

            let app =
                ServerApp.empty
                |> ServerApp.withProviderProfile (BlobProviderProfile.create storage)

            Expect.isSome app.ProviderProfile "the builder sets the gate compose reads"

        testCase "composing twice keeps the last store — compose mounts from the gate, once"
        <| fun () ->
            // `withProviderProfile` documents last-store-wins, and the AI
            // path calls it a second time when a deployment overrides the
            // store. Mounting from the FIELD at compose time rather than
            // appending per call is what keeps that from double-mounting
            // the same routes.
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let second = BlobProviderProfile.create storage

            let app =
                ServerApp.empty
                |> ServerApp.withProviderProfile (BlobProviderProfile.create storage)
                |> ServerApp.withProviderProfile second

            Expect.isTrue (obj.ReferenceEquals(app.ProviderProfile |> Option.get, second)) "the last store wins"
            Expect.equal (List.length (ProviderProfileApiHandler.routeHandlers app.ProviderProfile)) 1 "still one"
    ]

// ─── 3. Handler behaviour over the real store ────────────────────────

let private handlerTests =
    testList "handler" [
        testCase "an unconfigured scope reads as the empty profile, not an error"
        <| fun () ->
            let api, _, _ = build ()
            let view = api.GetProfile() |> Async.RunSynchronously |> expectOk "GetProfile"
            Expect.isEmpty view.Entries "no entries"
            Expect.isEmpty view.Routing "no routing"
            Expect.isEmpty view.Fallback.Ordered "no fallback"

        testCase "SaveEntry persists the entry and stores the pasted key"
        <| fun () ->
            let api, store, secrets = build ()

            api.SaveEntry(input "primary" "acme" (Some "sk-live-1"))
            |> Async.RunSynchronously
            |> expectOk "SaveEntry"

            let view = api.GetProfile() |> Async.RunSynchronously |> expectOk "GetProfile"
            Expect.equal (List.length view.Entries) 1 "one entry"
            let entry = view.Entries |> List.head
            Expect.equal entry.Label "primary" "label"
            Expect.isTrue entry.HasCredential "the credential flag reflects the stored key"
            Expect.equal entry.Origin CredentialOrigin.PastedKey "pasted-key origin"

            // The key itself never appears on the wire shape — the view
            // carries a boolean, and the secret is in the secret store.
            let stored =
                store.Get(scopeOf ())
                |> Async.RunSynchronously
                |> Option.get
                |> _.Entries
                |> List.head

            let key =
                secrets.GetSecret((scopeOf ()).Container, stored.SecretKeyName)
                |> Async.RunSynchronously

            Expect.equal key (Some "sk-live-1") "the key is in the secret store"

        testCase "a metadata edit with no pasted key leaves the stored credential alone"
        <| fun () ->
            let api, _, secrets = build ()

            api.SaveEntry(input "primary" "acme" (Some "sk-live-1"))
            |> Async.RunSynchronously
            |> expectOk "first save"

            api.SaveEntry {
                input "primary" "acme" None with
                    Tags = [ "cheap" ]
            }
            |> Async.RunSynchronously
            |> expectOk "metadata edit"

            let key =
                secrets.GetSecret((scopeOf ()).Container, "provider-key-primary")
                |> Async.RunSynchronously

            Expect.equal key (Some "sk-live-1") "the key survived a metadata-only edit"

            let view = api.GetProfile() |> Async.RunSynchronously |> expectOk "GetProfile"
            Expect.equal (view.Entries |> List.head).Tags [ "cheap" ] "the tags were updated"

        testCase "an edit preserves a 43.B OAuth binding and a probe-written health"
        <| fun () ->
            // The pasted-key surface must not sever a connected entry's
            // refresh binding, nor discard what the probe observed. Both
            // are fields this API never sets.
            let api, store, _ = build ()
            let scope = scopeOf ()

            let binding: ProviderOAuthBinding = {
                FlowName = "acme-oauth"
                Correlation = OAuthCorrelationKey.providerEntry "connected"
                ConnectedAt = DateTime.UtcNow
            }

            let connected =
                ProviderEntry.oauthConnected "connected" "acme" None "oauth-key-connected" binding

            store.Set(
                scope,
                {
                    ProviderProfile.empty () with
                        Entries = [ connected ]
                }
            )
            |> Async.RunSynchronously
            |> expectOk "seed"

            store.SetEntryHealth(
                scope,
                "connected",
                {
                    ProviderHealth.unknown with
                        Status = ProviderHealthStatus.Degraded
                }
            )
            |> Async.RunSynchronously
            |> expectOk "seed health"

            api.SaveEntry {
                input "connected" "acme" None with
                    Tags = [ "eu-resident" ]
            }
            |> Async.RunSynchronously
            |> expectOk "metadata edit"

            let entry =
                store.Get scope
                |> Async.RunSynchronously
                |> Option.get
                |> _.Entries
                |> List.head

            Expect.equal entry.Origin CredentialOrigin.OAuthConnected "origin preserved"
            Expect.isSome entry.OAuthBinding "binding preserved"
            Expect.equal entry.Health.Status ProviderHealthStatus.Degraded "health preserved"
            Expect.equal entry.SecretKeyName "oauth-key-connected" "the substrate's key name is kept"
            Expect.equal entry.Tags [ "eu-resident" ] "the edit landed"

        testCase "SetRoute refuses a label no entry carries"
        <| fun () ->
            // A stale EntryLabel resolves to None at read time, which is
            // indistinguishable from "nothing configured for this
            // surface" — so it is refused at the point of entry.
            let api, _, _ = build ()

            api.SaveEntry(input "primary" "acme" (Some "k"))
            |> Async.RunSynchronously
            |> expectOk "save"

            let result =
                api.SetRoute {
                    Surface = "rental.gateway"
                    Context = None
                    EntryLabel = "typo"
                }
                |> Async.RunSynchronously

            match result with
            | Ok() -> failtest "expected a refusal"
            | Error e -> Expect.stringContains e "typo" "the refusal names the bad label"

        testCase "routing round-trips resolveEntry semantics — a context rule beats the default"
        <| fun () ->
            let api, store, _ = build ()

            api.SaveEntry(input "cheap" "acme" (Some "k1"))
            |> Async.RunSynchronously
            |> expectOk "save cheap"

            api.SaveEntry(input "premium" "acme" (Some "k2"))
            |> Async.RunSynchronously
            |> expectOk "save premium"

            api.SetRoute {
                Surface = "rental.gateway"
                Context = None
                EntryLabel = "cheap"
            }
            |> Async.RunSynchronously
            |> expectOk "default route"

            api.SetRoute {
                Surface = "rental.gateway"
                Context = Some "bank-42"
                EntryLabel = "premium"
            }
            |> Async.RunSynchronously
            |> expectOk "context override"

            let scope = scopeOf ()

            let resolved (context: string option) =
                store.ResolveEntry(scope, "rental.gateway", context)
                |> Async.RunSynchronously
                |> Option.map _.Label

            Expect.equal (resolved None) (Some "cheap") "the surface default"
            Expect.equal (resolved (Some "bank-42")) (Some "premium") "the context override wins"
            Expect.equal (resolved (Some "bank-7")) (Some "cheap") "an unmatched context falls back to the default"

            api.ClearRoute("rental.gateway", Some "bank-42")
            |> Async.RunSynchronously
            |> expectOk "clear override"

            Expect.equal (resolved (Some "bank-42")) (Some "cheap") "the override is gone"

        testCase "SetFallback refuses an unknown label and accepts a known one"
        <| fun () ->
            let api, _, _ = build ()

            api.SaveEntry(input "primary" "acme" (Some "k"))
            |> Async.RunSynchronously
            |> expectOk "save"

            match api.SetFallback [ "primary"; "ghost" ] |> Async.RunSynchronously with
            | Ok() -> failtest "expected a refusal"
            | Error e -> Expect.stringContains e "ghost" "the refusal names the bad label"

            api.SetFallback [ "primary" ]
            |> Async.RunSynchronously
            |> expectOk "set fallback"

            let view = api.GetProfile() |> Async.RunSynchronously |> expectOk "GetProfile"
            Expect.equal view.Fallback.Ordered [ "primary" ] "the chain landed"

        testCase "RemoveEntry drops the entry, its credential, its routes and its fallback slot"
        <| fun () ->
            let api, _, secrets = build ()

            api.SaveEntry(input "primary" "acme" (Some "k1"))
            |> Async.RunSynchronously
            |> expectOk "save primary"

            api.SaveEntry(input "spare" "acme" (Some "k2"))
            |> Async.RunSynchronously
            |> expectOk "save spare"

            api.SetRoute {
                Surface = "rental.gateway"
                Context = None
                EntryLabel = "primary"
            }
            |> Async.RunSynchronously
            |> expectOk "route"

            api.SetFallback [ "primary"; "spare" ]
            |> Async.RunSynchronously
            |> expectOk "fallback"

            api.RemoveEntry "primary" |> Async.RunSynchronously |> expectOk "remove"

            let view = api.GetProfile() |> Async.RunSynchronously |> expectOk "GetProfile"
            Expect.equal (view.Entries |> List.map _.Label) [ "spare" ] "entry gone"
            Expect.isEmpty view.Routing "the route that named it is gone"
            Expect.equal view.Fallback.Ordered [ "spare" ] "its fallback slot is gone"

            let key =
                secrets.GetSecret((scopeOf ()).Container, "provider-key-primary")
                |> Async.RunSynchronously

            Expect.isNone key "the credential is gone"

        testCase "RemoveEntry is idempotent"
        <| fun () ->
            let api, _, _ = build ()
            api.RemoveEntry "never-existed" |> Async.RunSynchronously |> expectOk "first"
            api.RemoveEntry "never-existed" |> Async.RunSynchronously |> expectOk "second"

        testCase "RecordVerification maps the delegate's outcome onto advisory health"
        <| fun () ->
            let api, _, _ = build ()

            api.SaveEntry(input "primary" "acme" (Some "k"))
            |> Async.RunSynchronously
            |> expectOk "save"

            api.RecordVerification("primary", ProviderVerificationOutcome.Verified [ "m1"; "m2" ])
            |> Async.RunSynchronously
            |> expectOk "record pass"

            let health () =
                api.GetHealth()
                |> Async.RunSynchronously
                |> expectOk "GetHealth"
                |> List.head
                |> snd

            Expect.equal (health ()).Status ProviderHealthStatus.Healthy "verified reads healthy"
            Expect.isSome (health ()).LastVerifiedAt "the timestamp is minted server-side"
            Expect.equal (health ()).RecentErrorCount 0 "a pass resets the failure count"

            api.RecordVerification("primary", ProviderVerificationOutcome.Failed "401 unauthorized")
            |> Async.RunSynchronously
            |> expectOk "record failure"

            Expect.equal (health ()).Status ProviderHealthStatus.Unhealthy "a failure reads unhealthy"
            Expect.equal (health ()).RecentErrorCount 1 "and counts"

        testCase "RecordVerification for an unknown label is a no-op Ok"
        <| fun () ->
            // Matches `IProviderProfile.SetEntryHealth`'s own contract. A
            // verify delegate racing a removal must not surface as an
            // error the user has to act on.
            let api, _, _ = build ()

            api.RecordVerification("ghost", ProviderVerificationOutcome.Verified [])
            |> Async.RunSynchronously
            |> expectOk "no-op"
    ]

// ─── 4. The component's save gate ────────────────────────────────────

let private componentTests =
    testList "component" [
        testCase "tags parse from the comma field, dropping blanks"
        <| fun () ->
            // A trailing comma must not persist an empty tag: nothing can
            // ever match it and it renders as a phantom chip.
            Expect.equal (ProviderProfileUI.parseTags " fast , cheap ,, ") [ "fast"; "cheap" ] "trimmed, blanks dropped"

            Expect.isEmpty (ProviderProfileUI.parseTags "") "an empty field is no tags"

        testCase "with no verify delegate nothing is ever blocked"
        <| fun () ->
            // The no-AI consumer's case. A component that refused to save
            // because it could not verify would make the delegate a
            // dependency in all but name.
            let config = ProviderProfileUI.ProviderProfileConfig.forApi stubApi []

            let candidate: ProviderProfileUI.ProviderCandidate = {
                Label = "primary"
                ProviderId = "acme"
                Model = None
                Tags = []
                ApiKey = Some "sk-live-1"
            }

            Expect.isFalse
                (ProviderProfileUI.saveBlocked config ProviderProfileUI.VerificationState.NotAttempted candidate)
                "not blocked"

        testCase "with a verify delegate a pasted key must pass before it can be saved"
        <| fun () ->
            let config =
                ProviderProfileUI.ProviderProfileConfig.forApi stubApi []
                |> ProviderProfileUI.ProviderProfileConfig.withVerify (fun _ -> async { return Ok [] })

            let withKey: ProviderProfileUI.ProviderCandidate = {
                Label = "primary"
                ProviderId = "acme"
                Model = None
                Tags = []
                ApiKey = Some "sk-live-1"
            }

            let metadataOnly = { withKey with ApiKey = None }

            Expect.isTrue
                (ProviderProfileUI.saveBlocked config ProviderProfileUI.VerificationState.NotAttempted withKey)
                "unverified key is blocked"

            Expect.isTrue
                (ProviderProfileUI.saveBlocked config (ProviderProfileUI.VerificationState.Refused "401") withKey)
                "a refused key is blocked"

            Expect.isFalse
                (ProviderProfileUI.saveBlocked config (ProviderProfileUI.VerificationState.Passed []) withKey)
                "a verified key is not"

            Expect.isFalse
                (ProviderProfileUI.saveBlocked config ProviderProfileUI.VerificationState.NotAttempted metadataOnly)
                "a metadata-only edit is never blocked — no delegate can see a key it was not given"
    ]

let tests =
    testList "Phase 44 — provider-profile settings component" [
        layeringTests
        compositionTests
        handlerTests
        componentTests
    ]