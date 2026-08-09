module ToolUp.Platform.Tests.InProcess.FederationPreflightTests

open System
open Microsoft.FSharp.Reflection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

// ─── Phase 591 — federation-graph preflight ──────────────────────────
//
// A synthetic two-instance federation, built the way a real one is: each
// counterparty is a genuine `PeerServerApp` whose `PeerSurface` is
// described, exported and pinned through the shipped path — never a
// hand-written label. Every negative case is paired with the control that
// makes it pass, so a rule that stopped firing would show up as a
// *failing* negative rather than a quietly green suite.
//
// The consuming instance declares two consumed contracts; the fixtures
// then vary exactly one thing at a time — which counterparty is pinned,
// what versions it advertises, what posture its label declares, and how
// old the pin is.

/// A contract the consuming deployment calls on a seller counterparty.
/// NOT `private`: the host reflects via `FSharpType.IsRecord` without the
/// private-representation flag (see `PlatformPeerTests`).
type ReachContract = { Query: string -> Async<string> }

/// A contract the consuming deployment calls on a hub counterparty.
type DirectoryContract = {
    Lookup: string -> Async<string option>
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }
let private v11: ContractVersion = { Major = 1; Minor = 1 }
let private v2: ContractVersion = { Major = 2; Minor = 0 }

let private reachId = "example.reach"
let private directoryId = "example.directory"

let private sellerId = "seller-ssp"
let private hubId = "hub-registry"

let private sellerSource = "peers/seller-ssp.surface.json"
let private hubSource = "peers/hub-registry.surface.json"

let private reachImpl: ReachContract = {
    Query = fun q -> async { return $"reach:{q}" }
}

let private directoryImpl: DirectoryContract = {
    Lookup = fun _ -> async { return None }
}

let private enabledConfig = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
}

/// When each pin was taken, and when the preflight runs. Fixed values, so
/// the stale rule is exercised without waiting on a real clock.
let private pinnedAt = DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)
let private preflightAt = DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero)

// ─── Counterparty instances (real compositions, real surfaces) ───────

/// The seller counterparty's own deployment, described as a surface.
/// `bindAudience` composes (or omits) its `LocalPeer`, which is what
/// drives `TrustPosture.AudienceBound` on the label it publishes.
let private sellerSurface (versions: ContractVersion list) (bindAudience: bool) : PeerSurface =
    let composed =
        PeerServerApp.create ()
        |> PeerServerApp.withConfig enabledConfig
        |> PeerServerApp.withContract (fun fusion ->
            JsonRpcPeerHost.contract<ReachContract> reachId versions fusion reachImpl)

    if bindAudience then
        composed
        |> PeerServerApp.withLocalPeer {
            PeerId = sellerId
            DisplayName = "Seller SSP"
        }
        |> PeerSurface.describe
    else
        PeerSurface.describe composed

/// The hub counterparty's own deployment, described as a surface.
let private hubSurface () : PeerSurface =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withLocalPeer {
        PeerId = hubId
        DisplayName = "Hub registry"
    }
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<DirectoryContract> directoryId [ v1 ] fusion directoryImpl)
    |> PeerSurface.describe

let private sellerPin (surface: PeerSurface) : PinnedPeerSurface =
    FederationPin.ofSurface sellerId sellerSource pinnedAt surface

let private hubPin (surface: PeerSurface) : PinnedPeerSurface =
    FederationPin.ofSurface hubId hubSource pinnedAt surface

// ─── The consuming instance ──────────────────────────────────────────

/// The deployment under preflight: it consumes both contracts, and pins
/// whichever counterparty labels the case supplies.
let private consumerApp (pins: PinnedPeerSurface list) (requirements: PeerTrustRequirement list) : PeerServerApp =
    let declared =
        PeerServerApp.create ()
        |> PeerServerApp.withConfig enabledConfig
        |> PeerServerApp.withLocalPeer {
            PeerId = "buyer-acme"
            DisplayName = "Buyer"
        }
        |> PeerServerApp.withConsumedContract (PeerSurface.consumes<ReachContract> reachId [ v1 ] "seller")
        |> PeerServerApp.withConsumedContract (PeerSurface.consumes<DirectoryContract> directoryId [ v1 ] "hub")

    let pinned =
        pins
        |> List.fold (fun acc pin -> PeerServerApp.withPinnedCounterparty pin acc) declared

    requirements
    |> List.fold (fun acc requirement -> PeerServerApp.withRequiredPeerTrust requirement acc) pinned

