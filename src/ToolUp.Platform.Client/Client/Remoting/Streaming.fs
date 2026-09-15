// SPDX-License-Identifier: MIT
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Client

open System
open System.Collections.Generic
open Fable.Core
open Fable.SimpleJson
open ToolUp.Remoting

// ─── Phase 69c.D — the Fable consumer of a streaming method ────────────────
//
// A server API record field shaped `'arg -> IAsyncEnumerable<'T>` is served
// as server-sent events (`Server/Remoting/Streaming.fs`: `event: chunk` per
// element with `id: <correlation-id>-<chunk-index>`, then `event: complete`
// or `event: error`). `IAsyncEnumerable<'T>` has no runtime representation
// in Fable — there is nothing to `MoveNextAsync` — so the proxy builds a
// COLD stream for such a field instead: a `RemoteStream<'T>` that sends
// nothing until a consumer subscribes, and then POSTs the same JSON-array
// body a request/response call would, reads the response body
// incrementally, decodes the SSE frames and deserialises each `chunk` with
// the same `Fable.SimpleJson` converter the request/response path uses.
//
// The consumer shape is the one this client tier already uses for every
// push stream (`NotificationClient`, the AI `SSEClient`): a callback per
// element plus a disposable that closes the connection — not a new
// abstraction, and it composes with `EffectHandle` lifetimes unchanged.
// `EventSource` itself is NOT used: it can only GET, and a streaming method
// takes its argument in a POST body.
//
// Type erasure, and where it sits: the proxy returns the `RemoteStream<'T>`
// through a field statically typed `IAsyncEnumerable<'T>` (a `box`), and
// `RemoteStream.subscribe` takes that value back (the matching `unbox`).
// Symmetric, same-module, and the only place the Fable remoting tier erases
// a type — it is listed with the sanctioned boundaries in CLAUDE.md. A
// consumer never sees it: `RemoteStream.subscribe (api.Tail req) observer`
// is the whole surface.

/// One decoded server-sent event — the client-side mirror of the server's
/// `SseFrame`. `Event` is the `event:` field (`"message"` when absent, per
/// the SSE spec); `Id` is the `id:` field when present; `Data` is every
/// `data:` line joined with a line feed (the inverse of the spec's multiline
/// framing).
type SseFrame = {
    /// The `event:` field; `"message"` when the event carried none.
    Event: string
    /// The `id:` field when present (`<correlation-id>-<chunk-index>` on a dispatcher chunk).
    Id: string option
    /// Every `data:` line of the event, joined with a line feed.
    Data: string
}

/// The event-stream decoder (https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation).
/// Pure and total over text, so it is testable under Node without a
/// transport: `splitComplete` is the buffering half (a response body arrives
/// in chunks that need not align with event boundaries), `parse` the
/// decoding half.
[<RequireQualifiedAccess>]
module SseFrame =

    let private normalise (text: string) : string =
        text.Replace("\r\n", "\n").Replace("\r", "\n")

    /// Split a receive buffer at its last complete-event boundary: the
    /// first element holds every complete event (each ending in the blank
    /// line that delimits it), the second is the trailing partial event to
    /// keep for the next chunk. Line terminators are normalised to `\n`.
    let splitComplete (buffer: string) : string * string =
        let text = normalise buffer
        let last = text.LastIndexOf "\n\n"

        if last < 0 then
            "", text
        else
            text.Substring(0, last + 2), text.Substring(last + 2)

    /// Decode every complete event in `text`, in order. A trailing partial
    /// event is ignored (hand it to `splitComplete` first); an event with no
    /// `data:` line is dropped, as the spec requires; `:` comment lines
    /// (keepalives) are ignored.
    let parse (text: string) : SseFrame list =
        let lines = (normalise text).Split('\n')

        let fieldValue (line: string) (name: string) : string option =
            if line = name then
                Some ""
            elif line.StartsWith(name + ":") then
                let rest = line.Substring(name.Length + 1)
                Some(if rest.StartsWith " " then rest.Substring 1 else rest)
            else
                None

        let mutable eventName: string option = None
        let mutable id: string option = None
        let mutable data: string list = []
        let mutable acc: SseFrame list = []

        let flush () =
            match data with
            | [] -> ()
            | _ ->
                acc <-
                    {
                        Event = defaultArg eventName "message"
                        Id = id
                        Data = data |> List.rev |> String.concat "\n"
                    }
                    :: acc

            eventName <- None
            id <- None
            data <- []

        for line in lines do
            if line = "" then
                flush ()
            elif line.StartsWith ":" then
                ()
            else
                match fieldValue line "data" with
                | Some v -> data <- v :: data
                | None ->
                    match fieldValue line "event" with
                    | Some v -> eventName <- Some v
                    | None ->
                        match fieldValue line "id" with
                        | Some v -> id <- Some v
                        | None -> ()

        List.rev acc

