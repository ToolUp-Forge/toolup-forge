// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 691 — the seam gate's first production call site ───────────
//
// Phase 300 built a composition-capability gate, Phase 657 made it
// mandatory under the verified profile, and Phase 688 added the
// seam-granularity half. Phase 688's own recorded finding was that
// `ICompositionCapabilityGate.Check` and `ISeamAuthorityGate.CheckSeam`
// had **zero production call sites**: every caller was a test. The
// authority story guarded a decision point nothing in a composition
// routed through, which is the failure mode 657's header warns about
// one level up — enforcement that is believed rather than exercised.
//
// This module is the decision point. It answers, for a real composition:
// *which seams does each composed module reach, and is it permitted to?*
//
// **The reach is DERIVED, not hand-listed.** Phase 438/554's
// `ModuleSurface` already computes the substrate a module's own
// registrations IMPLY — a module that declares an `AITool` needs an
// `IAIProvider` by construction, one that declares `JobHandlers` needs
// an `IJobScheduler`, one that declares nothing needs nothing. That
// projection is shipped, tested, and keyed off the registration fields
// themselves (`nameof`-checked, with `Unclassified` / `Stale` reporting
// when the record drifts). Reading it here means there is exactly ONE
// declaration→substrate map in the SDK rather than a second one that
// silently disagrees with the first. `SeamId.ofInterface` and
// `ComponentId.forCompanionSlot` are the same id space, so the
// projection lands in the Phase 688 vocabulary with no translation.
//
// **What this does NOT claim.** `ModuleSurface` reports its own blind
// spots rather than guessing at them: a Giraffe `HttpHandler` is a
// closure whose routes are not enumerable, and no `ServerModule` field
// declares the `(TargetModule, QueryKey)` pairs a module ASKS for. So a
// refusal here is sound (every seam named is genuinely reached) but the
// admission is a subset claim, not a proof of confinement — a module can
// still reach substrate it resolved from the `IServiceProvider` by hand.
// Stating that is the point: the gate reports what it can observe, and
// an enforcement layer that overstated its coverage would be worse than
// one that does not exist, because it would be believed.
//
// **The additive floor is structural.** A composition with no
// `SeamGrantSignature` resolves every component to
// `SeamGrant.UnrestrictedSeams`, so every reached seam is granted and
// this returns `Ok` for every module shape — byte-for-byte the pre-688
// and pre-300 posture (GP 11). A deployment that never calls `verify`
// derives no reach and pays nothing (GP 13).
//
// **Deliberately a function, not a `ServerConfig` knob or a
// `ServerApp` field.** Either would retype a record every consumer
// builds, for a feature no existing consumer configures — the same
// rationale `ServerApp.verifyComposition` and `BootVerificationOptions`
// already record. The modules a composition adds are the modules the
// consumer already holds, so nothing has to be threaded anywhere.

/// The seams one composed module reaches, as data.
///
/// `ReachComponent` is the module's stable Phase 279 id — its declared
/// `ComponentId` when it has one, else the `Name`-derived default — so
/// it is the same key the `CapabilitySignature` and the
/// `SeamGrantSignature` are already keyed by.
type ComponentSeamReach = {
    /// The module's stable Phase 279 id.
    ReachComponent: ComponentId
    /// The substrate seams its registrations imply, deduplicated and in
    /// the `ModuleSurface` ordering (so a report is deterministic).
    ReachedSeams: SeamId list
}

/// Why a composition failed seam-authority verification. Two shapes,
/// because they have different remedies and different blast radii: the
/// profile could not be bound at all, or it was bound and some component
/// reached past its declaration.
[<RequireQualifiedAccess>]
type SeamAuthorityRefusal =
    /// The composition could not produce a gate under the profile it
    /// declared — the Phase 657 / 688 profile refusal, verbatim. Nothing
    /// was checked, because there was nothing to check against.
    | Profile of CompositionProfileRefusal
    /// The gate was built and refused one or more reaches. Every denial
    /// has already been handed to the gate's observer (and so, under
    /// `verifyAudited`, is already on the Phase 658 ledger) — this list
    /// is the caller's copy, not the audit path.
    | Reaches of CapabilityDenial list

