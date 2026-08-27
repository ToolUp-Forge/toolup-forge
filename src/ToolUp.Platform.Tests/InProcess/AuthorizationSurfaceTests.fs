module ToolUp.Platform.Tests.InProcess.AuthorizationSurfaceTests

open System
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

// ─── Phase 438 — authorization-surface manifest ───────────────────────
//
// Covers the acceptance shape: a two-module in-memory composition yields
// exactly the expected per-`ComponentId` exposed surface, derived from
// the live registrations (route prefixes under their Phase 66 admit sets,
// exact route overrides, AI tools, `OnEvent` job triggers) plus a
// remoting API record read through the dispatcher's OWN Phase 69d
// classifier — with nothing hand-listed; an unauthenticated route
// surfaces as anonymous-reachable; a weakened requirement flips the diff
// severity to critical; and a consumer that never reads the surface
// contributes no DI registration at all (GP 11 / GP 13).
//
// The "derived, not hand-declared" property is asserted directly: the
// same derivation function, over a module that grew one more route
// override and one more tool, yields those entries — no code in
// `AuthorizationSurface.fs` names any of these routes, tools, or roles.

// ── fixtures ──────────────────────────────────────────────────────────

/// A job handler that is never dispatched — the surface reads the
/// declaration's `Trigger` and `HandlerName`, never the handler.
type private StubJobHandler() =
    interface IJobHandler with
        member _.Execute(_) = async { return JobResult.Success }

let private stubTool
    (name: string)
    (sourceModule: string)
    : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = sourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
    },
    (fun _ _ -> async { return "" })

/// Two composed modules:
///
///   * `Reporting` — a route sub-tree left on the strict SDK fallback
///     (so the manifest must call it `inherited-default-deny`, not an
///     explicit gate), one deliberately public exact route, an AI tool,
///     and an `OnEvent` job handler.
///   * `Admin` — a route sub-tree under a DECLARED non-fallback admit
///     set (so the manifest must call it an explicit requirement).
let private composedModules () : ServerModule list =
    let reporting =
        ServerModule.create "Reporting"
        |> ServerModule.withComponentId "reporting-service"
        |> ServerModule.withRoutePrefix "/api/reporting/"
        |> ServerModule.withRouteSurfaceRequirement "get" "/api/reporting/public/status" SurfaceRequirement.public_
        |> ServerModule.withAITools [ stubTool "reporting.summarise" "Reporting" ]
        |> ServerModule.withJobHandler ("reporting.on-ingest", StubJobHandler(), OnEvent "DataIngested")

    let admin =
        ServerModule.create "Admin"
        |> ServerModule.withComponentId "admin-service"
        |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.teamScoped
        |> ServerModule.withRoutePrefix "/api/admin/"

    [ reporting; admin ]

let private reportingId = ComponentId.ofModule "reporting-service"
let private adminId = ComponentId.ofModule "admin-service"

let private surface = AuthorizationSurface.ofModules (composedModules ())

/// The identity triple every assertion below keys on.
let private triples (entries: ExposedSurface list) =
    entries
    |> List.map (fun e -> ComponentId.value e.Component, ExposedSurfaceKind.label e.ExposedKind, e.Endpoint)

let private endpointsOf (entries: ExposedSurface list) = entries |> List.map _.Endpoint

let private find (endpoint: string) =
    surface.Exposed |> List.find (fun e -> e.Endpoint = endpoint)

/// A remoting API record carrying the tier-shared `ToolUp.Platform.*`
/// attribute mirrors — the family a Fable-compiled contract uses. The
/// classifier honours both families by simple type name, so reading it
/// here is reading exactly what the dispatcher enforces.
type private ReportsApi = {
    [<PublicEndpoint>]
    Health: unit -> Async<string>
    [<AllowAnonymous>]
    GetPublicSummary: unit -> Async<string>
    [<RequiresRole "Admin">]
    DeleteReport: string -> Async<unit>
    [<TenantScoped>]
    ListReports: unit -> Async<string list>
}

/// A record whose fields carry NO authorization attribute at all. The
/// dispatcher refuses to start on this, which is precisely why the
/// manifest must not report it as reachable.
type private UnclassifiedApi = { DoThing: unit -> Async<string> }

let private apiComponent = ComponentId.ofModule "reports-api"

let private entry (componentId: ComponentId) kind endpoint requires access : ExposedSurface = {
    Component = componentId
    ExposedKind = kind
    Endpoint = endpoint
    Requires = requires
    Access = access
}

// ── derivation (438.A) ────────────────────────────────────────────────

