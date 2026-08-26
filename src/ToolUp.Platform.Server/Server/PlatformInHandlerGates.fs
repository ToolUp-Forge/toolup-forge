// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PlatformInHandlerGates

// ─── Phase 627.E declarations for the platform-tier API records ──────
//
// [Phase 627].E gave a record whose authorisation lives INSIDE the
// handler body a way to say so — `InHandlerGateDeclaration` +
// `AuthorizationSurface.resolveWithInHandlerGates`, which moves the
// declared endpoints out of the `anonymousReachable` headline and into
// `gatedInHandler`, carrying the stated rationale on each entry as a
// `gate:in-handler=…` requirement token.
//
// It shipped with the mechanism and no declarations, so the four records
// that motivated it kept overstating the anonymous surface by their whole
// method count: `IFormApi` 16, `JobApi` 8, `ModelExecutionApi` 7,
// `IModuleQueryBusApi` 1 — 32 of them, against a headline list whose
// entire value is that a genuine open door stands out in it. This module
// declares the three that live in the platform tier; `IFormApi`'s
// declarations live beside its own handler in `ToolUp.Forms.Server`,
// because a component declares its own gates.
//
// **What a declaration is worth, restated where the declarations are.**
// Nothing here VERIFIES anything. `GatedInHandler` sits BELOW an
// attribute gate in `AccessClassification.strength` precisely so that
// declaring one can never make a surface look better-defended than it is,
// and `resolveWithInHandlerGates` refines only entries that are already
// `AnonymousReachable`, so a stray declaration can never downgrade a real
// gate. What the rationale buys is that a reviewer who stops seeing an
// entry in the headline can find out why in one line — and that the line
// is checkable against the handler it names.
//
// **Every rationale below was read off the handler, not off the record's
// doc-comment.** Where a method is gated only by `StorageScope`
// isolation and not by a role check, the rationale says exactly that; an
// honest "scope only" is worth more here than a uniform claim of a role
// gate that three of these methods do not make.
//
// Nothing composes this — it is a value a surface audit passes to
// `resolveWithInHandlerGates`. A deployment that never runs one pays
// nothing (GP 13).

/// `JobApi` — 8 methods, all `[<AllowAnonymous>]` at the dispatcher.
///
/// Reads are open to any principal in the resolved scope; writes carry
/// `TeamRoles.canWriteTeamConfig` in `Team` / `MultiTeam` modes and are
/// ungated in single-user modes, where the caller owns its own scope.
/// Every method resolves `scopeId` from the caller's `AccessContext`, so
/// a caller cannot pass an arbitrary scope and reach another team's jobs
/// (GP 4).
let jobApi (componentId: ComponentId) : InHandlerGateDeclaration list =
    let scopeOnly =
        "handler resolves scopeId from the caller's AccessContext; a caller cannot name another team's scope (GP 4)"

    let ownerAdminWrite =
        "handler applies the Owner/Admin write gate (TeamRoles.canWriteTeamConfig) in team modes, plus scope resolution from the caller's AccessContext"

    [
        "JobApi.ListJobs", scopeOnly
        "JobApi.GetJob", scopeOnly
        "JobApi.GetRecentRuns", scopeOnly
        "JobApi.Schedule", ownerAdminWrite
        "JobApi.Cancel", ownerAdminWrite
        "JobApi.Disable", ownerAdminWrite
        "JobApi.Enable", ownerAdminWrite
        "JobApi.TriggerOnce", ownerAdminWrite
    ]
    |> List.map (fun (endpoint, rationale) -> {
        GatedComponent = componentId
        GatedEndpoint = endpoint
        GatedRationale = rationale
    })

/// `ModelExecutionApi` — 7 methods, all `[<AllowAnonymous>]` at the
/// dispatcher ([Phase 600]).
///
/// Team-scoped through the caller's resolved `AccessContext` (GP 4) and
/// audited with the submitter identity (GP 6); the mutating methods carry
/// the Owner/Admin write gate in team modes, and single-user modes own
/// their scope.
let modelExecutionApi (componentId: ComponentId) : InHandlerGateDeclaration list =
    let scopeOnly =
        "handler resolves the scope from the caller's AccessContext and reads only within it (GP 4); the submitter identity is audited (GP 6)"

    let ownerAdminWrite =
        "handler applies the Owner/Admin write gate in team modes on top of AccessContext scope resolution (GP 4), and audits the submitter identity (GP 6)"

    [
        "ModelExecutionApi.SubmitFit", ownerAdminWrite
        "ModelExecutionApi.SubmitFitBatch", ownerAdminWrite
        "ModelExecutionApi.RequestScore", ownerAdminWrite
        "ModelExecutionApi.GetOutcome", scopeOnly
        "ModelExecutionApi.QueryOutcomes", scopeOnly
        "ModelExecutionApi.ResolveLatestDatasetVersion", scopeOnly
        "ModelExecutionApi.ResolveDatasetVersion", scopeOnly
    ]
    |> List.map (fun (endpoint, rationale) -> {
        GatedComponent = componentId
        GatedEndpoint = endpoint
        GatedRationale = rationale
    })

/// `IModuleQueryBusApi` — the single `Ask` method, `[<AllowAnonymous>]`
/// at the dispatcher.
///
/// The client never passes an `AccessContext`; the server resolves one
/// per request (populated by `ScopeResolutionMiddleware`) and the bus's
/// own RBAC check against it is the gate. Denial surfaces as a typed
/// `PermissionDenied` rather than an HTTP 403, which is why it reads as
/// anonymous at the attribute layer and is not.
let moduleQueryBusApi (componentId: ComponentId) : InHandlerGateDeclaration list = [
    {
        GatedComponent = componentId
        GatedEndpoint = "IModuleQueryBusApi.Ask"
        GatedRationale =
            "IModuleQueryBus.Ask performs the RBAC check against the AccessContext the server resolved for the request; denial returns a typed PermissionDenied"
    }
]

/// Every platform-tier declaration under one component id — the shape a
/// surface audit over a composed deployment wants.
let all (componentId: ComponentId) : InHandlerGateDeclaration list =
    jobApi componentId
    @ modelExecutionApi componentId
    @ moduleQueryBusApi componentId