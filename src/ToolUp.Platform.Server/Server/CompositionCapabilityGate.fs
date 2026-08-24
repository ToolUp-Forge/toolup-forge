// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 300 — composition capability sandbox (runtime default-deny) ──
//
// Enforces the Phase 296 effect-join AT RUNTIME: a composed component may
// only exercise the capabilities its composition declared (Phase 282's
// `CompanionCapability`, resolved from a Phase 296 `CapabilitySignature`
// keyed by the Phase 279 `ComponentId`) — **default-deny** anything beyond
// it. The security property that turns "we reviewed the AI-emitted app" into
// "the app physically cannot touch a capability it didn't declare, by
// construction" (GP 4 — structural, not a runtime "remember to check").
//
// Generalises the Phase 266 authorizer-gated `IHostCapabilityRegistry.Invoke`
// from the hosted-view surface to the WHOLE composition: Phase 266 gated a
// named host-capability invoke through the Phase 113 tenant/tier authorizer;
// this gate adds the composition-level effect-envelope check IN FRONT of it
// (see `guardInvoke` below), so a capability invocation clears BOTH the
// declared effect envelope (300) AND the tenant/tier authorizer (266).
//
// **Opt-in, default off (GP 11 / GP 13).** The default is `disabled` — a
// byte-for-byte passthrough that grants every check (a deployment that never
// composes an enabled gate is unchanged and pays nothing). A deployment opts
// in by composing `create` over its `CapabilitySignature`; from then on an
// undeclared capability use fails closed with a readable, component-named
// error, and every deny is observable (the `onDeny` observer — never
// silent). An undeclared component is absent from the signature and resolves
// to `CompanionCapability.identity` ("pure"), so any effecting / hidden-read
// invocation it attempts is denied by construction (default-deny).
//
// **Dominance is the Phase 296 lattice order.** A required capability is
// permitted iff it sits at or below the component's declared capability in
// the join-semilattice — i.e. `join declared required = declared` (joining
// the requirement in adds nothing). Effecting beyond a `Pure` declaration, a
// determinism factor the declaration didn't list, or dev-only beyond a
// distributed-ready declaration each push the join above `declared` and are
// denied.

/// A refused capability access: the component that attempted it, what it
/// required, what it had declared, and a readable, component-named reason.
/// The security-relevant record — carried on the deny outcome and handed to
/// the observer so a deny is always visible (logged / audited), never
/// silent.
type CapabilityDenial = {
    /// The composing component that attempted the access (Phase 279).
    Component: ComponentId
    /// The capability the attempted operation required.
    Required: CompanionCapability
    /// The capability the component declared (its envelope) — the identity
    /// ("pure") for an undeclared component.
    Declared: CompanionCapability
    /// Human-readable explanation naming the component + the exceeded axes.
    Reason: string
}

/// The decision of a `ICompositionCapabilityGate.Check`: the access is within
/// the component's declared envelope (`CapabilityGranted`) or it is refused
/// (`CapabilityDenied`, fail-closed). Every non-grant path is a `Denied`
/// (default-deny), never an exception.
[<RequireQualifiedAccess>]
type CapabilityGateDecision =
    /// The required capability sits at or below the component's declared
    /// envelope — the operation may proceed.
    | Granted
    /// The required capability exceeds the declared envelope — refused, with
    /// the reason on the `CapabilityDenial`.
    | Denied of CapabilityDenial

/// Runtime composition-capability sandbox: resolves a component's declared
/// capability (Phase 282/296) and refuses, by default, any invocation that
/// exceeds it (Phase 300). The composition-level generalisation of the Phase
/// 266 per-invoke authorizer gate. A pipeline that composes no enabled gate
/// uses `CompositionCapabilityGate.disabled` (grant-all passthrough) and is
/// byte-for-byte unchanged (GP 11/13).
type ICompositionCapabilityGate =
    /// Whether `componentId` may exercise a capability requiring `required`.
    /// Granted iff `required` is at or below the component's declared
    /// envelope in the Phase 296 lattice; otherwise `Denied` (fail-closed),
    /// with the deny surfaced to the gate's observer.
    abstract Check: componentId: ComponentId -> required: CompanionCapability -> CapabilityGateDecision

