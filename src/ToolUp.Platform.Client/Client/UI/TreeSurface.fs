// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// A view whose code arrives on demand: the component a dynamic import resolves
/// to, mounted behind `Suspense`. It imports no Fuaran package, so a shell file
/// may import it at boot and pay nothing for what the component reaches.
///
/// A tree surface uses it to keep its whole tree off the first load. Its static
/// module (the one the shell imports for the surface's definition and `update`)
/// calls `deferred` with a dynamic import of a component in a file of its own,
/// and that file builds the tree and draws it with `TreeLoweringRuntime.Tree`
/// over `TreeLowering.treeProps`. The tree's authoring helpers and the Fuaran-UI
/// vocabulary are then reached only from that file, so the browser fetches them
/// with the renderer when the surface first mounts. `TreeLowering.lower` keeps
/// the vocabulary at boot: it lowers a tree the caller has already built, so
/// only the renderer waits.
///
/// While the code loads the surface shows a placeholder (`role="status"`,
/// `aria-busy`), the shape a grid or a chart shows while theirs loads. A load
/// that fails rejects into the nearest error boundary, which in the SDK shell
/// is `ModuleBoundary` and its "Reload module" button. `React.lazy` keeps a
/// rejection for good, so the rejected component is dropped here: the remount
/// the button causes builds a new one and the browser fetches the code again.
///
/// A component reached this way from a package a consumer restores from a feed
/// needs a `.js` re-export beside its source, as `TreeLoweringRuntime.js` is:
/// Fable spells the dynamic import `./<File>.js`, and a feed's sources compile
/// to `<File>.fs.js`.
module internal TreeSurface

open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Feliz

let private reactLazy: (unit -> JS.Promise<obj>) -> obj = import "lazy" "react"

[<Emit("$0.then(c => ({ default: c }), e => { $1(); throw e; })")>]
let private asLazyModule (load: JS.Promise<'c>) (onFailed: unit -> unit) : JS.Promise<obj> = jsNative

// Sanctioned module-level mutable state: one lazy component per surface key,
// so a re-render does not remount the surface, and a failed one is removed.
let private components = Dictionary<string, obj>()

let private componentFor (key: string) (load: unit -> JS.Promise<obj>) : obj =
    match components.TryGetValue key with
    | true, loaded -> loaded
    | _ ->
        let loaded =
            reactLazy (fun () -> asLazyModule (load ()) (fun () -> components.Remove key |> ignore))

        components[key] <- loaded
        loaded

/// Drop the component held for `key`, so its next render loads it again.
let internal forget (key: string) : unit = components.Remove key |> ignore

/// What a deferred view shows while its code loads.
let private placeholder =
    Html.div [
        prop.role "status"
        prop.ariaBusy true
        prop.className "animate-pulse rounded bg-gray-100"
        prop.style [ style.minHeight 160 ]
    ]

/// The component `load` resolves to, rendered with `props`. `key` names the
/// component for its lifetime: the first render under a key starts the load,
/// and later ones reuse it, so pass a key that is fixed per component (its
/// module and member name), never one built from the model.
let deferred (key: string) (load: unit -> JS.Promise<obj>) (props: 'Props) : ReactElement =
    React.Suspense(
        [
            ReactLegacy.createElement (unbox<ReactElement> (componentFor key load), box props)
        ],
        placeholder
    )