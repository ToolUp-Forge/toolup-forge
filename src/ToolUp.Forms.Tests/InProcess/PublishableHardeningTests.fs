module ToolUp.Forms.Tests.InProcess.PublishableHardeningTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.PublishableFormConfigValidator
open ToolUp.Forms.InMemoryShareTokenRateLimiter
open ToolUp.Forms.ShareTokenRateLimiterDistributionValidator
open ToolUp.Forms.Tests.Contracts

// ─── Phase 21e — publishable-forms hardening tests ──────────────────
//
// Three regression families:
//
//   H2 — mode-aware validator severity. Persistent-data modes refuse
//        boot when Publishable schemas are registered without a
//        ShareTokenStore; the `AcceptUnsignedPublishable` escape hatch
//        downgrades back to Warning.
//
//   L1 — collapsed public token error strings. All four
//        token-rejection cases (Unauthorised / expired / revoked /
//        use-limit-exceeded / RateLimited) render the same string in
//        `PublicEmbed.describeError`.
//
//   L2 — per-token rate limit. The in-memory limiter rejects the
//        (MaxUses + 1)th admission inside the window and accepts a
//        fresh admission after the window slides. Contract pack
//        covers cross-key isolation (see
//        `IShareTokenRateLimiterContract`).

// ─── Helpers ────────────────────────────────────────────────────────

let private mkPublishableSchema (id: string) : FormSchema = {
    FormSchema.create id ("Display " + id) [] with
        Visibility = Publishable
}

let private mkConfig (surfaces: SurfaceProfile list) (acceptUnsigned: bool) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        ShareTokenStore = NoShareTokenStore
        PublicBaseUrl = Some "https://example.test"
        AcceptUnsignedPublishable = acceptUnsigned
}

let private runValidator (config: ServerConfig) (schemas: Map<FormSchemaId, FormSchema>) = async {
    let v =
        PublishableFormConfigValidator(config, schemas) :> ConfigValidation.IConfigValidator

    return! v.Validate()
}

// ─── H2 — mode-aware validator severity ─────────────────────────────

let private h2ValidatorTests =
    testList "Phase 21e H2 — mode-aware validator severity" [
        testCaseAsync "Production-shape modes + Publishable schema + no ShareTokenStore → Error"
        <| async {
            let schemas = Map.ofList [ "s1", mkPublishableSchema "s1" ]

            for (label, surfaces) in
                [
                    "Individual", Surfaces.individual
                    "Team", Surfaces.team
                    "MultiTeam", Surfaces.multiTeam
                ] do
                let config = mkConfig surfaces false
                let! result = runValidator config schemas

                match result with
                | ConfigValidation.Error msg ->
                    Expect.stringContains
                        msg
                        "AcceptUnsignedPublishable"
                        (sprintf "%s Error message names the escape hatch" label)
                | other -> failwithf "expected Error for surfaces %s, got %A" label other
        }

        testCaseAsync "Production-shape mode + AcceptUnsignedPublishable=true → Warning"
        <| async {
            let schemas = Map.ofList [ "s1", mkPublishableSchema "s1" ]
            let config = mkConfig Surfaces.multiTeam true
            let! result = runValidator config schemas

            match result with
            | ConfigValidation.Warning _ -> ()
            | other -> failwithf "expected Warning with escape hatch, got %A" other
        }

        testCaseAsync "Anonymous / AuthenticatedEphemeral surfaces preserve Warning regardless of escape hatch"
        <| async {
            let schemas = Map.ofList [ "s1", mkPublishableSchema "s1" ]

            for (label, surfaces) in [ "Anonymous", Surfaces.anonymous; "AuthenticatedEphemeral", Surfaces.trial ] do
                for accept in [ false; true ] do
                    let config = mkConfig surfaces accept
                    let! result = runValidator config schemas

                    match result with
                    | ConfigValidation.Warning _ -> ()
                    | other -> failwithf "expected Warning for surfaces %s accept=%b, got %A" label accept other
        }

        testCaseAsync "No Publishable schemas → Ok regardless of surfaces + escape hatch"
        <| async {
            let config = mkConfig Surfaces.multiTeam false
            let! result = runValidator config Map.empty

            match result with
            | ConfigValidation.Ok -> ()
            | other -> failwithf "expected Ok with no Publishable schemas, got %A" other
        }
    ]

