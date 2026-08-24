// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 282 — typed companion capability descriptors ───────────────
//
// A companion's effect / determinism / distributed-readiness posture is
// today file-header prose ("dev-only vs distributed-ready") and the
// six-portability-rules document — reviewer-enforced, never
// machine-checkable. This makes it a typed, queryable *value*: the
// `CompanionCapability` a companion can declare, keyed against the same
// stable `ComponentId` (Phase 279) the manifest (Phase 280) and preflight
// (Phase 281) correlate against.
//
// Three orthogonal axes, each a small join-semilattice so a composition's
// posture is the componentwise join of its parts (Phase 296):
//   • Effect       — does invoking it write to the world?  (Pure ⊑ Effecting)
//   • Determinism  — does its *result* read hidden inputs?  (∅ ⊑ {factors…})
//   • Readiness     — safe for a distributed deployment?     (DistributedReady ⊑ DevOnly)
//
// **Conservative default preserves today's behaviour (GP 11).** The bottom
// of every axis — pure, deterministic, distributed-ready — is the join
// IDENTITY. An UNDECLARED companion contributes the identity, so a
// composition of undeclared companions joins to the identity ("pure") and a
// pre-282 deployment is byte-for-byte unchanged until a companion opts in to
// declaring its real posture. "Conservative" here means least-disruptive,
// not assume-the-worst: the declaration is the opt-in, absence is the
// no-op.
//
// **Generic substrate (GP 1) + zero cost when unread (GP 13).** The type
// carries no vendor / domain type — only three DUs and a set of atomic
// factors — and is a pure value: a deployment that never reads it builds
// nothing and pays nothing. It makes the existing six-portability-rules
// posture (GP 12) queryable instead of prose-only; it is read by the
// manifest + preflight, never enforced as a hard gate here (Phase 300 is
// the opt-in runtime gate).

/// Whether invoking a companion causes a side effect on the outside world
/// (a write). The join is `Pure ⊔ Effecting = Effecting` — an effecting
/// part makes the whole effecting.
type EffectClass =
    /// No outward side effect: a pure function of its inputs (given its
    /// determinism sources). The join identity.
    | Pure
    /// Writes to the world — network, disk, database, clipboard, an
    /// external service. Contaminates a composition to `Effecting`.
    | Effecting

/// An atomic source of nondeterminism a companion's *result* may depend on
/// beyond its explicit inputs. Composed by set union (Phase 296), so
/// `{clock} ⊔ {random} = {clock; random}`.
type DeterminismFactor =
    /// Reads wall-clock / monotonic time.
    | ClockFactor
    /// Reads a randomness source (RNG, GUID generation, sampling).
    | RandomFactor
    /// Reads mutable external state — network, disk, a database, env vars.
    | ExternalStateFactor

/// What a companion's output depends on beyond its explicit inputs. Bottom
/// is `Deterministic` (a pure function of its inputs — the join identity);
/// otherwise the set of nondeterminism factors it draws on. Normalised so
/// the empty factor set is always `Deterministic` (never
/// `Nondeterministic Set.empty`), keeping structural equality total.
type DeterminismSource =
    /// A pure function of its inputs — no hidden reads. The join identity.
    | Deterministic
    /// Draws on one or more nondeterminism factors. Never carries the empty
    /// set — construct through `DeterminismSource.ofFactors`, which folds
    /// the empty set back to `Deterministic`.
    | Nondeterministic of Set<DeterminismFactor>

/// Whether a companion is safe to run in a distributed deployment (stateless
/// between invocations, portability-rule-4-clean) or is a documented
/// dev-only exception (in-memory state, single-instance assumptions). The
/// join is `DistributedReady ⊔ DevOnly = DevOnly` — one dev-only part makes
/// the whole composition dev-only.
type Readiness =
    /// Stateless between invocations; a distributed task framework can host
    /// it (portability rule 4). The join identity.
    | DistributedReady
    /// A documented dev-only exception — holds in-memory state or assumes a
    /// single instance (e.g. the in-process reference implementations).
    /// Contaminates a composition to `DevOnly`.
    | DevOnly

