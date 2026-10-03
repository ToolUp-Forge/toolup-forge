// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

// ─── Connector-neutral transport errors (Phase 128) ──────────────
//
// The HTTP failure taxonomy every outbound caller needs, with no AI
// vocabulary in it. Phase 251 factored the classification rule out of
// the AI providers, but typed it over `AIProviderError`, so a data
// connector reaching for it had to speak AI. This section is the
// neutral form of the same rule — 429 / 5xx retry-worthy, every other
// 4xx permanent, a transport-level failure retry-worthy — plus the two
// things a quota-bearing third-party API adds: the provider's
// `Retry-After` hint, and a refusal raised locally by an outbound
// budget before any byte left the process.
//
// It lives in this tier rather than in `ToolUp.Platform.Core` because
// Core takes a project reference on `ToolUp.AI.Wire` (Phases 250/251
// moved the contract types and `RetryPolicy` DOWN here to keep the
// Fable-safe floor below Core), so this is the lowest assembly every
// caller already reaches. The AI classifier below is re-expressed over
// this one, so the rule exists exactly once.
//
// FSharp.Core + `System.TimeSpan` only — Fable-compiles.

namespace ToolUp.Platform.Transport

open System
open ToolUp.Platform.AI // HttpResponse — the Phase 251 record, namespace preserved

/// Why one outbound HTTP call failed, independent of what was called.
[<RequireQualifiedAccess>]
type TransportError =
    /// Connection refused, TCP reset, DNS failure, a client-side timeout
    /// — no response arrived. Retry-worthy.
    | Network of message: string
    /// 429 or any 5xx. Retry-worthy; `retryAfter` carries the provider's
    /// `Retry-After` hint when it sent one (delta-seconds form).
    | Transient of statusCode: int * body: string * retryAfter: TimeSpan option
    /// Every other non-2xx status (400, 401, 403, 404, 409, 422, …). The
    /// same request would fail identically — not retry-worthy.
    | Permanent of statusCode: int * body: string
    /// The caller's own outbound budget refused the call before it was
    /// sent (a long-window quota is spent, or the provider asked for a
    /// pause longer than the caller is willing to wait). Not retry-worthy
    /// within this run: the budget, not the provider, said no.
    | Refused of reason: string
    /// The retry policy ran out. `attempts` counts every attempt made,
    /// the first included; `lastError` is the final attempt's failure.
    | Exhausted of attempts: int * lastError: TransportError

