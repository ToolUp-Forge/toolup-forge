// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Bootstrap.MetadataHook

open ToolUp.Platform
open Fable.Core
open Fable.Core.JsInterop

// ─── Phase 57 — per-route metadata updater ────────────────────────
//
// On SPA navigation between prerendered routes, the page metadata
// (`<title>`, `<meta name="description">`, OG tags, optional
// JSON-LD) needs to swap to the new route's `PrerenderMeta`.
// Without this, the user navigates from `/individual` to `/family`
// and the tab title stays "Individual calculator" — bad UX, and
// worse for AdSense's contextual targeting which re-reads page
// metadata at every navigation tick.
//
// The hook registers a single `popstate` listener and a wrapper
// over `history.pushState` / `history.replaceState`, so the
// metadata updates on both Back/Forward navigation and on
// in-app `pushState` calls. Route lookup against
// `ClientConfig.PrerenderRoutes` is exact-match on path; routes
// not declared as prerender targets leave the existing metadata
// untouched.

[<Emit("typeof window !== 'undefined'")>]
let private hasWindow () : bool = jsNative

[<Emit("window.location.pathname")>]
let private currentPath () : string = jsNative

// Singleton — installed at most once per page-load. The original
// `history.pushState` / `replaceState` are captured before the
// wrap so re-entry is safe (a second `install` call is a no-op).
let mutable private installed = false

[<Emit("document.title = $0")>]
let private setDocumentTitle (title: string) : unit = jsNative

[<Emit("(function(name, content) { var el = document.querySelector('meta[name=\"' + name + '\"]'); if (!el) { el = document.createElement('meta'); el.setAttribute('name', name); document.head.appendChild(el); } el.setAttribute('content', content); })($0, $1)")>]
let private setMetaByName (name: string) (content: string) : unit = jsNative

[<Emit("(function(prop, content) { var el = document.querySelector('meta[property=\"' + prop + '\"]'); if (!el) { el = document.createElement('meta'); el.setAttribute('property', prop); document.head.appendChild(el); } el.setAttribute('content', content); })($0, $1)")>]
let private setMetaByProperty (property: string) (content: string) : unit = jsNative

[<Emit("(function(json) { var el = document.querySelector('script[type=\"application/ld+json\"][data-toolup-prerender=\"true\"]'); if (json === null) { if (el) el.remove(); return; } if (!el) { el = document.createElement('script'); el.setAttribute('type', 'application/ld+json'); el.setAttribute('data-toolup-prerender', 'true'); document.head.appendChild(el); } el.textContent = json; })($0)")>]
let private setJsonLd (json: string) : unit = jsNative

let private applyMeta (meta: PrerenderMeta) : unit =
    setDocumentTitle meta.Title
    setMetaByName "description" meta.Description

    for KeyValue(key, value) in meta.OpenGraph do
        // OG convention: tags use the `property=` attribute (not
        // `name=`). Consumers declare keys without the `og:` prefix
        // (e.g. `"title"`, `"image"`); the hook prepends it.
        let prop =
            if key.StartsWith "og:" || key.StartsWith "twitter:" then
                key
            else
                "og:" + key

        setMetaByProperty prop value

    match meta.JsonLd with
    | Some json -> setJsonLd json
    | None -> setJsonLd null

let private routeForPath (routes: PrerenderRoute list) (path: string) : PrerenderRoute option =
    routes |> List.tryFind (fun r -> r.Path = path)

let private updateForCurrentPath (routes: PrerenderRoute list) : unit =
    match routeForPath routes (currentPath ()) with
    | Some route -> applyMeta route.Meta
    | None -> ()

[<Emit("window.addEventListener('popstate', $0)")>]
let private addPopStateListener (cb: unit -> unit) : unit = jsNative

[<Emit("(function(cb) { var origPush = window.history.pushState; var origReplace = window.history.replaceState; window.history.pushState = function() { var r = origPush.apply(this, arguments); cb(); return r; }; window.history.replaceState = function() { var r = origReplace.apply(this, arguments); cb(); return r; }; })($0)")>]
let private wrapHistoryMethods (cb: unit -> unit) : unit = jsNative

/// Install the per-route metadata updater. Idempotent — second
/// and subsequent calls are no-ops. Call once during application
/// boot after `ClientConfig` is built, or skip when
/// `ClientConfig.PrerenderRoutes = []` (the empty-route case is
/// also a no-op, so calling unconditionally is safe).
let install (routes: PrerenderRoute list) : unit =
    if installed || not (hasWindow ()) || List.isEmpty routes then
        ()
    else
        installed <- true

        // Apply the initial-route metadata. Belt-and-braces against
        // hydration races where the prerendered `<head>` is already
        // correct — re-applying is cheap and converges.
        updateForCurrentPath routes

        let onNavigate () = updateForCurrentPath routes

        addPopStateListener onNavigate
        wrapHistoryMethods onNavigate