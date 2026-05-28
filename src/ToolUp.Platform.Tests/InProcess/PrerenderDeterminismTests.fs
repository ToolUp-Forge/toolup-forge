module ToolUp.Platform.Tests.InProcess.PrerenderDeterminismTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Bootstrap.PrerenderExport
open ToolUp.Platform.Prerender

// ─── Phase 57 follow-up — determinism + hydration-mismatch contract ─
//
// The FAKE Prerender target's correctness rests on three byte-stable
// pieces (the consumer's view, which the SDK can't audit for them,
// is the fourth — authoring rules in `docs/platform/prerender.md`
// warn against `DateTime.Now` / `Math.random` in init):
//
//   1. `serialiseRoutes` — the route-list JSON the Node script
//      consumes. Pinned across two runs of the same input.
//   2. `nodeScript ()` — the Node prerender script body, emitted
//      to a temp file at target-run time. Pinned across two reads.
//   3. The pure F# helpers `routePathToFile` + `wrapHtmlDocument`
//      + `escapeHtml` that live in `PrerenderExport.fs`. These
//      compile under Fable AND .NET because they use only BCL
//      primitives — the Tests project reaches them via the
//      Client-tier ProjectReference.
//
// The hydration-mismatch detection contract is partly documented
// here: `wrapHtmlDocument` MUST inject
// `<meta name="toolup-prerendered" content="true">`. The
// `Hydration.fs` bootstrap reads that exact marker to decide
// between `hydrateRoot` and `createRoot`. Drift on either side
// would silently break the hydration branch — the SPA would
// re-render from scratch on top of the prerendered DOM, voiding
// the SEO first-paint. Pinning the marker shape here catches
// that drift. The actual React behaviour (mismatch warning on
// perturbed HTML) is browser-side and verified in the consumer's
// DevTools console per the acceptance criteria.

let private sampleMetaWithEverything: PrerenderMeta = {
    Title = "Acme — free <calculator> & ad-supported tool"
    Description = "Estimate your <2025–26> tax in 30s. No \"signup\" required."
    OpenGraph =
        Map.ofList [
            "title", "Acme Calculator"
            "description", "Free calculator"
            "image", "https://acme.example/og/home.png"
            "twitter:card", "summary_large_image"
            "type", "website"
        ]
    JsonLd = Some "{\"@context\":\"https://schema.org\",\"@type\":\"WebApplication\"}"
}

let private sampleMetaMinimal = PrerenderMeta.basic "About — Acme" "About page."

let private sampleRoutes: PrerenderRoute list = [
    {
        Path = "/"
        InitStateKey = None
        Meta = sampleMetaWithEverything
    }
    {
        Path = "/about"
        InitStateKey = Some "about"
        Meta = sampleMetaMinimal
    }
    {
        Path = "/calc/2009"
        InitStateKey = Some "calc-2009"
        Meta = PrerenderMeta.basic "2009 calc" "Deep-link example."
    }
]

// ─── 1. serialiseRoutes determinism ───────────────────────────────

let private serialiseRoutesDeterministic =
    testCase "serialiseRoutes — twice on the same input produces identical bytes"
    <| fun () ->
        let first = serialiseRoutes sampleRoutes
        let second = serialiseRoutes sampleRoutes
        Expect.equal first second "two passes must emit byte-for-byte identical JSON"

let private serialiseRoutesOrderStable =
    testCase "serialiseRoutes — OpenGraph keys serialise in lexicographic order regardless of Map insertion sequence"
    <| fun () ->
        // Two routes whose Map contents are equivalent but built
        // with different insertion sequences must yield the same
        // JSON. F#'s Map iteration is sorted by key, so this is
        // a regression sentinel against a future
        // implementation-change that breaks the contract.
        let mapA = Map.ofList [ "title", "T"; "image", "I"; "type", "W" ]
        let mapB = Map.ofList [ "type", "W"; "title", "T"; "image", "I" ]

        let routeFor m : PrerenderRoute = {
            Path = "/"
            InitStateKey = None
            Meta = {
                Title = "Same"
                Description = "Same"
                OpenGraph = m
                JsonLd = None
            }
        }

        let serA = serialiseRoutes [ routeFor mapA ]
        let serB = serialiseRoutes [ routeFor mapB ]
        Expect.equal serA serB "different Map insertion orders must serialise identically"

