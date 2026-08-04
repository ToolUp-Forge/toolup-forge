module ToolUp.Platform.Tests.InProcess.PeerComposeKnobTests

open System
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 629 — compose-level registration for the deferred knobs ───
//
// Three shipped capabilities had a mechanism and no composition root:
// Phase 316's `PeerJobRetentionPolicy`, Phase 483's `IRoundOrchestrator`
// trio, and Phase 338's `PeerTokenPolicy`. Each was deferred because
// `PeerCompose.fs` sat outside its phase's declared key files — which is
// the shape Phase 330's unreferenced `VerifyDelegation` took, and a
// capability with no composition path is a capability nobody turns on.
//
// The cases below assert **both halves** of each knob, because only one
// of them is a claim about the knob:
//
//   * composing it registers / builds the intended thing, AND
//   * NOT composing it leaves the pre-629 value exactly in place.
//
// The second half is the GP 11 claim, and it is also what stops the
// first half passing against a compose path that had started doing the
// new thing unconditionally.

let private localPeer: PeerIdentity = {
    PeerId = "knob-instance"
    DisplayName = "Compose-knob instance"
}

let private enabledConfig = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
        JobScheduler = InProcessJobScheduler
}

let private baseApp () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withLocalPeer localPeer

/// A secret store carrying one strong signing key, so a token minted
/// through a composed provider actually validates.
type private KeyedSecretStore(peerId: string, key: string) =
    interface ISecretStore with
        member _.GetSecret(scopeId, k) = async {
            if scopeId = "_platform" && k = $"peers/{peerId}/signing-key" then
                return Some key
            else
                return None
        }

        member _.SetSecret(_, _, _) = async { return Ok() }
        member _.DeleteSecret(_, _) = async { return Ok() }
        member _.ListKeys _ = async { return [] }

let private signingKey = "phase-629-compose-knob-signing-key-0123456789"

// ─── Phase 316's retention policy ────────────────────────────────────

let jobRetentionTests =
    testList "Phase 629 — withJobRetention reaches the composed result store" [

        testCase "the composed store honours an explicit retention policy"
        <| fun _ ->
            // The lever Phase 316 shipped without: before this phase the
            // only way to change retention was to register a whole
            // `IPeerJobResultStore` of your own, which is a strange price
            // for a `TimeSpan`.
            let policy =
                PeerJobRetentionPolicy.default'
                |> PeerJobRetentionPolicy.withTtl (TimeSpan.FromHours 2.0)
                |> PeerJobRetentionPolicy.withDeleteOnRead (TimeSpan.FromMinutes 1.0)

            let app = baseApp () |> PeerServerApp.withJobRetention policy
            let store = PeerServerApp.jobResultStore app (InMemoryBlobStorage() :> IBlobStorage)

            Expect.equal
                store.Retention
                policy
                "the store the compose path builds reports the composed policy, not its own default"

        testCase "GP 11 CONTROL — a composition that never calls it keeps the pre-629 default"
        <| fun _ ->
            // `BlobPeerJobResultStore(blobs)` already selected
            // `PeerJobRetentionPolicy.default'` for itself, so the knob
            // must carry that exact value or every existing deployment's
            // retention changes on upgrade. Asserted against the store the
            // ONE-argument overload builds — the literal pre-629
            // construction — rather than against the constant, so a drift
            // in either is caught.
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let composed = PeerServerApp.jobResultStore (baseApp ()) blobs
            let pre629 = BlobPeerJobResultStore(blobs) :> IPeerJobResultStore

            Expect.equal
                composed.Retention
                pre629.Retention
                "an un-knobbed composition builds the same retention the pre-629 compose path did"

            Expect.equal composed.Retention PeerJobRetentionPolicy.default' "…which is the documented 30-day default"

        testCase "the knob is last-wins, matching every other with* on the record"
        <| fun _ ->
            let app =
                baseApp ()
                |> PeerServerApp.withJobRetention PeerJobRetentionPolicy.keepForever
                |> PeerServerApp.withJobRetention (
                    PeerJobRetentionPolicy.withTtl (TimeSpan.FromDays 1.0) PeerJobRetentionPolicy.keepForever
                )

            Expect.equal
                app.JobRetention.Ttl
                (Some(TimeSpan.FromDays 1.0))
                "the later call wins, as `withWireLimits` / `withTransportPolicy` do"
    ]

