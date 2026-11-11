module ToolUp.Platform.Tests.InProcess.BuiltInModuleSurfaceTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Scope.SurfaceEnforcement
open ToolUp.Platform.Config
open ToolUp.Platform.Scope

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
//   * AddTeamMember, RemoveTeamMember, ChangeMemberRole — `teamScoped`.
//     Owner/Admin gated inside the handler; the surface override
//     fails-fast at the middleware for `UserKind` callers with no
//     active team.
//   * SetActiveTeam, GetTeamMembers — `userOrTeam`. SetActiveTeam is
//     the transition INTO team scope: a member whose active-team
//     pointer is unset resolves as `UserKind`, so gating the route on
//     `TeamMemberKind` deadlocked onboarding (you couldn't become a
//     TeamMember without already being one). The handler validates
//     membership server-side. GetTeamMembers follows for the same
//     onboarding window (no-active-team member viewing their roster);
//     its handler returns `[]` for non-members.
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

        test "TeamApi CRUD overrides — mutating ops teamScoped, transition ops userOrTeam" {
            let expectedByPath =
                Map.ofList [
                    "/api/TeamApi/AddTeamMember", SurfaceRequirement.teamScoped
                    "/api/TeamApi/RemoveTeamMember", SurfaceRequirement.teamScoped
                    "/api/TeamApi/ChangeMemberRole", SurfaceRequirement.teamScoped
                    // The two onboarding-window routes: a `UserKind`
                    // caller (signed in, no active team) must be able
                    // to set their active team and view their roster,
                    // or they can never transition into team scope.
                    "/api/TeamApi/GetTeamMembers", SurfaceRequirement.userOrTeam
                    "/api/TeamApi/SetActiveTeam", SurfaceRequirement.userOrTeam
                ]

            for ((httpMethod, path), actual) in SurfaceRequirementRegistry.sdkBuiltInRouteOverrides do
                match expectedByPath.TryFind path with
                | Some expected -> Expect.equal actual expected (sprintf "%s %s declared requirement" httpMethod path)
                | None -> failtestf "unexpected override path %s — update expectedByPath" path
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

            Expect.equal resolved SurfaceRequirement.userOrTeam "exact overrides win over prefix catch-all"
        }

        test "SetActiveTeam admits UserKind — the onboarding-deadlock regression pin" {
            // The active-team pointer is what upgrades a subject from
            // `UserKind` to `TeamMemberKind`. When this route was gated
            // `teamScoped`, a member added by an admin / invite (whose
            // pointer no add path used to set) could NEVER transition:
            // they were `UserKind` until the pointer was set, and
            // setting the pointer required `TeamMemberKind`. Pin both
            // halves: the bridge resolves `userOrTeam` for the route,
            // and the matrix passes a `UserKind` subject through it.
            let registry = bridgeForTeam ()

            let requirement =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/SetActiveTeam"

            Expect.equal requirement SurfaceRequirement.userOrTeam "SetActiveTeam resolves userOrTeam"

            let user = Subject.AuthenticatedUser "freshly-added-member"

            Expect.equal
                (SurfaceEnforcement.evaluate user requirement)
                Pass
                "UserKind passes SetActiveTeam — the transition into team scope is reachable"

            // Membership listing for the onboarding window rides the
            // same requirement.
            let rosterRequirement =
                SurfaceRequirementRegistry.resolve registry "POST" "/api/TeamApi/GetTeamMembers"

            Expect.equal (SurfaceEnforcement.evaluate user rosterRequirement) Pass "UserKind passes GetTeamMembers"

            // Anonymous callers stay rejected — loosening to
            // `userOrTeam` must not open the route to AnonymousKind.
            match SurfaceEnforcement.evaluate (AnonymousSession "anon") requirement with
            | Reject(401, "authentication_required", None) -> ()
            | other -> failtestf "expected 401 authentication_required for anonymous, got %A" other
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
            let m =
                ShellSlots.builtIn (PlatformAdminUI.builtIn PublicUtilityConfig.defaults) None

            assertVisibleToAuthenticated "PlatformAdminUI" m
        }

        test "TeamManagerUI declares visibleToAuthenticated" {
            // Authenticated-with-team modules must still admit UserKind so
            // a user with no active team can reach the create-team /
            // accept-invite flow. `visibleToAuthenticated` admits UserKind
            // + TeamMemberKind + ClaimBearerKind — the right shape.
            let m = ShellSlots.builtIn TeamManagerUI.builtIn None
            assertVisibleToAuthenticated "TeamManagerUI" m
        }

        test "HealthMonitorUI declares visibleToAuthenticated" {
            let m = ShellSlots.builtIn HealthMonitorUI.builtIn None
            assertVisibleToAuthenticated "HealthMonitorUI" m
        }

        test "UsageDashboard declares visibleToAuthenticated" {
            let m = ShellSlots.builtIn UsageDashboard.builtIn None
            assertVisibleToAuthenticated "UsageDashboard" m
        }

        test "ServiceStatusBoardUI declares visibleToAuthenticated" {
            // Design §3.3 row 6 marks the *server* `ServiceStatusBoard`
            // endpoints as `public_` — anonymous callers can hit
            // `/api/service-status` directly. The SDK's built-in *UI*
            // module is admin-shaped (group "Platform Admin", role gated
            // server-side), so its sidebar Visibility hides from
            // AnonymousKind in line with the other admin built-ins.
            let m = ShellSlots.builtIn ServiceStatusBoardUI.builtIn None
            assertVisibleToAuthenticated "ServiceStatusBoardUI" m
        }

        test "PermissionsAdminUI declares visibleToAuthenticated" {
            let m = ShellSlots.builtIn PermissionsAdminUI.builtIn None
            assertVisibleToAuthenticated "PermissionsAdminUI" m
        }

        test "DataIngestionUI declares visibleToAuthenticated" {
            let m = ShellSlots.builtIn DataIngestionUI.builtIn None
            assertVisibleToAuthenticated "DataIngestionUI" m
        }

        test "WebhookAdminUI declares visibleToAuthenticated" {
            let m = ShellSlots.builtIn WebhookAdminUI.builtIn None
            assertVisibleToAuthenticated "WebhookAdminUI" m
        }

        test "TeamConfigUI declares visibleToAuthenticated" {
            let m = ShellSlots.builtIn TeamConfigUI.builtIn None
            assertVisibleToAuthenticated "TeamConfigUI" m
        }

        test "FileManagerUI declares visibleToAuthenticated" {
            // FileManagerUI is the SDK's `_sdk.DataManager` built-in.
            // Empty `displays` is fine here — `create` only stores the
            // list; the view function is never invoked.
            let m =
                ShellSlots.builtIn (FileManagerUI.builtIn [] DataManagerConfig.unlabelled) None

            assertVisibleToAuthenticated "FileManagerUI" m
        }

        test "DataSubjectRequestAdminUI declares visibleToAuthenticated" {
            let m = ShellSlots.builtIn DataSubjectRequestAdminUI.builtIn None
            assertVisibleToAuthenticated "DataSubjectRequestAdminUI" m
        }

        test "every Platform.Client SDK built-in hides from AnonymousKind" {
            // Aggregate sanity check — if a new SDK built-in lands without
            // a Visibility declaration, the default (`visibleToAll`) would
            // admit AnonymousKind and break the Anonymous-deployment
            // sidebar shape. Pinning the set here means a future addition
            // is a deliberate edit.
            let builtIns: (string * ErasedModule) list = [
                "PlatformAdminUI", ShellSlots.builtIn (PlatformAdminUI.builtIn PublicUtilityConfig.defaults) None
                "TeamManagerUI", ShellSlots.builtIn TeamManagerUI.builtIn None
                "HealthMonitorUI", ShellSlots.builtIn HealthMonitorUI.builtIn None
                "UsageDashboard", ShellSlots.builtIn UsageDashboard.builtIn None
                "ServiceStatusBoardUI", ShellSlots.builtIn ServiceStatusBoardUI.builtIn None
                "PermissionsAdminUI", ShellSlots.builtIn PermissionsAdminUI.builtIn None
                "DataIngestionUI", ShellSlots.builtIn DataIngestionUI.builtIn None
                "WebhookAdminUI", ShellSlots.builtIn WebhookAdminUI.builtIn None
                "TeamConfigUI", (ShellSlots.builtIn TeamConfigUI.builtIn) None
                "FileManagerUI", ShellSlots.builtIn (FileManagerUI.builtIn [] DataManagerConfig.unlabelled) None
                "DataSubjectRequestAdminUI", (ShellSlots.builtIn DataSubjectRequestAdminUI.builtIn) None
                "NotificationPreferencesUI", ShellSlots.builtIn NotificationPreferencesUI.builtIn None
            ]

            Expect.equal (List.length builtIns) 12 "twelve Platform.Client built-ins pinned"

            for (label, m) in builtIns do
                Expect.isFalse (m.Visibility AnonymousKind) (sprintf "%s must hide from AnonymousKind" label)
        }
    ]

