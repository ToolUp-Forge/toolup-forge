// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.FormsInHandlerGates

open ToolUp.Platform

// ─── Phase 627.E declarations for `IFormApi` ─────────────────────────
//
// `IFormApi` is blanket-`[<AllowAnonymous>]` at the dispatcher, and the
// record's own header says why: the write gate is
// `FormApiHandler.withWriteGate` (Owner/Admin in Team mode, ungated in
// user-scoped / anonymous-session modes), so a dispatcher-level role
// requirement would refuse the single-user modes the surface is meant to
// serve. That is a legitimate shape — and it made all sixteen methods
// land in `AuthorizationSurface.anonymousReachable`, the list whose whole
// value is that a genuine open door stands out in it.
//
// These declarations move them into `gatedInHandler` instead, each
// carrying what its handler actually checks. See
// `ToolUp.Platform.PlatformInHandlerGates` for the platform-tier records
// and for what a declaration is and is not worth.
//
// **The three tiers below are three DIFFERENT checks, and flattening them
// into one rationale would be the easy lie.** Seven methods take the
// Owner/Admin write gate. Four are gated by the SUBMISSION's own
// authorship or by a query the handler narrows. Five are gated by
// `StorageScope` isolation alone — a real gate (GP 4: a caller cannot
// name another scope) but not a role check, and a reviewer reading
// "Owner/Admin" against `ListSchemas` would be reading a claim the
// handler does not make.

let private declare (componentId: ComponentId) (endpoint: string, rationale: string) : InHandlerGateDeclaration = {
    GatedComponent = componentId
    GatedEndpoint = endpoint
    GatedRationale = rationale
}

/// `IFormApi` — 16 methods, all `[<AllowAnonymous>]` at the dispatcher.
let formApi (componentId: ComponentId) : InHandlerGateDeclaration list =
    let ownerAdminWrite =
        "FormApiHandler.withWriteGate — Owner/Admin (TeamRoles.canWriteTeamConfig) in team modes, ungated in single-user modes where the caller owns the scope; every store call is scoped to the resolved StorageScope (GP 4)"

    let scopeOnly =
        "gated by StorageScope isolation alone — the handler resolves scopeId from the request and every store call takes it, so a caller cannot reach another scope (GP 4); no role check, deliberately, because the surface serves anonymous-session survey modes"

    [
        // ── Owner/Admin write gate ──
        "IFormApi.SaveSchema", ownerAdminWrite
        "IFormApi.DeleteSchema", ownerAdminWrite
        "IFormApi.GetAggregations", ownerAdminWrite
        "IFormApi.IssueTokens", ownerAdminWrite
        "IFormApi.CloseSurvey", ownerAdminWrite
        "IFormApi.DispatchInvitationsByEmail", ownerAdminWrite
        "IFormApi.RebuildAnalyserOutputs", ownerAdminWrite

        // ── Per-submission authorship / narrowed query ──
        "IFormApi.UpdateDraft",
        "handler refuses unless the submission's Author is the resolved caller; scope-bounded read first (GP 4)"

        "IFormApi.GetSubmission",
        "handler returns the submission to its Author, and otherwise only to a caller the write gate admits; scope-bounded read first (GP 4)"

        "IFormApi.ListSubmissions",
        "handler intersects the caller-supplied query with a visibility predicate before it reaches the store (members see their own, Owner/Admin see the scope)"

        "IFormApi.ApplyTransition",
        "handler forwards the resolved AccessContext to IWorkflowEngine.Apply, which authorises the transition against the workflow definition"

        // ── Scope isolation only ──
        "IFormApi.GetSchema", scopeOnly
        "IFormApi.ListSchemas", scopeOnly
        "IFormApi.ListSchemasOverview", scopeOnly
        "IFormApi.ListPossibleTransitions", scopeOnly
        "IFormApi.Submit", scopeOnly
    ]
    |> List.map (declare componentId)