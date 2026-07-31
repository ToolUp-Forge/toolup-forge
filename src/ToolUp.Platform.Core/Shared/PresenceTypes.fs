// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.EntityTypes

// ─── Presence + soft-lock collaboration primitives — Phase 442 ───────
//
// Two shared-tier value families for the "shared workspace" step: who
// else is *here* (presence with a location descriptor), and who is
// *editing* a given entity (advisory TTL soft-locks). Both ride the
// shipped INotificationChannel (Phase 6a) for live fan-out — no new
// transport — and both stay strictly awareness-level: this is not
// merge-free co-editing (OT / CRDT stays a separate, larger problem).
//
// Relationship to the existing presence roster (`IPresenceChannel`,
// PresenceStatus / PresenceEntry): that channel answers "who is in this
// scope, and are they fresh". This phase adds the *location descriptor*
// (which module / page a user is on) and publishes join / move / leave
// events on the reserved `_platform.*` notification-key namespace, so a
// view can react live rather than poll. The two are complementary; the
// names here are deliberately distinct (PresencePeer vs PresenceEntry)
// so both can coexist in the `ToolUp.Platform` namespace.
//
// Portability (GP 3 / GP 12): every type here is by value — strings,
// domain records, DateTime — never a live handle. `EntityLockRef` is a
// structural record key (no ordering promise across refs); lease expiry
// is pure timestamp math (`EntityLock.isExpired`), so a distributed
// implementation needs no background sweeper (GP 13) and no cross-shard
// ordering claim. Fable-safe: BCL primitives only, no server deps.

/// Where a participant is within a deployment — the "location
/// descriptor" a presence view renders next to a name ("Ada is on the
/// Reports page"). `Module` is the module/route id; `Page` is an
/// optional finer-grained sub-location (a tab, a record id) the module
/// owns the meaning of. Both are opaque strings the SDK never
/// interprets — it is sector-agnostic and must not name domain
/// concepts.
type PresenceLocation = { Module: string; Page: string option }

module PresenceLocation =
    /// A location naming only a module, with no finer sub-location.
    let ofModule (moduleId: string) : PresenceLocation = { Module = moduleId; Page = None }

/// One participant tracked by `IPresenceTracker` within a scope. Unlike
/// `PresenceEntry` (the `IPresenceChannel` roster), a peer carries the
/// location descriptor so a view can show *where* each collaborator is.
/// `LastSeen` is the last heartbeat / move UTC timestamp — the view
/// decides how stale is "away"; the tracker only excludes fully-expired
/// peers from the roster.
type PresencePeer = {
    UserId: string
    DisplayName: string option
    Location: PresenceLocation
    LastSeen: DateTime
}

/// The kind of roster change a presence event describes. `Joined` also
/// covers reconnect-re-acquire (Phase 24 offline interplay): a client
/// whose entry expired while offline re-Joins on reconnect.
[<RequireQualifiedAccess>]
type PresenceChange =
    | Joined
    | Moved
    | Left

/// Payload published on the reserved `_platform.presence` notification
/// key when a peer joins, moves, or leaves. Serialised to the
/// `CustomNotification` payload JSON so the client can deserialise the
/// same shared shape (GP 10). Delivery is scope-gated by the channel —
/// the event is published on the caller's own `scopeId`, so no other
/// team ever sees it (GP 4).
type PresenceEvent = {
    Change: PresenceChange
    Peer: PresencePeer
}

/// Value key identifying the entity a soft-lock guards. By value (GP 12
/// rule 1) — `EntityType` + `EntityId`, the same coordinates
/// `EntityRef<'T>` (Phase 19) carries, minus the phantom type and
/// version so it is a clean structural dictionary key with no ordering
/// promise across refs.
type EntityLockRef = {
    EntityType: string
    EntityId: EntityId
}

module EntityLockRef =
    /// Build a lock ref from an entity-store `EntityRef<'T>`'s value
    /// coordinates. `'T` is dropped — the lock keys on identity, not on
    /// the entity's downloaded shape or version.
    let ofEntityRef (ref: EntityRef<'T>) : EntityLockRef = {
        EntityType = ref.Type
        EntityId = ref.Id
    }

    /// Stable string form — `"<type>/<id>"`. Used as a client-side map
    /// key and in log lines; not a wire contract.
    let toKey (ref: EntityLockRef) : string =
        sprintf "%s/%s" ref.EntityType ref.EntityId

