module ToolUp.Platform.Tests.InProcess.AccessContextFactoryTests

// Phase 1059 — the request's `AccessContext` takes its `UserId` and `TeamId`
// from the resolved `Subject`, by `AccessContext.principalId`'s rule.
//
// A `tusa_` service-account bearer is validated by
// `ServiceAccountTokenMiddleware`, not by the auth provider: the provider
// does not recognise it and answers `AuthenticatedUser.anonymous`, so the
// middleware stamps `ToolUp.UserId = "anonymous"`. The factory used to read
// that stamp, so every row keyed on the context's `UserId` (or on
// `PlatformApiHandler.resolveUserId`, which reads the same stamp) attributed a
// machine call to "anonymous" — while `ServiceAccountTokenHandler.claimFor`
// promises the account id. The team id likewise came from the storage
// container's prefix rather than from the `TeamMember` subject.

open System
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose

let private accountId = "sa-1059"

let private serviceAccountClaim: ShareTokenClaim = {
    TokenId = "tusa_1059"
    ScopeId = "team-acme"
    ResourceKind = "service-account"
    ResourceId = accountId
    AttributedHandle = Some accountId
    IssuedBy = accountId
    IssuedAt = DateTimeOffset.UtcNow
    ExpiresAt = DateTimeOffset.UtcNow.AddHours 1.0
    UseLimit = None
    UsedCount = 0
    Revoked = false
    RateLimit = None
}

/// A request the middleware resolved for `user` (the auth provider's) and
/// `subject`, with nothing stamped yet.
let private stamped (user: AuthenticatedUser) (subject: Subject) (scope: StorageScope) =
    let ctx = DefaultHttpContext()
    ctx, user, subject, scope

/// The principal `ScopeResolutionMiddleware` stamps for that resolution,
/// for a subject to which no module RBAC applies.
let private principalOf (ctx: HttpContext, user, subject, scope) =
    Middleware.principalOf ctx user subject (Some scope) ModulePermissions.Unrestricted Map.empty None []

let tests =
    testList "Phase 1059 — the access context's UserId and TeamId come from the Subject" [

        test "a ClaimBearer from a tusa_ bearer the auth provider calls anonymous carries the account id" {
            let ctx =
                stamped AuthenticatedUser.anonymous (ClaimBearer serviceAccountClaim) {
                    ScopeId = "acme"
                    Container = "team-acme"
                    Persist = true
                }

            let access = principalOf ctx
            Expect.equal access.UserId accountId "AccessContext.UserId is the service account, never \"anonymous\""
        }

        test "PlatformApiHandler.resolveUserId attributes the same machine call to the account" {
            let ctx =
                stamped AuthenticatedUser.anonymous (ClaimBearer serviceAccountClaim) {
                    ScopeId = "acme"
                    Container = "team-acme"
                    Persist = true
                }

            let http, _, _, _ = ctx
            AccessContext.stamp http.Items (principalOf ctx)
            Expect.equal (PlatformApiHandler.resolveUserId http) accountId "handler audit rows name the account"
        }

        test "a TeamMember's TeamId is the subject's team, whatever the container is called" {
            let user = {
                AuthenticatedUser.anonymous with
                    UserId = UserId "u-1059"
                    DisplayName = "Member"
            }

            let ctx =
                stamped user (TeamMember("u-1059", "acme")) {
                    ScopeId = "acme"
                    Container = "tenant-acme"
                    Persist = true
                }

            let access = principalOf ctx
            Expect.equal access.TeamId (Some "acme") "TeamId is read from TeamMember(_, teamId)"
            Expect.equal access.UserId "u-1059" "a member's UserId is its user id"
        }

        test "an AuthenticatedUser in a team-prefixed container carries no TeamId" {
            let user = {
                AuthenticatedUser.anonymous with
                    UserId = UserId "u-1059"
                    DisplayName = "Member"
            }

            let ctx =
                stamped user (Subject.AuthenticatedUser "u-1059") {
                    ScopeId = "u-1059"
                    Container = "team-lookalike"
                    Persist = true
                }

            let access = principalOf ctx
            Expect.equal access.TeamId None "a container prefix is not evidence of team membership"
        }

        test "Phase 882 — a stamped subject with no permission stamp is denied a module the team restricts" {
            let user = {
                AuthenticatedUser.anonymous with
                    UserId = UserId "u-882"
                    DisplayName = "Member"
            }

            let ctx =
                stamped user (TeamMember("u-882", "acme")) {
                    ScopeId = "acme"
                    Container = "team-acme"
                    Persist = true
                }

            let http, _, _, _ = ctx

            Expect.isNone
                (ComposeScopeResolver.accessContextOf http)
                "a subject the middleware never completed a principal for yields no context"

            let access = AccessContext.unresolved "AccessContextFactoryTests"
            Expect.isFalse (AccessContext.canAccessModule "Reports" access) "no permission stamp grants nothing"
        }

        test "Phase 882 — an empty permission map does not read as unrestricted" {
            let access = {
                AccessContexts.forSubject (TeamMember("u-882", "acme")) with
                    ModulePermissions = ModulePermissions.Restricted Map.empty
            }

            Expect.isFalse (AccessContext.canAccessModule "Reports" access) "an empty map grants nothing"
        }

        test
            "Phase 882 — a machine principal never reads as anonymous: caller id, viewer, team-policy viewer and actor name the account" {
            let ctx =
                stamped AuthenticatedUser.anonymous (ClaimBearer serviceAccountClaim) {
                    ScopeId = "acme"
                    Container = "team-acme"
                    Persist = true
                }

            let http, _, _, _ = ctx
            let access = principalOf ctx
            AccessContext.stamp http.Items access

            Expect.equal (AccessContext.callerId access) accountId "callerId"
            Expect.equal (AccessContext.callerIdOf http.Items) (Some accountId) "callerIdOf the items"

            Expect.equal
                (ToolUp.Platform.Scope.RequestViewer.ofItems http.Items |> Option.map _.UserId)
                (Some(UserId accountId))
                "the request viewer"

            let viewer =
                ToolUp.Platform.Scope.TeamPolicyStore.TeamPolicyViewer.resolve
                    (ServiceCollection().BuildServiceProvider())
                    (Some access)
                    "acme"
                |> Async.RunSynchronously

            Expect.equal viewer.UserId (UserId accountId) "the team-policy viewer"
            Expect.equal (AccessContext.entityPrincipal access).Principal accountId "the entity-store actor"
        }

        test "Phase 882 — an anonymous session's caller id is anonymous, whatever its session id" {
            let access = AccessContexts.anonymous "sess-882"

            Expect.equal
                (AccessContext.callerId access)
                "anonymous"
                "per-user reads key an anonymous caller as anonymous"
        }
    ]