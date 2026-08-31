// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityScriptLoader

open Fable.Core
open Fable.Core.JsInterop

// ─── Singleton GIS bootstrap ─────────────────────────────────────────
//
// The Google Identity Services library (`gsi/client`) must be loaded
// exactly once per page-load: a second `<script>` re-registers the
// `google.accounts.id` namespace and re-arms One Tap, which shows the
// prompt twice. `ensureLoaded` is idempotent — the first call injects
// the tag and marks a window flag, later calls observe the marker and
// wait for the API instead of injecting again.
//
// Same shape as `AdScriptLoader` (the AdSense bootstrap), with one
// addition it does not need: GIS is only usable AFTER load, so this
// loader takes continuations rather than being fire-and-forget.
// `onReady` fires when `window.google.accounts.id` is reachable;
// `onError` fires when the script tag errors or the API never appears.
//
// A CSP that does not allow `accounts.google.com` is by far the most
// likely cause of the error path in a hardened deployment, and it is
// silent from the SDK's side — the browser blocks the fetch and the
// `<script>` simply errors. The advisory emitted here names the
// contributor that widens the policy, because the deployment cannot
// otherwise tell a blocked script from an offline user.

[<Literal>]
let private scriptUrl = "https://accounts.google.com/gsi/client"

/// Poll interval while waiting for a script another component already
/// injected, and the ceiling before the wait is called a failure.
[<Literal>]
let private pollIntervalMs = 50.0

[<Literal>]
let private pollCeilingMs = 10_000.0

[<Emit("typeof document !== 'undefined'")>]
let private hasDocument () : bool = jsNative

[<Emit("document.createElement('script')")>]
let private createScript () : obj = jsNative

[<Emit("document.head.appendChild($0)")>]
let private appendToHead (el: obj) : unit = jsNative

[<Emit("typeof window !== 'undefined' && Array.isArray(window.__toolupGisScriptLoaded) ? window.__toolupGisScriptLoaded.length > 0 : false")>]
let private isMarked () : bool = jsNative

[<Emit("(window.__toolupGisScriptLoaded = window.__toolupGisScriptLoaded || []).push(1)")>]
let private markLoaded () : unit = jsNative

[<Emit("setInterval($0, $1)")>]
let private jsSetInterval (callback: unit -> unit) (ms: float) : float = jsNative

[<Emit("clearInterval($0)")>]
let private jsClearInterval (handle: float) : unit = jsNative

[<Emit("console.error($0)")>]
let private consoleError (message: string) : unit = jsNative

/// The advisory a deployment sees when the GIS script cannot load.
/// Public so the wording is pinned by a test rather than restated in
/// a doc that drifts: the top cause is a Content-Security-Policy that
/// was never widened for Google's origins, and the remedy is a
/// one-line composition change, not a hand-edited header.
[<Literal>]
let ScriptLoadAdvisory =
    "ToolUp: the Google Identity Services library could not be loaded from https://accounts.google.com/gsi/client, so the branded sign-in button cannot render. \
     If this deployment enforces a Content-Security-Policy, the most likely cause is that the policy does not allow Google's origins: compose \
     `ServerApp.withCspContributor (GoogleIdentityServicesCspContributor())` (ToolUp.Platform.Server) so the aggregated header widens script-src / frame-src / connect-src / style-src for accounts.google.com. \
     Otherwise the browser is offline or a network policy blocks the host. The redirect flow (OidcPresets.google + OidcRegister.handler) needs none of this and remains available."

/// True when `window.google.accounts.id` is reachable — the only
/// honest readiness test, since the script tag's `load` event fires
/// before the namespace is guaranteed to be assigned in every browser.
[<Emit("typeof window !== 'undefined' && !!(window.google && window.google.accounts && window.google.accounts.id)")>]
let isApiReady () : bool = jsNative

/// Load the GIS library at most once per page, then invoke `onReady`.
/// Safe to call on every mount of every component that needs GIS:
///
///   - API already reachable      → `onReady` immediately, no injection.
///   - not yet injected           → inject, `onReady` on load.
///   - injected by someone else   → wait for the API, `onReady` when it lands.
///
/// `onError` fires on a script-tag error or when the API has not
/// appeared within the poll ceiling, after emitting `ScriptLoadAdvisory`
/// to the console. Outside a browser (SSR / test host) neither
/// continuation fires — there is no document to inject into.
let ensureLoaded (onReady: unit -> unit) (onError: unit -> unit) : unit =
    let fail () =
        consoleError ScriptLoadAdvisory
        onError ()

    // Wait for the API to appear, for the case where another component
    // injected the tag and owns its `load` handler.
    let awaitApi () =
        let mutable waitedMs = 0.0
        let mutable handle = 0.0

        handle <-
            jsSetInterval
                (fun () ->
                    if isApiReady () then
                        jsClearInterval handle
                        onReady ()
                    else
                        waitedMs <- waitedMs + pollIntervalMs

                        if waitedMs >= pollCeilingMs then
                            jsClearInterval handle
                            fail ())
                pollIntervalMs

    if not (hasDocument ()) then
        ()
    elif isApiReady () then
        onReady ()
    elif isMarked () then
        awaitApi ()
    else
        markLoaded ()
        let script = createScript ()
        script?async <- true
        script?defer <- true
        script?src <- scriptUrl

        script?onload <-
            (fun () ->
                // `load` can precede the namespace assignment; fall
                // through to the same wait rather than assuming.
                if isApiReady () then onReady () else awaitApi ())

        script?onerror <- (fun () -> fail ())
        appendToHead script