/// A companion's declared effect / determinism / distributed-readiness
/// posture — the machine-queryable form of what was file-header prose. Each
/// axis is a join-semilattice with the "bottom" (pure / deterministic /
/// distributed-ready) as identity, so a composition's posture is the
/// componentwise join of its companions' capabilities (Phase 296). An
/// undeclared companion contributes `CompanionCapability.identity`.
type CompanionCapability = {
    Effect: EffectClass
    Determinism: DeterminismSource
    Readiness: Readiness
}

[<RequireQualifiedAccess>]
module DeterminismFactor =

    /// A stable, human-readable token for a factor — for manifest / preflight
    /// rendering and error messages. Never positional; matches the DU case.
    let toWireString (factor: DeterminismFactor) : string =
        match factor with
        | ClockFactor -> "clock"
        | RandomFactor -> "random"
        | ExternalStateFactor -> "external-state"

[<RequireQualifiedAccess>]
module DeterminismSource =

    /// The nondeterminism factors this source draws on — empty for
    /// `Deterministic`.
    let factors (source: DeterminismSource) : Set<DeterminismFactor> =
        match source with
        | Deterministic -> Set.empty
        | Nondeterministic fs -> fs

    /// Build a `DeterminismSource` from a factor set, normalising the empty
    /// set to `Deterministic` so equality is total (no
    /// `Nondeterministic Set.empty` alias for the bottom).
    let ofFactors (fs: Set<DeterminismFactor>) : DeterminismSource =
        if Set.isEmpty fs then
            Deterministic
        else
            Nondeterministic fs

    /// A source drawing on exactly the one factor.
    let ofFactor (factor: DeterminismFactor) : DeterminismSource = Nondeterministic(Set.singleton factor)

    /// Draws on the wall clock.
    let clock: DeterminismSource = ofFactor ClockFactor

    /// Draws on a randomness source.
    let random: DeterminismSource = ofFactor RandomFactor

    /// Draws on mutable external state (network / disk / db / env).
    let externalState: DeterminismSource = ofFactor ExternalStateFactor

    /// Componentwise join (Phase 296): the set union of the two sources'
    /// factors. `Deterministic ⊔ x = x` (the empty set is the identity);
    /// `clock ⊔ random = {clock; random}`. Associative + commutative +
    /// idempotent by the semilattice laws of set union.
    let join (a: DeterminismSource) (b: DeterminismSource) : DeterminismSource =
        ofFactors (Set.union (factors a) (factors b))

// ─── Phase 296 — the effect-join axis operators ───────────────────────
//
// Each axis is a two- (or set-) element join-semilattice; the composition's
// posture is the componentwise join of its parts. The three axis joins
// below feed `CompanionCapability.join`.

[<RequireQualifiedAccess>]
module EffectClass =

    /// Componentwise join: `Pure ⊔ Effecting = Effecting`. One effecting
    /// part makes the whole effecting. `Pure` is the identity.
    let join (a: EffectClass) (b: EffectClass) : EffectClass =
        match a, b with
        | Pure, Pure -> Pure
        | _ -> Effecting

[<RequireQualifiedAccess>]
module Readiness =

    /// Componentwise join: `DistributedReady ⊔ DevOnly = DevOnly`. One
    /// dev-only part makes the whole composition dev-only.
    /// `DistributedReady` is the identity.
    let join (a: Readiness) (b: Readiness) : Readiness =
        match a, b with
        | DistributedReady, DistributedReady -> DistributedReady
        | _ -> DevOnly

