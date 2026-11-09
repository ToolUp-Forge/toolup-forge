// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

/// One event on a streaming method's wire.
///
/// A streaming method answers with server-sent events: one `event: chunk` per
/// element, stamped `id: <correlation-id>-<chunk-index>` by the dispatcher,
/// then `event: complete` or `event: error`. Payloads are JSON TEXT: this
/// grammar is lexical and reads no JSON, so the server keeps its own encoder
/// for an element or an error message and the client its own reader.
[<RequireQualifiedAccess>]
type StreamFrame =
    /// `event: chunk`: one element's JSON text, with the `id:` field when stamped.
    | Chunk of id: string option * json: string
    /// `event: complete`: the sequence ended normally. Framed with the payload `{}`.
    | Complete
    /// `event: error`: the server's error payload, JSON text (`{"message":"…"}`).
    | Error of json: string
    /// Any other event, kept so the decoder stays total over the grammar: the
    /// notification channel's named events, and the default `message` events
    /// (no `event:` field) of the per-user AI channel.
    | Other of event: string * id: string option * data: string

/// The event-stream grammar
/// (https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation),
/// written once for both hosts. Pure, total over text, FSharp.Core only.
[<RequireQualifiedAccess>]
module Sse =

    let private normalise (text: string) : string =
        text.Replace("\r\n", "\n").Replace("\r", "\n")

    /// Every line of a value is its own `data:` line, so a payload carrying a
    /// line feed cannot end its event early. CRLF and CR are normalised to LF
    /// first, so the `\n\n` delimiter is never mis-read.
    let private dataLines (payload: string) : string =
        (normalise payload).Split('\n')
        |> Array.map (fun line -> "data: " + line)
        |> String.concat "\n"

    let private event (name: string option) (id: string option) (data: string) : string =
        let eventLine =
            match name with
            | Some name -> "event: " + name + "\n"
            | None -> ""

        // An id never carries a line terminator: the dispatcher generates a
        // GUID or takes a header value, and a header value cannot carry one.
        let idLine =
            match id with
            | Some id -> "id: " + id + "\n"
            | None -> ""

        eventLine + idLine + dataLines data + "\n\n"

    /// The frame's bytes as text, ending in the blank line that delimits it.
    /// An `Other` event named `message` is written with no `event:` field,
    /// which is the spec's default.
    let encode (frame: StreamFrame) : string =
        match frame with
        | StreamFrame.Chunk(id, json) -> event (Some "chunk") id json
        | StreamFrame.Complete -> event (Some "complete") None "{}"
        | StreamFrame.Error json -> event (Some "error") None json
        | StreamFrame.Other(name, id, data) -> event (if name = "message" then None else Some name) id data

    /// Split a receive buffer at its last complete-event boundary: the first
    /// element holds every complete event (each ending in its blank line), the
    /// second the trailing partial event to keep for the next read. Line
    /// terminators are normalised to `\n`.
    let splitComplete (buffer: string) : string * string =
        let text = normalise buffer
        let last = text.LastIndexOf "\n\n"

        if last < 0 then
            "", text
        else
            text.Substring(0, last + 2), text.Substring(last + 2)

    /// Decode every complete event in `text`, in order. A trailing partial
    /// event is ignored (hand the buffer to `splitComplete` first); an event
    /// with no `data:` line is dropped, as the spec requires; `:` comment lines
    /// (keepalives) and unknown fields are ignored. `data:` lines join with a
    /// line feed, the inverse of `encode`.
    let parse (text: string) : StreamFrame list =
        let fieldValue (line: string) (name: string) : string option =
            // `field:value`; one leading space after the colon is stripped,
            // and a bare `field` is the empty value.
            if line = name then
                Some ""
            elif line.StartsWith(name + ":") then
                let rest = line.Substring(name.Length + 1)
                Some(if rest.StartsWith " " then rest.Substring 1 else rest)
            else
                None

        let frameOf (name: string) (id: string option) (data: string) : StreamFrame =
            match name with
            | "chunk" -> StreamFrame.Chunk(id, data)
            | "complete" -> StreamFrame.Complete
            | "error" -> StreamFrame.Error data
            | other -> StreamFrame.Other(other, id, data)

        let mutable eventName: string option = None
        let mutable id: string option = None
        let mutable data: string list = []
        let mutable acc: StreamFrame list = []

        for line in (normalise text).Split('\n') do
            if line = "" then
                match data with
                | [] -> ()
                | _ ->
                    acc <-
                        frameOf (defaultArg eventName "message") id (data |> List.rev |> String.concat "\n")
                        :: acc

                eventName <- None
                id <- None
                data <- []
            elif not (line.StartsWith ":") then
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