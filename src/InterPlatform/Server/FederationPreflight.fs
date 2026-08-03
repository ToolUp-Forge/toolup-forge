// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 591 — federation-graph preflight ──────────────────────────
//
// The module-graph rules (Phase 583) prove the *intra-app* graph; nothing
// proved the **inter-instance** graph. A deployment that consumes a peer
// contract no counterparty serves, at an incompatible version, or under a
// trust posture the counterparty never declared, discovered it at call
// time — one federated request in, with the caller's traffic already
// flowing.
//
// **You cannot introspect another organisation's deployment; you validate
// against its signed label.** The counterparty's `PeerSurface` export
// (Phase 590) is that label: a hash-stamped, canonical-JSON document it
// published. A deployment PINS the labels of the counterparties it calls
// and this preflight checks the live composition against them — before
// traffic, at compose time, in the composition-validator pass.
//
// **Contract-level checking is what makes heterogeneous federations
// safe.** Nothing here reads, requires, or reasons about a counterparty's
// *composition*: participants' internals stay free and un-inspected. Only
// the pinned wire faces must agree — which is exactly the surface both
// sides already publish and the only surface a federation can honestly
// hold each other to.
//
// **Three rule families, stable codes, exported as data:**
//
// * `peer-contract-unsatisfied` (Error) — a consumed contract that no
//   pinned counterparty serves, or that one serves at no version this
//   deployment can speak. Version compatibility is the contract's own
//   declared discipline: the handshake resolves the **highest mutual**
//   version (`PeerCapabilityNegotiation.negotiate`), so compatibility is
//   a non-empty intersection of the two advertised sets — nothing weaker
//   (a "close enough" major) and nothing stronger (exact equality).
// * `peer-trust-mismatch` (Error) — a trust facet this deployment
//   REQUIRES of the counterparties it calls, which a pinned label does
//   not declare, or declares at a different value.
// * `peer-surface-stale` (Warning) — a pin older than the declared
//   maximum age. A warning and not an error deliberately: an aged pin is
//   not evidence that anything drifted, it is the ABSENCE of fresh
//   evidence, and taking a deployment down over a clock (rather than over
//   a defect) is not a trade an operator asked for when they declared a
//   refresh cadence. The two rules that DO refuse are the two that name a
//   call which cannot succeed.
//
// **Refuse vs report, stated once.** A consumed contract nothing serves,
// and a required trust facet a counterparty does not declare, are both
// *statements about calls this deployment will make*: the first fails at
// dispatch with `PeerContractNotFound` / `PeerVersionMismatch`, the
// second means the posture the deployment believes it federates under is
// not the posture the counterparty published. Surfacing either after the
// app is serving traffic is strictly worse than refusing to start, and
// the pin set is a deliberate operator declaration — so a deployment that
// pinned nothing can never be refused for either.
//
// **Aggregates need no special case.** A gateway-fronted group (Phase
// 595) publishes an ordinary `PeerSurface` through the identical export
// path, so it pins identically. Its trust facets are FLOORS over the
// exposing members, and a facet the members disagree on is published as
// an explicit `mixed:a|b` marker — which satisfies no required value,
// because a counterparty reading `mixed:` may rely on neither stance.
// That falls out of comparing declared values rather than being special
// cased, which is why there is no aggregate branch anywhere below.
//
// **Dormant with zero pinned surfaces (GP 11 / GP 13).** Every rule
// short-circuits on an empty pin set, `PeerCompose` registers no
// validator at all without one, and a deployment that never pins a
// counterparty is byte-for-byte a pre-591 composition. The store is opt-in
// data the deployment declares; the substrate fetches nothing, reaches
// nothing, and refuses nothing that was not declared.

/// One trust facet exactly as a counterparty's pinned label DECLARED it:
/// the facet name and its published value.
///
/// **A name/value pair rather than a mirror of `PeerTrustPosture`, and
/// that is the point.** A pinned label is a document written by another
/// organisation, under a `PeerSurfaceExport.FormatVersion` this
/// deployment does not control — so a facet vocabulary fixed by a record
/// this build declares would silently drop whatever the counterparty
/// published beyond it. The names come from `PeerTrustPosture`'s own
/// record fields by reflection at pinning time (`FederationPin.trustFacets`),
/// so the vocabulary is single-sourced and cannot drift; carrying them as
/// data is what lets a pin outlive the descriptor version it was taken
/// under.
type PinnedTrustFacet = {
    /// The facet name, as published — one of `PeerTrustPosture`'s record
    /// field names for a label this build's descriptor vocabulary
    /// produced.
    Facet: string
    /// The published value. A `mixed:a|b` value is an aggregate surface
    /// reporting that its exposing members disagree on this facet
    /// (Phase 595), and satisfies no requirement.
    Value: string
}

