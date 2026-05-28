// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Prerender

open System
open System.IO
open System.Text
open Fake.Core
open ToolUp.Platform
open ToolUp.Platform.Build

// ─── Phase 57 follow-up — FAKE Prerender target factory ───────────
//
// Wraps a Node.js invocation in a reusable FAKE target consumers
// compose into their `Build.fs`. The target depends on `Fable` (so
// the Fable bundle is up-to-date before prerender runs), reads the
// declared `PrerenderRoute list`, and emits one HTML file per
// route into the consumer's `dist/` directory.
//
// Design — F# does the rendering, Node orchestrates:
//   - The consumer's compiled Fable bundle calls
//     `Bootstrap.PrerenderExport.installEntryPoint` during module
//     load (`isBrowser` check prevents the SPA from mounting in
//     Node). After `import(bundlePath)`, two globals are live on
//     `globalThis`:
//       • `__toolup_prerender_render_doc(route, bundleScript,
//          cssLink)` — returns the full HTML document.
//       • `__toolup_prerender_route_to_file(path)` — maps a route
//          path to its on-disk filename.
//   - The Node script reads the route list (passed as a JSON file
//     path), imports the bundle, iterates routes calling the
//     render-doc global, writes each result to the output
//     directory. Reports per-route success/failure on stdout.
//
// Determinism — the F# wrapping + route-to-file mapping is pure.
// The renderer's only non-determinism source is the consumer's
// view (which the authoring rules in `docs/platform/prerender.md`
// warn against). The Node script writes byte-for-byte identical
// output across runs when the consumer's view honours the
// authoring rules.

type PrerenderTargetOptions = {
    /// Directory the bundle is read from, relative to the
    /// consumer's repo root. Defaults to the Fable output dir
    /// (`src/Client/output`) — set explicitly only for
    /// non-standard layouts.
    BundleDirectory: string
    /// The compiled Fable entry-point file (must be an ES module
    /// — Fable 5+ default). Resolved relative to
    /// `BundleDirectory`. Defaults to `Client.js`.
    BundleEntryFile: string
    /// Output directory the rendered HTML files are written to,
    /// relative to the consumer's repo root. Defaults to
    /// `src/Client/dist` — set explicitly only for non-standard
    /// layouts. The `PrerenderedRoutesMiddleware` reads from
    /// `ServerConfig.PublicPath` which is conventionally the
    /// same directory.
    OutputDirectory: string
    /// URL path the bundle is served at in production (used in
    /// the emitted `<script type="module" src="...">` tag).
    /// Defaults to `/output/Client.js` to match the standard SDK
    /// vite.config.mts asset pipeline.
    BundleScriptUrl: string
    /// Optional URL path for a separate CSS stylesheet. Pass
    /// `None` (the default) for consumers using Tailwind's
    /// JS-injected styles or a CSS-in-JS pipeline.
    CssLinkUrl: string option
}

module PrerenderTargetOptions =
    let defaults = {
        BundleDirectory = "src/Client/output"
        BundleEntryFile = "Client.js"
        OutputDirectory = "src/Client/dist"
        BundleScriptUrl = "/output/Client.js"
        CssLinkUrl = None
    }

// ─── JSON serialisation for the routes payload ────────────────────
//
// The Node script reads the route list from a temp JSON file the
// FAKE target writes immediately before spawning Node. We build
// the JSON by hand (rather than `System.Text.Json`) so the
// `PrerenderRoute` shape — with its F# `option` + `Map<string,
// string>` fields — encodes in the JS-friendly form the bundle's
// F# code path consumes when re-hydrating the record on the JS
// side (Fable's runtime expects `None` as JS `undefined` and
// `Map<string, string>` as a list of `[key, value]` tuples).

let private escapeJsonString (input: string) : string =
    if isNull input then
        ""
    else
        let sb = StringBuilder()

        for c in input do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\b' -> sb.Append "\\b" |> ignore
            | '\f' -> sb.Append "\\f" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

let private quoteString (s: string) : string = sprintf "\"%s\"" (escapeJsonString s)

let private renderMetaJson (meta: PrerenderMeta) : string =
    let sb = StringBuilder()
    sb.Append("{") |> ignore
    sb.AppendFormat("\"Title\":{0}", quoteString meta.Title) |> ignore

    sb.AppendFormat(",\"Description\":{0}", quoteString meta.Description) |> ignore

    // Fable's runtime shape for `Map<string, string>` is an
    // immutable map; on the JS side it's marshalled as a list
    // of [key, value] tuples that `Map.ofList` re-builds. The
    // Node side never reconstitutes the F# Map — it just
    // forwards the JSON record back to F# globals via the
    // installed `__toolup_prerender_render_doc(route, ...)`
    // call, which receives the route as a JS object. The F#
    // record's `OpenGraph: Map<string, string>` field accepts
    // a JS object literal at the Fable boundary because Fable's
    // runtime conversion handles the shape automatically when
    // the JS object is consumed via record-property access.
    sb.Append(",\"OpenGraph\":{") |> ignore
    let mutable first = true

    for (key, value) in meta.OpenGraph |> Map.toList |> List.sortBy fst do
        if not first then
            sb.Append(",") |> ignore

        first <- false
        sb.AppendFormat("{0}:{1}", quoteString key, quoteString value) |> ignore

    sb.Append("}") |> ignore

    match meta.JsonLd with
    | Some json -> sb.AppendFormat(",\"JsonLd\":{0}", quoteString json) |> ignore
    | None -> sb.Append(",\"JsonLd\":null") |> ignore

    sb.Append("}") |> ignore
    sb.ToString()