/// An advisory lease over an entity. `Holder` is the owning user id;
/// `AcquiredAt` / `ExpiresAt` bound the lease. Whether a lease is still
/// live is pure timestamp math against the current clock
/// (`EntityLock.isExpired`) — there is no background sweeper (GP 13),
/// so an expired lease is simply not returned as a holder and is
/// re-acquirable by anyone.
type LockLease = {
    Ref: EntityLockRef
    Holder: string
    AcquiredAt: DateTime
    ExpiresAt: DateTime
}

/// Outcome of an `Acquire` / `Renew`. `Acquired` carries the caller's
/// (new or refreshed) lease; `HeldByOther` carries the live lease held
/// by a *different* user — an acquire-conflict returns the current
/// holder and never blocks (GP 12: no blocking, no ordering promise).
[<RequireQualifiedAccess>]
type LockOutcome =
    | Acquired of LockLease
    | HeldByOther of LockLease

/// The kind of lock-lifecycle change a lock event describes. `Expired`
/// is emitted lazily — when a subsequent `Acquire` / `GetHolder`
/// observes that a lease has passed `ExpiresAt` — never by a timer, so
/// deployments that never touch a stale lock pay nothing.
[<RequireQualifiedAccess>]
type LockChange =
    | Taken
    | Released
    | Expired

/// Payload published on the reserved `_platform.lock` notification key.
/// Scope-gated exactly like `PresenceEvent`: published on the caller's
/// own `scopeId`, so a lock's holder is only ever revealed to that
/// team (GP 4).
type LockEvent = { Change: LockChange; Lease: LockLease }

/// Reserved `CustomNotification` keys for the collaboration events.
/// They live in the `_platform.*` reserved namespace (the same
/// convention as `_platform.notification_prefs` / `_platform.peer`) so
/// a module's own `CustomNotification` keys never collide. These are
/// notification *keys*, published on the caller's real per-scope
/// `scopeId` — NOT the cross-team `_platform` reserved scope
/// (`NotificationKind.PlatformReservedScope`); presence and lock state
/// must stay scope-isolated (GP 4).
module CollaborationTopics =
    [<Literal>]
    let Presence = "_platform.presence"

    [<Literal>]
    let Lock = "_platform.lock"

/// Pure lease-lifecycle math shared by the store implementation and its
/// contract tests. No state, no clock of its own — the caller supplies
/// `now`, so behaviour is deterministic under an injected clock.
module EntityLock =
    /// `true` once `now` has reached or passed the lease's expiry. An
    /// expired lease is re-acquirable by anyone and never reported as a
    /// holder.
    let isExpired (now: DateTime) (lease: LockLease) : bool = now >= lease.ExpiresAt

    /// `true` while the lease is still within its TTL window.
    let isLive (now: DateTime) (lease: LockLease) : bool = now < lease.ExpiresAt

