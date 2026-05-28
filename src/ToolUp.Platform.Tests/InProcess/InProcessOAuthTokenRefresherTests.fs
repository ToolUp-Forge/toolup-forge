module ToolUp.Platform.Tests.InProcess.InProcessOAuthTokenRefresherTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Metrics
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── InProcessOAuthTokenRefresher — IOAuthTokenRefresher contract + HTTP-path tests ──
//
// Two layers of tests in this file:
//
// 1. **Contract binding** — wires the `IOAuthTokenRefresherContract`
//    pack to a real `InProcessOAuthTokenRefresher.Impl` over an
//    `InProcessJobScheduler` rooted in a fresh temp directory. Tests
//    the portable interface semantics (Register / Unregister / Get /
//    List / RefreshNow on unknown).
//
// 2. **HTTP-path tests** — implementation-specific. Use a fake
//    `HttpMessageHandler` to enqueue token-endpoint responses and
//    verify the refresher's classification: happy-path Refreshed +
//    persisted access token, `invalid_grant` → TokenInvalidatedByProvider,
//    5xx → TransientError, other 4xx → PermanentError.

// ─── Fakes / stubs ───────────────────────────────────────────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }
    }

type InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    member _.Snapshot() =
        store |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq

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

/// Captures emitted audit events so HTTP-path tests can assert the
/// `OAuthTokenRefreshed` / `OAuthRefreshTokenInvalidated` audit family
/// fires at the right moments.
type CapturingAuditLog() =
    let entries = ResizeArray<string * AuditEvent>()

    member _.Snapshot() = entries |> List.ofSeq

    member _.Filter<'T>(picker: AuditEvent -> 'T option) =
        entries |> Seq.choose (fun (_, e) -> picker e) |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { entries.Add(scopeId, audit) }

        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Fake `HttpMessageHandler` driven by a per-test `respond` function