let private renderRouteJson (route: PrerenderRoute) : string =
    let sb = StringBuilder()
    sb.Append("{") |> ignore
    sb.AppendFormat("\"Path\":{0}", quoteString route.Path) |> ignore

    match route.InitStateKey with
    | Some k -> sb.AppendFormat(",\"InitStateKey\":{0}", quoteString k) |> ignore
    | None -> sb.Append(",\"InitStateKey\":null") |> ignore

    sb.AppendFormat(",\"Meta\":{0}", renderMetaJson route.Meta) |> ignore
    sb.Append("}") |> ignore
    sb.ToString()

/// Serialise a route list to the JSON payload the Node script
/// consumes. Public so consumers can pin determinism via
/// snapshot — the same routes serialise to the same bytes across
/// runs.
let serialiseRoutes (routes: PrerenderRoute list) : string =
    let sb = StringBuilder()
    sb.Append("[") |> ignore

    routes
    |> List.iteri (fun i route ->
        if i > 0 then
            sb.Append(",") |> ignore

        sb.Append(renderRouteJson route) |> ignore)

    sb.Append("]") |> ignore
    sb.ToString()

// ─── Node script body ─────────────────────────────────────────────
//
// Emitted as a literal F# string and written to a temp `.mjs`
// file at target-run time. Self-contained: depends only on Node
// 22.x built-ins (no npm install beyond what the consumer's
// Fable bundle already needs — `react` + `react-dom` are
// transitive deps of every Feliz/Fable.Elmish SPA in this
// workspace). Versioned with the SDK because the script's
// expectations (`globalThis.__toolup_prerender_render_doc` shape)
// are coupled to PrerenderExport.fs above.

let private nodeScriptBody =
    """// Auto-generated by ToolUp.Platform.Build.Prerender.registerTarget.
// Do not edit — regenerated on every FAKE Prerender target run.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const args = process.argv.slice(2);
const arg = (name) => {
    const idx = args.indexOf(name);
    if (idx < 0 || idx >= args.length - 1) {
        throw new Error('Missing required argument: ' + name);
    }
    return args[idx + 1];
};

const routesJsonPath = arg('--routes');
const bundlePath     = arg('--bundle');
const outDir         = arg('--out');
const bundleScript   = arg('--bundle-script');
const cssLink        = (args.indexOf('--css-link') >= 0) ? arg('--css-link') : null;

const routes = JSON.parse(readFileSync(routesJsonPath, 'utf8'));

// Import the consumer's Fable bundle. Importing triggers the
// consumer's top-level code, which must call
// `Bootstrap.PrerenderExport.installEntryPoint` before any
// browser-only path (the `isBrowser` check inside the SDK
// short-circuits the SPA mount in Node).
const bundleUrl = pathToFileURL(resolve(bundlePath)).href;
await import(bundleUrl);

const renderDoc  = globalThis.__toolup_prerender_render_doc;
const routeToFile = globalThis.__toolup_prerender_route_to_file;

if (typeof renderDoc !== 'function') {
    console.error('[prerender] globalThis.__toolup_prerender_render_doc was not registered.');
    console.error('[prerender] Check that the consumer Client.fs calls');
    console.error('[prerender]   Bootstrap.PrerenderExport.installEntryPoint config modules');
    console.error('[prerender] before any browser-only path.');
    process.exit(1);
}

if (typeof routeToFile !== 'function') {
    console.error('[prerender] globalThis.__toolup_prerender_route_to_file was not registered.');
    process.exit(1);
}

mkdirSync(outDir, { recursive: true });

let failureCount = 0;

for (const route of routes) {
    try {
        // F# Option<string> serialises through this script as
        // either a string or null. The Fable runtime treats null
        // and undefined as None when reading record fields. We
        // normalise to `null` so the JSON round-trip is
        // byte-stable.
        if (route.InitStateKey === undefined) {
            route.InitStateKey = null;
        }
        if (route.Meta && route.Meta.JsonLd === undefined) {
            route.Meta.JsonLd = null;
        }

        const html = renderDoc(route, bundleScript, cssLink === null ? undefined : cssLink);
        const filename = routeToFile(route.Path);
        const target = join(outDir, filename);
        mkdirSync(dirname(target), { recursive: true });
        writeFileSync(target, html, 'utf8');
        console.log('[prerender] ' + route.Path + ' -> ' + target + ' (' + html.length + ' bytes)');
    } catch (err) {
        failureCount++;
        console.error('[prerender] FAILED ' + route.Path + ': ' + (err && err.message ? err.message : String(err)));
        if (err && err.stack) {
            console.error(err.stack);
        }
    }
}

if (failureCount > 0) {
    console.error('[prerender] ' + failureCount + ' route(s) failed.');
    process.exit(1);
}

console.log('[prerender] ' + routes.length + ' route(s) rendered.');
"""

