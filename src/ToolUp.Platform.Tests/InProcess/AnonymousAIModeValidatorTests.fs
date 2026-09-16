module ToolUp.Platform.Tests.InProcess.AnonymousAIModeValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.ConfigValidation
open ToolUp.AI

// ─── Phase 6m — Anonymous surface + AI + no anonymous rate limit ─────
//
// The refusal has three conjuncts and the tests below pin each one
// independently, because two of them are places where a plausible
// simpler implementation is WRONG:
//
//   * the rate-limit conjunct is `RateLimitConfig.policyFor …
//     AnonymousKind`, not `RateLimitConfig.isEnabled`. A deployment
//     limiting only `UserKind` has a limiter registered and an
//     unlimited anonymous surface — `isEnabled` would pass it.
//   * the BYOK exemption reads `IAIProviderFactory.PlatformDescriptors`
//     rather than the `AIFallbackPolicy` the Phase 6m shard names: the
//     policy is a closure argument to `DefaultAIProviderFactory.create`,
//     not a member of the interface, so no validator can observe it.
//
// The escape hatch DEGRADES the refusal to a `Warning` rather than
// clearing it to `Ok` — same posture as
// `AcceptStickyRoutedAiInMultiInstance`, because upstream per-IP gating
// is an attestation about infrastructure the SDK cannot verify. Both
// grades boot; only `Error` refuses.

let private noCaps: AIProviderCapabilities = {
    Streaming = false
    ToolUse = false
    Vision = false
    SupportsPromptCaching = false
    SupportsTriage = false
    TriageModelId = None
    ProviderName = ""
    Model = ""
}

let private descriptor (id: string) : AIProviderDescriptor = {
    Id = id
    DisplayName = id
    SupportedModels = [ "model-1" ]
    DefaultModel = "model-1"
    Capabilities = {
        noCaps with
            ProviderName = id
            Model = "model-1"
    }
}

/// A factory whose only interesting property here is which providers the
/// DEPLOYMENT pays for. `Available` (the BYOK catalogue) is deliberately
/// populated on the BYOK-only fake so the tests prove the rule keys on
/// `PlatformDescriptors` and not on "any provider at all".
let private fakeFactory (platform: AIProviderDescriptor list) (available: AIProviderDescriptor list) =
    { new IAIProviderFactory with
        member _.Available = available
        member _.PlatformDescriptors = platform
        member _.PlatformDescriptor = platform |> List.tryHead
        member _.Resolve _ctx = async { return Result.Error NoProviderConfigured }
        member _.TryResolveByLabel(_ctx, _label) = async { return Result.Error NoProviderConfigured }
        member _.BuildPlatform(_providerId, _apiKey, _model) = None
    }

let private claude = descriptor "anthropic-claude"
let private openai = descriptor "openai-gpt"

/// Platform-paid: the deployment's own key funds every call.
let private platformPaid = fakeFactory [ claude ] []

/// BYOK-only: providers the USER configures, none the deployment pays
/// for. The documented legitimate Anonymous + AI shape.
let private byokOnly = fakeFactory [] [ claude; openai ]

let private policy: RateLimitPolicy = {
    PermitLimit = 100
    WindowSeconds = 60
    QueueLimit = 20
}

let private cfg (surfaces: SurfaceProfile list) (rateLimit: RateLimitConfig) (hatch: bool) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        RateLimit = rateLimit
        AcceptAnonymousModeWithAI = hatch
}

let private validator (config: ServerConfig) (factory: IAIProviderFactory) : IConfigValidator =
    AnonymousAIModeValidator.AnonymousAIModeValidator(config, factory) :> IConfigValidator

