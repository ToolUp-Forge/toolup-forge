// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Read-only external flag-resolution layer (Phase 239). The Phase 5c
/// `IFeatureFlagStore` is the in-process read/write/GDPR-erase store and
/// the source of truth for flags a deployment manages itself; an
/// `IFlagSource` lets a deployment additionally back the *evaluation*
/// path with an external flag system (LaunchDarkly / Flagsmith /
/// Unleash via the OpenFeature standard) without that system having to
/// implement the write-centric store.
///
/// Resolution precedence (see `FlagEvaluator.createWithFlagSources`):
///   User → Team → Platform store override  →  IFlagSource(s)  →  declared default
/// A source is consulted only when no in-process scope set the key, and
/// before the declared default — so a deployment with no sources wired
/// is byte-for-byte unchanged (GP 11).
///
/// The declared `FeatureFlag` is passed in so the source knows the
/// expected shape (`Bool` vs `Variant`) and runs the correctly-typed
/// external evaluation. `Resolve` returns `None` to defer to the next
/// source / the declared default. Read-only by construction — no
/// `Set` / `Erase` (those stay on `IFeatureFlagStore`).
///
/// Portability (GP 12): identity by value (key + declared flag + context
/// record), async at the boundary, stateless between calls.
type IFlagSource =
    abstract Resolve: flag: FeatureFlag * ctx: AccessContext -> Async<FlagValue option>