/// How a subscribed streaming call reports to its consumer. Exactly one of
/// `OnComplete` / `OnError` is called, after the last `OnChunk`, unless the
/// subscription is disposed first (then neither is).
type StreamObserver<'T> = {
    /// One deserialised element per `event: chunk` frame, in order.
    OnChunk: 'T -> unit
    /// The server's `event: complete` — the sequence ended normally.
    OnComplete: unit -> unit
    /// The server's `event: error` (a `ProxyRequestException` carrying the
    /// message), a non-200 response, a chunk that did not deserialise, or a
    /// transport failure.
    OnError: exn -> unit
}

/// A cold streaming call: nothing is sent until `Start`, and each `Start`
/// is an independent request. Built by the proxy for every
/// `'arg -> IAsyncEnumerable<'T>` field; consumers reach it through
/// `RemoteStream.subscribe`.
type RemoteStream<'T> = {
    /// Send the request and deliver the stream to `observer`; dispose the
    /// result to abort the connection.
    Start: StreamObserver<'T> -> IDisposable
}

[<RequireQualifiedAccess>]
module RemoteStream =

    /// `fetch` + incremental body read. `onText` receives each decoded chunk
    /// of the response body as it arrives; `onEnd` fires when the body is
    /// exhausted; `onFail` receives `(status, bodyText)` for a non-200
    /// response, or `(0, message)` for a transport failure. Returns the
    /// abort function. Runs through `window.fetch`, so the SDK's request
    /// guard (`CsrfClient.installRequestGuard`) attaches the live identity
    /// headers exactly as it does for the XHR path.
    // The IIFE's locals carry a `$sse` prefix deliberately: Fable substitutes
    // each `$n` with the caller's argument EXPRESSION, and a local named
    // `headers` would shadow the caller's `headers` inside it.
    [<Emit("""(() => {
        const $sseCtl = new AbortController();
        const $sseDec = new TextDecoder();
        const $sseHdr = {};
        for (const $ssePair of $1) $sseHdr[$ssePair[0]] = $ssePair[1];
        fetch($0, {
            method: 'POST',
            headers: $sseHdr,
            body: $2,
            credentials: $3 ? 'include' : 'same-origin',
            signal: $sseCtl.signal
        }).then(async ($sseResp) => {
            if ($sseResp.status !== 200) {
                const $sseText = await $sseResp.text();
                $6($sseResp.status, $sseText);
                return;
            }
            const $sseReader = $sseResp.body.getReader();
            while (true) {
                const $sseStep = await $sseReader.read();
                if ($sseStep.done) break;
                $4($sseDec.decode($sseStep.value, { stream: true }));
            }
            $5();
        }).catch(($sseErr) => {
            if (!$sseCtl.signal.aborted) $6(0, String($sseErr && $sseErr.message ? $sseErr.message : $sseErr));
        });
        return () => $sseCtl.abort();
    })()""")>]
    let private startFetch
        (url: string)
        (headers: (string * string)[])
        (body: string)
        (withCredentials: bool)
        (onText: string -> unit)
        (onEnd: unit -> unit)
        (onFail: Action<int, string>)
        : (unit -> unit) =
        jsNative

    /// Read the `message` of a server `event: error` payload
    /// (`{"message":"…"}`); the raw payload when it is not that shape.
    let private errorMessage (data: string) : string =
        try
            match SimpleJson.parseNative data with
            | JObject fields ->
                match Map.tryFind "message" fields with
                | Some(JString m) -> m
                | _ -> data
            | _ -> data
        with _ ->
            data

    /// Build the cold stream for one streaming method. `elementType` is the
    /// `'T` of the field's `IAsyncEnumerable<'T>`, resolved at proxy-build
    /// time; `decodeErrorOf` reads a Phase 783 decode-refusal envelope out
    /// of a non-200 body (the proxy's own reader, passed in so this module
    /// stays independent of it).
    let create
        (url: string)
        (headers: (string * string) list)
        (body: string)
        (withCredentials: bool)
        (decodeErrorOf: string -> DecodeError option)
        (elementType: TypeInfo)
        : RemoteStream<'T> =
        {
            Start =
                fun observer ->
                    let mutable buffer = ""
                    let mutable finished = false
                    let mutable abort: unit -> unit = ignore

                    let finish () =
                        finished <- true
                        abort ()

                    let fail (exn: exn) =
                        if not finished then
                            finish ()
                            observer.OnError exn

                    let handleFrame (frame: SseFrame) =
                        if not finished then
                            match frame.Event with
                            | "chunk" ->
                                let decoded =
                                    try
                                        Ok(
                                            Convert.fromJsonAs (SimpleJson.parseNative frame.Data) elementType
                                            |> unbox<'T>
                                        )
                                    with ex ->
                                        Error ex

                                match decoded with
                                | Ok value -> observer.OnChunk value
                                | Error ex ->
                                    fail (
                                        ProxyRequestException(
                                            {
                                                StatusCode = 200
                                                ResponseBody = frame.Data
                                            },
                                            sprintf "A chunk streamed from %s did not decode: %s" url ex.Message,
                                            frame.Data
                                        )
                                    )
                            | "complete" ->
                                finish ()
                                observer.OnComplete()
                            | "error" ->
                                fail (
                                    ProxyRequestException(
                                        {
                                            StatusCode = 200
                                            ResponseBody = frame.Data
                                        },
                                        sprintf "Error streamed from %s: %s" url (errorMessage frame.Data),
                                        frame.Data
                                    )
                                )
                            | _ -> ()

                    let onText (text: string) =
                        if not finished then
                            buffer <- buffer + text
                            let complete, rest = SseFrame.splitComplete buffer
                            buffer <- rest

                            for frame in SseFrame.parse complete do
                                handleFrame frame

                    let onEnd () =
                        // The body ended with no terminal frame: the server went
                        // away mid-stream. Never silently complete.
                        fail (
                            ProxyRequestException(
                                {
                                    StatusCode = 200
                                    ResponseBody = buffer
                                },
                                sprintf "The stream from %s ended without a complete or error frame" url,
                                buffer
                            )
                        )

                    let onFail =
                        Action<int, string>(fun status text ->
                            let response = {
                                StatusCode = status
                                ResponseBody = text
                            }

                            let message =
                                if status = 0 then
                                    sprintf "Network error while streaming from %s: %s" url text
                                else
                                    sprintf "Http error (%d) while streaming from %s" status url

                            fail (ProxyRequestException(response, message, text, decodeErrorOf text)))

                    abort <- startFetch url (List.toArray headers) body withCredentials onText onEnd onFail

                    { new IDisposable with
                        member _.Dispose() =
                            if not finished then
                                finished <- true
                                abort ()
                    }
        }

    /// Subscribe to a streaming method's result. `stream` is what the proxy
    /// returned for an `'arg -> IAsyncEnumerable<'T>` field — the value is a
    /// cold `RemoteStream<'T>` behind the interface type (see the file
    /// header) — and the call sends the request. Dispose the result to
    /// abort. Every `subscribe` sends a fresh request.
    let subscribe (stream: IAsyncEnumerable<'T>) (observer: StreamObserver<'T>) : IDisposable =
        if isNull (box stream) then
            invalidArg "stream" "RemoteStream.subscribe: the stream is null — call the proxy's streaming method first"

        let remote = unbox<RemoteStream<'T>> stream

        if isNull (box remote.Start) then
            invalidArg
                "stream"
                "RemoteStream.subscribe: not a proxy-built stream — only the value returned by a streaming method on a Remoting proxy can be subscribed"

        remote.Start observer