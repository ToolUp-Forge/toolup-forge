// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module internal ToolUp.Platform.BrowserHistory

open Fable.Core
open ToolUp.Platform.RegisteredModules

// ─── Phase 1240 — a view's URL ─────────────────────────────────────────
//
// The shell's active view lives in the Elmish model. This is the URL half,
// used only under `ClientConfig.BrowserHistory = HashHistory`: a view's URL
// is the fragment `#/{sidebarId}`, where the sidebar id is the
// `{moduleId}{pageRoute}` a sidebar click already sends.
//
// A fragment and not a path, because a path cannot work here: an SDK module
// id contains a dot (`_sdk.TeamManager`), and `SpaFallbackMiddleware` reads a
// path with an extension as an asset, so a reload would 404. A fragment never
// reaches the server, so an auth callback, a server-rendered route or any other
// path the server answers still resolves first, and the pathname and query
// string are left as they are. A fragment not starting `#/` is an ordinary
// in-page anchor and is not read as a view.
//
// Both writes compare against the URL when they run, which is what keeps a
// Back or Forward from pushing again: by the time the navigation it dispatched
// reaches a write, the URL already names that view.

[<Emit("typeof window !== 'undefined' && typeof window.history !== 'undefined'")>]
let private hasHistory () : bool = jsNative

[<Emit("window.location.hash")>]
let private currentHash () : string = jsNative

[<Emit("window.history.pushState(null, '', $0)")>]
let private pushUrl (url: string) : unit = jsNative

// Keeps whatever state the entry already carries; only the URL moves.
[<Emit("window.history.replaceState(window.history.state, '', $0)")>]
let private replaceUrl (url: string) : unit = jsNative

[<Emit("window.addEventListener('popstate', $0)")>]
let private addPopStateListener (handler: obj -> unit) : unit = jsNative

[<Emit("window.removeEventListener('popstate', $0)")>]
let private removePopStateListener (handler: obj -> unit) : unit = jsNative

[<Emit("encodeURI($0)")>]
let private encodeUri (text: string) : string = jsNative

[<Emit("decodeURI($0)")>]
let private decodeUri (text: string) : string = jsNative

/// The sidebar id of a view: `{moduleId}{pageRoute}`.
let viewId (moduleId: string) (pageRoute: string option) : string = moduleId + defaultArg pageRoute ""

/// The fragment naming a view.
let formatHash (sidebarId: string) : string = "#/" + encodeUri sidebarId

/// The sidebar id a fragment names, or `None` for anything else: no fragment,
/// an in-page anchor, or percent-encoding a hand-edited URL has broken.
let parseHash (hash: string) : string option =
    if hash.StartsWith "#/" && hash.Length > 2 then
        try
            Some(decodeUri (hash.Substring 2))
        with _ ->
            None
    else
        None

/// A sidebar id from a URL, checked against the registered modules. A URL is
/// reader input: an unknown id is refused, which also refuses the sidebar's
/// reserved area ids, and a bare module id or an undeclared page resolves to
/// the module's first page. Whether the reader may see the result is not
/// decided here; the id goes through the shell's route guard like any other.
let resolve (entries: ModuleEntry list) (sidebarId: string) : string option =
    let moduleId, pageRoute =
        match sidebarId.IndexOf '/' with
        | -1 -> sidebarId, None
        | i -> sidebarId.Substring(0, i), Some(sidebarId.Substring i)

    entries
    |> List.tryFind (fun entry -> entry.ModuleId = moduleId)
    |> Option.map (fun entry ->
        match pageRoute, entry.PageRoutes with
        | Some route, routes when List.contains route routes -> viewId moduleId (Some route)
        | _, first :: _ -> viewId moduleId (Some first)
        | _, [] -> moduleId)

let private urlView () : string option =
    if hasHistory () then parseHash (currentHash ()) else None

/// The view the current URL names, resolved against the registered modules.
let current (entries: ModuleEntry list) : string option =
    urlView () |> Option.bind (resolve entries)

/// Add a history entry for a view, unless the URL already names it.
let push (sidebarId: string) : unit =
    if hasHistory () && urlView () <> Some sidebarId then
        pushUrl (formatHash sidebarId)

/// Point the current history entry at a view, unless the URL already names it.
let replace (sidebarId: string) : unit =
    if hasHistory () && urlView () <> Some sidebarId then
        replaceUrl (formatHash sidebarId)

/// Call `onView` with each view a Back or Forward lands on, or a hand-edited
/// fragment names. A URL the modules do not recognise is ignored and a
/// non-canonical one is corrected in place, so a later write compares against
/// the id the shell will hold. Returns the unsubscribe thunk.
let subscribe (entries: unit -> ModuleEntry list) (onView: string -> unit) : unit -> unit =
    if hasHistory () then
        let handler (_: obj) =
            match current (entries ()) with
            | Some sidebarId ->
                replace sidebarId
                onView sidebarId
            | None -> ()

        addPopStateListener handler
        fun () -> removePopStateListener handler
    else
        ignore