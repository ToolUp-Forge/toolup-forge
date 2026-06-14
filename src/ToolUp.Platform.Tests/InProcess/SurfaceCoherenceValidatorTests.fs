module ToolUp.Platform.Tests.InProcess.SurfaceCoherenceValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.ConfigValidation

// ─── Phase 66 Stream B.2 — SurfaceCoherenceValidator tests ───────────
//
// One test per rule (10 rules — 8 base from design §3.8 + 2 from
// §3.11) plus a happy-path test. Each rule fires through the
// publicly-surfaced `evaluate` helper so a future `/dev/inspect`
// Validators panel can render per-rule outcomes without parsing
// the aggregated message.

let private validate
    (config: ServerConfig)
    (authProvider: IAuthProvider option)
    (moduleSurfaceDefaults: (string * SurfaceRequirement) list)
    (routeSurfaceOverrides: ((string * string) * SurfaceRequirement) list)
    (shareTokenStoreDecoratorCount: int)
    : ValidationResult =
    SurfaceCoherenceValidator.evaluate
        config
        authProvider
        moduleSurfaceDefaults
        routeSurfaceOverrides
        shareTokenStoreDecoratorCount

let private headerAuth () : IAuthProvider =
    HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider

/// A stand-in for a "non-HeaderAuth" provider for Rule 7. Anything
/// whose runtime type is NOT `HeaderAuthProvider` qualifies; the
/// simplest stand-in is an inline object expression implementing
/// `IAuthProvider` minimally.
let private nonHeaderAuth () : IAuthProvider =
    let user = AuthenticatedUser.anonymous

    { new IAuthProvider with
        member _.GetUser(_ctx) = async { return user }

        member _.ValidateRequest(_ctx) = async { return Result.Ok user }
        member _.IsCryptographicallyVerified = true
    }

let private cfg (surfaces: SurfaceProfile list) (shareTokenStore: ShareTokenStoreMode) = {
    ServerConfig.defaults with
        Surfaces = surfaces
        ShareTokenStore = shareTokenStore
}

