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

let tests =
    testList "Phase 14t — embedder retry + dead-letter" [ classification; backoff; alerts ]