/// An external **effect signature** an authoring / supply-chain tool joins
/// componentwise: every composed unit's declared `CompanionCapability` keyed
/// by its stable `ComponentId` (the same key space the Phase 280 manifest
/// enumerates). `CompanionCapability.composedEffect` folds it to the whole
/// composition's effect class — the static "what can this composition touch?"
/// property. An absent id contributes the conservative default (the
/// identity), so an undeclared companion never changes the join (GP 11).
type CapabilitySignature = Map<ComponentId, CompanionCapability>

[<RequireQualifiedAccess>]
module CompanionCapability =

    /// The join identity: pure, deterministic, distributed-ready — the
    /// bottom of every axis. An UNDECLARED companion contributes this, so a
    /// composition of undeclared companions joins to it and a pre-282
    /// deployment is byte-for-byte unchanged (GP 11). Joining anything with
    /// `identity` returns the other value (Phase 296).
    let identity: CompanionCapability = {
        Effect = Pure
        Determinism = Deterministic
        Readiness = DistributedReady
    }

    /// The conservative default an undeclared companion takes — an alias for
    /// `identity`. A manifest / preflight that reads the posture of an
    /// undeclared companion sees this and reports "pure", unchanged from a
    /// pre-282 build.
    let defaultCapability: CompanionCapability = identity

    /// A pure companion with no declared side effect and no hidden reads,
    /// distributed-ready — the identity, offered by name for a companion
    /// that wants to declare its purity explicitly.
    let pure': CompanionCapability = identity

    /// A distributed-ready companion that writes to the world (effecting)
    /// and reads external state — the common posture of a real cloud
    /// companion (blob storage, a notification channel, a job scheduler).
    let distributedEffecting: CompanionCapability = {
        Effect = Effecting
        Determinism = DeterminismSource.externalState
        Readiness = DistributedReady
    }

    /// A dev-only companion holding in-memory state — the documented
    /// exception posture (e.g. the in-process reference `IJobScheduler` /
    /// notification channel / a local embedding provider). Effecting +
    /// external-state (its in-memory store) + dev-only.
    let devOnlyEffecting: CompanionCapability = {
        Effect = Effecting
        Determinism = DeterminismSource.externalState
        Readiness = DevOnly
    }

    /// Set the effect class on a capability (fluent declaration helper).
    let withEffect (effect: EffectClass) (cap: CompanionCapability) : CompanionCapability = { cap with Effect = effect }

    /// Set the determinism source on a capability (fluent declaration helper).
    let withDeterminism (source: DeterminismSource) (cap: CompanionCapability) : CompanionCapability = {
        cap with
            Determinism = source
    }

    /// Set the distributed-readiness on a capability (fluent declaration
    /// helper).
    let withReadiness (readiness: Readiness) (cap: CompanionCapability) : CompanionCapability = {
        cap with
            Readiness = readiness
    }

    /// Whether this capability is the join identity (pure / deterministic /
    /// distributed-ready) — i.e. it declares no effect, no hidden read, and
    /// distributed-readiness. True for an undeclared companion's default.
    let isPure (cap: CompanionCapability) : bool = cap = identity

    /// Whether this capability is safe for a distributed deployment — false
    /// only when it declares `DevOnly`.
    let isDistributedReady (cap: CompanionCapability) : bool = cap.Readiness = DistributedReady

    // ─── Phase 296 — the componentwise effect-join ────────────────────
    //
    // The composition's posture is the componentwise join of its
    // companions' capabilities: effecting wins over pure, determinism
    // factors union, dev-only wins over distributed-ready. `identity` is
    // the join identity (joining with it returns the other value), so an
    // undeclared companion never changes the join (GP 11). The join is
    // associative + commutative + idempotent — the three axes are each a
    // join-semilattice, so the product is too — which is exactly what an
    // authoring tool needs to reason about a composition's effect class
    // regardless of the order it composed the parts.

    /// Componentwise join of two capabilities: effect ⊔ effect, determinism
    /// ⊔ determinism (factor-set union), readiness ⊔ readiness. Associative,
    /// commutative, idempotent; `identity` is the two-sided identity.
    let join (a: CompanionCapability) (b: CompanionCapability) : CompanionCapability = {
        Effect = EffectClass.join a.Effect b.Effect
        Determinism = DeterminismSource.join a.Determinism b.Determinism
        Readiness = Readiness.join a.Readiness b.Readiness
    }

    /// The join of a sequence of capabilities, folded from `identity`. An
    /// empty sequence joins to `identity` ("pure"); the fold order does not
    /// matter (associative + commutative). This is the composed app's effect
    /// class — the static "what can this composition touch?" property.
    let joinAll (caps: CompanionCapability seq) : CompanionCapability = Seq.fold join identity caps

    /// Look a component's declared capability up in a `CapabilitySignature`,
    /// falling back to the conservative default (`identity`) for an
    /// undeclared component — so an absent id never changes a join (GP 11).
    let resolve (signature: CapabilitySignature) (componentId: ComponentId) : CompanionCapability =
        signature |> Map.tryFind componentId |> Option.defaultValue defaultCapability

    /// The composed app's joined effect class over a whole
    /// `CapabilitySignature` — the join of every declared component
    /// capability. Undeclared components contribute the identity (they are
    /// simply absent from the map), so the result equals the join of the
    /// declared capabilities. This is the value the Phase 280 manifest
    /// surfaces (opt-in) as the composition's effect class.
    let composedEffect (signature: CapabilitySignature) : CompanionCapability =
        signature |> Map.toSeq |> Seq.map snd |> joinAll

