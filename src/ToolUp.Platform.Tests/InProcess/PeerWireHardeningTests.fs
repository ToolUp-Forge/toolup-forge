module ToolUp.Platform.Tests.InProcess.PeerWireHardeningTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.Contracts

// ─── Phase 315 — peer host wire hardening ────────────────────────────
//
// Two wire-level fixes on `JsonRpcPeerHost`, each pinned with a probe
// that goes red when the hardening is removed and a CONTROL asserting
// the legitimate path still works. Without the pair, "the call was
// refused" would pass equally against a host that had broken and
// started refusing everything.
//
//   1. **The contract route reads the body under a ceiling.** It used
//      to call `ctx.ReadBodyFromRequestAsync()` and buffer whatever
//      arrived. Auth-gating bounds *who* can do that — a real bound on
//      the attacker set, and none at all on the memory: a federation
//      trusts its peers to be authentic, not to be well-behaved, and a
//      merely buggy one is enough. The ceiling is enforced twice, and
//      the tests treat those as separate claims: a declared
//      `Content-Length` over the limit is refused without reading a
//      byte, and a request that declares nothing (chunked encoding,
//      which the CALLER picks) is stopped by the bounded read. A
//      header-only check would be a suggestion.
//   2. **The job-poll response carries a correlation id.** Every poll
//      response hard-coded `Id = ""`, so it paired with nothing, while
//      a dispatch response has always echoed `request.Id`.
//
// The byte-counting cases below drive `JsonRpcPeerHost.routes` against a
// hand-built `HttpContext` whose request body is a stream that records
// how much of itself was pulled. That is the only way to assert "before
// it is fully buffered" as a measurement rather than an inference — over
// a `TestServer` the same request would only tell us the status code.

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

let private seedSigningKey (store: ISecretStore) (peerId: string) (key: string) =
    store.SetSecret("_platform", $"peers/{peerId}/signing-key", key)
    |> Async.RunSynchronously
    |> ignore

let private callerId: PeerIdentity = {
    PeerId = "buyer"
    DisplayName = "Buyer Deployment"
}

let private receiverId: PeerIdentity = {
    PeerId = "seller"
    DisplayName = "Seller Deployment"
}

let private intruderId: PeerIdentity = {
    PeerId = "intruder"
    DisplayName = "Intruder Deployment"
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private contractId = "ledger"

let private callerKey = "wire-hardening-buyer-signing-key-0123456789"
let private intruderKey = "wire-hardening-intruder-signing-key-012345"

/// The contract the receiver hosts. NOT `private`: the host reflects via
/// `FSharpType.IsRecord`, and a private record reads back as a non-record.
type LedgerContract = { Measure: string -> Async<int> }

/// Counts its own invocations, so a test can assert the difference
/// between "refused at the wire" and "refused after the handler ran".
let private dispatches = ref 0

let private ledgerImpl: LedgerContract = {
    Measure =
        fun payload -> async {
            Interlocked.Increment dispatches |> ignore
            return payload.Length
        }
}

let private authFor (secrets: ISecretStore) =
    JwtPeerAuthProvider(secrets) :> IPeerAuthProvider

let private issue (provider: IPeerAuthProvider) (caller: PeerIdentity) = async {
    match! provider.IssuePeerToken(caller, receiverId, Anonymous) with
    | Ok token -> return token
    | Error e -> return failtestf "Expected a minted token, got %A" e
}

/// The receiver's provider set, without the transport: everything the
/// handlers resolve from `ctx.RequestServices`. `limits = None` is the
/// composition that never called `withWireLimits` — the host must then
/// fall back to `PeerWireLimits.defaults` rather than to no limit.
let private receiverServices (auth: IPeerAuthProvider) (limits: PeerWireLimits option) : IServiceProvider =
    let peer = DefaultPlatformPeer() :> IPlatformPeer

    let ledgerHost =
        JsonRpcPeerHost.contract<LedgerContract> contractId [ v1 ] None ledgerImpl

    peer.RegisterContract ledgerHost.Registration

    let services = ServiceCollection()
    services.AddSingleton<IPeerAuthProvider>(auth) |> ignore
    services.AddSingleton<IPlatformPeer>(peer) |> ignore

    match limits with
    | Some l -> services.AddSingleton<PeerWireLimits>(l) |> ignore
    | None -> ()

    services.BuildServiceProvider() :> IServiceProvider

/// A read-only stream over a fixed payload that records how many bytes
/// the consumer actually pulled. The whole point of the cap is that this
/// number stays bounded no matter how large the payload is.
type private CountingStream(payload: byte[]) =
    inherit Stream()
    let inner = new MemoryStream(payload)
    let mutable bytesRead = 0L

    member _.BytesRead = bytesRead

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = int64 payload.Length

    override _.Position
        with get () = inner.Position
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.Read(buffer, offset, count) =
        let n = inner.Read(buffer, offset, count)
        bytesRead <- bytesRead + int64 n
        n

    override this.ReadAsync(buffer: byte[], offset: int, count: int, _ct: CancellationToken) : Task<int> =
        Task.FromResult(this.Read(buffer, offset, count))

/// Drive `JsonRpcPeerHost.routes` directly against a hand-built context,
/// returning the status code, the response body, and the request stream
/// so the test can read its byte counter.
let private invokeRoutes
    (services: IServiceProvider)
    (token: string option)
    (declaredLength: int64 option)
    (body: CountingStream)
    =
    task {
        let ctx = DefaultHttpContext()
        ctx.RequestServices <- services
        ctx.Request.Method <- "POST"
        ctx.Request.Path <- PathString $"/peer/v1/{contractId}"
        ctx.Request.Body <- body

        ctx.Request.ContentLength <-
            match declaredLength with
            | Some len -> Nullable<int64> len
            | None -> Nullable<int64>()

        match token with
        | Some t -> ctx.Request.Headers["Authorization"] <- Microsoft.Extensions.Primitives.StringValues $"Bearer {t}"
        | None -> ()

        use responseBody = new MemoryStream()
        ctx.Response.Body <- responseBody

        let! _ = JsonRpcPeerHost.routes earlyReturn ctx

        return ctx.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray())
    }

