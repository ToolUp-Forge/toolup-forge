module ToolUp.Platform.Tests.InProcess.AccessContextFactoryTests

// Phase 1059 — the request's `AccessContext` takes its `UserId` and `TeamId`
// from the resolved `Subject`, by `AccessContext.unrestricted`'s rule.
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
open ToolUp.Platform
open ToolUp.Platform.Auth

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

/// The `HttpContext.Items` `ScopeResolutionMiddleware` leaves for a request
/// whose auth-provider user is `user` and whose resolved subject is `subject`.
let private stamped (user: AuthenticatedUser) (subject: Subject) (scope: StorageScope) =
    let ctx = DefaultHttpContext()
    ctx.Items["ToolUp.User"] <- box user
    ctx.Items["ToolUp.UserId"] <- box user.UserId
    ctx.Items["ToolUp.Subject"] <- box subject
    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx

let tests =
    testList "Phase 1059 — the access context's UserId and TeamId come from the Subject" [

        test "a ClaimBearer from a tusa_ bearer the auth provider calls anonymous carries the account id" {
            let ctx =
                stamped AuthenticatedUser.anonymous (ClaimBearer serviceAccountClaim) {
                    ScopeId = "acme"
                    Container = "team-acme"
                    Persist = true
                }

            let access = ComposeScopeResolver.accessContextOf ctx
            Expect.equal access.UserId accountId "AccessContext.UserId is the service account, never \"anonymous\""
        }

        test "PlatformApiHandler.resolveUserId attributes the same machine call to the account" {
            let ctx =
                stamped AuthenticatedUser.anonymous (ClaimBearer serviceAccountClaim) {
                    ScopeId = "acme"
                    Container = "team-acme"
                    Persist = true
                }

            Expect.equal (PlatformApiHandler.resolveUserId ctx) accountId "handler audit rows name the account"
        }

        test "a TeamMember's TeamId is the subject's team, whatever the container is called" {
            let user = {
                AuthenticatedUser.anonymous with
                    UserId = "u-1059"
                    DisplayName = "Member"
            }

            let ctx =
                stamped user (TeamMember("u-1059", "acme")) {
                    ScopeId = "acme"
                    Container = "tenant-acme"
                    Persist = true
                }

            let access = ComposeScopeResolver.accessContextOf ctx
            Expect.equal access.TeamId (Some "acme") "TeamId is read from TeamMember(_, teamId)"
            Expect.equal access.UserId "u-1059" "a member's UserId is its user id"
        }

        test "an AuthenticatedUser in a team-prefixed container carries no TeamId" {
            let user = {
                AuthenticatedUser.anonymous with
                    UserId = "u-1059"
                    DisplayName = "Member"
            }

            let ctx =
                stamped user (Subject.AuthenticatedUser "u-1059") {
                    ScopeId = "u-1059"
                    Container = "team-lookalike"
                    Persist = true
                }

            let access = ComposeScopeResolver.accessContextOf ctx
            Expect.equal access.TeamId None "a container prefix is not evidence of team membership"
        }
    ]