// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

// ─── Portable HTTP egress seam (Wave 32, Phase 251) ──────────────
//
// `IHttpTransport` is the one async boundary the wire mapping needs: a
// per-provider mapper builds a `JsonValue` request body and parses a
// `JsonValue` response, and the *host* injects how the bytes actually
// travel. The server host wraps a shared `System.Net.Http.HttpClient`
// (`ToolUp.AI.Server`'s `HttpClientTransport`); a browser host wraps
// `fetch` (consumer-side — it never enters the SDK). Both implement the
// same minimal contract, so the mapper stays 100% pure and Fable-safe.
//
// These records carry only FSharp.Core + primitive types — no
// `System.Net.Http`, no `HttpRequestMessage` — so this file Fable-compiles
// alongside the rest of `ToolUp.AI.Wire`. The .NET `HttpClientTransport`
// is the non-portable companion that maps these records onto the BCL
// `HttpClient` (GP 12 — the injected-interface posture; GP 7 — async at
// the boundary).

/// A single outbound HTTP request, host-agnostic. `Method` is the verb
/// ("POST", "GET"); `Url` is relative to the transport's configured base
/// address (the provider's `HttpClient.BaseAddress` on the .NET host),
/// matching today's inline egress which posts to e.g. "/v1/chat/completions".
/// `Headers` are request-level headers only (Authorization, x-api-key,
/// anthropic-version) — the content-type rides with the body. `Body` is
/// `None` for verbs without a payload; `Some json` carries a UTF-8
/// JSON string.
type HttpRequest = {
    Method: string
    Url: string
    Headers: (string * string) list
    Body: string option
}

module HttpRequest =
    /// A JSON POST — the shape every AI provider uses. Convenience for
    /// the common case (verb "POST", a `Some` body).
    let post (url: string) (headers: (string * string) list) (body: string) : HttpRequest = {
        Method = "POST"
        Url = url
        Headers = headers
        Body = Some body
    }

/// A buffered HTTP response, host-agnostic. `StatusCode` is the numeric
/// HTTP status (the error classifier reasons over it); `Body` is the
/// fully-read response (success payload or error body — providers read
/// the error body to surface vendor diagnostics). `Headers` carries the
/// response-level headers for callers that inspect them.
type HttpResponse = {
    StatusCode: int
    Headers: (string * string) list
    Body: string
}

module HttpResponse =
    /// True for a 2xx status — mirrors `HttpResponseMessage.IsSuccessStatusCode`
    /// so a migrated mapper can branch success/error without touching the BCL.
    let isSuccess (response: HttpResponse) : bool =
        response.StatusCode >= 200 && response.StatusCode < 300

/// The injected egress. One method, one async boundary. A host supplies
/// the implementation; the wire mapper calls `Send` and never sees a
/// concrete HTTP client. The buffered shape (a fully-read `HttpResponse`)
/// is the non-streaming path; SSE streaming stays a host-specific concern
/// the .NET transport reproduces with `HttpCompletionOption.ResponseHeadersRead`
/// (it cannot ride a portable string-body record).
type IHttpTransport =
    abstract member Send: request: HttpRequest -> Async<HttpResponse>