// ─── Phase 483's round orchestrator ──────────────────────────────────

/// The substrate the orchestrator trio resolves out of the peer graph,
/// registered here directly so the test asserts the knob's registrations
/// rather than the whole compose pass.
let private substrateFor (withChannel: bool) =
    let services = ServiceCollection()

    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage() :> IBlobStorage)
    |> ignore

    services.AddSingleton<IPeerFanout>(DefaultPeerFanout() :> IPeerFanout) |> ignore

    if withChannel then
        services.AddSingleton<INotificationChannel>(InMemoryNotificationChannel None :> INotificationChannel)
        |> ignore

    services

let roundOrchestratorTests =
    testList "Phase 629 — withRoundOrchestrator registers the Phase 483 trio" [

        testCase "composing it resolves an orchestrator, a durable state store and an observer"
        <| fun _ ->
            let services = substrateFor true

            PeerServerApp.roundOrchestrationServices (baseApp () |> PeerServerApp.withRoundOrchestrator) services
            |> ignore

            use provider = services.BuildServiceProvider()

            Expect.isNotNull
                (provider.GetService<IRoundOrchestrator>() |> box)
                "IRoundOrchestrator resolves from the composed graph — the whole point of the knob"

            let store = provider.GetService<IRoundStateStore>()

            Expect.isTrue
                (store :? BlobRoundStateStore)
                "the DURABLE store, deliberately: InMemoryRoundStateStore loses its state with the process, which defeats the resume the orchestrator exists for"

            Expect.isNotNull (provider.GetService<IRoundObserver>() |> box) "…and the observer the run reports through"

        testCase "GP 13 CONTROL — a composition that never calls it registers nothing at all"
        <| fun _ ->
            // Without this the case above proves only that the trio can be
            // resolved, not that it was the knob that put it there.
            let services = substrateFor true
            PeerServerApp.roundOrchestrationServices (baseApp ()) services |> ignore
            use provider = services.BuildServiceProvider()

            Expect.isNull
                (provider.GetService<IRoundOrchestrator>() |> box)
                "no orchestrator — a federation running no round protocol pays for none"

            Expect.isNull
                (provider.GetService<IRoundStateStore>() |> box)
                "no state store, so no _platform blob prefix either"

            Expect.isNull (provider.GetService<IRoundObserver>() |> box) "no observer"

        testCase "a deployment's own registration wins — the trio is TryAdd, not Add"
        <| fun _ ->
            let services = substrateFor true
            let mine = InMemoryRoundStateStore() :> IRoundStateStore
            services.AddSingleton<IRoundStateStore>(mine) |> ignore

            PeerServerApp.roundOrchestrationServices (baseApp () |> PeerServerApp.withRoundOrchestrator) services
            |> ignore

            use provider = services.BuildServiceProvider()

            Expect.isTrue
                (obj.ReferenceEquals(provider.GetService<IRoundStateStore>(), mine))
                "the base ServiceConfig runs first, so a deployment that registered its own store keeps it"

        testCase "the observer composes from whatever observability substrate is present"
        <| fun _ ->
            // `PlatformRoundObserver` takes both halves optionally and
            // independently, so a partial host with no notification
            // channel still resolves an observer rather than failing to
            // compose. That is the property under test, and it is the one
            // a `getService`-with-a-cast wiring would break.
            let services = substrateFor false

            PeerServerApp.roundOrchestrationServices (baseApp () |> PeerServerApp.withRoundOrchestrator) services
            |> ignore

            use provider = services.BuildServiceProvider()

            Expect.isNotNull
                (provider.GetService<IRoundObserver>() |> box)
                "an observer resolves with neither an IAuditLog nor an INotificationChannel registered"
    ]

// ─── Phase 338's token policy ────────────────────────────────────────

