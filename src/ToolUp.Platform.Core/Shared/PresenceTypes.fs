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

/// Selects whether `compose` registers the presence + soft-lock
/// collaboration substrate (Phase 442). Default `NoPresence` — no
/// `IPresenceTracker` / `IEntityLockStore` in DI, no heartbeat cost, no
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
    /// defaults (single-instance; a multi-instance deployment supplies
    /// distributed implementations over the distributed
    /// `INotificationChannel` companion — see the Phase 9c
    /// distributed-companion family). Presence / lock events fan out on
    /// the reserved `_platform.*` keys, scope-isolated per team.
    | EnabledPresence