let private derivation =
    testList "derivation" [

        test "the composition enumerates its known surface exactly" {
            // Deterministic order: by component id, then kind label, then
            // endpoint. Asserting the WHOLE list (not a containment) is
            // what makes this a manifest rather than a spot check — an
            // extra derived entry fails here.
            Expect.equal
                (triples surface.Exposed)
                [
                    "module:admin-service", "route", "/api/admin/"
                    "module:reporting-service", "ai-tool", "reporting.summarise"
                    "module:reporting-service", "event-handler", "reporting.on-ingest"
                    "module:reporting-service", "route", "/api/reporting/"
                    "module:reporting-service", "route", "GET /api/reporting/public/status"
                ]
                "every registered route / tool / event handler surfaces, attributed to its own component"
        }

        test "per-component attribution answers 'what does this component expose'" {
            Expect.equal
                (endpointsOf (AuthorizationSurface.ofComponent adminId surface))
                [ "/api/admin/" ]
                "the admin module exposes only its own sub-tree"

            Expect.equal
                (AuthorizationSurface.components surface)
                [ adminId; reportingId ]
                "both components are named, in id order"
        }

        test "an exact route override carries its declared admit set" {
            let e = find "GET /api/reporting/public/status"

            Expect.equal
                e.Requires
                [
                    "subject:AnonymousKind"
                    "subject:ClaimBearerKind"
                    "subject:TeamMemberKind"
                    "subject:UserKind"
                ]
                "the Phase 66 admit set is the requirement, as sorted tokens"

            Expect.equal (ExposedSurfaceKind.label e.ExposedKind) "route" "an exact (method, path) override is a route"
        }

        test "an event-triggered handler records the event type as its reachability condition" {
            let e = find "reporting.on-ingest"

            Expect.equal e.Requires [ "event:DataIngested" ] "the OnEvent trigger IS the way in"
            Expect.equal e.ExposedKind ExposedEventHandler "and it is classed as an event handler"
        }

        test "a cron / manual job is NOT an exposed surface" {
            let cronOnly =
                AuthorizationSurface.ofModules [
                    ServerModule.create "Billing"
                    |> ServerModule.withJobHandler (
                        "billing.nightly",
                        StubJobHandler(),
                        Trigger.CronTrigger "0 2 * * *"
                    )
                    |> ServerModule.withJobHandler ("billing.replay", StubJobHandler(), Trigger.Manual)
                ]

            Expect.isEmpty
                cronOnly.Exposed
                "nothing outside the deployment can trigger a cron or manual job, so neither is attack surface"
        }

        test "derived, never hand-listed: a newly registered route and tool appear with no source change" {
            let grown =
                composedModules ()
                |> List.map (fun m ->
                    if m.Name = "Admin" then
                        m
                        |> ServerModule.withRouteSurfaceRequirement
                            "POST"
                            "/api/admin/impersonate"
                            SurfaceRequirement.teamScoped
                        |> ServerModule.withAITools [ stubTool "admin.impersonate" "Admin" ]
                    else
                        m)
                |> AuthorizationSurface.ofModules

            let added =
                AuthorizationSurface.diff surface grown |> _.SurfacesAdded |> endpointsOf

            Expect.equal
                added
                [ "admin.impersonate"; "POST /api/admin/impersonate" ]
                "the same derivation function surfaces both new registrations"
        }

        test "attribution follows an explicit ComponentId, and falls back to the name (GP 11)" {
            let unnamed =
                AuthorizationSurface.ofModules [
                    ServerModule.create "Legacy" |> ServerModule.withRoutePrefix "/api/legacy/"
                ]

            Expect.equal
                (AuthorizationSurface.components unnamed)
                [ ComponentId.ofModule "Legacy" ]
                "a module declaring no id keeps the name-derived identity"
        }
    ]

// ── default-deny classification (438.B) ───────────────────────────────

let private classification =
    testList "classification" [

        test "an unauthenticated route surfaces as anonymous-reachable — the headline" {
            let anonymous = AuthorizationSurface.anonymousReachable surface

            Expect.equal
                (endpointsOf anonymous)
                [ "GET /api/reporting/public/status" ]
                "the one anonymous-admitting route, and only it, is the headline set"
        }

        test "a route left on the strict SDK fallback is inherited-default-deny, not an explicit gate" {
            Expect.equal
                (find "/api/reporting/").Access
                InheritedDefaultDeny
                "nothing was declared, so the fail-closed floor applies — and the manifest says so"

            Expect.equal
                (find "/api/reporting/").Requires
                [ "subject:TeamMemberKind"; "subject:UserKind" ]
                "the floor's admit set is still reported"
        }

        test "a module that moved its default off the fallback declares an explicit requirement" {
            Expect.equal
                (find "/api/admin/").Access
                ExplicitRequirement
                "a declared non-fallback admit set is a gate the module chose"

            Expect.equal (find "/api/admin/").Requires [ "subject:TeamMemberKind" ] "carrying its admit set"
        }

        test "an AI tool sits at the Phase 113 default-deny floor until a policy grants it" {
            Expect.equal
                (find "reporting.summarise").Access
                InheritedDefaultDeny
                "an AI-drivable tool with no action policy is denied, not open"
        }

        test "the three classifications partition the surface" {
            let total =
                List.length (AuthorizationSurface.anonymousReachable surface)
                + List.length (AuthorizationSurface.defaultDenied surface)
                + List.length (AuthorizationSurface.explicitlyRequired surface)

            Expect.equal total (List.length surface.Exposed) "every entry lands in exactly one class"
        }

        test "duplicate registrations collapse to the MOST PERMISSIVE entry" {
            // A surface that reported the stronger of two paths to one
            // endpoint would understate the attack surface.
            let collapsed =
                AuthorizationSurface.empty
                |> AuthorizationSurface.withSurface (
                    entry reportingId ExposedRoute "/api/x/" [ "subject:UserKind" ] ExplicitRequirement
                )
                |> AuthorizationSurface.withSurface (
                    entry reportingId ExposedRoute "/api/x/" [ "subject:AnonymousKind" ] AnonymousReachable
                )

            Expect.equal (List.length collapsed.Exposed) 1 "one endpoint, one entry"

            Expect.equal
                (List.head collapsed.Exposed).Access
                AnonymousReachable
                "and it reports the way in, not the way it was meant to be gated"
        }
    ]

// ── remoting endpoints, read through the dispatcher's own classifier ──

let private api = AuthorizationSurface.ofApiRecord<ReportsApi> apiComponent

let private endpoint name =
    api.Exposed |> List.find (fun e -> e.Endpoint = "ReportsApi." + name)

