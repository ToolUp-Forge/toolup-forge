// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

open ToolUp.Platform.Transport

// ─── Pure HTTP error classification ──────────────────────────────
//
// The `statusCode → AIProviderError` decision, projected from the
// connector-neutral `TransportClassifier` onto the AI taxonomy. No
// `System.Net.Http` — it reasons over the numeric status only, so it
// Fable-compiles.

module ErrorClassifier =

    /// Classify a non-success HTTP response into the retry-aware
    /// `AIProviderError` taxonomy:
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
    /// DNS failure, a timeout from the HTTP client) as `TransientNetwork`.
    /// Retry-worthy within the policy budget.
    let classifyTransportFailure (message: string) : AIProviderError = TransientNetwork message