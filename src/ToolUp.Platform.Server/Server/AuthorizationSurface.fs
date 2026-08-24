// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Authorization-surface manifest (AuthorizationSurface) ────────────
//
// `CompositionManifest` (Phase 280) enumerates WHAT was composed;
// `EventTopology` (Phase 431) who talks to whom; `DataFootprint`
// (Phase 433) what is left at rest. None of them answers the question a
// security review actually opens with: **what does this application
// expose to the outside, and what does each entry require?**
//
// That answer has always existed — spread across route registrations,
// API-record attributes, tool declarations and job triggers — and it has
// only ever been recoverable by reading source. This makes it a value:
// per `ComponentId`, every externally reachable thing a component
// contributes, with its authorization requirement and a resolved
// **default-deny classification**. The headline is the
// `AnonymousReachable` set; an addition to it is the highest-signal
// entry the composition golden-file gate can produce.
//
// **Derived from live registrations, never hand-listed** (the Phase 293
// rule, repeated by 431 / 432 / 433). Four seams, each already owned by
// the SDK, each attributable without a new field on any shipped record:
//
//   1. **Route prefixes → routes.** `ServerModule.RoutePrefixes` names
//      the sub-trees a module owns, and `DefaultSurfaceRequirement` is
//      the Phase 66 admit set the `SurfaceEnforcementMiddleware` applies
//      across each. A module that adds a prefix surfaces here with no
//      change to this file.
//   2. **Exact route overrides → routes.**
//      `ServerModule.RouteSurfaceRequirements` are the per-`(method,
//      path)` admit sets that WIN over the prefix default. They are
//      always an explicit declaration; a prefix default is only an
//      explicit declaration when the module moved it off the strict SDK
//      fallback (`SurfaceRequirement.userOrTeam`).
//   3. **API-record fields → remoting endpoints.** The Phase 69d
//      classifier (`ToolUp.Remoting.Server.AuthClassifier`) already
//      normalises `[<RequiresRole>]` / `[<RequiresClaim>]` /
//      `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]`
//      — from BOTH attribute families — into the `AuthRequirement` data
//      the dispatcher evaluates per request. This file reads THAT, not
//      the raw attributes: the manifest reports exactly what the
//      dispatcher enforces, and the two cannot drift, because there is
//      only one classification.
//   4. **Tool + event-handler registrations.** `ServerModule.AITools`
//      is the AI-drivable surface (Phase 45's allowlist as one surface
//      class); a `ServerModule.JobHandlers` entry whose `Trigger` is
//      `OnEvent` is reachable by anything that can write that event.
//      Both sit at the Phase 113 default-deny floor until an
//      `IActionAuthorizer` policy grants them, which is what
//      `resolveWithPolicy` folds in.
//
// **Classification is three-valued, and the middle value is the honest
// one.** `ExplicitRequirement` — the registration declares a gate.
// `AnonymousReachable` — an unauthenticated caller reaches it.
// `InheritedDefaultDeny` — nothing was declared, so the composition's
// fail-closed floor applies. The third is not a defect: it is the
// correct posture for most surfaces, and saying so is what stops a
// reader from mistaking "no declaration" for "no gate" in either
// direction.
//
// **What the derivation cannot see, it does not guess at.** A module's
// `Handlers` are Giraffe closures — their routes are not enumerable at
// all, which is why the route surface is recovered from the DECLARED
// prefixes / overrides (the same honesty `ModuleSurface` records under
// `Opaque`). A remoting endpoint likewise needs its API record TYPE
// (`ofApiRecord`), because `ServerApp` accumulates built handlers, not
// contracts. An undeclared route is invisible here — and it is also
// invisible to the Phase 66 registry, so it runs under the strict
// global fallback rather than under nothing.
//
// **Generic substrate (GP 1); zero cost when unused (GP 11 / GP 13).**
// The shape carries no vendor or domain type — only `ComponentId`s and
// strings drawn from the registrations themselves — nothing is built
// until a caller asks, and nothing is registered into DI at all. A
// deployment that never introspects composes byte-for-byte what it did
// before.

/// The transport an exposed surface is reachable over. Namespaced case
/// names (`Exposed*`) because `Route` / `Surface` are already crowded
/// tokens in this namespace.
type ExposedSurfaceKind =
    /// An HTTP route sub-tree or exact `(method, path)` a module owns.
    | ExposedRoute
    /// One field of a remoting API record — a dispatchable method.
    | ExposedRemotingEndpoint
    /// A tool the AI agent loop can invoke on a caller's behalf.
    | ExposedAiTool
    /// A job handler fired by an `IEventStore` write of a named type.
    | ExposedEventHandler

/// How the composition gates one exposed surface.
type AccessClassification =
    /// The registration declares a gate — a role, a claim, a tenant
    /// binding, an admit set moved off the strict fallback, or a
    /// matched action-policy rule.
    | ExplicitRequirement
    /// Nothing was declared for this surface, so the composition's
    /// fail-closed default-deny floor applies. Not a defect — the
    /// correct posture for most surfaces, said out loud.
    | InheritedDefaultDeny
    /// An unauthenticated caller reaches it. The headline set.
    | AnonymousReachable

/// One externally reachable thing a component contributes, with its
/// resolved authorization posture.
type ExposedSurface = {
    /// The stable Phase 279 identity of the contributing component.
    Component: ComponentId
    /// Which transport it is reachable over.
    ExposedKind: ExposedSurfaceKind
    /// The addressable identity: a route prefix, `METHOD /path`,
    /// `IApi.Method`, a tool name, a job handler name.
    Endpoint: string
    /// Normalised requirement tokens, sorted ordinally — `role:<r>`,
    /// `claim:<c>` / `claim:<c>=<v>`, `tenant`, `subject:<Kind>`,
    /// `permission:<module>/<perm>`, `tier:premium`, `event:<type>`.
    /// Strings rather than a DU so a persisted baseline never depends
    /// on a union shape round-tripping through a serialiser.
    Requires: string list
    /// The resolved default-deny classification.
    Access: AccessClassification
}

