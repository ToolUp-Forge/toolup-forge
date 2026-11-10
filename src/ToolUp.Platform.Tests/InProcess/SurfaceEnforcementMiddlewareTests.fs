module ToolUp.Platform.Tests.InProcess.SurfaceEnforcementMiddlewareTests

open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Scope.SurfaceEnforcement
open ToolUp.Platform.Infra

// ─── SurfaceEnforcementMiddleware response-code matrix — Phase 66 D.2 ──
//
// Drives the real `SurfaceEnforcementMiddleware.InvokeAsync` through a
// `DefaultHttpContext` (captured response body) and asserts the actual
// HTTP status code + machine-readable error body for every row of the
// design §3.1 7-row matrix:
//
//   | Subject kind     | Surface admits | Response                       |
//   |------------------|----------------|--------------------------------|
//   | AnonymousKind    | No             | 401 authentication_required    |
//   | UserKind         | Yes            | 200 pass (next invoked)        |
//   | UserKind         | No (Team only) | 403 team_required + select_team|
//   | TeamMemberKind   | Yes            | 200 pass                       |
//   | TeamMemberKind   | No (Anon only) | 403 authenticated_subject_…    |
//   | ClaimBearerKind  | Yes            | 200 pass                       |
//   | ClaimBearerKind  | No             | 403 claim_bearer_not_admitted  |
//
// Plus the two evaluate branches the table folds into row 3 / row 5
// (`user_subject_not_admitted`, `team_member_not_admitted`), the
// `/api/*` path-scoping passthrough, the missing-Subject defensive
// evaluation (fail-closed since Phase 336 — see
// `FailClosedDispatchTests` for that seam in full), and the strict
// fail-closed global default. Complements
// `SurfaceCoherenceValidatorTests` (compose-time refusals) and
// `CsrfCarveOutDerivationTests` (the CSRF predicate over the same
// registry) by pinning the runtime gate's wire behaviour end to end —
// registry resolution → `evaluate` → `writeRejection` JSON shape.

let private aClaim: ShareTokenClaim = {
    TokenId = "token-1"
    ScopeId = "scope-1"
    ResourceKind = "forms.publishable"
    ResourceId = "form-1"
    AttributedHandle = None
    IssuedBy = "issuer-1"
    IssuedAt = System.DateTimeOffset.UnixEpoch
    ExpiresAt = System.DateTimeOffset.UnixEpoch.AddDays 1.0
    UseLimit = None
    UsedCount = 0
    Revoked = false
    RateLimit = None
}

/// Registry shaped the way composition does for a single mounted
/// module: one `/api/` prefix default → `requirement`. Drives the
/// middleware's `SurfaceRequirementRegistry.resolve` longest-prefix
/// path (not just the pure `evaluate`).
let private registryFor (requirement: SurfaceRequirement) =
    SurfaceRequirementRegistry.merge [ "/api/", requirement ] [] SurfaceRequirementRegistry.empty

/// Result of driving the middleware once: the response status code,
/// the body text (empty when the request passed through to `next`),
/// and whether `next` was actually invoked.
type private Outcome = {
    StatusCode: int
    Body: string
    NextInvoked: bool
    ContentType: string
}

let private run
    (registry: SurfaceRequirementRegistry)
    (subject: Subject option)
    (method: string)
    (path: string)
    : Outcome =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- method
    ctx.Request.Path <- PathString(path)

    let body = new MemoryStream()
    ctx.Response.Body <- body

    match subject with
    | Some s -> AccessContexts.stampSubject ctx.Items (s)
    | None -> ()

    let mutable nextInvoked = false

    let next =
        RequestDelegate(fun _ ->
            nextInvoked <- true
            Task.CompletedTask)

    let mw = SurfaceEnforcementMiddleware(next, registry)
    (mw.InvokeAsync ctx).GetAwaiter().GetResult()

    body.Position <- 0L
    let text = (new StreamReader(body)).ReadToEnd()

    {
        StatusCode = ctx.Response.StatusCode
        Body = text
        NextInvoked = nextInvoked
        ContentType =
            match ctx.Response.ContentType with
            | null -> ""
            | ct -> ct
    }

// ─── RouteClassifierTests — the one path-prefix table ────────────────
//
// Seven lists used to classify a route, each with its own prefixes. Every
// prefix any of them held is pinned here to the class that list implied,
// so the table that replaced them is held to what they said.

