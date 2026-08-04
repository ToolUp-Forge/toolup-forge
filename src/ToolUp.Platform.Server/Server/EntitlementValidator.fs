// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.ConfigValidation

// ─── Phase 492.B/C — projecting entitlements onto flags, and surfacing ─
//                     lifecycle state at boot
//
// `EntitlementToken.fs` establishes WHAT is granted. This file is the two
// places that state has to reach: the gate a feature checks, and the
// operator who has to renew.
//
// **492.B — one gating model, and it is the flag.** Gated code reads a
// Phase 5c feature flag. It never sees a token, a claim set, a phase, or a
// validity window — those exist in the composition root and nowhere else,
// which is what keeps a lapse from being a code path every feature has to
// remember to handle. `EntitlementFlagCeiling.decorate` wraps an existing
// `FlagEvaluator` and caps governed keys at what the entitlement grants.
//
// **Why a ceiling over the evaluator, and not an `IFlagSource`.** The
// Phase 239 `IFlagSource` seam is the obvious fit and is the wrong one:
// sources are consulted only when NO in-process scope set the key, so a
// single Platform-scope flag override would lift the entitlement
// completely. An entitlement a local admin toggle can switch off is not an
// entitlement. The ceiling composes at the evaluator instead — after the
// whole `User → Team → Platform → source → declared default` walk — so
// there is no layer left that could override it. The semantics match the
// Phase 62 `PremiumOnly` precedent exactly (`granted && resolved`: the
// gate is a bound, never a value substitute), so a deployment already
// reasoning about premium gating reasons about this the same way.
//
// **492.C — the preflight never returns `Error`, and that is the design.**
// A Phase 9m `Error` aborts the boot. An entitlement mechanism that can
// abort a boot is a data lockout with extra steps: the customer's data is
// on that disk, and a process that will not start is the most complete way
// to withhold it. So every outcome here is `Ok` or `Warning` — expiry,
// lapse, a tampered token, a wrong key, an unparseable file, a clock
// problem, all of them. The validator's job is to be LOUD, not to be
// fatal, and `EntitlementValidatorTests` asserts across every refusal case
// and every phase that no input produces `Error`, falsified against a
// control validator that does.
//
// It is nonetheless marked `IStructuralClassValidator`, so
// `ServerConfig.SkipPreflight` cannot silence it. It reads local bytes and
// a clock — no socket, microseconds — which is the cost test that
// classification turns on, and an operator riding out a dependency outage
// should not thereby lose the line telling them their entitlement lapsed
// four days ago.

/// Where the deployment's entitlement state comes from. A function, so a
/// composition root reads a mounted file, a config value, a secret store,
/// or nothing at all — forge does not care, and cannot: it has no idea
/// where a given deployment keeps its token.
///
/// Returning the refusal alongside the status is what lets the preflight
/// distinguish "lapsed" from "could not establish" while BOTH resolve to
/// the same fail-safe reduced state. `EntitlementValidation.resolveFailSafe`
/// has exactly this shape.
type EntitlementStatusSource = unit -> Async<EntitlementStatus * EntitlementRefusal option>

// ─── 492.B — the flag ceiling ─────────────────────────────────────────

[<RequireQualifiedAccess>]
module EntitlementFlagCeiling =

    /// Cap a `FlagEvaluator`'s governed keys at what the entitlement
    /// grants.
    ///
    /// Behaviour, key by key:
    ///
    ///   * **Not governed** — passes through untouched. This includes every
    ///     `EntitlementFloor` key, because `EntitlementGovernance.governs`
    ///     refuses them structurally. An export flag is therefore resolved
    ///     by the ordinary scope walk in every phase, and no entitlement
    ///     state participates in the answer.
    ///   * **Governed and granted** — passes through. The entitlement
    ///     permits the feature; whether it is ON is still the deployment's
    ///     own flag decision.
    ///   * **Governed and not granted** — `false`, whatever the walk said.
    ///
    /// `Variant` flags pass through unchanged: a governed key is a
    /// capability, which is boolean, and a governed `Variant` declaration
    /// is a composition mismatch rather than a gating question. The
    /// preflight `Warning`s about it by name — silently coercing a variant
    /// to a boolean would pick an option nobody declared.
    ///
    /// **Identity.** `decorate status EntitlementGovernance.none evaluator`
    /// returns an evaluator behaviourally identical to `evaluator` — the
    /// governed set is empty, so every branch is the pass-through (GP 11).
    let decorate
        (status: EntitlementStatus)
        (governance: EntitlementGovernance)
        (inner: FlagEvaluator.FlagEvaluator)
        : FlagEvaluator.FlagEvaluator =
        let capped (key: string) =
            EntitlementGovernance.governs key governance
            && not (EntitlementStatus.grants key status)

        let tryEvaluate (key: string) (ctx: AccessContext) : Async<FlagValue option> =
            if capped key then
                async.Return(Some(FlagValue.Bool false))
            else
                inner.TryEvaluate key ctx

        let isEnabled (key: string) (ctx: AccessContext) : Async<bool> =
            if capped key then
                async.Return false
            else
                inner.IsEnabled key ctx

        // Variant resolution is never capped — see the doc comment.
        let resolveVariant (key: string) (ctx: AccessContext) : Async<string> = inner.ResolveVariant key ctx

        let resolve (flag: FeatureFlag) (ctx: AccessContext) : Async<FlagValue> = async {
            match flag.DefaultValue with
            | FlagValue.Bool _ ->
                let! enabled = isEnabled flag.Key ctx
                return FlagValue.Bool enabled
            | FlagValue.Variant(options, _) ->
                let! chosen = resolveVariant flag.Key ctx
                return FlagValue.Variant(options, chosen)
        }

        {
            TryEvaluate = tryEvaluate
            IsEnabled = isEnabled
            ResolveVariant = resolveVariant
            Resolve = resolve
        }