[<RequireQualifiedAccess>]
module SeamAuthorityEnforcement =

    /// The `ModuleSurfaceEntry.Kind` that names an implied substrate
    /// seam. A literal rather than a magic string at the filter, so a
    /// rename on the projection side is one edit.
    [<Literal>]
    let private SubstrateKind = "substrate"

    /// The seams one module's registrations imply.
    ///
    /// Reads `ModuleSurface.describe`'s `Needs` — the shipped
    /// declaration→substrate projection — and keeps the `substrate`
    /// entries, whose `Key` is a companion interface name and therefore
    /// already a `SeamId` in the Phase 688 id space.
    let reachOf (serverModule: ServerModule) : ComponentSeamReach =
        let surface = ModuleSurface.describe serverModule

        {
            ReachComponent = surface.Component
            ReachedSeams =
                surface.Needs
                |> List.filter (fun entry -> entry.Kind = SubstrateKind)
                |> List.map (fun entry -> SeamId.ofInterface entry.Key)
                |> List.distinct
        }

    /// The reach of a whole composition, in the order the modules were
    /// declared — so a refusal report reads in the order the composition
    /// root does.
    let reach (modules: ServerModule list) : ComponentSeamReach list = modules |> List.map reachOf

    /// The capability the seam check requires.
    ///
    /// `CompanionCapability.identity` — the lattice bottom — and that is
    /// load-bearing rather than a placeholder. `ISeamAuthorityGate.
    /// CheckSeam` runs the Phase 300 EFFECT check first and only then the
    /// seam set, so passing anything above the bottom here would make a
    /// seam question fail on the effect axis and report an effect denial
    /// for a reach the component was entitled to. The effect envelope is
    /// Phase 300's own question, asked at Phase 300's own call sites;
    /// this call site asks only "may this component reach this seam".
    let private seamOnly = CompanionCapability.identity

    /// **The call site.** Check every seam every composed module reaches
    /// against the gate, and collect every refusal.
    ///
    /// Not short-circuiting: an operator fixing a composition wants the
    /// whole list, not the first entry, and the gate has already observed
    /// each denial by the time it is returned (so stopping early would
    /// also under-report the audit trail). The result is deterministic —
    /// modules in declaration order, seams in `ModuleSurface` order.
    ///
    /// `Ok ()` under any gate that grants every seam, which is every gate
    /// a composition with no `SeamGrantSignature` can produce — the
    /// additive floor (GP 11).
    let verify (gate: ISeamAuthorityGate) (modules: ServerModule list) : Result<unit, CapabilityDenial list> =
        let denials = [
            for entry in reach modules do
                for seam in entry.ReachedSeams do
                    match gate.CheckSeam entry.ReachComponent seam seamOnly with
                    | CapabilityGateDecision.Denied denial -> denial
                    | CapabilityGateDecision.Granted -> ()
        ]

        if List.isEmpty denials then Ok() else Error denials

    /// The one call a verified composition makes: build the profile's
    /// seam gate with its refusals already on the audit path, then check
    /// the composition's reach through it.
    ///
    /// Mirrors `VerifiedCompositionProfile.auditedSeamGate`'s framing one
    /// level up — under `CompositionProfile.Standard` with nothing
    /// declared the gate is `SeamAuthorityGate.disabled` and this is
    /// unconditionally `Ok`; under `CompositionProfile.Verified` an
    /// undeclared envelope or a half-declared grant signature is refused
    /// before any reach is checked, because a mandatory check with
    /// nothing to check against would admit everything while presenting
    /// as enforcement.
    let verifyAudited
        (auditLog: IAuditLog)
        (scopeId: string)
        (profile: CompositionProfile)
        (signature: CapabilitySignature option)
        (grants: SeamGrantSignature option)
        (modules: ServerModule list)
        : Result<unit, SeamAuthorityRefusal> =
        match VerifiedCompositionProfile.auditedSeamGate auditLog scopeId profile signature grants with
        | Error refusal -> Error(SeamAuthorityRefusal.Profile refusal)
        | Ok gate ->
            match verify gate modules with
            | Ok() -> Ok()
            | Error denials -> Error(SeamAuthorityRefusal.Reaches denials)

    /// Human-readable account of a refusal — what a composition root
    /// logs, and what a preflight report renders. Each reach refusal is
    /// already component- and seam-named by
    /// `SeamAuthorityGate.refusalReason`, so this only joins them.
    let describeRefusal (refusal: SeamAuthorityRefusal) : string =
        match refusal with
        | SeamAuthorityRefusal.Profile profileRefusal -> CompositionProfileRefusal.describe profileRefusal
        | SeamAuthorityRefusal.Reaches denials ->
            denials |> List.map _.Reason |> String.concat System.Environment.NewLine