/// The derived authorization surface of a composition: every exposed
/// entry, keyed by stable `ComponentId` and held in a deterministic
/// order, so two compositions that registered the same things in a
/// different order produce the same surface.
///
/// The field is `Exposed`, not `Surfaces` or `Entries`, deliberately —
/// the Phase 431 field-inference lesson: F#'s last-declared-wins
/// inference re-points every unannotated construction sharing a full
/// field-name set at whichever record compiled later.
type AuthorizationSurface = { Exposed: ExposedSurface list }

/// The structural difference between two surfaces, keyed by
/// `(Component, ExposedKind, Endpoint)` — never by list position, so
/// re-ordering registrations diffs to empty.
///
/// A separate delta type rather than fields grown onto
/// `CompositionDelta` (Phase 286), for the reason recorded on
/// `ClassifiedCompositionRule` and repeated by `EventTopologyDelta` /
/// `DataFootprintDelta`: growing a shipped F# record breaks its
/// constructor. The deltas join on `ComponentId`, which they all carry.
type AuthorizationSurfaceDelta = {
    SurfacesAdded: ExposedSurface list
    SurfacesRemoved: ExposedSurface list
    /// `(before, after)` pairs whose authorization got WEAKER — the
    /// class a security review must never miss.
    RequirementsWeakened: (ExposedSurface * ExposedSurface) list
    /// `(before, after)` pairs whose authorization got stronger.
    RequirementsStrengthened: (ExposedSurface * ExposedSurface) list
}

/// How loud a surface delta is. The whole point of the manifest: a new
/// anonymous-reachable endpoint and a weakened requirement are a
/// different KIND of change from an added guarded endpoint, and the
/// golden-file failure says which it got.
type AuthorizationDriftSeverity =
    /// The two surfaces are structurally identical.
    | NoAuthorizationDrift
    /// Something moved, but nothing became more reachable — an added
    /// guarded surface, a removal, a strengthened requirement.
    | ReviewableAuthorizationDrift
    /// A new anonymous-reachable entry, or a weakened requirement. The
    /// attack surface grew.
    | CriticalAuthorizationDrift

/// The surface projected to plain strings — the shape a golden file or
/// an external tool persists, so neither depends on the F# union
/// representations round-tripping through a serialiser. The field names
/// differ from `ExposedSurface`'s for the field-inference reason
/// recorded on `AuthorizationSurface.Exposed`.
type AuthorizationSurfaceWireEntry = {
    ComponentIdentity: string
    KindLabel: string
    EndpointPath: string
    Requirements: string list
    Classification: string
}

/// The `ExposedSurfaceKind` vocabulary as stable strings — what the
/// wire projection and the readable delta print. Round-trips exactly.
module ExposedSurfaceKind =

    /// The persisted label for a kind.
    let label (kind: ExposedSurfaceKind) : string =
        match kind with
        | ExposedRoute -> "route"
        | ExposedRemotingEndpoint -> "remoting-endpoint"
        | ExposedAiTool -> "ai-tool"
        | ExposedEventHandler -> "event-handler"

    /// Read a persisted label back. An unrecognised label reads as a
    /// route — the most conservative kind for a surface of unknown
    /// provenance, since a route is the one every reviewer inspects.
    let ofLabel (raw: string) : ExposedSurfaceKind =
        match (if isNull raw then "" else raw.Trim()) with
        | "remoting-endpoint" -> ExposedRemotingEndpoint
        | "ai-tool" -> ExposedAiTool
        | "event-handler" -> ExposedEventHandler
        | _ -> ExposedRoute

    /// The Phase 113 `ActionDescriptor.Kind` an entry of this kind maps
    /// onto, so an `ActionPolicy` written in the host's action
    /// vocabulary can be resolved against the surface.
    let actionKind (kind: ExposedSurfaceKind) : string =
        match kind with
        | ExposedRoute -> "navigate"
        | ExposedRemotingEndpoint -> "call"
        | ExposedAiTool -> "ai-tool"
        | ExposedEventHandler -> "dispatch"

/// The `AccessClassification` vocabulary + its strength ordering — the
/// single place "weaker" is defined, so the diff and the severity can
/// never disagree about which direction a change went.
module AccessClassification =

    /// The persisted label for a classification.
    let label (access: AccessClassification) : string =
        match access with
        | ExplicitRequirement -> "explicit-requirement"
        | InheritedDefaultDeny -> "inherited-default-deny"
        | AnonymousReachable -> "anonymous-reachable"

    /// Read a persisted label back. An unrecognised label reads as
    /// `InheritedDefaultDeny` — the honest "nothing was declared here"
    /// value, never `AnonymousReachable` (which would fabricate a
    /// headline finding) and never `ExplicitRequirement` (which would
    /// claim a gate that may not exist).
    let ofLabel (raw: string) : AccessClassification =
        match (if isNull raw then "" else raw.Trim()) with
        | "explicit-requirement" -> ExplicitRequirement
        | "anonymous-reachable" -> AnonymousReachable
        | _ -> InheritedDefaultDeny

    /// How closed a classification is. Higher is stronger; a drop is a
    /// weakening.
    let strength (access: AccessClassification) : int =
        match access with
        | AnonymousReachable -> 0
        | InheritedDefaultDeny -> 1
        | ExplicitRequirement -> 2