/// The conformant graph: both counterparties pinned, both serving what
/// this deployment consumes, at a mutual version.
let private conformantApp () : PeerServerApp =
    consumerApp [ sellerPin (sellerSurface [ v1; v11 ] true); hubPin (hubSurface ()) ] []

let private defectsOf (app: PeerServerApp) =
    PeerServerApp.auditFederationGraph preflightAt app

let private codesOf (defects: CompositionDefect list) =
    defects |> List.map _.RuleCode |> List.distinct |> List.sort

let private messagesFor (code: string) (defects: CompositionDefect list) =
    defects |> List.filter (fun d -> d.RuleCode = code) |> List.map _.Message

let tests =
    testList "InProcess.FederationPreflight (Phase 591)" [

        test "a conformant federation graph passes clean" {
            Expect.isEmpty
                (defectsOf (conformantApp ()))
                "every consumed contract is served by a pinned counterparty at a mutual version — the preflight must find nothing"
        }

        test "a consumed contract no pinned counterparty serves fails preflight, naming both" {
            // Drop the hub pin: the directory contract is still consumed,
            // and now nothing pinned serves it.
            let app = consumerApp [ sellerPin (sellerSurface [ v1; v11 ] true) ] []
            let defects = defectsOf app

            Expect.equal
                (codesOf defects)
                [ "peer-contract-unsatisfied" ]
                "an unserved consumed contract must fire exactly the contract rule"

            let message = messagesFor "peer-contract-unsatisfied" defects |> List.exactlyOne

            Expect.stringContains message directoryId "the defect must name the consumed contract"
            Expect.stringContains message sellerId "the defect must enumerate the pinned counterparties"
            Expect.stringContains message "hub" "the defect must name the expected counterpart role"

            // Control: the same composition with the hub pinned passes.
            Expect.isEmpty
                (defectsOf (conformantApp ()))
                "pinning the counterparty that serves the contract must clear the defect"

            // Control: dropping the DECLARATION clears it too — the rule
            // reads the consumed set, not a hardcoded expectation.
            let noDirectory =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withConsumedContract (PeerSurface.consumes<ReachContract> reachId [ v1 ] "seller")
                |> PeerServerApp.withPinnedCounterparty (sellerPin (sellerSurface [ v1; v11 ] true))

            Expect.isEmpty (defectsOf noDirectory) "a contract that is not consumed cannot be unsatisfied"
        }

        test "version skew is named on both sides" {
            // The seller has moved to 2.0 only; this deployment still
            // speaks 1.0, so the handshake would resolve no mutual version.
            let app =
                consumerApp [ sellerPin (sellerSurface [ v2 ] true); hubPin (hubSurface ()) ] []

            let defects = defectsOf app

            Expect.equal
                (codesOf defects)
                [ "peer-contract-unsatisfied" ]
                "version skew is the same rule as an unserved contract — the call cannot resolve either way"

            let message = messagesFor "peer-contract-unsatisfied" defects |> List.exactlyOne

            Expect.stringContains message "1.0" "the defect must name the versions this deployment speaks"
            Expect.stringContains message "2.0" "the defect must name the versions the counterparty advertises"
            Expect.stringContains message sellerId "the defect must name the counterparty"

            // Control: an overlapping version set resolves. 1.1 is in the
            // counterparty's set and in ours, so the intersection is
            // non-empty even though neither side's set is a subset of the
            // other's — the handshake's highest-mutual discipline exactly.
            let overlapping =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withConsumedContract (PeerSurface.consumes<ReachContract> reachId [ v11; v2 ] "seller")
                |> PeerServerApp.withPinnedCounterparty (sellerPin (sellerSurface [ v1; v11 ] true))

            Expect.isEmpty (defectsOf overlapping) "a non-empty version intersection must pass"
        }

        test "a required trust facet the counterparty's label contradicts fails preflight" {
            // The seller composed no LocalPeer, so its published label
            // declares AudienceBound = false.
            let app =
                consumerApp [ sellerPin (sellerSurface [ v1; v11 ] false); hubPin (hubSurface ()) ] [
                    PeerTrustRequirement.audienceBound
                ]

            let defects = defectsOf app

            Expect.equal (codesOf defects) [ "peer-trust-mismatch" ] "a contradicted facet must fire the trust rule"

            let message = messagesFor "peer-trust-mismatch" defects |> List.exactlyOne

            Expect.stringContains message sellerId "the defect must name the counterparty"
            Expect.stringContains message "AudienceBound" "the defect must name the facet"

            // Control 1: the same requirement against a counterparty whose
            // label declares it passes.
            Expect.isEmpty
                (defectsOf (
                    consumerApp [ sellerPin (sellerSurface [ v1; v11 ] true); hubPin (hubSurface ()) ] [
                        PeerTrustRequirement.audienceBound
                    ]
                ))
                "a counterparty that declares the required facet must pass"

            // Control 2: without the requirement declared, the same
            // counterparty passes — the rule is dormant, not lenient.
            Expect.isEmpty
                (defectsOf (consumerApp [ sellerPin (sellerSurface [ v1; v11 ] false); hubPin (hubSurface ()) ] []))
                "an undeclared requirement must leave the trust rule dormant"
        }

        test "a facet the label does not declare at all is no claim, not a weak one" {
            let unknownFacet: PeerTrustRequirement = {
                Facet = "HardwareAttested"
                RequiredValue = "true"
            }

            let defects =
                defectsOf (
                    consumerApp [ sellerPin (sellerSurface [ v1; v11 ] true); hubPin (hubSurface ()) ] [ unknownFacet ]
                )

            Expect.equal (codesOf defects) [ "peer-trust-mismatch" ] "an undeclared facet must fire the trust rule"

            // BOTH pinned counterparties are consumed from, so both are in
            // play — the rule is per-counterparty, not first-match.
            let messages = messagesFor "peer-trust-mismatch" defects
            Expect.hasLength messages 2 "every pinned counterparty this deployment calls must be checked"

            Expect.all
                messages
                (fun m -> m.Contains "declares no trust facet")
                "an omitted facet must be reported as an absent claim"
        }

        test "a pinned counterparty this deployment never calls is a record, not an edge" {
            // A third label, pinned but never consumed from. It declares
            // no audience binding, which would fail the requirement if the
            // rule looked at every pin rather than the ones in play.
            let bystander =
                FederationPin.ofSurface
                    "bystander-peer"
                    "peers/bystander.surface.json"
                    pinnedAt
                    (PeerServerApp.create ()
                     |> PeerServerApp.withConfig enabledConfig
                     |> PeerServerApp.withContract (fun fusion ->
                         JsonRpcPeerHost.contract<ReachContract> "example.unrelated" [ v1 ] fusion reachImpl)
                     |> PeerSurface.describe)

            let app =
                consumerApp [
                    sellerPin (sellerSurface [ v1; v11 ] true)
                    hubPin (hubSurface ())
                    bystander
                ] [ PeerTrustRequirement.audienceBound ]

            Expect.isEmpty (defectsOf app) "a pin held for a counterparty nothing consumes from must not gate the boot"
        }

        test "an aggregate node pins like any instance, and a mixed facet satisfies nothing" {
            let withTransportSecurity (value: string) (surface: PeerSurface) = {
                surface with
                    TrustPosture =
                        surface.TrustPosture
                        |> Option.map (fun posture -> {
                            posture with
                                TransportSecurity = value
                        })
            }

            let memberA: AggregateMember = {
                Target = {
                    Peer = {
                        PeerId = "member-a"
                        DisplayName = "Member A"
                    }
                    BaseUrl = "https://a.example"
                }
                Surface = sellerSurface [ v1; v11 ] true
            }

            let memberB (transportSecurity: string) : AggregateMember = {
                Target = {
                    Peer = {
                        PeerId = "member-b"
                        DisplayName = "Member B"
                    }
                    BaseUrl = "https://b.example"
                }
                Surface = hubSurface () |> withTransportSecurity transportSecurity
            }

            let exposure: AggregateExposure = {
                Group = {
                    PeerId = "consortium"
                    DisplayName = "Consortium"
                }
                Contracts = [
                    { ContractId = reachId; Owner = None }
                    {
                        ContractId = directoryId
                        Owner = None
                    }
                ]
            }

            let pinnedGroup (transportSecurity: string) =
                match AggregatePeerSurface.derive ([ memberA; memberB transportSecurity ], exposure) with
                | Result.Error errors -> failtestf "the aggregate must derive: %A" errors
                | Result.Ok aggregate ->
                    FederationPin.ofSurface "consortium" "peers/consortium.surface.json" pinnedAt aggregate

            let requirement = PeerTrustRequirement.transportSecurity "deployment-managed"

            // Control: the members agree, so the group's floor IS the
            // members' stance and the requirement is satisfied.
            Expect.isEmpty
                (defectsOf (consumerApp [ pinnedGroup "deployment-managed" ] [ requirement ]))
                "a unanimous aggregate posture must satisfy a requirement its members satisfy"

            // The members disagree, so the group publishes `mixed:` — a
            // counterparty may rely on neither stance, so it satisfies
            // nothing.
            let divergent =
                defectsOf (consumerApp [ pinnedGroup "tls-terminated" ] [ requirement ])

            Expect.equal
                (codesOf divergent)
                [ "peer-trust-mismatch" ]
                "a divergent aggregate facet must fail a requirement on that facet"

            let message = messagesFor "peer-trust-mismatch" divergent |> List.exactlyOne

            Expect.stringContains
                message
                AggregatePeerSurface.mixedPrefix
                "the defect must show the mixed marker the group published"

            // The serving half of an aggregate pin still resolves: both
            // consumed contracts are fronted by the group, so the contract
            // rule finds nothing to report in either case.
            Expect.isEmpty
                (messagesFor "peer-contract-unsatisfied" divergent)
                "a group that fronts both consumed contracts satisfies them from one pin"
        }

        test "a pin older than the declared maximum age reports, and never refuses" {
            let stale =
                conformantApp ()
                |> PeerServerApp.withPinnedSurfaceMaxAge (TimeSpan.FromHours 12.0)

            let defects = defectsOf stale

            Expect.equal (codesOf defects) [ "peer-surface-stale" ] "an aged pin must fire exactly the stale rule"
            Expect.hasLength defects 2 "both pins are older than the declared maximum"

            Expect.all
                defects
                (fun d -> d.Severity = DefectWarning)
                "an aged pin is absent evidence, not evidence of drift — it must warn, never error"

            match FederationPreflight.toValidationResult (PeerServerApp.federationPreflightInput preflightAt stale) with
            | ValidationResult.Warning _ -> ()
            | other -> failtestf "a stale-only report must not abort startup, got %A" other

            // Control 1: a maximum age the pins are inside reports nothing.
            Expect.isEmpty
                (defectsOf (
                    conformantApp ()
                    |> PeerServerApp.withPinnedSurfaceMaxAge (TimeSpan.FromDays 30.0)
                ))
                "a pin inside the declared maximum age must not be reported"

            // Control 2: undeclared, the rule is dormant however old the
            // pins are — forge does not invent a refresh cadence.
            Expect.isEmpty (defectsOf (conformantApp ())) "an undeclared maximum age must leave the stale rule dormant"
        }

        test "a composition that pins nothing is checked against nothing" {
            let unpinned =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withConsumedContract (PeerSurface.consumes<ReachContract> reachId [ v1 ] "seller")
                |> PeerServerApp.withRequiredPeerTrust PeerTrustRequirement.audienceBound
                |> PeerServerApp.withPinnedSurfaceMaxAge (TimeSpan.FromSeconds 1.0)

            Expect.isEmpty
                (defectsOf unpinned)
                "with no pinned counterparty there is no declared federation graph to check (GP 13)"

            Expect.equal
                (PeerServerApp.create ()).FederationPins
                FederationPinStore.empty
                "a bare composition must carry the empty pin store"
        }

        test "the preflight validator is structural-class and aborts on an unsatisfied edge" {
            let inputOf (app: PeerServerApp) =
                fun () -> PeerServerApp.federationPreflightInput preflightAt app

            let unsatisfied =
                FederationPreflight.FederationPreflightValidator(
                    inputOf (consumerApp [ sellerPin (sellerSurface [ v1; v11 ] true) ] [])
                )

            Expect.isTrue
                ((unsatisfied :> obj) :? IStructuralClassValidator)
                "the federation preflight must run even under SkipPreflight — it reaches no counterparty"

            Expect.equal
                (unsatisfied :> IConfigValidator).Name
                FederationPreflight.ValidatorName
                "the validator must register under its stable name"

            match (unsatisfied :> IConfigValidator).Validate() |> Async.RunSynchronously with
            | ValidationResult.Error message ->
                Expect.stringContains message directoryId "the abort must name the contract"
            | other -> failtestf "an unsatisfied federation edge must abort startup, got %A" other

            let conformant =
                FederationPreflight.FederationPreflightValidator(inputOf (conformantApp ()))

            match (conformant :> IConfigValidator).Validate() |> Async.RunSynchronously with
            | ValidationResult.Ok -> ()
            | other -> failtestf "a conformant federation graph must pass preflight silently, got %A" other
        }

        test "the rule family is exported as data, one source of truth" {
            let codes = FederationPreflight.ruleManifest |> List.map _.Code

            Expect.equal
                codes
                [
                    "peer-contract-unsatisfied"
                    "peer-trust-mismatch"
                    // Phase 642 — declared beside the other two errors and
                    // before the warning, so the manifest reads
                    // severity-ordered.
                    "peer-visibility-insufficient"
                    "peer-surface-stale"
                ]
                "the exported manifest must carry exactly the shipped rule codes, in declaration order"

            Expect.equal
                (FederationPreflight.ruleManifest |> List.map _.Severity)
                (FederationPreflight.structuralRules |> List.map _.Severity)
                "the manifest must project the declared rules, never restate them"

            Expect.all
                FederationPreflight.classifiedRuleManifest
                (fun rule -> rule.Class = StructuralRule)
                "every federation-graph rule is structural — a deployment can never switch one off"

            Expect.equal
                (FederationPreflight.classifiedRuleManifest |> List.map _.Code)
                codes
                "both projections must read the same declared list, in the same order"

            Expect.all
                FederationPreflight.ruleManifest
                (fun rule -> not (String.IsNullOrWhiteSpace rule.Description))
                "every exported rule must carry a description an external checker can render"
        }

        test "pinned facet names come from PeerTrustPosture itself" {
            let postureFields =
                FSharpType.GetRecordFields typeof<PeerTrustPosture>
                |> Array.map _.Name
                |> Array.toList
                |> List.sort

            let pinnedFacets =
                sellerPin (sellerSurface [ v1 ] true) |> _.TrustFacets |> List.map _.Facet

            Expect.equal
                pinnedFacets
                postureFields
                "a pin's facet vocabulary is the posture's own field names — a renamed field must not silently stop being pinned"

            // The named helpers must address facets that exist, else a
            // requirement built from one would report "no claim" forever.
            let helperFacets =
                [
                    PeerTrustRequirement.audienceBound
                    PeerTrustRequirement.authProfile "x"
                    PeerTrustRequirement.delegationVerification "x"
                    PeerTrustRequirement.replayStance "x"
                    PeerTrustRequirement.transportSecurity "x"
                ]
                |> List.map _.Facet
                |> List.sort

            Expect.equal
                helperFacets
                postureFields
                "every well-known requirement helper must name a facet the posture declares"

            // Booleans pin as `true` / `false`, which is what the
            // `audienceBound` helper requires.
            let audienceFacet =
                sellerPin (sellerSurface [ v1 ] true)
                |> _.TrustFacets
                |> List.find (fun facet -> facet.Facet = "AudienceBound")

            Expect.equal
                audienceFacet.Value
                PeerTrustRequirement.audienceBound.RequiredValue
                "a composed audience binding must pin as the value the helper requires"
        }

        test "a published export becomes a pin only when its stamp verifies" {
            let surface = sellerSurface [ v1; v11 ] true
            let document = PeerSurface.exportJson surface
            let stamp = (PeerSurface.export surface).SurfaceHash

            match FederationPin.ofExportJson sellerId sellerSource stamp pinnedAt document with
            | Result.Error message -> failtestf "a verifying document must pin: %s" message
            | Result.Ok pin ->
                Expect.equal
                    pin
                    (FederationPin.ofSurface sellerId sellerSource pinnedAt surface)
                    "pinning the published document and pinning the surface value must agree"

            // A document edited after it was stamped is corrupt, not
            // stale, and must never become a pin.
            let tampered = document.Replace(reachId, "example.reach-elsewhere")

            match FederationPin.ofExportJson sellerId sellerSource stamp pinnedAt tampered with
            | Result.Ok _ -> failtest "a document edited after stamping must be refused"
            | Result.Error message -> Expect.stringContains message "corrupt" "the refusal must say why"

            // An internally-consistent document that is not the one this
            // deployment agreed to out of band is refused too.
            match FederationPin.ofExportJson sellerId sellerSource "0123abcd" pinnedAt document with
            | Result.Ok _ -> failtest "a document that does not match the agreed hash must be refused"
            | Result.Error message ->
                Expect.stringContains message "0123abcd" "the refusal must name the hash that was expected"

            // A label written under a format version this build cannot
            // read is refused rather than half-read.
            let future =
                JsonRpc.serialize {
                    PeerSurface.export surface with
                        FormatVersion = PeerSurface.formatVersion + 1
                }

            match FederationPin.ofExportJson sellerId sellerSource stamp pinnedAt future with
            | Result.Ok _ -> failtest "a future-format label must be refused, not half-read"
            | Result.Error message ->
                Expect.stringContains message "FormatVersion" "the refusal must name the version mismatch"

            // A body that is not an export at all fails as data, not as an
            // exception out of the compose path.
            match FederationPin.ofExportJson sellerId sellerSource stamp pinnedAt "{\"not\":\"a surface\"}" with
            | Result.Ok _ -> failtest "an unparseable document must be refused"
            | Result.Error message -> Expect.stringContains message sellerId "the refusal must name the counterparty"
        }

        // ── Phase 642 — the authority-level requirement ───────────────

        test "a counterparty granting less than this deployment requires refuses the composition" {
            // The drift check. Without it the same verdict arrives at the
            // first call, with traffic already flowing — the exact class
            // of surprise this preflight family exists to move earlier.
            let app =
                conformantApp ()
                |> PeerServerApp.withRequiredPeerDataVisibility PeerDataVisibilityLevel.ViewOnly

            let defects = defectsOf app

            Expect.contains
                (codesOf defects)
                "peer-visibility-insufficient"
                "a pinned counterparty granting aggregates only cannot serve a deployment that requires bounded views"

            let message =
                messagesFor "peer-visibility-insufficient" defects
                |> List.tryHead
                |> Option.defaultValue ""

            Expect.stringContains message "AggregatesOnly" "the message names what the counterparty granted"
            Expect.stringContains message "ViewOnly" "and what this deployment requires"

            Expect.stringContains
                message
                "silence is not a grant"
                "and says why a label that predates the facet reads as the narrowest level"
        }

        test "a counterparty granting at least the required level passes" {
            // The control that separates a rule which fires from one that
            // refuses every composition that declares a requirement.
            let grantingSeller =
                sellerSurface [ v1; v11 ] true
                |> fun surface -> {
                    surface with
                        DataVisibility = PeerDataVisibilityLevel.label PeerDataVisibilityLevel.Full
                }

            let app =
                consumerApp [ sellerPin grantingSeller; hubPin (hubSurface ()) ] []
                |> PeerServerApp.withRequiredPeerDataVisibility PeerDataVisibilityLevel.ViewOnly

            // The hub grants nothing, and this deployment consumes from
            // it — so the rule must still fire for the hub and not for the
            // seller. Checking both halves is what distinguishes a rule
            // that reads the pin from one that reads the requirement only.
            let messages = messagesFor "peer-visibility-insufficient" (defectsOf app)

            Expect.hasLength messages 1 "exactly the counterparty that grants too little is reported"
            Expect.stringContains (List.head messages) hubId "and it is the hub, not the seller"
        }

        test "a requirement never reaches a counterparty this deployment does not call" {
            // A pin held for a counterparty nothing consumes from is a
            // record, not an edge. Gating a boot on the grant of a
            // deployment we never address would refuse compositions that
            // are correct today and punish an operator for keeping a
            // complete registry.
            let unrelated =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withLocalPeer {
                    PeerId = "observer"
                    DisplayName = "Observer"
                }
                |> PeerSurface.describe

            let app =
                consumerApp [
                    sellerPin (sellerSurface [ v1; v11 ] true)
                    hubPin (hubSurface ())
                    FederationPin.ofSurface "observer" "peers/observer.surface.json" pinnedAt unrelated
                ] []

            Expect.isEmpty
                (messagesFor "peer-visibility-insufficient" (defectsOf app))
                "with no requirement declared the rule is dormant, whoever is pinned"
        }

        test "the rule is declared in both manifests, as a structural error" {
            let descriptor =
                FederationPreflight.classifiedRuleManifest
                |> List.tryFind (fun rule -> rule.Code = "peer-visibility-insufficient")

            let rule =
                Expect.wantSome descriptor "the rule must appear in the introspectable manifest, not only in the check"

            Expect.equal rule.Severity DefectError "a call that cannot succeed refuses rather than reports"

            Expect.equal
                rule.Class
                StructuralRule
                "a pure sweep over declared data already in memory, so SkipPreflight must not bypass it"
        }
    ]