// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Client.ServiceWorkerRegistration

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Phase 24 — service-worker + PWA manifest boot ───────────────────
//
// Registers the deployment's service worker and, when a manifest is
// configured, links it so the browser offers an install prompt.
//
// **Nothing here runs under `NoOffline`.** `boot` takes the
// `OfflineMode` and returns immediately on `NoOffline` — no
// registration, no manifest link, no install prompt, no console noise
// (GP 11 + GP 13). That is the whole reason the entry point takes the
// mode rather than the config.
//
// **Cache versioning is passed to the worker, not baked into it.** The
// worker reads `?v=<CacheVersion>` off its own script URL and derives
// every cache name from it, so a deployment bumps ONE config field to
// evict the previous generation. Baking the version into the worker
// source would mean editing a JS file on every release, which is
// exactly the drift a deployment does not notice until users are
// served last release's bundle.

[<Emit("('serviceWorker' in navigator)")>]
let private serviceWorkerSupported () : bool = jsNative

[<Emit("navigator.serviceWorker.register($0, { scope: $1 })")>]
let private registerWorker (url: string) (scope: string) : JS.Promise<obj> = jsNative

[<Emit("navigator.serviceWorker.getRegistrations().then(function (rs) { return Promise.all(rs.map(function (r) { return r.unregister(); })); })")>]
let private unregisterAll () : JS.Promise<obj> = jsNative

[<Emit("URL.createObjectURL(new Blob([$0], { type: 'application/manifest+json' }))")>]
let private manifestBlobUrl (json: string) : string = jsNative

/// Result of a boot attempt. A DU rather than a `bool` so a caller can
/// tell "the deployment did not ask for offline" apart from "the
/// deployment asked and the browser refused" — those want different
/// UI, and collapsing them is how an unsupported browser silently
/// looks like a working one.
type BootResult =
    /// `NoOffline` — nothing was attempted.
    | OfflineDisabled
    /// Worker registered against the configured scope.
    | WorkerRegistered of scope: string
    /// Offline was requested but this browser has no
    /// `navigator.serviceWorker` (an insecure origin, or a private
    /// window on some engines).
    | WorkerUnsupported
    /// Registration was attempted and rejected. Carries the browser's
    /// own message.
    | WorkerFailed of reason: string

/// Serialise a `PwaManifest` to the W3C manifest JSON shape.
///
/// Hand-built rather than serialised off the record because the wire
/// keys are `short_name` / `background_color` / `theme_color` /
/// `start_url` — snake_case names no F# record serialiser produces from
/// these field names, and a manifest with the wrong keys is silently
/// ignored by the browser rather than reported.
let manifestJson (manifest: PwaManifest) : string =
    let icons =
        manifest.Icons
        |> List.map (fun (src, sizes, mimeType) ->
            sprintf
                """{"src":%s,"sizes":%s,"type":%s}"""
                (JS.JSON.stringify src)
                (JS.JSON.stringify sizes)
                (JS.JSON.stringify mimeType))
        |> String.concat ","

    sprintf
        """{"name":%s,"short_name":%s,"display":%s,"background_color":%s,"theme_color":%s,"start_url":%s,"icons":[%s]}"""
        (JS.JSON.stringify manifest.Name)
        (JS.JSON.stringify manifest.ShortName)
        (JS.JSON.stringify manifest.Display)
        (JS.JSON.stringify manifest.BackgroundColor)
        (JS.JSON.stringify manifest.ThemeColor)
        (JS.JSON.stringify manifest.StartUrl)
        icons

/// The worker's script URL with the cache version attached. Exposed
/// (rather than inlined) because the reference worker's contract is
/// "read your version off your own URL", and a deployment writing its
/// own worker needs to see the shape it must honour.
///
/// A version change also changes the script URL, which is what makes
/// the browser fetch and install the new worker rather than reusing the
/// byte-identical old one.
let versionedWorkerUrl (config: OfflineConfig) : string =
    let separator = if config.ServiceWorkerUrl.Contains "?" then "&" else "?"
    sprintf "%s%sv=%s&cache=%s" config.ServiceWorkerUrl separator config.CacheVersion config.CachePrefix

/// Link the PWA manifest into `<head>`, from `ManifestUrl` when the
/// deployment ships its own file, otherwise from a blob built out of
/// `Manifest`. Idempotent — an existing `link[rel=manifest]` is
/// replaced, never duplicated.
let private linkManifest (config: OfflineConfig) : unit =
    let href =
        match config.ManifestUrl, config.Manifest with
        | Some url, _ -> Some url
        | None, Some manifest -> Some(manifestBlobUrl (manifestJson manifest))
        | None, None -> None

    match href with
    | None -> ()
    | Some url ->
        let doc = Browser.Dom.document

        let link =
            match doc.querySelector "link[rel='manifest']" with
            | null ->
                let created = doc.createElement "link"
                created.setAttribute ("rel", "manifest")
                doc.head.appendChild created |> ignore
                created
            | existing -> existing :?> Browser.Types.HTMLElement

        link.setAttribute ("href", url)

        // Match the manifest's theme colour on the meta tag too — a
        // manifest alone does not tint the address bar on the first
        // load, before the app is installed.
        match config.Manifest with
        | None -> ()
        | Some manifest ->
            let meta =
                match doc.querySelector "meta[name='theme-color']" with
                | null ->
                    let created = doc.createElement "meta"
                    created.setAttribute ("name", "theme-color")
                    doc.head.appendChild created |> ignore
                    created
                | existing -> existing :?> Browser.Types.HTMLElement

            meta.setAttribute ("content", manifest.ThemeColor)

/// Boot offline support for the given mode. Safe to call on every app
/// start; safe to call under `NoOffline`; never raises.
let boot (mode: OfflineMode) : Async<BootResult> = async {
    match mode with
    | NoOffline -> return OfflineDisabled
    | EnabledOffline config ->
        linkManifest config

        if not (serviceWorkerSupported ()) then
            return WorkerUnsupported
        else
            try
                let! _ =
                    registerWorker (versionedWorkerUrl config) config.ServiceWorkerScope
                    |> Async.AwaitPromise

                return WorkerRegistered config.ServiceWorkerScope
            with ex ->
                return WorkerFailed ex.Message
}

/// Tear every registration down. The sign-out / opt-out path: a worker
/// left registered keeps serving cached responses to the NEXT user of
/// the browser, which is a data-leak shape rather than a staleness one.
/// Pair it with `IOfflineQueue.Clear`.
let unregister () : Async<unit> = async {
    if serviceWorkerSupported () then
        try
            let! _ = unregisterAll () |> Async.AwaitPromise
            return ()
        with _ ->
            return ()
}