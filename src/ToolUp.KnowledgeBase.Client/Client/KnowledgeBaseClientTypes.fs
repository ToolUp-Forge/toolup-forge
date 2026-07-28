// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.KnowledgeBase

open Feliz
open ToolUp.Platform

// ─── Client-only Knowledge Base branding + mode ───────────────────
//
// Phase 1e. Mirrors the `DataManagerMode` pattern (four cases: No /
// Default / Configured / External) so a deployment can substitute a
// custom Knowledge Base module — a Confluence sync, a Notion sync,
// custom dedup rules, a custom permission model — without stripping
// the companion's props imports and project reference back out.
//
// The DU lives in the companion's client tier, not in
// `ToolUp.Platform.Client`, for the same reason `AIAssistantMode`
// lives in `ToolUp.AI.Client`: the platform shell must not take a
// dependency on a companion, and `DefaultKnowledgeBase` has to
// construct the companion's own module. `ToolUp.AI.Client`'s
// `AIClientConfig.appendAssistantModule` is the precedent — the
// companion owns a module-list transform, the shell owns nothing.
//
// Server-side wiring stays explicit (the `KnowledgeApi` registration,
// `makeIngestionStatusObserver` into `composeWithRAG`, the opt-in
// `standingContextBuilder`): the mode governs *client* auto-injection
// only, exactly as `DataManagerMode` governs the sidebar module while
// `fileManagementApi` stays a composition-root concern.

/// Branding for the built-in Knowledge Base module's sidebar entry.
/// Mirrors `DataManagerConfig` — a typed `ReactElement` icon so the
/// sidebar renders the SVG component inline (with `currentColor`
/// cascading from the parent's CSS colour).
type KnowledgeBaseConfig = {
    /// Display name shown in the sidebar (default: "Knowledge Base").
    ///
    /// Note — unlike the SDK's own built-ins, the Knowledge Base module
    /// carries no reserved `_sdk.` id: its `Definition.Id` is derived
    /// from the name (`ClientModule.create` uses
    /// `Name.Replace(" ", "")`), so `register ()` has always produced
    /// `"KnowledgeBase"`. Renaming here therefore renames the module id
    /// too, which is the RBAC key in `ServerConfig.ModuleNames` — pair a
    /// rename with the matching server-side key change.
    Name: string
    /// Icon shown in the sidebar — typically `ToolUp.KnowledgeBase.Icons.knowledge`
    /// or any other typed `ReactElement`. Default:
    /// `ToolUp.KnowledgeBase.Icons.knowledge`.
    Icon: ReactElement
    /// Sidebar group the module appears under. `None` keeps the
    /// companion default ("Knowledge"); `Some g` overrides it (e.g. to
    /// merge the Knowledge Base into a deployment-specific group).
    Group: string option
}

/// Controls which Knowledge Base module the client shell shows, and
/// whether the companion's own narrative-commit broker is wired.
///
/// Applied by `ToolUp.KnowledgeBase.Client.KnowledgeBaseClientConfig.withKnowledgeBase`,
/// which returns the transformed `ClientConfig` + module list for
/// `Client.run` (or `AIClientConfig.run` — the transform composes with
/// either).
///
/// There is no SDK-side default: a deployment that never calls the
/// transform is byte-for-byte unchanged (GP 11), and `register ()`
/// remains a valid direct registration for consumers that predate this
/// DU.
type KnowledgeBaseMode =
    /// No Knowledge Base module in the sidebar. The narrative-commit
    /// handler is left exactly as the consumer supplied it (normally
    /// `None`, which hides `NarrativeRenderer`'s "Save to Knowledge
    /// Base" button by construction).
    | NoKnowledgeBase
    /// The companion's built-in Knowledge Base — the `/documents`,
    /// `/notes`, `/platform-library` and `/ai-context` pages under the
    /// "Knowledge" sidebar group, plus the Platform-Admin-gated
    /// `_sdk.PlatformKnowledgeAdmin` content admin. Installs the
    /// companion's `narrativeCommitHandler` unless the consumer already
    /// supplied one.
    | DefaultKnowledgeBase
    /// The companion's built-in Knowledge Base with a custom sidebar
    /// name / icon / group. Identical wiring to `DefaultKnowledgeBase`
    /// otherwise.
    | ConfiguredKnowledgeBase of KnowledgeBaseConfig
    /// A deployment-supplied module in place of the companion's — a
    /// Confluence sync, a Notion sync, a KB with custom dedup rules or
    /// a custom permission model. The companion's own module and
    /// content admin are NOT injected, and its `narrativeCommitHandler`
    /// is NOT installed, so nothing double-registers.
    ///
    /// **The three integration contracts an external Knowledge Base
    /// must honour.** These are the seams the rest of the SDK reaches
    /// the Knowledge Base through; an external implementation that
    /// skips one degrades a surface it does not own.
    ///
    /// 1. **NarrativeCommit handler.** Other modules offer "Save to
    ///    Knowledge Base" by rendering `NarrativeRenderer` and letting
    ///    it dispatch through the global `Toolup.NarrativeCommit`
    ///    broker — no module imports Knowledge Base types directly. An
    ///    external Knowledge Base supplies its own
    ///    `ClientConfig.Handlers.NarrativeCommitHandler` (a
    ///    `NarrativeCommitHandler` record whose `Submit` takes the
    ///    `NarrativeDocument` plus an overwrite flag and returns a
    ///    `NarrativeCommitResult`). Leave it `None` and the button
    ///    hides itself — the SDK never fails a build over it.
    /// 2. **`IIngestionStatusObserver` registration.** Server side, and
    ///    therefore outside this DU's remit: the composition root
    ///    passes an `IIngestionStatusObserver` (from
    ///    `ToolUp.RAG.IngestionTypes`) into `composeWithRAG`. The
    ///    observer's `OnChunkIndexed` / `OnChunkFailed` drive the
    ///    per-document ingestion status index and publish the status
    ///    notifications. Identity is by-value (`IngestionJob.DocumentId
    ///    : string`) and both members are `Async<unit>`, per the six
    ///    portability rules (GP 12).
    /// 3. **Notification-key contract.** The wire-format string
    ///    `"KnowledgeBase.IngestionStatus"` — exposed as
    ///    `[<Literal>] SharedTypes.IngestionStatusNotificationKey` — is
    ///    a *published* key, not an imported one: the AI assistant's
    ///    side panel subscribes to the literal directly so AI (the more
    ///    foundational companion) never depends on the Knowledge Base.
    ///    An external Knowledge Base either publishes under the same key
    ///    and inherits the stock AI side-panel surface, or defines its
    ///    own and accepts that the stock surface will not subscribe.
    | ExternalKnowledgeBase of ErasedModule