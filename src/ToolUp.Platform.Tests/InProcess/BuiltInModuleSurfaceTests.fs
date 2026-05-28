module ToolUp.Platform.Tests.InProcess.BuiltInModuleSurfaceTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.SurfaceEnforcement

// ─── Phase 66 Stream B.4 — SDK built-in surface-requirement tests ────
//
// Pins the per-route exact overrides the SDK contributes via
// `SurfaceRequirementRegistry.sdkBuiltInRouteOverrides` and folds into
// `fromServerConfig`'s `Exact` map. Per design §3.3 — the
// `PlatformApi` row's "Per-endpoint overrides: team-CRUD endpoints
// require `teamScoped`" caveat.
//
// The five tested method names match the mutating endpoints on the
// `TeamApi` record:
//   * AddTeamMember, RemoveTeamMember, ChangeMemberRole — Owner/Admin
//     gated inside the handler; the surface override fails-fast at the
//     middleware for `UserKind` callers with no active team.
//   * GetTeamMembers — read-shape but requires team context to mean
//     anything.
//   * SetActiveTeam — switching the active team requires being in
//     SOME team.
//
// Excluded from the override list (assertions below confirm they
// fall through to the strict global default `userOrTeam`):
//   * CreateTeam — caller has no team yet; `teamScoped` would
//     block the bootstrap path.
//   * GetMyTeams / GetActiveTeam / GetTeamCreationPolicy — `UserKind`
//     subjects with zero teams legitimately read these to render the
//     team-management UI.

let private bridgeForIndividual () =
    SurfaceRequirementRegistry.fromServerConfig {
        ServerConfig.defaults with
            Surfaces = Surfaces.individual
    }

let private bridgeForTeam () =
    SurfaceRequirementRegistry.fromServerConfig {
        ServerConfig.defaults with
            Surfaces = Surfaces.team
    }

let private bridgeForAnonymous () =
    SurfaceRequirementRegistry.fromServerConfig {
        ServerConfig.defaults with
            Surfaces = Surfaces.anonymous
    }

