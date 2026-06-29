// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── ITenantLifecyclePreview (Phase 54c) ─────────────────────────────
//
// Optional preview capability a lifecycle hook MAY implement *in
// addition to* `ITenantLifecycle`. The aggregator's `previewDeprovision`
// checks each hook for this interface: a hook that implements it
// contributes a count-only, mutation-free would-affect item; a hook that
// doesn't surfaces a `LifecyclePreviewItem.noPreview` ("no preview
// available") so the operator sees the gap explicitly.
//
// **Why a separate interface, not a member on `ITenantLifecycle`.**
// Adding an abstract member to `ITenantLifecycle` would break every
// existing implementor (the first-party hooks + every companion hook).
// A separate optional interface keeps the preview opt-in with a safe
// default — existing hooks need no change (GP 13) — which is the
// non-breaking realisation of "an optional `OnDeprovisionPreview` with a
// default that returns a skipped item".
//
// **Mutation-free contract.** An `OnDeprovisionPreview` implementation
// MUST NOT mutate anything — it counts what the corresponding
// `OnDeprovisioned` would affect (resolve the key id without destroying
// it, count members without evicting, `ListJobs` without cancelling,
// `IErasureHandler.Preview` not `Erase`). The contract pack's canary
// test asserts a preview leaves keys / jobs / cache / records
// byte-for-byte unchanged.

/// Optional, mutation-free preview capability for an `ITenantLifecycle`
/// hook (Phase 54c). Implement alongside `ITenantLifecycle` to contribute
/// a would-affect projection to `IPlatformTenantApi.PreviewDeprovision`.
type ITenantLifecyclePreview =
    /// Project — without mutating anything — what this hook's
    /// `OnDeprovisioned(scopeId, actorUserId)` would affect. Returns a
    /// count-only `LifecyclePreviewItem`. Must be side-effect-free.
    abstract OnDeprovisionPreview: scopeId: string * actorUserId: string -> Async<LifecyclePreviewItem>