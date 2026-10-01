module ToolUp.Platform.Tests.InProcess.IngestionEmbedderRetryTests

// ─── Phase 14t — embedder retry + dead-letter (unit) ─────────────────
//
// Covers the pure decision logic behind the ingestion retry / dead-letter
// path:
//   * `classifyIndexFailure` — 401/403 (bad creds) + other 4xx are
//     Permanent (dead-letter now); 429 / 5xx / timeout / network are
//     Transient (retry).
//   * `IngestionRetryPolicy` backoff — attempt 1 immediate, exponential
//     thereafter, capped at `MaxBackoff`; jitter bounded to
//     [0, JitterFactor·base].
//   * `IngestionAlertState` throttling — a provider outage failing N
//     chunks yields ONE Owner/Admin alert (not N), and the dead-letter-
//     rate alert fires once at the threshold then throttles.

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.RAG.IngestionTypes
open ToolUp.RAG.IngestionService

let private httpEx (status: int) : exn =
    HttpRequestException("http", null, Nullable<HttpStatusCode>(enum<HttpStatusCode> status)) :> exn

let private isPermanent (ex: exn) =
    match classifyIndexFailure ex with
    | Permanent _ -> true
    | Transient _ -> false

let private classification =
    testList "classifyIndexFailure" [
        test "401 / 403 are permanent (bad credentials)" {
            Expect.isTrue (isPermanent (httpEx 401)) "401 ⇒ permanent"
            Expect.isTrue (isPermanent (httpEx 403)) "403 ⇒ permanent"
        }

        test "other 4xx are permanent (non-retryable client error)" {
            Expect.isTrue (isPermanent (httpEx 400)) "400 ⇒ permanent"
            Expect.isTrue (isPermanent (httpEx 404)) "404 ⇒ permanent"
        }

        test "429 and 5xx are transient (retry)" {
            Expect.isFalse (isPermanent (httpEx 429)) "429 ⇒ transient"
            Expect.isFalse (isPermanent (httpEx 500)) "500 ⇒ transient"
            Expect.isFalse (isPermanent (httpEx 503)) "503 ⇒ transient"
        }

        test "timeout / cancellation / network are transient" {
            Expect.isFalse (isPermanent (TaskCanceledException() :> exn)) "timeout ⇒ transient"
            Expect.isFalse (isPermanent (TimeoutException() :> exn)) "TimeoutException ⇒ transient"
            Expect.isFalse (isPermanent (HttpRequestException("connection refused") :> exn)) "no-status ⇒ transient"
        }

        test "unknown failure defaults to transient (retry, never silent drop)" {
            Expect.isFalse (isPermanent (InvalidOperationException("weird") :> exn)) "unknown ⇒ transient"
        }
    ]

let private backoff =
    let p = IngestionRetryPolicy.defaults

    testList "IngestionRetryPolicy backoff + jitter" [
        test "attempt 1 runs immediately" {
            Expect.equal (IngestionRetryPolicy.baseDelayFor p 1) TimeSpan.Zero "attempt 1 ⇒ Zero"
        }

        test "backoff grows and is capped at MaxBackoff" {
            let d2 = IngestionRetryPolicy.baseDelayFor p 2
            let d3 = IngestionRetryPolicy.baseDelayFor p 3
            Expect.isGreaterThan d3 d2 "backoff is monotonically increasing"
            let dHuge = IngestionRetryPolicy.baseDelayFor p 30
            Expect.isLessThanOrEqual dHuge p.MaxBackoff "never exceeds MaxBackoff"
        }

        test "jitter is bounded to [0, JitterFactor·base]" {
            let baseMs = (IngestionRetryPolicy.baseDelayFor p 3).TotalMilliseconds
            let jZero = IngestionRetryPolicy.jitterComponentFor p 3 0.0
            let jFull = IngestionRetryPolicy.jitterComponentFor p 3 1.0
            Expect.equal jZero TimeSpan.Zero "sample 0.0 ⇒ no jitter"
            Expect.floatClose Accuracy.high jFull.TotalMilliseconds (baseMs * p.JitterFactor) "sample 1.0 ⇒ full jitter"

            Expect.isLessThanOrEqual
                jFull.TotalMilliseconds
                (baseMs * p.JitterFactor + 1.0)
                "jitter never exceeds the band"
        }

        test "no jitter on attempt 1 (base is Zero)" {
            Expect.equal (IngestionRetryPolicy.jitterComponentFor p 1 1.0) TimeSpan.Zero "attempt 1 ⇒ no jitter"
        }
    ]

let private alerts =
    let t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let window = TimeSpan.FromMinutes 5.0

    testList "IngestionAlertState throttling" [
        test "provider outage failing N chunks yields ONE alert, not N" {
            let st = IngestionAlertState()
            Expect.isTrue (st.ShouldAlertProvider("scope-1", t0, window)) "first chunk fires the alert"

            // Nine more failures inside the dedup window — none re-alert.
            for i in 1..9 do
                let within = t0.AddSeconds(float i)
                Expect.isFalse (st.ShouldAlertProvider("scope-1", within, window)) "within-window failures are deduped"

            // A different scope is independent.
            Expect.isTrue
                (st.ShouldAlertProvider("scope-2", t0.AddSeconds 1.0, window))
                "different scope alerts independently"

            // After the window elapses, the scope may alert again.
            Expect.isTrue (st.ShouldAlertProvider("scope-1", t0.AddMinutes 6.0, window)) "re-alerts after the window"
        }

        test "dead-letter-rate alert fires once at the threshold then throttles" {
            let st = IngestionAlertState()
            let threshold = 3

            Expect.isFalse (st.RecordDeadLetterAndShouldAlert("scope-1", t0, window, threshold)) "1st below threshold"
            Expect.isFalse (st.RecordDeadLetterAndShouldAlert("scope-1", t0, window, threshold)) "2nd below threshold"
            Expect.isTrue (st.RecordDeadLetterAndShouldAlert("scope-1", t0, window, threshold)) "3rd crosses ⇒ fires"

            Expect.isFalse
                (st.RecordDeadLetterAndShouldAlert("scope-1", t0, window, threshold))
                "further crossings throttled"
        }
    ]