/// A pinned copy of what a counterparty CLAIMED to serve: the projection
/// of one `PeerSurfaceExport` a deployment holds and validates against.
///
/// Built with `FederationPin.ofExportJson` (the realistic path — verify
/// the published document's hash stamp, then project) or
/// `FederationPin.ofSurface` for a surface the deployment already holds
/// as a value. Never fetched: the substrate opens no socket for a pin,
/// because a label you fetch at boot is a label that changed after you
/// read it.
type PinnedPeerSurface = {
    /// The counterparty peer id this pin describes — what defect
    /// messages name, and what an operator reconciles against the
    /// registry.
    CounterpartyId: string
    /// Where the snapshot came from: a file path, a URL, an out-of-band
    /// reference. Purely descriptive — nothing dereferences it — so that
    /// a stale-pin report can tell an operator where to get a fresh one.
    Source: string
    /// The `PeerSurfaceExport.FormatVersion` the label was published
    /// under, recorded for diagnostics. A label this build cannot read is
    /// refused at pinning time rather than carried as a half-read pin.
    FormatVersion: int
    /// The label's own SHA-256 stamp (`PeerSurfaceExport.SurfaceHash`),
    /// verified against a recomputation when the pin was taken. Named in
    /// every defect message so two operators can compare pins without
    /// exchanging documents.
    SurfaceHash: string
    /// When this snapshot was taken — the age the stale rule measures.
    PinnedAt: DateTimeOffset
    /// What the counterparty's label says it serves: contract ids and
    /// their advertised wire versions, the same shape the capability
    /// handshake exchanges. Sorted by contract id.
    Serves: CapabilityList
    /// The counterparty's declared trust posture, as published. Empty
    /// when the label carries none (a non-federating counterparty — a
    /// pin worth holding only to record that fact).
    TrustFacets: PinnedTrustFacet list
}

/// One trust facet this deployment REQUIRES of every pinned counterparty
/// it calls. Declared with `PeerServerApp.withRequiredPeerTrust`, built
/// with the `PeerTrustRequirement` helpers.
///
/// A requirement is a statement about the posture this deployment
/// believes it federates under. It is checked against the counterparty's
/// published label and nothing else — the substrate never asks a
/// counterparty to prove a posture, because a posture claim is exactly
/// what a label IS.
type PeerTrustRequirement = {
    /// The facet name — a `PeerTrustPosture` record field name.
    Facet: string
    /// The value the counterparty's label must declare for this facet.
    RequiredValue: string
}

/// The well-known trust requirements, named so a composition does not
/// spell facet names by hand. Each mirrors one `PeerTrustPosture` field;
/// the correspondence is pinned by a conformance test, so a facet renamed
/// on the posture fails the suite rather than silently matching nothing.
[<RequireQualifiedAccess>]
module PeerTrustRequirement =

    /// Require the counterparty to bind inbound tokens' `aud` claim to
    /// its own peer id — the Phase 130 confused-deputy defence. The
    /// facet a federation is most likely to want stated, because a
    /// counterparty without it accepts a token minted for a DIFFERENT
    /// receiver whenever that receiver trusts the same issuer.
    let audienceBound: PeerTrustRequirement = {
        Facet = "AudienceBound"
        RequiredValue = "true"
    }

    /// Require a specific auth profile (e.g. `"jwt-hs256-bearer"`).
    let authProfile (value: string) : PeerTrustRequirement = {
        Facet = "AuthProfile"
        RequiredValue = value
    }

    /// Require a specific delegation-verification stance (e.g.
    /// `"per-peer-trust-anchor"`) — the check a multi-hop federation
    /// relies on when it forwards `Delegated` assertions.
    let delegationVerification (value: string) : PeerTrustRequirement = {
        Facet = "DelegationVerification"
        RequiredValue = value
    }

    /// Require a specific replay stance (e.g. `"freshness-window"`).
    let replayStance (value: string) : PeerTrustRequirement = {
        Facet = "ReplayStance"
        RequiredValue = value
    }

    /// Require a specific transport-security stance (e.g.
    /// `"deployment-managed"`).
    let transportSecurity (value: string) : PeerTrustRequirement = {
        Facet = "TransportSecurity"
        RequiredValue = value
    }