/// (list, path, the class it implied).
let private formerListPrefixes: (string * string * RouteClass) list = [
    // MetricsMiddleware: excluded from request latency emission.
    "metrics exclusion", "/api/notifications", RouteClass.Sse
    "metrics exclusion", "/api/ai/events", RouteClass.Sse
    "metrics exclusion", "/health", RouteClass.Health
    "metrics exclusion", "/ready", RouteClass.Health
    "metrics exclusion", "/metrics", RouteClass.Metrics
    // RequestTimingMiddleware: long-running, never a slow request.
    "slow-request exemption", "/api/notifications", RouteClass.Sse
    "slow-request exemption", "/api/ai/events", RouteClass.Sse
    "slow-request exemption", "/health", RouteClass.Health
    "slow-request exemption", "/ready", RouteClass.Health
    // RateLimiting: probes bypass; SSE connects take the per-IP window.
    "rate-limit bypass", "/health", RouteClass.Health
    "rate-limit bypass", "/ready", RouteClass.Health
    "rate-limit SSE connect", "/api/notifications", RouteClass.Sse
    "rate-limit SSE connect", "/api/ai/events", RouteClass.Sse
    // SurfaceRequirementRegistry: the SSE carve-out's routes.
    "surface SSE routes", "/api/ai/events", RouteClass.Sse
    "surface SSE routes", "/api/notifications", RouteClass.Sse
    // ReaderRouteClass: the reader refusal's SSE class.
    "reader SSE class", "/api/ai/events", RouteClass.Sse
    "reader SSE class", "/api/notifications", RouteClass.Sse
    // ScopeResolutionMiddleware: `/api`, `/dev`, and a page route.
    "scope resolution", "/api/x", RouteClass.Api
    "scope resolution", "/api", RouteClass.Api
    "scope resolution", "/dev/inspect", RouteClass.Dev
    "scope resolution", "/dev", RouteClass.Dev
    "scope resolution", "/reports/q3", RouteClass.Page
    "scope resolution", "/", RouteClass.Page
    // SurfaceEnforcementMiddleware: the `/api` surface it gates.
    "surface gate", "/api/TeamApi/CreateTeam", RouteClass.Api
]

let private routeClassifierTests =
    testList "RouteClassifierTests — one path-prefix table" [
        test "every prefix a former list held classifies as that list implied" {
            for list, path, expected in formerListPrefixes do
                Expect.equal (RouteClassifier.classify path) expected $"{list}: {path}"
        }

        test "a prefix matches by whole segments, ignoring case" {
            Expect.equal (RouteClassifier.classify "/API/X") RouteClass.Api "case-insensitive, like StartsWithSegments"
            Expect.equal (RouteClassifier.classify "/apix") RouteClass.Page "not a segment of /api"
            Expect.equal (RouteClassifier.classify "/api/notifications/x") RouteClass.Sse "under an SSE stream"
            Expect.equal (RouteClassifier.classify "/health/rag") RouteClass.Health "under a probe"
            Expect.equal (RouteClassifier.classify "/healthz") RouteClass.Page "not a segment of /health"
            Expect.equal (RouteClassifier.classify null) RouteClass.Page "no path"
        }

        test "the `/api` surface is the API and its SSE streams, and nothing else" {
            for routeClass in
                [
                    RouteClass.Api
                    RouteClass.Dev
                    RouteClass.Health
                    RouteClass.Metrics
                    RouteClass.Sse
                    RouteClass.Page
                ] do
                Expect.equal
                    (RouteClass.isApiSurface routeClass)
                    (routeClass = RouteClass.Api || routeClass = RouteClass.Sse)
                    (RouteClass.name routeClass)
        }

        test "the SSE carve-out reads the table's SSE prefixes" {
            Expect.equal
                (RouteClassifier.prefixesOf RouteClass.Sse |> Set.ofList)
                (set [ "/api/notifications"; "/api/ai/events" ])
                "the two SDK streams"
        }

        test "a request carries the stamped class; an unstamped one is classified from its path" {
            let ctx = DefaultHttpContext()
            ctx.Request.Path <- PathString "/api/x"
            Expect.equal (RouteClassifier.ofRequest ctx) RouteClass.Api "unstamped: read from the path"
            RouteClassifier.stamp ctx.Items RouteClass.Sse
            Expect.equal (RouteClassifier.ofItems ctx.Items) (Some RouteClass.Sse) "the stamp round-trips"
            Expect.equal (RouteClassifier.ofRequest ctx) RouteClass.Sse "the stamp wins"
        }

        test "the surface gate passes a non-/api route by its stamped class" {
            let ctx = DefaultHttpContext()
            ctx.Request.Method <- "GET"
            ctx.Request.Path <- PathString "/api/x"
            RouteClassifier.stamp ctx.Items RouteClass.Page
            let mutable nextInvoked = false

            let mw =
                SurfaceEnforcementMiddleware(
                    RequestDelegate(fun _ ->
                        nextInvoked <- true
                        Task.CompletedTask),
                    registryFor SurfaceRequirement.userOrTeam
                )

            mw.InvokeAsync(ctx).GetAwaiter().GetResult()
            Expect.isTrue nextInvoked "the gate reads the stamp, not a list of its own"
        }
    ]

