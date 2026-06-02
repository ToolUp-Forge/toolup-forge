// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.UIDecodeErrorHandler

open System
open System.IO
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open ToolUp.Platform

// ─── Phase 6g.E — client UI decoder-error observability sink ────
//
// `POST /api/ai/ui-decode-error`. A client-side companion's composed
// `ActionDecoder` fires this (fire-and-forget) when a module-author
// field decoder rejects or throws on an AI `set_field` payload —
// the failure that used to be dropped silently. The handler emits a
// `_platform.ui.decode_error` event to `IEventStore` so the operator
// has a queryable trail.
//
// Lives in ToolUp.AI.Server alongside the sibling client-resident
// endpoints `/api/ai/tool-result` and `/api/ai/fastpath/beacon` —
// client-side companions that contribute AI tool definitions only
// (no HTTP routes of their own) post here.
//
// `RawPayload` is bounded to `MaxRawPayloadChars` here as defence in
// depth — the client already truncates, but the server must not trust
// the body to be small, so an oversized AI value can never bloat the
// event store.

[<Literal>]
let private UIDecodeErrorSourceModule = "_platform.ui.decode_error"

[<Literal>]
let private UIDecodeErrorEventType = "UIDecodeError"

[<Literal>]
let MaxRawPayloadChars = 500

/// Pure, unit-testable bound on the raw payload. Null-safe; never
/// throws; returns at most `MaxRawPayloadChars` characters.
let truncatePayload (s: string) : string =
    if isNull s then ""
    elif s.Length <= MaxRawPayloadChars then s
    else s.Substring(0, MaxRawPayloadChars)

// Wire shape — must match Client/AIControl.fs `reportDecodeFailure`.
type UIDecodeErrorReport = {
    ModuleId: string
    FieldName: string
    RawPayload: string
    DecoderError: string
}

type private UIDecodeErrorEventPayload = {
    ModuleId: string
    FieldName: string
    RawPayload: string
    DecoderError: string
}

let private jsonOptions = FableConverters.create ()

let private resolveScope (ctx: HttpContext) : StorageScope =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s
    | _ ->
        let fallback =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        {
            ScopeId = fallback
            Container = $"user-{fallback}"
            Persist = true
        }

let uiDecodeErrorHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()
            let report = JsonSerializer.Deserialize<UIDecodeErrorReport>(body, jsonOptions)

            if isNull (box report) then
                ctx.Response.StatusCode <- 400
                return! next ctx
            else
                let scope = resolveScope ctx

                match ctx.RequestServices.GetService(typeof<IEventStore>) with
                | :? IEventStore as store ->
                    let payload: UIDecodeErrorEventPayload = {
                        ModuleId = report.ModuleId
                        FieldName = report.FieldName
                        RawPayload = truncatePayload report.RawPayload
                        DecoderError = report.DecoderError
                    }

                    let evt: ModuleEvent = {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime.UtcNow
                        ScopeId = scope.ScopeId
                        SourceModule = UIDecodeErrorSourceModule
                        EventType = UIDecodeErrorEventType
                        Payload = JsonSerializer.Serialize(payload, jsonOptions)
                    }

                    try
                        do! store.Write evt
                    with _ ->
                        ()
                | _ -> ()

                ctx.Response.StatusCode <- 202
                return! next ctx
        with _ ->
            ctx.Response.StatusCode <- 400
            return! next ctx
    }