let private remoting =
    testList "remoting endpoints" [

        test "every API record field surfaces as a remoting endpoint" {
            Expect.equal
                (endpointsOf api.Exposed)
                [
                    "ReportsApi.DeleteReport"
                    "ReportsApi.GetPublicSummary"
                    "ReportsApi.Health"
                    "ReportsApi.ListReports"
                ]
                "derived from the record type — adding a method needs no change to AuthorizationSurface.fs"
        }

        test "PublicEndpoint and AllowAnonymous are both anonymous-reachable" {
            Expect.equal
                (endpointsOf (AuthorizationSurface.anonymousReachable api))
                [ "ReportsApi.GetPublicSummary"; "ReportsApi.Health" ]
                "the auth-context resolver either is not consulted, or is not enforced"

            Expect.equal (endpoint "Health").Requires [ "public-endpoint" ] "and each says which marker put it there"

            Expect.equal
                (endpoint "GetPublicSummary").Requires
                [ "allow-anonymous" ]
                "the anonymous-admitting marker is named too"
        }

        test "role and tenant requirements are normalised into requirement tokens" {
            Expect.equal (endpoint "DeleteReport").Requires [ "role:Admin" ] "the role the dispatcher will demand"

            Expect.equal
                (endpoint "DeleteReport").Access
                ExplicitRequirement
                "a declared role is an explicit requirement"

            Expect.equal (endpoint "ListReports").Requires [ "tenant" ] "a tenant binding is a requirement too"
        }

        test "an unclassified method is reported at the fail-closed classification, never as reachable" {
            let unclassified =
                AuthorizationSurface.ofApiRecord<UnclassifiedApi> (ComponentId.ofModule "legacy-api")

            let e = List.head unclassified.Exposed
            Expect.equal e.Requires [ "unclassified" ] "named honestly"

            Expect.equal
                e.Access
                InheritedDefaultDeny
                "the dispatcher refuses to start on it; the manifest must not claim it is open"
        }

        test "a non-record type contributes nothing rather than throwing" {
            let none = AuthorizationSurface.ofApiRecordType apiComponent typeof<string>
            Expect.isEmpty none.Exposed "there is no API record to inspect"
        }

        test "the module half and the remoting half merge into one manifest" {
            let whole = AuthorizationSurface.mergeAll [ surface; api ]

            Expect.equal
                (List.length whole.Exposed)
                (List.length surface.Exposed + List.length api.Exposed)
                "both halves are present, keyed by their own components"
        }
    ]

// ── policy resolution against the Phase 113 default-deny seam ─────────

let private policyResolution =
    testList "policy resolution" [

        test "a matching rule replaces the default-deny floor with its requirement" {
            let policy: ActionPolicy = {
                Rules = [
                    {
                        Kind = "ai-tool"
                        Target = "reporting.*"
                        Requirement = ActionRequirement.Permission("Reporting", ModulePermission.Read)
                    }
                ]
            }

            let resolved = AuthorizationSurface.resolveWithPolicy policy surface

            let tool =
                resolved.Exposed |> List.find (fun e -> e.Endpoint = "reporting.summarise")

            Expect.equal tool.Access ExplicitRequirement "the policy is the gate"
            Expect.equal tool.Requires [ "permission:Reporting/Read" ] "and it is reported as a requirement token"
        }

        test "an unconditional grant resolves to anonymous-reachable" {
            let policy: ActionPolicy = {
                Rules = [
                    {
                        Kind = "ai-tool"
                        Target = "*"
                        Requirement = ActionRequirement.Unrestricted
                    }
                ]
            }

            let resolved = AuthorizationSurface.resolveWithPolicy policy surface

            Expect.contains
                (endpointsOf (AuthorizationSurface.anonymousReachable resolved))
                "reporting.summarise"
                "'grant unconditionally' is exactly what the headline set means"
        }

        test "only floor entries are refined — a declared gate is never overwritten" {
            let policy: ActionPolicy = {
                Rules = [
                    {
                        Kind = "*"
                        Target = "*"
                        Requirement = ActionRequirement.Unrestricted
                    }
                ]
            }

            let resolved = AuthorizationSurface.resolveWithPolicy policy surface

            let admin = resolved.Exposed |> List.find (fun e -> e.Endpoint = "/api/admin/")

            Expect.equal
                admin.Access
                ExplicitRequirement
                "a route the Phase 66 middleware gates is not opened by an action policy that never runs in front of it"
        }

        test "an empty policy is the default-deny floor and changes nothing" {
            Expect.isTrue
                (AuthorizationSurface.isEmptyDelta (
                    AuthorizationSurface.diff
                        surface
                        (AuthorizationSurface.resolveWithPolicy ActionPolicy.empty surface)
                ))
                "no rules, no refinement"
        }
    ]

// ── diff + severity (438.C) ───────────────────────────────────────────

