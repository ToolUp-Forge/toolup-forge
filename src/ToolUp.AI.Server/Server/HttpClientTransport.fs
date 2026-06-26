// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

open System
open System.Net.Http
open System.Text
open System.Threading
open ToolUp.Platform // RetryPolicy.clampTimeoutMs

// ─── .NET HTTP transport (Wave 32, Phase 251) ────────────────────
//
// The non-portable companion to the portable `IHttpTransport` seam: it
// maps the host-agnostic `HttpRequest` / `HttpResponse` records onto the
// BCL `HttpClient`, reproducing the egress the AI providers currently
// inline byte-for-byte — the same per-process shared `HttpClient` (stable
// `BaseAddress` + instance-wide `Timeout`), the same per-request headers on
// the `HttpRequestMessage` (never on the client), the same per-call timeout
// `CancellationTokenSource` derived from `RetryPolicy.Timeout` and clamped
// via `RetryPolicy.clampTimeoutMs`, and the same `ResponseHeadersRead`
// completion option for the SSE streaming path.
//
// This lives in `ToolUp.AI.Server` (not the portable `ToolUp.AI.Wire`
// tier) because it references `System.Net.Http` — GP 1 keeps the BCL HTTP
// dependency out of the Fable-safe wire tier. The browser `fetch` transport
// is the consumer-side mirror; it never enters the SDK.
//
// Phase 251 is purely additive: the existing providers keep their inline
// egress until their own migration phases (252–254). This transport is the
// byte-identical reference target they migrate onto.

/// `IHttpTransport` over a BCL `HttpClient`. Construct with the provider's
/// shared client (its `BaseAddress` / instance `Timeout` already set) and
/// an optional per-call timeout (the caller's `RetryPolicy.Timeout`):
///
///   HttpClientTransport(sharedClient, retryPolicy.Timeout)
///
/// Each `Send` issues one request lifecycle — fresh `HttpRequestMessage`
/// (they cannot be reused after sending), per-call `CancellationTokenSource`,
/// buffered response read. Transport-level failures propagate as exceptions
/// for the caller's `catch` arm to classify via
/// `ErrorClassifier.classifyTransportFailure` (the retry runner sits above
/// the transport, exactly as the inline loop does today).
type HttpClientTransport(client: HttpClient, ?timeout: TimeSpan) =

    /// Build the per-call timeout CTS — byte-identical to the providers'
    /// inline construction: a clamped `CancellationTokenSource` when a
    /// per-call timeout is supplied, an untimed one otherwise (deferring
    /// to the client's instance-wide `Timeout`).
    let newTimeoutCts () : CancellationTokenSource =
        match timeout with
        | Some t ->
            let clampedMs = RetryPolicy.clampTimeoutMs (int t.TotalMilliseconds)
            new CancellationTokenSource(TimeSpan.FromMilliseconds(float clampedMs))
        | None -> new CancellationTokenSource()

    /// Map the portable `HttpRequest` onto a fresh `HttpRequestMessage`:
    /// verb + relative URL, request-level headers added to `.Headers`
    /// (matching the providers' `request.Headers.Add`), and a UTF-8 JSON
    /// `StringContent` body when present (the content-type rides with the
    /// body, exactly as today).
    let toRequestMessage (request: HttpRequest) : HttpRequestMessage =
        let message = new HttpRequestMessage(HttpMethod(request.Method), request.Url)

        match request.Body with
        | Some body -> message.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        | None -> ()

        for (name, value) in request.Headers do
            message.Headers.Add(name, value)

        message

    /// Flatten response + content headers into the portable header list.
    let collectHeaders (response: HttpResponseMessage) : (string * string) list =
        let fromCollection (headers: Headers.HttpHeaders) =
            headers
            |> Seq.collect (fun kv -> kv.Value |> Seq.map (fun v -> kv.Key, v))
            |> List.ofSeq

        fromCollection response.Headers
        @ (if isNull response.Content then
               []
           else
               fromCollection response.Content.Headers)

    /// The streaming-friendly send: read only the response headers, leaving
    /// the body stream open for an SSE reader. .NET-only (returns the raw
    /// `HttpResponseMessage`), so it is NOT on the portable `IHttpTransport`
    /// interface — it is the byte-identical reproduction of the providers'
    /// `client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
    /// cts.Token)` streaming path, exposed for the per-provider migrations
    /// (252–254). The caller owns reading + disposing the response.
    member _.SendForStreaming(request: HttpRequest, cancellationToken: CancellationToken) : Async<HttpResponseMessage> = async {
        let message = toRequestMessage request

        let! response =
            client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            |> Async.AwaitTask

        return response
    }

    interface IHttpTransport with
        /// Buffered request/response — byte-identical to the providers'
        /// non-streaming arm: `client.SendAsync(request, cts.Token)`, then
        /// read the body string. Status classification is the caller's job
        /// (via `ErrorClassifier`), so a non-2xx is returned as data, not
        /// thrown — only genuine transport failures throw.
        member _.Send(request: HttpRequest) : Async<HttpResponse> = async {
            let message = toRequestMessage request
            use cts = newTimeoutCts ()

            let! response = client.SendAsync(message, cts.Token) |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            return {
                StatusCode = int response.StatusCode
                Headers = collectHeaders response
                Body = body
            }
        }