/// Derivation, queries, policy resolution, and diff over a
/// composition's authorization surface. Every function here is pure
/// over already-collected registrations — nothing is built until a
/// caller asks (GP 13), and nothing is registered into DI ever.
module AuthorizationSurface =

    /// The surface of a composition that exposes nothing.
    let empty: AuthorizationSurface = { Exposed = [] }

    // ── assembly ──────────────────────────────────────────────────────

    let private compareOrdinal (a: string) (b: string) = String.CompareOrdinal(a, b)

    let private ordered (entries: ExposedSurface list) : ExposedSurface list =
        entries
        |> List.sortWith (fun a b ->
            match compareOrdinal a.Component.Value b.Component.Value with
            | 0 ->
                match
                    compareOrdinal (ExposedSurfaceKind.label a.ExposedKind) (ExposedSurfaceKind.label b.ExposedKind)
                with
                | 0 -> compareOrdinal a.Endpoint b.Endpoint
                | c -> c
            | c -> c)

    let private sortedTokens (tokens: string list) : string list =
        tokens
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> List.map _.Trim()
        |> List.distinct
        |> List.sortWith compareOrdinal

    /// The single assembly point every derivation funnels through, so
    /// all of them share one ordering and one collapse rule.
    ///
    /// **Duplicates collapse to the MOST PERMISSIVE entry.** Two
    /// registrations addressing one endpoint mean the endpoint is
    /// reachable by the weaker of the two, and a manifest of the attack
    /// surface that reported the stronger one would understate it. The
    /// sort is stable, so an exact tie keeps the first-registered.
    let private assemble (entries: ExposedSurface list) : AuthorizationSurface =
        let collapsed =
            entries
            |> List.map (fun e -> {
                e with
                    Requires = sortedTokens e.Requires
            })
            |> List.groupBy (fun e -> e.Component, e.ExposedKind, e.Endpoint)
            |> List.map (fun (_, group) ->
                group
                |> List.sortBy (fun e -> AccessClassification.strength e.Access)
                |> List.head)

        { Exposed = ordered collapsed }

    /// Union two surfaces; a duplicate endpoint collapses per
    /// `assemble`. Associative and commutative up to the collapse rule,
    /// so merge order never changes the result.
    let merge (a: AuthorizationSurface) (b: AuthorizationSurface) : AuthorizationSurface =
        assemble (a.Exposed @ b.Exposed)

    /// `merge` over a list — the shape a composition root uses to fold
    /// the module-derived half together with the API-record half.
    let mergeAll (surfaces: AuthorizationSurface list) : AuthorizationSurface =
        surfaces |> List.collect _.Exposed |> assemble

    /// Record one entry. Additive — everything already present is kept.
    let withSurface (entry: ExposedSurface) (surface: AuthorizationSurface) : AuthorizationSurface =
        assemble (entry :: surface.Exposed)

    // ── requirement vocabulary ────────────────────────────────────────

    /// A Phase 66 admit set as sorted `subject:<Kind>` tokens.
    /// `Set<SubjectKind>` iterates in tag order, so the projection is
    /// deterministic before the sort and stable after it.
    let subjectTokens (requirement: SurfaceRequirement) : string list =
        requirement.AcceptedSubjects
        |> Set.toList
        |> List.map (fun kind -> "subject:" + string kind)
        |> sortedTokens

    /// Whether an admit set lets an unauthenticated caller through.
    let admitsAnonymous (requirement: SurfaceRequirement) : bool =
        requirement.AcceptedSubjects.Contains AnonymousKind

    /// Classify a route's admit set. `declared` says whether the admit
    /// set is a DECLARATION or the strict SDK fallback arriving by
    /// default — the distinction between "this module chose a gate" and
    /// "nothing was said, so the floor applies". An anonymous-admitting
    /// set is the headline classification regardless of which it was.
    let classifyAdmitSet (declared: bool) (requirement: SurfaceRequirement) : AccessClassification =
        if admitsAnonymous requirement then AnonymousReachable
        elif declared then ExplicitRequirement
        else InheritedDefaultDeny

    /// A Phase 113 `ActionRequirement` as sorted tokens.
    let rec private policyTokens (requirement: ActionRequirement) : string list =
        match requirement with
        | ActionRequirement.Permission(moduleName, permission) -> [
            sprintf "permission:%s/%s" moduleName (string permission)
          ]
        | ActionRequirement.PremiumTier -> [ "tier:premium" ]
        | ActionRequirement.All nested -> nested |> List.collect policyTokens
        | ActionRequirement.Unrestricted -> [ "unrestricted" ]

    /// Whether a policy requirement grants without asking anything of
    /// the caller. `All []` grants vacuously — every nested requirement
    /// grants — which is exactly the reading `ActionPolicy` documents,
    /// and exactly the shape that would otherwise hide behind a
    /// reassuring-looking rule.
    let rec private grantsUnconditionally (requirement: ActionRequirement) : bool =
        match requirement with
        | ActionRequirement.Unrestricted -> true
        | ActionRequirement.All nested -> nested |> List.forall grantsUnconditionally
        | _ -> false

    // ── derivation from the live module registrations (438.A) ─────────

    /// A module's resolved identity: its explicit `ComponentId` when it
    /// declares one, else the `Name`-derived id (GP 11 — byte-for-byte
    /// the identity every other introspection surface already uses).
    let componentIdOf (serverModule: ServerModule) : ComponentId =
        serverModule.ComponentId
        |> Option.defaultValue (ComponentId.ofModule serverModule.Name)

    /// Derive the surface a set of composed modules exposes, from their
    /// own registrations. `attribute` resolves a module to the identity
    /// its entries are recorded under; the default `componentIdOf`
    /// reproduces the existing convention exactly, and it is a parameter
    /// only so a composition root that keys components differently (a
    /// multi-tenant shell, a test harness) can say so without this file
    /// learning about it.
    ///
    /// Nothing here is a hand-listed table: a module that adds a route
    /// prefix, a route override, an AI tool, or an `OnEvent` job
    /// declaration surfaces with no change to this function.
    let ofModulesWith (attribute: ServerModule -> ComponentId) (modules: ServerModule list) : AuthorizationSurface =
        let perModule (serverModule: ServerModule) = [
            let owner = attribute serverModule

            // Seam 1 — declared route prefixes under the module's
            // default admit set. The default is a DECLARATION only when
            // the module moved it off the strict SDK fallback.
            let defaultDeclared =
                serverModule.DefaultSurfaceRequirement <> SurfaceRequirement.userOrTeam

            for prefix in serverModule.RoutePrefixes do
                if not (String.IsNullOrWhiteSpace prefix) then
                    {
                        Component = owner
                        ExposedKind = ExposedRoute
                        Endpoint = prefix.Trim()
                        Requires = subjectTokens serverModule.DefaultSurfaceRequirement
                        Access = classifyAdmitSet defaultDeclared serverModule.DefaultSurfaceRequirement
                    }

            // Seam 2 — exact `(method, path)` overrides. Always an
            // explicit declaration; they win over the prefix default at
            // resolution time, and they win here too.
            for (httpMethod, path), requirement in serverModule.RouteSurfaceRequirements do
                {
                    Component = owner
                    ExposedKind = ExposedRoute
                    Endpoint = httpMethod.Trim().ToUpperInvariant() + " " + path.Trim()
                    Requires = subjectTokens requirement
                    Access = classifyAdmitSet true requirement
                }

            // Seam 3 — the AI-drivable allowlist (Phase 45) as one
            // surface class. A tool sits at the Phase 113 default-deny
            // floor until an `ActionPolicy` grants it — see
            // `resolveWithPolicy`.
            for definition, _ in serverModule.AITools do
                if not (String.IsNullOrWhiteSpace definition.Name) then
                    {
                        Component = owner
                        ExposedKind = ExposedAiTool
                        Endpoint = definition.Name.Trim()
                        Requires = []
                        Access = InheritedDefaultDeny
                    }

            // Seam 4 — event-triggered job handlers. Reachable by
            // anything that can write the named event type into the
            // scope, so the event type IS the reachability condition and
            // is recorded as the requirement token.
            for declaration in serverModule.JobHandlers do
                match declaration.Trigger with
                | OnEvent eventType when not (String.IsNullOrWhiteSpace eventType) -> {
                    Component = owner
                    ExposedKind = ExposedEventHandler
                    Endpoint = declaration.HandlerName
                    Requires = [ "event:" + eventType.Trim() ]
                    Access = InheritedDefaultDeny
                  }
                | _ -> ()
        ]

        modules |> List.collect perModule |> assemble

    /// `ofModulesWith` under the default attribution — the call a
    /// composition root makes.
    let ofModules (modules: ServerModule list) : AuthorizationSurface = ofModulesWith componentIdOf modules

    // ── derivation from a remoting API record (438.A, seam 3) ─────────

    /// Normalise one Phase 69d classification into this vocabulary. The
    /// dispatcher's own classifier produced it, from BOTH attribute
    /// families, so the manifest reports exactly what is enforced.
    let private ofMethodClassification
        (classification: ToolUp.Remoting.Server.MethodClassification)
        : string list * AccessClassification =
        match classification with
        // The auth-context resolver is not consulted at all — the
        // method dispatches regardless of caller identity.
        | ToolUp.Remoting.Server.Public -> [ "public-endpoint" ], AnonymousReachable
        // Anonymous callers are admitted alongside authenticated ones.
        | ToolUp.Remoting.Server.Anonymous -> [ "allow-anonymous" ], AnonymousReachable
        | ToolUp.Remoting.Server.RequiresAuth requirements ->
            let tokens =
                requirements
                |> List.map (fun requirement ->
                    match requirement with
                    | ToolUp.Remoting.Server.RoleRequired role -> "role:" + role
                    | ToolUp.Remoting.Server.ClaimRequired(claim, Some value) -> "claim:" + claim + "=" + value
                    | ToolUp.Remoting.Server.ClaimRequired(claim, None) -> "claim:" + claim
                    | ToolUp.Remoting.Server.TenantRequired -> "tenant")

            tokens, ExplicitRequirement
        // The dispatcher REFUSES TO START on an unclassified method, so
        // this cannot reach production. It is reported rather than
        // dropped because a surface derived from a type under
        // development is exactly where a reviewer wants to see it — and
        // at the fail-closed classification, never as reachable.
        | ToolUp.Remoting.Server.Unclassified -> [ "unclassified" ], InheritedDefaultDeny

    /// Derive the remoting half of a component's surface from an API
    /// record TYPE. `ServerApp` accumulates BUILT handlers rather than
    /// contracts, so the type is what a composition root passes — the
    /// same value it hands `Api.make`.
    ///
    /// Derived, never hand-listed: a field added to the record surfaces
    /// here with no change to this function, carrying whichever
    /// authorization attribute it declares (or `unclassified`, which the
    /// dispatcher refuses to start on).
    let ofApiRecordType (componentId: ComponentId) (apiType: Type) : AuthorizationSurface =
        if isNull apiType then
            empty
        else
            ToolUp.Remoting.Server.AuthClassifier.classify apiType
            |> Map.toList
            |> List.map (fun (fieldName, classification) ->
                let tokens, access = ofMethodClassification classification

                {
                    Component = componentId
                    ExposedKind = ExposedRemotingEndpoint
                    Endpoint = apiType.Name + "." + fieldName
                    Requires = tokens
                    Access = access
                })
            |> assemble

    /// `ofApiRecordType` with the API record supplied as a type
    /// argument — the call site a composition root usually wants.
    let ofApiRecord<'TApi> (componentId: ComponentId) : AuthorizationSurface =
        ofApiRecordType componentId typeof<'TApi>

    // ── default-deny resolution against a Phase 113 policy (438.B) ────

    /// Fold an `IActionAuthorizer` policy into the surface: every entry
    /// still sitting at the default-deny floor is looked up in the
    /// policy under its kind's `ActionDescriptor.Kind`, and a matching
    /// rule replaces the floor with the rule's requirement.
    ///
    /// **Only floor entries are refined.** An entry that already
    /// declares a gate keeps it, and an entry already known to be
    /// anonymous-reachable is never quietly upgraded by a policy rule
    /// that does not actually run in front of it.
    ///
    /// A rule granting `Unrestricted` (or an `All` of nothing) resolves
    /// to `AnonymousReachable` — that is what "grant unconditionally"
    /// means, and it is precisely the dev-only shape
    /// `ActionAuthorizer.allowAll` warns about, promoted from a comment
    /// to a headline diff entry.
    let resolveWithPolicy (policy: ActionPolicy) (surface: AuthorizationSurface) : AuthorizationSurface =
        surface.Exposed
        |> List.map (fun entry ->
            if entry.Access <> InheritedDefaultDeny then
                entry
            else
                let descriptor: ActionDescriptor = {
                    Kind = ExposedSurfaceKind.actionKind entry.ExposedKind
                    Target = entry.Endpoint
                    Scope = None
                }

                match ActionPolicy.tryFindRule descriptor policy with
                | None -> entry
                | Some rule -> {
                    entry with
                        Requires = entry.Requires @ policyTokens rule.Requirement
                        Access =
                            if grantsUnconditionally rule.Requirement then
                                AnonymousReachable
                            else
                                ExplicitRequirement
                  })
        |> assemble

    // ── queries (438.B) ───────────────────────────────────────────────

    /// **The headline.** Every entry an unauthenticated caller reaches,
    /// in id order. Additions to this list are the highest-signal diff
    /// class the composition gate produces.
    let anonymousReachable (surface: AuthorizationSurface) : ExposedSurface list =
        surface.Exposed |> List.filter (fun e -> e.Access = AnonymousReachable)

    /// Every entry gated only by the composition's default-deny floor.
    let defaultDenied (surface: AuthorizationSurface) : ExposedSurface list =
        surface.Exposed |> List.filter (fun e -> e.Access = InheritedDefaultDeny)

    /// Every entry carrying a declared gate.
    let explicitlyRequired (surface: AuthorizationSurface) : ExposedSurface list =
        surface.Exposed |> List.filter (fun e -> e.Access = ExplicitRequirement)

    /// One component's whole exposed surface — the per-component
    /// attribution the manifest exists to give.
    let ofComponent (componentId: ComponentId) (surface: AuthorizationSurface) : ExposedSurface list =
        surface.Exposed |> List.filter (fun e -> e.Component = componentId)

    /// Every entry reachable over one transport.
    let ofKind (kind: ExposedSurfaceKind) (surface: AuthorizationSurface) : ExposedSurface list =
        surface.Exposed |> List.filter (fun e -> e.ExposedKind = kind)

    /// Every component contributing at least one exposed entry, in id
    /// order.
    let components (surface: AuthorizationSurface) : ComponentId list =
        surface.Exposed |> List.map _.Component |> List.distinct

    /// The requirement tokens recorded against one endpoint, across
    /// every component that exposes it.
    let requirementsOf (endpoint: string) (surface: AuthorizationSurface) : string list =
        surface.Exposed
        |> List.filter (fun e -> e.Endpoint = endpoint)
        |> List.collect _.Requires
        |> sortedTokens

    /// A single entry rendered for a human — the line the readable
    /// delta and any external report print.
    let describe (entry: ExposedSurface) : string =
        let requires =
            match entry.Requires with
            | [] -> ""
            | tokens -> " {" + String.concat "," tokens + "}"

        sprintf
            "%s %s [%s]%s"
            (ExposedSurfaceKind.label entry.ExposedKind)
            entry.Endpoint
            (AccessClassification.label entry.Access)
            requires

    // ── diff (438.C) ──────────────────────────────────────────────────

    /// The empty delta — what an identical pair (or a surface diffed
    /// against itself) projects to.
    let emptyDelta: AuthorizationSurfaceDelta = {
        SurfacesAdded = []
        SurfacesRemoved = []
        RequirementsWeakened = []
        RequirementsStrengthened = []
    }

    let private keyOf (entry: ExposedSurface) =
        entry.Component.Value, ExposedSurfaceKind.label entry.ExposedKind, entry.Endpoint

    /// A `subject:<Kind>` token — an ADMIT-set member. The two token
    /// families move in OPPOSITE directions, which is the whole reason
    /// this distinction is drawn rather than diffing one flat set.
    let private isAdmitToken (token: string) =
        token.StartsWith("subject:", StringComparison.Ordinal)

    /// Direction for a DEMAND axis (roles, claims, tenant, permissions):
    /// every token must be satisfied, so a larger set is a STRONGER gate.
    let private compareDemandSet (before: Set<string>) (after: Set<string>) : int =
        if after = before then 0
        elif Set.isProperSuperset after before then 1
        else -1

    /// Direction for an ADMIT axis (Phase 66 subject kinds): every token
    /// is a kind let THROUGH, so a larger set is a MORE PERMISSIVE gate
    /// — the inverse of a demand set. Getting this backwards would
    /// report an admit set opened from `teamScoped` to `authenticated`
    /// as a strengthening, which is exactly the change this manifest
    /// exists to catch.
    let private compareAdmitSet (before: Set<string>) (after: Set<string>) : int =
        if after = before then 0
        elif Set.isProperSubset after before then 1
        else -1

    /// Whether `after` is weaker than `before` for the same endpoint —
    /// `-1` weaker, `0` unchanged, `+1` stronger. The one definition of
    /// "weaker", so the diff and the severity can never disagree.
    ///
    /// Classification dominates; below it the two token axes are
    /// compared in their own directions and **any axis reporting weaker
    /// wins**. A set that is neither a subset nor a superset of its
    /// predecessor also counts as WEAKENED, deliberately: a swapped role,
    /// claim, or admit kind is not provably at least as strong as what it
    /// replaced, and the conservative reading is the one a security gate
    /// wants.
    let private comparePosture (before: ExposedSurface) (after: ExposedSurface) : int =
        let classDirection =
            compare (AccessClassification.strength after.Access) (AccessClassification.strength before.Access)

        if classDirection <> 0 then
            classDirection
        else
            let split (entry: ExposedSurface) =
                let tokens = Set.ofList entry.Requires
                Set.filter isAdmitToken tokens, Set.filter (isAdmitToken >> not) tokens

            let beforeAdmit, beforeDemand = split before
            let afterAdmit, afterDemand = split after

            let admitDirection = compareAdmitSet beforeAdmit afterAdmit
            let demandDirection = compareDemandSet beforeDemand afterDemand

            if admitDirection < 0 || demandDirection < 0 then -1
            elif admitDirection > 0 || demandDirection > 0 then 1
            else 0

    /// Structurally diff two surfaces. `before` is the reference (or
    /// prior) composition, `after` the candidate — so "added" means
    /// present in `after` and not in `before`. Keyed throughout by
    /// `(Component, ExposedKind, Endpoint)`, so the result never
    /// depends on registration order.
    let diff (before: AuthorizationSurface) (after: AuthorizationSurface) : AuthorizationSurfaceDelta =
        let beforeByKey = before.Exposed |> List.map (fun e -> keyOf e, e) |> Map.ofList
        let afterByKey = after.Exposed |> List.map (fun e -> keyOf e, e) |> Map.ofList

        let added =
            after.Exposed |> List.filter (fun e -> not (beforeByKey.ContainsKey(keyOf e)))

        let removed =
            before.Exposed |> List.filter (fun e -> not (afterByKey.ContainsKey(keyOf e)))

        let changed =
            after.Exposed
            |> List.choose (fun candidate ->
                beforeByKey
                |> Map.tryFind (keyOf candidate)
                |> Option.bind (fun reference ->
                    match comparePosture reference candidate with
                    | 0 -> None
                    | direction -> Some(reference, candidate, direction)))

        {
            SurfacesAdded = ordered added
            SurfacesRemoved = ordered removed
            RequirementsWeakened =
                changed
                |> List.filter (fun (_, _, direction) -> direction < 0)
                |> List.map (fun (reference, candidate, _) -> reference, candidate)
            RequirementsStrengthened =
                changed
                |> List.filter (fun (_, _, direction) -> direction > 0)
                |> List.map (fun (reference, candidate, _) -> reference, candidate)
        }

    /// `true` when two surfaces are structurally identical.
    let isEmptyDelta (d: AuthorizationSurfaceDelta) : bool =
        List.isEmpty d.SurfacesAdded
        && List.isEmpty d.SurfacesRemoved
        && List.isEmpty d.RequirementsWeakened
        && List.isEmpty d.RequirementsStrengthened

    /// How loud a delta is. A new anonymous-reachable entry or ANY
    /// weakening is critical; everything else that moved is reviewable.
    let severity (d: AuthorizationSurfaceDelta) : AuthorizationDriftSeverity =
        let anonymousAdded =
            d.SurfacesAdded |> List.exists (fun e -> e.Access = AnonymousReachable)

        if anonymousAdded || not (List.isEmpty d.RequirementsWeakened) then
            CriticalAuthorizationDrift
        elif isEmptyDelta d then
            NoAuthorizationDrift
        else
            ReviewableAuthorizationDrift

    /// The persisted label for a severity.
    let severityLabel (s: AuthorizationDriftSeverity) : string =
        match s with
        | NoAuthorizationDrift -> "none"
        | ReviewableAuthorizationDrift -> "reviewable"
        | CriticalAuthorizationDrift -> "CRITICAL"

    /// A deterministic, human-readable rendering of a delta — the
    /// message a composition golden-file CI gate prints on a surface
    /// mismatch. The severity leads, and the critical classes are
    /// marked inline, so the reviewer's eye lands on the thing that
    /// grew the attack surface rather than on the longest list.
    let renderDelta (d: AuthorizationSurfaceDelta) : string =
        if isEmptyDelta d then
            "(no authorization-surface differences)"
        else
            let section title (lines: string list) =
                if List.isEmpty lines then
                    []
                else
                    [ sprintf "%s:" title; yield! lines ]

            let addedLines =
                d.SurfacesAdded
                |> List.map (fun e ->
                    let marker =
                        if e.Access = AnonymousReachable then
                            "  + [CRITICAL anonymous-reachable] "
                        else
                            "  + "

                    marker + ComponentId.value e.Component + " " + describe e)

            let removedLines =
                d.SurfacesRemoved
                |> List.map (fun e -> "  - " + ComponentId.value e.Component + " " + describe e)

            let changeLines marker (pairs: (ExposedSurface * ExposedSurface) list) =
                pairs
                |> List.map (fun (before, after) ->
                    sprintf
                        "  %s %s %s  ->  %s"
                        marker
                        (ComponentId.value after.Component)
                        (describe before)
                        (describe after))

            [
                sprintf "Severity: %s" (severityLabel (severity d))
                yield! section "Exposed surfaces added" addedLines
                yield! section "Exposed surfaces removed" removedLines
                yield! section "Requirements WEAKENED (critical)" (changeLines "!" d.RequirementsWeakened)
                yield! section "Requirements strengthened" (changeLines "^" d.RequirementsStrengthened)
            ]
            |> String.concat "\n"

    // ── wire projection ───────────────────────────────────────────────

    /// Project a surface to plain strings for persistence (a golden
    /// file, an external viewer). Deterministic — entries in the
    /// assembled order, requirement tokens sorted ordinally.
    let toWire (surface: AuthorizationSurface) : AuthorizationSurfaceWireEntry list =
        surface.Exposed
        |> List.map (fun entry -> {
            ComponentIdentity = ComponentId.value entry.Component
            KindLabel = ExposedSurfaceKind.label entry.ExposedKind
            EndpointPath = entry.Endpoint
            Requirements = sortedTokens entry.Requires
            Classification = AccessClassification.label entry.Access
        })

    /// Read a persisted wire projection back into a surface.
    /// Round-trips `toWire` exactly, which is what lets the golden-file
    /// gate compare a committed baseline against a live derivation
    /// through `diff`.
    let ofWire (entries: AuthorizationSurfaceWireEntry list) : AuthorizationSurface =
        entries
        |> List.map (fun entry -> {
            Component = ComponentId.create entry.ComponentIdentity
            ExposedKind = ExposedSurfaceKind.ofLabel entry.KindLabel
            Endpoint = entry.EndpointPath
            Requires = entry.Requirements
            Access = AccessClassification.ofLabel entry.Classification
        })
        |> assemble

