// SPDX-License-Identifier: MIT
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Client

open System
open System.Collections.Generic
open Fable.Core
open Fable.SimpleJson
open ToolUp.Remoting

/// Phase 911 — int64 / uint64 on the REFLECTIVE response path: exact, or a
/// named refusal, never a silently wrong value.
///
/// `Fable.SimpleJson`'s `Convert.fromJsonAs` reads an int64 JSON NUMBER
/// through `int`, so every value outside int32 wraps modulo 2^32 (2^53 + 1,
/// which `JSON.parse` has already rounded to 2^53, reads as 0; 5,000,000,000
/// as 705,032,704), and a negative number at uint64 wraps to near 2^64. Its
/// STRING arms are exact, and the server's writer always sends the string
/// form — a number token comes from any other writer.
///
/// So before the library sees the parsed tree, this pass walks it beside the
/// return type's `TypeInfo` and rewrites a number at an int64 / uint64
/// position into the digit string the library reads exactly. A number it
/// cannot rewrite exactly — fractional, negative at uint64, or beyond
/// ±(2^53 − 1), where `JSON.parse` may already have rounded it — is REFUSED
/// with the path to it, and the proxy raises it as a `DecodeError` on
/// `ProxyRequestException`, the same carrier the algebra's refusals use.
///
/// The walk mirrors the shapes the library reads: records by field name,
/// options, the list/array/seq/set/ResizeArray/HashSet element, tuples by
/// position, `Map`/`Dictionary` values (object form) and `[key, value]`
/// pairs (array form), and union cases in the `{"Case": payload}` and
/// `["Case", …]` forms. A shape it does not know is passed through untouched,
/// exactly as before. `holdsWide` decides once per proxy field (and once
/// per stream, for a streamed element type) whether the type has an int64 /
/// uint64 anywhere, so a type without one pays nothing per call or chunk.
/// It lives here, ahead of `Proxy`, because the streaming chunk decode below
/// is the second reflective read it serves.
module internal ReflectiveWideIntegers =

    /// 2^53 − 1: the largest magnitude at which every integer is a double.
    let private maxSafeInteger = 9007199254740991.0

    let private isSafeInteger (value: float) =
        System.Math.Floor value = value && abs value <= maxSafeInteger

    let private refusal (path: string list) (typeName: string) (value: float) : DecodeError =
        let found =
            if System.Math.Floor value <> value then
                sprintf "number %s, which is not an integer" (string value)
            elif value < 0.0 && typeName = "UInt64" then
                sprintf "number %s, which is negative" (string value)
            else
                sprintf "number %s, beyond the ±(2^53 − 1) a parsed JSON number carries exactly" (string value)

        DecodeError.at
            path
            (sprintf "%s as an exact integer (a digit string, or a number within ±(2^53 − 1))" typeName)
            found

    /// Whether `info` holds an int64 / uint64 anywhere the walk reaches.
    /// Records and unions already on the descent path are not re-entered,
    /// so a recursive type terminates.
    let holdsWide (info: TypeInfo) : bool =
        let rec go (seen: Set<string>) (info: TypeInfo) =
            match info with
            | TypeInfo.Long
            | TypeInfo.UInt64 -> true
            | TypeInfo.Option element
            | TypeInfo.List element
            | TypeInfo.Array element
            | TypeInfo.Seq element
            | TypeInfo.Set element
            | TypeInfo.ResizeArray element
            | TypeInfo.HashSet element -> go seen (element ())
            | TypeInfo.Tuple elements -> elements () |> Array.exists (go seen)
            | TypeInfo.Map types ->
                let key, value = types ()
                go seen key || go seen value
            | TypeInfo.Dictionary types ->
                let key, value, _ = types ()
                go seen key || go seen value
            | TypeInfo.Record fields ->
                let fields, recordType = fields ()

                not (seen.Contains recordType.FullName)
                && fields
                   |> Array.exists (fun field -> go (seen.Add recordType.FullName) field.FieldType)
            | TypeInfo.Union cases ->
                let cases, unionType = cases ()

                not (seen.Contains unionType.FullName)
                && cases
                   |> Array.exists (fun case -> case.CaseTypes |> Array.exists (go (seen.Add unionType.FullName)))
            | _ -> false

        go Set.empty info

    /// Map `f` over `items`, stopping at the first refusal.
    let private traverse (f: int -> 'A -> Result<'B, DecodeError>) (items: 'A list) : Result<'B list, DecodeError> =
        let rec loop index acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | item :: rest ->
                match f index item with
                | Ok mapped -> loop (index + 1) (mapped :: acc) rest
                | Error error -> Error error

        loop 0 [] items

    let private at (path: string list) (index: int) = path @ [ sprintf "[%d]" index ]

    /// The rewrite pass — see the module header.
    let rec widen (path: string list) (json: Json) (info: TypeInfo) : Result<Json, DecodeError> =
        match json, info with
        | JNumber value, TypeInfo.Long ->
            if isSafeInteger value then
                Ok(JString((int64 value).ToString()))
            else
                Error(refusal path "Int64" value)
        | JNumber value, TypeInfo.UInt64 ->
            if value >= 0.0 && isSafeInteger value then
                Ok(JString((uint64 value).ToString()))
            else
                Error(refusal path "UInt64" value)
        | JNull, TypeInfo.Option _ -> Ok json
        | _, TypeInfo.Option element -> widen path json (element ())
        | JArray items, TypeInfo.List element
        | JArray items, TypeInfo.Array element
        | JArray items, TypeInfo.Seq element
        | JArray items, TypeInfo.Set element
        | JArray items, TypeInfo.ResizeArray element
        | JArray items, TypeInfo.HashSet element ->
            let elementType = element ()

            items
            |> traverse (fun index item -> widen (at path index) item elementType)
            |> Result.map JArray
        | JArray items, TypeInfo.Tuple elements ->
            let elementTypes = elements ()

            if List.length items <> elementTypes.Length then
                Ok json
            else
                items
                |> traverse (fun index item -> widen (at path index) item elementTypes.[index])
                |> Result.map JArray
        | JObject members, TypeInfo.Record fields ->
            let fields, _ = fields ()

            members
            |> Map.toList
            |> traverse (fun _ (name, value) ->
                match fields |> Array.tryFind (fun field -> field.FieldName = name) with
                | Some field -> widen (path @ [ name ]) value field.FieldType |> Result.map (fun v -> name, v)
                | None -> Ok(name, value))
            |> Result.map (Map.ofList >> JObject)
        | JObject members, TypeInfo.Map types when not (members.ContainsKey "comparer" && members.ContainsKey "tree") ->
            let _, valueType = types ()
            widenValues path members valueType
        | JObject members, TypeInfo.Dictionary types ->
            let _, valueType, _ = types ()
            widenValues path members valueType
        | JArray pairs, TypeInfo.Map types ->
            let keyType, valueType = types ()
            widenPairs path pairs keyType valueType
        | JArray pairs, TypeInfo.Dictionary types ->
            let keyType, valueType, _ = types ()
            widenPairs path pairs keyType valueType
        | JObject members, TypeInfo.Union cases when members.Count = 1 ->
            let cases, _ = cases ()
            let caseName, payload = members |> Map.toList |> List.head

            match cases |> Array.tryFind (fun case -> case.CaseName = caseName) with
            | None -> Ok json
            | Some case ->
                let casePath = path @ [ caseName ]

                let widened =
                    match payload, case.CaseTypes with
                    | JArray _, [| single |] when Convert.arrayLike single || Convert.optional single ->
                        widen casePath payload single
                    | JArray values, caseTypes when List.length values = caseTypes.Length ->
                        values
                        |> traverse (fun index value -> widen (at casePath index) value caseTypes.[index])
                        |> Result.map JArray
                    | JArray _, _ -> Ok payload
                    | _, [| single |] -> widen casePath payload single
                    | _ -> Ok payload

                widened |> Result.map (fun p -> JObject(Map.ofList [ caseName, p ]))
        | JArray(JString caseName :: values), TypeInfo.Union cases ->
            let cases, _ = cases ()

            match cases |> Array.tryFind (fun case -> case.CaseName = caseName) with
            | Some case when List.length values = case.CaseTypes.Length ->
                let casePath = path @ [ caseName ]

                values
                |> traverse (fun index value -> widen (at casePath index) value case.CaseTypes.[index])
                |> Result.map (fun widened -> JArray(JString caseName :: widened))
            | _ -> Ok json
        | _ -> Ok json

    and widenValues path (members: Map<string, Json>) (valueType: TypeInfo) =
        members
        |> Map.toList
        |> traverse (fun _ (key, value) -> widen (path @ [ key ]) value valueType |> Result.map (fun v -> key, v))
        |> Result.map (Map.ofList >> JObject)

    and widenPairs path (pairs: Json list) (keyType: TypeInfo) (valueType: TypeInfo) =
        pairs
        |> traverse (fun index pair ->
            match pair with
            | JArray [ key; value ] ->
                let pairPath = at path index

                match widen (at pairPath 0) key keyType with
                | Error error -> Error error
                | Ok key ->
                    widen (at pairPath 1) value valueType
                    |> Result.map (fun value -> JArray [ key; value ])
            | other -> Ok other)
        |> Result.map JArray

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
        let holdsWideIntegers = ReflectiveWideIntegers.holdsWide elementType

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
                                // Phase 911 — an int64 / uint64 number in a chunk
                                // is read exactly or refused by name, as on the
                                // request/response path.
                                let decoded =
                                    try
                                        let parsed = SimpleJson.parseNative frame.Data

                                        let widened =
                                            if holdsWideIntegers then
                                                ReflectiveWideIntegers.widen [] parsed elementType
                                            else
                                                Ok parsed

                                        match widened with
                                        | Ok json -> Ok(Convert.fromJsonAs json elementType |> unbox<'T>)
                                        | Error error -> Error(DecodeError.render error, Some error)
                                    with ex ->
                                        Error(ex.Message, None)

                                match decoded with
                                | Ok value -> observer.OnChunk value
                                | Error(message, decodeError) ->
                                    fail (
                                        ProxyRequestException(
                                            {
                                                StatusCode = 200
                                                ResponseBody = frame.Data
                                            },
                                            sprintf "A chunk streamed from %s did not decode: %s" url message,
                                            frame.Data,
                                            decodeError
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