/// The pinned-counterparty store a peer composition declares: the labels
/// it holds, how long a label stays acceptable, and what it requires of
/// every counterparty it calls.
///
/// One record rather than three fields on `PeerServerApp`, on the
/// argument `ModuleGraphReferences` makes for the module-graph facts: the
/// federation-preflight inputs extend as a family, and a family that
/// grows inside its own record grows the compose record's constructor
/// once instead of once per fact.
type FederationPinStore = {
    /// The pinned counterparty labels. **Empty is the whole GP 13
    /// story** — every rule short-circuits on it and no validator is
    /// registered.
    Pins: PinnedPeerSurface list
    /// How long a pin stays acceptable before `peer-surface-stale`
    /// reports it. `None` — the default — leaves the rule dormant, which
    /// is the honest default: forge cannot know a federation's refresh
    /// cadence, and inventing one would report on every deployment that
    /// pinned a label and never said how often it renews.
    MaxPinAge: TimeSpan option
    /// Trust facets required of every pinned counterparty this deployment
    /// actually consumes from. Empty leaves `peer-trust-mismatch`
    /// dormant.
    RequiredTrust: PeerTrustRequirement list
}

[<RequireQualifiedAccess>]
module FederationPinStore =

    /// The store of a composition that pinned nothing — every rule a
    /// no-op, no validator registered, byte-for-byte a pre-591
    /// composition (GP 11 / GP 13).
    let empty: FederationPinStore = {
        Pins = []
        MaxPinAge = None
        RequiredTrust = []
    }

    /// Add a pinned counterparty label. Re-pinning the same counterparty
    /// keeps BOTH entries deliberately: a counterparty that legitimately
    /// serves a contract from more than one published label (a migration
    /// window, a gateway plus the member behind it) is a real federation
    /// shape, and the contract rule is satisfied by ANY pin that serves
    /// the contract.
    let withPin (pin: PinnedPeerSurface) (store: FederationPinStore) : FederationPinStore = {
        store with
            Pins = store.Pins @ [ pin ]
    }

    /// Declare the maximum age a pin stays acceptable for.
    let withMaxPinAge (maxAge: TimeSpan) (store: FederationPinStore) : FederationPinStore = {
        store with
            MaxPinAge = Some maxAge
    }

    /// Declare a trust facet required of every pinned counterparty this
    /// deployment consumes from.
    let withRequiredTrust (requirement: PeerTrustRequirement) (store: FederationPinStore) : FederationPinStore = {
        store with
            RequiredTrust = store.RequiredTrust @ [ requirement ]
    }

/// The composed facts the federation-graph rules read — derived from the
/// composition at `run`, never hand-listed, so the preflight checks
/// exactly what the deployment declared.
///
/// `Now` is a parameter rather than a read of the ambient clock so the
/// stale rule is a pure function of its inputs and a test can age a pin
/// without waiting (GP 12 rule 4 — no state between calls, nothing
/// implicit).
type FederationPreflightInput = {
    /// This deployment's own peer id, for defect messages. Empty for a
    /// host-only composition (which consumes nothing, so every rule is
    /// vacuous anyway).
    LocalPeerId: string
    /// What this deployment declared it calls on counterparts
    /// (`PeerServerApp.withConsumedContract`).
    Consumes: ConsumedContract list
    /// The declared pin store.
    Pins: FederationPinStore
    /// The moment pin ages are measured against.
    Now: DateTimeOffset
}

