module ToolUp.Forms.Tests.InProcess.PublicSubmitSurfaceTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.SurfaceEnforcement
open ToolUp.Forms.FormsCompose

// ─── Phase 66 Stream B.6 — Forms public-submit surface tests ────────
//
// Pins the per-route `SurfaceRequirement.claimBearerOnly` declarations
// `FormsCompose` registers for the two `IPublicFormApi` routes
// (`GetSchemaByToken` + `SubmitWithToken`). Three layers:
//
//   1. `composeForms` produces a `ServerApp.RouteSurfaceOverrides`
//      list carrying the two exact `(POST, path)` entries.
//   2. The same `fromServerConfig + merge` chain `SDK.Server.fs` uses
//      resolves both routes to `claimBearerOnly`.
//   3. The pure `SurfaceEnforcement.evaluate` matrix admits a
//      `ClaimBearer` subject and rejects every other kind with the
//      design §3.1 status / error-code shape.
//
// Anonymous `Surfaces` so `composeForms`' non-Anonymous-mode
// `Console.Error.WriteLine` warnings (auto-defaulting `IActionLedger`)
// stay silent in the test runner.

let private anonymousConfig: ServerConfig = {
    ServerConfig.defaults with
        Surfaces = Surfaces.anonymous
}

let private dummyClaim: ShareTokenClaim = {
    TokenId = "tok-1"
    ScopeId = "scope-1"
    ResourceKind = "forms.publishable"
    ResourceId = "schema-1"
    AttributedHandle = None
    IssuedBy = "user-1"
    IssuedAt = DateTimeOffset.UtcNow
    ExpiresAt = DateTimeOffset.UtcNow.AddHours 1.0
    UseLimit = Some 1
    UsedCount = 0
    Revoked = false
    RateLimit = None
}

let private composedFormsApp () : ServerApp =
    ServerApp.empty
    |> withForms (FormsOptions.create ())
    |> ServerApp.withConfig anonymousConfig
    |> ServerApp.resolve

