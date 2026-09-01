// Ambient context for `docs/platform/offline-entitlement.md`.
//
// The page teaches a mechanism that lives in a composition root, so most
// of its blocks are one line out of a program it never shows in full:
// the pinned key and the governance record declared in "The shape", the
// verifier a deployment adapts at its own call site, the token it holds,
// the declared flag set and the evaluator the ceiling wraps, and the
// `IServiceCollection` the preflight registers into. None of those are
// SDK types — they are exactly what the page's own deployment provides —
// so they are declared once here rather than restated in every block.
//
// The two `renderAdvancedPanel` / `renderUpgradePrompt` stubs stand in
// for the module's own view layer, which the page deliberately says
// nothing about: its point is that gated code sees a flag and nothing
// entitlement-shaped at all.
open System.Security.Cryptography
open Microsoft.Extensions.DependencyInjection

[<AutoOpen>]
module PageAmbient =

    /// Whatever the gated module's own view layer produces. Page-local
    /// on purpose — the page never says, and never needs to.
    type ReportPanel = ReportPanel of string

    /// The claim set built in "The shape", as the issuing-side blocks
    /// read it back.
    let claims: EntitlementClaims = failwith "ambient"

    /// The key this deployment pins, and the governed key set — both
    /// declared in "The shape" and consumed by every later block.
    let pin: PinnedEntitlementKey = failwith "ambient"

    let governance: EntitlementGovernance = failwith "ambient"

    /// The deployment's own ES256 public key, held by the verifier it
    /// composes. This is what makes the pin real (see the page).
    let publicKey: ECDsa = failwith "ambient"

    /// The verifier and the validation config the "Clock skew" block
    /// rebuilds with an appliance's declared drift.
    let verify: VerifyDetachedJws = failwith "ambient"

    let validation: EntitlementValidation = failwith "ambient"

    /// The appliance profile the skew bridge reads. A value read, not a
    /// dependency — entitlements are not appliance-only.
    let profile: ApplianceProfile = failwith "ambient"

    /// The token this host was provisioned with, if any.
    let presentedToken: EntitlementToken option = failwith "ambient"

    /// The flag set the deployment's modules declared, and the evaluator
    /// the entitlement ceiling composes over.
    let declaredFlags: FeatureFlag list = failwith "ambient"

    let evaluator: FlagEvaluator.FlagEvaluator = failwith "ambient"

    /// What a gated module holds: the capped evaluator and the request's
    /// access context. It never sees a token, a claim set, or a phase.
    let flags: FlagEvaluator.FlagEvaluator = failwith "ambient"

    let ctx: AccessContext = failwith "ambient"

    let renderAdvancedPanel () : ReportPanel = failwith "ambient"

    let renderUpgradePrompt () : ReportPanel = failwith "ambient"

    /// The capacity read surface and the seat count a caller is asking
    /// for, plus the two outcomes the calling code already has.
    let budget: EntitlementBudget = failwith "ambient"

    let requestedSeats: int64 = failwith "ambient"

    let proceed () : unit = failwith "ambient"

    let refuse (breach: Usage.QuotaBreached) : unit = failwith "ambient"

    /// The preflight registration `resolveAndCap` hands back, and the DI
    /// collection the composition root is assembling.
    let registerPreflight: IServiceCollection -> IServiceCollection = failwith "ambient"

    let services: IServiceCollection = failwith "ambient"

    /// The issuing side's own signature material.
    let keyId: string = failwith "ambient"

    let algorithm: string = failwith "ambient"

    let detachedJws: string = failwith "ambient"