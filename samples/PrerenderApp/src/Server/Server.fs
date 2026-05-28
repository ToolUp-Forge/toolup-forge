// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PrerenderApp.Server

open ToolUp.Platform

// ─── Phase 57 worked example — server composition ────────────────
//
// The SDK's `ConfigurePipeline.fs` already wires
// `PrerenderedRoutesMiddleware` ahead of `UseStaticFiles` for
// every Kestrel-hosted forge app — the consumer doesn't need to
// register anything additional. The middleware reads the same
// `ServerConfig.PublicPath` Vite writes to, so when the FAKE
// Prerender target emits `dist/individual.html`, an inbound
// `GET /individual` resolves to that file automatically.
//
// This sample exists to show what minimum `ServerConfig` shape
// is needed for the prerender path to work — the answer is
// "nothing extra". The middleware ships always-on (no-op when
// no `.html` files exist beyond `index.html`).

let config = {
    ServerConfig.defaults with
        // Vite writes its build to `src/Client/dist/`. The
        // prerender target uses the same path by default. The
        // PrerenderedRoutesMiddleware reads it from here.
        PublicPath = "src/Client/dist"
        // Port: sample default.
        Port = 13930
}

// A real consumer's server composition then calls
// `ServerApp.run config moduleRegistrations` — out of scope for
// this sample, which exists to demonstrate the Phase 57 seam.