// ─── THE TWO-SUBSTRATE DECISION — Phase 622.A ────────────────────────
//
// READ THIS BEFORE ADDING ANYTHING PRESENCE-SHAPED. The SDK carries two
// presence families, and every author who has come this way has had to
// rediscover that by hand:
//
//   * Phase 241 — `IPresenceChannel` (`Shared/IPresenceChannel.fs`) with
//     `PresenceEntry` / `PresenceStatus`. Join / Heartbeat / Leave /
//     Roster. Answers "who is in this scope, and are they fresh".
//   * Phase 442 — `IPresenceTracker` + `IEntityLockStore` (both
//     `Platform.Server`) with `PresencePeer` / `LockLease` (this file).
//     Adds the *location* descriptor and advisory soft-locks, and fans
//     out on the reserved `_platform.presence` / `_platform.lock` keys.
//
// **`IPresenceApi` below binds Phase 442, and only 442.** Three reasons,
// in the order that decided it:
//
//   1. **442 is the only one that is composed.** `EnabledPresence`
//      registers `IPresenceTracker` + `IEntityLockStore` into DI
//      (`ComposeNotifications.registerPresenceSubstrate`).
//      `IPresenceChannel` has NO compose site and NO DI registration
//      anywhere in the SDK — `InMemoryPresenceChannel` is constructed
//      only by its own test. An API over 241 would have nothing to
//      resolve at run time.
//   2. **442 is a strict superset of 241's information.** Every field of
//      `PresenceEntry` is recoverable from a `PresencePeer`
//      (`PrincipalId` ≡ `UserId`; `DisplayName` and `LastSeen` are
//      carried verbatim; `Status` is a pure function of `LastSeen`
//      against a freshness window). Nothing recovers `Location` from a
//      `PresenceEntry`. The projection is total in one direction only.
//   3. **Locks exist only in 442.** Half of this API has no 241
//      counterpart at all.
//
// **241 is NOT deleted, and this is deliberate.** `IPresenceChannel` is
// shipped public surface; removing it is a breaking change that buys
// nothing, and its client-tier hook (`PresenceClient`) is the reusable
// heartbeat/poll pump this phase's shell auto-mount runs on — see
// `PresenceClient.startPump`. What 241 no longer is: a substrate the SDK
// will build new server surface over. Treat it as the narrower
// roster-only interface a deployment may still implement directly;
// treat 442 as the substrate. **Do not add a third family** — extend
// `IPresenceTracker` / `IEntityLockStore` and this API instead.

// ─── IPresenceApi — the batteries-included platform surface (622.B) ───
//
// The half a consumer actually consumes: a Remoting contract over the
// DI-registered 442 substrate, mounted by `compose` when
// `ServerConfig.Presence = EnabledPresence`. Before this existed the SDK
// registered the substrate and left every deployment to hand-roll the
// wire — which the documented contract still permits (see
// `PresenceMode.EnabledPresence`), so this surface is purely additive.
//
// **Scope isolation is structural (GP 4), not a filter.** Note what is
// absent from every signature below: there is no `scopeId` parameter and
// no `userId` parameter. Both are resolved server-side from the caller's
// authenticated request (`PresenceApiHandler`), exactly as
// `IUserSchemaApi` does — so a client cannot name another tenant's scope
// or impersonate another principal, because the wire format gives it
// nowhere to say either. A roster read is therefore incapable of
// crossing a tenant boundary; presence leaking across scopes would be a
// wire-shape defect, not a forgotten `WHERE` clause.
//
// **Every method is `[<TenantScoped>]`** — the Phase 69d startup
// classifier refuses to start on an unclassified method, and presence is
// meaningless without a tenant-bound subject in any case.
//
// **The lease TTL is server-owned** (`PresenceApi.lockTtl`) and does not
// appear on the wire. A client that wants to hold a lock longer renews
// it — that is what `RenewLock` is for — rather than asking for an
// unbounded lease, so a hostile or buggy client cannot strand an entity
// behind a lock nobody can take.
type IPresenceApi = {
    /// "I am here, at this location." Idempotent, and the only announce
    /// verb — it folds Phase 442's `Join` / `Move` / `Heartbeat` into one
    /// call so the client never has to track which it owes the server:
    /// the handler joins when the caller is absent from the roster,
    /// moves when the caller's location changed, and heartbeats
    /// otherwise. Folding matters for correctness, not just ergonomics —
    /// `IPresenceTracker.Heartbeat` is a deliberate no-op for a peer that
    /// has expired out of the roster, so a client that only ever
    /// heartbeats would vanish after one missed window and never return.
    /// Returns the fresh roster, so a heartbeat and a roster read are one
    /// round trip.
    [<TenantScoped>]
    Heartbeat: PresenceLocation -> Async<PresencePeer list>

    /// Remove the caller from the scope roster (announces `Left`).
    /// Best-effort from the client's side — a peer that never calls this
    /// still expires on its own heartbeat window.
    [<TenantScoped>]
    Leave: unit -> Async<unit>

    /// The current non-expired roster for the caller's own scope.
    [<TenantScoped>]
    Roster: unit -> Async<PresencePeer list>

    /// Take (or refresh the caller's own) advisory lease over `ref` for
    /// the server-owned TTL. Never blocks: returns `HeldByOther` with the
    /// live lease when a different user holds it.
    [<TenantScoped>]
    AcquireLock: EntityLockRef -> Async<LockOutcome>

    /// Extend the caller's own lease — the client's auto-renew path.
    /// Re-acquires a free / expired slot; yields to a different live
    /// holder.
    [<TenantScoped>]
    RenewLock: EntityLockRef -> Async<LockOutcome>

    /// Release the caller's own lease. Idempotent, and a no-op when the
    /// lease is absent or held by someone else.
    [<TenantScoped>]
    ReleaseLock: EntityLockRef -> Async<unit>

    /// The current live holder of `ref` within the caller's scope, or
    /// `None` when free or expired.
    [<TenantScoped>]
    LockHolder: EntityLockRef -> Async<LockLease option>
}