[<RequireQualifiedAccess>]
module CompositionCapabilityGate =

    /// Dominance in the Phase 296 join-semilattice: `required` is permitted
    /// by `declared` iff joining the requirement into the declaration adds
    /// nothing (`join declared required = declared`) — i.e. `required` sits
    /// at or below `declared` on every axis (effect / determinism factors /
    /// readiness).
    let permits (declared: CompanionCapability) (required: CompanionCapability) : bool =
        CompanionCapability.join declared required = declared

    /// The axes on which `required` exceeds `declared` — the human-readable
    /// list a denial reason names. Empty exactly when `permits declared
    /// required`.
    let private exceededAxes (declared: CompanionCapability) (required: CompanionCapability) : string list = [
        if required.Effect = Effecting && declared.Effect = Pure then
            "effect (Effecting beyond a Pure declaration)"

        let extraFactors =
            Set.difference
                (DeterminismSource.factors required.Determinism)
                (DeterminismSource.factors declared.Determinism)

        if not (Set.isEmpty extraFactors) then
            let listing =
                extraFactors
                |> Set.toList
                |> List.map DeterminismFactor.toWireString
                |> String.concat ", "

            sprintf "determinism (undeclared factor(s): %s)" listing

        if required.Readiness = DevOnly && declared.Readiness = DistributedReady then
            "readiness (DevOnly beyond a DistributedReady declaration)"
    ]

    /// Build the readable, component-named denial reason.
    let private denialReason
        (componentId: ComponentId)
        (declared: CompanionCapability)
        (required: CompanionCapability)
        : string =
        let axes = exceededAxes declared required |> String.concat "; "

        sprintf
            "composition capability sandbox: component '%s' attempted a capability beyond its declared envelope — %s. Declare the capability on the component's CompanionCapability (Phase 282) to permit it, or the access stays denied (default-deny)."
            (ComponentId.value componentId)
            axes

    /// The off state (GP 11/13): a passthrough that grants every check. The
    /// default a deployment that never opts into the sandbox uses —
    /// byte-for-byte unchanged, no signature consulted, nothing observed.
    let disabled: ICompositionCapabilityGate =
        { new ICompositionCapabilityGate with
            member _.Check _ _ = CapabilityGateDecision.Granted
        }

    /// The enabled sandbox over a Phase 296 `CapabilitySignature`. Each
    /// `Check` resolves the component's declared capability (undeclared →
    /// `CompanionCapability.identity`, so an undeclared component is denied
    /// any effecting / hidden-read access — default-deny) and grants only
    /// when the requirement sits at or below it. Every deny is handed to
    /// `onDeny` (logged / audited — never silent) AND returned fail-closed.
    let create (onDeny: CapabilityDenial -> unit) (signature: CapabilitySignature) : ICompositionCapabilityGate =
        { new ICompositionCapabilityGate with
            member _.Check componentId required =
                let declared = CompanionCapability.resolve signature componentId

                if permits declared required then
                    CapabilityGateDecision.Granted
                else
                    let denial = {
                        Component = componentId
                        Required = required
                        Declared = declared
                        Reason = denialReason componentId declared required
                    }

                    onDeny denial
                    CapabilityGateDecision.Denied denial
        }

    /// Bridge to the Phase 266 authorizer-gated registry: enforce the
    /// composition effect-envelope (300) IN FRONT of the tenant/tier
    /// authorizer (266). A capability invocation clears BOTH gates —
    /// `owner`'s declared envelope first (denied → a `HostCapabilityOutcome.
    /// Denied` carrying the sandbox reason, the registry never reached), then
    /// the registry's own default-deny authorizer. This is how Phase 266's
    /// per-invoke gate generalises to the whole composition.
    let guardInvoke
        (gate: ICompositionCapabilityGate)
        (owner: ComponentId)
        (required: CompanionCapability)
        (registry: IHostCapabilityRegistry)
        (capability: CapabilityId)
        (args: HostCapabilityArgs)
        (ctx: AccessContext)
        : Async<HostCapabilityOutcome> =
        async {
            match gate.Check owner required with
            | CapabilityGateDecision.Denied denial -> return HostCapabilityOutcome.Denied denial.Reason
            | CapabilityGateDecision.Granted -> return! registry.Invoke capability args ctx
        }

