// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.OfflineSyncApi

open ToolUp.Platform // forge-native auth + audit attributes
open ToolUp.Offline

// ─── Phase 24 — the sync wire contract ───────────────────────────────
//
// Deliberately TINY. Everything the offline companion needs from the
// server is "here is a mutation I made while disconnected; apply it or
// tell me it conflicts". Persistence of the queue itself is entirely
// client-side (IndexedDB) — the server holds no per-client pending
// state, which is what keeps this surface a pure function of the
// request plus the entity store.
//
// **Every method is `[<TenantScoped>]`.** A replayed mutation writes
// into an entity-store scope, so the caller must have an active team;
// the Phase 69d startup classifier refuses to boot on an unclassified
// method, and anonymous replay would be a tenant-isolation hole rather
// than a convenience. The `ScopeId` on the mutation is NOT trusted —
// the handler resolves the scope from the caller's `AccessContext` and
// refuses a mismatch (see `OfflineSyncHandler`).
//
// **Batch, not stream.** `ApplyBatch` exists because a reconnect
// typically has several mutations to replay and a per-mutation round
// trip over a link that has just come back is the worst possible
// shape. Outcomes are returned positionally paired with their
// `MutationId` so a partially-applied batch is still fully attributed
// — nothing is inferred from list position alone.

/// One mutation's result, paired back to the request that produced it.
type SyncResult = {
    MutationId: MutationId
    Outcome: SyncOutcome
}

/// `FetchCurrent` request payload — a tagged record rather than a
/// tuple so Fable.Remoting serialises the two arguments as named wire
/// fields (the `RescheduleRequest` convention in `ISchedulingApi`).
/// Record fields cannot carry named tuple elements in F#, so a bare
/// `entityType: string * entityId: string` is not expressible here.
type FetchCurrentRequest = { EntityType: string; EntityId: string }

/// The offline companion's server surface.
type IOfflineSyncApi = {
    /// Replay one mutation. Returns `Applied` / `Conflict` / `Rejected`
    /// — never raises for an ordinary conflict, because a conflict is
    /// an expected outcome of offline editing, not an error.
    [<TenantScoped>]
    [<Audit "OfflineMutationApplied">]
    Apply: QueuedMutation -> Async<SyncOutcome>

    /// Replay several mutations in the order given. Applied
    /// sequentially — a `Conflict` on one does NOT abort the rest,
    /// because the remaining mutations may touch unrelated entities
    /// and holding them hostage to an unresolved conflict is how a
    /// queue never drains.
    [<TenantScoped>]
    [<Audit "OfflineMutationApplied">]
    ApplyBatch: QueuedMutation list -> Async<SyncResult list>

    /// Fetch the server's current bytes for one entity, so the
    /// conflict resolver can re-read after the user picks a side
    /// without guessing at the version it should rebase onto.
    [<TenantScoped>]
    FetchCurrent: FetchCurrentRequest -> Async<byte[] option>
}

/// HTTP route prefix. Mirrors the `IFormApi` / `ISchedulingApi` /
/// `IAlgorithmCatalogApi` convention so the client proxy and the
/// server handler agree on the URL shape without naming routes
/// individually.
///
/// NOTE the `/api/` prefix is load-bearing for the reference service
/// worker: `examples/offline-sw.js` routes everything under `/api/`
/// network-first, and the sync endpoints must be reachable at the
/// moment the network returns — so the worker excludes this contract's
/// own routes from its queue-on-fail path. Changing this prefix means
/// changing that worker.
let routeBuilder (typeName: string) (methodName: string) : string =
    sprintf "/api/%s/%s" typeName methodName