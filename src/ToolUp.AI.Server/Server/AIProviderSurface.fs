// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AIProviderSurface

// ─── Phase 43.A — AI's keys into the canonical IProviderProfile ──
//
// The platform-wide IProviderProfile store (ToolUp.Platform.Providers)
// is surface-agnostic — it routes by free-form string keys. The AI
// assistant is one consumer of that store; these literals are the
// keys it uses, kept in one place so the factory, the settings
// handler, and the usage-metering wrapper all agree.
//
// Before the Phase 43.A cutover these same two literals lived
// privately inside the IUserAIConfigStore shim
// (DefaultUserAIConfigStore.aiSurface / .platformModelKey). The shim
// is removed; the values are unchanged, so a profile blob written by
// the shim resolves identically after the cutover (the byte-identical-
// persisted-state regression bar in the Phase 43 acceptance criteria).

/// Routing surface the AI assistant resolves its active provider
/// against: `IProviderProfile.ResolveEntry(scope, aiAssistant, None)`.
/// The shim mapped `AIUserConfig.ActiveProviderLabel` onto the routing
/// rule `{ Surface = "ai.assistant"; Context = None }`.
[<Literal>]
let aiAssistant = "ai.assistant"

/// `SurfaceModelOverrides` key carrying the user's preferred model for
/// the deployment's *platform-configured* provider (distinct from a
/// per-entry `ProviderEntry.Model`). The shim stored
/// `AIUserConfig.PlatformModelOverride` under this key.
[<Literal>]
let platformModelKey = "ai.platform"

/// Phase 70: `SurfaceProviderOverrides` key carrying the user's
/// preferred provider id among the deployment's wired
/// `PlatformDescriptors` (e.g. when the deployment wires Anthropic +
/// OpenAI + Gemini, the user picks one). `None` semantics → use the
/// first descriptor.
[<Literal>]
let platformProviderKey = "ai.platform.provider"