let private diffing =
    testList "diff" [

        test "a surface diffs clean against itself" {
            Expect.isTrue
                (AuthorizationSurface.isEmptyDelta (AuthorizationSurface.diff surface surface))
                "identical surfaces produce no delta"

            Expect.equal
                (AuthorizationSurface.severity (AuthorizationSurface.diff surface surface))
                NoAuthorizationDrift
                "and no drift"
        }

        test "registration order never shows up in the diff" {
            let reversed = AuthorizationSurface.ofModules (List.rev (composedModules ()))

            Expect.isTrue
                (AuthorizationSurface.isEmptyDelta (AuthorizationSurface.diff surface reversed))
                "the surface is keyed, not positional"
        }

        test "widening an admit set is a WEAKENING, and flips the severity to critical" {
            // teamScoped -> authenticated admits two more subject kinds.
            // A flat subset comparison would call this a strengthening;
            // an admit set moves the other way.
            let widened =
                AuthorizationSurface.ofModules [
                    ServerModule.create "Admin"
                    |> ServerModule.withComponentId "admin-service"
                    |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.authenticated
                    |> ServerModule.withRoutePrefix "/api/admin/"
                ]

            let delta =
                AuthorizationSurface.diff
                    (AuthorizationSurface.ofModules [
                        ServerModule.create "Admin"
                        |> ServerModule.withComponentId "admin-service"
                        |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.teamScoped
                        |> ServerModule.withRoutePrefix "/api/admin/"
                    ])
                    widened

            Expect.isNonEmpty delta.RequirementsWeakened "admitting more subject kinds is weaker"

            Expect.equal
                (AuthorizationSurface.severity delta)
                CriticalAuthorizationDrift
                "a weakening is always critical"

            Expect.stringContains
                (AuthorizationSurface.renderDelta delta)
                "WEAKENED"
                "and the readable failure names the section"
        }

        test "dropping a demanded role is a weakening; adding one is a strengthening" {
            let before =
                AuthorizationSurface.empty
                |> AuthorizationSurface.withSurface (
                    entry
                        apiComponent
                        ExposedRemotingEndpoint
                        "Api.Delete"
                        [ "role:Admin"; "tenant" ]
                        ExplicitRequirement
                )

            let after =
                AuthorizationSurface.empty
                |> AuthorizationSurface.withSurface (
                    entry apiComponent ExposedRemotingEndpoint "Api.Delete" [ "role:Admin" ] ExplicitRequirement
                )

            Expect.isNonEmpty (AuthorizationSurface.diff before after).RequirementsWeakened "one fewer demand is weaker"

            Expect.isNonEmpty
                (AuthorizationSurface.diff after before).RequirementsStrengthened
                "and the reverse is stronger"

            Expect.equal
                (AuthorizationSurface.severity (AuthorizationSurface.diff after before))
                ReviewableAuthorizationDrift
                "a strengthening is reviewable, not critical"
        }

        test "a swapped requirement is treated conservatively as a weakening" {
            let before =
                AuthorizationSurface.empty
                |> AuthorizationSurface.withSurface (
                    entry apiComponent ExposedRemotingEndpoint "Api.Delete" [ "role:Admin" ] ExplicitRequirement
                )

            let after =
                AuthorizationSurface.empty
                |> AuthorizationSurface.withSurface (
                    entry apiComponent ExposedRemotingEndpoint "Api.Delete" [ "role:Member" ] ExplicitRequirement
                )

            Expect.isNonEmpty
                (AuthorizationSurface.diff before after).RequirementsWeakened
                "a swapped role is not provably at least as strong as what it replaced"
        }

        test "an added guarded surface is reviewable, an added anonymous one is critical" {
            let guarded =
                AuthorizationSurface.empty
                |> AuthorizationSurface.withSurface (
                    entry adminId ExposedRoute "/api/new/" [ "subject:UserKind" ] ExplicitRequirement
                )

            Expect.equal
                (AuthorizationSurface.severity (AuthorizationSurface.diff AuthorizationSurface.empty guarded))
                ReviewableAuthorizationDrift
                "a new gated endpoint is a normal review item"

            let opened =
                AuthorizationSurface.empty
                |> AuthorizationSurface.withSurface (
                    entry adminId ExposedRoute "/api/new/" [ "subject:AnonymousKind" ] AnonymousReachable
                )

            let delta = AuthorizationSurface.diff AuthorizationSurface.empty opened

            Expect.equal
                (AuthorizationSurface.severity delta)
                CriticalAuthorizationDrift
                "a new anonymous-reachable endpoint is the loudest class"

            Expect.stringContains
                (AuthorizationSurface.renderDelta delta)
                "CRITICAL anonymous-reachable"
                "the readable failure marks it inline"
        }

        test "an empty delta renders as a readable no-op" {
            Expect.stringContains
                (AuthorizationSurface.renderDelta AuthorizationSurface.emptyDelta)
                "no authorization-surface differences"
                "the gate's happy path is legible too"
        }
    ]

// ── wire projection (438.C) ───────────────────────────────────────────

let private wire =
    testList "wire projection" [

        test "the wire projection round-trips losslessly" {
            let back = AuthorizationSurface.ofWire (AuthorizationSurface.toWire surface)

            Expect.isTrue
                (AuthorizationSurface.isEmptyDelta (AuthorizationSurface.diff surface back))
                "toWire -> ofWire preserves the surface structurally"
        }

        test "the wire projection is deterministic" {
            let once = AuthorizationSurface.toWire surface

            let twice =
                AuthorizationSurface.toWire (AuthorizationSurface.merge AuthorizationSurface.empty surface)

            Expect.equal twice once "the same surface always projects to the same wire shape"
        }

        test "an unrecognised persisted classification reads back fail-closed" {
            Expect.equal
                (AccessClassification.ofLabel "who-knows")
                InheritedDefaultDeny
                "never anonymous-reachable (a fabricated headline) and never an explicit gate (a claimed one)"
        }
    ]

// ── zero footprint (GP 11 / GP 13) ────────────────────────────────────

let private zeroFootprint =
    testList "zero footprint" [

        test "deriving the surface contributes no DI registration" {
            let services = ServiceCollection()
            let before = services.Count

            let derived = AuthorizationSurface.ofModules (composedModules ())

            Expect.isNonEmpty derived.Exposed "the surface is genuinely derived"

            Expect.equal
                services.Count
                before
                "there is no serviceRegistration closure in this file at all — a consumer that never reads the surface composes byte-for-byte what it did before"
        }

        test "the derivation is a pure query — repeatable and side-effect-free" {
            let modules = composedModules ()

            Expect.equal
                (AuthorizationSurface.ofModules modules)
                (AuthorizationSurface.ofModules modules)
                "deriving twice from the same registrations yields the same value"
        }

        test "a composition that exposes nothing derives the empty surface" {
            Expect.equal
                (AuthorizationSurface.ofModules [])
                AuthorizationSurface.empty
                "nothing composed, nothing exposed"

            Expect.isEmpty
                (AuthorizationSurface.ofModules [ ServerModule.create "Quiet" ]).Exposed
                "a module registering no route, tool, or event handler exposes nothing"
        }
    ]

