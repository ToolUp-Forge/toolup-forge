// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.HttpBodyBinding

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

// ─── Phase 763 — one body-binding seam for the server tier ────────
//
// The idiom this replaces was copied across the server tier:
//
//     let event =
//         try Some (JsonSerializer.Deserialize<'T>(body, options))
//         with _ -> None
//
// It reads as "a body that will not bind becomes `None`", and for a
// truncated or non-JSON body that is exactly what happens. For the
// request body `null` it is not. `null` is VALID JSON, so nothing
// throws: `Deserialize<'T>` returns a null reference typed as `'T`,
// the `Some` arm runs, and the first field access on it throws a
// `NullReferenceException` — OUTSIDE the `try`, where nothing catches
// it. The caller gets a 500 for what is a 400-class input, and any
// parse-drop counter on the `None` arm never fires, so the drop is
// invisible in exactly the case an operator would most want to see.
//
// The gap is entirely in the SHAPE of the idiom, not in any one
// handler: every site that dereferences the bound value inside its own
// `try` was already safe by accident, and every site that dereferences
// it afterwards was already wrong. That is why this is a shared helper
// rather than a null check per handler — a per-handler guard closes the
// instances that exist today and none of the ones written tomorrow.
//
// **What this helper does NOT do: choose a response shape.** The server
// tier has no single error envelope — the ad-analytics and consent
// endpoints answer a plain-text 400, `AdUnitConfigApi` answers through
// its own `writeError`, the SCIM routes answer a SCIM error document.
// Inventing a fourth here would change what existing callers see for a
// MALFORMED body, which is the behaviour a swept handler must preserve
// byte-for-byte (GP 11). So the helper owns the DECISION — the typed
// error, and `statusCodeFor`, which is 400 for every arm — and each
// handler keeps its own writer and its own existing message. What is
// stable across the tier is the status and the classification; the
// envelope stays the endpoint's.

/// Why a request body did not bind.
///
/// The two arms are the two distinct causes, and they are kept apart
/// because they say different things to whoever reads the log: a
/// malformed body is usually a wire-format skew or a hostile probe,
/// while a null body is usually a client that serialised an absent
/// value and posted it anyway. Both are the caller's error and both
/// answer 400 — see `statusCodeFor`.
type BodyBindError =
    /// The body carried no bindable value: it was absent, empty,
    /// whitespace, or the JSON literal `null`. All four produced a
    /// `null` reference (or a throw) from the deserialiser and none of
    /// them can yield a usable record.
    | BodyNull
    /// The body was present but did not parse or did not fit `'T`. The
    /// payload is the deserialiser's own message, for the log line —
    /// never for the response, which must not echo parser internals to
    /// an anonymous caller.
    | BodyMalformed of detail: string

module BodyBindError =

    /// A short stable token naming the cause, for structured log lines
    /// and metric-adjacent prose. Stable in the sense that matters: it
    /// is safe to grep for and safe to alert on, and it does not carry
    /// caller-controlled text.
    let reason (error: BodyBindError) : string =
        match error with
        | BodyNull -> "null-body"
        | BodyMalformed _ -> "malformed-body"

    /// A one-line human account of the cause, for a `Warn`. Carries the
    /// deserialiser's message on the malformed arm because that is what
    /// makes a client-side serialisation regression diagnosable; keep
    /// it out of the response body.
    let describe (error: BodyBindError) : string =
        match error with
        | BodyNull -> "the request body carried no bindable value (absent, empty, or the JSON literal null)"
        | BodyMalformed detail -> detail

/// The status every bind failure answers. One function rather than a
/// literal at each call site, so "a body that will not bind is a 400"
/// is a single assertable fact about the tier rather than a convention
/// each handler restates. Both arms are the caller's error: a null body
/// is no more the server's fault than a truncated one.
let statusCodeFor (_error: BodyBindError) : int = 400

/// Bind a JSON body that has already been read into a string.
///
/// The overload the capped readers use — `AdAnalyticsApiHandler` and
/// `TelemetryApiHandler` read the body themselves so they can refuse an
/// oversized one with a 413 BEFORE deserialising, and that gate must
/// keep running ahead of this.
///
/// Never returns a null `'T`: a reference type that deserialises to
/// null is `Error BodyNull`, which is the whole point of the helper.
/// A value-type `'T` cannot be null and its `null` body throws inside
/// the `try`, landing on `BodyMalformed` — also correct, since there is
/// no value to bind either way.
let tryBindJsonString<'T> (options: JsonSerializerOptions) (body: string) : Result<'T, BodyBindError> =
    if String.IsNullOrWhiteSpace body then
        // Not merely a shortcut. `Deserialize<'T>("")` throws, so an
        // empty body would otherwise be reported as MALFORMED — which
        // is the wrong story for a client that sent no body at all, and
        // the wrong one to put in front of an operator reading the log.
        Error BodyNull
    else
        try
            let bound = JsonSerializer.Deserialize<'T>(body, options)

            if isNull (box bound) then Error BodyNull else Ok bound
        with ex ->
            Error(BodyMalformed ex.Message)

/// Read the whole request body and bind it. The overload for handlers
/// with no size gate of their own, reading exactly as the sites it
/// replaces did (a `StreamReader` over `Request.Body` to the end), so
/// the read behaviour of a swept handler is unchanged.
///
/// A handler that must cap the body reads it itself and calls
/// `tryBindJsonString` — this overload deliberately offers no cap
/// rather than a default one, because a silent default would be a
/// second, differently-sized gate sitting behind the explicit ones.
let tryBindJson<'T> (options: JsonSerializerOptions) (ctx: HttpContext) : Task<Result<'T, BodyBindError>> = task {
    use reader = new StreamReader(ctx.Request.Body)
    let! body = reader.ReadToEndAsync()
    return tryBindJsonString<'T> options body
}