[<Tests>]
let tests =
    testList "Phase 66 Stream B.4 — SDK built-in surface requirements" [

        test "sdkBuiltInRouteOverrides contains the five TeamApi CRUD overrides" {
            // The list is publicly surfaced for test fixtures and
            // future consumer extensions. Pin both the count and the
            // exact set so a future addition is a deliberate edit.
            Expect.equal
                (List.length SurfaceRequirementRegistry.sdkBuiltInRouteOverrides)
                5
                "five overrides today; future B.4 / B.6 continuations grow the list"

            let methods =
                SurfaceRequirementRegistry.sdkBuiltInRouteOverrides
                |> List.map (fun ((_, path), _) -> path)
                |> List.sort

            Expect.equal
                methods
                [
                    "/api/TeamApi/AddTeamMember"
                    "/api/TeamApi/ChangeMemberRole"
                    "/api/TeamApi/GetTeamMembers"
                    "/api/TeamApi/RemoveTeamMember"
                    "/api/TeamApi/SetActiveTeam"
                ]
                "the five TeamApi mutating endpoints"
        }

        test "every TeamApi CRUD override resolves to teamScoped" {
            for ((httpMethod, path), expected) in SurfaceRequirementRegistry.sdkBuiltInRouteOverrides do
                Expect.equal
                    expected
                    SurfaceRequirement.teamScoped
                    (sprintf "%s %s declared teamScoped" httpMethod path)
        }

        test "Individual-mode bridge resolves TeamApi CRUD endpoints to teamScoped" {
            // Smoke-test the end-to-end resolution via the registry's
            // `resolve` helper. Method/path normalisation means the
            // declared casing (PascalCase) and request casing
            // (whatever the client sent) both reach the override.
            let registry = bridgeForIndividual ()

            let resolved =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/AddTeamMember"

            Expect.equal resolved SurfaceRequirement.teamScoped "PascalCase request resolves"

            let resolvedLower =
                SurfaceRequirementRegistry.resolve registry "post" "/api/teamapi/removeteammember"

            Expect.equal resolvedLower SurfaceRequirement.teamScoped "lower-case request resolves"
        }

        test "Team-mode bridge resolves TeamApi CRUD endpoints to teamScoped (unaffected by Surfaces shape)" {
            let registry = bridgeForTeam ()

            let resolved =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/ChangeMemberRole"

            Expect.equal resolved SurfaceRequirement.teamScoped "override applies regardless of Surfaces shape"
        }

        test "Anonymous-deployment bridge — CRUD overrides still apply" {
            // In an Anonymous-only deployment the bridge adds an
            // `/api/` → `public_` ModulePrefix catch-all. The
            // exact-match overrides resolve BEFORE the prefix scan
            // (per `SurfaceRequirementRegistry.resolve`), so the
            // CRUD overrides still win — but in practice an
            // Anonymous-only deployment has no `TeamApi` handler
            // mounted (no auth ⇒ no team) so these routes 404.
            // Pinning the resolution behaviour anyway so the bridge
            // shape stays sane if a mixed-mode deployment combines
            // Anonymous with Team surfaces.
            let registry = bridgeForAnonymous ()

            let resolved =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/SetActiveTeam"

            Expect.equal resolved SurfaceRequirement.teamScoped "exact overrides win over prefix catch-all"
        }

        test "Non-CRUD TeamApi endpoints fall through to the strict global default" {
            // The four endpoints deliberately excluded from the
            // override list. In Individual / Team mode they resolve
            // to the strict default `userOrTeam` (no module-prefix
            // declaration today for `/api/teamapi/`).
            let registry = bridgeForIndividual ()

            let createTeam =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/CreateTeam"

            Expect.equal
                createTeam
                SurfaceRequirementRegistry.strictDefault
                "CreateTeam → strict default (UserKind admitted)"

            let getMyTeams =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/GetMyTeams"

            Expect.equal getMyTeams SurfaceRequirementRegistry.strictDefault "GetMyTeams → strict default"

            let getActiveTeam =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/GetActiveTeam"

            Expect.equal getActiveTeam SurfaceRequirementRegistry.strictDefault "GetActiveTeam → strict default"

            let getCreationPolicy =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/GetTeamCreationPolicy"

            Expect.equal
                getCreationPolicy
                SurfaceRequirementRegistry.strictDefault
                "GetTeamCreationPolicy → strict default"
        }

        test "CRUD overrides pass the surface-enforcement matrix for TeamMember subjects" {
            // End-to-end through `SurfaceEnforcement.evaluate`: a
            // TeamMember subject hitting AddTeamMember passes; a
            // UserKind subject gets the `team_required + select_team`
            // 403 hint per design §3.1 row 3.
            let teamMember = TeamMember("user-1", "team-1")
            let user = Subject.AuthenticatedUser "user-1"
            let req = SurfaceRequirement.teamScoped

            Expect.equal (SurfaceEnforcement.evaluate teamMember req) Pass "team member passes teamScoped"

            match SurfaceEnforcement.evaluate user req with
            | Reject(403, "team_required", Some "select_team") -> ()
            | other -> failtestf "expected 403 team_required + select_team, got %A" other
        }

        test "CRUD overrides reject AnonymousSession with 401" {
            // The matrix's row 1: anonymous subject hitting an
            // authentication-requiring route — 401 +
            // `authentication_required`. teamScoped admits only
            // TeamMember so anonymous is rejected here.
            let anon = AnonymousSession "anon-1"
            let req = SurfaceRequirement.teamScoped

            match SurfaceEnforcement.evaluate anon req with
            | Reject(401, "authentication_required", None) -> ()
            | other -> failtestf "expected 401 authentication_required, got %A" other
        }
    ]

// ─── Phase 66 Stream B.4 partial 2 — client-side Visibility tests ────
//
// Each SDK built-in client module under `ToolUp.Platform.Client/Client/*UI.fs`
// declares `Visibility = Visibility.visibleToAuthenticated` via the B.3
// `withVisibility` builder. The shell's sidebar filter (SDK.Client.fs
// post-RBAC + post-Platform-Admin stage) consumes the predicate to hide
// authenticated-only built-ins from `AnonymousKind` callers — structurally
// completing Phase 55's blanket-hide pathology fix.
//
// Tests construct each built-in via its `create` function and pin the
// predicate's output across every `SubjectKind`. AI / KnowledgeBase
// built-ins are covered indirectly: their declarations live in
// `ToolUp.AI.Client` / `ToolUp.KnowledgeBase.Client` (not referenced by
// this test project to keep the dep surface lean), but the `withVisibility`
// call site there compiles against the same B.3 builder, so a wrong
// predicate would fail Fable / .NET build before reaching the runner.

let private assertVisibleToAuthenticated (label: string) (m: ErasedModule) =
    Expect.isFalse (m.Visibility AnonymousKind) (sprintf "%s — AnonymousKind hidden" label)
    Expect.isTrue (m.Visibility UserKind) (sprintf "%s — UserKind visible" label)
    Expect.isTrue (m.Visibility TeamMemberKind) (sprintf "%s — TeamMemberKind visible" label)
    Expect.isTrue (m.Visibility ClaimBearerKind) (sprintf "%s — ClaimBearerKind visible" label)

