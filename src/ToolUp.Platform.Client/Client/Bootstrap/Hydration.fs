// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Bootstrap.Hydration

open ToolUp.Platform
open Elmish
open Elmish.React
open Fable.Core

// ─── Phase 57 — hydration-aware bootstrap ─────────────────────────
//
// When the FAKE `Prerender` target writes a prerendered HTML file
// for a route, it injects a `<meta name="toolup-prerendered"
// content="true">` marker into the document head. On page load,
// this module reads the marker:
//   - present  → mount via `Program.withReactHydrate` (React's
//                `hydrateRoot` against the existing DOM, preserving
//                the indexable first paint emitted by the build).
//   - absent   → mount via `Program.withReactSynchronous` (React's
//                `createRoot`, the standard SPA-only path).
//
// Stock SPA deployments stay byte-for-byte unchanged:
// `ClientConfig.PrerenderRoutes = []` ⇒ no prerendered HTML ⇒
// no marker ⇒ standard `createRoot` mount.

[<Emit("typeof document !== 'undefined' && document.querySelector('meta[name=\"toolup-prerendered\"]') !== null")>]
let private hasPrerenderMarker () : bool = jsNative

/// True when the current document was emitted by the Phase 57
/// prerender pass. Detected by the `<meta name="toolup-prerendered"
/// content="true">` marker the FAKE Prerender target injects.
/// Exposed so callers can branch on it (e.g. defer dev-tools wiring
/// until after hydration completes).
let isPrerendered () : bool = hasPrerenderMarker ()

/// Run the SDK shell with hydration awareness. Drop-in replacement
/// for `Client.run`: when the prerender marker is present, mounts
/// React via `hydrateRoot`; otherwise mounts via `createRoot`. The
/// rest of the boot sequence (PublicEntryDispatchers, request seam
/// install, auth bridge, boot-line summary log) runs identically.
///
/// PublicEntryDispatchers are consulted only on the non-prerender
/// branch — token-gated public-entry URLs (e.g. `/r/{token}` for
/// publishable forms) are never prerendered by design, so the
/// dispatcher path stays SPA-only.
let run (config: ClientConfig) (modules: ErasedModule list) =
    if isPrerendered () then
        Client.program config modules
        |> Program.withReactHydrate "elmish-app"
        |> Program.run
    else
        Client.run config modules