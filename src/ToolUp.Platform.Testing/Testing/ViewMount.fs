// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Testing.ViewMount

// ─── The DOM half of the module-view a11y floor — OPT-IN FABLE SOURCE ──
//
// `ModuleHarness.AssertAccessibleView` gives a module the Phase 180
// accessibility floor over its OWN `view` — the real Feliz function the
// shell renders, not a hand-built `A11yNode` mirror of it. To do that
// something has to turn a `ReactElement` into markup, and that something
// needs a DOM. This file is that something.
//
// ── Why it is not compiled into ToolUp.Platform.Testing ──
// It is `Content`-packed under `fable/` like every other file here, but it
// is deliberately absent from the `<Compile>` list, so it is NOT part of
// the .NET assembly and NOT part of the package's compiled surface. That
// is the whole seam:
//
//   * `ModuleHarness` never names jsdom, react-dom or React. It takes a
//     `mount: 'View -> string` FUNCTION. Its only dependency is the BCL.
//   * A consumer that never source-includes this file emits nothing from
//     it, imports nothing, and needs no `jsdom` / `react-dom` in
//     `package.json`. A deployment that does not use it pays nothing
//     (GP 13) — including the deployments that consume the Testing
//     package purely .NET-side, where mounting React is impossible in
//     principle.
//   * A consumer that DOES want the floor adds one `<Compile
//     Include="…\ToolUp.Platform.Testing\Testing\ViewMount.fs" />` to its
//     Fable test project (exactly as it already does for
//     `AccessibilityAssertions.fs`) plus `jsdom` as a devDependency, and
//     gets the mount rather than copy-pasting forty lines of it.
//
// Shipping it as source instead of as a compiled member is the only shape
// that satisfies both halves: the machinery is REUSED (a second DOM
// harness per consumer is the drift this exists to avoid) and it is
// INERT unless asked for.
//
// ── Why a DOM at all, and not a string renderer ──
// `react-dom/server.renderToStaticMarkup` needs no DOM and is tempting,
// but it only ever renders a component's INITIAL state. Any view whose
// shape depends on `useState` driven by an event — a hover, an expand, a
// focus — is unreachable that way. Phase 610 measured this on the shell
// rail (whose narrow-vs-expanded axis is exactly such a state) and
// mounted instead; `mountAndInteract` below is that lesson generalised:
// the caller drives whatever event its view needs and reads the markup
// after React has committed.
//
// ── What it does NOT model ──
// CSS, focus rings, contrast, and the browser's real accessibility tree.
// `Accessibility.ofHtml` reads DOM/ARIA shape; computed style is
// invisible to it.

open Fable.Core
open Fable.Core.JsInterop

[<Import("JSDOM", from = "jsdom")>]
[<AllowNullLiteral>]
type private JSDOM(html: string, options: obj) =
    member _.window: obj = jsNative

[<Import("createRoot", from = "react-dom/client")>]
let private createRoot (container: obj) : obj = jsNative

/// React's own render-flush wrapper. Every mutation of a mounted tree —
/// the initial render and any interaction — runs inside it, so React has
/// committed before the markup is read; without it the capture races the
/// commit and intermittently reads the pre-render DOM.
[<Import("act", from = "react")>]
let private act (body: unit -> unit) : unit = jsNative

/// `globalThis.<name> = value`, via `defineProperty` because several of
/// the names below (`navigator` above all) are getter-only on the Node
/// global object and a plain assignment throws.
[<Emit("Object.defineProperty(globalThis, $0, { value: $1, configurable: true, writable: true })")>]
let private defineGlobal (name: string) (value: obj) : unit = jsNative

/// The globals React 19 and the SDK's client-tier libraries read off the
/// ambient scope. Installed from the jsdom window per mount —
/// `react-dom/client` reads them when `createRoot` runs, not at import
/// time, so ordinary static imports above are fine.
let private ambientGlobals = [
    "window"
    "document"
    "HTMLElement"
    "Element"
    "Node"
    "Event"
    "MouseEvent"
    "KeyboardEvent"
    "SVGElement"
    "getComputedStyle"
    "requestAnimationFrame"
    "cancelAnimationFrame"
    "navigator"
]

let private installGlobals (window: obj) =
    for name in ambientGlobals do
        defineGlobal name (window?(name))

    // React refuses `act` outside an act-environment; this is the flag it
    // reads to know it is in a test.
    defineGlobal "IS_REACT_ACT_ENVIRONMENT" true

/// Mount a rendered view into a fresh DOM, run `drive` against the
/// mounting host element (inside React's `act`, so its effects are
/// committed before the capture), and return the host's markup.
///
/// `drive` is how a `useState`-driven state is reached: dispatch a real
/// event at a descendant of `host` and the component re-renders exactly
/// as it would in a browser. Pass `ignore` for a view whose initial
/// render is the state under test — that is what `mount` does.
///
/// The whole HOST is returned, not just the view's own root: portalled
/// live regions, drag-and-drop keyboard instructions and other sibling
/// nodes are part of what assistive tech sees, and a floor that skipped
/// them would be checking less than the user gets.
let mountAndInteract (drive: obj -> unit) (element: 'View) : string =
    let dom =
        JSDOM("<!doctype html><html><body></body></html>", createObj [ "pretendToBeVisual" ==> true ])

    installGlobals dom.window

    let document = dom.window?document
    let host = document?createElement "div"
    document?body?appendChild host

    let root = createRoot host
    act (fun () -> root?render element |> ignore)
    act (fun () -> drive host)

    host?innerHTML

/// Mount a rendered view into a fresh DOM and return the markup a browser
/// would have. The `mount` seam `ModuleHarness.AssertAccessibleView`
/// expects.
let mount (element: 'View) : string = mountAndInteract ignore element