/// — the test enqueues a response shape for the next refresh POST.
type FakeHttpHandler(respond: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(req: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        Task.FromResult(respond req)

// ─── Substrate factory shared by both test layers ────────────────

let private mkSubstrate () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-oauth-refresher-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(root) |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore

    let config = ServerConfig.defaults

    let scheduler =
        JobScheduler.create jobStore eventStore silentChannel config silentLogger (NoOpActivitySink() :> IActivitySink)
        :> IJobScheduler

    // Pre-register the refresh job handler name so RegisterDescriptor's
    // Schedule call passes the "handler is registered" gate.
    scheduler.RegisterHandler(
        InProcessOAuthTokenRefresher.HandlerName,
        { new IJobHandler with
            member _.Execute(_) = async { return JobResult.Success }
        }
    )

    scheduler

// ─── Contract binding ────────────────────────────────────────────

let contractTests =
    let factory () =
        let scheduler = mkSubstrate ()
        let secretStore = InMemorySecretStore() :> ISecretStore
        let auditLog = CapturingAuditLog() :> IAuditLog
        let metricsSink = NoOpMetricsSink() :> IMetricsSink
        let rateLimiter = NoOpRateLimiter() :> IRateLimiter

        // HttpClient is constructed with a fake handler that 500s
        // on every request — the contract tests never invoke
        // RefreshNow against a registered descriptor, so the
        // handler is never actually called.
        let http =
            new HttpClient(
                new FakeHttpHandler(fun _ ->
                    new HttpResponseMessage(HttpStatusCode.InternalServerError, Content = new StringContent ""))
            )

        let refresher =
            InProcessOAuthTokenRefresher.create scheduler secretStore auditLog metricsSink rateLimiter http silentLogger

        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        refresher :> IOAuthTokenRefresher, "team-" + suffix

    IOAuthTokenRefresherContract.tests "InProcessOAuthTokenRefresher" factory

// ─── HTTP-path tests ─────────────────────────────────────────────

let private mkBench (respond: HttpRequestMessage -> HttpResponseMessage) =
    let scheduler = mkSubstrate ()
    let secretStore = InMemorySecretStore()
    let auditLog = CapturingAuditLog()
    let metricsSink = NoOpMetricsSink() :> IMetricsSink
    let rateLimiter = NoOpRateLimiter() :> IRateLimiter
    let http = new HttpClient(new FakeHttpHandler(respond))

    let refresher =
        InProcessOAuthTokenRefresher.create
            scheduler
            (secretStore :> ISecretStore)
            (auditLog :> IAuditLog)
            metricsSink
            rateLimiter
            http
            silentLogger

    let scope = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

    let descriptor =
        OAuthRefreshDescriptor.withDefaults
            "ga4"
            "acct-1"
            scope
            "https://example.test/ga4/token"
            "client-id"
            "ga4-client-secret-acct-1"
            "ga4-refresh-acct-1"

    // Seed the secrets the refresher will read.
    let secretStoreIface = secretStore :> ISecretStore

    secretStoreIface.SetSecret(scope, "ga4-client-secret-acct-1", "supersecret")
    |> Async.RunSynchronously
    |> ignore

    secretStoreIface.SetSecret(scope, "ga4-refresh-acct-1", "rt-original")
    |> Async.RunSynchronously
    |> ignore

    refresher, secretStore, auditLog, descriptor

let private jsonResponse (status: HttpStatusCode) (body: string) : HttpResponseMessage =
    let resp = new HttpResponseMessage(status)
    resp.Content <- new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    resp

let httpPathTests =
    testList "InProcessOAuthTokenRefresher — HTTP-path classification" [

        testCaseAsync "200 + JSON body → Refreshed + access-token persisted + audit emitted"
        <| async {
            let respond _ =
                jsonResponse
                    HttpStatusCode.OK
                    """{"access_token":"new-access","expires_in":3600,"refresh_token":"rt-rotated"}"""

            let refresher, secretStore, auditLog, descriptor = mkBench respond
            do! (refresher :> IOAuthTokenRefresher).RegisterDescriptor descriptor

            let! result = (refresher :> IOAuthTokenRefresher).RefreshNow(descriptor.Provider, descriptor.ConfigId)

            match result with
            | Refreshed newExpiry ->
                Expect.isGreaterThan newExpiry (DateTimeOffset.UtcNow.AddMinutes 30.0) "expiry ~1h out"
            | other -> failtestf "Expected Refreshed, got %A" other

            let storeSnapshot = secretStore.Snapshot()

            let accessKey = OAuthRefreshDescriptor.accessTokenKey descriptor

            let expiryKey = OAuthRefreshDescriptor.accessExpiryKey descriptor

            let access = storeSnapshot |> List.tryFind (fun (k, _) -> snd k = accessKey)

            Expect.isSome access "access token persisted"
            Expect.equal (snd access.Value) "new-access" "access token matches body"

            let expiry = storeSnapshot |> List.tryFind (fun (k, _) -> snd k = expiryKey)

            Expect.isSome expiry "access expiry persisted"

            // Rotated refresh token persisted under the descriptor's key.
            let refresh =
                storeSnapshot |> List.tryFind (fun (k, _) -> snd k = descriptor.RefreshTokenKey)

            Expect.isSome refresh "refresh token present"
            Expect.equal (snd refresh.Value) "rt-rotated" "refresh token rotated"

            let refreshedAudits =
                auditLog.Filter(fun e ->
                    match e with
                    | OAuthTokenRefreshed p -> Some p
                    | _ -> None)

            Expect.hasLength refreshedAudits 1 "one OAuthTokenRefreshed audit emitted"
            Expect.equal refreshedAudits[0].Provider "ga4" "provider tag matches"
        }

        testCaseAsync "400 + invalid_grant → TokenInvalidatedByProvider + audit emitted"
        <| async {
            let respond _ =
                jsonResponse HttpStatusCode.BadRequest """{"error":"invalid_grant"}"""

            let refresher, _, auditLog, descriptor = mkBench respond
            do! (refresher :> IOAuthTokenRefresher).RegisterDescriptor descriptor

            let! result = (refresher :> IOAuthTokenRefresher).RefreshNow(descriptor.Provider, descriptor.ConfigId)

            match result with
            | TokenInvalidatedByProvider -> ()
            | other -> failtestf "Expected TokenInvalidatedByProvider, got %A" other

            let invalidatedAudits =
                auditLog.Filter(fun e ->
                    match e with
                    | OAuthRefreshTokenInvalidated p -> Some p
                    | _ -> None)

            Expect.hasLength invalidatedAudits 1 "one OAuthRefreshTokenInvalidated audit emitted"
        }

        testCaseAsync "500 → TransientError"
        <| async {
            let respond _ =
                jsonResponse HttpStatusCode.InternalServerError "upstream broken"

            let refresher, _, _, descriptor = mkBench respond
            do! (refresher :> IOAuthTokenRefresher).RegisterDescriptor descriptor

            let! result = (refresher :> IOAuthTokenRefresher).RefreshNow(descriptor.Provider, descriptor.ConfigId)

            match result with
            | TransientError reason -> Expect.stringContains reason "500" "names the status code"
            | other -> failtestf "Expected TransientError, got %A" other
        }

        testCaseAsync "401 (no invalid_grant) → PermanentError"
        <| async {
            let respond _ =
                jsonResponse HttpStatusCode.Unauthorized """{"error":"invalid_client"}"""

            let refresher, _, _, descriptor = mkBench respond
            do! (refresher :> IOAuthTokenRefresher).RegisterDescriptor descriptor

            let! result = (refresher :> IOAuthTokenRefresher).RefreshNow(descriptor.Provider, descriptor.ConfigId)

            match result with
            | PermanentError reason -> Expect.stringContains reason "401" "names the status code"
            | other -> failtestf "Expected PermanentError, got %A" other
        }
    ]

let tests = testList "InProcessOAuthTokenRefresher" [ contractTests; httpPathTests ]