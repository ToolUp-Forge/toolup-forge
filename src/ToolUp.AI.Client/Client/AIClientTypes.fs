// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open Feliz
open ToolUp.Platform

// ─── Client-only AI assistant branding + slot fills ──────────────
//
// `AIAssistantBranding` (in `Shared/AITypes.fs`) carries `Icon: string`
// because the server's `AIAssistantServerConfig.Branding` nests it and
// must stay wire-friendly. The client side wants a typed
// `Icon: ReactElement` so app composition roots can pass a typed
// `vite-plugin-svgr`-imported component (e.g.
// `ToolUp.AIProviders.Claude.Icons.claude`). Splitting the two types
// keeps each side simple: the server's branding stays in shared types,
// the client's branding lives here in client-only territory.

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

/// Phase 1074 — what fills each of the AI companion's shell slots. The
/// assistant family is three slots of the shell's slot table (declared
/// through `ShellSlotFills.Extensions`), so a deployment can empty or
/// replace any of them as it can every built-in: the assistant page, the
/// per-user AI settings, and the platform-admin keys page. Client-only —
/// server-side AI configuration goes through `AIAssistantServerConfig`.
type AISlotFills = {
    /// The AI assistant page. `Configured branding` renames it, sets its
    /// icon and decides whether the conversation side panel shows; the
    /// side panel shows for `Default` and `External`, and never for
    /// `Empty`.
    AIAssistant: SlotFill<AIAssistantClientBranding>
    /// The per-user AI settings page (bring-your-own key, model choice).
    /// Present beside the assistant whatever the deployment's key policy:
    /// under a platform-only policy it renders a managed-by-deployment
    /// notice, so enabling BYOK later is a policy flip, not a module-list
    /// change.
    AISettings: SlotFill<ModuleLabel>
    /// The platform-admin keys page. Its sidebar group's role gate hides
    /// it from non-admin callers.
    PlatformAIKeys: SlotFill<ModuleLabel>
}

/// The AI slots' fills.
module AISlotFills =
    /// The built-in assistant family, each page under its own name.
    let defaults: AISlotFills = {
        AIAssistant = SlotFill.Default
        AISettings = SlotFill.Default
        PlatformAIKeys = SlotFill.Default
    }

    /// The built-in family with the assistant rebranded (its name, its
    /// icon, and whether the side panel shows).
    let configured (branding: AIAssistantClientBranding) : AISlotFills = {
        defaults with
            AIAssistant = SlotFill.Configured branding
    }

    /// No AI page at all, and no side panel.
    let empty: AISlotFills = {
        AIAssistant = SlotFill.Empty
        AISettings = SlotFill.Empty
        PlatformAIKeys = SlotFill.Empty
    }