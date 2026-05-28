// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PrerenderApp.Client

open ToolUp.Platform
open ToolUp.Platform.Bootstrap

// ─── Phase 57 worked example — client composition ────────────────
//
// Demonstrates the three boot-sequence touch-points a forge SPA
// adds to opt into static prerendering:
//
//   1. `ClientConfig.PrerenderRoutes = SharedTypes.routes` —
//      declares the routes the build-time prerender pass renders.
//      The same list also drives `MetadataHook` so SPA navigation
//      between prerendered routes updates `<title>` / OG tags
//      live. Stock SPA deployments leave this `[]` (the default).
//
//   2. `Bootstrap.PrerenderExport.installEntryPoint config modules`
//      — registers the `globalThis.__toolup_prerender_render_doc`
//      function the FAKE Prerender target's Node script calls.
//      No-op in the browser (the `isBrowser` check
//      short-circuits), so the SPA pays nothing for it.
//
//   3. `Bootstrap.Hydration.run config modules` replacing
//      `Client.run` — mounts via React's `hydrateRoot` when the
//      prerender marker is present, `createRoot` otherwise.
//      Drop-in replacement; SPA-only routes (those NOT in
//      `PrerenderRoutes`) get exactly today's `createRoot` path.
//
// The minimal sample has no modules (`modules = []`) — its
// purpose is to demonstrate the seam, not to ship a working
// calculator. A real consumer wires their domain modules here as
// usual.

let private modules: ErasedModule list = []

let private config = {
    ClientConfig.defaults with
        AppName = "Acme Calculator"
        PrerenderRoutes = SharedTypes.routes
}

// Boot sequence. Order is load-bearing:
//   - `installEntryPoint` first so the Node prerender script's
//     `import()` of this bundle registers the renderer globals
//     before any browser-only mount path runs.
//   - `MetadataHook.install` second so it captures the first
//     `<title>` apply on initial hydration as a belt-and-braces
//     pass. No-op when `PrerenderRoutes = []`.
//   - `Hydration.run` last — mounts the SPA. In Node (build-time
//     prerender), this is reached AFTER `installEntryPoint` has
//     short-circuited via `isBrowser () = false`, and
//     `Hydration.run`'s underlying `Client.run` also fails fast
//     in Node because `Program.run`'s React mount requires a
//     DOM. The consumer can wrap `Hydration.run` in
//     `if PrerenderExport.isBrowser () then ...` for an
//     even-belt-and-braces shape; here the install-first
//     ordering is sufficient because the Node script reads the
//     globals between import-time and any browser-only path
//     actually firing.
PrerenderExport.installEntryPoint config modules

if PrerenderExport.isBrowser () then
    MetadataHook.install config.PrerenderRoutes
    Hydration.run config modules