// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.AuthTracer

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.AuthProviders.Oidc.OidcTypes

// ─── Auth tracer ─────────────────────────────────────────────────────
//
// Structured emission for the OIDC sign-in / refresh / classify flow.
// Off by default — `nullTracer` is the configured default and adds
// zero runtime cost. Turning the tracer on (build-time Vite define
// `__TOOLUP_AUTH_TRACE__` truthy) routes every named edge of the flow
// to the browser console in a single grep-friendly format:
//
//     [auth] <corr> <stage>[ <detail>][ err=<kind> sub=<sub-cause>]
//
// Goal: when something breaks, the operator can grep `[auth]` in the
// console (or in the streamed log if a remote console wrapper is
// attached) and walk the timeline of one sign-in attempt without
// reaching for the React devtools or guessing from screenshots.
//
// Correlation ids are issued at `beginSignIn` and stashed alongside
// the PKCE verifier; subsequent edges read the corr-id from
// `OidcTokenStore` so the tracer can stitch one flow's lines
// together. Edges that fire outside any active sign-in (cold-start
// classify, post-reboot refresh, sign-out) emit with `CorrelationId =
// None` and read as `-`.
//
// Cross-side stitching (the server's matching `[auth]` lines being
// correlated to the same id via an `X-ToolUp-Auth-Corr` request
// header) is a coordinated change that rides the larger auth refactor
// shipping the unified config record + coherence validator. This
// module ships the client-side primitive; the header-threading
// counterpart lands then.

/// A single transition the auth state machine has just taken.
/// Constructed at the emit site by the orchestration code, handed to
/// the configured tracer. Implementations may format, filter, or
/// drop entries — no contract beyond fire-and-forget.
type AuthTransition = {
    /// Correlation id for the originating sign-in flow. `None` when
    /// the transition fires outside any active sign-in flow.
    CorrelationId: string option
    /// Short stable label for the edge — e.g. `"begin-sign-in"`,
    /// `"token-exchange-start"`, `"token-exchange-ok"`,
    /// `"validate-id-token-ok"`, `"classify-stored:fresh-jwt"`,
    /// `"refresh-start"`, `"refresh-rejected"`, `"sign-out"`. Stable
    /// so downstream consumers can filter / tag metrics by stage.
    Stage: string
    /// Optional structured detail string for the transition — short,
    /// human-readable, not a full diagnostic. (Diagnostics for
    /// failures go in `Outcome`.)
    Detail: string option
    /// Optional diagnostic when the transition was a failure. `None`
    /// for happy-path events.
    Outcome: AuthDiagnostic option
}

/// Tracer interface. `Emit` is fire-and-forget; implementations MUST
/// NOT throw, block, or perform I/O that can fail. Use `nullTracer`
/// for the off path and `consoleTracer` for the developer-console
/// path. Production consumers may wrap with their own tracer that
/// fans out to a remote log channel.
type AuthTracer =
    abstract Emit: AuthTransition -> unit

/// Tracer that discards every event. Default; zero runtime cost.
let nullTracer: AuthTracer =
    { new AuthTracer with
        member _.Emit _ = ()
    }

/// Format an `AuthTransition` as a single grep-friendly line. Exposed
/// (and pure) so tests + custom tracer wrappers can reuse it without
/// reaching for the browser console shim.
let formatTransition (t: AuthTransition) : string =
    let corr = t.CorrelationId |> Option.defaultValue "-"

    let detail = t.Detail |> Option.map (sprintf " %s") |> Option.defaultValue ""

    let outcome =
        t.Outcome
        |> Option.map (fun d ->
            let cause = d.SubCause |> Option.defaultValue ""
            sprintf " err=%s sub=%s" d.Kind cause)
        |> Option.defaultValue ""

    sprintf "[auth] %s %s%s%s" corr t.Stage detail outcome

/// Tracer that formats every transition and hands the resulting line
/// to the supplied writer. Production code uses `consoleTracer`
/// (writer = `console.log`); tests pass a capturing writer to assert
/// the rendered surface.
let writingTracer (write: string -> unit) : AuthTracer =
    { new AuthTracer with
        member _.Emit t = write (formatTransition t)
    }

[<Emit("console.log($0)")>]
let private consoleLog (msg: string) : unit = jsNative

/// Tracer that emits one formatted line per transition to the
/// browser console via `console.log`. Cheap to enable, no extra
/// infrastructure required.
let consoleTracer: AuthTracer = writingTracer consoleLog

/// Pure selector — exposed for tests. Production code uses `fromEnv`
/// which derives the flag from a Vite-injected build-time constant.
let select (isEnabled: bool) : AuthTracer =
    if isEnabled then consoleTracer else nullTracer

/// Pure selector that lets the caller supply the writer when enabled.
/// Production code uses `select` (writer = `console.log`); tests use
/// this form with a capturing writer so the on-path is reachable from
/// .NET-side Expecto (the production `consoleTracer` calls `[<Emit>]`
/// `console.log`, which throws in .NET).
let selectWith (isEnabled: bool) (write: string -> unit) : AuthTracer =
    if isEnabled then writingTracer write else nullTracer

[<Emit("typeof __TOOLUP_AUTH_TRACE__ !== 'undefined' ? __TOOLUP_AUTH_TRACE__ : null")>]
let private buildTimeFlag () : obj = jsNative

let private isTruthy (v: obj) : bool =
    if isNullOrUndefined v then
        false
    else
        try
            unbox<bool> v
        with _ ->
            try
                let s = unbox<string> v
                s = "1" || s.ToLowerInvariant() = "true"
            with _ ->
                false

/// Resolve the tracer from the build-time `__TOOLUP_AUTH_TRACE__`
/// Vite define. Truthy → `consoleTracer`; otherwise → `nullTracer`.
/// Build-time gating means an off deployment pays zero runtime cost
/// (the dead branch is dead-code-eliminated by Vite).
let fromEnv () : AuthTracer = select (isTruthy (buildTimeFlag ()))

// ─── Mutable tracer slot for the current page ────────────────────────
//
// Documented client-side mutable: a tracer reference is not Elmish
// model state; orchestration code (`beginSignIn`, `exchangeCode...`,
// `classifyStoredToken`, …) reaches for it ambiently rather than
// threading it through every signature. At most one tracer is
// installed per page — the consumer's composition root calls
// `install` once at startup. Defaults to `nullTracer` so the surface
// works correctly when no consumer has configured one.

let mutable private current: AuthTracer = nullTracer

/// Install the tracer used for ambient emission. Idempotent in the
/// sense that calling twice simply replaces the previous tracer;
/// callers should treat this as a one-shot composition-root knob.
let install (tracer: AuthTracer) : unit = current <- tracer

/// Read the currently-installed tracer. Orchestration code uses this
/// rather than threading a tracer parameter through every async
/// helper signature.
let active () : AuthTracer = current

/// Emit a transition to the currently-installed tracer. Thin helper
/// — the orchestration call sites prefer this over `active().Emit`.
let emit (transition: AuthTransition) : unit = current.Emit transition

/// Convenience for the common "success edge" shape. Saves a
/// per-call-site record-construction.
let emitOk (corrId: string option) (stage: string) (detail: string option) : unit =
    emit {
        CorrelationId = corrId
        Stage = stage
        Detail = detail
        Outcome = None
    }

/// Convenience for the failure-edge shape.
let emitErr (corrId: string option) (stage: string) (diagnostic: AuthDiagnostic) : unit =
    emit {
        CorrelationId = corrId
        Stage = stage
        Detail = None
        Outcome = Some diagnostic
    }