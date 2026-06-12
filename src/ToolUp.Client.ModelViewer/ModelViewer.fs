// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Client.ModelViewer

open Fable.Core
open Fable.Core.JsInterop
open Feliz

/// Interop plumbing for the binding. Public (not `private`) because
/// the `modelViewer` builders below are `inline` members on an
/// erased type — Fable inlines their bodies at every call site, so
/// call sites import these helpers directly (same constraint as the
/// SDK's `MemoizedChart` rule).
[<AutoOpen>]
module ModelViewerInterop =

    /// Prefix distinguishing event-handler props from attribute
    /// props inside the erased prop tuples; `ModelViewer.viewer`
    /// partitions on it.
    [<Literal>]
    let EventPropPrefix = "__toolup-event:"

    let mkModelViewerProp (name: string) (value: obj) : IModelViewerProperty = unbox (name, value)

    /// Presence-based boolean attributes: React 19 passes unknown
    /// props on custom elements through as attributes, but a `false`
    /// boolean must be omitted entirely (attribute presence is what
    /// the component reads), so lower true → "" and false → null
    /// (React omits null-valued attributes).
    let mkBoolAttr (name: string) (value: bool) : IModelViewerProperty =
        mkModelViewerProp name (if value then box "" else null)

    let mkEventProp (name: string) (handler: obj) : IModelViewerProperty =
        mkModelViewerProp (EventPropPrefix + name) handler

/// Typed `<model-viewer>` props. `src` and `alt` are not here — they
/// are required arguments of `ModelViewer.viewer` (a11y by
/// construction).
[<Erase>]
type modelViewer =
    /// Image shown while the model loads (and until `reveal`
    /// dismisses it). Pair with `loading` / `reveal` for skeleton
    /// states consistent with platform conventions.
    static member inline poster(value: string) = mkModelViewerProp "poster" value

    /// Enable orbit / zoom / pan user interaction.
    static member inline cameraControls(value: bool) = mkBoolAttr "camera-controls" value

    /// Slowly rotate the model when idle.
    static member inline autoRotate(value: bool) = mkBoolAttr "auto-rotate" value

    /// Initial camera position as "theta phi radius", e.g.
    /// "45deg 55deg 2.5m".
    static member inline cameraOrbit(value: string) = mkModelViewerProp "camera-orbit" value

    /// Vertical field of view, e.g. "30deg".
    static member inline fieldOfView(value: string) = mkModelViewerProp "field-of-view" value

    /// Exposure multiplier for scene lighting (component default 1.0).
    static member inline exposure(value: float) = mkModelViewerProp "exposure" value

    /// Environment image (equirectangular HDR / LDR url) used for
    /// lighting, or "neutral" for the built-in studio environment.
    static member inline environmentImage(value: string) =
        mkModelViewerProp "environment-image" value

    /// When the model file starts downloading.
    static member inline loading(value: Loading) = mkModelViewerProp "loading" value

    /// When the loaded model replaces the poster.
    static member inline reveal(value: Reveal) = mkModelViewerProp "reveal" value

    /// Offer AR presentation on capable devices.
    static member inline ar(value: bool) = mkBoolAttr "ar" value

    /// AR presentation paths in preference order; lowers to the
    /// space-separated `ar-modes` attribute.
    static member inline arModes(modes: ArMode list) =
        mkModelViewerProp "ar-modes" (modes |> List.map unbox<string> |> String.concat " ")

    /// CSS class for the element (the component defaults to a
    /// 300×150 inline box — size it like any replaced element).
    static member inline className(value: string) = mkModelViewerProp "class" value

    /// Inline styles for the element.
    static member inline style(properties: IStyleAttribute list) =
        mkModelViewerProp "style" (createObj !!properties)

    /// Forward-compatibility escape: pass any component attribute the
    /// binding doesn't name yet, e.g. `custom ("shadow-intensity", "1")`
    /// — new component releases don't need a binding release.
    static member inline custom(name: string, value: string) = mkModelViewerProp name value

    /// The model finished loading (poster → reveal lifecycle: with
    /// the default `reveal = auto`, fires right as the model becomes
    /// visible).
    static member inline onLoad(handler: Browser.Types.Event -> unit) = mkEventProp "load" (box handler)

    /// The model failed to load (or the WebGL context was lost).
    static member inline onError(handler: ModelViewerError -> unit) =
        mkEventProp
            "error"
            (box (fun (ev: Browser.Types.Event) ->
                let detailType: obj = ev?detail?``type``

                handler {
                    ErrorType = if isNull detailType then "" else string detailType
                }))

    /// Download progress in [0.0 .. 1.0] — drive skeleton / progress
    /// affordances from this.
    static member inline onProgress(handler: float -> unit) =
        mkEventProp "progress" (box (fun (ev: Browser.Types.Event) -> handler (unbox ev?detail?totalProgress)))

[<RequireQualifiedAccess>]
module ModelViewer =

    // Importing the package registers the <model-viewer> custom
    // element with the browser — once per app, automatically, and
    // only when this companion is composed: Fable hoists the import
    // to the top of this compiled module, and the module only enters
    // the bundle graph when consumer code references the binding
    // (GP 13 — apps without the companion ship zero script weight).
    importSideEffects "@google/model-viewer"

    /// Render a `<model-viewer>` element. `src` (the glTF / GLB url)
    /// and `alt` (assistive-technology description — mandatory for
    /// a11y, so it is an argument rather than an optional prop) are
    /// required by construction.
    let viewer (src: string) (alt: string) (props: IModelViewerProperty list) : ReactElement =
        let pairs = props |> List.map unbox<string * obj>

        let events =
            pairs
            |> List.choose (fun (name, value) ->
                if name.StartsWith EventPropPrefix then
                    Some(name.Substring EventPropPrefix.Length, value)
                else
                    None)

        let attributes =
            pairs |> List.filter (fun (name, _) -> not (name.StartsWith EventPropPrefix))

        let baseProps = [ "src", box src; "alt", box alt ] @ attributes

        let allProps =
            if events.IsEmpty then
                baseProps
            else
                let handlers = List.toArray events

                // Custom-element events ("load" / "error" /
                // "progress" are CustomEvents, not on* properties)
                // need addEventListener; React's on* props don't
                // reach them. Wire via a callback ref, detaching
                // whatever the previous render attached so listeners
                // never stack across re-renders.
                let refCallback (element: Browser.Types.Element) =
                    if not (isNull (box element)) then
                        let prior: (string * obj)[] = element?__toolupModelViewerListeners

                        if not (isNull (box prior)) then
                            for name, handler in prior do
                                element.removeEventListener (name, unbox handler)

                        element?__toolupModelViewerListeners <- handlers

                        for name, handler in handlers do
                            element.addEventListener (name, unbox handler)

                baseProps @ [ "ref", box refCallback ]

        // Feliz's IReactProperty is the same erased (name, value)
        // tuple shape as IModelViewerProperty, so the list passes
        // straight through the Feliz element factory.
        HtmlHelper.createElement "model-viewer" (unbox<IReactProperty list> allProps)