/// Classification of non-success responses into `TransportError`.
module TransportClassifier =

    /// Response header an outbound-budget decorator sets on the 429 it
    /// synthesises when it refuses a call locally. Its presence turns
    /// the response into `TransportError.Refused` rather than a
    /// retry-worthy `Transient`, so a spent daily quota is not retried
    /// into the ground. The body carries the refusal reason.
    [<Literal>]
    let RefusedHeader = "X-ToolUp-Outbound-Refused"

    let private headerValue (name: string) (headers: (string * string) list) : string option =
        headers
        |> List.tryFind (fun (key, _) -> String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map snd

    /// The provider's `Retry-After` hint, in its delta-seconds form.
    /// The HTTP-date form needs a clock this pure tier does not hold, so
    /// it reads as absent and the retry policy's own backoff applies.
    let tryRetryAfter (headers: (string * string) list) : TimeSpan option =
        match headerValue "Retry-After" headers with
        | Some raw ->
            match Int32.TryParse(raw.Trim()) with
            | true, seconds when seconds >= 0 -> Some(TimeSpan.FromSeconds(float seconds))
            | _ -> None
        | None -> None

    /// The status-only rule — byte-identical to the Phase 251 provider
    /// rule: 429 or any 5xx is `Transient`, every other status
    /// `Permanent`. Callers pass non-2xx statuses only.
    let classifyStatus (statusCode: int) (body: string) : TransportError =
        if statusCode = 429 || statusCode >= 500 then
            TransportError.Transient(statusCode, body, None)
        else
            TransportError.Permanent(statusCode, body)

    /// Classify a whole non-success response: the status rule, plus the
    /// provider's `Retry-After` hint on a transient status, plus the
    /// local-refusal marker an outbound budget sets.
    let classifyResponse (response: HttpResponse) : TransportError =
        match headerValue RefusedHeader response.Headers with
        | Some _ -> TransportError.Refused response.Body
        | None ->
            match classifyStatus response.StatusCode response.Body with
            | TransportError.Transient(code, body, _) ->
                TransportError.Transient(code, body, tryRetryAfter response.Headers)
            | other -> other

    /// Classify a transport-level failure (no response arrived).
    let classifyTransportFailure (message: string) : TransportError = TransportError.Network message

/// Companion functions over `TransportError`.
[<RequireQualifiedAccess>]
module TransportError =

    /// Whether another attempt could succeed.
    let isRetryable (error: TransportError) : bool =
        match error with
        | TransportError.Network _
        | TransportError.Transient _ -> true
        | TransportError.Permanent _
        | TransportError.Refused _
        | TransportError.Exhausted _ -> false

    /// The HTTP status the failure carried, when it carried one.
    let rec statusCode (error: TransportError) : int option =
        match error with
        | TransportError.Transient(code, _, _)
        | TransportError.Permanent(code, _) -> Some code
        | TransportError.Exhausted(_, last) -> statusCode last
        | TransportError.Network _
        | TransportError.Refused _ -> None

    /// Human-readable rendering for logs, job results and admin UIs.
    let rec toMessage (error: TransportError) : string =
        match error with
        | TransportError.Network message -> $"Transport failure: {message}"
        | TransportError.Transient(code, body, Some wait) ->
            $"Transient HTTP {code} (retry after {int wait.TotalSeconds} s): {body}"
        | TransportError.Transient(code, body, None) -> $"Transient HTTP {code}: {body}"
        | TransportError.Permanent(code, body) -> $"HTTP {code}: {body}"
        | TransportError.Refused reason -> $"Outbound budget refused the call: {reason}"
        | TransportError.Exhausted(attempts, last) -> $"Gave up after {attempts} attempts: {toMessage last}"

namespace ToolUp.Platform.AI

open ToolUp.Platform.Transport

// ─── Pure HTTP error classification (Wave 32, Phase 251) ─────────
//
// The `statusCode → AIProviderError` decision currently inlined
// identically in every provider's `*.fs` (`if code = 429 || code >= 500
// then TransientServer else PermanentClient`), lifted to one pure portable
// function so the per-provider mappers (Phases 252–254) classify by calling
// in, not by re-deriving the rule. No `System.Net.Http` — reasons over the
// numeric status only, so it Fable-compiles.
//
// Phase 128 — the rule itself now lives once, in the connector-neutral
// `TransportClassifier` above; this module projects it onto the AI
// taxonomy. Behaviour is unchanged.

module ErrorClassifier =

    /// Classify a non-success HTTP response into the retry-aware
    /// `AIProviderError` taxonomy. The rule is byte-identical to the
    /// providers' inline egress:
    ///   * 429 (rate-limited) or any 5xx → `TransientServer` (retry-worthy
    ///     with backoff);
    ///   * every other 4xx (401 / 403 auth, 400 bad request, 404 model not
    ///     found, content-policy 4xx) → `PermanentClient` (the same request
    ///     would fail identically — not retry-worthy).
    /// `body` is the vendor error payload, threaded into the error so the
    /// surfaced diagnostic keeps the provider's own message.
    ///
    /// Callers pass this only for non-2xx responses — a 2xx never reaches
    /// the classifier (the mapper parses the success body instead).
    let classifyStatus (statusCode: int) (body: string) : AIProviderError =
        match TransportClassifier.classifyStatus statusCode body with
        | TransportError.Transient(code, payload, _) -> TransientServer(code, payload)
        | _ -> PermanentClient(statusCode, body)

    /// Classify a transport-level failure (connection refused, TCP reset,
    /// DNS failure, a timeout from the HTTP client) — the `catch` arm in
    /// today's egress that maps an `HttpRequestException` / cancelled
    /// `OperationCanceledException` to `TransientNetwork`. Retry-worthy
    /// within the policy budget.
    let classifyTransportFailure (message: string) : AIProviderError = TransientNetwork message