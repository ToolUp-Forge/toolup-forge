// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Usage

open System
// Auth attributes (`RequiresClaim`) — this file lives in the
// `ToolUp.Platform.Usage` namespace, so the attribute namespace must
// be opened explicitly.
open ToolUp.Platform

// ─── Usage admin API (Fable.Remoting surface) ────────────────────
//
// `IUsageQueryApi` is the read-side and CSV-export surface for the
// admin dashboard (`Client/UsageDashboard.fs`) and any deployment-
// specific billing exporter. Write paths live elsewhere — emission
// happens through `IUsageLog.Record` from middleware / decorators /
// stores. This API is read-only; quota policy edits live in
// `IConfigStore` against the `_platform.usage` schema, not here.
//
// All methods are scoped server-side from `AccessContext.ScopeId`.
// Callers cannot pass an arbitrary scope — Fable.Remoting clients
// MUST surface their own resolved scope through the auth header
// only. The handler at `Server/UsageQueryApi.fs` overrides any
// caller-supplied scope with the resolved one before reading.
//
// Owner/Admin role gate is enforced by the handler via
// `TeamRoles.canWriteTeamConfig` (read access in admin contexts is
// gated by the same predicate as write access for billing UIs —
// usage is sensitive enough that Member-role users should not see
// the deployment's spend).

/// Date range for filtered queries / aggregations. Both endpoints
/// inclusive. UTC.
type UsageDateRange = { From: DateTime; To: DateTime }

/// Bucketed aggregate row returned to the dashboard. The key is
/// stringly-typed because `UsageGrouping`'s key shape varies
/// (yyyy-MM-dd / yyyy-MM / kind / userId).
type UsageAggregateRow = { Bucket: string; Quantity: decimal }

/// Fable.Remoting surface for the usage admin module. Mirrored on
/// the client by `IUsageQueryApi` proxy (Fable.Remoting record).
type IUsageQueryApi = {
    /// Filtered record query. Caller supplies optional resource kind
    /// + date range; the handler scopes to the caller's resolved
    /// `AccessContext.ScopeId`.
    [<RequiresClaim "scope">]
    Query: string option * UsageDateRange option -> Async<UsageRecord list>

    /// Aggregate by the requested grouping. Returns a list of
    /// `(bucket, sum)` rows so Fable serialisation is deterministic
    /// (Map serialises differently across providers).
    [<RequiresClaim "scope">]
    Aggregate: UsageGrouping -> Async<UsageAggregateRow list>

    /// CSV export, finance-ready. Headers:
    /// `RecordId,ScopeId,ResourceKind,Quantity,Unit,Origin,Timestamp,Metadata`.
    /// Returns the raw bytes so the client can trigger a download
    /// without an additional round trip. Optional date range; no
    /// filter on resource kind (full export by design — finance
    /// reconciles all kinds).
    [<RequiresClaim "scope">]
    ExportCsv: UsageDateRange option -> Async<byte[]>
}

module UsageQueryApi =
    /// Fable.Remoting route builder. Mirrors `IUsageQueryApi` member
    /// names; consumed on both the server (registration) and client
    /// (proxy construction).
    let routeBuilder (typeName: string) (methodName: string) =
        sprintf "/api/_platform/usage/%s" methodName