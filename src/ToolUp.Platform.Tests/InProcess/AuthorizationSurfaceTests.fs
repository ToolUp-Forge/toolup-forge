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

let tests =
    testList "AuthorizationSurface" [
        derivation
        classification
        remoting
        policyResolution
        diffing
        wire
        zeroFootprint
    ]