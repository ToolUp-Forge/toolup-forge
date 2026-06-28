// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 264 — host-state binding-source namespace (read-side) ───────
//
// The Phase 110 client host-capability seam lets an external typed-tree
// UI language *be* a `ClientModule` view and routes its typed ACTIONS
// (`Navigate` / `Call` / `Notify` / `Dispatch`) onto shipped concretes —
// but it ships no READ-side. A data-driven hosted tree had to bake state
// into the tree at construction and re-emit the whole tree on every
// change, because there was no neutral place for the host to project its
// state into and the tree to resolve its bindings against.
//
// `HostBindingSources` is that place: a renderer-neutral key→value
// namespace. The host owns PROJECTION (model / session-state →
// `HostBindingSources`); the tree owns RESOLUTION (`QueryResults[name]`
// / `State[key]` → a value). No tree-language type appears here (GP 1),
// so any external tree language binds its resolver onto these two maps
// exactly as the action side binds onto the four capability hooks.
//
// It lives in `ToolUp.Platform.Core` (the shared floor) — not the Fable
// client tier — because BOTH the CSR projection (`ToolUp.Platform.Client`,
// from an Elmish `Model`) and the SSR projection (`ToolUp.PublicRendering`,
// from authoritative live-session state) produce the SAME type, and
// `ToolUp.PublicRendering` does not (and must not) reference the client
// tier. GP 10: a type shared across the client/server boundary lives in a
// shared `<Compile>` file, never client-side-only.

/// Renderer-neutral namespace a hosted typed-tree resolves its data
/// bindings against. A hosted tree reads:
///
///   - `QueryResults["name"]` — host-projected domain values (the host
///     ran the query / projected the model; the tree only reads).
///   - `State["key"]`         — renderer-maintained UI state.
///
/// Values are boxed (`obj`) at this seam so the namespace stays neutral:
/// the host boxes a domain value on projection, the tree unboxes it (or
/// renders its `string` form) on resolution — the same sanctioned
/// type-erasure boundary the module registry and `DataTypeDisplay` use.
type HostBindingSources = {
    QueryResults: Map<string, obj>
    State: Map<string, obj>
}

[<RequireQualifiedAccess>]
module HostBindingSources =

    /// The shared empty namespace. Bound once at module load, so a
    /// pipeline that never projects resolves against this single interned
    /// value and allocates nothing per access (GP 13) — `empty` returns
    /// the same object reference every time.
    let empty: HostBindingSources = {
        QueryResults = Map.empty
        State = Map.empty
    }

    /// Project only host-projected domain values; UI `State` stays empty.
    let ofQueryResults (queryResults: Map<string, obj>) : HostBindingSources = {
        QueryResults = queryResults
        State = Map.empty
    }

    /// Project only renderer-maintained UI state; `QueryResults` stays empty.
    let ofState (state: Map<string, obj>) : HostBindingSources = {
        QueryResults = Map.empty
        State = state
    }

    /// Resolve a binding key: `QueryResults` wins over `State` (host data
    /// shadows UI state on a name clash), `None` when neither carries it.
    /// The single resolution rule both tier projections agree on, so a
    /// tree resolves identically whether the namespace came from the CSR
    /// or the SSR path.
    let tryResolve (key: string) (sources: HostBindingSources) : obj option =
        match Map.tryFind key sources.QueryResults with
        | Some _ as hit -> hit
        | None -> Map.tryFind key sources.State