// ── The shipped in-handler gate declarations (627.E residue) ──────────
//
// 627.E shipped the mechanism with no declarations, so the four records
// that motivated it kept overstating `anonymousReachable` by their whole
// method count — 32 across `IFormApi` (16), `JobApi` (8),
// `ModelExecutionApi` (7) and `IModuleQueryBusApi` (1). The three
// platform-tier records are checked here; `IFormApi`'s sixteen are
// checked in `ToolUp.Forms.Tests`, beside its own declarations.
//
// **Each case asserts the count moves by the EXPECTED amount and that the
// endpoints that moved are the endpoints named** — not merely that the
// number went down. A sweep that silently covered the wrong methods would
// move the number down too, and would be exactly the kind of untrue
// reassurance the headline list exists to avoid.

let private inHandlerGates =
    let componentId = ComponentId.create "toolup.platform"

    /// Assert: every method starts anonymous, every method is declared,
    /// the headline empties, and the same methods land in
    /// `gatedInHandler` carrying a non-empty rationale token.
    let sweepCase
        (name: string)
        (surface: AuthorizationSurface)
        (declarations: InHandlerGateDeclaration list)
        (expected: int)
        =
        testCase name
        <| fun () ->

            let before = AuthorizationSurface.anonymousReachable surface

            Expect.equal
                (List.length before)
                expected
                "the fixture is only meaningful if every method starts in the headline set"

            let resolved = AuthorizationSurface.resolveWithInHandlerGates declarations surface

            Expect.isEmpty
                (AuthorizationSurface.anonymousReachable resolved)
                "the headline set empties — every method of this record is declared"

            Expect.equal
                (AuthorizationSurface.gatedInHandler resolved |> List.map _.Endpoint)
                (before |> List.map _.Endpoint)
                "and the entries that moved are exactly the entries that were there — same set, same order"

            Expect.equal
                (AuthorizationSurface.anonymousAtAttributeLayer resolved |> List.length)
                expected
                "the dispatcher-level question is unchanged: all of them are still anonymous at the ATTRIBUTE layer"

            for entry in AuthorizationSurface.gatedInHandler resolved do
                Expect.isTrue
                    (entry.Requires
                     |> List.exists (fun token -> token.StartsWith("gate:in-handler=", StringComparison.Ordinal)))
                    (sprintf "%s carries the rationale a reviewer needs to check the claim" entry.Endpoint)

    testList "shipped in-handler gate declarations (627.E)" [

        sweepCase
            "JobApi — all 8 methods declared"
            (AuthorizationSurface.ofApiRecord<JobApi> componentId)
            (PlatformInHandlerGates.jobApi componentId)
            8

        sweepCase
            "ModelExecutionApi — all 7 methods declared"
            (AuthorizationSurface.ofApiRecord<ModelExecutionApi> componentId)
            (PlatformInHandlerGates.modelExecutionApi componentId)
            7

        sweepCase
            "IModuleQueryBusApi — the 1 method declared"
            (AuthorizationSurface.ofApiRecord<IModuleQueryBusApi> componentId)
            (PlatformInHandlerGates.moduleQueryBusApi componentId)
            1

        test "a declaration naming a method the record does not carry is inert, not a lie" {
            // The stale-declaration case the mechanism is documented to
            // tolerate: these lists are authored beside handlers and the
            // surface is derived from records, so a rename leaves a
            // declaration pointing at nothing. It must not invent an
            // entry, and it must not fail a composition.
            let surface = AuthorizationSurface.ofApiRecord<JobApi> componentId

            let stale = {
                GatedComponent = componentId
                GatedEndpoint = "JobApi.RenamedAwayLastWeek"
                GatedRationale = "a check on a method that no longer exists"
            }

            let resolved = AuthorizationSurface.resolveWithInHandlerGates [ stale ] surface

            Expect.equal resolved surface "a declaration matching nothing changes nothing"
        }

        test "the three platform records account for 16 of the 32 undeclared methods" {
            // The arithmetic the tidy-up item states, asserted rather than
            // recited — `IFormApi`'s other 16 are pinned in
            // ToolUp.Forms.Tests, which is the pack that can see them.
            let count (surface: AuthorizationSurface) =
                AuthorizationSurface.anonymousReachable surface |> List.length

            let total =
                count (AuthorizationSurface.ofApiRecord<JobApi> componentId)
                + count (AuthorizationSurface.ofApiRecord<ModelExecutionApi> componentId)
                + count (AuthorizationSurface.ofApiRecord<IModuleQueryBusApi> componentId)

            Expect.equal total 16 "8 + 7 + 1 — if a record grew a method, its declarations need the new one too"

            Expect.equal (List.length (PlatformInHandlerGates.all componentId)) 16 "and every one of them is declared"
        }
    ]

// ─── Phase 554 — the grant-authority facet ────────────────────────────
//
// The facet answers a question one level up from the two projections
// above: not "who reaches this component" but **who can hand out access
// to it, by which path, and what must be true first**.
//
// Two properties are asserted here and they are different in kind. The
// DERIVATION cases pin what the facet says about a fixture composition —
// ordinary manifest assertions. The DRIFT GUARD (554.C) asserts the
// facet is COMPLETE: it reflects over the real `IPermissionStore` and the
// real grant entry points and fails on a member the write-path table does
// not classify. That is the case that keeps the facet honest, because the
// only way a meta-authority manifest can be dangerous is by
// under-reporting who can grant — and a table nobody checks drifts
// silently in exactly that direction.

let private ledgerParty = PartyRef.create "acme-dpo"

