module ToolUp.Platform.Tests.InProcess.PeerSurfaceTests

open System
open Microsoft.FSharp.Reflection
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

// ─── Phase 590 — PeerSurface derivation guard ────────────────────────
//
// The descriptor's whole value is that it is **derived from the composed
// peer registrations by construction** — so the load-bearing tests
// independently enumerate the registrations (from the typed contract
// declarations and the compose record's own fields, never through the
// descriptor's code path) and assert set-equality with what `describe`
// reports. Plus: the export is deterministic and hash-stamped
// (registration order never changes the hash; a new registration always
// does), routines mirror the job-substrate gate, and a non-federating
// deployment yields the empty surface without running a single contract
// builder (GP 11 / GP 13).

// ─── Reference federated composition ─────────────────────────────────

/// A served contract with one immediate and one long-running method.
/// NOT `private`: the host reflects via `FSharpType.IsRecord` without
/// the private-representation flag (see `PlatformPeerTests`).
type OrderContract = {
    PlaceOrder: string -> Async<string>
    ReconcileLedger: string -> Async<PeerJobHandle<int>>
}

/// A served contract with immediate methods only.
type CatalogueContract = {
    ListItems: unit -> Async<string list>
}

/// A contract this deployment *consumes* from an upstream counterpart.
type UpstreamDirectoryContract = {
    Lookup: string -> Async<string option>
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }
let private v11: ContractVersion = { Major = 1; Minor = 1 }

let private orderId = "example.orders"
let private catalogueId = "example.catalogue"
let private directoryId = "example.directory"

let private orderImpl: OrderContract = {
    PlaceOrder = fun order -> async { return $"placed:{order}" }
    ReconcileLedger =
        fun _ -> async {
            return {
                JobId = Guid.NewGuid()
                Poll = fun () -> async { return Completed 0 }
            }
        }
}

let private catalogueImpl: CatalogueContract = {
    ListItems = fun () -> async { return [ "widget" ] }
}

let private localPeer: PeerIdentity = {
    PeerId = "reference-instance"
    DisplayName = "Reference federated instance"
}

let private enabledConfig = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
        JobScheduler = InProcessJobScheduler
}

let private consumedDirectory =
    PeerSurface.consumes<UpstreamDirectoryContract> directoryId [ v1 ] "hub"

let private consumedAudit =
    PeerSurface.consumes<IPeerAuditApi> PeerAudit.contractId [ PeerAudit.v1 ] "any-counterparty"

/// The reference federated composition: two served contracts (one with a
/// long-running routine), the audit-transparency opt-in, and two
/// consumed-contract declarations.
let private referenceApp () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withLocalPeer localPeer
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<OrderContract> orderId [ v1; v11 ] fusion orderImpl)
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<CatalogueContract> catalogueId [ v1 ] fusion catalogueImpl)
    |> PeerServerApp.withPeerAuditTransparency
    |> PeerServerApp.withConsumedContract consumedDirectory
    |> PeerServerApp.withConsumedContract consumedAudit

// ─── Independent enumeration (never through the descriptor) ──────────

/// Long-running method names of a contract record type, enumerated
/// straight from the registration input (the typed contract), not the
/// descriptor: a field returning `Async<PeerJobHandle<'T>>` is a
/// routine.
let private longRunningMethods<'TApi> () : string list =
    FSharpType.GetRecordFields typeof<'TApi>
    |> Array.filter (fun field ->
        let ret = FSharpType.GetFunctionElements field.PropertyType |> snd

        let rec finalReturn (t: Type) =
            if FSharpType.IsFunction t then
                FSharpType.GetFunctionElements t |> snd |> finalReturn
            else
                t

        let asyncInner = (finalReturn ret).GetGenericArguments()[0]

        asyncInner.IsGenericType
        && asyncInner.GetGenericTypeDefinition() = typedefof<PeerJobHandle<_>>)
    |> Array.map _.Name
    |> Array.toList