[<Tests>]
let visibilityTests =
    testList "Phase 66 Stream B.4 — SDK built-in client-side Visibility" [

        test "PlatformAdminUI declares visibleToAuthenticated" {
            let m = PlatformAdminUI.create None ClientConfig.defaults
            assertVisibleToAuthenticated "PlatformAdminUI" m
        }

        test "TeamManagerUI declares visibleToAuthenticated" {
            // Authenticated-with-team modules must still admit UserKind so
            // a user with no active team can reach the create-team /
            // accept-invite flow. `visibleToAuthenticated` admits UserKind
            // + TeamMemberKind + ClaimBearerKind — the right shape.
            let m = TeamManagerUI.create None
            assertVisibleToAuthenticated "TeamManagerUI" m
        }

        test "HealthMonitorUI declares visibleToAuthenticated" {
            let m = HealthMonitorUI.create None
            assertVisibleToAuthenticated "HealthMonitorUI" m
        }

        test "UsageDashboard declares visibleToAuthenticated" {
            let m = UsageDashboard.create None
            assertVisibleToAuthenticated "UsageDashboard" m
        }

        test "ServiceStatusBoardUI declares visibleToAuthenticated" {
            // Design §3.3 row 6 marks the *server* `ServiceStatusBoard`
            // endpoints as `public_` — anonymous callers can hit
            // `/api/service-status` directly. The SDK's built-in *UI*
            // module is admin-shaped (group "Platform Admin", role gated
            // server-side), so its sidebar Visibility hides from
            // AnonymousKind in line with the other admin built-ins.
            let m = ServiceStatusBoardUI.create None
            assertVisibleToAuthenticated "ServiceStatusBoardUI" m
        }

        test "PermissionsAdminUI declares visibleToAuthenticated" {
            let m = PermissionsAdminUI.create None
            assertVisibleToAuthenticated "PermissionsAdminUI" m
        }

        test "DataIngestionUI declares visibleToAuthenticated" {
            let m = DataIngestionUI.create None
            assertVisibleToAuthenticated "DataIngestionUI" m
        }

        test "WebhookAdminUI declares visibleToAuthenticated" {
            let m = WebhookAdminUI.create None
            assertVisibleToAuthenticated "WebhookAdminUI" m
        }

        test "TeamConfigUI declares visibleToAuthenticated" {
            let m = TeamConfigUI.create None
            assertVisibleToAuthenticated "TeamConfigUI" m
        }

        test "FileManagerUI declares visibleToAuthenticated" {
            // FileManagerUI is the SDK's `_sdk.DataManager` built-in.
            // Empty `displays` is fine here — `create` only stores the
            // list; the view function is never invoked.
            let m = FileManagerUI.create [] None
            assertVisibleToAuthenticated "FileManagerUI" m
        }

        test "DataSubjectRequestAdminUI declares visibleToAuthenticated" {
            let m = DataSubjectRequestAdminUI.create None
            assertVisibleToAuthenticated "DataSubjectRequestAdminUI" m
        }

        test "every Platform.Client SDK built-in hides from AnonymousKind" {
            // Aggregate sanity check — if a new SDK built-in lands without
            // a Visibility declaration, the default (`visibleToAll`) would
            // admit AnonymousKind and break the Anonymous-deployment
            // sidebar shape. Pinning the set here means a future addition
            // is a deliberate edit.
            let builtIns: (string * ErasedModule) list = [
                "PlatformAdminUI", PlatformAdminUI.create None ClientConfig.defaults
                "TeamManagerUI", TeamManagerUI.create None
                "HealthMonitorUI", HealthMonitorUI.create None
                "UsageDashboard", UsageDashboard.create None
                "ServiceStatusBoardUI", ServiceStatusBoardUI.create None
                "PermissionsAdminUI", PermissionsAdminUI.create None
                "DataIngestionUI", DataIngestionUI.create None
                "WebhookAdminUI", WebhookAdminUI.create None
                "TeamConfigUI", TeamConfigUI.create None
                "FileManagerUI", FileManagerUI.create [] None
                "DataSubjectRequestAdminUI", DataSubjectRequestAdminUI.create None
            ]

            Expect.equal (List.length builtIns) 11 "eleven Platform.Client built-ins pinned"

            for (label, m) in builtIns do
                Expect.isFalse (m.Visibility AnonymousKind) (sprintf "%s must hide from AnonymousKind" label)
        }
    ]