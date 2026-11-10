module ToolUp.Platform.Tests.InProcess.AuthorizationTests

open System
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Platform.Config

// ─── Phase 69d.tail — per-method authorization metadata, classifier
// default-on ─────────────────────────────────────────────────────────
//
// Contract tests for the dispatcher's startup classifier + per-request
// evaluation:
//   * classification recognises BOTH attribute families — the
//     server-tier `ToolUp.Remoting.Server.*` set and the tier-shared
//     `ToolUp.Platform.*` mirrors that Fable-compiled API records carry
//     (simple-name matching, normalised to `AuthRequirement` data at
//     startup);
//   * per-attribute predicate evaluation (role / claim / tenant),
//     including multi-attribute AND semantics;
//   * fail-closed behaviour (anonymous denied on RequiresAuth methods,
//     no-resolver denied, unclassified denied at runtime as belt+braces);
//   * `Api.make` refuses startup on a record with unclassified methods
//     (the Phase 69d.tail default-on flip), and composes cleanly for a
//     fully-annotated record;
//   * every forge SDK API record reachable from this test pack is fully
//     classified (the sweep-enforcement test — a regression gate for
//     "someone added a method without an attribute").

// ── Test fixture records ────────────────────────────────────────────

/// Server-family attributes (this assembly's own attribute set).
type private ServerAttributedApi = {
    [<RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<RequiresClaim "scope">]
    NeedsScope: unit -> Async<int>
    [<TenantScoped>]
    TenantOnly: unit -> Async<int>
    [<AllowAnonymous>]
    OpenToAll: unit -> Async<int>
    [<PublicEndpoint>]
    PublicSurface: unit -> Async<int>
    [<RequiresRole "Admin">]
    [<TenantScoped>]
    AdminAndTenant: unit -> Async<int>
}

/// Platform-mirror attributes (the tier-shared `ToolUp.Platform.Core`
/// set that Fable-compiled API records carry). The classifier must
/// produce identical classifications for this record — the simple-name
/// recognition is the load-bearing Phase 69d.tail substrate change.
type private MirrorAttributedApi = {
    [<ToolUp.Platform.RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<ToolUp.Platform.RequiresClaim "scope">]
    NeedsScope: unit -> Async<int>
    [<ToolUp.Platform.TenantScoped>]
    TenantOnly: unit -> Async<int>
    [<ToolUp.Platform.AllowAnonymous>]
    OpenToAll: unit -> Async<int>
    [<ToolUp.Platform.PublicEndpoint>]
    PublicSurface: unit -> Async<int>
    [<ToolUp.Platform.RequiresRole "Admin">]
    [<ToolUp.Platform.TenantScoped>]
    AdminAndTenant: unit -> Async<int>
}

/// One method with no classification — the startup-refusal fixture.
type private PartiallyAnnotatedApi = {
    [<AllowAnonymous>]
    Fine: unit -> Async<int>
    Naked: unit -> Async<int>
}

type private UnannotatedApi = { Naked: unit -> Async<int> }

// ── IAuthContext test double ────────────────────────────────────────

let private authContext
    (roles: string list)
    (claims: (string * string option) list)
    (hasTenant: bool)
    (anonymous: bool)
    : IAuthContext =
    { new IAuthContext with
        member _.HasRole role = List.contains role roles
        member _.HasClaim(claim, value) = List.contains (claim, value) claims
        member _.HasTenant() = hasTenant
        member _.IsAnonymous() = anonymous
        member _.SubjectId = "test-subject"
    }

let private expectDeny (decision: AuthDecision) (label: string) =
    match decision with
    | AuthDecision.Deny _ -> ()
    | AuthDecision.Allow -> failtestf "%s: expected Deny, got Allow" label

let private expectAllow (decision: AuthDecision) (label: string) =
    match decision with
    | AuthDecision.Allow -> ()
    | AuthDecision.Deny reason -> failtestf "%s: expected Allow, got Deny %s" label reason