/// Return the Node script body as a string. Pure / deterministic —
/// the same SDK version emits byte-for-byte identical script
/// contents across runs. The determinism test pins this.
let nodeScript () : string = nodeScriptBody

// ─── FAKE target registration ─────────────────────────────────────

/// Register a `Prerender` FAKE target that, after the standard
/// `Fable` target completes, invokes Node against the consumer's
/// compiled bundle and emits one prerendered HTML file per
/// declared route.
///
/// Usage:
/// ```fsharp
/// // Build.fs
/// open ToolUp.Platform.Build
/// open ToolUp.Platform.Prerender
///
/// let routes : PrerenderRoute list = [ ... ]
/// init args
/// registerTargets config
/// registerTarget config PrerenderTargetOptions.defaults routes
/// execute args
/// ```
///
/// Then `dotnet run -- Prerender` runs the chain `Build → Fable
/// → Prerender`. Empty `routes` is a no-op (the target logs and
/// exits 0 without spawning Node), so wiring the call
/// unconditionally is safe even for SPA-only deployments. (The
/// FAKE target dep on `Fable` matches the chain documented in
/// `docs/platform/prerender.md`.)
let registerTarget (config: BuildConfig) (options: PrerenderTargetOptions) (routes: PrerenderRoute list) : unit =
    Target.create "Prerender" (fun _ ->
        if List.isEmpty routes then
            Trace.tracefn "[prerender] No routes declared (PrerenderRoutes = []). Skipping."
        else
            let repoRoot = Path.GetFullPath "."
            let bundleDir = Path.GetFullPath(Path.Combine(repoRoot, options.BundleDirectory))

            let bundleFile = Path.GetFullPath(Path.Combine(bundleDir, options.BundleEntryFile))

            let outDir = Path.GetFullPath(Path.Combine(repoRoot, options.OutputDirectory))

            if not (File.Exists bundleFile) then
                failwithf
                    "[prerender] Bundle entry-point not found at %s. Ensure the Fable target ran successfully before Prerender."
                    bundleFile

            // Write the Node script + routes JSON to a temp dir.
            // Using a temp dir (rather than the consumer's repo)
            // keeps the consumer's git status clean across runs.
            let tempDir =
                Path.Combine(Path.GetTempPath(), sprintf "toolup-prerender-%s" (Guid.NewGuid().ToString("N")))

            Directory.CreateDirectory tempDir |> ignore

            let scriptPath = Path.Combine(tempDir, "prerender.mjs")
            let routesJsonPath = Path.Combine(tempDir, "routes.json")

            File.WriteAllText(scriptPath, nodeScriptBody)
            File.WriteAllText(routesJsonPath, serialiseRoutes routes)

            Trace.tracefn "[prerender] Bundle:  %s" bundleFile
            Trace.tracefn "[prerender] Routes:  %d declared" (List.length routes)
            Trace.tracefn "[prerender] Output:  %s" outDir

            Directory.CreateDirectory outDir |> ignore

            let baseArgs = [
                scriptPath
                "--routes"
                routesJsonPath
                "--bundle"
                bundleFile
                "--out"
                outDir
                "--bundle-script"
                options.BundleScriptUrl
            ]

            let nodeArgs =
                match options.CssLinkUrl with
                | Some css -> baseArgs @ [ "--css-link"; css ]
                | None -> baseArgs

            try
                CreateProcess.fromRawCommand "node" nodeArgs
                |> CreateProcess.withWorkingDirectory repoRoot
                |> CreateProcess.ensureExitCode
                |> Proc.run
                |> ignore
            finally
                try
                    if Directory.Exists tempDir then
                        Directory.Delete(tempDir, true)
                with _ ->
                    ())

    // Dependency wiring — Prerender runs after Fable. The
    // `Fable` target itself is registered transitively when
    // `Bundle` / `Run` wires it; consumers who only call
    // Prerender (no Bundle / no Run) get an implicit fable
    // build via the dependency chain.
    let (==>) a b = Fake.Core.TargetOperators.(==>) a b
    "Build" ==> "Prerender" |> ignore