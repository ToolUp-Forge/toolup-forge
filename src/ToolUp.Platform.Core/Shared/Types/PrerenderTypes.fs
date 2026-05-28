// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 57 — static-prerender substrate types ─────────────────
//
// Types describing the routes the build-time prerender pass should
// emit indexable HTML for. Lives in Core so both the Fable-compiled
// Client (which consumes them via `ClientConfig.PrerenderRoutes` +
// the per-route metadata hook) AND the .NET-compiled
// `ToolUp.Platform.Build` (which consumes them via the FAKE
// `Prerender` target factory) can reference the same shape. Before
// the FAKE target follow-up the types lived inside
// `Client/SDK.ClientTypes.fs`; lifting them here removed the need
// for Build to mint a parallel mirror.

/// Open-graph + structured-data metadata applied to a prerendered
/// route's `<head>`. Drives both the indexable HTML emitted by the
/// build-time prerender pass and the runtime `MetadataHook` that
/// updates `<title>` / `<meta>` / OG tags as the SPA navigates.
type PrerenderMeta = {
    Title: string
    Description: string
    OpenGraph: Map<string, string>
    JsonLd: string option
}

/// A route the consumer wants statically prerendered. `Path` is the
/// URL path the renderer will write to `dist/{slugged-path}.html`.
/// `Meta` populates the emitted `<head>`. `InitStateKey` is an
/// optional opaque marker the prerender pass passes to the
/// consumer's render function so the view can be parameterised per
/// route — the SDK stays type-erased here because the SPA's `Model`
/// shape is module-specific.
type PrerenderRoute = {
    Path: string
    InitStateKey: string option
    Meta: PrerenderMeta
}

/// Outcome reported by the FAKE prerender target for each declared
/// route. Surfaced in build logs + CI exit codes so a broken
/// prerender fails the deployment instead of silently shipping an
/// empty SPA shell.
type PrerenderResult =
    | PrerenderSucceeded of route: PrerenderRoute * htmlByteSize: int
    | PrerenderFailed of route: PrerenderRoute * reason: string

module PrerenderMeta =
    /// Minimal metadata — title + description only, no OG, no JSON-LD.
    let basic (title: string) (description: string) = {
        Title = title
        Description = description
        OpenGraph = Map.empty
        JsonLd = None
    }