// ─── 492.C — the boot preflight ───────────────────────────────────────

[<RequireQualifiedAccess>]
module EntitlementPreflight =

    /// Registration name. Suffix-free — a deployment holds one entitlement.
    [<Literal>]
    let ValidatorName = "entitlement"

    /// Governance audit findings that are worth a line but are not about
    /// the token at all: a governed key no module declared (a typo, so the
    /// gate never fires), or one declared as a `Variant` (which the ceiling
    /// cannot cap).
    ///
    /// Both are compose-time facts, so they are computed from the declared
    /// flag set rather than probed. They are `Warning`-worthy and never
    /// fatal, in keeping with the rest of this validator.
    let auditGovernance (declared: FeatureFlag seq) (governance: EntitlementGovernance) : string list =
        let declared = declared |> Seq.map (fun f -> f.Key, f) |> Map.ofSeq

        governance.GovernedKeys
        |> Set.toList
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.choose (fun key ->
            match declared.TryFind key with
            | None ->
                Some(
                    sprintf
                        "entitlement governs flag key '%s', which no module declared — the gate will never fire and the capability is effectively ungated. Check for a typo against the declared flag keys."
                        key
                )
            | Some { DefaultValue = FlagValue.Variant _ } ->
                Some(
                    sprintf
                        "entitlement governs flag key '%s', which is declared as a Variant — capabilities are boolean, so the ceiling cannot cap it and this key is effectively ungated. Declare it as a Bool flag, or stop governing it."
                        key
                )
            | Some _ -> None)

    /// The renewal advisory (the revocation story, made visible). Two
    /// separate findings, because they have different remedies: a token
    /// nearing expiry needs renewing, whereas a token with a long lifetime
    /// needs the ISSUER's cadence changing.
    let auditRenewal (policy: RenewalPolicy) (status: EntitlementStatus) : string list =
        match status.Phase with
        | EntitlementPhase.Unentitled -> []
        | _ -> [
            if
                policy.MaxTokenLifetime <> TimeSpan.MaxValue
                && status.Lifetime > policy.MaxTokenLifetime
            then
                yield
                    sprintf
                        "entitlement token is valid for %.0f day(s), longer than this deployment's declared maximum of %.0f. There is no revocation fetch on this path by design, so the token's lifetime IS the revocation latency: a withdrawn entitlement stays effective for that long. Ask the issuer for shorter tokens renewed more often."
                        status.Lifetime.TotalDays
                        policy.MaxTokenLifetime.TotalDays

            match status.Phase with
            | EntitlementPhase.Active daysRemaining when
                policy.RenewalNotice > TimeSpan.Zero
                && daysRemaining <= policy.RenewalNotice.TotalDays
                ->
                yield
                    sprintf
                        "entitlement expires in %.1f day(s), inside this deployment's %.0f-day renewal notice. Renew before expiry to avoid entering the grace window."
                        daysRemaining
                        policy.RenewalNotice.TotalDays
            | _ -> ()
          ]

    /// Assemble the full preflight message for a resolved state. Public so
    /// the test pack asserts on the exact text an operator reads, rather
    /// than on a paraphrase of it.
    let describe
        (declared: FeatureFlag seq)
        (governance: EntitlementGovernance)
        (policy: RenewalPolicy)
        (status: EntitlementStatus)
        (refusal: EntitlementRefusal option)
        : ValidationResult =
        let holder =
            if String.IsNullOrWhiteSpace status.HolderId then
                ""
            else
                sprintf " (holder '%s', token '%s')" status.HolderId status.TokenId

        let governanceFindings = auditGovernance declared governance
        let renewalFindings = auditRenewal policy status

        let refusalLine =
            refusal
            |> Option.map (fun r ->
                sprintf
                    "Entitlement could not be established, so this deployment has REDUCED to read + export only: %s No stored data has been withheld — everything remains readable and fully exportable."
                    (EntitlementRefusal.describe r))

        let lapseLine =
            match status.Phase with
            | EntitlementPhase.Grace _
            | EntitlementPhase.Lapsed _ when refusal.IsNone ->
                Some(sprintf "Entitlement%s: %s" holder (EntitlementPhase.describe status.Phase))
            | _ -> None

        let findings = [
            yield! refusalLine |> Option.toList
            yield! lapseLine |> Option.toList
            yield! governanceFindings
            yield! renewalFindings
        ]

        match findings with
        | [] ->
            // Quiet: an active (or absent) entitlement with nothing to say.
            // `Ok` still logs at Info through the aggregator, which is where
            // days-remaining belongs when nothing is wrong.
            Ok
        | _ ->
            // Warning, NEVER Error. See this file's header: an Error aborts
            // the boot, and a boot that will not start is the most complete
            // way to withhold a customer's own data.
            Warning(String.Join(" ", findings))

    /// The **structural-class** validator that surfaces entitlement state
    /// at boot.
    ///
    /// Structural rather than external-probe because it reads local bytes
    /// and a clock — the cost test that classification turns on — and
    /// therefore runs even under `ServerConfig.SkipPreflight`. An operator
    /// booting through a storage outage should not also lose the line
    /// telling them their entitlement lapsed last week.
    ///
    /// It is deliberately NOT `ISecurityClassValidator`: that marker's
    /// contract is "runs anyway AND still aborts on Error", and this
    /// validator has no `Error` to abort on.
    type EntitlementConfigValidator
        (
            source: EntitlementStatusSource,
            declared: FeatureFlag seq,
            governance: EntitlementGovernance,
            policy: RenewalPolicy
        ) =

        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout

            member _.Validate() = async {
                try
                    let! status, refusal = source ()
                    return describe declared governance policy status refusal
                with ex ->
                    // Even a raising source resolves to a Warning. The
                    // aggregator would otherwise convert the throw to an
                    // Error itself and abort the boot — the exact hole
                    // Phase 488.A's decorator had to close, and the same
                    // remedy.
                    return
                        Warning(
                            sprintf
                                "Entitlement state could not be read (%s: %s), so this deployment has REDUCED to read + export only. No stored data has been withheld."
                                (ex.GetType().Name)
                                ex.Message
                        )
            }

        interface IStructuralClassValidator

