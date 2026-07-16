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
    ]