/// A payload of `size` bytes that is *valid JSON* — so a host that read
/// it all the way through would fail on the JSON-RPC shape rather than
/// on the encoding, and the 413 cases cannot be mistaken for a parse
/// accident.
let private jsonPayload (size: int) =
    let filler = String('x', max 0 (size - 12))
    Encoding.UTF8.GetBytes $"{{\"pad\":\"{filler}\"}}"

let private structuredError (body: string) =
    let response = JsonRpc.deserialize<JsonRpcResponse> body

    match response.Error with
    | None -> failtestf "expected a JSON-RPC error response, got %s" body
    | Some err ->
        match err.Data with
        | Some data -> err.Code, JsonRpc.deserialize<PeerError> data
        | None -> failtestf "expected the structured PeerError in `data`, got %s" body

// ─── (1) The request-body ceiling ────────────────────────────────────

let private tightCap: PeerWireLimits =
    PeerWireLimits.defaults |> PeerWireLimits.withMaxRequestBytes 4096L

let requestBodyCapTests =
    testList "Phase 315 — the peer contract route reads the body under a ceiling" [

        testCaseAsync "an over-cap body with NO Content-Length is stopped by the bounded read"
        <| async {
            // The chunked-encoding case, which a header check cannot
            // see: `Content-Length` is absent because the caller chose
            // it to be. The claim under test is a MEASUREMENT — the
            // receiver pulls a bounded number of bytes off a stream that
            // would have handed it a megabyte.
            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerId.PeerId callerKey
            let auth = authFor secrets
            let! token = issue auth callerId

            let payload = jsonPayload (1024 * 1024)
            let stream = new CountingStream(payload)

            let! status, body =
                invokeRoutes (receiverServices auth (Some tightCap)) (Some token) None stream
                |> Async.AwaitTask

            Expect.equal status 413 "an over-ceiling body is refused with 413, not accepted and not a 500"

            let code, err = structuredError body
            Expect.equal code JsonRpc.requestTooLarge "the refusal carries the language-neutral JSON-RPC code"

            Expect.equal
                err
                (PeerRequestTooLarge 4096L)
                "…and the structured PeerError names the ceiling the caller has to fit under"

            Expect.isLessThan
                stream.BytesRead
                (int64 payload.Length)
                "the receiver did NOT read the whole body — this is the difference between a limit and a measurement"

            Expect.isLessThanOrEqual
                stream.BytesRead
                (4096L + 16384L)
                "reads stop within one buffer of the ceiling, however large the payload is"
        }

        testCaseAsync "a declared Content-Length over the cap is refused without reading a byte"
        <| async {
            // The cheap path: the caller announced the size, so nothing
            // needs to be pulled at all. Removing this pre-check leaves
            // the test green on status and red on the byte counter,
            // which is the point of asserting both.
            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerId.PeerId callerKey
            let auth = authFor secrets
            let! token = issue auth callerId

            let payload = jsonPayload (1024 * 1024)
            let stream = new CountingStream(payload)

            let! status, body =
                invokeRoutes (receiverServices auth (Some tightCap)) (Some token) (Some(int64 payload.Length)) stream
                |> Async.AwaitTask

            Expect.equal status 413 "a declared over-size body is refused"
            let _, err = structuredError body
            Expect.equal err (PeerRequestTooLarge 4096L) "…with the same structured error as the bounded-read path"

            Expect.equal
                stream.BytesRead
                0L
                "not one byte of the announced payload was read — the declaration alone settled it"
        }

        testCaseAsync "NEGATIVE CONTROL — under a generous ceiling the same payload IS read in full"
        <| async {
            // Without this the two cases above would pass just as
            // happily against a route that refused everything, or
            // against a counting stream that counted nothing. Here the
            // ceiling is the 8 MiB default, the 1 MiB payload is read
            // end to end, and the request fails on its JSON-RPC shape
            // (400) — proof that the 413s above were the cap talking.
            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerId.PeerId callerKey
            let auth = authFor secrets
            let! token = issue auth callerId

            let payload = jsonPayload (1024 * 1024)
            let stream = new CountingStream(payload)

            let! status, body =
                invokeRoutes (receiverServices auth None) (Some token) None stream
                |> Async.AwaitTask

            Expect.equal status 400 "a within-ceiling body is read and then judged on its content"
            let _, err = structuredError body

            match err with
            | PeerDeserialization _ -> ()
            | other -> failtestf "expected the parse failure of a well-formed but non-JSON-RPC body, got %A" other

            Expect.equal
                stream.BytesRead
                (int64 payload.Length)
                "the whole payload was pulled — the counter measures real reads, and 1 MiB is under the default"
        }

        testCaseAsync "the DEFAULT ceiling is in force with no PeerWireLimits registered"
        <| async {
            // GP 11's other half: the tunable defaults to something, and
            // that something is enforced. Declared at 9 MiB against the
            // 8 MiB default, so the pre-check settles it and the test
            // costs nothing to run.
            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerId.PeerId callerKey
            let auth = authFor secrets
            let! token = issue auth callerId

            let stream = new CountingStream(jsonPayload 64)

            let! status, body =
                invokeRoutes (receiverServices auth None) (Some token) (Some(9L * 1024L * 1024L)) stream
                |> Async.AwaitTask

            Expect.equal status 413 "a composition that never called withWireLimits still has a ceiling"
            let _, err = structuredError body

            Expect.equal
                err
                (PeerRequestTooLarge PeerWireLimits.defaultMaxRequestBytes)
                "…and it is the documented default, reported to the caller"
        }

        testCaseAsync "the ceiling does not fire ahead of the credential check"
        <| async {
            // Phase 343 closed a status-code oracle on this route: every
            // credential defect answers 401, identically. A size check
            // moved above auth would hand an UNAUTHENTICATED caller a
            // 413 — a new oracle, and one that also tells them the
            // receiver's ceiling for free.
            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerId.PeerId callerKey
            let auth = authFor secrets

            let stream = new CountingStream(jsonPayload (1024 * 1024))

            let! status, _ =
                invokeRoutes (receiverServices auth (Some tightCap)) None None stream
                |> Async.AwaitTask

            Expect.equal status 401 "an unauthenticated over-cap request is a rejected credential, nothing more"

            Expect.equal
                stream.BytesRead
                0L
                "…and it still buffers nothing, because auth already ran before the read (Phase 330's ordering)"
        }

        testCase "the limits are a value a composition sets, and default when it does not"
        <| fun _ ->
            let fresh = PeerCompose.PeerServerApp.create ()

            Expect.equal
                fresh.WireLimits
                PeerWireLimits.defaults
                "a fresh PeerServerApp carries the defaults — no opt-in required for the ceiling to exist"

            Expect.equal
                PeerWireLimits.defaults.MaxRequestBytes
                (8L * 1024L * 1024L)
                "the documented default is 8 MiB — far above any argument array the substrate is shaped to carry"

            let raised =
                fresh
                |> PeerCompose.PeerServerApp.withWireLimits (
                    PeerWireLimits.defaults
                    |> PeerWireLimits.withMaxRequestBytes (32L * 1024L * 1024L)
                )

            Expect.equal
                raised.WireLimits.MaxRequestBytes
                (32L * 1024L * 1024L)
                "…and a deployment that genuinely exchanges more raises it in one line"
    ]