/// Four modules spanning every `GrantPolicy` arm: one that declares
/// nothing (so it must be absent from the facet entirely), and one for
/// each declared arm.
let private policyBearingModules () : ServerModule list = [
    // Declares nothing — `AdminDiscretion` is the default.
    ServerModule.create "Reporting"
    |> ServerModule.withComponentId "reporting-service"

    ServerModule.create "Ledger"
    |> ServerModule.withComponentId "ledger-service"
    |> ServerModule.withGrantPolicy GrantPolicy.RequiresAcknowledgement

    // No explicit ComponentId — the entry must fall back to the
    // Name-derived identity, exactly as the exposed surface does.
    ServerModule.create "Payroll"
    |> ServerModule.withGrantPolicy GrantPolicy.RequiresSubjectConsent

    ServerModule.create "ClinicalTrial"
    |> ServerModule.withComponentId "trial-service"
    |> ServerModule.withGrantPolicy (GrantPolicy.RequiresCounterpartyApproval ledgerParty)
]

let private authority = GrantAuthoritySurface.ofModules (policyBearingModules ())

let private authorityEntry (moduleName: string) =
    match GrantAuthoritySurface.entryFor moduleName authority with
    | Some entry -> entry
    | None -> failwithf "the facet carries no entry for '%s'" moduleName

let private grantAuthorityDerivation =
    testList "grant-authority facet — derivation (554.A)" [

        test "only modules that DECLARED a policy appear, in module-name order" {
            // Asserting the whole list rather than a containment: an extra
            // entry — most importantly an `AdminDiscretion` module quietly
            // rostered as "an administrator may grant it" — fails here.
            Expect.equal
                (authority.Authority
                 |> List.map (fun e ->
                     e.AuthorityModule, ComponentId.value e.AuthorityComponent, GrantPolicy.toToken e.AuthorityPolicy))
                [
                    "ClinicalTrial", "module:trial-service", "requires-counterparty-approval:acme-dpo"
                    "Ledger", "module:ledger-service", "requires-acknowledgement"
                    "Payroll", "module:Payroll", "requires-subject-consent"
                ]
                "three declared arms, name-ordered, with the declaring module's own component identity"

            Expect.isNone
                (GrantAuthoritySurface.entryFor "Reporting" authority)
                "a module that declared nothing has declared nothing — it is not rostered as a non-declaration"
        }

        test "the facet is derived from the registration, not from a table naming modules" {
            // The GP 9 property, asserted directly: a module invented here
            // and named nowhere in the SDK surfaces with its policy.
            let invented =
                ServerModule.create "SomethingNobodyNamed"
                |> ServerModule.withGrantPolicy GrantPolicy.RequiresAcknowledgement

            let derived = GrantAuthoritySurface.ofModules [ invented ]

            Expect.equal
                (derived.Authority |> List.map _.AuthorityModule)
                [ "SomethingNobodyNamed" ]
                "no code in AuthorizationSurface.fs names this module, and it is in the facet"
        }

        test "a counterparty module names its party, its principals, and its only open paths" {
            // The acceptance sentence, asserted as data: this is what a
            // counterparty inspects instead of trusting prose.
            let entry = authorityEntry "ClinicalTrial"

            Expect.equal
                entry.AuthorityPolicy
                (GrantPolicy.RequiresCounterpartyApproval ledgerParty)
                "the declared policy names the party"

            Expect.equal
                entry.AuthorityPrincipals
                [ PlatformAdminPrincipal; ServiceAccountPrincipal; CounterpartyPrincipal ]
                "no path reaches this module without the named counterparty being part of the write"

            Expect.equal
                entry.AuthorityOpenPaths
                [
                    "GrantConsentStore.grantWithCounterpartyApproval"
                    "IPermissionStore.SetTeamPermissions"
                ]
                "and those are the only two paths still open against it"

            Expect.equal
                (GrantAuthoritySurface.counterpartyModules authority)
                [ "ClinicalTrial", ledgerParty ]
                "the counterparty's own query finds exactly its module"
        }

        test "a subject-consent module puts the GRANTEE in the authority chain" {
            // The finding a review is most likely to miss: under this arm
            // the grant is written PENDING and the grantee's own
            // acceptance is what confers authority, so the subject is a
            // grant-writing principal and not merely a beneficiary.
            let entry = authorityEntry "Payroll"

            Expect.contains
                entry.AuthorityPrincipals
                GranteeSubjectPrincipal
                "the acceptance path is open, so the grantee completes the authority"

            Expect.contains entry.AuthorityOpenPaths "PermissionGrants.acceptGrant" "and it is named"

            Expect.isFalse
                ((authorityEntry "Ledger").AuthorityPrincipals
                 |> List.contains GranteeSubjectPrincipal)
                "under acknowledgement there is nothing for the grantee to accept, so they are not in the chain"
        }

        test "the evidence-free write paths are closed against every declared arm" {
            // `SetMemberPermissions` and `SetTeamDefaults` have nowhere to
            // carry an acknowledgement, which is why Phase 551 refuses
            // them by construction. The facet must say so rather than
            // listing them as available.
            for entry in authority.Authority do
                Expect.isFalse
                    (entry.AuthorityOpenPaths
                     |> List.contains "IPermissionStore.SetMemberPermissions")
                    (sprintf "%s: the legacy per-member write cannot carry evidence" entry.AuthorityModule)

                Expect.isFalse
                    (entry.AuthorityOpenPaths |> List.contains "IPermissionStore.SetTeamDefaults")
                    (sprintf "%s: a team default has no subject to record against" entry.AuthorityModule)

                Expect.isFalse
                    (entry.AuthorityOpenPaths |> List.contains "IPermissionStore.SetModuleExposure")
                    (sprintf "%s: exposure is visibility, never authority" entry.AuthorityModule)

                Expect.isNonEmpty
                    entry.AuthorityPreconditions
                    (sprintf "%s: every open path states what it demands" entry.AuthorityModule)
        }

        test "an undeclared module reports the ADMIN classes, never the empty set" {
            // The one misreading a meta-authority manifest must not
            // produce: "no entry" means nothing was narrowed, not that
            // nobody can grant.
            let principals = GrantAuthoritySurface.principalsOf "Reporting" authority

            Expect.isNonEmpty principals "an undeclared module is grantable by the ordinary admin classes"

            Expect.contains principals PlatformAdminPrincipal "including the broadest one"

            Expect.isFalse
                (principals |> List.contains CounterpartyPrincipal)
                "but not by a counterparty — no module named one"
        }
    ]