// ─── Phase 688 — seam-granularity module authority grants ─────────────
//
// `ICompositionCapabilityGate.Check` above is the EFFECT half: "may this
// component do effecting work at all", over a two-point lattice. A
// component that clears it may then resolve every seam the composition
// will hand it, so "review the grants, not the code" holds for what a
// module EXPOSES (Phase 438/554) and not for what it REACHES.
//
// The seam gate closes that. It INHERITS the Phase 300 interface rather
// than growing it: adding a member to a shipped F# interface is a source
// break for every implementer, and F# cannot author a default
// implementation to soften it. Inheriting costs nothing — an
// `ISeamAuthorityGate` IS an `ICompositionCapabilityGate`, so it drops
// into every hole the shipped one fits, and no existing implementation
// changes.
//
// **The refusal reuses `CapabilityDenial` deliberately.** The Phase 657
// `auditingObserver` already turns one into an
// `AuditEvent.CompositionCapabilityRefused`, which the Phase 658
// hash-chained ledger already carries. A second denial record would have
// meant a second audit event, a second observer, and a second thing to
// remember to compose — for a refusal that belongs on exactly the same
// path. The seam is named in the `Reason`, which is the field the audit
// payload renders.

/// A capability gate that additionally holds each component to its
/// declared **reachable-seam set** (Phase 688). Inherits
/// `ICompositionCapabilityGate` rather than growing it, so every existing
/// implementation and every existing consumer of the Phase 300 interface
/// is untouched — a seam gate drops into any hole an
/// `ICompositionCapabilityGate` fits, and a composition that never asks
/// for one is byte-for-byte unchanged (GP 11/13).
type ISeamAuthorityGate =
    inherit ICompositionCapabilityGate

    /// Whether `componentId` may resolve `seam` while requiring
    /// `required`. Both halves must clear: the Phase 300 effect envelope
    /// FIRST (so an existing denial still reads as an effect denial, with
    /// its existing reason), then the Phase 688 seam set. Any non-grant is
    /// a `Denied` carrying a `CapabilityDenial` — never an exception, and
    /// never silent.
    abstract CheckSeam:
        componentId: ComponentId -> seam: SeamId -> required: CompanionCapability -> CapabilityGateDecision

