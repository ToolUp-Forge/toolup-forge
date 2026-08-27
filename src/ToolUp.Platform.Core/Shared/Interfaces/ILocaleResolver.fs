// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── ILocaleResolver ──────────────────────────────────────────────────
//
// Phase 12a. Resolves the active `LocaleCode` for an in-flight
// request. Lives in `ToolUp.Platform.Core` so both server-tier
// compose and client-tier views reference the same interface;
// server-side implementations read from `IConfigStore`, the
// `Accept-Language` header, and `AccessContext`; client-side
// implementations read from the prefetched `ClientModuleContext`
// platform config map.
//
// Returning a `LocaleCode` synchronously keeps the surface lean.
// Implementations that need an async lookup (e.g. reading
// per-team locale from a network-backed config store) should
// pre-fetch into memory and refresh on `_platform` config change
// — the contract here is "what is the locale right now?".

/// Resolves the active locale for a request / render frame. The
/// argument is a `Map<string, string>` of resolved platform-level
/// config (locale candidates live under
/// the platform locale config keys); the `AccessContext` carries
/// user / team identity; the `acceptLanguage` argument is the
/// browser's `Accept-Language` header verbatim. Implementations
/// pick whichever signal best fits the deployment's locale model.
///
/// Resolution-order recipe (default implementation):
///   1. team config (`_platform.locale`)
///   2. user profile (`_platform.userLocale.{userId}`)
///   3. browser `Accept-Language` first acceptable tag
///   4. fallback locale supplied at construction
type ILocaleResolver =
    abstract Resolve:
        platformConfig: Map<string, string> * accessContext: AccessContext * acceptLanguage: string option -> LocaleCode