let tokenPolicyTests =
    testList "Phase 629 — withReplayGuard / withContractBoundCalls reach the composed provider" [

        testCaseAsync "a composed replay guard makes the composed provider refuse a second presentation"
        <| async {
            // Behavioural, not structural: the claim is that the guard
            // reaches the provider the compose path registers, and the
            // only honest way to say that is to replay a token through it.
            let secrets = KeyedSecretStore(localPeer.PeerId, signingKey) :> ISecretStore
            let guard = InMemoryPeerReplayGuard() :> IPeerReplayGuard

            let app = baseApp () |> PeerServerApp.withReplayGuard guard
            let provider = PeerServerApp.peerAuthProvider app secrets

            match! provider.IssuePeerToken(localPeer, localPeer, Anonymous) with
            | Error e -> return failtestf "Expected a minted token, got %A" e
            | Ok token ->
                let! first = provider.ValidatePeerToken token
                Expect.isOk first "the first presentation of a freshly-minted token is accepted"

                let! second = provider.ValidatePeerToken token

                Expect.isError
                    second
                    "the second presentation is refused — the guard the composition named is the one the provider claims against"
        }

        testCaseAsync "GP 11 CONTROL — the SAME token replays freely when no guard is composed"
        <| async {
            // Without this, the case above would pass equally against a
            // provider that had started refusing every second call for any
            // reason at all.
            let secrets = KeyedSecretStore(localPeer.PeerId, signingKey) :> ISecretStore
            let provider = PeerServerApp.peerAuthProvider (baseApp ()) secrets

            match! provider.IssuePeerToken(localPeer, localPeer, Anonymous) with
            | Error e -> return failtestf "Expected a minted token, got %A" e
            | Ok token ->
                let! first = provider.ValidatePeerToken token
                Expect.isOk first "first presentation accepted"

                let! second = provider.ValidatePeerToken token

                Expect.isOk
                    second
                    "…and so is the second: an un-knobbed composition consults no store and examines no jti (GP 11 / GP 13)"
        }

        testCase "withContractBoundCalls composes the binding, and the default composes none"
        <| fun _ ->
            Expect.equal
                (baseApp ()).TokenPolicy.CallScope
                UnscopedCalls
                "the default is the pre-338 posture, byte-for-byte"

            Expect.equal
                (baseApp () |> PeerServerApp.withContractBoundCalls).TokenPolicy.CallScope
                ContractBoundCalls
                "…and the knob is what turns the binding on"

        testCase "the two axes are additive in either order"
        <| fun _ ->
            // They set different fields of one record, so a composition
            // that wants both must get both whichever way round it says
            // them. `withTokenPolicy` is the whole-record form and
            // deliberately replaces — that caveat is documented on it, the
            // same way `withInsecurePeerTransport` documents its own.
            let guard = InMemoryPeerReplayGuard() :> IPeerReplayGuard

            let a =
                baseApp ()
                |> PeerServerApp.withReplayGuard guard
                |> PeerServerApp.withContractBoundCalls

            let b =
                baseApp ()
                |> PeerServerApp.withContractBoundCalls
                |> PeerServerApp.withReplayGuard guard

            Expect.equal a.TokenPolicy b.TokenPolicy "order does not change the composed policy"
            Expect.isSome a.TokenPolicy.ReplayGuard "the guard survived the binding call"
            Expect.equal a.TokenPolicy.CallScope ContractBoundCalls "and the binding survived the guard call"

        testCase "the composed provider still binds the audience to this deployment's own id"
        <| fun _ ->
            // The knob threads a third constructor argument through a call
            // site that already carried two. Pinning the audience here is
            // what catches a future edit that drops it.
            let secrets = KeyedSecretStore(localPeer.PeerId, signingKey) :> ISecretStore

            Expect.equal
                (PeerServerApp.auditAudienceBinding (baseApp ()))
                (AudienceBindingEnforced localPeer.PeerId)
                "the composition's posture is unchanged by the token-policy field"

            Expect.isTrue
                (PeerServerApp.peerAuthProvider (baseApp ()) secrets :? IPeerCallScopedAuth)
                "and the composed provider still offers the call-scoped seam the host validates through"
    ]