/// The federation-graph rule family: declared rules, the pure check they
/// generate, the Phase 294 manifest projection, and the structural-class
/// `IConfigValidator` that runs them at preflight.
[<RequireQualifiedAccess>]
module FederationPreflight =

    /// Stable `IConfigValidator.Name` for the federation-graph preflight.
    /// Structural-class (Phase 585), so `SkipPreflight` does NOT bypass
    /// it: every rule is a pure sweep over declared data already in
    /// memory — no socket, no counterparty reached, microseconds — so an
    /// emergency boot loses nothing by running them, while skipping them
    /// would mean booting a deployment whose federation edges are known
    /// not to resolve.
    [<Literal>]
    let ValidatorName = "federation-graph-preflight"

    /// A single declared federation-graph rule. `Evaluate` is pure: it
    /// maps the composed federation facts to zero or more defect
    /// messages, each already alternative-enumerating. The runtime check
    /// tags every message with this rule's `Code` + `Severity`;
    /// `ruleManifest` projects `Code` / `Severity` / `Description` — one
    /// definition, two readers, exactly as `CompositionValidator` does
    /// for the intra-app rules.
    type FederationRule = {
        Code: string
        Severity: CompositionDefectSeverity
        Description: string
        Evaluate: FederationPreflightInput -> string list
    }

    /// A quoted, comma-separated list, or `"none"` for the empty case —
    /// every message enumerates its alternatives.
    let private enumerate (values: string list) : string =
        match values |> List.distinct |> List.sort with
        | [] -> "none"
        | vs -> vs |> List.map (sprintf "'%s'") |> String.concat ", "

    /// `1.0` form of a wire version, for defect messages.
    let private versionText (version: ContractVersion) : string =
        sprintf "%d.%d" version.Major version.Minor

    let private versionsText (versions: ContractVersion list) : string =
        match versions |> List.distinct |> List.sort with
        | [] -> "none"
        | vs -> vs |> List.map versionText |> String.concat ", "

    // ── Rule evaluators (pure) ──────────────────────────────────────────

    /// Every pin that serves `contractId`, paired with the capability
    /// entry that advertises it.
    let private serving (contractId: string) (input: FederationPreflightInput) =
        input.Pins.Pins
        |> List.choose (fun pin ->
            pin.Serves
            |> List.tryFind (fun capability -> capability.ContractId = contractId)
            |> Option.map (fun capability -> pin, capability))

    /// A consumed contract no pinned counterparty serves, or that one
    /// serves at no version this deployment can speak.
    ///
    /// **Compatibility is the intersection, because that is what the
    /// handshake does.** `PeerCapabilityNegotiation.negotiate` intersects
    /// the two advertised version sets and takes the highest mutual
    /// (`List.max` over the structural `Major`-dominates-`Minor` order),
    /// so a non-empty intersection is precisely the condition under which
    /// a call resolves. Checking anything else here — a major-version
    /// heuristic, an exact match — would answer a different question from
    /// the one the runtime asks, and the whole value of a preflight is
    /// that it asks the same one earlier.
    let private evalContractUnsatisfied (input: FederationPreflightInput) : string list =
        let pinnedCounterparties = input.Pins.Pins |> List.map _.CounterpartyId

        input.Consumes
        |> List.distinct
        |> List.choose (fun consumed ->
            match serving consumed.ContractId input with
            | [] ->
                let servedAnywhere =
                    input.Pins.Pins |> List.collect (fun pin -> pin.Serves |> List.map _.ContractId)

                Some(
                    sprintf
                        "Consumed peer contract '%s' (expected counterpart role '%s', versions %s) is served by no pinned counterparty. Every call this deployment makes against it would fail at dispatch with PeerContractNotFound. Pinned counterparties: %s; contracts they serve: %s. Pin the counterparty that serves '%s' (FederationPin.ofExportJson over its published PeerSurface export), correct the consumed contract id, or drop the PeerServerApp.withConsumedContract declaration."
                        consumed.ContractId
                        consumed.CounterpartRole
                        (versionsText consumed.Versions)
                        (enumerate pinnedCounterparties)
                        (enumerate servedAnywhere)
                        consumed.ContractId
                )
            | candidates ->
                let mutual =
                    candidates
                    |> List.filter (fun (_, capability) ->
                        consumed.Versions |> List.exists (fun v -> List.contains v capability.Versions))

                if not (List.isEmpty mutual) then
                    None
                else
                    let advertised =
                        candidates
                        |> List.map (fun (pin, capability) ->
                            sprintf
                                "'%s' serves %s (label %s)"
                                pin.CounterpartyId
                                (versionsText capability.Versions)
                                pin.SurfaceHash)
                        |> String.concat "; "

                    Some(
                        sprintf
                            "Consumed peer contract '%s' is served by %d pinned counterparty(ies), but at no version this deployment speaks: it consumes %s, while %s. The capability handshake resolves the HIGHEST MUTUAL version, so an empty intersection makes every call fail with PeerVersionMismatch. Widen the consumed version set to one the counterparty advertises, take a fresher pin if the counterparty has since added one, or drop the declaration."
                            consumed.ContractId
                            (List.length candidates)
                            (versionsText consumed.Versions)
                            advertised
                    ))

    /// A trust facet this deployment requires, which a pinned
    /// counterparty it actually calls does not declare — or declares at a
    /// different value.
    ///
    /// **Scoped to counterparties this deployment consumes from.** A pin
    /// held for a counterparty nothing calls is a record, not an edge;
    /// gating a boot on the posture of a deployment we never address
    /// would refuse compositions that are correct today (GP 11) and would
    /// punish an operator for keeping a complete registry.
    let private evalTrustMismatch (input: FederationPreflightInput) : string list =
        match input.Pins.RequiredTrust with
        | [] -> []
        | required ->
            let consumedIds = input.Consumes |> List.map _.ContractId |> Set.ofList

            let counterpartiesInPlay =
                input.Pins.Pins
                |> List.filter (fun pin -> pin.Serves |> List.exists (fun c -> Set.contains c.ContractId consumedIds))

            counterpartiesInPlay
            |> List.collect (fun pin ->
                let declared =
                    pin.TrustFacets |> List.map (fun f -> sprintf "%s=%s" f.Facet f.Value)

                required
                |> List.choose (fun requirement ->
                    match pin.TrustFacets |> List.tryFind (fun f -> f.Facet = requirement.Facet) with
                    | Some facet when facet.Value = requirement.RequiredValue -> None
                    | Some facet ->
                        Some(
                            sprintf
                                "Pinned counterparty '%s' declares trust facet '%s' = '%s', but this deployment requires '%s'. The pinned label (%s, taken %s from %s) is the only statement of a counterparty's posture a federation has, so a call made under a posture it never claimed is made under an assumption nothing supports. Declared facets: %s. Take a fresher pin if the counterparty has since changed its posture, relax the requirement with PeerServerApp.withRequiredPeerTrust, or stop consuming this counterparty's contracts."
                                pin.CounterpartyId
                                requirement.Facet
                                facet.Value
                                requirement.RequiredValue
                                pin.SurfaceHash
                                (pin.PinnedAt.ToString "u")
                                pin.Source
                                (enumerate declared)
                        )
                    | None ->
                        Some(
                            sprintf
                                "Pinned counterparty '%s' declares no trust facet '%s', which this deployment requires at '%s'. A label that omits a facet asserts nothing about it — it is not a weaker claim, it is no claim. Declared facets: %s (label %s, taken %s from %s). Take a fresher pin if the counterparty's label predates the facet, relax the requirement with PeerServerApp.withRequiredPeerTrust, or stop consuming this counterparty's contracts."
                                pin.CounterpartyId
                                requirement.Facet
                                requirement.RequiredValue
                                (enumerate declared)
                                pin.SurfaceHash
                                (pin.PinnedAt.ToString "u")
                                pin.Source
                        )))

    /// A pin older than the declared maximum age. Dormant unless
    /// `MaxPinAge` is declared — an undeclared deployment evaluates one
    /// `match` and yields nothing (GP 13).
    let private evalSurfaceStale (input: FederationPreflightInput) : string list =
        match input.Pins.MaxPinAge with
        | None -> []
        | Some maxAge ->
            input.Pins.Pins
            |> List.filter (fun pin -> input.Now - pin.PinnedAt > maxAge)
            |> List.map (fun pin ->
                sprintf
                    "Pinned surface for counterparty '%s' was taken %s (%.1f days ago) and the declared maximum pin age is %.1f days. Nothing here says the counterparty HAS drifted — an aged pin is the absence of fresh evidence, not evidence of a defect, which is why this reports rather than refuses. Re-fetch the counterparty's PeerSurface export from %s and re-pin it (its stamp at pinning time was %s), or raise the maximum age with PeerServerApp.withPinnedSurfaceMaxAge."
                    pin.CounterpartyId
                    (pin.PinnedAt.ToString "u")
                    (input.Now - pin.PinnedAt).TotalDays
                    maxAge.TotalDays
                    pin.Source
                    pin.SurfaceHash)

    /// The declared rule set — the single source of truth the runtime
    /// check is generated from and the manifest projects. Every rule is
    /// structural-class: a pure sweep over declared data already in
    /// memory, correct and fast on a machine with every counterparty
    /// unreachable, which is exactly the bar Phase 585 sets for a rule
    /// that runs unconditionally.
    let structuralRules: FederationRule list = [
        {
            Code = "peer-contract-unsatisfied"
            Severity = DefectError
            Description =
                "Every peer contract a deployment declares it consumes must be served by some pinned counterparty, at a version the two sides share (the handshake's highest-mutual discipline)."
            Evaluate = evalContractUnsatisfied
        }
        {
            Code = "peer-trust-mismatch"
            Severity = DefectError
            Description =
                "Every trust facet a deployment requires of its counterparties must be declared, at the required value, on the pinned label of each counterparty it consumes from."
            Evaluate = evalTrustMismatch
        }
        {
            Code = "peer-surface-stale"
            Severity = DefectWarning
            Description =
                "A pinned counterparty surface older than the declared maximum pin age should be refreshed; an aged pin is absent evidence rather than evidence of drift, so it reports and never refuses."
            Evaluate = evalSurfaceStale
        }
    ]

    /// The Phase 294 introspectable rule manifest for the federation-graph
    /// family: every shipped rule as data (code + severity +
    /// description), projected from the same `structuralRules` list
    /// `check` runs. One source of truth — a rule cannot appear in the
    /// runtime check without appearing here, or vice versa — so an
    /// external pre-build checker validates a federation against forge's
    /// own invariants with no re-encoding and no drift.
    ///
    /// Exported as its own manifest rather than folded into
    /// `CompositionValidator.ruleManifest` because these rules read
    /// federation facts that no `CompositionManifest` carries, and the
    /// peer substrate is a companion the platform tier does not (and must
    /// not) reference. The shapes are the platform's own descriptor types,
    /// so a checker that already reads one manifest reads this one with no
    /// new vocabulary.
    let ruleManifest: CompositionRuleDescriptor list =
        structuralRules
        |> List.map (fun rule -> {
            Code = rule.Code
            Severity = rule.Severity
            Description = rule.Description
        })

    /// `ruleManifest` plus each rule's Phase 585 `Class`. Every
    /// federation-graph rule is `StructuralRule`, so a deployment can
    /// never switch one off: a violation means the app does not boot,
    /// `SkipPreflight` or not.
    let classifiedRuleManifest: ClassifiedCompositionRule list =
        structuralRules
        |> List.map (fun rule -> {
            Code = rule.Code
            Severity = rule.Severity
            Description = rule.Description
            Class = StructuralRule
        })

    /// Run every federation-graph rule over the composed facts, returning
    /// the flat, rule-tagged defect list. Pure.
    ///
    /// An empty pin set short-circuits before a single rule runs: a
    /// deployment that pinned no counterparty has declared no federation
    /// graph to check, and inventing one from its consumed declarations
    /// alone would refuse every federating deployment that upgraded into
    /// this phase (GP 11).
    let check (input: FederationPreflightInput) : CompositionDefect list =
        match input.Pins.Pins with
        | [] -> []
        | _ ->
            structuralRules
            |> List.collect (fun rule ->
                rule.Evaluate input
                |> List.map (fun message -> {
                    RuleCode = rule.Code
                    Severity = rule.Severity
                    Message = message
                }))

    /// Translate the federation-graph defects into a `ValidationResult`,
    /// through the same projection the intra-app composition rules use —
    /// so an operator reads one preflight report in one vocabulary,
    /// whether the defect was in the app's own graph or between
    /// instances.
    let toValidationResult (input: FederationPreflightInput) : ValidationResult =
        CompositionValidator.toValidationResult (check input)

    /// The first-party `IConfigValidator` that runs the federation-graph
    /// rules at preflight.
    ///
    /// Structural-class (Phase 585): it carries the
    /// `IStructuralClassValidator` marker, so `ServerConfig.SkipPreflight`
    /// does not bypass it. Registered by `PeerCompose.run` only when the
    /// composition pinned at least one counterparty, so a deployment that
    /// declared no federation graph pays not even a singleton (GP 13).
    ///
    /// The input arrives as a thunk rather than a value so the clock is
    /// read when the preflight actually runs, not when the composition
    /// was built — the stale rule measures the age at boot, which is the
    /// moment an operator is reading the report.
    type FederationPreflightValidator(inputOf: unit -> FederationPreflightInput) =
        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout
            member _.Validate() = async { return toValidationResult (inputOf ()) }

        interface IStructuralClassValidator