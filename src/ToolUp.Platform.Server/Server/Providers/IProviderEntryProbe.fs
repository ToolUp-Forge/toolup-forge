// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 43.C — provider verification + live-status probe seam ──
//
// Exercising a configured `ProviderEntry` means BUILDING the vendor
// client and making a real call — which is knowledge the AI companion
// (`IAIProviderFactory`) has and `ToolUp.Platform` deliberately does
// not (GP 1: the SDK core carries no vendor dependency, and it carries
// no AI-assistant dependency either). So the platform owns the SEAM
// and the scheduling; a consumer owns the call.
//
// `ToolUp.AI.Server` ships the first implementation
// (`AIProviderEntryProbe`), built over
// `IAIProviderFactory.TryResolveByLabel` — deliberately the same
// resolution shape the settings UI's "Test connection" already uses,
// rather than a second mechanism that could drift from it. A
// deployment with a different BYOK consumer implements this seam
// instead and gets the same verification-on-add and background-probe
// behaviour for free.
//
// **Six-rule portability audit (GP 12).**
//   1. Identity by value      — `StorageScope` + `ProviderEntry` are
//                                records; the outcome is a record of
//                                primitives. No live handle crosses
//                                the seam, so a probe can run on a
//                                different node from its caller.
//   2. Async at boundary      — the single method returns
//                                `Async<Result<ProviderProbeOutcome, string>>`.
//   3. Retry / supervision as data — failure is the `Result`'s `Error`
//                                string plus the outcome record; no
//                                `OnFailure` callback, and the retry
//                                behaviour belongs to the job
//                                substrate's `JobRetryPolicy`.
//   4. Stateless between calls — every input arrives per call.
//                                Implementations must not cache a
//                                built provider between probes; the
//                                key may have been rotated since.
//   5. No cross-shard ordering — probes for two entries are
//                                independent; nothing is promised
//                                about their relative order.
//   6. Precision at lower bound — no timing primitive here. The probe
//                                JOB declares `JobPrecision.Minute`.

/// What one probe of a configured entry observed. Every field beyond
/// `Reachable` is best-effort: a provider whose API does not expose
/// rate-limit headroom or spend returns `None` rather than a
/// fabricated number, because an invented headroom figure is worse
/// than an absent one — it would drive a dashboard badge that means
/// nothing.
type ProviderProbeOutcome = {
    /// Whether the provider answered successfully. `false` with a
    /// `Diagnostic` is the ordinary "bad key / quota exhausted" shape.
    Reachable: bool
    /// Models the provider reported as available to this credential.
    /// Empty when the provider exposes no model-list endpoint — NOT a
    /// signal that the credential has no models.
    Models: string list
    /// Fraction of the provider's rate limit still available
    /// (0.0–1.0), when the response headers expose it.
    RateLimitHeadroom: float option
    /// Spend recorded against this credential in the provider's
    /// current billing window, when its API exposes it. Advisory
    /// only — never used to gate a request.
    SpendToDate: decimal option
    /// Human-readable diagnostic. On failure this is the provider's
    /// own error text where available (operators read vendor error
    /// codes faster than translated ones). MUST NOT contain key
    /// material.
    Diagnostic: string option
}

module ProviderProbeOutcome =
    /// A successful probe that observed nothing beyond reachability.
    let reachable: ProviderProbeOutcome = {
        Reachable = true
        Models = []
        RateLimitHeadroom = None
        SpendToDate = None
        Diagnostic = None
    }

    /// A failed probe carrying the provider's diagnostic.
    let failed (diagnostic: string) : ProviderProbeOutcome = {
        Reachable = false
        Models = []
        RateLimitHeadroom = None
        SpendToDate = None
        Diagnostic = Some diagnostic
    }

    /// Project an outcome onto the advisory `ProviderHealth` written
    /// through `IProviderProfile.SetEntryHealth`. `prior` supplies the
    /// rolling error count, which resets to 0 on a reachable probe and
    /// increments otherwise — the single place that rule lives, so the
    /// verification call and the background probe cannot disagree
    /// about what "degraded" means.
    ///
    /// The threshold is deliberately blunt: one failure is
    /// `Unhealthy`, because the next user request would fail too and a
    /// dashboard that waits for three failures before saying so has
    /// told the user nothing they could act on. `Degraded` is reserved
    /// for a REACHABLE provider with thin rate-limit headroom
    /// (< 10%) — advisory, and resolution still routes there.
    let toHealth (prior: ProviderHealth) (now: System.DateTime) (outcome: ProviderProbeOutcome) : ProviderHealth =
        if outcome.Reachable then
            let status =
                match outcome.RateLimitHeadroom with
                | Some h when h < 0.1 -> ProviderHealthStatus.Degraded
                | _ -> ProviderHealthStatus.Healthy

            {
                LastVerifiedAt = Some now
                RecentErrorCount = 0
                RateLimitHeadroom = outcome.RateLimitHeadroom
                Status = status
            }
        else
            {
                LastVerifiedAt = prior.LastVerifiedAt
                RecentErrorCount = prior.RecentErrorCount + 1
                RateLimitHeadroom = outcome.RateLimitHeadroom |> Option.orElse prior.RateLimitHeadroom
                Status = ProviderHealthStatus.Unhealthy
            }

/// Seam for exercising a configured `ProviderEntry` against its
/// upstream. Implemented by whichever companion knows how to build a
/// client from the entry; consumed by the verification-on-add path and
/// the background live-status probe.
type IProviderEntryProbe =
    /// Make a small, real call against the entry's credential and
    /// report what was observed.
    ///
    /// **Contract:** the call must be as cheap as the provider allows
    /// (~100 output tokens for an LLM), must not mutate anything
    /// upstream, and must never throw — a vendor exception is reported
    /// as `Ok (ProviderProbeOutcome.failed …)` or `Error`, so a
    /// background job cannot be taken down by a provider SDK.
    ///
    /// `Error` is reserved for "could not even attempt" (the entry
    /// resolved to no provider, the deployment has no factory);
    /// `Ok { Reachable = false }` is "attempted and the upstream said
    /// no". The two drive different UI copy, so they are not collapsed.
    abstract Probe: scope: StorageScope * entry: ProviderEntry -> Async<Result<ProviderProbeOutcome, string>>