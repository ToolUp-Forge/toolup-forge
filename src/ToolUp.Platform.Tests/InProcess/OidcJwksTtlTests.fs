module ToolUp.Platform.Tests.InProcess.OidcJwksTtlTests

// Phase 463 — OIDC JWKS cache: configurable TTL + surfaced revocation window.
//
// Three properties, each of which is the whole point of one of the phase's
// levers, and each of which would have been unassertable before it:
//
//   * a SHORTENED TTL expires a cached key set sooner than the shipped
//     10-minute default — the ordinary revocation window is now the
//     operator's number, not the SDK's;
//   * TTL = 0 disables the stale-fallback. This is the assertion that
//     distinguishes "no cache" from "a cache that has always expired": an
//     always-expired cache still serves its stale entry when the refetch
//     fails, which would leave exactly the unbounded window an operator set
//     the TTL to zero to close;
//   * a fetch failure PUBLISHES, and a subscribed sibling EVICTS — so the
//     fleet-wide window is one channel round-trip rather than each silo's
//     TTL measured independently.
//
// The JWKS/discovery caches are process-wide and keyed by URL, so every case
// mints a fresh keypair with GUID-suffixed URLs (`OidcFixture.mkKey`) and can
// therefore run in any order beside every other OIDC pack without bleeding
// cache state. The internal `getJwksCore*` seam is driven directly (via
// `InternalsVisibleTo`) because the alternative — reaching these windows
// through the public provider — means waiting out a real 10-minute TTL.
//
// Test-only. Compiles into the test runner and is byte-for-byte absent from
// any consumer build (GP 11 / GP 13).

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.AuthProviders
open ToolUp.AuthProviders.OidcJwksCacheTypes

let private silentLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// ─── Fixtures (mirrors AuthProviderTests' private OidcFixture; kept local
//     so this pack stays self-contained, the same way ThreatLensRegression-
//     Suite does). ──────────────────────────────────────────────────────────

module private OidcFixture =
    let private b64u (bytes: byte[]) =
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

    type IssuerKey = {
        Rsa: RSA
        Kid: string
        IssuerUrl: string
        JwksUrl: string
    }

    /// Fresh keypair + GUID-suffixed URLs, so each case owns its own
    /// process-wide cache slot.
    let mkKey () : IssuerKey =
        let rsa = RSA.Create 2048
        let unique = Guid.NewGuid().ToString("N").Substring(0, 8)
        let issuer = $"https://jwks-ttl-oidc/{unique}"

        {
            Rsa = rsa
            Kid = $"jwks-ttl-key-{unique}"
            IssuerUrl = issuer
            JwksUrl = $"{issuer}/jwks.json"
        }

    /// JWKS JSON containing one RSA public key (private half never exported).
    let buildJwks (key: IssuerKey) =
        let p = key.Rsa.ExportParameters(false)
        let n = b64u p.Modulus
        let e = b64u p.Exponent
        $"""{{"keys":[{{"kty":"RSA","kid":"{key.Kid}","alg":"RS256","use":"sig","n":"{n}","e":"{e}"}}]}}"""

/// Stub handler routing absolute URLs to canned JSON; anything else 404s
/// (surfaces as `JwksUnavailable`).
type private StubHttpHandler(routes: Map<string, string>) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        match routes.TryFind(string request.RequestUri) with
        | Some body ->
            let response = new HttpResponseMessage(HttpStatusCode.OK)
            response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            Task.FromResult response
        | None ->
            let response = new HttpResponseMessage(HttpStatusCode.NotFound)
            response.Content <- new StringContent("no stub")
            Task.FromResult response

let private servingClient (key: OidcFixture.IssuerKey) =
    new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildJwks key ]))

/// Empty route map → every fetch 404s.
let private failingClient () =
    new HttpClient(new StubHttpHandler(Map.empty))

let private oneMin = TimeSpan.FromMinutes 1.0

// ─── A recording INotificationChannel ────────────────────────────────────
//
// Records every publish, and fans each one out to its subscribers the way
// the SDK's in-process default does — which is what lets a single test drive
// the publish AND the receiving eviction, i.e. the property that actually
// matters (that a fetch failure on one instance clears another's cache),
// rather than merely that a message was constructed.