// ─── Phase 688 — the OUTBOUND half of the same manifest ───────────────
//
// Everything above is the INBOUND surface: what a component exposes and
// who may reach it. The question a review opens with has a second half —
// what does the component itself reach? — and until Phase 688 the answer
// was "whatever DI will hand it", recoverable only by reading its code.
//
// A component's declared reachable-seam set (Phase 688's `SeamGrant`,
// keyed by the same `ComponentId`) makes that a value too, and this
// section gives it the same treatment the exposed surface gets: a
// deterministic projection, a key-based diff, a severity, a readable
// delta, and a plain-string wire form a golden file can hold. A grant-set
// change is then a reviewable CI diff rather than code archaeology.
//
// **A sibling projection rather than fields grown onto
// `AuthorizationSurface`** — the rule this file already records against
// `AuthorizationSurfaceDelta`, and which `ClassifiedCompositionRule`,
// `EventTopologyDelta` and `DataFootprintDelta` all follow: growing a
// shipped F# record breaks its constructor. The projections join on
// `ComponentId`, which every one of them carries.

/// One component's declared outbound authority: which seams it may
/// resolve. Field names are `Grant*`-prefixed for the Phase 431
/// field-inference reason recorded on `AuthorizationSurface.Exposed` —
/// F#'s last-declared-wins inference re-points every unannotated
/// construction sharing a full field-name set at whichever record
/// compiled later, and `Component` alone is shared with `ExposedSurface`.
type ComponentSeamGrant = {
    /// The stable Phase 279 identity of the declaring component.
    GrantComponent: ComponentId
    /// What it declared it reaches — `UnrestrictedSeams` when it declared
    /// nothing (the pre-688 posture).
    GrantedSeams: SeamGrant
}