// ─── (2) The job-poll response correlates ────────────────────────────

let private pollTarget: TargetPeer = {
    Peer = receiverId
    BaseUrl = "http://localhost"
}

/// The parked result the ownership assertions look for. Contains letters
/// outside the hex alphabet on purpose — see the comment at its use.
let private parkedSentinel = "parked-result-QZX"

/// A `TestServer`-hosted receiver with the job-fusion substrate present,
/// so the poll route reaches the result store. The scheduler stub throws
/// on any use — polling must never schedule.
let private buildPollReceiver (auth: IPeerAuthProvider) =
    let store = IPlatformPeerContract.InMemoryResultStore() :> IPeerJobResultStore

    let fusion: PeerJobFusion = {
        Scheduler = IPlatformPeerContract.StubScheduler()
        ResultStore = store
        AuditLog = None
    }

    let peer = DefaultPlatformPeer() :> IPlatformPeer

    let ledgerHost =
        JsonRpcPeerHost.contract<LedgerContract> contractId [ v1 ] (Some fusion) ledgerImpl

    peer.RegisterContract ledgerHost.Registration

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun services ->
                        services.AddSingleton<IPeerAuthProvider>(auth) |> ignore
                        services.AddSingleton<IPlatformPeer>(peer) |> ignore
                        services.AddSingleton<PeerJobFusion>(fusion) |> ignore)
                    .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe JsonRpcPeerHost.routes)
                |> ignore)
            .Build()

    store, host

