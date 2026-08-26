module ToolUp.Platform.Tests.InProcess.FailClosedDispatchTests

open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.SurfaceEnforcement

// ─── Phase 336 — fail-closed dispatch consistency ─────────────────────
//
// Two seams in the dispatch-authorization layer failed OPEN where every
// other decision point in the same layer fails closed. This pack pins
// both closed, and pins the correct-path behaviour they must not have
// disturbed (GP 11).
//
// (1) `SurfaceEnforcementMiddleware` — the canonical `/api/*`
//     authentication gate — called `next.Invoke` when no `Subject` was
//     stashed on the request. That is not merely a defensive branch for
//     an unsupported pipeline: `ScopeResolutionMiddleware` catches an
//     infrastructure exception (DI hiccup, store throw) and continues
//     WITHOUT stashing a Subject, so a resolver crash downgraded the
//     primary gate to a pass-through — the one moment it most needs to
//     hold. It now synthesises an anonymous subject and runs the same
//     §3.1 matrix.
//
// (2) `PlatformAdminAuthorizationMiddleware` — its premium-write
//     discriminator used a case-sensitive `EndsWith "/premium"` and an
//     ordinal `httpMethod = "POST"`, while the prefix guard beside it
//     uses `StartsWithSegments` (`OrdinalIgnoreCase`). The two halves of
//     one `if` disagreed about casing, so `/…/PREMIUM` and a lower-case
//     `post` satisfied the prefix and skipped the backstop.
//
// Both were bounded by a second gate at the time of writing (Giraffe's
// `routef` is case-sensitive; the in-handler `canModifyPlatformConfig`
// check remains). A backstop that a casing trick or a swallowed
// exception disables is a defect regardless — the whole point of a
// backstop is to hold when the other gate does not.

// ── Harness: drive the real SurfaceEnforcementMiddleware ─────────────

/// Result of one middleware invocation.
type private Outcome = {
    StatusCode: int
    Body: string
    NextInvoked: bool
}

/// Registry shaped the way composition does for one mounted module.
let private registryFor (requirement: SurfaceRequirement) =
    SurfaceRequirementRegistry.merge [ "/api/", requirement ] [] SurfaceRequirementRegistry.empty

/// `stashSubject = None` is the whole point of this pack — it reproduces
/// the state `ScopeResolutionMiddleware` leaves behind when its catch
/// swallows a resolver exception. `headers` lets a test prove the
/// synthesised subject is NOT built from caller-supplied identity.
let private runSurface
    (registry: SurfaceRequirementRegistry)
    (stashSubject: Subject option)
    (headers: (string * string) list)
    (method: string)
    (path: string)
    : Outcome =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- method
    ctx.Request.Path <- PathString path

    for (k, v) in headers do
        ctx.Request.Headers[k] <- Microsoft.Extensions.Primitives.StringValues v

    let body = new MemoryStream()
    ctx.Response.Body <- body

    match stashSubject with
    | Some s -> ctx.Items[SubjectItemsKey] <- box s
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
    }

// ── Harness: drive the real PlatformAdminAuthorizationMiddleware ─────

let private gate = PlatformAdminAuthorization.requiresPlatformAdmin

/// Drives the backstop end to end. `role` is the `ToolUp.PlatformRole`
/// item `ScopeResolutionMiddleware` stamps. `PlatformRole` is a
/// single-case DU, so "not an admin" is the item's ABSENCE — which is
/// exactly what `isPlatformAdmin` reads.
let private runAdmin (role: PlatformRole option) (method: string) (path: string) : Outcome =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- method
    ctx.Request.Path <- PathString path

    let body = new MemoryStream()
    ctx.Response.Body <- body

    match role with
    | Some r -> ctx.Items["ToolUp.PlatformRole"] <- box r
    | None -> ()

    let mutable nextInvoked = false

    let next =
        RequestDelegate(fun _ ->
            nextInvoked <- true
            Task.CompletedTask)

    let mw = PlatformAdminAuthorization.PlatformAdminAuthorizationMiddleware next
    (mw.InvokeAsync ctx).GetAwaiter().GetResult()

    body.Position <- 0L
    let text = (new StreamReader(body)).ReadToEnd()

    {
        StatusCode = ctx.Response.StatusCode
        Body = text
        NextInvoked = nextInvoked
    }