/// The derived outbound authority of a composition: every component's
/// declared reachable-seam set, in a deterministic `ComponentId` order,
/// so two compositions that declared the same grants in a different
/// order produce the same surface.
type SeamAuthoritySurface = { Granted: ComponentSeamGrant list }

/// The structural difference between two seam-authority surfaces, keyed
/// by `ComponentId` — never by list position.
type SeamAuthorityDelta = {
    GrantsAdded: ComponentSeamGrant list
    GrantsRemoved: ComponentSeamGrant list
    /// `(before, after)` pairs whose reachable-seam set grew — the class
    /// a security review must never miss, and the outbound twin of
    /// `RequirementsWeakened`.
    GrantsWidened: (ComponentSeamGrant * ComponentSeamGrant) list
    /// `(before, after)` pairs whose reachable-seam set shrank.
    GrantsNarrowed: (ComponentSeamGrant * ComponentSeamGrant) list
}

/// A component's grant projected to plain strings — the shape a golden
/// file persists, so it never depends on the `Set` / union shapes
/// round-tripping through a serialiser.
type SeamAuthorityWireEntry = {
    GrantComponentIdentity: string
    /// `"unrestricted"` or `"declared"`.
    GrantKind: string
    /// The declared seam identities, sorted ordinally. Empty for
    /// `unrestricted` AND for a declaration of no seams — `GrantKind` is
    /// what distinguishes them, which is why both fields are persisted.
    GrantSeams: string list
}

