// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Bootstrap.PrerenderExport

open ToolUp.Platform
open Fable.Core
open Fable.Core.JsInterop

// ─── Phase 57 — SDK-side prerender entry-point ────────────────────
//
// Exposes the consumer's compiled Elmish view tree to the FAKE
// `Prerender` target's Node.js script. The script imports the
// consumer's Fable bundle, calls the function registered here on
// `globalThis`, and receives back the prerendered HTML body as a
// string. The script then wraps the body in the standard HTML
// document shape — `<head>` populated from `PrerenderMeta`, the
// `<meta name="toolup-prerendered" content="true">` marker
// injected — and writes `dist/{slugged-path}.html`.
//
// Why a globalThis registration rather than a Fable ES module
// export? Two reasons:
//   1. The consumer's bundle entry point is `Client.fs`, which
//      kicks off `Program.run` on module load. Calling
//      `installEntryPoint` *before* the program-run lets the Node
//      script reach the renderer without also triggering the SPA
//      mount (which would fail outside the browser anyway because
//      Hydration.run reads `document`). The browser branch never
//      reaches `installEntryPoint` because the function exits
//      early when `window` is present.
//   2. The Node script doesn't know the consumer's module-export
//      name — `installEntryPoint` reserves a stable
//      `globalThis.__toolup_prerender_render` shape regardless of
//      the consumer's bundling conventions.
//
// The renderer reuses the same `Client.program` builder the SPA
// uses, so the prerendered HTML is byte-equivalent to what the
// browser would mount immediately after hydration (modulo any
// non-determinism the view itself introduces — `DateTime.Now` /
// `Math.random` / locale-dependent formatting at init time. The
// authoring rules in `docs/platform/prerender.md` warn against
// these explicitly.)

[<Emit("typeof window !== 'undefined' && typeof document !== 'undefined'")>]
let private hasBrowserGlobals () : bool = jsNative

/// True when running in a browser (window + document defined),
/// false in Node / build-time. Consumers can branch on this when
/// composing their boot sequence — call `installEntryPoint` in
/// both, but only invoke `Hydration.run` / SPA mount in the
/// browser branch.
let isBrowser () : bool = hasBrowserGlobals ()

// `react-dom/server`'s `renderToString` ships in the same
// `react-dom` npm package the SPA already depends on. The
// `import "renderToString" from "react-dom/server"` form is the
// standard ESM entry; Fable resolves it to the Node-compatible
// build at prerender time. Browser bundlers (Vite) tree-shake the
// import out when it's not reached at runtime, so there's no
// browser-bundle cost for consumers that don't actually call
// `renderRouteToHtml` from the SPA path.
[<Import("renderToString", "react-dom/server")>]
let private renderToString (element: obj) : string = jsNative

/// Render a single declared route's Elmish view tree to an HTML
/// body string. Builds the same `Program<unit, Model, Msg,
/// ReactElement>` the SPA does via `Client.program`, then walks
/// the program's `init` + `view` once to produce a static
/// snapshot.
///
/// **Determinism contract** — the prerendered HTML MUST be
/// byte-for-byte identical across two runs of the same route
/// list. Sources of non-determinism the SDK cannot detect for
/// the consumer: `DateTime.Now`, `Math.random`, locale-dependent
/// formatting, async data loaded post-`init`. The authoring rules
/// in `docs/platform/prerender.md` warn consumers to pin these.
///
/// **InitStateKey contract** — passed through to the consumer's
/// init via a `__toolup_prerender_state_key` global the
/// consumer's init can read (via `window?.__toolup_prerender_state_key`).
/// Consumers that don't need route-specific state ignore the
/// key. Setting it before invoking `init` lets the consumer
/// branch on the route without the SDK needing to know the
/// shape of the consumer's `Model`.
let renderRouteToHtml (config: ClientConfig) (modules: ErasedModule list) (route: PrerenderRoute) : string =
    // Stash the route's init-state key on `globalThis` so the
    // consumer's `init` can read it. Setting before
    // `Client.program` runs ensures the consumer's init sees
    // the right value. Cleared on exit so a subsequent route's
    // render doesn't see a stale key.
    let stateKeyValue =
        match route.InitStateKey with
        | Some k -> box k
        | None -> null

    emitJsStatement
        (stateKeyValue, route.Path)
        """
        globalThis.__toolup_prerender_state_key = $0;
        globalThis.__toolup_prerender_path = $1;
    """

    // The Elmish `Program` exposes its `init` and `view` only
    // through `Program.run`, which mounts to a DOM node. For
    // static prerender we bypass `Program.run` and build the
    // initial render manually via the same primitives. The
    // re-import here uses the underlying `Client.program`
    // function's init + view closures — we re-create the
    // model+view at the snapshot point, then hand the
    // ReactElement to `renderToString`.
    //
    // We construct the program once to capture its closures via
    // Fable's internal representation of `Program<...>`. The
    // record has fields named `init` / `view` / `update` / etc.
    // We read them by JS property access; this is brittle to
    // Fable.Elmish internals but is the only way to extract
    // them without a public accessor.
    let program = Client.program config modules

    // Capture init and view via Fable's record-property access.
    // The fields on the Elmish `Program<arg, model, msg, view>`
    // record are typed but private; the runtime JS object
    // exposes them at predictable property names matching the
    // F# field names.
    let initFn: unit -> obj * obj = program?init
    let viewFn: obj -> (obj -> unit) -> obj = program?view

    let model, _cmd = initFn ()
    let noopDispatch (_msg: obj) : unit = ()
    let element = viewFn model noopDispatch

    let html = renderToString element

    // Clear the per-route globals so subsequent renders start
    // clean. (`undefined` is preferable to leaving the prior
    // route's key dangling — consumer init code checking
    // `globalThis.__toolup_prerender_state_key !== undefined`
    // stays correct.)
    emitJsStatement
        ()
        """
        delete globalThis.__toolup_prerender_state_key;
        delete globalThis.__toolup_prerender_path;
    """

    html