type private RecordingChannel() =
    let published = ResizeArray<string * Notification>()
    let subscribers = ResizeArray<string * (NotificationEnvelope -> unit)>()
    let gate = obj ()

    member _.Published = lock gate (fun () -> published |> List.ofSeq)

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async {
            let handlers =
                lock gate (fun () ->
                    published.Add((scopeId, notification))

                    subscribers
                    |> Seq.filter (fun (s, _) -> s = scopeId)
                    |> Seq.map snd
                    |> List.ofSeq)

            let envelope = NotificationEnvelope.create scopeId notification

            for handler in handlers do
                handler envelope
        }

        member _.Subscribe(scopeId, handler) = async {
            let id = Guid.NewGuid().ToString("N")
            lock gate (fun () -> subscribers.Add((scopeId, handler)))
            return NotificationSubscriptionId id
        }

        member _.Unsubscribe _ = async { return () }

// ─── A — configurable TTL shortens the ordinary revocation window ────────

let private ttlTests =
    testList "OidcAuthProvider — configurable JWKS cache TTL (Phase 463)" [
        testCaseAsync "a shortened TTL expires the cached key set that the default TTL would still serve"
        <| async {
            let key = OidcFixture.mkKey ()
            use ok = servingClient key
            use failing = failingClient ()

            // Seed the cache from a healthy issuer.
            let! seeded = OidcAuthProviderJwks.getJwksCore ok silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match seeded with
            | Ok keys -> Expect.equal keys.Count 1 "seed fetch returns the one JWKS key"
            | Error e -> failtestf "seed fetch should succeed; got %A" e

            // Under the SHIPPED default the entry is comfortably fresh, so the
            // failing client is never consulted — the cache answers.
            let! underDefault =
                OidcAuthProviderJwks.getJwksCore failing silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match underDefault with
            | Ok keys -> Expect.equal keys.Count 1 "the default 10-minute TTL still serves the entry seeded moments ago"
            | Error e -> failtestf "default TTL should have served from cache without fetching; got %A" e

            // Under a TTL SHORTER than the entry's age, the same entry is
            // expired: the read path re-fetches (and here, fails). This is the
            // whole knob — the window is now the operator's number.
            let! underShortTtl =
                OidcAuthProviderJwks.getJwksCore
                    failing
                    silentLogger
                    key.JwksUrl
                    false
                    true
                    (TimeSpan.FromTicks 1L)
                    oneMin

            match underShortTtl with
            | Ok _ ->
                failtest
                    "a TTL shorter than the entry's age must expire it and force a re-fetch, not keep serving the cached key set"
            | Error _ -> ()

            // And the shortened TTL is genuinely a re-fetch rather than a
            // blanket refusal: pointed at a healthy issuer it succeeds.
            let! shortTtlHealthy =
                OidcAuthProviderJwks.getJwksCore ok silentLogger key.JwksUrl false false (TimeSpan.FromTicks 1L) oneMin

            match shortTtlHealthy with
            | Ok keys -> Expect.equal keys.Count 1 "an expired entry under a short TTL re-fetches successfully"
            | Error e -> failtestf "short TTL against a healthy issuer must re-fetch, not fail; got %A" e
        }

        testCaseAsync "TTL = 0 disables the stale-fallback that a merely-expired cache would still serve"
        <| async {
            let key = OidcFixture.mkKey ()
            use ok = servingClient key
            use failing = failingClient ()

            let! seeded = OidcAuthProviderJwks.getJwksCore ok silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match seeded with
            | Ok keys -> Expect.equal keys.Count 1 "seed fetch returns the one JWKS key"
            | Error e -> failtestf "seed fetch should succeed; got %A" e

            // The contrast that makes this test worth having. A *positive* but
            // tiny TTL is "expired", and the availability-first fallback still
            // hands back the stale key set when the refetch fails — which is
            // correct, and is precisely the unbounded window an operator sets
            // the TTL to zero to close.
            let! expiredButCaching =
                OidcAuthProviderJwks.getJwksCore
                    failing
                    silentLogger
                    key.JwksUrl
                    false
                    false
                    (TimeSpan.FromTicks 1L)
                    oneMin

            match expiredButCaching with
            | Ok keys ->
                Expect.equal keys.Count 1 "an expired-but-enabled cache still serves stale keys on a failed refetch"
            | Error e -> failtestf "the stale fallback should have served here; got %A" e

            // TTL = 0 is NOT "always expired" — it is "no cache". Same failing
            // client, same seeded entry, and nothing is served.
            let! zeroTtl =
                OidcAuthProviderJwks.getJwksCore failing silentLogger key.JwksUrl false false TimeSpan.Zero oneMin

            match zeroTtl with
            | Ok _ -> failtest "TTL = 0 must disable the stale-fallback entirely, not serve the cached key set"
            | Error _ -> ()

            // A zero-TTL reader must also leave the shared process-wide entry
            // alone: another provider instance in the same process, configured
            // with the default TTL, is still entitled to serve it.
            let! stillCachedForOthers =
                OidcAuthProviderJwks.getJwksCore failing silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match stillCachedForOthers with
            | Ok keys ->
                Expect.equal
                    keys.Count
                    1
                    "a zero-TTL reader must not evict or mutate the entry a default-TTL reader owns"
            | Error e -> failtestf "the default-TTL reader should still see the seeded entry; got %A" e
        }

        testCaseAsync "TTL = 0 still fetches successfully — it disables caching, not validation"
        <| async {
            let key = OidcFixture.mkKey ()
            use ok = servingClient key

            let! first = OidcAuthProviderJwks.getJwksCore ok silentLogger key.JwksUrl false false TimeSpan.Zero oneMin

            let! second = OidcAuthProviderJwks.getJwksCore ok silentLogger key.JwksUrl false false TimeSpan.Zero oneMin

            match first, second with
            | Ok a, Ok b ->
                Expect.equal a.Count 1 "zero-TTL fetch resolves the key set"
                Expect.equal b.Count 1 "and does so again on the next call, from the issuer rather than a cache"
            | _ -> failtest "a zero-TTL provider against a healthy issuer must validate normally"
        }

        testCase "a negative TTL is refused at construction rather than aliasing to zero"
        <| fun () ->
            let key = OidcFixture.mkKey ()

            let config: AuthConfig = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = None
                PreferOidWhenPresent = None
                ClaimMapping = None
            }

            let hardening = {
                OidcAuthProvider.OidcHardening.defaults with
                    JwksCacheTtl = Some(TimeSpan.FromMinutes -1.0)
            }

            Expect.throwsT<ArgumentException>
                (fun () -> OidcAuthProvider.fromConfigHardened None hardening config |> ignore)
                "a negative TTL has no coherent reading and must surface at startup"

        testCase "a blank eviction-signal replica id is refused at construction"
        <| fun () ->
            let key = OidcFixture.mkKey ()

            let config: AuthConfig = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = None
                PreferOidWhenPresent = None
                ClaimMapping = None
            }

            let hardening = {
                OidcAuthProvider.OidcHardening.defaults with
                    JwksEvictionSignal =
                        Some {
                            Channel = RecordingChannel() :> INotificationChannel
                            OriginReplicaId = "   "
                        }
            }

            Expect.throwsT<ArgumentException>
                (fun () -> OidcAuthProvider.fromConfigHardened None hardening config |> ignore)
                "every instance sharing the empty identity would discard each other's signals as its own echo"

        testCase "the shipped defaults still describe the documented 10-minute window"
        <| fun () ->
            // The revocation window this provider's docs promise is a NUMBER,
            // and the docs name `defaultJwksTtl` rather than restating it.
            // Pin it so a silent change to the default is a failing test and
            // not a quietly-wrong security note.
            Expect.equal defaultJwksTtl (TimeSpan.FromMinutes 10.0) "documented default JWKS TTL"
            Expect.equal defaultDiscoveryTtl (TimeSpan.FromHours 24.0) "documented default discovery TTL"

            let policy =
                OidcAuthProvider.OidcHardening.toCachePolicy OidcAuthProvider.OidcHardening.defaults

            Expect.equal
                policy
                JwksCachePolicy.defaults
                "OidcHardening.defaults maps to the behaviour-preserving policy"
    ]

