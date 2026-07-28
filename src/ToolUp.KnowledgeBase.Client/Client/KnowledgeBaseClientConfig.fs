// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.KnowledgeBase.Client.KnowledgeBaseClientConfig

// Phase 1e — the `KnowledgeBaseMode` application seam. Mirrors
// `ToolUp.AI.Client.AIClientConfig.appendAssistantModule`: the
// companion owns the module-list transform, the platform shell owns
// nothing and takes no dependency on this package.
//
// Typical usage in an application's Client.fs:
//
//     open ToolUp.KnowledgeBase
//     open ToolUp.KnowledgeBase.Client
//
//     let kbMode = DefaultKnowledgeBase
//     let config, modules = KnowledgeBaseClientConfig.withKnowledgeBase kbMode config modules
//     Client.run config modules
//
// or, with the AI companion composed in as well (the transform is a
// plain config/module-list pair, so it layers under either entry
// point):
//
//     let config, modules = KnowledgeBaseClientConfig.withKnowledgeBase kbMode config modules
//     AIClientConfig.run aiMode config modules
//
// Swapping in a custom Knowledge Base is then a one-line change —
// `ExternalKnowledgeBase (MyConfluenceKb.register ())` — with the props
// imports and project reference left in place. See the
// `ExternalKnowledgeBase` case's doc comment for the three integration
// contracts an external implementation must honour.

open ToolUp.Platform
open ToolUp.KnowledgeBase

// ─── Module-list transformation ──────────────────────────────────

/// Append the Knowledge Base module pages based on mode. No-op when
/// mode is `NoKnowledgeBase`.
///
/// `DefaultKnowledgeBase` / `ConfiguredKnowledgeBase` append the
/// companion's team-side module plus the Platform-Admin content admin
/// (`_sdk.PlatformKnowledgeAdmin`). The admin's sidebar entry is
/// role-gated at render time by its "Platform Management" group, so the
/// unconditional append is safe on every deployment shape — the same
/// argument `AIClientConfig.appendAssistantModule` makes for
/// `PlatformAIKeysAdminUI`.
///
/// Appended (not prepended) so the companion's "Knowledge" group lands
/// after the app's declared groups, ahead of the SDK's own trailing
/// Admin built-ins — `Client.prepareModules` appends those last.
///
/// `ExternalKnowledgeBase` appends the deployment's module verbatim and
/// nothing else: the external implementation owns its own admin
/// surface, if it wants one.
let appendKnowledgeBaseModule (mode: KnowledgeBaseMode) (modules: ErasedModule list) : ErasedModule list =
    match mode with
    | NoKnowledgeBase -> modules
    | DefaultKnowledgeBase ->
        modules
        @ [ KnowledgeBaseView.create None; PlatformKnowledgeAdminUI.register () ]
    | ConfiguredKnowledgeBase cfg ->
        modules
        @ [ KnowledgeBaseView.create (Some cfg); PlatformKnowledgeAdminUI.register () ]
    | ExternalKnowledgeBase custom -> modules @ [ custom ]

// ─── Handler-registry transformation ─────────────────────────────

/// Install the companion's "Save to Knowledge Base" broker into
/// `ClientConfig.Handlers` when — and only when — the deployment runs
/// the companion's own Knowledge Base.
///
/// Integration contract 1 (see `ExternalKnowledgeBase`): an
/// `ExternalKnowledgeBase` supplies its own handler, so this transform
/// must not overwrite it — nor install the companion's, which would
/// route "Save to Knowledge Base" at a module that is not in the
/// sidebar. `NoKnowledgeBase` likewise leaves the field untouched.
///
/// An explicitly-supplied handler always wins, in every mode: a
/// consumer that set `Handlers.NarrativeCommitHandler` by hand has made
/// a deliberate choice (a wrapper that tees to two stores, a decorator
/// that adds provenance), and implicit wiring never overrides an
/// explicit one.
let withNarrativeCommitHandler (mode: KnowledgeBaseMode) (config: ClientConfig) : ClientConfig =
    match mode, config.Handlers.NarrativeCommitHandler with
    | (DefaultKnowledgeBase | ConfiguredKnowledgeBase _), None -> {
        config with
            Handlers = {
                config.Handlers with
                    NarrativeCommitHandler = Some KnowledgeBaseView.narrativeCommitHandler
            }
      }
    | _ -> config

// ─── The composed seam ───────────────────────────────────────────

/// Apply a `KnowledgeBaseMode` to a deployment's `ClientConfig` +
/// module list: injects the module(s) per mode and wires the
/// narrative-commit broker. Returns the pair to hand to `Client.run`
/// (or to `AIClientConfig.run` / `AIClientConfig.program`, which take
/// the same two arguments after their mode).
///
/// A deployment that never calls this is byte-for-byte unchanged
/// (GP 11) — there is no SDK-side default mode, and the hand-rolled
/// `KnowledgeBaseView.register ()` registration keeps working.
let withKnowledgeBase
    (mode: KnowledgeBaseMode)
    (config: ClientConfig)
    (modules: ErasedModule list)
    : ClientConfig * ErasedModule list =
    withNarrativeCommitHandler mode config, appendKnowledgeBaseModule mode modules