// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AuthDiagnostics

open Fable.Core
open Fable.Core.JsInterop

// ─── Auth-observability A4 — generic client-side auth diagnostics ────
//
// The OIDC provider's `AuthTracer` shipped first (Phase 3) and was
// scoped to the OIDC sign-in / refresh / classify flow. This module
// lifts the tracer shape into `ToolUp.Platform.Client` so every
// client-side auth-relevant code path — CsrfClient warnings,
// UserSession bridge-refresh outcomes, AuthUIProvider handler-lookup
// failures — can emit through the same seam.
//
// Wire shape and semantics deliberately mirror the OIDC `AuthTracer`
// so a thin compatibility shim can land in the OIDC provider that
// forwards to this module (follow-up; not in this commit). The
// diagnostic record is generic — no OIDC-specific fields — so non-OIDC
// auth providers (Clerk, future) can adopt without inheriting OIDC
// vocabulary.
//
// Off by default — `nullTracer` is the configured default and adds
// zero runtime cost. Turning the tracer on (build-time Vite define
// `__TOOLUP_AUTH_TRACE__` truthy) routes every named edge through the
// browser console in a single grep-friendly format:
//
//     [auth] <corr> <stage>[ <detail>][ err=<kind> sub=<sub-cause>]
//
// A `forwarding` tracer lets deployments wire the timeline to a
// server-side activity sink via a Cmd.OfRemoting call, so a Datadog /
// OpenTelemetry-instrumented deployment can capture client auth
// traces without devtools access.

/// Optional diagnostic payload for a failure-edge transition.
/// Generic across providers — `Kind` is a short stable tag (e.g.
/// `"NonceMismatch"`, `"BridgeInstallFailed"`, `"CsrfTokenMissing"`);
/// `SubCause` is a free-form sub-classification when the `Kind` alone
/// doesn't disambiguate (e.g. the underlying exception type).
type AuthDiagnostic = {
    /// Short stable classification — drives metric tags and
    /// downstream filters.
    Kind: string
    /// Optional sub-classification (the underlying exception type
    /// or a more specific failure mode).
    SubCause: string option
}

/// A single transition the client-side auth surface has just taken.
/// Constructed at the emit site, handed to the configured tracer.
/// Implementations may format, filter, or drop entries — no contract
/// beyond fire-and-forget.
type AuthTransition = {
    /// Correlation id for the originating auth flow when one is
    /// active (sign-in, refresh, sign-out). `None` for one-off
    /// edges with no enclosing flow.
    CorrelationId: string option
    /// Short stable label for the edge — e.g. `"begin-sign-in"`,
    /// `"csrf-prefetch-ok"`, `"bridge-install-failed"`,
    /// `"authui-handler-missing"`. Stable so consumers can filter /
    /// tag metrics by stage.
    Stage: string
    /// Optional structured detail string for the transition —
    /// short, human-readable, not a full diagnostic. (Diagnostics
    /// for failures go in `Outcome`.)
    Detail: string option
    /// Optional diagnostic when the transition was a failure.
    /// `None` for happy-path events.
    Outcome: AuthDiagnostic option
}

/// Tracer interface. `Emit` is fire-and-forget; implementations MUST
/// NOT throw, block, or perform I/O that can fail. Use `nullTracer`
/// for the off path, `consoleTracer` for the developer-console path,
/// `forwardingTracer` to route to a server-side sink or a remote log
/// channel.
type IAuthTracer =
    abstract Emit: AuthTransition -> unit

/// Tracer that discards every event. Default; zero runtime cost.
let nullTracer: IAuthTracer =
    { new IAuthTracer with
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
let writingTracer (write: string -> unit) : IAuthTracer =
    { new IAuthTracer with
        member _.Emit t = write (formatTransition t)
    }

[<Emit("console.log($0)")>]
let private consoleLog (msg: string) : unit = jsNative

/// Tracer that emits one formatted line per transition to the
/// browser console via `console.log`.
let consoleTracer: IAuthTracer = writingTracer consoleLog

/// Tracer that forwards each transition to a consumer-supplied
/// handler (typically a `Cmd.OfRemoting.call` to a server-side
/// activity sink, or a wrapper that fans out to multiple
/// destinations). Use when a deployment runs Datadog / OpenTelemetry
/// and wants client-side auth traces in the same store as server
/// traces — without requiring devtools access.
let forwardingTracer (handler: AuthTransition -> unit) : IAuthTracer =
    { new IAuthTracer with
        member _.Emit t =
            try
                handler t
            with _ ->
                // Defensive — a throwing forwarder must not crash
                // the auth code path. Drop silently; the operator
                // sees the absence rather than a cascade.
                ()
    }

/// Pure selector — exposed for tests. Production code uses `fromEnv`
/// which derives the flag from a Vite-injected build-time constant.
let select (isEnabled: bool) : IAuthTracer =
    if isEnabled then consoleTracer else nullTracer

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
let fromEnv () : IAuthTracer = select (isTruthy (buildTimeFlag ()))

// ─── Mutable tracer slot for the current page ────────────────────────
//
// Documented client-side mutable: a tracer reference is not Elmish
// model state; client-side auth code reaches for it ambiently rather
// than threading it through every signature. At most one tracer is
// installed per page — the consumer's composition root calls `install`
// once at startup. Defaults to `nullTracer` so the surface works
// correctly when no consumer has configured one.

let mutable private current: IAuthTracer = nullTracer

/// Install the tracer used for ambient emission. Idempotent in the
/// sense that calling twice simply replaces the previous tracer;
/// callers should treat this as a one-shot composition-root knob.
let install (tracer: IAuthTracer) : unit = current <- tracer

/// Read the currently-installed tracer.
let active () : IAuthTracer = current

/// Emit a transition to the currently-installed tracer. Thin helper —
/// call sites prefer this over `active().Emit`.
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

/// Convenience for the common "exception-as-failure" shape — wraps an
/// `exn` into the diagnostic carrier so call sites stay terse.
let emitException (corrId: string option) (stage: string) (kind: string) (ex: exn) : unit =
    let subCause =
        if isNull ex.Message then
            None
        elif ex.Message.Length > 200 then
            Some(ex.Message.Substring(0, 200))
        else
            Some ex.Message

    emitErr corrId stage { Kind = kind; SubCause = subCause }