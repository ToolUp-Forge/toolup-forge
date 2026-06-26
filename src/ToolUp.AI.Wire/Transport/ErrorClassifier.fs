// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

// ─── Pure HTTP error classification (Wave 32, Phase 251) ─────────
//
// The `statusCode → AIProviderError` decision currently inlined
// identically in every provider's `*.fs` (`if code = 429 || code >= 500
// then TransientServer else PermanentClient`), lifted to one pure portable
// function so the per-provider mappers (Phases 252–254) classify by calling
// in, not by re-deriving the rule. No `System.Net.Http` — reasons over the
// numeric status only, so it Fable-compiles.

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
        if statusCode = 429 || statusCode >= 500 then
            TransientServer(statusCode, body)
        else
            PermanentClient(statusCode, body)

    /// Classify a transport-level failure (connection refused, TCP reset,
    /// DNS failure, a timeout from the HTTP client) — the `catch` arm in
    /// today's egress that maps an `HttpRequestException` / cancelled
    /// `OperationCanceledException` to `TransientNetwork`. Retry-worthy
    /// within the policy budget.
    let classifyTransportFailure (message: string) : AIProviderError = TransientNetwork message