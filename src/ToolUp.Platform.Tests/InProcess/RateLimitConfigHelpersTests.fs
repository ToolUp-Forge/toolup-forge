module ToolUp.Platform.Tests.InProcess.RateLimitConfigHelpersTests

open System
open Expecto
open ToolUp.Platform

let private policy: RateLimitPolicy = {
    PermitLimit = 100
    WindowSeconds = 60
    QueueLimit = 20
}

let private tightAnon: RateLimitPolicy = {
    PermitLimit = 10
    WindowSeconds = 60
    QueueLimit = 0
}

let private claim (tokenId: string) : ShareTokenClaim = {
    TokenId = tokenId
    ScopeId = "scope-1"
    ResourceKind = "forms.publishable"
    ResourceId = "form-1"
    AttributedHandle = None
    IssuedBy = "issuer-1"
    IssuedAt = DateTimeOffset.UnixEpoch
    ExpiresAt = DateTimeOffset.UnixEpoch.AddDays 1.0
    UseLimit = None
    UsedCount = 0
    Revoked = false
    RateLimit = None
}

[<Tests>]
let tests =
    testList "RateLimitConfig helpers (Phase 66 C.3)" [

        testList "isEnabled" [
            test "none → false" {
                Expect.isFalse (RateLimitConfig.isEnabled RateLimitConfig.none) "no policies = disabled"
            }
            test "uniform → true (Default present)" {
                Expect.isTrue (RateLimitConfig.isEnabled (RateLimitConfig.uniform policy)) "Default policy enables"
            }
            test "perShape with one entry → true" {
                let config = RateLimitConfig.perShape (Map.ofList [ AnonymousKind, tightAnon ])
                Expect.isTrue (RateLimitConfig.isEnabled config) "a PerShape entry enables"
            }
            test "perShape empty map → false" {
                Expect.isFalse (RateLimitConfig.isEnabled (RateLimitConfig.perShape Map.empty)) "empty = disabled"
            }
        ]

        testList "policyFor" [
            test "none → no policy for any kind" {
                Expect.isNone (RateLimitConfig.policyFor RateLimitConfig.none UserKind) "nothing configured"
            }
            test "uniform → Default for every kind" {
                let config = RateLimitConfig.uniform policy
                Expect.equal (RateLimitConfig.policyFor config AnonymousKind) (Some policy) "anon falls back to Default"
                Expect.equal (RateLimitConfig.policyFor config UserKind) (Some policy) "user falls back to Default"
            }
            test "perShape → matched kind hits its override, unmatched kind is None" {
                let config = RateLimitConfig.perShape (Map.ofList [ AnonymousKind, tightAnon ])
                Expect.equal (RateLimitConfig.policyFor config AnonymousKind) (Some tightAnon) "matched override"
                Expect.isNone (RateLimitConfig.policyFor config UserKind) "no Default, unmatched kind unlimited"
            }
            test "withOverrides → override wins for its kind, Default for the rest" {
                let config =
                    RateLimitConfig.withOverrides policy (Map.ofList [ AnonymousKind, tightAnon ])

                Expect.equal (RateLimitConfig.policyFor config AnonymousKind) (Some tightAnon) "override wins"
                Expect.equal (RateLimitConfig.policyFor config UserKind) (Some policy) "others fall back to Default"
                Expect.equal (RateLimitConfig.policyFor config TeamMemberKind) (Some policy) "team falls back too"
            }
        ]

        testList "partitionFor" [
            test "anonymous → partitions on supplied client IP" {
                let key =
                    RateLimitPolicy.partitionFor "203.0.113.7" (AnonymousSession "session-abc")

                Expect.equal key "ip:203.0.113.7" "anonymous keyed on IP, not the client-minted session id"
            }
            test "authenticated user → partitions on user id" {
                let key = RateLimitPolicy.partitionFor "203.0.113.7" (AuthenticatedUser "user-42")
                Expect.equal key "user:user-42" "keyed on user id, IP ignored"
            }
            test "team member → partitions on team id (shared team budget)" {
                let key =
                    RateLimitPolicy.partitionFor "203.0.113.7" (TeamMember("user-42", "team-9"))

                Expect.equal key "team:team-9" "team members share one budget keyed on team id"
            }
            test "claim bearer → partitions on token id" {
                let key = RateLimitPolicy.partitionFor "203.0.113.7" (ClaimBearer(claim "tok-xyz"))
                Expect.equal key "token:tok-xyz" "keyed on token id"
            }
        ]
    ]