let private validate (config: ServerConfig) (factory: IAIProviderFactory) : ValidationResult =
    (validator config factory).Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 6m — anonymous-mode AI safety net" [

        test "Anonymous + no rate limit + platform-paid provider yields Error (the refusal)" {
            match validate (cfg Surfaces.anonymous RateLimitConfig.none false) platformPaid with
            | Error msg ->
                Expect.stringContains msg "Anonymous" "names the surface"
                Expect.stringContains msg "AnonymousKind" "names the unlimited subject kind"
                Expect.stringContains msg "anthropic-claude" "names the platform-paid provider"

                // The operator-actionable sentence the Phase 6m shard
                // specifies verbatim: all three exits, one line.
                Expect.stringContains
                    msg
                    "Anonymous mode + AI without rate limit is unbounded cost surface; set TOOLUP_RATE_LIMIT_PERMITS=N or use a BYOK-only IAIProviderFactory or set TOOLUP_ACCEPT_ANONYMOUS_MODE_WITH_AI=1"
                    "carries the specified refusal sentence naming all three exits"

                Expect.stringContains msg "AcceptAnonymousModeWithAI" "documents the hatch by field name"
                Expect.stringContains msg "/dev/inspect" "tells the operator where to verify"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Anonymous + uniform rate limit yields Ok (the limiter covers anonymous callers)" {
            let result =
                validate (cfg Surfaces.anonymous (RateLimitConfig.uniform policy) false) platformPaid

            Expect.equal result Ok "a Default policy resolves for every kind, AnonymousKind included"
        }

        test "Anonymous + PerShape policy for AnonymousKind only yields Ok" {
            let rl = RateLimitConfig.perShape (Map.ofList [ AnonymousKind, policy ])
            let result = validate (cfg Surfaces.anonymous rl false) platformPaid
            Expect.equal result Ok "the anonymous surface is the one that is limited"
        }

        test "Anonymous + PerShape policy for UserKind only yields Error (isEnabled would miss this)" {
            // A limiter IS registered, so `RateLimitConfig.isEnabled` is
            // true — and anonymous callers are still unlimited. This is
            // the hole the phase exists to close; keying on `isEnabled`
            // would turn this test green while shipping the bug.
            let rl = RateLimitConfig.perShape (Map.ofList [ UserKind, policy ])

            match validate (cfg Surfaces.anonymous rl false) platformPaid with
            | Error msg -> Expect.stringContains msg "AnonymousKind" "says which kind is unlimited"
            | other -> failtestf "expected Error — UserKind-only limiting leaves anonymous unbounded — got %A" other
        }

        test "BYOK-only factory yields Ok (the documented legitimate Anonymous + AI shape)" {
            let result = validate (cfg Surfaces.anonymous RateLimitConfig.none false) byokOnly

            Expect.equal
                result
                Ok
                "with no platform-paid provider an anonymous caller can only ever get NoProviderConfigured"
        }

        test "Authenticated-only deployment yields Ok (no anonymous surface)" {
            let result =
                validate (cfg Surfaces.individual RateLimitConfig.none false) platformPaid

            Expect.equal result Ok "RateLimitModeValidator owns the authenticated no-limiter case"
        }

        test "Mixed Anonymous + Individual yields Error (anonymous requests still reach AI)" {
            let mixed = Surfaces.anonymous @ Surfaces.individual

            match validate (cfg mixed RateLimitConfig.none false) platformPaid with
            | Error msg -> Expect.stringContains msg "Anonymous" "the mixed label still names the anonymous surface"
            | other -> failtestf "expected Error — a mixed deployment still admits unauthenticated AI — got %A" other
        }

        test "Escape hatch yields Warning (degraded, not cleared) and the deployment boots" {
            match validate (cfg Surfaces.anonymous RateLimitConfig.none true) platformPaid with
            | Warning msg ->
                Expect.stringContains
                    msg
                    "AcceptAnonymousModeWithAI = true"
                    "records that the operator attested upstream cost control"

                Expect.stringContains msg "Residual risk" "keeps the residual exposure visible"
            | other ->
                failtestf "expected Warning — the attestation DEGRADES the refusal, it does not clear it — got %A" other
        }

        test "Escape hatch is inert when the rule would not have fired" {
            let result =
                validate (cfg Surfaces.individual RateLimitConfig.none true) platformPaid

            Expect.equal result Ok "attesting upstream gating on an authenticated deployment changes nothing"
        }

        test "Several platform-paid providers are all named" {
            let many = fakeFactory [ openai; claude ] []

            match validate (cfg Surfaces.anonymous RateLimitConfig.none false) many with
            | Error msg ->
                Expect.stringContains msg "2 platform-paid provider(s)" "reports how many are wired"
                Expect.stringContains msg "anthropic-claude, openai-gpt" "names them, sorted"
            | other -> failtestf "expected Error, got %A" other
        }

        test "no-AI deployments are unaffected — the validator ships in the AI tier" {
            // The validator carries no "is AI composed?" knob: it keys on
            // config + the composed factory. What makes a platform-only
            // deployment safe is that the type is only ever REGISTERED
            // inside the AI compose branch (`AICompose.fs`) and only ever
            // SHIPPED in the AI-tier assembly — the same argument
            // `AICancellationDispatchInstanceValidator` pins.
            let assemblyName =
                typeof<AnonymousAIModeValidator.AnonymousAIModeValidator>.Assembly.GetName().Name

            Expect.equal assemblyName "ToolUp.AI.Server" "the validator stays in the AI tier"
        }

        test "Validator metadata is well-formed" {
            let v = validator (cfg Surfaces.anonymous RateLimitConfig.none false) platformPaid
            Expect.equal v.Name "anonymous-ai-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
            Expect.equal v.Timeout IConfigValidator.defaultTimeout "defaults to the shared preflight timeout"
        }

        test "Timeout override is honoured" {
            let v =
                AnonymousAIModeValidator.AnonymousAIModeValidator(
                    cfg Surfaces.anonymous RateLimitConfig.none false,
                    platformPaid,
                    TimeSpan.FromSeconds 7.0
                )
                :> IConfigValidator

            Expect.equal v.Timeout (TimeSpan.FromSeconds 7.0) "the optional ctor argument wins"
        }
    ]