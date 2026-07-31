// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PresenceApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 622 — IPresenceApi Remoting handler ───────────────────────
//
// Builds a per-request `IPresenceApi` from the resolved scope + principal
// and the DI-registered Phase 442 substrate (`IPresenceTracker` +
// `IEntityLockStore`). Mirrors `UserSchemaApiHandler.userSchemaApi`.
//
// **Which substrate, and why:** Phase 442's, exclusively — the decision
// record lives at the shared seam (`Shared/PresenceTypes.fs`, "THE
// TWO-SUBSTRATE DECISION"). The short version: 442's pair is the only
// presence substrate `compose` registers, its `PresencePeer` is a strict
// superset of Phase 241's `PresenceEntry`, and locks exist only in 442.
//
// **Scope isolation (GP 4) is carried by the wire shape.** Neither
// `scopeId` nor `userId` appears in any `IPresenceApi` signature, so this
// handler is the only thing that decides either, and it decides them from
// the authenticated request. `forScope` below takes both explicitly and
// closes over them for the record's whole lifetime; a client has no
// syntax with which to name a different tenant or a different principal.
// That is why cross-scope presence is structurally impossible here rather
// than filtered — and why the scope-isolation tests drive `forScope` with
// two different scope ids over ONE shared substrate.

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.Items.TryGetValue "ToolUp.AccessContext" with
    | true, (:? AccessContext as ac) -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

let private resolveScopeId (ctx: HttpContext) (accessContext: AccessContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as scope) -> scope.ScopeId
    | _ -> accessContext.UserId

/// Build the whole API over an explicitly-supplied scope + principal.
///
/// Split out from `presenceApi` so the scope-isolation and lock-contention
/// behaviour is testable without an `HttpContext`: a test constructs two
/// records over the SAME tracker + lock store with two different
/// `scopeId`s (or the same scope and two different `userId`s) and asserts
/// what each can observe. That is the isolation property stated as
/// directly as it can be stated — no HTTP, no DI, no middleware in the
/// way of the assertion.
///
/// `displayName` is `None` from the composed path today: the SDK's
/// `AccessContext` carries a stable `UserId` but no human-readable name,
/// and inventing one from a client-supplied value would put unverified
/// text in front of every other member of the tenant. A deployment that
/// wants names decorates `IPresenceTracker` (the `NotifyingNarrativeStore`
/// / `RevokeOnIssuerRemovedStore` pattern) and resolves them from its own
/// directory on the way through.
let forScope
    (tracker: IPresenceTracker)
    (lockStore: IEntityLockStore)
    (scopeId: string)
    (userId: string)
    (displayName: string option)
    : IPresenceApi =
    {
        // Join / Move / Heartbeat folded into one idempotent announce —
        // see the `IPresenceApi.Heartbeat` doc comment for why the fold is
        // a correctness requirement and not an ergonomic nicety.
        Heartbeat =
            fun location -> async {
                let! roster = tracker.Roster scopeId

                match roster |> List.tryFind (fun p -> p.UserId = userId) with
                | None ->
                    // Absent — first announce, or the peer expired while
                    // the tab was backgrounded. Re-join at the location
                    // the client just told us, which is why the location
                    // rides every beat rather than only the first.
                    do! tracker.Join(scopeId, userId, displayName, location)
                | Some existing when existing.Location <> location -> do! tracker.Move(scopeId, userId, location)
                | Some _ ->
                    // Present and unmoved — the cheap path, and the only
                    // one that emits no `_platform.presence` event.
                    do! tracker.Heartbeat(scopeId, userId)

                return! tracker.Roster scopeId
            }
        Leave = fun () -> tracker.Leave(scopeId, userId)
        Roster = fun () -> tracker.Roster scopeId
        AcquireLock = fun entityRef -> lockStore.Acquire(scopeId, entityRef, userId, PresenceApi.lockTtl)
        RenewLock = fun entityRef -> lockStore.Renew(scopeId, entityRef, userId, PresenceApi.lockTtl)
        ReleaseLock = fun entityRef -> lockStore.Release(scopeId, entityRef, userId)
        LockHolder = fun entityRef -> lockStore.GetHolder(scopeId, entityRef)
    }

/// Build the `IPresenceApi` handler. Resolves the Phase 442 substrate +
/// the caller's scope / principal from DI + `HttpContext` per request.
///
/// The route is mounted only under `ServerConfig.Presence =
/// EnabledPresence` (`BuildRouteHandlers`), which is the same flag that
/// registers the substrate — so the two resolves below cannot observe a
/// null in a composed application.
let presenceApi (ctx: HttpContext) : IPresenceApi =
    let tracker =
        ctx.RequestServices.GetService(typeof<IPresenceTracker>) :?> IPresenceTracker

    let lockStore =
        ctx.RequestServices.GetService(typeof<IEntityLockStore>) :?> IEntityLockStore

    let accessContext = resolveAccessContext ctx
    let scopeId = resolveScopeId ctx accessContext
    forScope tracker lockStore scopeId accessContext.UserId None