[<Tests>]
let tests =
    testList "Phase 66 Stream B.2 — SurfaceCoherenceValidator" [

        // ─── Happy path ──────────────────────────────────────────────

        test "Happy path — Surfaces.individual + HeaderAuth + no overrides → Ok" {
            let result =
                validate (cfg Surfaces.individual NoShareTokenStore) (Some(headerAuth ())) [] [] 0

            Expect.equal result Ok "coherent single-surface deployment passes"
        }

        test
            "Happy path — mixed-mode [individual; team; claimBearer] + ShareTokenStore enabled + Team-CRUD overrides → Ok" {
            let surfaces = [ SurfaceProfile.individual; SurfaceProfile.team; SurfaceProfile.claimBearer ]

            let overrides = [ ("POST", "/api/team/add-member"), SurfaceRequirement.teamScoped ]

            let result =
                validate (cfg surfaces EnabledShareTokenStore) (Some(headerAuth ())) [] overrides 0

            Expect.equal result Ok "coherent mixed-mode deployment passes"
        }

        // ─── Rule 1 — Surfaces is empty ─────────────────────────────

        test "Rule 1 — empty Surfaces → Error" {
            let result = validate (cfg [] NoShareTokenStore) (Some(headerAuth ())) [] [] 0

            match result with
            | Error msg ->
                Expect.stringContains msg "ServerConfig.Surfaces is empty" "names the empty Surfaces"
                Expect.stringContains msg "at least one SurfaceProfile" "suggests remediation"
            | other -> failtestf "expected Error, got %A" other
        }

        // ─── Rule 2 — Duplicate constructors ────────────────────────

        test "Rule 2 — duplicate Anonymous constructors → Error" {
            let surfaces = [ SurfaceProfile.anonymous; SurfaceProfile.anonymousPersistent ]

            let result = validate (cfg surfaces NoShareTokenStore) (Some(headerAuth ())) [] [] 0

            match result with
            | Error msg ->
                Expect.stringContains msg "duplicate SurfaceProfile constructors" "names the issue"
                Expect.stringContains msg "Anonymous × 2" "lists the duplicate count"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Rule 2 — duplicate Team constructors (team + multiTeam) → Error" {
            let surfaces = [ SurfaceProfile.team; SurfaceProfile.multiTeam ]

            let result = validate (cfg surfaces NoShareTokenStore) (Some(headerAuth ())) [] [] 0

            match result with
            | Error msg -> Expect.stringContains msg "Team × 2" "team + multiTeam both produce Team"
            | other -> failtestf "expected Error, got %A" other
        }

        // ─── Rule 3 — Module surface-requirement unreachable ────────

        test "Rule 3 — module teamScoped requirement + Surfaces.individual → Error" {
            let moduleDefaults = [ "/api/forms/", SurfaceRequirement.teamScoped ]

            let result =
                validate (cfg Surfaces.individual NoShareTokenStore) (Some(headerAuth ())) moduleDefaults [] 0

            match result with
            | Error msg ->
                Expect.stringContains msg "/api/forms/" "names the route prefix"
                Expect.stringContains msg "unreachable" "explains the consequence"
                Expect.stringContains msg "TeamMemberKind" "names the missing kind"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Rule 3 — module userOrTeam requirement + Surfaces.anonymous → Error" {
            let moduleDefaults = [ "/api/private/", SurfaceRequirement.userOrTeam ]

            let result =
                validate (cfg Surfaces.anonymous NoShareTokenStore) (Some(headerAuth ())) moduleDefaults [] 0

            match result with
            | Error msg ->
                Expect.stringContains msg "/api/private/" "names the route prefix"
                Expect.stringContains msg "unreachable" "explains the consequence"
            | other -> failtestf "expected Error, got %A" other
        }

        // ─── Rule 4 — Route-level override unreachable ──────────────

        test "Rule 4 — route claimBearerOnly + Surfaces.individual → Error" {
            let overrides = [
                ("POST", "/api/public/forms/SubmitWithToken"), SurfaceRequirement.claimBearerOnly
            ]

            let result =
                validate (cfg Surfaces.individual NoShareTokenStore) (Some(headerAuth ())) [] overrides 0

            match result with
            | Error msg ->
                Expect.stringContains msg "POST" "names the method"
                Expect.stringContains msg "/api/public/forms/SubmitWithToken" "names the path"
                Expect.stringContains msg "unreachable" "explains the consequence"
            | other -> failtestf "expected Error, got %A" other
        }

        // ─── Rule 5 — ClaimBearer + NoShareTokenStore explicit ──────

        test "Rule 5 — Surfaces contains ClaimBearer + ShareTokenStore=NoShareTokenStore → Warning" {
            let surfaces = [ SurfaceProfile.individual; SurfaceProfile.claimBearer ]

            let result = validate (cfg surfaces NoShareTokenStore) (Some(headerAuth ())) [] [] 0

            match result with
            | Warning msg ->
                Expect.stringContains msg "ClaimBearer" "names the surface"
                Expect.stringContains msg "NoShareTokenStore" "names the store setting"
                Expect.stringContains msg "auto-promote" "explains the auto-promotion fallback"
            | other -> failtestf "expected Warning, got %A" other
        }

        // ─── Rule 6 — EnabledShareTokenStore + no ClaimBearer ───────

        test "Rule 6 — ShareTokenStore=EnabledShareTokenStore + Surfaces.individual → Warning" {
            let result =
                validate (cfg Surfaces.individual EnabledShareTokenStore) (Some(headerAuth ())) [] [] 0

            match result with
            | Warning msg ->
                Expect.stringContains msg "EnabledShareTokenStore" "names the store setting"
                Expect.stringContains msg "no SurfaceProfile.ClaimBearer" "explains the gap"
            | other -> failtestf "expected Warning, got %A" other
        }

        // ─── Rule 7 — Anonymous-only + non-HeaderAuth provider ──────

        test "Rule 7 — Surfaces.anonymous + non-HeaderAuth provider → Warning" {
            let result =
                validate (cfg Surfaces.anonymous NoShareTokenStore) (Some(nonHeaderAuth ())) [] [] 0

            match result with
            | Warning msg ->
                Expect.stringContains msg "only Anonymous" "names the deployment shape"
                Expect.stringContains msg "non-HeaderAuthProvider" "names the provider issue"
                Expect.stringContains msg "unreachable" "explains the consequence"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Rule 7 — Surfaces.anonymous + HeaderAuthProvider → Ok (the dev path)" {
            let result =
                validate (cfg Surfaces.anonymous NoShareTokenStore) (Some(headerAuth ())) [] [] 0

            Expect.equal result Ok "anon + HeaderAuth is the canonical dev shape"
        }

        // ─── Rule 8 — Non-Anonymous surface + no auth provider ──────

        test "Rule 8 — Surfaces.individual + authProvider = None → Error" {
            let result = validate (cfg Surfaces.individual NoShareTokenStore) None [] [] 0

            match result with
            | Error msg ->
                Expect.stringContains msg "Individual" "names the surface label"
                Expect.stringContains msg "withAuth was never called" "names the remediation"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Rule 8 — Surfaces.team + authProvider = None → Error" {
            let result = validate (cfg Surfaces.team NoShareTokenStore) None [] [] 0

            match result with
            | Error msg -> Expect.stringContains msg "Team" "names the surface label"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Rule 8 — Surfaces.anonymous + authProvider = None → Ok (anon needs no auth)" {
            let result = validate (cfg Surfaces.anonymous NoShareTokenStore) None [] [] 0
            Expect.equal result Ok "anonymous-only deployment does not require an auth provider"
        }

        // ─── Rule 9 — decorator wired but no ClaimBearer surface ────

        test "Rule 9 — decorator wired + Surfaces.individual (no ClaimBearer) → Warning" {
            // Decorator chain length = 1 but Surfaces lacks ClaimBearer.
            // The deployment has a Team surface so Rule 10 does not fire.
            let surfaces = [ SurfaceProfile.individual; SurfaceProfile.team ]

            let result = validate (cfg surfaces NoShareTokenStore) (Some(headerAuth ())) [] [] 1

            match result with
            | Warning msg ->
                Expect.stringContains msg "withShareTokenStoreDecorator was called 1" "counts the calls"
                Expect.stringContains msg "no SurfaceProfile.ClaimBearer" "explains the gap"
            | other -> failtestf "expected Warning, got %A" other
        }

        // ─── Rule 10 — decorator wired but no Team surface ──────────

        test "Rule 10 — decorator wired + Surfaces.individual + ClaimBearer (no Team) → Warning" {
            // Decorator chain length = 1; ClaimBearer surface is present
            // so Rule 9 does not fire. Team is absent → Rule 10 fires.
            let surfaces = [ SurfaceProfile.individual; SurfaceProfile.claimBearer ]

            let result =
                validate (cfg surfaces EnabledShareTokenStore) (Some(headerAuth ())) [] [] 1

            match result with
            | Warning msg ->
                Expect.stringContains msg "withShareTokenStoreDecorator was called 1" "counts the calls"
                Expect.stringContains msg "no SurfaceProfile.Team" "explains the gap"
                Expect.stringContains msg "MembershipChanged" "names the trigger event the decorator subscribes to"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Rules 9+10 — decorator wired + Surfaces.individual (no Team, no ClaimBearer) → Warning naming both gaps" {
            let result =
                validate (cfg Surfaces.individual NoShareTokenStore) (Some(headerAuth ())) [] [] 1

            match result with
            | Warning msg ->
                Expect.stringContains msg "no SurfaceProfile.ClaimBearer" "Rule 9 fires"
                Expect.stringContains msg "no SurfaceProfile.Team" "Rule 10 fires"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Rules 9+10 — decorator wired + teamWithShareTokens → Ok (both surfaces present)" {
            let result =
                validate (cfg Surfaces.teamWithShareTokens EnabledShareTokenStore) (Some(headerAuth ())) [] [] 1

            Expect.equal result Ok "decorator + team + claimBearer is the canonical RevokeOnIssuerRemoved shape"
        }

        // ─── Aggregation ────────────────────────────────────────────

        test "Error wins over Warning when both fire" {
            // Empty Surfaces (Error) + decorator wired (Warning). The
            // aggregator should return Error.
            let result = validate (cfg [] NoShareTokenStore) (Some(headerAuth ())) [] [] 1

            match result with
            | Error _ -> ()
            | other -> failtestf "expected Error wins over Warning, got %A" other
        }

        // ─── Validator metadata ─────────────────────────────────────

        test "Validator metadata is well-formed" {
            let v =
                SurfaceCoherenceValidator.SurfaceCoherenceValidator(
                    cfg Surfaces.individual NoShareTokenStore,
                    Some(headerAuth ()),
                    [],
                    [],
                    0
                )
                :> IConfigValidator

            Expect.equal v.Name "surface-coherence" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }

        test "Validator routes through the IConfigValidator.Validate path" {
            let v =
                SurfaceCoherenceValidator.SurfaceCoherenceValidator(
                    cfg [] NoShareTokenStore,
                    Some(headerAuth ()),
                    [],
                    [],
                    0
                )
                :> IConfigValidator

            let result = v.Validate() |> Async.RunSynchronously

            match result with
            | Error _ -> ()
            | other -> failtestf "expected Error from empty Surfaces, got %A" other
        }
    ]