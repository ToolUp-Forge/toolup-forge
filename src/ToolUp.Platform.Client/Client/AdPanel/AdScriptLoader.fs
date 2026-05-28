// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AdPanel.AdScriptLoader

open Fable.Core
open Fable.Core.JsInterop

// ─── Phase 60 — singleton AdSense bootstrap ───────────────────────
//
// The AdSense bundle (`adsbygoogle.js`) must be loaded exactly once
// per page-load. Loading it twice doubles every `push({})` call.
// `ensureLoaded` is idempotent — first call injects the `<script>`,
// subsequent calls are no-ops. Triggered automatically by the first
// `AdSlot` mount; consumers wanting eager-load call it explicitly.

[<Emit("typeof document !== 'undefined'")>]
let private hasDocument () : bool = jsNative

[<Emit("document.createElement('script')")>]
let private createScript () : obj = jsNative

[<Emit("document.head.appendChild($0)")>]
let private appendToHead (el: obj) : unit = jsNative

[<Emit("typeof window !== 'undefined' && Array.isArray(window.__toolupAdScriptLoaded) ? window.__toolupAdScriptLoaded.length > 0 : false")>]
let private isLoaded () : bool = jsNative

[<Emit("(window.__toolupAdScriptLoaded = window.__toolupAdScriptLoaded || []).push(1)")>]
let private markLoaded () : unit = jsNative

let ensureLoaded (adClientId: string) =
    if hasDocument () && not (isLoaded ()) then
        markLoaded ()
        let script = createScript ()
        script?async <- true
        script?crossOrigin <- "anonymous"
        script?src <- $"https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client={adClientId}"
        appendToHead script