let tests =
    testList "InProcess.PeerSurface (Phase 590)" [

        test "describe enumerates exactly the served contracts — ids, versions, routines" {
            let surface = PeerSurface.describe (referenceApp ())

            // Independent expectation, straight from the registration
            // inputs above (contract ids, version lists, and the typed
            // contracts' long-running fields via PeerJob.handlerName).
            let expected =
                [
                    orderId,
                    [ v1; v11 ],
                    (longRunningMethods<OrderContract> () |> List.map (PeerJob.handlerName orderId))
                    catalogueId,
                    [ v1 ],
                    (longRunningMethods<CatalogueContract> ()
                     |> List.map (PeerJob.handlerName catalogueId))
                    PeerAudit.contractId, [ PeerAudit.v1 ], []
                ]
                |> List.map (fun (id, versions, routines) -> id, List.sort versions, List.sort routines)
                |> List.sortBy (fun (id, _, _) -> id)

            let actual =
                surface.Serves.Contracts
                |> List.map (fun c -> c.ContractId, c.Versions, c.Routines)

            Expect.equal actual expected "the served set must equal the independently-enumerated registrations"

            Expect.equal
                surface.Serves.Endpoints
                PeerSurface.endpoints
                "the wire endpoints must be the v1 route templates"
        }

        test "describe enumerates exactly the consumed declarations" {
            let surface = PeerSurface.describe (referenceApp ())

            let expected = [ consumedAudit; consumedDirectory ] |> List.sortBy _.ContractId

            Expect.equal surface.Consumes expected "the consumed set must equal the withConsumedContract declarations"
        }

        test "every PeerServerApp registration field is accounted for by the descriptor" {
            // Drift guard for registration *kinds*: a new field on the
            // compose record is a new registration surface the descriptor
            // must learn (or explicitly exempt) before this list grows.
            let handled = [
                "Base" // config gate (PeerSubstrate / JobScheduler) → Enabled / Budgets
                "LocalPeer" // → LocalPeerId + TrustPosture.AudienceBound
                "Contracts" // → Serves.Contracts (materialised builders)
                "AuditTransparency" // → the reserved audit contract in Serves
                "ContractProfiles" // method-lifecycle overlay; served live at /peer/v1/capabilities/profile, not re-projected here
                "ConsumedContracts" // → Consumes
                // Phase 309 — deliberately NOT projected. It registers
                // nothing and changes nothing a counterparty can observe:
                // it only decides whether a missing `LocalPeer` is an
                // advisory or a compose-time refusal, and the posture it
                // guards is already reported as `TrustPosture.AudienceBound`.
                "StrictAudienceBinding"
                // Phase 343 — deliberately NOT projected either, and for
                // the mirror-image reason. It registers nothing and
                // describes nothing this deployment SERVES: it governs
                // how this deployment reads a counterparty's capability
                // profile on the OUTBOUND handshake. A `PeerSurface` is
                // the face a counterparty sees, and a counterparty cannot
                // observe how strictly we read its answers.
                "LegacyProfileFallback"
                // Phase 315 — NOT projected, and this one needs a
                // different argument from the two above, because unlike
                // them it IS observable: a caller that exceeds the
                // ceiling is refused `PeerRequestTooLarge`.
                //
                // It is still not part of the *face*. A `PeerSurface` is
                // hash-stamped and pinned by counterparties, so anything
                // in it is a thing they may rely on staying put — and a
                // body ceiling is receiver capacity policy that an
                // operator may retune at any restart without touching a
                // contract or its version. Projecting it would make
                // every such tweak invalidate every pinned copy, for a
                // value the caller learns from the structured refusal at
                // the exact moment it matters. If the surface ever wants
                // to advertise capacity, the natural home is
                // `PeerBudgetShape` alongside `LongRunningEnabled` — and
                // that is a `formatVersion` bump, which belongs to the
                // surface's own phase, not to this one.
                "WireLimits"
                // Phase 331 — NOT projected, on exactly the WireLimits
                // argument. It is observable (an over-ceiling budget is
                // clamped, an over-deep route refused `PeerHopLimitExceeded`)
                // and it is still receiver-side capacity policy an operator
                // retunes at a restart, not a term of the contract a
                // counterparty pins. The one field that is not merely
                // policy — `LocalPeerId` — is already projected, from
                // `LocalPeer`, which is where it comes from.
                "CascadePolicy"
                // Phase 311 — NOT projected, on the same argument, with
                // one extra wrinkle worth naming because it cuts the
                // other way. A composed clean-room floor is very
                // observable: a counterparty's answers come back
                // suppressed, or `PeerCleanRoomWithheld`, and unlike a
                // body ceiling the floor is arguably a term of the deal
                // rather than capacity policy — a clean-room's whole
                // point is that the floor is DECLARED.
                //
                // It stays out for now anyway, because putting it in
                // means putting `MinCohortSize` / `SuppressionThreshold`
                // / `PermittedShapes` into a hash-stamped descriptor
                // counterparties pin, and an operator tightening a floor
                // (which they should be free to do at any restart, and
                // which can only ever make the answer safer) would
                // invalidate every pinned copy. Advertising the floor
                // properly means a `formatVersion` bump and a decision
                // about whether a tightening is a breaking change — that
                // is the surface's own phase to make, not this one's.
                "CleanRoomTemplates"
                // Phase 312 — NOT projected, and this is the easiest
                // exemption of the set. The per-call deadline governs how
                // long THIS deployment waits on an outbound call; it
                // registers nothing a counterparty is served by and,
                // unlike `WireLimits`, it is not even observable from the
                // other end — a receiver that gets abandoned mid-call
                // sees a dropped connection, which is indistinguishable
                // from any other. It belongs with `LegacyProfileFallback`
                // as initiator-side policy, on the same argument: a
                // `PeerSurface` is the face a counterparty sees, and it
                // cannot see our patience.
                "TransportPolicy"
                // Phase 480 — PARTLY projected, and the split is the
                // honest one. The registration it makes — the reserved
                // `_platform.peer.template-approval` contract — IS in
                // `Serves`, on exactly the Phase 18a argument: it is a
                // contract a counterparty calls, so it belongs in the
                // face, and a counterparty that cannot see it cannot
                // submit an approval at all.
                //
                // What stays out is the approval STATE (who has signed
                // what, for which template version, under which validity
                // window). That is live registry data read through the
                // contract, not a compose-time registration — and it
                // changes every time either party signs anything, so
                // projecting it into a hash-stamped descriptor
                // counterparties pin would invalidate every pinned copy
                // on each approval. The skew tolerance stays out on the
                // `WireLimits` argument: receiver-side policy an
                // operator retunes at a restart.
                "TemplateApprovals"
                // Phase 591 — NOT projected, and this one is exempt for
                // a reason none of the others share: it is not a
                // statement about this deployment at all. The pin store
                // holds what OTHER instances published about themselves,
                // held here as inbound evidence to validate this
                // deployment's own consumed declarations against. A
                // `PeerSurface` is the face this instance presents; who
                // it has pinned is the opposite direction.
                //
                // It would also be actively wrong to publish. The pin set
                // names this deployment's counterparties, their label
                // hashes, and (through `RequiredTrust`) the postures it
                // insists on — a map of who a participant federates with,
                // handed to every counterparty that reads its face. A
                // federation's whole safety argument is that only the
                // wire faces are shared and the compositions stay
                // un-inspected; the counterparty SET is composition, not
                // face.
                "FederationPins"
                // Phase 190 — NOT projected, and the argument is
                // `CleanRoomTemplates`' one sharpened by a degree.
                //
                // A composed ε budget is observable in the strongest
                // sense: a counterparty that has spent its ceiling gets
                // `PeerCleanRoomWithheld` on every subsequent call, and
                // the ceiling is plainly a term of the commercial deal
                // rather than receiver capacity policy. So the case for
                // publishing it is better than for the floor.
                //
                // It stays out for exactly the reason the floor does,
                // and one more. A `PeerSurface` is hash-stamped and
                // pinned; putting the ceiling in it means an operator
                // TIGHTENING a budget — which can only make the
                // protection stronger, and which they should be free to
                // do at any restart — invalidates every pinned copy.
                // And the field it would have to carry is a *policy*,
                // not a *state*: the remaining budget changes on every
                // query, so anything counterparties could actually act
                // on is the half that cannot live in a stamped
                // descriptor at all. Advertising a budget properly means
                // a `formatVersion` bump plus a live read path, which is
                // the surface's own phase to make, not this one's.
                //
                // Note also that the ledger itself registers nothing a
                // counterparty calls: unlike Phase 480's approval
                // handshake there is no reserved contract here, so there
                // is nothing owed to `Serves` either.
                "PrivacyBudget"
                // Phase 481 — NOT projected, and the argument is
                // `PrivacyBudget`'s, one degree further along again.
                //
                // A composed noise policy is observable in the plainest
                // way of all: the answers come back randomised, and the
                // calibration (ε, δ, sensitivity, lattice) is a public
                // parameter of a public mechanism that a counterparty is
                // entitled to know — it is what tells them how much of
                // the variation in an answer is theirs and how much is
                // the mechanism's. So the case for publishing it is the
                // strongest in this list.
                //
                // It stays out for the same structural reason the two
                // above do, not for a privacy one. The policy is
                // compose-time *policy*, and an operator who tightens it
                // — spending less ε, drawing wider noise, which can only
                // make the release safer — should be free to do so at a
                // restart without invalidating every pinned copy of a
                // hash-stamped descriptor. Advertising the calibration
                // properly means a `formatVersion` bump and a decision
                // about whether a tightening is a breaking change, which
                // is the surface's own phase to make.
                //
                // In the meantime the calibration is not withheld from
                // the counterparty in any meaningful sense: it is
                // recorded on every gated decision row, and the bilateral
                // template-approval handshake (Phase 480) is where two
                // parties agree the terms of a clean-room release,
                // including what noise it carries.
                //
                // Like the ledger and unlike the approval registry, it
                // registers no contract, so nothing is owed to `Serves`.
                "NoisedReleases"
                // Phase 316 / 629 — NOT projected, on the `WireLimits`
                // argument almost unchanged. Retention IS faintly
                // observable: a caller polling a long-running result
                // after the record was reclaimed sees `Pending`, the
                // same answer it gets for a job that has not finished.
                // But it is receiver-side storage lifecycle an operator
                // retunes at a restart — and unlike a body ceiling it is
                // not even a term a caller could fit under, because the
                // poll window a caller cares about is minutes and the
                // default is thirty days. Projecting it would make a
                // retention tweak invalidate every pinned copy for a
                // value no counterparty can act on.
                "JobRetention"
                // Phase 483 / 629 — NOT projected, and this is the
                // easiest exemption since `TransportPolicy`. The
                // orchestrator is INITIATOR-side machinery: it fans out
                // rounds to participants over `IPeerFanout` and persists
                // its own run state. It registers no contract a
                // counterparty calls, mounts no route, and adds nothing
                // to the wire face — a participant in a round sees only
                // the ordinary contract calls the orchestrator makes,
                // which are already in `Consumes`. Composition, not
                // face.
                "RoundOrchestration"
                // Phase 338 / 629 — NOT projected, and this is the
                // exemption that is genuinely arguable rather than
                // obvious, so it gets the long version.
                //
                // The posture IS counterparty-relevant, more directly
                // than anything else on this list. Under
                // `ContractBoundCalls` a counterparty MUST mint through
                // `IssueScopedPeerToken` or every call it makes is
                // refused; under a composed replay guard its tokens are
                // single-use, so a retry of the same token fails. Those
                // are not observations about our internals — they are
                // terms a counterparty has to satisfy, which is exactly
                // what a face is for. And a home already exists:
                // `PeerTrustPosture.ReplayStance`, today the constant
                // `"freshness-window"`.
                //
                // It stays out because filling that field in is a
                // BREAKING change dressed as a strengthening, and the
                // mechanism that breaks is in this repo. A trust
                // requirement is an exact string match on a facet
                // (`PeerTrustRequirement.replayStance`), so a
                // counterparty that pinned `"freshness-window"` — the
                // only value that has ever existed — has its requirement
                // FAIL the moment this deployment composes a guard and
                // the stance reads `"freshness-window+single-use"`.
                // Strengthening a defence must not read as losing a
                // facet. Making the stance a lattice (a stronger posture
                // satisfies a requirement for a weaker one) is the fix,
                // and it is a `formatVersion`-class decision about the
                // requirement vocabulary — the surface's own phase to
                // make, exactly as it is for the clean-room floor and
                // the ε ceiling above.
                //
                // Until then the posture is not hidden: it is the one
                // thing in this list a counterparty learns immediately
                // and unambiguously from the structured refusal, at the
                // exact moment it matters — `missing 'cid'`, `missing
                // 'jti'`, or `replayed`, each naming what to do.
                "TokenPolicy"
                // Phase 630 — PROJECTED, but not by `describe`, which is
                // why it sits at the end of this list with an argument of
                // its own rather than beside an exemption.
                //
                // The map's presence is exactly what decides whether a
                // gateway can serve the long-running poll leg, and that
                // IS a term of the face: it is reported as
                // `PeerBudgetShape.LongRunningEnabled`, narrowed from the
                // members' floor by `PeerGateway.surface`. So nothing is
                // being withheld.
                //
                // `PeerSurface.describe` still must not read it, because
                // `describe` knows nothing about groups. A map composed on
                // an ordinary deployment fronts nothing — no aggregate was
                // ever derived, no forwarding contract registered — and
                // `describe` reading it would report a capability from a
                // field that, on its own, registers nothing at all. The
                // gateway is the only thing that knows both halves, so the
                // gateway is where the two are joined.
                "GroupJobMap"
                // Phase 642 — the data-visibility authority level, read by
                // `describe` and published verbatim as `DataVisibility`.
                // It belongs in the face on §8's inclusion test: a
                // counterparty acts differently on it, because a modeller
                // needing bounded views cannot use a data host that grants
                // aggregates only.
                "DataVisibility"
                // Phase 644 — the registry lifecycle-transition grant,
                // read by `describe` and published verbatim as
                // `TransitionAuthority`. In the face on the same §8 test,
                // and a SEPARATE axis from the level above rather than a
                // rung on it: a modelling deployment whose workflow ends
                // in a cross-peer approval cannot use a data host that
                // admits none, and it needs to learn that before it fits
                // a model it then cannot promote.
                "TransitionAuthority"
            ]

            let actual =
                FSharpType.GetRecordFields typeof<PeerServerApp>
                |> Array.map _.Name
                |> Array.toList

            Expect.equal
                actual
                handled
                "PeerServerApp gained/lost a registration field the PeerSurface descriptor does not account for — teach PeerSurface.describe about it, then update this list"
        }

        test "the audit-transparency entry matches the live registration" {
            // Derive the expected id + versions from the actual Phase 18a
            // registration (an inert IAuditLog is enough to build it) —
            // if the host's registration drifts, this fails.
            let inertAudit =
                { new IAuditLog with
                    member _.Record(_, _) = async.Return()
                    member _.GetAuditTrail(_, _, _) = async.Return []
                }

            let live = PeerAuditContractHost.registration inertAudit
            let surface = PeerSurface.describe (referenceApp ())

            let described =
                surface.Serves.Contracts |> List.find (fun c -> c.ContractId = live.ContractId)

            Expect.equal
                described.Versions
                live.Versions
                "the descriptor's audit-contract versions must match the live registration"
        }

        test "routines are advertised only when the job substrate is composed" {
            let noScheduler =
                referenceApp ()
                |> PeerServerApp.withConfig {
                    enabledConfig with
                        JobScheduler = NoJobScheduler
                }

            let surface = PeerSurface.describe noScheduler

            let order = surface.Serves.Contracts |> List.find (fun c -> c.ContractId = orderId)

            Expect.isEmpty order.Routines "an undispatched long-running method must not be advertised as a routine"

            Expect.equal
                (surface.Budgets |> Option.map _.LongRunningEnabled)
                (Some false)
                "the budget shape must report long-running as unavailable"

            let withScheduler = PeerSurface.describe (referenceApp ())

            let orderWith =
                withScheduler.Serves.Contracts |> List.find (fun c -> c.ContractId = orderId)

            Expect.equal
                orderWith.Routines
                [ PeerJob.handlerName orderId "ReconcileLedger" ]
                "a dispatchable long-running method must surface under its canonical handler name"
        }

        test "trust posture derives from the composition" {
            let surface = PeerSurface.describe (referenceApp ())

            match surface.TrustPosture with
            | None -> failtest "an enabled composition must carry a trust posture"
            | Some posture ->
                Expect.isTrue
                    posture.AudienceBound
                    "a composition with a LocalPeer identity binds inbound audiences (Phase 130)"

            let hostOnly =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withContract (fun fusion ->
                    JsonRpcPeerHost.contract<CatalogueContract> catalogueId [ v1 ] fusion catalogueImpl)

            let hostOnlySurface = PeerSurface.describe hostOnly

            Expect.equal
                (hostOnlySurface.TrustPosture |> Option.map _.AudienceBound)
                (Some false)
                "a composition without a LocalPeer identity cannot bind audiences"

            Expect.isNone hostOnlySurface.LocalPeerId "a host-only composition declares no local peer id"
        }

        test "export is deterministic and registration order never changes the hash" {
            let first = PeerSurface.exportJson (PeerSurface.describe (referenceApp ()))
            let second = PeerSurface.exportJson (PeerSurface.describe (referenceApp ()))
            Expect.equal first second "the same composition must export byte-identical JSON"

            // The same registrations in a different order — the sorted
            // canonical form must hash identically.
            let permuted =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withLocalPeer localPeer
                |> PeerServerApp.withContract (fun fusion ->
                    JsonRpcPeerHost.contract<CatalogueContract> catalogueId [ v1 ] fusion catalogueImpl)
                |> PeerServerApp.withContract (fun fusion ->
                    JsonRpcPeerHost.contract<OrderContract> orderId [ v11; v1 ] fusion orderImpl)
                |> PeerServerApp.withPeerAuditTransparency
                |> PeerServerApp.withConsumedContract consumedAudit
                |> PeerServerApp.withConsumedContract consumedDirectory

            let reference = PeerSurface.export (PeerSurface.describe (referenceApp ()))
            let permutedExport = PeerSurface.export (PeerSurface.describe permuted)

            Expect.equal
                permutedExport.SurfaceHash
                reference.SurfaceHash
                "registration order must not change the surface hash"

            Expect.equal
                reference.SurfaceHash
                (PeerSurface.export reference.Surface).SurfaceHash
                "re-stamping the same surface must reproduce the hash"
        }

        test "a new registration changes the hash — pinned counterparts detect staleness" {
            let reference = PeerSurface.export (PeerSurface.describe (referenceApp ()))

            let grown =
                referenceApp ()
                |> PeerServerApp.withContract (fun fusion ->
                    JsonRpcPeerHost.contract<UpstreamDirectoryContract> directoryId [ v1 ] fusion {
                        Lookup = fun _ -> async { return None }
                    })

            let grownExport = PeerSurface.export (PeerSurface.describe grown)

            Expect.notEqual
                grownExport.SurfaceHash
                reference.SurfaceHash
                "adding a registration must change the surface hash"

            Expect.isSome
                (grownExport.Surface.Serves.Contracts
                 |> List.tryFind (fun c -> c.ContractId = directoryId))
                "the new registration must surface with zero descriptor edits"
        }

        test "a non-federating deployment yields the empty surface at zero cost" {
            let disabled =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig {
                    enabledConfig with
                        PeerSubstrate = NoPeerSubstrate
                }
                // A booby-trapped builder proves describe never runs the
                // registrations on the strip-imports path (GP 13).
                |> PeerServerApp.withContract (fun _ ->
                    failwith "a NoPeerSubstrate describe must not materialise contract builders")
                |> PeerServerApp.withConsumedContract consumedDirectory

            Expect.equal
                (PeerSurface.describe disabled)
                PeerSurface.empty
                "NoPeerSubstrate must yield the empty surface"
        }

        test "consumes<'TApi> refuses a non-record contract type" {
            Expect.throws
                (fun () -> PeerSurface.consumes<string> "bogus" [ v1 ] "any" |> ignore)
                "a consumed declaration must be tied to a record contract type"
        }

        // Phase 594 — pinned data-vocabulary packs surface on the
        // cross-instance face (so the Phase 591 preflight can require
        // compatible pins across a contract edge), hashed + sorted, and
        // change the export hash (a counterparty detects a repin).
        test "pinned vocabulary packs surface on the descriptor and affect the hash" {
            let packA: DataVocabularyPack = {
                Id = "reference-core"
                Namespace = "reference"
                Version = { Major = 1; Minor = 0 }
                Entries = [
                    {
                        TypeName = "reference.Widget"
                        Description = "a widget"
                        Fields = []
                    }
                ]
            }

            let pinned =
                referenceApp ()
                |> PeerServerApp.withConfig {
                    enabledConfig with
                        PinnedVocabularyPacks = [ packA ]
                }

            let surface = PeerSurface.describe pinned

            Expect.equal
                (surface.PinnedVocabulary |> List.map _.PackId)
                [ "reference-core" ]
                "the pinned pack id surfaces on the cross-instance face"

            let pinEntry = surface.PinnedVocabulary |> List.exactlyOne
            Expect.equal pinEntry.Version packA.Version "the pinned version surfaces"
            Expect.equal pinEntry.Hash (DataVocabulary.hash packA) "the pin carries the pack's canonical hash"

            // Repinning a different version changes the surface hash — a
            // pinned counterparty detects the drift.
            let repinned =
                referenceApp ()
                |> PeerServerApp.withConfig {
                    enabledConfig with
                        PinnedVocabularyPacks = [
                            {
                                packA with
                                    Version = { Major = 2; Minor = 0 }
                            }
                        ]
                }

            Expect.notEqual
                (PeerSurface.export surface).SurfaceHash
                (PeerSurface.export (PeerSurface.describe repinned)).SurfaceHash
                "a changed pin must change the surface hash"

            // No pins surface as [] and match a deployment that pins nothing.
            Expect.isEmpty
                (PeerSurface.describe (referenceApp ())).PinnedVocabulary
                "a deployment that pins no pack surfaces no vocabulary pins"
        }
    ]