let tests =
    testList "Phase 66 Stream B.6 — Forms public-submit surface declarations" [

        test "composeForms registers two claimBearerOnly route overrides" {
            let composed = composedFormsApp ()

            let formsOverrides =
                composed.RouteSurfaceOverrides
                |> List.filter (fun ((_, path), _) -> path.Contains "/api/public/forms/")

            Expect.equal
                (List.length formsOverrides)
                2
                "two public-form route overrides registered by formsPublicSubmitSurfaceModule"

            let methods = formsOverrides |> List.map (fun ((_, path), _) -> path) |> List.sort

            Expect.equal
                methods
                [ "/api/public/forms/GetSchemaByToken"; "/api/public/forms/SubmitWithToken" ]
                "the two IPublicFormApi route paths declare claimBearerOnly"

            for ((httpMethod, path), req) in formsOverrides do
                Expect.equal httpMethod "POST" (sprintf "%s declared as POST (ToolUp.Remoting convention)" path)

                Expect.equal req SurfaceRequirement.claimBearerOnly (sprintf "%s declares claimBearerOnly" path)
        }

        test "Forms public-submit routes resolve to claimBearerOnly via the merged registry" {
            // End-to-end through the same merge step SDK.Server.fs runs
            // at compose time: `fromServerConfig` → `merge` with the
            // accumulated module-level defaults and per-route overrides.
            // The resolved requirement matches the design §3.3 row
            // "Forms (public-submit endpoints) | claimBearerOnly".
            let composed = composedFormsApp ()

            let registry =
                SurfaceRequirementRegistry.fromServerConfig composed.Config
                |> SurfaceRequirementRegistry.merge composed.ModuleSurfaceDefaults composed.RouteSurfaceOverrides

            for path in [ "/api/public/forms/GetSchemaByToken"; "/api/public/forms/SubmitWithToken" ] do
                let resolved = SurfaceRequirementRegistry.resolve registry "POST" path

                Expect.equal resolved SurfaceRequirement.claimBearerOnly (sprintf "%s -> claimBearerOnly" path)
        }

        test "Forms public-submit registry override case-insensitively normalises method and path" {
            // The registry stores keys as upper-case method + lower-case
            // path; declarations from `withRouteSurfaceRequirement`
            // round-trip regardless of caller casing.
            let composed = composedFormsApp ()

            let registry =
                SurfaceRequirementRegistry.fromServerConfig composed.Config
                |> SurfaceRequirementRegistry.merge composed.ModuleSurfaceDefaults composed.RouteSurfaceOverrides

            let resolvedLower =
                SurfaceRequirementRegistry.resolve registry "post" "/api/public/forms/submitwithtoken"

            Expect.equal
                resolvedLower
                SurfaceRequirement.claimBearerOnly
                "lower-case method + path still resolves to the registered override"
        }

        test "claimBearerOnly admits a ClaimBearer subject and rejects every other kind" {
            // Pure §3.1 matrix walk. The four enumeration cases:
            //   * ClaimBearer  → Pass
            //   * Anonymous    → 401 authentication_required
            //   * User         → 403 user_subject_not_admitted
            //   * TeamMember   → 403 team_member_not_admitted
            //
            // The `SurfaceEnforcementMiddleware` translates these into
            // the wire response per `evaluate`; the test confirms the
            // requirement value `claimBearerOnly` produces them.
            let req = SurfaceRequirement.claimBearerOnly

            let claimBearer = ClaimBearer dummyClaim
            let anon = AnonymousSession "anon-1"
            let user = AuthenticatedUser "user-1"
            let teamMember = TeamMember("user-1", "team-1")

            Expect.equal (SurfaceEnforcement.evaluate claimBearer req) Pass "claim bearer admitted"

            match SurfaceEnforcement.evaluate anon req with
            | Reject(401, "authentication_required", None) -> ()
            | other -> failtestf "anonymous: expected 401 authentication_required, got %A" other

            match SurfaceEnforcement.evaluate user req with
            | Reject(403, "user_subject_not_admitted", None) -> ()
            | other -> failtestf "user: expected 403 user_subject_not_admitted, got %A" other

            match SurfaceEnforcement.evaluate teamMember req with
            | Reject(403, "team_member_not_admitted", None) -> ()
            | other -> failtestf "team member: expected 403 team_member_not_admitted, got %A" other
        }
        test "builders applied after withForms reach the resolved app and the Forms tier" {
            let logger = ConsoleLogger.ConsoleLogger() :> ILogger

            let app =
                ServerApp.empty
                |> withForms (FormsOptions.create ())
                |> ServerApp.withConfig {
                    anonymousConfig with
                        ReplicaCount = 2
                        PublicBaseUrl = Some "https://forms.example.com"
                }
                |> ServerApp.withLogger logger

            let resolved = ServerApp.resolve app

            Expect.contains resolved.ComposedCompanions "ToolUp.Forms" "the Forms tier is composed"
            Expect.equal (ServerApp.effectiveConfig app).ReplicaCount 2 "the later withConfig reaches the app"
            Expect.isTrue (obj.ReferenceEquals(resolved.Logger.Value, logger)) "and the later withLogger"

            Expect.equal
                (ServerApp.composedExtensions app).Handlers.Length
                (ServerApp.empty.Extensions.Handlers.Length + 2)
                "the Forms tier contributed its two API handlers"
        }

        test "a second withForms replaces the first rather than composing Forms twice" {
            let resolved =
                ServerApp.empty
                |> withForms (FormsOptions.create ())
                |> withForms (FormsOptions.create ())
                |> ServerApp.withConfig anonymousConfig
                |> ServerApp.resolve

            Expect.equal
                (resolved.ComposedCompanions |> List.filter ((=) "ToolUp.Forms"))
                [ "ToolUp.Forms" ]
                "Forms is composed once"
        }
    ]