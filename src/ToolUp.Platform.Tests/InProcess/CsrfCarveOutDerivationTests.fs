module ToolUp.Platform.Tests.InProcess.CsrfCarveOutDerivationTests

open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.SurfaceEnforcement

// ─── CSRF carve-out derivation — Phase 66 Stream D.4 ─────────────────
//
// `Csrf.requiresValidation` derives the CSRF requirement for a request
// purely from the route's resolved `SurfaceRequirement` (design §3.1,
// A.6 cut-over): a state-changing `/api/*` request is guarded UNLESS the
// admit set contains `AnonymousKind` (no logged-in session to bind a
// nonce against) or `ClaimBearerKind` (share-token flows are HMAC-gated
// already; a session-bound nonce on top breaks the cookie-less embed).
//
// `SecurityHardeningTests` already pins the carve-outs that flow through
// the `SurfaceRequirementRegistry.fromServerConfig` *bridge* (the CSRF
// token endpoint, SSE query-param fallback, peer-bearer prefixes, the
// anonymous-deployment `/api/` catch-all). This pack covers the
// complementary seam D.4 names explicitly: the **per-module
// `SurfaceRequirement` declaration** path (`SurfaceRequirementRegistry.merge`
// of module-prefix defaults + exact `(method, path)` route overrides),
// which is how a module author — not the deployment bridge — moves a
// route in or out of the CSRF gate. Three cases per the D.4 description:
// an `anonymousOnly` route skips, a `userOrTeam` route requires, and a
// per-endpoint exact override wins over the surrounding module prefix in
// both directions.

let private ctxFor (method: string) (path: string) =
    let c = DefaultHttpContext()
    c.Request.Method <- method
    c.Request.Path <- PathString(path)
    c :> HttpContext

/// Registry built the way composition does for module authors: overlay
/// module-prefix defaults + exact route overrides onto the empty base
/// (the `fromServerConfig` bridge is exercised separately in
/// `SecurityHardeningTests`). `merge` normalises method/path casing.
let private moduleRegistry
    (prefixes: (string * SurfaceRequirement) list)
    (overrides: ((string * string) * SurfaceRequirement) list)
    =
    SurfaceRequirementRegistry.merge prefixes overrides SurfaceRequirementRegistry.empty

let tests =
    testList "Phase 66 D.4 — CSRF carve-out derivation (module declarations)" [

        // ── The two headline rows ─────────────────────────────────────

        test "userOrTeam module route requires CSRF on a state-changing /api/* request" {
            let registry = moduleRegistry [ "/api/widgets/", SurfaceRequirement.userOrTeam ] []

            Expect.isTrue
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/widgets/create"))
                "userOrTeam admits neither Anonymous nor ClaimBearer → session-bound, so CSRF is required"
        }

        test "anonymousOnly module route skips CSRF (no session to bind a nonce against)" {
            let registry =
                moduleRegistry [ "/api/intake/", SurfaceRequirement.anonymousOnly ] []

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/intake/submit"))
                "anonymousOnly admits AnonymousKind → no logged-in session, so the CSRF gate is skipped"
        }

        // ── The other two admit-set shapes, for completeness ──────────

        test "claimBearerOnly module route skips CSRF (share-token flows are HMAC-gated)" {
            let registry =
                moduleRegistry [ "/api/embed/", SurfaceRequirement.claimBearerOnly ] []

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/embed/answer"))
                "claimBearerOnly admits ClaimBearerKind → the bearer is the gate; a session nonce would break the cookie-less embed"
        }

        test "teamScoped module route requires CSRF (admits only TeamMemberKind — still session-bound)" {
            let registry = moduleRegistry [ "/api/billing/", SurfaceRequirement.teamScoped ] []

            Expect.isTrue
                (Csrf.requiresValidation registry (ctxFor "PUT" "/api/billing/plan"))
                "teamScoped admits neither Anonymous nor ClaimBearer → CSRF required"
        }

        test "public_ module route skips CSRF (admit set contains AnonymousKind)" {
            let registry = moduleRegistry [ "/api/status/", SurfaceRequirement.public_ ] []

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/status/ping"))
                "public_ admits AnonymousKind, so the gate skips"
        }

        // ── Per-endpoint override — the third D.4 case, both directions ─

        test "exact override pulls one path INTO the gate inside an anonymousOnly module prefix" {
            // A public-submit module declares its prefix `anonymousOnly`
            // (open intake skips CSRF) but locks one admin mutation down
            // to `userOrTeam` via an exact route override. The override
            // must win — the admin path requires CSRF while its sibling
            // public path under the same prefix still skips.
            let registry =
                moduleRegistry [ "/api/forms/", SurfaceRequirement.anonymousOnly ] [
                    ("POST", "/api/forms/admin/purge"), SurfaceRequirement.userOrTeam
                ]

            Expect.isTrue
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/forms/admin/purge"))
                "exact userOrTeam override wins over the anonymousOnly prefix → CSRF required"

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/forms/submit"))
                "a sibling path under the anonymousOnly prefix (no exact override) still skips"
        }

        test "exact override pushes one path OUT of the gate inside a userOrTeam module prefix" {
            // The mirror: a share module declares its prefix `userOrTeam`
            // (management endpoints require CSRF) but carves the cookie-
            // less embed answer endpoint out to `claimBearerOnly`. The
            // embed path skips; the management path still requires.
            let registry =
                moduleRegistry [ "/api/share/", SurfaceRequirement.userOrTeam ] [
                    ("POST", "/api/share/embed"), SurfaceRequirement.claimBearerOnly
                ]

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/share/embed"))
                "exact claimBearerOnly override wins over the userOrTeam prefix → CSRF skipped"

            Expect.isTrue
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/share/manage"))
                "a sibling path under the userOrTeam prefix (no exact override) still requires CSRF"
        }

        // ── Longest-prefix-wins flows through the CSRF derivation ─────

        test "nested module prefixes — longest match decides the CSRF carve-out" {
            let registry =
                moduleRegistry [
                    "/api/", SurfaceRequirement.userOrTeam
                    "/api/catalog/public/", SurfaceRequirement.anonymousOnly
                ] []

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/catalog/public/list"))
                "the longer anonymousOnly prefix wins → skip"

            Expect.isTrue
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/catalog/admin"))
                "outside the public sub-prefix the /api/ userOrTeam default wins → require"
        }

        // ── Method / path guards interact with the derivation ────────

        test "safe methods are exempt even on a userOrTeam route" {
            let registry = moduleRegistry [ "/api/widgets/", SurfaceRequirement.userOrTeam ] []

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "GET" "/api/widgets/create"))
                "GET is not state-changing → never gated regardless of the requirement"
        }

        test "carve-out derivation is case-insensitive in method and path" {
            // `merge` lower-cases the registered prefix; `resolve` lower-
            // cases the request path, so a mixed-case anonymous route
            // still resolves to its anonymousOnly requirement and skips.
            let registry =
                moduleRegistry [ "/api/intake/", SurfaceRequirement.anonymousOnly ] []

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "post" "/API/Intake/Submit"))
                "lower-cased registry vs mixed-case request still resolves anonymousOnly → skip"
        }
    ]