/// HTML-escape a string for safe insertion into element text /
/// attribute contexts. Conservative — escapes the five
/// HTML-significant characters. Used internally by
/// `wrapHtmlDocument`; exposed so a consumer authoring their
/// own document wrapper can reuse the same rules.
let escapeHtml (input: string) : string =
    if isNull input then
        ""
    else
        input
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")

/// Wrap a prerendered body HTML fragment in the standard HTML
/// document shape: doctype, `<html>` root, `<head>` populated
/// from `PrerenderMeta` (title / description / OG tags / optional
/// JSON-LD), the `<meta name="toolup-prerendered" content="true">`
/// marker, and a `<body>` containing the body inside the
/// `<div id="elmish-app">` mount node Bootstrap.Hydration looks
/// for.
///
/// `bundleScriptPath` is the URL the client bundle is served at
/// (e.g. `/output/Client.js`). `cssLinkHref` is an optional
/// stylesheet link (`Some "/output/Client.css"`); pass `None` for
/// consumers with no separate stylesheet.
///
/// Pure — same input produces byte-for-byte identical output. The
/// determinism test in `ToolUp.Platform.Tests` pins this.
let wrapHtmlDocument
    (meta: PrerenderMeta)
    (bodyHtml: string)
    (bundleScriptPath: string)
    (cssLinkHref: string option)
    : string =
    let sb = System.Text.StringBuilder()
    let inline appendLine (s: string) = sb.Append(s).Append('\n') |> ignore

    appendLine "<!DOCTYPE html>"
    appendLine "<html>"
    appendLine "<head>"
    appendLine "<meta charset=\"utf-8\">"
    appendLine "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
    appendLine "<meta name=\"toolup-prerendered\" content=\"true\">"
    appendLine (sprintf "<title>%s</title>" (escapeHtml meta.Title))

    appendLine (sprintf "<meta name=\"description\" content=\"%s\">" (escapeHtml meta.Description))

    // OpenGraph tags. Iterate in lexicographic key order so
    // emitted HTML is byte-stable across runs (Map iteration
    // order is sorted by key, but spelling the sort out
    // defends against any future implementation change).
    for (key, value) in meta.OpenGraph |> Map.toList |> List.sortBy fst do
        let property =
            if key.StartsWith "og:" || key.StartsWith "twitter:" then
                key
            else
                "og:" + key

        appendLine (sprintf "<meta property=\"%s\" content=\"%s\">" (escapeHtml property) (escapeHtml value))

    match meta.JsonLd with
    | Some json ->
        appendLine "<script type=\"application/ld+json\" data-toolup-prerender=\"true\">"
        appendLine json
        appendLine "</script>"
    | None -> ()

    match cssLinkHref with
    | Some href -> appendLine (sprintf "<link rel=\"stylesheet\" href=\"%s\">" (escapeHtml href))
    | None -> ()

    appendLine "</head>"
    appendLine "<body>"
    appendLine (sprintf "<div id=\"elmish-app\">%s</div>" bodyHtml)
    appendLine (sprintf "<script type=\"module\" src=\"%s\"></script>" (escapeHtml bundleScriptPath))
    appendLine "</body>"
    appendLine "</html>"

    sb.ToString()

/// Map a route path to the on-disk filename the prerender pass
/// writes under `dist/`. Filesystem-preserving so the
/// `PrerenderedRoutesMiddleware` lookup contract matches
/// byte-for-byte:
///   `/`            → `index.html`
///   `/individual`  → `individual.html`
///   `/calc/2009`   → `calc/2009.html`
///   `/foo/`        → `foo/index.html`
/// Pure; deterministic. The determinism test pins the same set
/// of cases.
let routePathToFile (path: string) : string =
    if System.String.IsNullOrEmpty path then
        "index.html"
    else
        let trimmed = path.TrimStart '/'

        if trimmed = "" then "index.html"
        elif trimmed.EndsWith '/' then trimmed + "index.html"
        else trimmed + ".html"

[<Emit("(function(render, mapPath) { globalThis.__toolup_prerender_render_doc = render; globalThis.__toolup_prerender_route_to_file = mapPath; })($0, $1)")>]
let private registerGlobals
    (render: PrerenderRoute * string * string option -> string)
    (mapPath: string -> string)
    : unit =
    jsNative

/// Install the prerender entry-point on `globalThis`. The FAKE
/// Prerender target's Node script reads two registrations:
///   - `globalThis.__toolup_prerender_render_doc(route,
///        bundleScriptPath, cssLinkHref)` — returns the fully
///        wrapped HTML document (head + meta + body + script tag)
///        for a declared route. Pure F# — no Node-side wrapper
///        code needs to know the document shape.
///   - `globalThis.__toolup_prerender_route_to_file(path)` —
///        maps a `PrerenderRoute.Path` to the on-disk filename
///        the document should be written to (filesystem-preserving;
///        matches the `PrerenderedRoutesMiddleware` lookup
///        contract byte-for-byte).
///
/// No-op when running in a browser — the SPA path doesn't need
/// the globals. Always safe to call: consumers can wire it
/// unconditionally in their boot sequence.
let installEntryPoint (config: ClientConfig) (modules: ErasedModule list) : unit =
    if isBrowser () then
        ()
    else
        let renderDoc (route: PrerenderRoute, bundleScriptPath: string, cssLinkHref: string option) : string =
            let body = renderRouteToHtml config modules route
            wrapHtmlDocument route.Meta body bundleScriptPath cssLinkHref

        registerGlobals renderDoc routePathToFile