[<RequireQualifiedAccess>]
module SeamAuthorityGate =

    /// Whether `seam` may be resolved under `declared` — `UnrestrictedSeams`
    /// permits every seam (the undeclared, pre-688 posture); a declared set
    /// permits exactly its members, and `DeclaredSeams Set.empty` permits
    /// none. The seam analogue of `CompositionCapabilityGate.permits`.
    let permitsSeam (declared: SeamGrant) (seam: SeamId) : bool = SeamGrant.permits declared seam

    /// The readable, component-named seam-refusal reason. Names the seam
    /// that was refused AND the set the component did declare, so the
    /// remedy is in the message rather than in the source — the same shape
    /// as the Phase 300 effect denial, and the string the Phase 657 audit
    /// payload carries onto the Phase 658 ledger.
    let refusalReason (componentId: ComponentId) (declared: SeamGrant) (seam: SeamId) : string =
        sprintf
            "composition seam authority: component '%s' attempted to resolve seam '%s', which is outside its declared reachable-seam set %s. Add the seam to the component's SeamGrant (Phase 688) to permit it, or the resolution stays refused (default-deny)."
            (ComponentId.value componentId)
            (SeamId.value seam)
            (SeamGrant.render declared)

    /// Lift any Phase 300 gate to a seam gate that grants every seam —
    /// the additive floor. A composition with no `SeamGrantSignature`
    /// resolves exactly the decisions its underlying gate resolved, so
    /// lifting `CompositionCapabilityGate.disabled` grants everything and
    /// lifting an enabled gate changes nothing about its effect checks.
    let unrestricted (inner: ICompositionCapabilityGate) : ISeamAuthorityGate =
        { new ISeamAuthorityGate with
            member _.Check componentId required = inner.Check componentId required
            member _.CheckSeam componentId _seam required = inner.Check componentId required
        }

    /// The off state: grants every check and every seam. What a
    /// deployment that composes nothing uses — byte-for-byte the pre-688
    /// and pre-300 posture.
    let disabled: ISeamAuthorityGate = unrestricted CompositionCapabilityGate.disabled

    /// The enabled seam gate over a Phase 296 `CapabilitySignature` and a
    /// Phase 688 `SeamGrantSignature`.
    ///
    /// `Check` is the underlying Phase 300 gate, unchanged — the effect
    /// envelope is not re-derived here, so a deployment that adds seam
    /// grants keeps exactly the effect decisions it had. `CheckSeam` runs
    /// that check first and only then the seam set, so a component that
    /// fails both is reported against the axis it was already failing.
    /// A component absent from the grant signature resolves to
    /// `UnrestrictedSeams` and every seam resolution it makes is granted
    /// (GP 11) — the verified profile, not this constructor, is what makes
    /// declaration mandatory.
    let create
        (onDeny: CapabilityDenial -> unit)
        (signature: CapabilitySignature)
        (grants: SeamGrantSignature)
        : ISeamAuthorityGate =
        let inner = CompositionCapabilityGate.create onDeny signature

        { new ISeamAuthorityGate with
            member _.Check componentId required = inner.Check componentId required

            member _.CheckSeam componentId seam required =
                match inner.Check componentId required with
                | CapabilityGateDecision.Denied denial -> CapabilityGateDecision.Denied denial
                | CapabilityGateDecision.Granted ->
                    let declared = SeamGrant.resolve grants componentId

                    if SeamGrant.permits declared seam then
                        CapabilityGateDecision.Granted
                    else
                        let denial = {
                            Component = componentId
                            Required = required
                            Declared = CompanionCapability.resolve signature componentId
                            Reason = refusalReason componentId declared seam
                        }

                        onDeny denial
                        CapabilityGateDecision.Denied denial
        }

    /// **The seam-resolution choke point.** Resolve a seam through the
    /// gate: the factory runs only when the component is permitted to
    /// reach it, and a refusal returns the typed, already-observed
    /// `CapabilityDenial` rather than throwing. Fail-closed by
    /// construction — there is no path through this function that
    /// produces a `'T` without a grant.
    ///
    /// A `Result` rather than an option because the caller almost always
    /// needs the reason: it is what a composition root logs, what a
    /// preflight reports, and what the Phase 657 audit payload already
    /// knows how to render.
    let resolveSeam
        (gate: ISeamAuthorityGate)
        (owner: ComponentId)
        (seam: SeamId)
        (required: CompanionCapability)
        (resolve: unit -> 'T)
        : Result<'T, CapabilityDenial> =
        match gate.CheckSeam owner seam required with
        | CapabilityGateDecision.Denied denial -> Error denial
        | CapabilityGateDecision.Granted -> Ok(resolve ())

    /// The Phase 266 registry bridge with the seam named — `guardInvoke`
    /// plus the Phase 688 check. A capability invocation now clears THREE
    /// gates in order: the declared effect envelope (300), the declared
    /// seam set (688), then the registry's own default-deny authorizer
    /// (266). The registry is never reached when either composition-level
    /// gate refuses.
    let guardSeamInvoke
        (gate: ISeamAuthorityGate)
        (owner: ComponentId)
        (seam: SeamId)
        (required: CompanionCapability)
        (registry: IHostCapabilityRegistry)
        (capability: CapabilityId)
        (args: HostCapabilityArgs)
        (ctx: AccessContext)
        : Async<HostCapabilityOutcome> =
        async {
            match gate.CheckSeam owner seam required with
            | CapabilityGateDecision.Denied denial -> return HostCapabilityOutcome.Denied denial.Reason
            | CapabilityGateDecision.Granted -> return! registry.Invoke capability args ctx
        }