let private serialiseRoutesEmpty =
    testCase "serialiseRoutes — empty list serialises to []"
    <| fun () -> Expect.equal (serialiseRoutes []) "[]" "empty list must produce []"

let private serialiseRoutesEscaping =
    testCase "serialiseRoutes — JSON-escapes the five significant characters + control bytes"
    <| fun () ->
        let route: PrerenderRoute = {
            Path = "/x"
            InitStateKey = None
            Meta = {
                Title = "with \"quote\" and\nnewline and \\ backslash"
                Description = "x"
                OpenGraph = Map.empty
                JsonLd = None
            }
        }

        let s = serialiseRoutes [ route ]

        Expect.stringContains s "\\\"quote\\\"" "double quotes must JSON-escape"
        Expect.stringContains s "\\n" "newlines must JSON-escape"
        Expect.stringContains s "\\\\" "backslashes must JSON-escape"

// ─── 2. nodeScript () determinism ─────────────────────────────────

let private nodeScriptDeterministic =
    testCase "nodeScript — same SDK version emits byte-stable script content"
    <| fun () ->
        let first = nodeScript ()
        let second = nodeScript ()
        Expect.equal first second "two reads must return byte-for-byte identical script"

let private nodeScriptReferencesContract =
    testCase "nodeScript — references the SDK-side global names"
    <| fun () ->
        let s = nodeScript ()

        Expect.stringContains
            s
            "__toolup_prerender_render_doc"
            "Node script must call the render-doc global the SDK installs"

        Expect.stringContains
            s
            "__toolup_prerender_route_to_file"
            "Node script must call the route-to-file global the SDK installs"

        Expect.stringContains s "--routes" "Node script must accept the --routes argument"
        Expect.stringContains s "--bundle" "Node script must accept the --bundle argument"
        Expect.stringContains s "--out" "Node script must accept the --out argument"

// ─── 3. routePathToFile determinism + filesystem-preserving map ────

let private routePathToFileCases = [
    "/", "index.html"
    "", "index.html"
    "/individual", "individual.html"
    "/calc/2009", "calc/2009.html"
    "/foo/", "foo/index.html"
]

let private routePathToFileDeterministic =
    testCase "routePathToFile — deterministic across two calls per case"
    <| fun () ->
        for (path, expected) in routePathToFileCases do
            let first = routePathToFile path
            let second = routePathToFile path

            Expect.equal first expected (sprintf "routePathToFile %s should map to %s" path expected)

            Expect.equal first second "twice-called must return the same result"

let private routePathToFileMatchesMiddlewareContract =
    testCase "routePathToFile — output matches PrerenderedRoutesPath.tryResolve byte-for-byte"
    <| fun () ->
        // The Kestrel-side middleware reads the same filesystem
        // mapping. If they drift, prerendered files would be
        // written somewhere the middleware can't find them.
        // Pin parity here so the two stay in lockstep.
        for (path, _expected) in routePathToFileCases do
            let writerOutput = routePathToFile path

            let readerOutput =
                PrerenderedRoutesPath.tryResolve (if path = "" then "/" else path)

            match readerOutput with
            | Some reader ->
                Expect.equal
                    writerOutput
                    reader
                    (sprintf "writer (%s) must equal reader (%s) for path %s" writerOutput reader path)
            | None -> failwithf "PrerenderedRoutesPath.tryResolve returned None for %s" path

// ─── 4. wrapHtmlDocument determinism + structure ──────────────────

