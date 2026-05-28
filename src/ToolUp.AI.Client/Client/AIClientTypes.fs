// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open Feliz

// ─── Client-only AI assistant branding + mode ────────────────────
//
// `AIAssistantBranding` (in `Shared/AITypes.fs`) carries `Icon: string`
// because the server's `AIAssistantServerConfig.Branding` nests it and
// must stay wire-friendly. The client side wants a typed
// `Icon: ReactElement` so app composition roots can pass a typed
// `vite-plugin-svgr`-imported component (e.g.
// `ToolUp.AIProviders.Claude.Icons.claude`). Splitting the two types
// keeps each side simple: the server's branding stays in shared types,
// the client's branding lives here in client-only territory.
//
// The DU case `ConfiguredAIAssistant` was previously parameterised
// with the shared `AIAssistantBranding`; it now takes the new client
// record. App callers see no friction — they construct
// `ConfiguredAIAssistant { Name = ...; Icon = ...; ShowSidePanel = ...
// }` exactly as before, and F# infers the new record type from the
// `Icon` field's type.

/// Client-side branding for the AI assistant module and side panel.
/// Carries a typed `ReactElement` for the icon so the sidebar can
/// render the SVG component inline (with `currentColor` cascading
/// from the parent's CSS color). Mirror of the shared
/// `AIAssistantBranding` minus the URL-string Icon.
type AIAssistantClientBranding = {
    /// Display name for the AI module page
    Name: string
    /// Typed icon component (typically a vite-plugin-svgr import)
    Icon: ReactElement
    /// Whether to show the conversation side panel
    ShowSidePanel: bool
}

/// Controls whether/how the AI assistant appears in the platform.
/// Mirrors the DataManagerMode pattern. Client-only — server-side
/// AI configuration goes through `AIAssistantServerConfig` directly.
type AIAssistantMode =
    /// No AI assistant — modules provide their own AI or none is needed.
    | NoAIAssistant
    /// Use the platform's built-in AI assistant UI (default name/icon).
    | DefaultAIAssistant
    /// Use the platform's built-in AI assistant with custom branding.
    | ConfiguredAIAssistant of AIAssistantClientBranding