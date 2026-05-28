// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AdPanel

open ToolUp.Platform

// ─── Phase 60 — analytics seam ────────────────────────────────────
//
// `IAdAnalyticsSink` is the optional consumer surface for first-party
// impression / click logging. AdSense's own dashboard owns the
// authoritative numbers; this seam exists for deployments wanting
// per-slot / per-route attribution in their own observability stack.
//
// Default `NoOpAdAnalyticsSink` is no-op so wiring it is a runtime
// no-cost choice. The `ServerSinkAdAnalytics` reference (below)
// POSTs to `/api/_platform/ads/impression` — only mount when
// `ServerConfig.AdAnalytics = EnabledAdAnalytics`.
//
// **Six-rule portability audit** (see
// [`docs/platform/portability-rules.md`](../../../../docs/platform/portability-rules.md)):
//
//   1. Identity by value. `AdImpression` / `AdClick` carry `SlotId`
//      + `AdClientId` + `OccurredAt` + `PathAtImpression` strings;
//      no `IActorRef` / `IGrainReference` / live handles cross the
//      boundary. Slot identity is a value the AdSense console mints,
//      not a runtime pointer.
//   2. Async at every boundary. Both methods return `Async<unit>`.
//      No fire-and-forget `Tell`-style signatures even though the
//      contract is best-effort — implementers swallow errors
//      internally, but the caller still gets an awaitable to chain
//      bootstrap teardown / smoke-test verification against.
//   3. Retry / supervision as data. The contract is best-effort by
//      design (ad-render path cannot fail when the sink is down).
//      Implementations swallow their own errors rather than surface
//      them through a callback or out-of-band exception channel —
//      see `ServerSinkAdAnalytics` for the canonical try/with that
//      keeps a sink outage from cascading into the render path.
//      A future retrying sink would express its policy as a record
//      (e.g. `AdAnalyticsRetryPolicy { MaxAttempts; Backoff }`) on
//      its `create` function, not as an `OnFailure` callback on this
//      interface.
//   4. Stateless between calls. `LogImpression` / `LogClick` receive
//      the complete event payload via parameters — no per-slot
//      state is carried across invocations. A distributed sink can
//      hash-partition by `SlotId` and route each call to any node.
//   5. No cross-shard ordering promises. Impressions and clicks
//      are independent observations. There is no implied ordering
//      between two events for the same `SlotId`, between an
//      impression and a click for the same render, or across
//      `ScopeId`s. Consumers requiring ordering reconstruct it from
//      `OccurredAt` timestamps client-side.
//   6. Precision N/A. The sink carries no timing primitives —
//      it logs externally-observed wall-clock events. The
//      timestamp field is provided by the caller (`OccurredAt`),
//      not minted by the sink.
//
// Contract pack: [`IAdAnalyticsSinkContract`](../../../ToolUp.Platform.Tests/Contracts/IAdAnalyticsSinkContract.fs)
// exercises this audit against `NoOpAdAnalyticsSink` and an in-
// memory recording fake. Any external impl (Server-backed, GA4,
// Plausible, …) binds to the same pack from its own InProcess test
// file.

type IAdAnalyticsSink =
    /// Fire-and-forget impression record. Implementations must
    /// swallow their own errors — a sink outage cannot fail the
    /// ad render.
    abstract LogImpression: AdImpression -> Async<unit>

    /// Fire-and-forget click record. Click events arrive via the
    /// optional click-redirect handler (out of v1 substrate scope —
    /// see Phase 60 follow-ons); today's path uses impressions only.
    abstract LogClick: AdClick -> Async<unit>

type NoOpAdAnalyticsSink() =
    interface IAdAnalyticsSink with
        member _.LogImpression(_event: AdImpression) = async.Return()
        member _.LogClick(_event: AdClick) = async.Return()