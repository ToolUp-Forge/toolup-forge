module ToolUp.AI.AnonymousAIModeValidator

open System
open ToolUp.AI
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6m — Anonymous surface + AI + no anonymous rate limit ─────
//
// `src/ToolUp.AI/README.md` has carried "ToolUp.AI is designed for
// authenticated platform modes" as a documented design PRINCIPLE since
// the companion shipped, with the explicit caveat that `AIServerApp.run`
// does not refuse to start. An operator could therefore deploy the
// wide-open shape — no sign-in, no limiter, the deployment's own
// provider key — with no signal at all until the invoice arrived. This
// validator converts the principle into a runtime refusal.
//
// The rule has three conjuncts, and each is load-bearing:
//
//   1. `DeploymentConfig.hasAnonymous` — some declared surface admits
//      unauthenticated requests. A mixed-mode deployment (Anonymous +
//      Individual) counts: anonymous requests still resolve to
//      `AnonymousKind` and still reach the AI routes.
//   2. `RateLimitConfig.policyFor config.RateLimit AnonymousKind` is
//      `None` — no policy resolves for anonymous callers. Deliberately
//      NOT `RateLimitConfig.isEnabled`: a deployment limiting only
//      `UserKind` has a limiter registered and an unlimited anonymous
//      surface, which is exactly the hole this phase is about.
//   3. The composed `IAIProviderFactory` reports at least one
//      `PlatformDescriptors` entry — the deployment has wired a provider
//      IT pays for. With none wired, `Resolve` can only ever return
//      `NoProviderConfigured` for an anonymous caller (an anonymous
//      subject has no secret scope to hold a BYOK key), so there is no
//      spend to bound and the deployment is exempt.
//
// Conjunct 3 is the BYOK-only exemption the Phase 6m shard names, read
// off the shipped factory shape rather than the shard's 2026-05
// `AIProviderFactory != PlatformOnly` vocabulary — `AIFallbackPolicy` is
// a closure argument to `DefaultAIProviderFactory.create`, not a member
// of `IAIProviderFactory`, so no validator can observe it and adding an
// abstract member would break every implementer. `PlatformDescriptors`
// is on the interface, so the rule works against ANY registered factory
// (GP 12). It over-approximates in one direction only: a `StrictBYOK`
// deployment that ALSO wired platform providers (for the Platform Admin
// key-test path) reports them here even though `fallback` refuses to use
// them. That case refuses and the operator attests — the conservative
// direction, since the opposite error is silent unbounded spend.
//
// Escape hatch: `ServerConfig.AcceptAnonymousModeWithAI`
// (`TOOLUP_ACCEPT_ANONYMOUS_MODE_WITH_AI=1`). Like
// `AcceptStickyRoutedAiInMultiInstance` it DEGRADES the refusal to a
// `Warning` rather than clearing it to `Ok`: per-IP gating at a proxy is
// an assertion about infrastructure the SDK cannot verify, so the
// residual exposure stays visible in the Validators panel. The
// deployment boots either way — only `Error` refuses.
//
// Registered by the AI compose branch, so AI is composed by construction
// (the same posture as `AICancellationDispatchInstanceValidator`); a
// platform-only composition never loads this assembly.

/// Phase 6m — config validator that refuses startup for an AI
/// deployment admitting anonymous requests with no anonymous rate-limit
/// policy and a platform-paid provider wired. The escape hatch
/// `ServerConfig.AcceptAnonymousModeWithAI` degrades the refusal to a
/// `Warning`.
type AnonymousAIModeValidator(config: ServerConfig, factory: IAIProviderFactory, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "anonymous-ai-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            let anonymousSurface = DeploymentConfig.hasAnonymous config

            let anonymousUnlimited =
                (RateLimitConfig.policyFor config.RateLimit AnonymousKind) |> Option.isNone

            let platformPaid = factory.PlatformDescriptors
            let attested = config.AcceptAnonymousModeWithAI

            match anonymousSurface && anonymousUnlimited, platformPaid with
            | false, _
            | _, [] -> return Ok
            | true, descriptors ->
                let providerIds = descriptors |> List.map _.Id |> List.sort |> String.concat ", "

                if not attested then
                    return
                        Error(
                            sprintf
                                "ServerConfig.Surfaces = %s admits unauthenticated requests, no RateLimit policy resolves for AnonymousKind, and the composed IAIProviderFactory wires %d platform-paid provider(s) (%s) — so anyone with the URL drives provider spend against the deployment's own API key, with no identity to attribute it to, rate-limit per, or cut off. Anonymous mode + AI without rate limit is unbounded cost surface; set TOOLUP_RATE_LIMIT_PERMITS=N or use a BYOK-only IAIProviderFactory or set TOOLUP_ACCEPT_ANONYMOUS_MODE_WITH_AI=1. The env var sets ServerConfig.AcceptAnonymousModeWithAI = true, which attests that per-IP gating or request budgets bound the cost upstream; it degrades this refusal to a warning rather than clearing it. Verify in the HealthMonitorUI Preflight tab or /dev/inspect Validators panel."
                                (DeploymentConfig.surfacesLabel config)
                                (List.length descriptors)
                                providerIds
                        )
                else
                    return
                        Warning(
                            sprintf
                                "ServerConfig.Surfaces = %s with no RateLimit policy for AnonymousKind and %d platform-paid provider(s) (%s) wired; upstream cost control attested (AcceptAnonymousModeWithAI = true). Residual risk if the per-IP gating is bypassed, misconfigured or removed: unauthenticated callers drive platform-funded provider spend with no per-user attribution. A per-subject-kind limiter (ServerConfig.RateLimit) is the in-SDK control."
                                (DeploymentConfig.surfacesLabel config)
                                (List.length descriptors)
                                providerIds
                        )
        }