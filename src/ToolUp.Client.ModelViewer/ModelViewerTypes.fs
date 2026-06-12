// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Client.ModelViewer

open Fable.Core

// ─── Phase 125 — typed surface for the <model-viewer> binding ────
//
// Marker-interface prop pattern (the same shape Feliz prop modules
// use): each `modelViewer.*` builder returns an
// `IModelViewerProperty`, so only model-viewer props type-check on
// the element, with a `custom` escape for forward compatibility
// with component attributes the binding doesn't name yet.

/// A typed `<model-viewer>` prop. Constructed via the `modelViewer`
/// builders in `ModelViewer.fs`; consumers never implement this.
type IModelViewerProperty = interface end

/// `loading` attribute — when the model file starts downloading.
[<StringEnum; RequireQualifiedAccess>]
type Loading =
    /// Loads when the element is near the viewport (the component's
    /// default).
    | Auto
    | Lazy
    | Eager

/// `reveal` attribute — when the loaded model replaces the poster.
[<StringEnum; RequireQualifiedAccess>]
type Reveal =
    /// Reveal as soon as the model is ready (the component's
    /// default).
    | Auto
    /// Reveal on user interaction with the poster.
    | Interaction
    /// Reveal only when `dismissPoster()` is called on the element.
    | Manual

/// `ar-modes` attribute values — which AR presentation paths the
/// component may offer, in preference order.
[<StringEnum; RequireQualifiedAccess>]
type ArMode =
    | [<CompiledName "webxr">] WebXr
    | [<CompiledName "scene-viewer">] SceneViewer
    | [<CompiledName "quick-look">] QuickLook

/// Typed projection of the component's `error` event detail.
type ModelViewerError = {
    /// `"loadfailure"` (the model file failed to fetch / parse) or
    /// `"webglcontextlost"`.
    ErrorType: string
}