let private wrapHtmlDeterministic =
    testCase "wrapHtmlDocument — twice on the same input produces identical bytes"
    <| fun () ->
        let first =
            wrapHtmlDocument sampleMetaWithEverything "<p>body</p>" "/output/Client.js" None

        let second =
            wrapHtmlDocument sampleMetaWithEverything "<p>body</p>" "/output/Client.js" None

        Expect.equal first second "two passes must emit byte-for-byte identical HTML"

let private wrapHtmlInjectsMarker =
    testCase "wrapHtmlDocument — injects the toolup-prerendered marker the Hydration bootstrap reads"
    <| fun () ->
        let html = wrapHtmlDocument sampleMetaMinimal "<p>body</p>" "/output/Client.js" None
        // Hydration.fs reads exactly this attribute selector:
        //   document.querySelector('meta[name="toolup-prerendered"]')
        // Drift in name spelling (e.g. `toolup-pre-rendered`,
        // `ttm-prerendered`) breaks hydration detection silently.
        Expect.stringContains
            html
            "<meta name=\"toolup-prerendered\" content=\"true\">"
            "wrapHtmlDocument must inject the exact marker the bootstrap looks for"

let private wrapHtmlEscapesUserStrings =
    testCase "wrapHtmlDocument — HTML-escapes user-supplied title / description / og values"
    <| fun () ->
        let html =
            wrapHtmlDocument sampleMetaWithEverything "<p>body</p>" "/output/Client.js" None

        Expect.stringContains html "&lt;calculator&gt;" "title must escape angle brackets"
        Expect.stringContains html "&amp;" "title must escape ampersand"
        Expect.stringContains html "&quot;signup&quot;" "description must escape double quotes"

let private wrapHtmlMountNodeShape =
    testCase "wrapHtmlDocument — body contains the elmish-app mount node Hydration expects"
    <| fun () ->
        let html = wrapHtmlDocument sampleMetaMinimal "<p>body</p>" "/output/Client.js" None
        // Hydration calls `Program.withReactHydrate "elmish-app"`
        // — the prerendered HTML must wrap the body in
        // `<div id="elmish-app">...</div>` for `hydrateRoot` to
        // adopt the prerendered DOM without re-rendering. Pin
        // both the element shape and the id.
        Expect.stringContains
            html
            "<div id=\"elmish-app\"><p>body</p></div>"
            "body must mount inside <div id=\"elmish-app\">..."

let private wrapHtmlBundleScriptTagged =
    testCase "wrapHtmlDocument — bundle <script> tag is type=module for ES module loading"
    <| fun () ->
        let html = wrapHtmlDocument sampleMetaMinimal "<p>body</p>" "/output/Client.js" None

        Expect.stringContains
            html
            "<script type=\"module\" src=\"/output/Client.js\"></script>"
            "bundle script must be type=module so the Fable ESM bundle loads"

let private wrapHtmlOptionalCssLink =
    testCase "wrapHtmlDocument — CSS link is opt-in"
    <| fun () ->
        let withCss =
            wrapHtmlDocument sampleMetaMinimal "<p>body</p>" "/output/Client.js" (Some "/output/Client.css")

        let withoutCss =
            wrapHtmlDocument sampleMetaMinimal "<p>body</p>" "/output/Client.js" None

        Expect.stringContains
            withCss
            "<link rel=\"stylesheet\" href=\"/output/Client.css\">"
            "explicit CssLinkUrl must emit a <link rel=stylesheet>"

        Expect.isFalse
            (withoutCss.Contains "<link rel=\"stylesheet\"")
            "None CssLinkUrl must emit no <link rel=stylesheet>"

let private wrapHtmlOgPropertyPrefix =
    testCase "wrapHtmlDocument — OpenGraph bare keys get og: prefix; twitter: keys keep their prefix"
    <| fun () ->
        let html =
            wrapHtmlDocument sampleMetaWithEverything "<p>body</p>" "/output/Client.js" None

        Expect.stringContains html "<meta property=\"og:title\"" "bare keys must be prefixed with og:"

        Expect.stringContains html "<meta property=\"twitter:card\"" "twitter: keys must keep their prefix unchanged"

        Expect.isFalse
            (html.Contains "<meta property=\"og:twitter:card\"")
            "twitter:card must NOT be double-prefixed to og:twitter:card"