// ─── C — the fetch-failure notification, and the eviction it drives ──────

let private evictionSignalTests =
    testList "OidcAuthProvider — cross-instance JWKS eviction signal (Phase 463)" [
        testCaseAsync "a JWKS fetch failure publishes the eviction envelope"
        <| async {
            let key = OidcFixture.mkKey ()
            use failing = failingClient ()
            let channel = RecordingChannel()

            let signal: JwksEvictionSignal = {
                Channel = channel :> INotificationChannel
                OriginReplicaId = "instance-a"
            }

            let! result =
                OidcAuthProviderJwks.getJwksCoreWith
                    (Some signal)
                    failing
                    silentLogger
                    key.JwksUrl
                    false
                    false
                    defaultJwksTtl
                    oneMin

            match result with
            | Ok _ -> failtest "a cold cache against a failing issuer must not resolve keys"
            | Error _ -> ()

            let envelopes =
                channel.Published
                |> List.choose (fun (scopeId, n) ->
                    match n with
                    | CustomNotification(k, payload) when k = JwksFetchFailedNotification.NotificationKey ->
                        Some(scopeId, payload)
                    | _ -> None)

            Expect.equal envelopes.Length 1 "exactly one eviction envelope is published per failed fetch"

            let scopeId, payload = envelopes.Head

            Expect.equal
                scopeId
                NotificationKind.PlatformReservedScope
                "published on the cross-scope reserved bus, not a tenant scope"

            let decoded =
                JsonSerializer.Deserialize<JwksFetchFailedEnvelope>(
                    payload,
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
                )

            Expect.equal decoded.JwksUrl key.JwksUrl "the envelope names the URL siblings must evict"
            Expect.equal decoded.OriginReplicaId "instance-a" "the envelope carries the originating instance"
            Expect.isFalse (String.IsNullOrWhiteSpace decoded.Reason) "the envelope carries a classified reason"
        }

        testCaseAsync "a subscribed sibling evicts its own cached key set on receipt"
        <| async {
            let key = OidcFixture.mkKey ()
            use ok = servingClient key
            use failing = failingClient ()
            let channel = RecordingChannel()

            // Instance B is warm: it holds a cached key set well inside its TTL,
            // and would keep serving it for the rest of that TTL no matter what
            // happened to instance A. That independence is the defect.
            let! seeded = OidcAuthProviderJwks.getJwksCore ok silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match seeded with
            | Ok keys -> Expect.equal keys.Count 1 "instance B is warm before the signal"
            | Error e -> failtestf "seed fetch should succeed; got %A" e

            let! _subscription =
                OidcAuthProvider.OidcJwksCache.subscribeToEvictions (channel :> INotificationChannel) "instance-b" None

            // Instance A runs a TIGHTENED TTL, so the shared entry is already
            // expired for it: it re-fetches, fails, and publishes. (Under the
            // default TTL A would have been served from cache and never
            // fetched at all — no failure, no signal, and a test that passed
            // by never exercising anything.)
            let signalA: JwksEvictionSignal = {
                Channel = channel :> INotificationChannel
                OriginReplicaId = "instance-a"
            }

            let! failedOnA =
                OidcAuthProviderJwks.getJwksCoreWith
                    (Some signalA)
                    failing
                    silentLogger
                    key.JwksUrl
                    false
                    true
                    (TimeSpan.FromTicks 1L)
                    oneMin

            match failedOnA with
            | Ok _ -> failtest "instance A's strict-mode fetch failure must surface as an error"
            | Error _ -> ()

            // B's entry is gone. Proven by the failing client: had the entry
            // survived, B would have served it from cache without a fetch.
            let! afterSignal =
                OidcAuthProviderJwks.getJwksCore failing silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match afterSignal with
            | Ok _ ->
                failtest
                    "the subscriber must have evicted the cached key set; instance B is still serving keys A could not verify"
            | Error _ -> ()
        }

        testCaseAsync "an instance does not evict on its own echo"
        <| async {
            let key = OidcFixture.mkKey ()
            use ok = servingClient key
            use failing = failingClient ()
            let channel = RecordingChannel()

            let! seeded = OidcAuthProviderJwks.getJwksCore ok silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match seeded with
            | Ok keys -> Expect.equal keys.Count 1 "warm before the echo"
            | Error e -> failtestf "seed fetch should succeed; got %A" e

            // Publisher and subscriber are the SAME instance id — the shape the
            // in-process channel always produces, and the one a fleet of one
            // only ever produces.
            let! _subscription =
                OidcAuthProvider.OidcJwksCache.subscribeToEvictions
                    (channel :> INotificationChannel)
                    "instance-solo"
                    None

            let signal: JwksEvictionSignal = {
                Channel = channel :> INotificationChannel
                OriginReplicaId = "instance-solo"
            }

            // Tightened TTL again, for the same reason: this must reach the
            // fetch — and therefore the publish — or the echo under test never
            // occurs and the assertion below holds for no reason.
            let! failedOnSelf =
                OidcAuthProviderJwks.getJwksCoreWith
                    (Some signal)
                    failing
                    silentLogger
                    key.JwksUrl
                    false
                    false
                    (TimeSpan.FromTicks 1L)
                    oneMin

            match failedOnSelf with
            | Ok _ -> ()
            | Error e -> failtestf "default mode should have served the stale cache after publishing; got %A" e

            // The publish must genuinely have happened, or the echo suppression
            // below is asserting nothing.
            Expect.isNonEmpty channel.Published "the failing fetch must have published a signal to echo back"

            // The stale fallback served (default mode), and the entry survives:
            // self-eviction would have thrown away the very cache that is
            // keeping this single instance available during the outage.
            let! afterEcho =
                OidcAuthProviderJwks.getJwksCore failing silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match afterEcho with
            | Ok keys -> Expect.equal keys.Count 1 "an instance must not evict on its own published signal"
            | Error e -> failtestf "self-echo suppression failed — the entry was evicted; got %A" e
        }

        testCaseAsync "an unwired provider publishes nothing (GP 11)"
        <| async {
            let key = OidcFixture.mkKey ()
            use failing = failingClient ()
            let channel = RecordingChannel()

            let! _result =
                OidcAuthProviderJwks.getJwksCoreWith
                    None
                    failing
                    silentLogger
                    key.JwksUrl
                    false
                    false
                    defaultJwksTtl
                    oneMin

            Expect.isEmpty channel.Published "a deployment that declares no eviction signal must publish nothing"
        }

        testCaseAsync "a malformed envelope evicts nothing rather than clearing the cache"
        <| async {
            let key = OidcFixture.mkKey ()
            use ok = servingClient key
            use failing = failingClient ()
            let channel = RecordingChannel()

            let! seeded = OidcAuthProviderJwks.getJwksCore ok silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match seeded with
            | Ok keys -> Expect.equal keys.Count 1 "warm before the malformed message"
            | Error e -> failtestf "seed fetch should succeed; got %A" e

            let! _subscription =
                OidcAuthProvider.OidcJwksCache.subscribeToEvictions (channel :> INotificationChannel) "instance-b" None

            // A payload that names no URL must be ignored, not read as "evict
            // everything" — a broadcast storm of those would otherwise be a
            // denial-of-service lever against every instance's key cache.
            do!
                (channel :> INotificationChannel)
                    .Publish(
                        NotificationKind.PlatformReservedScope,
                        CustomNotification(JwksFetchFailedNotification.NotificationKey, "not json at all")
                    )

            let! afterGarbage =
                OidcAuthProviderJwks.getJwksCore failing silentLogger key.JwksUrl false false defaultJwksTtl oneMin

            match afterGarbage with
            | Ok keys -> Expect.equal keys.Count 1 "a malformed eviction signal must be a no-op"
            | Error e -> failtestf "a malformed signal must not clear the cache; got %A" e
        }
    ]

// ─── Aggregated ─────────────────────────────────────────────────────────

let tests = testList "OidcJwksTtl" [ ttlTests; evictionSignalTests ]