// ─── Phase 867 — a revoked key is not a transient fault ──────────────
//
// The OpenAI companion (and any API-backed provider following the 14u
// contract) does NOT surface a 401 / 403 as an `HttpRequestException`: it
// emits its own platform-scoped audit and raises the typed
// `EmbeddingProviderUnavailableException`. A classifier that only knew
// the HTTP exception therefore filed a revoked key as `Transient`: five
// attempts over thirty minutes, a dead-letter recorded as transient, and
// the "provider rejected credentials" Owner/Admin alert never raised from
// ingestion. These pin the typed arm, through whatever wrapping the
// async machinery adds, and the alert end to end through the retry
// handler — the path a chunk's second and later attempts take.

type private CapturingChannel() =
    let published = ResizeArray<string * Notification>()
    let gate = obj ()

    member _.Published = lock gate (fun () -> published |> List.ofSeq)

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { lock gate (fun () -> published.Add(scopeId, notification)) }

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private credentialRejection =
    let rejected () =
        EmbeddingProviderUnavailableException(401, "invalid api key") :> exn

    testList "Phase 867 — a typed credentials rejection is permanent" [
        test "the typed EmbeddingProviderUnavailableException classifies Permanent" {
            match classifyIndexFailure (rejected ()) with
            | Permanent reason -> Expect.stringContains reason "401" "the reason names the status"
            | Transient _ -> failtest "a revoked key was classified Transient — it would be retried for thirty minutes"
        }

        test "the typed exception is found through the wrapping the async machinery adds" {
            let wrapped: exn list = [
                AggregateException(rejected ())
                AggregateException(AggregateException(rejected ()))
                InvalidOperationException("outer", rejected ())
                AggregateException(TimeoutException "a sibling", rejected ())
            ]

            for ex in wrapped do
                Expect.isTrue
                    (isPermanent ex)
                    (sprintf "%s wrapping the typed rejection ⇒ permanent" (ex.GetType().Name))
        }

        testCaseAsync "a chunk whose provider rejects credentials raises the Owner/Admin alert and is not retried"
        <| async {
            let channel = CapturingChannel()

            let pipeline =
                { new IRetrievalPipeline with
                    member _.Retrieve _ _ = async.Return []
                    member _.Index _ _ _ = async { return raise (rejected ()) }
                    member _.DeleteByScope _ = async.Return()
                }

            let deps: IngestionRetryDeps = {
                Pipeline = pipeline
                EventStore = ToolUp.Platform.InMemoryEventStore.InMemoryEventStore()
                Observers = []
                NotificationChannel = Some(channel :> INotificationChannel)
                Telemetry = ToolUp.RAG.RagTelemetry.NoOpRagTelemetry()
                Logger = silentLogger
                // Zero backoff: the handler owns the delay, and this pin is
                // about the classification, not the wait.
                Policy = {
                    IngestionRetryPolicy.defaults with
                        InitialBackoff = TimeSpan.Zero
                        MaxBackoff = TimeSpan.Zero
                        JitterFactor = 0.0
                }
                AlertState = IngestionAlertState()
            }

            let payload: IngestionRetryPayload = {
                DocumentId = "doc-867"
                DocumentName = "doc.pdf"
                ChunkId = "doc-867:chunk:0"
                ChunkIndex = 0
                Chunk = { Content = "x"; Metadata = Map.empty }
                Scope = Deployment
                ScopeId = "team-867"
                Container = "team-867"
                OriginatingUserId = None
                Attempt = None
            }

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = "team-867"
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
                // Scheduler attempt 1 = ingestion attempt 2: retries remain,
                // so a Transient verdict would re-dispatch rather than stop.
                Attempt = 1
                Trigger = Manual
                Scope = ResolvedScope.anonymous
                TriggerSource = ScheduledManually "system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = Outcome.toJson payload
                DeadLetterDestination = None
            }

            let handler = ToolUp.RAG.IngestionRetryJobHandler.create deps
            let! result = handler.Execute ctx

            match result with
            | JobResult.PermanentFailure _ -> ()
            | other -> failtestf "a revoked key must stop retrying at once; the handler returned %A" other

            let alerts =
                channel.Published
                |> List.choose (fun (scope, n) ->
                    match n with
                    | SystemMessage(SystemMessageLevel.Error, text) -> Some(scope, text)
                    | _ -> None)

            match alerts with
            | [ scope, text ] ->
                Expect.equal scope "team-867" "the alert goes to the tenant scope that uploaded"
                Expect.stringContains text "rejected" "the alert says the provider rejected ingestion"
            | other -> failtestf "expected exactly one Owner/Admin error alert, got %A" other
        }
    ]

let tests =
    testList "Phase 14t — embedder retry + dead-letter" [ classification; backoff; alerts; credentialRejection ]