// ─── Phase 688 — seam-granularity authority grants ────────────────────
//
// The three axes above are an EFFECT LATTICE. They answer "may this
// component do effecting work at all", over a two-point order — they do
// not answer "which seams may it reach". So a component holding any
// effecting grant may resolve every seam the composition will hand it,
// `IEntityStore` and `ISecretStore` and `IAuditSink` alike, and reviewing
// what a module CAN reach still means reading its code. A declared
// reachable-seam set closes that half: "I reach `IEntityStore` and
// `IAuditSink`, nothing else", held to by the Phase 300 gate.
//
// **Why a sibling value rather than a fourth field on
// `CompanionCapability`.** This is a lattice property, not a
// compatibility dodge. Each of the three axes has its *identity* at the
// BOTTOM (pure / deterministic / distributed-ready), which is exactly
// what lets an undeclared companion contribute `identity` and change no
// join (GP 11). A seam set's behaviour-preserving value is the OPPOSITE
// end: an undeclared component must keep reaching everything, which is
// the TOP of the subset order. Folding an inverted axis into
// `CompanionCapability.join` would stop `identity` being the join
// identity and quietly break the Phase 296 laws the manifest, the
// preflight and the gate all rest on. So the grant is a separate value
// in a separate signature map, joined to the capability by the same
// stable `ComponentId` key — the same "deltas join on `ComponentId`"
// discipline the Phase 438 surface records.
//
// **Absent ⇒ unrestricted (GP 11).** A component with no entry in a
// `SeamGrantSignature` resolves to `UnrestrictedSeams`: byte-for-byte the
// pre-688 posture, no seam refused. Declaration is the opt-in; absence is
// the no-op. Under the Phase 657 verified profile that inverts — there,
// absence is refused rather than defaulted, because a mandatory
// seam-authority check with nothing declared would permit everything
// while presenting as enforcement.
//
// **Generic substrate (GP 1), zero cost when unread (GP 13).** A string
// id, a two-case union and a set: no vendor or domain type, nothing built
// until a caller asks.

/// The stable identity of a **seam** — an SDK interface / DI service a
/// component may resolve (`IEntityStore`, `IAuditSink`, `ISecretStore`).
/// A value-typed `string` wrapper with structural comparison, so it is
/// safe as a set member, a map key, and across the wire (portability
/// rule 1). Deliberately its own type rather than a `ComponentId`: a
/// `ComponentId` names a composed UNIT, a `SeamId` names the CONTRACT
/// that unit reaches through, and the two key spaces must not be
/// interchangeable.
type SeamId =
    | SeamId of string

    /// The raw underlying seam identity string.
    member this.Value =
        let (SeamId v) = this
        v

    override this.ToString() = this.Value