/// Wire + timing constants for `IPresenceApi`, shared by the server
/// handler and the client auto-mount (GP 10) so the two cannot drift.
module PresenceApi =
    /// Remoting route builder — mounts the surface at
    /// `/api/IPresenceApi/<method>`, the same shape every other platform
    /// API uses.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"

    /// Server-owned advisory lease TTL. Deliberately not a wire
    /// parameter — see the `IPresenceApi` header. Comfortably longer than
    /// `lockRenewIntervalMs` so a renew that loses one round trip does
    /// not drop the lease mid-edit.
    let lockTtl = TimeSpan.FromSeconds 90.0

    /// How often the shell auto-mount announces presence. Must stay well
    /// inside `InMemoryPresenceTracker`'s 90-second expiry window so a
    /// single dropped beat never evicts a live peer.
    [<Literal>]
    let heartbeatIntervalMs = 20_000

    /// How often `useEntityLock` renews a held lease. Must stay well
    /// inside `lockTtl` for the same reason.
    [<Literal>]
    let lockRenewIntervalMs = 30_000

/// Selects whether `compose` registers the presence + soft-lock
/// collaboration substrate (Phase 442) and mounts the platform presence
/// API (Phase 622). Default `NoPresence` — no `IPresenceTracker` /
/// `IEntityLockStore` in DI, no route, no heartbeat cost, no
/// `_platform.presence` / `_platform.lock` fan-out; an existing
/// deployment that upgrades stays byte-for-byte identical until it opts
/// in (GP 11 + GP 13). Mirrors `PeerSubstrateMode` / `EntityStoreMode`
/// (binary, opt-in).
type PresenceMode =
    /// No presence / soft-lock infrastructure registered. The default —
    /// awareness features cost nothing for deployments that don't
    /// compose them in.
    | NoPresence
    /// Register the in-memory `IPresenceTracker` + `IEntityLockStore`
    /// defaults into DI (single-instance; a multi-instance deployment
    /// supplies distributed implementations over the distributed
    /// `INotificationChannel` companion — see the Phase 9c
    /// distributed-companion family). Presence / lock events fan out on
    /// the reserved `_platform.*` keys, scope-isolated per team.
    ///
    /// Phase 622 additionally mounts `IPresenceApi` over that substrate
    /// at `/api/IPresenceApi/*`. **The hand-mounted path is unchanged and
    /// still supported:** a deployment that already exposes its own
    /// module-owned API over the resolved `IPresenceTracker` /
    /// `IEntityLockStore` and mounts `PresenceContext.provider` itself
    /// keeps working exactly as before — the platform API is an
    /// additional route it need never call, and a self-mounted context
    /// provider nested inside the shell's wins for the views below it.
    /// The client hooks stay transport-parameterised by design precisely
    /// so both paths compose.
    ///
    /// The client half is gated separately by `ClientConfig.Presence`,
    /// which defaults to `NoPresence` — so enabling the server substrate
    /// never starts a heartbeat in an existing deployment's browser
    /// (GP 11). Set both to `EnabledPresence` for the batteries-included
    /// path.
    | EnabledPresence