let tests =
    testList "Phase 336 — fail-closed dispatch consistency" [

        testList "Surface gate — missing Subject is evaluated, never passed through" [

            // The core deny path. Pre-336 this reached the handler
            // unauthenticated; it is now an ordinary 401 from the same
            // matrix every other row goes through.
            test "no stashed Subject on an auth-required /api route → 401, handler never reached" {
                let r =
                    runSurface (registryFor SurfaceRequirement.userOrTeam) None [] "POST" "/api/x"

                Expect.equal r.StatusCode 401 "unresolved subject on a userOrTeam route is 401"
                Expect.isFalse r.NextInvoked "the handler is NOT reached — this is the fail-open seam 336 closes"
                Expect.stringContains r.Body "authentication_required" "the matrix's own error code, not a new shape"
                Expect.stringContains r.Body "\"status\":401" "status echoed in the body as every other row does"
            }

            // Same seam, reached via the strict global default rather
            // than a declared prefix — an unmapped route is the shape a
            // future handler arrives in before anyone declares it.
            test "no stashed Subject on an unmapped /api route → 401 via the strict global default" {
                let r = runSurface SurfaceRequirementRegistry.empty None [] "POST" "/api/unmapped"

                Expect.equal r.StatusCode 401 "the strict userOrTeam floor applies to a synthesised subject too"
                Expect.isFalse r.NextInvoked "no handler reached"
            }

            test "no stashed Subject on a teamScoped route → 401, not the team_required 403" {
                // An anonymous subject fails the admit check at row 1,
                // so it is 401 (credentials would unblock this) rather
                // than the 403 a signed-in user without a team gets.
                let r =
                    runSurface (registryFor SurfaceRequirement.teamScoped) None [] "POST" "/api/x"

                Expect.equal r.StatusCode 401 "anonymous-kind synthesis lands on row 1 of the matrix"
                Expect.isFalse r.NextInvoked "no handler reached"
                Expect.isFalse (r.Body.Contains "team_required") "not the signed-in-user rejection"
            }

            // GP 11 — the synthesised subject runs the SAME matrix, so a
            // route that genuinely admits anonymous callers is unchanged.
            // This is why the fix synthesises rather than hard-401ing.
            test "no stashed Subject on a public_ route → still passes (correct-path behaviour preserved)" {
                let r =
                    runSurface (registryFor SurfaceRequirement.public_) None [] "GET" "/api/csrf-token"

                Expect.isTrue r.NextInvoked "a public route admits AnonymousKind — synthesis must not close it"
                Expect.equal r.Body "" "no rejection body on a pass"
            }

            test "no stashed Subject on an anonymousOnly route → still passes" {
                let r =
                    runSurface (registryFor SurfaceRequirement.anonymousOnly) None [] "POST" "/api/signup"

                Expect.isTrue r.NextInvoked "anonymousOnly admits the synthesised subject"
                Expect.equal r.Body "" "no rejection body on a pass"
            }

            // Path scoping is untouched — the gate is still /api/*-only.
            test "no stashed Subject on a NON-/api path → passes through unenforced as before" {
                let r =
                    runSurface (registryFor SurfaceRequirement.userOrTeam) None [] "GET" "/dashboard"

                Expect.isTrue r.NextInvoked "the SPA shell / static assets are not gated"
                Expect.equal r.Body "" "no rejection body for an unenforced path"
            }

            // Phase 337's hazard, applied here: the synthesised session
            // id must not come from caller-supplied identity. If it did,
            // a forged header on the resolver-crash path would hand the
            // caller a scope — the softest door in the building.
            test "forged identity headers do not upgrade the synthesised subject" {
                let forged = [
                    "X-User-Id", "victim-user"
                    "X-Share-Token", "forged-token"
                    "Authorization", "Bearer forged"
                ]

                let r =
                    runSurface (registryFor SurfaceRequirement.userOrTeam) None forged "POST" "/api/x"

                Expect.equal r.StatusCode 401 "self-asserted headers cannot make the synthesised subject authenticated"
                Expect.isFalse r.NextInvoked "no handler reached"
                Expect.isFalse (r.Body.Contains "victim-user") "the forged id is not echoed back"
            }

            // Regression anchors: a request that DOES carry a resolved
            // Subject is byte-for-byte what it was before 336.
            test "resolved Subject rows are unchanged — user passes, anonymous 401s" {
                let user =
                    runSurface
                        (registryFor SurfaceRequirement.userOrTeam)
                        (Some(AuthenticatedUser "u"))
                        []
                        "POST"
                        "/api/x"

                Expect.isTrue user.NextInvoked "a signed-in user still reaches the handler"
                Expect.equal user.Body "" "no rejection body"

                let anon =
                    runSurface
                        (registryFor SurfaceRequirement.userOrTeam)
                        (Some(AnonymousSession "s"))
                        []
                        "POST"
                        "/api/x"

                Expect.equal anon.StatusCode 401 "a resolved anonymous subject is 401 exactly as before"
                Expect.isFalse anon.NextInvoked "no handler reached"
            }
        ]

        testList "PlatformAdmin backstop — casing and trailing slashes cannot skip it" [

            // Baseline: the shapes that were already gated stay gated.
            test "the canonical grant / revoke writes are gated (unchanged)" {
                Expect.isTrue (gate "POST" (PathString "/api/_platform/users/u1/premium")) "grant gated"
                Expect.isTrue (gate "DELETE" (PathString "/api/_platform/users/u1/premium")) "revoke gated"
            }

            // The casing variants the phase names. Each satisfied the
            // case-INsensitive prefix guard and then failed the
            // case-SENSITIVE suffix guard beside it.
            test "upper- and mixed-case /premium variants are gated" {
                Expect.isTrue (gate "POST" (PathString "/api/_platform/users/u1/PREMIUM")) "all-caps gated"
                Expect.isTrue (gate "POST" (PathString "/api/_platform/users/u1/Premium")) "mixed-case gated"
                Expect.isTrue (gate "DELETE" (PathString "/api/_platform/users/u1/PreMiUm")) "revoke, mixed-case gated"
            }

            test "trailing-slash variants are gated" {
                Expect.isTrue (gate "POST" (PathString "/api/_platform/users/u1/premium/")) "one trailing slash gated"
                Expect.isTrue (gate "POST" (PathString "/api/_platform/users/u1/premium//")) "repeated slashes gated"

                Expect.isTrue
                    (gate "DELETE" (PathString "/api/_platform/users/u1/PREMIUM/"))
                    "casing and slash together gated"
            }

            // Giraffe's `POST` combinator routes via `HttpMethods.IsPost`
            // (OrdinalIgnoreCase) while this guard compared ordinally, so
            // a lower-case method reached the handler with the backstop
            // silent. Kestrel preserves an unrecognised method verbatim.
            test "lower-case HTTP methods are gated" {
                Expect.isTrue (gate "post" (PathString "/api/_platform/users/u1/premium")) "lower-case post gated"
                Expect.isTrue (gate "delete" (PathString "/api/_platform/users/u1/premium")) "lower-case delete gated"
                Expect.isTrue (gate "Post" (PathString "/api/_platform/users/u1/PREMIUM/")) "every variant at once"
            }

            // Normalising must be strictly MORE closed, never less: the
            // deliberately-open surfaces stay open on both guards.
            test "the intentionally-open premium-status read stays open" {
                Expect.isFalse
                    (gate "GET" (PathString "/api/_platform/users/me/premium-status"))
                    "the public read is a GET and does not end /premium"

                Expect.isFalse
                    (gate "POST" (PathString "/api/_platform/users/me/premium-status"))
                    "even as a POST, premium-status is not a premium write"

                Expect.isFalse
                    (gate "POST" (PathString "/api/_platform/users/me/PREMIUM-STATUS"))
                    "and the trailing-slash trim does not turn it into one"

                Expect.isFalse
                    (gate "GET" (PathString "/api/_platform/users/u1/premium"))
                    "a non-mutating read of the grant path is still not gated here"
            }

            test "surfaces outside the backstop's remit stay untouched" {
                Expect.isFalse
                    (gate "POST" (PathString "/api/_platform/encryption/destroy-scope-key/s1"))
                    "encryption keeps its role-OR-token dual gate"

                Expect.isFalse (gate "POST" (PathString "/api/_platform/consent")) "consent sink stays open"
                Expect.isFalse (gate "GET" (PathString "/api/_platform/ads/serve")) "ad serving stays open"
            }

            test "the prefix arms are unchanged" {
                Expect.isTrue (gate "GET" (PathString "/api/_platform/admin/ad-units")) "admin prefix gated"
                Expect.isTrue (gate "POST" (PathString "/api/_platform/tenants/CreateTenant")) "tenant prefix gated"
            }

            // End to end through the real middleware, not just the
            // predicate: a casing-trick write is refused with the same
            // 403 wire contract the in-handler refusal emits.
            test "middleware refuses a casing-trick premium write for a non-admin caller" {
                let r = runAdmin None "POST" "/api/_platform/users/u1/PREMIUM"

                Expect.equal r.StatusCode 403 "non-admin caller is refused"
                Expect.isFalse r.NextInvoked "the handler is never reached"

                Expect.stringContains
                    r.Body
                    "platform admin role required"
                    "same wire contract as the in-handler refusal"
            }

            test "middleware refuses a lower-case-method premium write for a non-admin caller" {
                let r = runAdmin None "post" "/api/_platform/users/u1/premium/"

                Expect.equal r.StatusCode 403 "a caller with no stamped PlatformAdmin role is refused"
                Expect.isFalse r.NextInvoked "the handler is never reached"

                Expect.stringContains
                    r.Body
                    "platform admin role required"
                    "same wire contract as the in-handler refusal"
            }

            test "middleware admits a PlatformAdmin caller on every variant" {
                for method, path in
                    [
                        "POST", "/api/_platform/users/u1/premium"
                        "POST", "/api/_platform/users/u1/PREMIUM/"
                        "delete", "/api/_platform/users/u1/Premium"
                    ] do
                    let r = runAdmin (Some PlatformRole.PlatformAdmin) method path

                    Expect.isTrue r.NextInvoked (sprintf "admin reaches the handler for %s %s" method path)
                    Expect.equal r.Body "" (sprintf "no refusal body for %s %s" method path)
            }

            test "middleware leaves the public premium-status read alone" {
                let r = runAdmin None "GET" "/api/_platform/users/me/premium-status"

                Expect.isTrue r.NextInvoked "an anonymous caller still reaches the public status read"
                Expect.equal r.Body "" "no refusal body"
            }
        ]
    ]