/// A component's declared reachable-seam authority. Two cases, and the
/// distinction between the second one's EMPTY set and the first case is
/// the whole security property:
///
///   • `UnrestrictedSeams` — nothing was declared. The pre-688 posture:
///     every seam resolves. What an absent signature entry means.
///   • `DeclaredSeams s`   — exactly `s` resolves, everything else is
///     refused. `DeclaredSeams Set.empty` is a real declaration meaning
///     "this component reaches no seam at all", NOT a synonym for
///     unrestricted — which is why `ofSeams` does not normalise the
///     empty set away the way `DeterminismSource.ofFactors` does.
type SeamGrant =
    /// No declaration — every seam resolves (GP 11: absence is the no-op).
    | UnrestrictedSeams
    /// Exactly these seams resolve; any other resolution is refused.
    | DeclaredSeams of Set<SeamId>

/// Declared seam authority per composed unit, keyed by the same stable
/// `ComponentId` the Phase 296 `CapabilitySignature`, the Phase 280
/// manifest and the Phase 438 surface use. An absent id resolves to
/// `UnrestrictedSeams`, so a composition that declares nothing is
/// unchanged (GP 11).
type SeamGrantSignature = Map<ComponentId, SeamGrant>

[<RequireQualifiedAccess>]
module SeamId =

    let private normalise (raw: string) : string =
        if System.String.IsNullOrWhiteSpace raw then
            ""
        else
            raw.Trim()

    /// The raw underlying seam identity string.
    let value (SeamId v) : string = v

    /// Construct a `SeamId` from a raw identity (trimmed). Raises
    /// `ArgumentException` on a null / empty / whitespace input — an
    /// unnamed seam can never be granted or refused, and silently
    /// accepting one would put an unmatchable member in a grant set.
    let create (raw: string) : SeamId =
        let v = normalise raw

        if v = "" then
            invalidArg "raw" "SeamId cannot be null, empty, or whitespace."

        SeamId v

    /// `create` as an option — `None` for a null / empty / whitespace
    /// input rather than a raise.
    let tryCreate (raw: string) : SeamId option =
        let v = normalise raw
        if v = "" then None else Some(SeamId v)

    /// The seam a component reaches through an SDK interface, named by
    /// the interface's own name (`IEntityStore`). The conventional
    /// derivation — a seam IS its contract, so the contract's name is its
    /// stable identity, independent of which implementation is composed
    /// behind it.
    let ofInterface (interfaceName: string) : SeamId = create interfaceName