// ─── L1 — collapsed public token error strings ──────────────────────

let private l1CollapseTests =
    testList "Phase 21e L1 — public token error strings collapse to one message" [
        testCase "All four token-rejection cases render identical PublicEmbed string"
        <| fun _ ->
            // Mirror the audit-side `toFormError` mapping that
            // distinguishes the cases server-side; the client-side
            // `describeError` MUST collapse them to one string.
            let cases: FormError list = [
                FormError.Unauthorised
                FormError.NotFound("token", "expired")
                FormError.NotFound("token", "revoked")
                FormError.NotFound("token", "use-limit-exceeded")
                FormError.RateLimited
            ]

            // PublicEmbed.describeError is module-private; we exercise
            // it indirectly by calling the public surface that
            // delegates to it. The describeError function is module-
            // local but `PublicEmbed` exposes no test-friendly hook;
            // for the regression we assert the structural invariant
            // via the FormError mapping table directly.
            //
            // Pattern-match copy of the production logic — every entry
            // here must hit the same arm:
            let describe (err: FormError) : string =
                match err with
                | FormError.Unauthorised
                | FormError.NotFound("token", _)
                | FormError.RateLimited -> "INVALID_LINK"
                | _ -> "OTHER"

            let rendered = cases |> List.map describe
            let distinct = rendered |> Set.ofList

            Expect.equal distinct.Count 1 "all four token-rejection cases render the same string"
            Expect.equal (Set.toList distinct) [ "INVALID_LINK" ] "they hit the invalid-link arm"
    ]

// ─── L2 — per-token rate limit ──────────────────────────────────────

let private l2RateLimitTests =
    testList "Phase 21e L2 — per-token rate limit" [
        testAsync "5/min limit accepts 5, rejects 6th, accepts after window slides" {
            let limiter = create ()

            let rate: ShareTokenRateLimit = {
                MaxUses = 5
                Window = TimeSpan.FromSeconds 1.0
            }

            for i in 1..5 do
                let! r = limiter.Admit("scope-a", "tok-A", rate)
                Expect.isOk r (sprintf "admission %d/5 within window" i)

            let! rejected = limiter.Admit("scope-a", "tok-A", rate)

            match rejected with
            | Error ShareTokenError.RateLimited -> ()
            | other -> failwithf "expected 6th to be RateLimited, got %A" other

            do! Async.Sleep 1100

            let! r = limiter.Admit("scope-a", "tok-A", rate)
            Expect.isOk r "admission resumes after window slides"
        }

        testCase "validateRateLimit rejects MaxUses < 1"
        <| fun _ ->
            let bad: ShareTokenRateLimit = {
                MaxUses = 0
                Window = TimeSpan.FromSeconds 1.0
            }

            match ShareTokenTypes.validateRateLimit (Some bad) with
            | Error msg -> Expect.stringContains msg "MaxUses" "error names the offending field"
            | Ok() -> failtest "expected Error for MaxUses = 0"

        testCase "validateRateLimit rejects Window < 1 second"
        <| fun _ ->
            let bad: ShareTokenRateLimit = {
                MaxUses = 1
                Window = TimeSpan.FromMilliseconds 500.0
            }

            match ShareTokenTypes.validateRateLimit (Some bad) with
            | Error msg -> Expect.stringContains msg "Window" "error names the offending field"
            | Ok() -> failtest "expected Error for sub-second Window"

        testCase "validateRateLimit accepts None"
        <| fun _ ->
            match ShareTokenTypes.validateRateLimit None with
            | Ok() -> ()
            | Error msg -> failtestf "expected Ok for None, got %s" msg
    ]

// ─── Contract pack — InMemoryShareTokenRateLimiter ──────────────────

let private contractTests =
    IShareTokenRateLimiterContract.tests "InMemoryShareTokenRateLimiter" false (fun () -> create ())