// ─── Composition ──────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module EntitlementCompose =

    /// Register the boot preflight. **Calling this IS the opt-in** (GP 13)
    /// — a deployment that gates nothing never calls it, registers no
    /// validator, and composes a byte-for-byte identical `services`. There
    /// is no `ServerConfig` field, for the reason Phase 434 and Phase 488
    /// both give: a field is a breaking constructor change and a public-API
    /// baseline removal, for a posture only a handful of deployments hold.
    let serviceRegistration
        (source: EntitlementStatusSource)
        (declared: FeatureFlag seq)
        (governance: EntitlementGovernance)
        (policy: RenewalPolicy)
        : IServiceCollection -> IServiceCollection =
        fun services ->
            services.AddSingleton<IConfigValidator>(
                EntitlementPreflight.EntitlementConfigValidator(source, declared, governance, policy)
                :> IConfigValidator
            )
            |> ignore

            services

    /// The whole wiring for a deployment that holds a token: resolve the
    /// state once, cap the evaluator, expose the budget, and register the
    /// preflight.
    ///
    /// Resolving ONCE at compose is deliberate. A per-request resolve would
    /// re-verify a signature on the hot path for a value that changes at
    /// most once a quarter, and — worse — would let a deployment slide from
    /// Active into Lapsed mid-process with no operator ever seeing the
    /// preflight line that says so. Renewal is a restart, which is what an
    /// operator who has just installed a new token expects to do anyway.
    let resolveAndCap
        (validation: EntitlementValidation)
        (token: EntitlementToken option)
        (declared: FeatureFlag seq)
        (evaluator: FlagEvaluator.FlagEvaluator)
        : Async<FlagEvaluator.FlagEvaluator * EntitlementBudget * (IServiceCollection -> IServiceCollection)> =
        async {
            let! status, refusal = EntitlementValidation.resolveFailSafe validation token

            let capped = EntitlementFlagCeiling.decorate status validation.Governance evaluator

            let budget = EntitlementBudget.ofStatus status
            let source: EntitlementStatusSource = fun () -> async.Return(status, refusal)

            let registration =
                serviceRegistration source declared validation.Governance validation.Renewal

            return capped, budget, registration
        }