/// Raw `GET` on the poll route, returning the status and the parsed
/// JSON-RPC envelope — the in-tree client never surfaces `Id`, so the
/// wire has to be read directly to see it.
let private pollRaw (client: HttpClient) (token: string) (jobId: Guid) = async {
    use request =
        new HttpRequestMessage(HttpMethod.Get, $"http://localhost/peer/v1/{contractId}/jobs/{jobId}")

    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
    let! response = client.SendAsync request |> Async.AwaitTask
    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
    return int response.StatusCode, JsonRpc.deserialize<JsonRpcResponse> body, body
}

let pollCorrelationTests =
    testList "Phase 315 — the job-poll response carries a correlation id" [

        testCaseAsync "every answer on the poll route echoes the polled jobId as its JSON-RPC Id"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerId.PeerId callerKey
            seedSigningKey secrets intruderId.PeerId intruderKey
            let receiverAuth = authFor secrets

            let callerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey callerSecrets callerId.PeerId callerKey
            let callerAuth = authFor callerSecrets

            let intruderSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey intruderSecrets intruderId.PeerId intruderKey
            let intruderAuth = authFor intruderSecrets

            let store, receiver = buildPollReceiver receiverAuth
            use host = receiver
            host.Start()
            use client = host.GetTestClient()

            let completed = Guid.NewGuid()
            let unknown = Guid.NewGuid()

            // The leak sentinel is deliberately NOT a number. Now that
            // the response echoes the jobId, a numeric marker like "42"
            // shows up inside a random GUID often enough to fail the
            // no-leak assertion for the wrong reason — the probe has to
            // be un-representable in hex to measure what it claims.
            do!
                store.SaveResult(
                    "_platform",
                    completed,
                    callerId.PeerId,
                    PeerJobStatus.Completed(JsonRpc.serialize parkedSentinel)
                )

            let! callerToken = issue callerAuth callerId
            let! intruderToken = issue intruderAuth intruderId

            // (a) the owner's terminal read …
            let! ownedStatus, owned, ownedBody = pollRaw client callerToken completed
            Expect.equal ownedStatus 200 "the owner reads its own parked result"

            Expect.equal
                owned.Id
                (string completed)
                "the response pairs with the job it was asked about — this is what `Id = \"\"` could not do"

            Expect.isTrue
                (ownedBody.Contains parkedSentinel)
                "CONTROL — the status still rides in `Result`, so a client reading only that is unaffected"

            // (b) … an unknown id, which answers Pending to everyone …
            let! pendingStatus, pending, _ = pollRaw client callerToken unknown
            Expect.equal pendingStatus 200 "an unknown jobId is Pending, disclosing nothing"

            Expect.equal
                pending.Id
                (string unknown)
                "a Pending answer correlates too — a poller with several jobs in flight can tell them apart"

            // (c) … and the non-owner refusal.
            let! refusedStatus, refused, refusedBody = pollRaw client intruderToken completed
            Expect.equal refusedStatus 401 "a non-owner is still refused (Phase 308 is untouched)"

            Expect.equal
                refused.Id
                (string completed)
                "the refusal correlates as well — the id came from the caller's own URL, so echoing it discloses nothing"

            Expect.isFalse (refusedBody.Contains parkedSentinel) "…and it carries no trace of the parked result"

            Expect.notEqual owned.Id "" "the whole defect, stated positively: the id is no longer empty"
        }

        testCaseAsync "CONTROL — the in-tree client still round-trips a poll unchanged"
        <| async {
            // Existing callers read `Result` / `Error` and ignore `Id`,
            // so they must observe no difference at all. Asserted with
            // the real `HttpPeerClient` rather than by reasoning about
            // what it reads.
            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerId.PeerId callerKey
            let receiverAuth = authFor secrets

            let callerSecrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey callerSecrets callerId.PeerId callerKey
            let callerAuth = authFor callerSecrets

            let store, receiver = buildPollReceiver receiverAuth
            use host = receiver
            host.Start()
            use client = host.GetTestClient()

            let jobId = Guid.NewGuid()
            do! store.SaveResult("_platform", jobId, callerId.PeerId, PeerJobStatus.Completed(JsonRpc.serialize 42))

            let transport = HttpPeerClient(client, callerAuth, callerId) :> IPeerClient
            let! polled = transport.PollJob(pollTarget, contractId, jobId)

            match polled with
            | Ok(PeerJobStatus.Completed json) ->
                Expect.equal (JsonRpc.deserialize<int> json) 42 "the typed result still arrives intact"
            | other -> failtestf "Expected Completed, got %A" other
        }

        testCaseAsync "CONTROL — a within-ceiling dispatch is unchanged, id echo and all"
        <| async {
            // The other half of GP 11: the invoke leg's response shape
            // is untouched by this phase. A genuine call is dispatched,
            // the handler runs, and the response echoes `request.Id`
            // exactly as it always has.
            let secrets = InMemorySecretStore() :> ISecretStore
            seedSigningKey secrets callerId.PeerId callerKey
            let auth = authFor secrets
            let! token = issue auth callerId

            let before = dispatches.Value

            let context: PeerCallContext = {
                Peer = callerId
                User = Anonymous
                ContractVersion = v1
                Route = [ callerId.PeerId ]
                RootRequestId = "root-request-315"
                ParentRequestId = None
                HopsRemaining = 4
            }

            let payload: PeerWirePayload = {
                Context = context
                // The positional-argument wire shape `marshalArgs`
                // produces: a JSON array, one element per parameter.
                Arguments = """["hello"]"""
            }

            let envelope = JsonRpc.request context.RootRequestId "Measure" payload
            let bytes = Encoding.UTF8.GetBytes(JsonRpc.serialize envelope)
            let stream = new CountingStream(bytes)

            let! status, body =
                invokeRoutes (receiverServices auth (Some tightCap)) (Some token) (Some(int64 bytes.Length)) stream
                |> Async.AwaitTask

            Expect.equal status 200 "a normal-sized call is dispatched exactly as before"

            let response = JsonRpc.deserialize<JsonRpcResponse> body
            Expect.equal response.Id context.RootRequestId "the dispatch response still echoes the request id"
            Expect.isNone response.Error "…and carries no error"

            Expect.equal
                (response.Result |> Option.map JsonRpc.deserialize<int>)
                (Some 5)
                "the handler ran and its typed result came back"

            Expect.equal (dispatches.Value - before) 1 "the contract implementation was reached exactly once"
        }
    ]