let private wrapHtmlJsonLdConditional =
    testCase "wrapHtmlDocument — JSON-LD script emitted only when present"
    <| fun () ->
        let withJsonLd =
            wrapHtmlDocument sampleMetaWithEverything "<p>body</p>" "/output/Client.js" None

        let withoutJsonLd =
            wrapHtmlDocument sampleMetaMinimal "<p>body</p>" "/output/Client.js" None

        Expect.stringContains
            withJsonLd
            "<script type=\"application/ld+json\" data-toolup-prerender=\"true\">"
            "Some JsonLd must emit the ld+json script"

        Expect.isFalse (withoutJsonLd.Contains "application/ld+json") "None JsonLd must emit no ld+json script"

// ─── 5. escapeHtml — the five-character contract ──────────────────

let private escapeHtmlContract =
    testCase "escapeHtml — escapes the five HTML-significant characters"
    <| fun () ->
        Expect.equal (escapeHtml "<a>") "&lt;a&gt;" "angle brackets"
        Expect.equal (escapeHtml "Tom & Jerry") "Tom &amp; Jerry" "ampersand"
        Expect.equal (escapeHtml "say \"hi\"") "say &quot;hi&quot;" "double quote"
        Expect.equal (escapeHtml "it's") "it&#39;s" "single quote"
        Expect.equal (escapeHtml null) "" "null input → empty string"

let private escapeHtmlOrderIndependence =
    testCase "escapeHtml — `&` is escaped before other expansions to avoid double-escape"
    <| fun () ->
        // If `&amp;` is produced and THEN `&` matched again, the
        // output would be `&amp;amp;`. The implementation must
        // escape `&` first.
        let s = escapeHtml "&<"
        Expect.equal s "&amp;&lt;" "single-pass escape leaves no &amp;amp; double-escape"

// ─── 6. PrerenderTargetOptions sanity ─────────────────────────────

let private optionsDefaultsSane =
    testCase "PrerenderTargetOptions.defaults — sane SDK conventions"
    <| fun () ->
        let d = PrerenderTargetOptions.defaults
        Expect.equal d.BundleDirectory "src/Client/output" "default Fable output dir"
        Expect.equal d.BundleEntryFile "Client.js" "default Fable entry-point filename"
        Expect.equal d.OutputDirectory "src/Client/dist" "default Vite dist dir"
        Expect.equal d.BundleScriptUrl "/output/Client.js" "default bundle-script URL"
        Expect.equal d.CssLinkUrl None "default has no separate CSS link"

[<Tests>]
let tests =
    testList "Phase 57 — prerender determinism + hydration-mismatch contract" [
        testList "serialiseRoutes" [
            serialiseRoutesDeterministic
            serialiseRoutesOrderStable
            serialiseRoutesEmpty
            serialiseRoutesEscaping
        ]
        testList "nodeScript" [ nodeScriptDeterministic; nodeScriptReferencesContract ]
        testList "routePathToFile" [ routePathToFileDeterministic; routePathToFileMatchesMiddlewareContract ]
        testList "wrapHtmlDocument" [
            wrapHtmlDeterministic
            wrapHtmlInjectsMarker
            wrapHtmlEscapesUserStrings
            wrapHtmlMountNodeShape
            wrapHtmlBundleScriptTagged
            wrapHtmlOptionalCssLink
            wrapHtmlOgPropertyPrefix
            wrapHtmlJsonLdConditional
        ]
        testList "escapeHtml" [ escapeHtmlContract; escapeHtmlOrderIndependence ]
        testList "options" [ optionsDefaultsSane ]
    ]