[<RequireQualifiedAccess>]
module SeamGrant =

    /// The undeclared posture — every seam resolves. What an absent
    /// signature entry means, and the value a pre-688 composition has
    /// everywhere (GP 11).
    let unrestricted: SeamGrant = UnrestrictedSeams

    /// Declare exactly these seams. An EMPTY sequence produces
    /// `DeclaredSeams Set.empty` — "reaches nothing" — and is deliberately
    /// NOT folded to `UnrestrictedSeams`: the two are opposite ends of the
    /// order, and normalising one into the other would turn the tightest
    /// possible declaration into the loosest.
    let ofSeams (seams: SeamId seq) : SeamGrant = DeclaredSeams(Set.ofSeq seams)

    /// Declare exactly these seams, named by their interface names — the
    /// declaration shape a companion author writes by hand.
    let ofInterfaces (interfaceNames: string seq) : SeamGrant =
        DeclaredSeams(interfaceNames |> Seq.map SeamId.ofInterface |> Set.ofSeq)

    /// The declared seam set — empty for `UnrestrictedSeams`, which
    /// declares no set at all. Read it together with `isDeclared`: an
    /// empty result means "unrestricted" or "reaches nothing" depending
    /// on which case produced it.
    let seams (grant: SeamGrant) : Set<SeamId> =
        match grant with
        | UnrestrictedSeams -> Set.empty
        | DeclaredSeams s -> s

    /// Whether the component declared a seam set at all. `false` is the
    /// undeclared posture the verified profile refuses.
    let isDeclared (grant: SeamGrant) : bool =
        match grant with
        | UnrestrictedSeams -> false
        | DeclaredSeams _ -> true

    /// Whether `seam` may be resolved under this grant. Unrestricted
    /// permits everything; a declared set permits exactly its members.
    let permits (grant: SeamGrant) (seam: SeamId) : bool =
        match grant with
        | UnrestrictedSeams -> true
        | DeclaredSeams s -> Set.contains seam s

    /// Whether `grant` permits everything `other` permits — the
    /// containment order used to decide whether a grant-set change WIDENED
    /// a component's authority. `UnrestrictedSeams` covers everything;
    /// nothing but itself covers `UnrestrictedSeams`.
    let covers (grant: SeamGrant) (other: SeamGrant) : bool =
        match grant, other with
        | UnrestrictedSeams, _ -> true
        | DeclaredSeams _, UnrestrictedSeams -> false
        | DeclaredSeams a, DeclaredSeams b -> Set.isSubset b a

    /// Join two grants for the same component: the union of what each
    /// permits, with `UnrestrictedSeams` absorbing (it is the TOP of this
    /// order, not the bottom — see the header note on why this is not a
    /// fourth `CompanionCapability` axis). Associative, commutative,
    /// idempotent; `DeclaredSeams Set.empty` is the identity.
    let join (a: SeamGrant) (b: SeamGrant) : SeamGrant =
        match a, b with
        | UnrestrictedSeams, _
        | _, UnrestrictedSeams -> UnrestrictedSeams
        | DeclaredSeams x, DeclaredSeams y -> DeclaredSeams(Set.union x y)

    /// Look a component's declared grant up in a `SeamGrantSignature`,
    /// falling back to `UnrestrictedSeams` for an absent id — so an
    /// undeclared component keeps reaching every seam (GP 11).
    let resolve (signature: SeamGrantSignature) (componentId: ComponentId) : SeamGrant =
        signature |> Map.tryFind componentId |> Option.defaultValue UnrestrictedSeams

    /// The stable kind label — `"unrestricted"` / `"declared"` — for
    /// manifest wire projections and refusal messages. Never positional.
    let kindLabel (grant: SeamGrant) : string =
        match grant with
        | UnrestrictedSeams -> "unrestricted"
        | DeclaredSeams _ -> "declared"

    /// The declared seam identities, sorted ordinally — the deterministic
    /// projection a golden file persists.
    let seamLabels (grant: SeamGrant) : string list =
        seams grant
        |> Set.toList
        |> List.map SeamId.value
        |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

    /// A readable one-line rendering: `unrestricted`, `declared{}`, or
    /// `declared{IAuditSink,IEntityStore}`.
    let render (grant: SeamGrant) : string =
        match grant with
        | UnrestrictedSeams -> "unrestricted"
        | DeclaredSeams _ -> "declared{" + String.concat "," (seamLabels grant) + "}"

    /// Rebuild a grant from its persisted kind label + seam labels. An
    /// unrecognised kind reads as `unrestricted` — the pre-688 posture,
    /// never a fabricated restriction that would refuse resolutions the
    /// composition never declared against.
    let ofWireParts (kind: string) (labels: string seq) : SeamGrant =
        match (if isNull kind then "" else kind.Trim()) with
        | "declared" -> DeclaredSeams(labels |> Seq.choose SeamId.tryCreate |> Set.ofSeq)
        | _ -> UnrestrictedSeams