/// Derivation, diff and projection over a composition's declared
/// outbound seam authority. Pure over an already-composed
/// `SeamGrantSignature`; nothing is built until a caller asks (GP 13),
/// and nothing is registered into DI ever.
module SeamAuthoritySurface =

    /// The surface of a composition that declares no grants.
    let empty: SeamAuthoritySurface = { Granted = [] }

    let private compareOrdinal (a: string) (b: string) = String.CompareOrdinal(a, b)

    let private ordered (entries: ComponentSeamGrant list) : ComponentSeamGrant list =
        entries
        |> List.sortWith (fun a b -> compareOrdinal a.GrantComponent.Value b.GrantComponent.Value)

    /// Derive the surface from a composed `SeamGrantSignature`. Components
    /// absent from the signature are absent here too — an absent entry
    /// resolves to `UnrestrictedSeams` at the gate, and listing every
    /// undeclared component as "unrestricted" would bury the declared ones
    /// in a roster of non-declarations.
    let ofSignature (signature: SeamGrantSignature) : SeamAuthoritySurface = {
        Granted =
            signature
            |> Map.toList
            |> List.map (fun (componentId, grant) -> {
                GrantComponent = componentId
                GrantedSeams = grant
            })
            |> ordered
    }

    /// The grant a named component declared — `UnrestrictedSeams` when it
    /// is absent, matching what the gate resolves.
    let grantOf (componentId: ComponentId) (surface: SeamAuthoritySurface) : SeamGrant =
        surface.Granted
        |> List.tryFind (fun entry -> entry.GrantComponent = componentId)
        |> Option.map _.GrantedSeams
        |> Option.defaultValue UnrestrictedSeams

    /// A single grant rendered for a human — the line the readable delta
    /// and any external report print.
    let describe (entry: ComponentSeamGrant) : string =
        sprintf "reaches %s" (SeamGrant.render entry.GrantedSeams)

    // ── diff ──────────────────────────────────────────────────────────

    /// The empty delta — what an identical pair projects to.
    let emptyDelta: SeamAuthorityDelta = {
        GrantsAdded = []
        GrantsRemoved = []
        GrantsWidened = []
        GrantsNarrowed = []
    }

    /// Direction of a grant change: `-1` WIDENED (reaches more), `0`
    /// unchanged, `+1` narrowed. The single definition of "widened", so
    /// the diff and the severity can never disagree.
    ///
    /// Two sets that are neither a subset nor a superset of one another
    /// count as WIDENED, deliberately — the same conservative reading
    /// `comparePosture` takes for a swapped role or claim. A grant set
    /// that traded `IAuditSink` for `ISecretStore` is not provably at
    /// most what it replaced, and a security gate that guessed otherwise
    /// would wave through exactly the change it exists to catch.
    let private compareGrant (before: ComponentSeamGrant) (after: ComponentSeamGrant) : int =
        if before.GrantedSeams = after.GrantedSeams then
            0
        elif SeamGrant.covers before.GrantedSeams after.GrantedSeams then
            1
        else
            -1

    /// Structurally diff two seam-authority surfaces. `before` is the
    /// reference (or prior) composition, `after` the candidate — so
    /// "added" means present in `after` and not in `before`. Keyed by
    /// `ComponentId`, so the result never depends on declaration order.
    let diff (before: SeamAuthoritySurface) (after: SeamAuthoritySurface) : SeamAuthorityDelta =
        let key (entry: ComponentSeamGrant) = entry.GrantComponent.Value
        let beforeByKey = before.Granted |> List.map (fun e -> key e, e) |> Map.ofList
        let afterByKey = after.Granted |> List.map (fun e -> key e, e) |> Map.ofList

        let changed =
            after.Granted
            |> List.choose (fun candidate ->
                beforeByKey
                |> Map.tryFind (key candidate)
                |> Option.bind (fun reference ->
                    match compareGrant reference candidate with
                    | 0 -> None
                    | direction -> Some(reference, candidate, direction)))

        {
            GrantsAdded =
                after.Granted
                |> List.filter (fun e -> not (beforeByKey.ContainsKey(key e)))
                |> ordered
            GrantsRemoved =
                before.Granted
                |> List.filter (fun e -> not (afterByKey.ContainsKey(key e)))
                |> ordered
            GrantsWidened =
                changed
                |> List.filter (fun (_, _, direction) -> direction < 0)
                |> List.map (fun (reference, candidate, _) -> reference, candidate)
            GrantsNarrowed =
                changed
                |> List.filter (fun (_, _, direction) -> direction > 0)
                |> List.map (fun (reference, candidate, _) -> reference, candidate)
        }

    /// `true` when two seam-authority surfaces are structurally identical.
    let isEmptyDelta (d: SeamAuthorityDelta) : bool =
        List.isEmpty d.GrantsAdded
        && List.isEmpty d.GrantsRemoved
        && List.isEmpty d.GrantsWidened
        && List.isEmpty d.GrantsNarrowed

    /// How loud a delta is, in the same vocabulary the inbound surface
    /// uses. A widened grant set, or a newly-declared component that
    /// declares itself UNRESTRICTED, is critical — both grow what the
    /// composition can reach, and the second is the outbound twin of a
    /// new anonymous-reachable endpoint. Everything else that moved is
    /// reviewable.
    let severity (d: SeamAuthorityDelta) : AuthorizationDriftSeverity =
        let unrestrictedAdded =
            d.GrantsAdded
            |> List.exists (fun e -> not (SeamGrant.isDeclared e.GrantedSeams))

        if unrestrictedAdded || not (List.isEmpty d.GrantsWidened) then
            CriticalAuthorizationDrift
        elif isEmptyDelta d then
            NoAuthorizationDrift
        else
            ReviewableAuthorizationDrift

    /// A deterministic, human-readable rendering of a delta — the message
    /// a composition golden-file CI gate prints on a grant mismatch. The
    /// severity leads and the widening is marked inline, so the reviewer's
    /// eye lands on the authority that grew.
    let renderDelta (d: SeamAuthorityDelta) : string =
        if isEmptyDelta d then
            "(no seam-authority differences)"
        else
            let section title (lines: string list) =
                if List.isEmpty lines then
                    []
                else
                    [ sprintf "%s:" title; yield! lines ]

            let addedLines =
                d.GrantsAdded
                |> List.map (fun e ->
                    let marker =
                        if SeamGrant.isDeclared e.GrantedSeams then
                            "  + "
                        else
                            "  + [CRITICAL unrestricted] "

                    marker + ComponentId.value e.GrantComponent + " " + describe e)

            let removedLines =
                d.GrantsRemoved
                |> List.map (fun e -> "  - " + ComponentId.value e.GrantComponent + " " + describe e)

            let changeLines marker (pairs: (ComponentSeamGrant * ComponentSeamGrant) list) =
                pairs
                |> List.map (fun (before, after) ->
                    sprintf
                        "  %s %s %s  ->  %s"
                        marker
                        (ComponentId.value after.GrantComponent)
                        (describe before)
                        (describe after))

            [
                sprintf "Severity: %s" (AuthorizationSurface.severityLabel (severity d))
                yield! section "Seam grants added" addedLines
                yield! section "Seam grants removed" removedLines
                yield! section "Seam grants WIDENED (critical)" (changeLines "!" d.GrantsWidened)
                yield! section "Seam grants narrowed" (changeLines "^" d.GrantsNarrowed)
            ]
            |> String.concat "\n"

    // ── wire projection ───────────────────────────────────────────────

    /// Project a seam-authority surface to plain strings for persistence.
    /// Deterministic — components in `ComponentId` order, seam identities
    /// sorted ordinally within each.
    let toWire (surface: SeamAuthoritySurface) : SeamAuthorityWireEntry list =
        surface.Granted
        |> ordered
        |> List.map (fun entry -> {
            GrantComponentIdentity = ComponentId.value entry.GrantComponent
            GrantKind = SeamGrant.kindLabel entry.GrantedSeams
            GrantSeams = SeamGrant.seamLabels entry.GrantedSeams
        })

    /// Read a persisted wire projection back. Round-trips `toWire`
    /// exactly, which is what lets the golden-file gate compare a
    /// committed baseline against a live derivation through `diff`.
    let ofWire (entries: SeamAuthorityWireEntry list) : SeamAuthoritySurface = {
        Granted =
            entries
            |> List.map (fun entry -> {
                GrantComponent = ComponentId.create entry.GrantComponentIdentity
                GrantedSeams = SeamGrant.ofWireParts entry.GrantKind entry.GrantSeams
            })
            |> ordered
    }