// ── the drift guard (554.C) ───────────────────────────────────────────

/// The two surfaces the guard reflects over, resolved from the shipped
/// assembly rather than named as strings, so a rename is a compile error
/// or a loud failure rather than a silently-empty check.
let private permissionStoreType =
    typeof<ToolUp.Platform.PermissionStore.IPermissionStore>

let private moduleTypeNamed (name: string) =
    permissionStoreType.Assembly.GetTypes()
    |> Array.tryFind (fun t -> t.Name = name && t.IsAbstract && t.IsSealed)

/// Public, declared, non-property members of a type — the shape both an
/// F# interface's abstract members and an F# module's functions take.
let private publicMembersOf (t: Type) =
    t.GetMethods(
        Reflection.BindingFlags.Public
        ||| Reflection.BindingFlags.Static
        ||| Reflection.BindingFlags.Instance
        ||| Reflection.BindingFlags.DeclaredOnly
    )
    |> Array.filter (fun m -> not m.IsSpecialName)
    |> Array.map _.Name
    |> Array.distinct
    |> Array.sort
    |> List.ofArray

let private pathsOnSurface (surfaceName: string) =
    GrantAuthoritySurface.platformWritePaths
    |> List.filter (fun p -> p.PathSurface = surfaceName)
    |> List.map _.PathMember
    |> List.sort

let private grantAuthorityDriftGuard =
    testList "grant-authority facet — drift guard (554.C)" [

        test "every mutating IPermissionStore member is classified by the write-path table" {
            // The completeness half. `Get`-prefixed members are reads;
            // EVERYTHING ELSE must be classified — not merely everything
            // named `Set*`, so a member arriving under any other verb
            // fails here rather than being missed by a name heuristic.
            let members = publicMembersOf permissionStoreType

            let reads =
                members |> List.filter (fun m -> m.StartsWith("Get", StringComparison.Ordinal))

            let mutators = members |> List.filter (fun m -> not (List.contains m reads))

            Expect.isNonEmpty members "the reflection found the interface — an empty member list would pass vacuously"

            Expect.equal
                mutators
                (pathsOnSurface "IPermissionStore")
                "every member that is not a read is classified in AuthorizationSurface's write-path table, and the table names no member that does not exist"

            Expect.equal
                (List.length members)
                (List.length reads + List.length mutators)
                "the partition is total — a member is a read or it is classified"
        }

        test "every grant entry point is classified by the write-path table" {
            // The half with the cheap falsifier: adding a public function
            // to `PermissionGrants` is exactly what "an unenumerated grant
            // path" looks like, and it fails here by name.
            let permissionGrants = moduleTypeNamed "PermissionGrants"

            Expect.isSome
                permissionGrants
                "the PermissionGrants module was found — a rename must fail loudly, not silently pass"

            let members = publicMembersOf permissionGrants.Value

            Expect.isNonEmpty members "the reflection found its functions"

            Expect.equal
                members
                (pathsOnSurface "PermissionGrants")
                "every public grant entry point is classified, and the table names no function that does not exist"
        }

        test "the counterparty entry point the table names is a real function" {
            // `GrantConsentStore` is a large module whose members are
            // mostly not write paths, so it is not swept wholesale. What
            // is checked is the other direction — the row the table
            // carries names something that exists.
            let consentModule = moduleTypeNamed "GrantConsentStore"

            Expect.isSome consentModule "the GrantConsentStore module was found"

            let members = publicMembersOf consentModule.Value

            for named in pathsOnSurface "GrantConsentStore" do
                Expect.contains members named "the table names a function this module actually declares"
        }

        test "every classified path is usable as a manifest entry" {
            // A row that named no principal, proved nothing, or explained
            // nothing would pass the completeness checks above and still
            // be useless — worse, a row with no principals would silently
            // shrink the principal set of every module it stays open on.
            for path in GrantAuthoritySurface.platformWritePaths do
                let identity = GrantWritePath.identity path

                Expect.isNonEmpty path.PathPrincipals (sprintf "%s names the principal classes that reach it" identity)

                Expect.isNonEmpty path.PathSatisfies (sprintf "%s says what it can prove" identity)

                Expect.isFalse
                    (String.IsNullOrWhiteSpace path.PathDemands)
                    (sprintf "%s states what it demands, in a line a reviewer can read" identity)

                for principal in path.PathPrincipals do
                    Expect.contains
                        GrantPrincipalClass.all
                        principal
                        (sprintf "%s names a principal class the vocabulary knows" identity)

            Expect.equal
                (GrantAuthoritySurface.platformWritePaths
                 |> List.map GrantWritePath.identity
                 |> List.distinct
                 |> List.length)
                (List.length GrantAuthoritySurface.platformWritePaths)
                "path identities are unique — two rows for one path would double-count its principals"
        }

        test "every module declaring a non-default policy appears in the facet" {
            // The other completeness axis: the facet must never drop a
            // declaration. Quantified over every arm rather than over the
            // fixture, so a new `GrantPolicy` case that the derivation
            // filtered away would fail here.
            let arms = [
                GrantPolicy.RequiresAcknowledgement
                GrantPolicy.RequiresSubjectConsent
                GrantPolicy.RequiresCounterpartyApproval(PartyRef.create "some-party")
            ]

            for arm in arms do
                let name = "Module-" + GrantPolicy.toToken arm

                let derived =
                    GrantAuthoritySurface.ofModules [ ServerModule.create name |> ServerModule.withGrantPolicy arm ]

                match GrantAuthoritySurface.entryFor name derived with
                | None -> failtestf "the facet dropped a module declaring '%s'" (GrantPolicy.toToken arm)
                | Some entry ->
                    Expect.isNonEmpty
                        entry.AuthorityPrincipals
                        (sprintf "'%s' names at least one principal able to write a grant" (GrantPolicy.toToken arm))

                    Expect.isNonEmpty
                        entry.AuthorityOpenPaths
                        (sprintf "'%s' names at least one path still open" (GrantPolicy.toToken arm))
        }
    ]