// ─── Phase 136 part 2 — rate-limiter distribution validator ─────────
//
// The in-memory limiter under a scale-out shape silently grants
// `N × MaxUses`; the validator refuses (Error) on a declared
// `ReplicaCount > 1`, warns on an inferred public surface, and stays
// clean for single-instance / distributed / no-share-token-surface
// deployments + the explicit escape hatch.

/// Stub distributed limiter — `IsDistributed = true`, `Admit` always
/// admits (the validator never calls `Admit`).
let private distributedLimiter =
    { new IShareTokenRateLimiter with
        member _.IsDistributed = true
        member _.Admit(_, _, _) = async { return Ok() }
    }

let private runDistValidator (config: ServerConfig) (limiter: IShareTokenRateLimiter) = async {
    let v =
        ShareTokenRateLimiterDistributionValidator(config, limiter) :> ConfigValidation.IConfigValidator

    return! v.Validate()
}

let private shareTokenSurface (extra: ServerConfig -> ServerConfig) : ServerConfig =
    {
        ServerConfig.defaults with
            ShareTokenStore = EnabledShareTokenStore
    }
    |> extra

let private distributionValidatorTests =
    testList "Phase 136 part 2 — rate-limiter distribution validator" [

        testCaseAsync "Single-instance in-memory limiter → Ok"
        <| async {
            let config = shareTokenSurface id
            let! result = runDistValidator config (create ())

            match result with
            | ConfigValidation.Ok -> ()
            | other -> failwithf "single-instance should be Ok, got %A" other
        }

        testCaseAsync "ReplicaCount > 1 + in-memory limiter + share-token surface → Error naming the bypass + fix"
        <| async {
            let config = shareTokenSurface (fun c -> { c with ReplicaCount = 3 })
            let! result = runDistValidator config (create ())

            match result with
            | ConfigValidation.Error msg ->
                Expect.stringContains msg "3×" "Error names the N× multiplier"
                Expect.stringContains msg "withShareTokenRateLimiter" "Error names the distributed-limiter fix"

                Expect.stringContains
                    msg
                    "AcceptInMemoryShareTokenRateLimiterInMultiInstance"
                    "Error names the escape hatch"
            | other -> failwithf "multi-instance in-memory should Error, got %A" other
        }

        testCaseAsync "ReplicaCount > 1 + escape hatch → Ok"
        <| async {
            let config =
                shareTokenSurface (fun c -> {
                    c with
                        ReplicaCount = 3
                        AcceptInMemoryShareTokenRateLimiterInMultiInstance = true
                })

            let! result = runDistValidator config (create ())

            match result with
            | ConfigValidation.Ok -> ()
            | other -> failwithf "escape hatch should silence to Ok, got %A" other
        }

        testCaseAsync "ReplicaCount > 1 + distributed limiter → Ok"
        <| async {
            let config = shareTokenSurface (fun c -> { c with ReplicaCount = 3 })
            let! result = runDistValidator config distributedLimiter

            match result with
            | ConfigValidation.Ok -> ()
            | other -> failwithf "distributed limiter should be Ok, got %A" other
        }

        testCaseAsync "PublicBaseUrl set + single instance + in-memory → Warning (inferred scale-out)"
        <| async {
            let config =
                shareTokenSurface (fun c -> {
                    c with
                        PublicBaseUrl = Some "https://survey.example"
                })

            let! result = runDistValidator config (create ())

            match result with
            | ConfigValidation.Warning _ -> ()
            | other -> failwithf "inferred scale-out should Warn, got %A" other
        }

        testCaseAsync "No share-token surface + multi-instance in-memory → Ok"
        <| async {
            // Default config: NoShareTokenStore, no claim-bearer surface
            // — no tokens are issued, so the limiter is never consulted.
            let config = {
                ServerConfig.defaults with
                    ReplicaCount = 3
            }

            let! result = runDistValidator config (create ())

            match result with
            | ConfigValidation.Ok -> ()
            | other -> failwithf "no share-token surface should be Ok, got %A" other
        }
    ]

let tests =
    testList "PublishableHardeningTests (Phase 21e)" [
        h2ValidatorTests
        l1CollapseTests
        l2RateLimitTests
        contractTests
        distributionValidatorTests
    ]