let private classificationOf (apiType: Type) (methodName: string) =
    AuthClassifier.classify apiType
    |> Map.tryFind methodName
    |> Option.defaultWith (fun () -> failtestf "method %s not found on %s" methodName apiType.Name)

// ── Sweep-enforcement list: every forge SDK API record reachable from
// this test pack must be fully classified. Append new records here as
// they ship — a missing attribute on any method fails the sweep test
// with the method named. ──────────────────────────────────────────────
let private forgeApiRecords: (string * Type) list = [
    "PlatformAdminApi", typeof<ToolUp.Platform.PlatformAdminApi>
    "IConfigApi", typeof<ToolUp.Platform.IConfigApi>
    "IFeatureFlagApi", typeof<ToolUp.Platform.IFeatureFlagApi>
    "PlatformInfoApi", typeof<ToolUp.Platform.PlatformInfoApi>
    "TeamApi", typeof<ToolUp.Platform.TeamApi>
    "PermissionApi", typeof<ToolUp.Platform.PermissionApi>
    "AccessibilityApi", typeof<ToolUp.Platform.AccessibilityApi>
    "DataCatalogApi", typeof<ToolUp.Platform.DataCatalogApi>
    "ITeamInviteApi", typeof<ToolUp.Platform.ITeamInviteApi>
    "JobApi", typeof<ToolUp.Platform.JobApi>
    "FileManagementApi", typeof<ProcessedDataTypes.FileManagementApi>
    "IModuleQueryBusApi", typeof<ToolUp.Platform.IModuleQueryBusApi>
    "IUserDirectoryApi", typeof<ToolUp.Platform.IUserDirectoryApi>
    "IDataIngestionApi", typeof<ToolUp.Platform.IDataIngestionApi>
    "IDataMigrationApi", typeof<ToolUp.Platform.IDataMigrationApi>
    "IWebhookApi", typeof<ToolUp.Platform.IWebhookApi>
    "IHealthMonitorApi", typeof<ToolUp.Platform.IHealthMonitorApi>
    "MaintenanceApi", typeof<ToolUp.Platform.MaintenanceApi>
    "IServiceStatusBoardApi", typeof<ToolUp.Platform.IServiceStatusBoardApi>
    "IUsageQueryApi", typeof<ToolUp.Platform.Usage.IUsageQueryApi>
    "IDataSubjectRequestApi", typeof<ToolUp.Platform.DataSubjectRequestApi.IDataSubjectRequestApi>
    "IPlatformTenantApi", typeof<ToolUp.Platform.IPlatformTenantApi>
]