// ── determinism + wire projection (554.B) ─────────────────────────────

let private grantAuthorityProjection =
    testList "grant-authority facet — deterministic projection (554.B)" [

        test "the derivation is repeatable and independent of registration order" {
            Expect.equal
                (GrantAuthoritySurface.ofModules (policyBearingModules ()))
                (GrantAuthoritySurface.ofModules (policyBearingModules ()))
                "deriving twice from the same registrations yields the same value"

            Expect.equal
                (GrantAuthoritySurface.ofModules (policyBearingModules () |> List.rev))
                authority
                "a composition that registered the same modules in a different order derives the same facet"
        }

        test "the rendered artifact is byte-stable and carries no clock" {
            let once = GrantAuthoritySurface.render authority

            let twice =
                GrantAuthoritySurface.render (GrantAuthoritySurface.ofModules (policyBearingModules ()))

            Expect.equal twice once "two runs over the same composition produce identical text"

            Expect.equal
                (GrantAuthoritySurface.render (GrantAuthoritySurface.ofModules (policyBearingModules () |> List.rev)))
                once
                "…and so does a run over a differently-ordered registration"

            Expect.isFalse
                (Text.RegularExpressions.Regex.IsMatch(once, @"\d{4}-\d{2}-\d{2}"))
                "the artifact is a review document, not a log line — a date in it would diff on every run"

            Expect.stringContains
                once
                "ClinicalTrial [requires-counterparty-approval:acme-dpo]"
                "the headline names the module and its declared policy"

            Expect.stringContains
                once
                "    via GrantConsentStore.grantWithCounterpartyApproval"
                "and the paths are named under it"
        }

        test "an empty composition renders the honest empty artifact" {
            Expect.equal
                (GrantAuthoritySurface.ofModules [])
                GrantAuthoritySurface.empty
                "nothing composed, nothing declared"

            Expect.equal
                (GrantAuthoritySurface.ofModules [ ServerModule.create "Quiet" ])
                GrantAuthoritySurface.empty
                "a module that declares no policy contributes no entry"

            Expect.equal
                (GrantAuthoritySurface.render GrantAuthoritySurface.empty)
                "(no module declares a grant policy)"
                "and the artifact says so rather than being blank"
        }

        test "the wire projection round-trips exactly" {
            Expect.equal
                (GrantAuthoritySurface.ofWire (GrantAuthoritySurface.toWire authority))
                authority
                "persisting and reading back is the identity"

            Expect.equal
                (GrantAuthoritySurface.toWire authority |> List.map _.AuthorityModuleName)
                [ "ClinicalTrial"; "Ledger"; "Payroll" ]
                "the persisted order is the derived order"
        }

        test "reading a persisted facet back is FAIL-CLOSED on both axes" {
            // A baseline written by a newer deployment must read back as
            // more constrained than it may be, never as less — the same
            // posture `AccessClassification.ofLabel` takes one projection
            // up.
            let foreign = {
                AuthorityModuleName = "FromTheFuture"
                AuthorityComponentIdentity = "module:FromTheFuture"
                AuthorityPolicyToken = "requires-something-this-node-has-never-heard-of"
                AuthorityPrincipalLabels = [ "quartermaster" ]
                AuthorityPathIdentities = []
                AuthorityPreconditionLines = []
            }

            let read = GrantAuthoritySurface.ofWire [ foreign ]
            let entry = List.exactlyOne read.Authority

            Expect.notEqual
                entry.AuthorityPolicy
                GrantPolicy.AdminDiscretion
                "an unreadable policy token never reads back as 'anyone with admin may grant this'"

            Expect.equal
                entry.AuthorityPolicy
                GrantPolicy.strictestConstructible
                "it reads as the strictest arm this node can construct"

            Expect.equal
                entry.AuthorityPrincipals
                [ PlatformAdminPrincipal ]
                "and an unreadable principal label reads as the BROADEST class — a manifest of authority may overstate who can grant, never understate it"
        }

        test "deriving the facet contributes no DI registration and no runtime weight" {
            let services = ServiceCollection()
            let before = services.Count

            let derived = GrantAuthoritySurface.ofModules (policyBearingModules ())

            Expect.isNonEmpty derived.Authority "the facet is genuinely derived"

            Expect.equal
                services.Count
                before
                "there is no serviceRegistration closure in this facet at all — a deployment that never reads it composes byte-for-byte what it did before (GP 13)"
        }
    ]

let tests =
    testList "AuthorizationSurface" [
        derivation
        classification
        remoting
        policyResolution
        diffing
        wire
        zeroFootprint
        inHandlerGates
        grantAuthorityDerivation
        grantAuthorityDriftGuard
        grantAuthorityProjection
    ]