let tests =
    testList "Phase 66 D.2 — SurfaceEnforcementMiddleware response-code matrix" [

        // ── Row 1: AnonymousKind, not admitted → 401 ──────────────────
        test "Row 1 — anonymous on userOrTeam route → 401 authentication_required, next not invoked" {
            let r =
                run (registryFor SurfaceRequirement.userOrTeam) (Some(AnonymousSession "s")) "POST" "/api/x"

            Expect.equal r.StatusCode 401 "anonymous to an auth-required route is 401"
            Expect.isFalse r.NextInvoked "rejected request never reaches the handler"
            Expect.stringContains r.Body "authentication_required" "machine-readable error code"
            Expect.stringContains r.Body "\"status\":401" "status echoed in the body"
            Expect.isFalse (r.Body.Contains "hint") "401 carries no client hint"
            Expect.stringContains r.ContentType "application/json" "rejection body is JSON"
        }

        // ── Row 2: UserKind, admitted → 200 pass ──────────────────────
        test "Row 2 — user on userOrTeam route → pass (next invoked, no rejection body)" {
            let r =
                run (registryFor SurfaceRequirement.userOrTeam) (Some(AuthenticatedUser "u")) "POST" "/api/x"

            Expect.isTrue r.NextInvoked "admitted request reaches the handler"
            Expect.equal r.Body "" "pass writes no rejection body"
        }

        // ── Row 3: UserKind, not admitted, Team admitted → 403 + hint ─
        test "Row 3 — user on teamScoped route → 403 team_required + select_team hint" {
            let r =
                run (registryFor SurfaceRequirement.teamScoped) (Some(AuthenticatedUser "u")) "POST" "/api/x"

            Expect.equal r.StatusCode 403 "user without a team on a team route is 403"
            Expect.isFalse r.NextInvoked "rejected request never reaches the handler"
            Expect.stringContains r.Body "team_required" "names the team-required error"
            Expect.stringContains r.Body "select_team" "carries the actionable client hint"
        }

        // ── Row 4: TeamMemberKind, admitted → 200 pass ────────────────
        test "Row 4 — team member on teamScoped route → pass" {
            let r =
                run (registryFor SurfaceRequirement.teamScoped) (Some(TeamMember("u", "t"))) "POST" "/api/x"

            Expect.isTrue r.NextInvoked "admitted team member reaches the handler"
            Expect.equal r.Body "" "pass writes no rejection body"
        }

        // ── Row 5: TeamMemberKind, not admitted, Anon admitted → 403 ──
        test "Row 5 — team member on anonymousOnly route → 403 authenticated_subject_not_admitted" {
            let r =
                run (registryFor SurfaceRequirement.anonymousOnly) (Some(TeamMember("u", "t"))) "POST" "/api/x"

            Expect.equal r.StatusCode 403 "an authenticated subject hitting a sign-up-only route is 403"
            Expect.isFalse r.NextInvoked "rejected request never reaches the handler"
            Expect.stringContains r.Body "authenticated_subject_not_admitted" "names the anon-only rejection"
            Expect.isFalse (r.Body.Contains "hint") "no actionable hint — the route is simply closed to this caller"
        }

        // ── Row 6: ClaimBearerKind, admitted → 200 pass ───────────────
        test "Row 6 — claim bearer on claimBearerOnly route → pass" {
            let r =
                run (registryFor SurfaceRequirement.claimBearerOnly) (Some(ClaimBearer aClaim)) "POST" "/api/x"

            Expect.isTrue r.NextInvoked "admitted claim bearer reaches the handler"
            Expect.equal r.Body "" "pass writes no rejection body"
        }

        // ── Row 7: ClaimBearerKind, not admitted → 403 ────────────────
        test "Row 7 — claim bearer on userOrTeam route → 403 claim_bearer_not_admitted" {
            let r =
                run (registryFor SurfaceRequirement.userOrTeam) (Some(ClaimBearer aClaim)) "POST" "/api/x"

            Expect.equal r.StatusCode 403 "a share-token bearer on an auth-only route is 403"
            Expect.isFalse r.NextInvoked "rejected request never reaches the handler"
            Expect.stringContains r.Body "claim_bearer_not_admitted" "names the claim-bearer rejection"
        }

        // ── Folded branches the 7-row table summarises ────────────────
        test "Row 3 variant — user where Team is also not admitted → generic 403 user_subject_not_admitted (no hint)" {
            // anonymousOnly admits neither User nor Team, so the
            // `team_required` branch (which needs Team in the admit set)
            // does not fire — the generic user 403 does instead.
            let r =
                run (registryFor SurfaceRequirement.anonymousOnly) (Some(AuthenticatedUser "u")) "POST" "/api/x"

            Expect.equal r.StatusCode 403 "user not admitted anywhere on this route is 403"
            Expect.stringContains r.Body "user_subject_not_admitted" "generic user rejection (no team to select)"
            Expect.isFalse (r.Body.Contains "select_team") "no select_team hint when Team isn't admitted either"
        }

        test "Row 5 variant — team member where Anon is also not admitted → 403 team_member_not_admitted" {
            let r =
                run (registryFor SurfaceRequirement.claimBearerOnly) (Some(TeamMember("u", "t"))) "POST" "/api/x"

            Expect.equal r.StatusCode 403 "team member not admitted is 403"
            Expect.stringContains r.Body "team_member_not_admitted" "team-member rejection when not a sign-up route"
        }

        // ── Path scoping: only /api/* is enforced ─────────────────────
        test "Non-/api path passes through unenforced even for a would-be-rejected subject" {
            // userOrTeam would 401 an anonymous subject — but the SPA
            // shell path is not /api/*, so the gate never runs.
            let r =
                run (registryFor SurfaceRequirement.userOrTeam) (Some(AnonymousSession "s")) "GET" "/dashboard"

            Expect.isTrue r.NextInvoked "non-api path reaches the handler"
            Expect.equal r.Body "" "no rejection body for an unenforced path"
        }

        // ── Defensive: missing Subject on /api/* is fail-CLOSED (336) ─
        test "Missing Subject on /api path → evaluated as anonymous, not passed through" {
            // Pre-Phase-336 this asserted the opposite (`next` invoked).
            // That fall-through turned the primary authentication gate
            // into a pass-through under any condition that left `Subject`
            // unstashed — including a swallowed `ScopeResolutionMiddleware`
            // resolver exception, which is a live path. The full
            // fail-closed matrix (public routes still admitted, forged
            // identity headers ignored) is pinned by
            // `FailClosedDispatchTests`.
            let r = run (registryFor SurfaceRequirement.userOrTeam) None "POST" "/api/x"
            Expect.equal r.StatusCode 401 "no stashed Subject → synthesised anonymous → 401 on an auth-required route"
            Expect.isFalse r.NextInvoked "the handler is never reached without a resolved Subject"
            Expect.stringContains r.Body "authentication_required" "the matrix's own error code"
        }

        // ── Strict fail-closed default: empty registry ────────────────
        test "Empty registry (no prefix match) → strict userOrTeam default 401s anonymous" {
            let r =
                run SurfaceRequirementRegistry.empty (Some(AnonymousSession "s")) "POST" "/api/unmapped"

            Expect.equal r.StatusCode 401 "strict default is userOrTeam → anonymous is 401 (fail-closed)"
            Expect.stringContains r.Body "authentication_required" "strict-default rejection still names the error"
        }

        test "Empty registry — authenticated user admitted by the strict userOrTeam default" {
            let r =
                run SurfaceRequirementRegistry.empty (Some(AuthenticatedUser "u")) "POST" "/api/unmapped"

            Expect.isTrue r.NextInvoked "strict default userOrTeam admits a signed-in user"
            Expect.equal r.Body "" "pass writes no rejection body"
        }

        routeClassifierTests
    ]