[<Tests>]
let tests =
    testList "Phase 69d.tail — authorization metadata" [

        // ── Phase 132 — RequiresRole "PlatformAdmin" bridges to the
        //    server-resolved PlatformRole, not the always-empty user.Roles ──
        testCaseAsync "Phase 132: HasRole PlatformAdmin reads server-resolved ToolUp.PlatformRole"
        <| async {
            let ctx = DefaultHttpContext() :> HttpContext
            ToolUp.Platform.AccessContexts.stampPlatformRole ctx.Items (ToolUp.Platform.PlatformRole.PlatformAdmin)
            let! authCtx = ToolUp.Platform.Config.ApiSeams.defaultForgeAuthContextResolver ctx
            Expect.isTrue (authCtx.HasRole "PlatformAdmin") "a real admin (PlatformRole stamped) is allowed"
        }

        testCaseAsync "Phase 132: HasRole PlatformAdmin denies when PlatformRole is absent"
        <| async {
            // Before the bridge this returned false for EVERYONE (user.Roles
            // is always empty) — including genuine admins. Now: false only
            // when the server did not resolve PlatformAdmin.
            let ctx = DefaultHttpContext() :> HttpContext
            let! authCtx = ToolUp.Platform.Config.ApiSeams.defaultForgeAuthContextResolver ctx
            Expect.isFalse (authCtx.HasRole "PlatformAdmin") "no PlatformRole stamped → denied"
        }

        // ── A method with no marker has no classification, and a composition
        //    with a resolver refuses the record rather than serve it ──
        test "an unmarked method is absent from the classification and refused under a resolver" {
            let cls = AuthClassifier.classify typeof<PartiallyAnnotatedApi>
            Expect.isFalse (cls.ContainsKey "Naked") "an unmarked method has no classification"

            let flags = {
                ContractFlags.defaults with
                    AuthResolverComposed = true
                    RecordSeamsComposed = true
            }

            match Contract.ofType flags typeof<PartiallyAnnotatedApi> with
            | Error refusals ->
                Expect.contains
                    refusals
                    (ContractRefusal.UnclassifiedMethods("PartiallyAnnotatedApi", [ "Naked" ]))
                    "the record is refused at compose, naming the unmarked method"
            | Ok _ -> failtest "a composition with a resolver must refuse an unmarked method"
        }

        // ── Phase 132 — round-trip startup assertion: every classified field
        //    name must round-trip through the active RouteBuilder ──
        test "Phase 132: nonRoundTripping is empty for the default route builder" {
            let cls = AuthClassifier.classify typeof<ServerAttributedApi>
            // Api.make's default forge route builder.
            let divergences =
                AuthClassifier.nonRoundTripping (sprintf "/api/%s/%s") "ServerAttributedApi" cls

            Expect.isEmpty divergences "default `/api/{type}/{method}` route builder round-trips every field"
        }

        test "Phase 132: nonRoundTripping is empty for the tenant API's custom route builder" {
            // The tenant API ships a custom builder (`/api/_platform/tenants/{method}`);
            // its trailing segment is still the method name, so it round-trips.
            let cls = AuthClassifier.classify typeof<ToolUp.Platform.IPlatformTenantApi>

            let divergences =
                AuthClassifier.nonRoundTripping ToolUp.Platform.PlatformTenantApi.routeBuilder "IPlatformTenantApi" cls

            Expect.isEmpty divergences "the tenant custom route builder still round-trips the method name"
        }

        test "Phase 132: nonRoundTripping flags a route builder whose trailing segment diverges" {
            let cls = AuthClassifier.classify typeof<ServerAttributedApi>
            // A builder that suffixes the method name — the runtime key
            // ("AdminOnly_v2") no longer equals the classification key.
            let badBuilder = (fun (t: string) (m: string) -> sprintf "/%s/%s_v2" t m)

            let divergences =
                AuthClassifier.nonRoundTripping badBuilder "ServerAttributedApi" cls

            Expect.isNonEmpty divergences "a non-round-tripping builder must be flagged"

            Expect.isTrue
                (divergences
                 |> List.forall (fun (field, runtimeKey) -> runtimeKey = field + "_v2"))
                "each divergence names the field and its (wrong) runtime key"
        }

        // ── Phase 132 — un-emittable role warning: a RequiresRole naming a
        //    role the default resolver can never emit is a dead gate ──
        test "Phase 132: unemittableRoles names non-PlatformAdmin role gates against the default resolver" {
            let cls = AuthClassifier.classify typeof<ServerAttributedApi>
            // `ServerAttributedApi` gates on "Admin" — un-emittable by the
            // default first-party resolver (only "PlatformAdmin" is server-
            // resolved).
            let dead = AuthClassifier.unemittableRoles (fun r -> r = "PlatformAdmin") cls
            Expect.equal dead [ "Admin" ] "the 'Admin' role gate is dead against the default resolver"
        }

        test "Phase 132: unemittableRoles is empty when every required role is PlatformAdmin" {
            // The tenant API gates exclusively on PlatformAdmin — all emittable.
            let cls = AuthClassifier.classify typeof<ToolUp.Platform.IPlatformTenantApi>
            let dead = AuthClassifier.unemittableRoles (fun r -> r = "PlatformAdmin") cls
            Expect.isEmpty dead "PlatformAdmin gates are emittable by the default resolver — no dead gates"
        }

        // ── Phase 132 — admin structural backstop middleware ──
        testList "Phase 132 — admin path-prefix backstop" [
            // Path-matching predicate: which surfaces the backstop guards.
            test "requiresPlatformAdmin gates the admin / tenant / grant prefixes" {
                let gate = ToolUp.Platform.Scope.PlatformAdminAuthorization.requiresPlatformAdmin
                Expect.isTrue (gate "GET" (PathString "/api/_platform/admin/ad-units")) "admin prefix gated"
                Expect.isTrue (gate "POST" (PathString "/api/_platform/admin/ad-units")) "admin write gated"
                Expect.isTrue (gate "GET" (PathString "/api/_platform/tenants/GetLifecycleSummary")) "tenant gated"
                Expect.isTrue (gate "POST" (PathString "/api/_platform/users/u1/premium")) "premium grant gated"
                Expect.isTrue (gate "DELETE" (PathString "/api/_platform/users/u1/premium")) "premium revoke gated"
            }

            test "requiresPlatformAdmin leaves open surfaces untouched" {
                let gate = ToolUp.Platform.Scope.PlatformAdminAuthorization.requiresPlatformAdmin
                // Public premium-status read — GET, ends `/premium-status`.
                Expect.isFalse
                    (gate "GET" (PathString "/api/_platform/users/me/premium-status"))
                    "premium-status read is public"
                // Encryption admin — has its own role-OR-token gate; not ours.
                Expect.isFalse
                    (gate "POST" (PathString "/api/_platform/encryption/destroy-scope-key/s1"))
                    "encryption endpoint keeps its dual-path gate"
                // Ordinary API.
                Expect.isFalse (gate "GET" (PathString "/api/data/load")) "ordinary API not gated"
            }

            // End-to-end middleware: deny non-admin, pass admin, leave open
            // surfaces alone — even when the downstream handler would (here)
            // always succeed (modelling a handler that omits its in-line check).
            let runBackstop (httpMethod: string) (path: string) (stampAdmin: bool) : int * bool =
                let ctx = DefaultHttpContext() :> HttpContext
                ctx.Request.Method <- httpMethod
                ctx.Request.Path <- PathString path

                if stampAdmin then
                    ToolUp.Platform.AccessContexts.stampPlatformRole
                        ctx.Items
                        (ToolUp.Platform.PlatformRole.PlatformAdmin)

                let mutable reached = false

                let next =
                    RequestDelegate(fun _ ->
                        reached <- true
                        Task.CompletedTask)

                let mw =
                    ToolUp.Platform.Scope.PlatformAdminAuthorization.PlatformAdminAuthorizationMiddleware(next)

                mw.InvokeAsync(ctx).GetAwaiter().GetResult()
                ctx.Response.StatusCode, reached

            test "non-admin is denied 403 at the backstop, never reaching the handler" {
                let status, reached = runBackstop "POST" "/api/_platform/admin/ad-units" false
                Expect.equal status 403 "non-admin denied with 403"
                Expect.isFalse reached "the handler is never invoked — fail-closed before dispatch"
            }

            test "a genuine PlatformAdmin passes the backstop to the handler" {
                let status, reached = runBackstop "POST" "/api/_platform/admin/ad-units" true
                Expect.isTrue reached "admin request reaches the handler"
                Expect.notEqual status 403 "admin is not denied by the backstop"
            }

            test "the backstop is transparent to open surfaces (premium-status read)" {
                let _, reached = runBackstop "GET" "/api/_platform/users/me/premium-status" false
                Expect.isTrue reached "public premium-status read passes through untouched"
            }
        ]

        // ── Classification: server family ──
        test "server-family attributes classify per method" {
            let cls = AuthClassifier.classify typeof<ServerAttributedApi>

            match cls["AdminOnly"] with
            | MethodClassification.RequiresAuth [ RoleRequired "Admin" ] -> ()
            | other -> failtestf "AdminOnly: unexpected classification %A" other

            match cls["NeedsScope"] with
            | MethodClassification.RequiresAuth [ ClaimRequired("scope", None) ] -> ()
            | other -> failtestf "NeedsScope: unexpected classification %A" other

            match cls["TenantOnly"] with
            | MethodClassification.RequiresAuth [ TenantRequired ] -> ()
            | other -> failtestf "TenantOnly: unexpected classification %A" other

            Expect.equal cls["OpenToAll"] MethodClassification.Anonymous "OpenToAll must classify Anonymous"
            Expect.equal cls["PublicSurface"] MethodClassification.Public "PublicSurface must classify Public"
        }

        test "mirror-family attributes classify identically (family-agnostic recognition)" {
            let server = AuthClassifier.classify typeof<ServerAttributedApi>
            let mirror = AuthClassifier.classify typeof<MirrorAttributedApi>

            Expect.equal
                mirror
                server
                "the ToolUp.Platform.* mirror attributes must produce the same \
                 classification map as the server-tier family — Fable-compiled \
                 API records carry the mirrors, and pre-69d.tail they were \
                 silently invisible to the dispatcher (the original defect)"
        }

        test "multi-attribute methods normalise to multiple requirements" {
            match classificationOf typeof<MirrorAttributedApi> "AdminAndTenant" with
            | MethodClassification.RequiresAuth requirements ->
                Expect.containsAll
                    requirements
                    [ RoleRequired "Admin"; TenantRequired ]
                    "AdminAndTenant must carry both the role and the tenant requirement"
            | other -> failtestf "AdminAndTenant: unexpected classification %A" other
        }

        test "unclassified methods are detected" {
            Expect.equal
                (AuthClassifier.unclassified typeof<PartiallyAnnotatedApi>)
                [ "Naked" ]
                "exactly the naked method is unclassified"
        }

        // ── Evaluation semantics ──
        test "role requirement allows holders, denies others" {
            let cls = classificationOf typeof<ServerAttributedApi> "AdminOnly"
            expectAllow (AuthClassifier.evaluate cls (Some(authContext [ "Admin" ] [] false false))) "role held"
            expectDeny (AuthClassifier.evaluate cls (Some(authContext [ "Member" ] [] false false))) "role missing"
        }

        test "claim requirement evaluates presence and value" {
            let cls = classificationOf typeof<ServerAttributedApi> "NeedsScope"

            expectAllow
                (AuthClassifier.evaluate cls (Some(authContext [] [ "scope", None ] false false)))
                "claim present"

            expectDeny (AuthClassifier.evaluate cls (Some(authContext [] [] false false))) "claim absent"
        }

        test "tenant requirement gates on HasTenant" {
            let cls = classificationOf typeof<ServerAttributedApi> "TenantOnly"
            expectAllow (AuthClassifier.evaluate cls (Some(authContext [] [] true false))) "tenant resolved"
            expectDeny (AuthClassifier.evaluate cls (Some(authContext [] [] false false))) "tenant missing"
        }

        test "multi-attribute evaluation is AND — every requirement must hold" {
            let cls = classificationOf typeof<ServerAttributedApi> "AdminAndTenant"

            expectAllow
                (AuthClassifier.evaluate cls (Some(authContext [ "Admin" ] [] true false)))
                "role AND tenant both held"

            expectDeny
                (AuthClassifier.evaluate cls (Some(authContext [ "Admin" ] [] false false)))
                "role held but tenant missing"

            expectDeny (AuthClassifier.evaluate cls (Some(authContext [] [] true false))) "tenant held but role missing"
        }

        test "anonymous callers are denied on RequiresAuth methods before predicates run" {
            let cls = classificationOf typeof<ServerAttributedApi> "AdminOnly"

            expectDeny
                (AuthClassifier.evaluate cls (Some(authContext [ "Admin" ] [] true true)))
                "anonymous caller denied even when the double claims the role"
        }

        test "fail-closed: RequiresAuth with no resolved context denies" {
            let cls = classificationOf typeof<ServerAttributedApi> "AdminOnly"
            expectDeny (AuthClassifier.evaluate cls None) "no auth context resolved"
        }

        test "Public and Anonymous classifications always allow" {
            expectAllow (AuthClassifier.evaluate MethodClassification.Public None) "public, no context"
            expectAllow (AuthClassifier.evaluate MethodClassification.Anonymous None) "anonymous-permitted, no context"

            expectAllow
                (AuthClassifier.evaluate MethodClassification.Anonymous (Some(authContext [] [] false true)))
                "anonymous-permitted, anonymous caller"
        }

        // ── Api.make default-on flip ──
        test "Api.make refuses startup on a record with unclassified methods" {
            let builder (_: HttpContext) : UnannotatedApi = { Naked = fun () -> async { return 1 } }

            Expect.throwsT<InvalidOperationException>
                (fun () -> ToolUp.Platform.Config.Api.make builder |> ignore)
                "Phase 69d.tail: the classifier validator is default-on — an \
                 unannotated record must refuse at composition, naming the record"
        }

        test "Api.make startup diagnostic names the record and the unclassified method" {
            let builder (_: HttpContext) : PartiallyAnnotatedApi = {
                Fine = fun () -> async { return 1 }
                Naked = fun () -> async { return 2 }
            }

            // Expecto's `Expect.throwsT` returns unit — capture the
            // exception manually to assert on the diagnostic text.
            let ex =
                try
                    ToolUp.Platform.Config.Api.make builder |> ignore
                    failtest "expected the classifier to refuse composition with InvalidOperationException"
                with :? InvalidOperationException as ex ->
                    ex

            Expect.stringContains ex.Message "PartiallyAnnotatedApi" "diagnostic names the record"
            Expect.stringContains ex.Message "Naked" "diagnostic names the unclassified method"
        }

        test "Api.make composes cleanly for a fully-annotated record (both families)" {
            let serverBuilder (_: HttpContext) : ServerAttributedApi = {
                AdminOnly = fun () -> async { return 1 }
                NeedsScope = fun () -> async { return 2 }
                TenantOnly = fun () -> async { return 3 }
                OpenToAll = fun () -> async { return 4 }
                PublicSurface = fun () -> async { return 5 }
                AdminAndTenant = fun () -> async { return 6 }
            }

            let mirrorBuilder (_: HttpContext) : MirrorAttributedApi = {
                AdminOnly = fun () -> async { return 1 }
                NeedsScope = fun () -> async { return 2 }
                TenantOnly = fun () -> async { return 3 }
                OpenToAll = fun () -> async { return 4 }
                PublicSurface = fun () -> async { return 5 }
                AdminAndTenant = fun () -> async { return 6 }
            }

            ToolUp.Platform.Config.Api.make serverBuilder |> ignore
            ToolUp.Platform.Config.Api.make mirrorBuilder |> ignore
        }

        // ── Sweep enforcement over the shipped forge SDK records ──
        testList "every forge SDK API record is fully classified" [
            for name, apiType in forgeApiRecords ->
                test name {
                    let unclassified = AuthClassifier.unclassified apiType

                    Expect.isEmpty
                        unclassified
                        (sprintf
                            "%s has unclassified method(s) %A — every forge SDK API record \
                             method must carry an authorization attribute (Phase 69d.tail); \
                             annotate the method or the deployment refuses startup"
                            name
                            unclassified)
                }
        ]
    ]