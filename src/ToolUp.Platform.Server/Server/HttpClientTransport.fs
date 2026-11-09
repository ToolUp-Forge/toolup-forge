// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Infra.Transport

open ToolUp.Platform
open ToolUp.Platform.Transport

open System
open System.Net.Http
open System.Text
open System.Threading

// ─── .NET HTTP transport ─────────────────────────────────────────
//
// The non-portable companion to the portable `IHttpTransport` seam: it
// maps the host-agnostic `HttpRequest` / `HttpResponse` records onto the
// BCL `HttpClient` — a per-process shared client (stable `BaseAddress` +
// instance-wide `Timeout`), per-request headers on the `HttpRequestMessage`
// (never on the client), an optional per-call timeout clamped via
// `RetryPolicy.clampTimeoutMs`, and the `ResponseHeadersRead` completion
// option for the SSE streaming path.
//
// It lives in the server tier (not the portable `ToolUp.Platform.Transport`
// tier) because it references `System.Net.Http` — GP 1 keeps the BCL HTTP
// dependency out of the Fable-safe tier. The AI connectors' kernel
// (`ProviderCall`) and the data-source connectors share this one adapter.

/// `IHttpTransport` over a BCL `HttpClient`. Construct with the provider's
/// shared client (its `BaseAddress` / instance `Timeout` already set) and
/// an optional per-call timeout (the caller's `RetryPolicy.Timeout`):
///
///   HttpClientTransport(sharedClient, retryPolicy.Timeout)
///
/// Each `Send` issues one request lifecycle — fresh `HttpRequestMessage`
/// (they cannot be reused after sending), per-call `CancellationTokenSource`,
/// buffered response read. Transport-level failures propagate as exceptions
/// for the caller to classify; the retry loop sits above the transport.
type HttpClientTransport(client: HttpClient, ?timeout: TimeSpan) =

    /// Build the per-call timeout CTS: a clamped `CancellationTokenSource` when a
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
    /// and a UTF-8 JSON `StringContent` body when present (the content-type
    /// rides with the body).
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
    /// interface. The caller owns reading + disposing the response.
    member _.SendForStreaming(request: HttpRequest, cancellationToken: CancellationToken) : Async<HttpResponseMessage> = async {
        let message = toRequestMessage request

        let! response =
            client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            |> Async.AwaitTask

        return response
    }

    /// The buffered send under the caller's `cancellationToken` alone (no
    /// per-call timeout of this transport's own): a caller that owns the
    /// timeout links it into the token. Status classification is the
    /// caller's job, so a non-2xx is returned as data; only genuine
    /// transport failures throw.
    member _.SendWith(request: HttpRequest, cancellationToken: CancellationToken) : Async<HttpResponse> = async {
        let message = toRequestMessage request
        let! response = client.SendAsync(message, cancellationToken) |> Async.AwaitTask
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        return {
            StatusCode = int response.StatusCode
            Headers = collectHeaders response
            Body = body
        }
    }

    interface IHttpTransport with
        /// Buffered request/response under this transport's per-call timeout.
        member this.Send(request: HttpRequest) : Async<HttpResponse> = async {
            use cts = newTimeoutCts ()
            return! this.SendWith(request, cts.Token)
        }