// ─── Each built-in is one BuiltIn value over the view contract ─────────
//
// A built-in exports a `BuiltIn` value that `ShellSlots.builtIn` builds, and
// its view takes the shell's `RenderContext`; the `create` pipelines and the
// `[<ReactComponent>] XxxBody` components that existed only to read the
// catalog through a hook are gone. Read from the sources, because the
// property is about how the module is written.

/// Repo root (`toolup-forge`) from the running assembly.
let private repoRoot () =
    let assemblyDir =
        System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    System.IO.Path.GetFullPath(System.IO.Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// The client module files: every `*UI.fs` under `src/`, plus the three
/// built-ins whose names do not end in `UI`.
let private clientModuleFiles () =
    let src = System.IO.Path.Combine(repoRoot (), "src")
    let client = System.IO.Path.Combine(src, "ToolUp.Platform.Client", "Client")

    let named = [
        System.IO.Path.Combine(client, "Home.fs")
        System.IO.Path.Combine(client, "AdminHome.fs")
        System.IO.Path.Combine(client, "UsageDashboard.fs")
    ]

    let uiFiles =
        System.IO.Directory.EnumerateFiles(src, "*UI.fs", System.IO.SearchOption.AllDirectories)
        |> Seq.filter (fun f ->
            let segments = f.Split([| '\\'; '/' |])

            not (
                segments
                |> Array.exists (fun seg -> seg = "bin" || seg = "obj" || seg = "node_modules")
            ))
        |> List.ofSeq

    named @ uiFiles |> List.distinct |> List.sort

/// `XxxBody` components: a `[<ReactComponent>]` whose binding is named `…Body`.
let internal hookSiteBodies (lines: string array) : string list = [
    for i in 0 .. lines.Length - 2 do
        if lines[i].Trim() = "[<ReactComponent>]" then
            let m =
                System.Text.RegularExpressions.Regex.Match(
                    lines[i + 1],
                    @"^\s*let\s+(?:private\s+|internal\s+)?(\w+Body)\b"
                )

            if m.Success then
                yield m.Groups[1].Value
]

let builtInContractTests =
    testList "every built-in is one BuiltIn value over the view contract" [
        test "the scan reads the client module files" {
            let files = clientModuleFiles ()
            Expect.isGreaterThanOrEqual (List.length files) 30 "the module files the scan reads"
        }

        test "no client module declares a hook-site body" {
            let offending = [
                for f in clientModuleFiles () do
                    for body in hookSiteBodies (System.IO.File.ReadAllLines f) do
                        yield $"{System.IO.Path.GetFileName f}: {body}"
            ]

            Expect.isEmpty
                offending
                "a [<ReactComponent>] body that exists to read the catalog through a hook; the view reads `ctx.Messages` from its RenderContext"
        }

        test "no built-in module file defines a create pipeline" {
            let createSite =
                System.Text.RegularExpressions.Regex(
                    @"^let create \(",
                    System.Text.RegularExpressions.RegexOptions.Multiline
                )

            let offending = [
                for f in clientModuleFiles () do
                    if createSite.IsMatch(System.IO.File.ReadAllText f) then
                        yield System.IO.Path.GetFileName f
            ]

            Expect.isEmpty offending "a built-in exports a `BuiltIn` value that `ShellSlots.builtIn` builds"
        }

        test "every slot the shell fills with an SDK module builds a BuiltIn value" {
            let slots =
                System.IO.File.ReadAllText(
                    System.IO.Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "ShellSlots.fs")
                )

            Expect.isFalse
                (System.Text.RegularExpressions.Regex.IsMatch(slots, @"\b\w+(?:UI|Home|Dashboard)\.create\b"))
                "a slot row calls a module's create"

            Expect.isGreaterThanOrEqual
                (System.Text.RegularExpressions.Regex.Matches(slots, @"\bbuiltIn\b[^\n]*\.builtIn\b").Count)
                25
                "the slot rows build the built-ins' BuiltIn values"
        }

        test "the scan flags a re-added hook-site body — go-red" {
            let lines = [|
                "[<ReactComponent>]"
                "let private TeamManagerBody (model: Model) (dispatch: Msg -> unit) ="
                "    let msgs = (MessageCatalogProvider.useMessages ()).TeamManager"
                "[<ReactComponent>]"
                "let private QuietHoursForm (current: int) ="
            |]

            Expect.equal (hookSiteBodies lines) [ "TeamManagerBody" ] "the body is flagged; a stateful form is not"
        }
    ]