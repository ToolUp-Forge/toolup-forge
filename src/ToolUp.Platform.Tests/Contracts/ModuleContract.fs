module ToolUp.Platform.Tests.Contracts.ModuleContract

open System
open System.Reflection
open Expecto
open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── Phase 582 — IModuleContract conformance pack ─────────────────────
//
// Phase 285 (`IComponentRegistryContract`) proved the pattern:
// parameterised contract laws + a self-test that shows the pack has
// teeth. This applies it to the MODULE seam. A module is registered
// twice — once server-side (`ServerModule`) and once client-side
// (`ErasedModule`) — through two independent composition roots that
// never see each other, and the SDK has no place to check that the two
// halves agree. This is that check, as a reusable law set every module
// (in-tree or packaged) binds in its own test project.
//
// The five laws:
//
//   1. **Server/client id parity.** `ServerModule.Name` is documented as
//      "must match the client `ClientModule.Definition.Id`" — it is the
//      RBAC key, the `ServerConfig.ModuleNames` entry, and the
//      `Model.ModuleStates` map key. Nothing enforced it. Phase 580's
//      `ModuleIdentity.componentIdOf` is the shared derivation, so the
//      law is: both tiers resolve the SAME `ComponentId`.
//   2. **Wire-`TypeName` uniqueness.** A module's `DataType.Id` IS the
//      wire `TypeName` its `Process` stamps onto the emitted
//      `ProcessedData`; two registrations sharing one collide silently.
//   3. **NeedsData satisfiability.** A module that gates its view on
//      data no composition can supply renders its empty state forever.
//   4. **Action emitter↔decoder key coverage.** A tool declaring
//      `EmitsActions` against its own module needs a matching
//      `ActionDecoder` — an undeclared decoder drops the action
//      silently (`ActionDeclaration`'s own doc comment says so).
//   5. **Top-level-namespace convention.** Two packages each exporting
//      a bare `DatasetView` cannot compose; every type a module package
//      exports must sit under one declared root.
//
// **The laws read the module's `ModuleSurface` (Phase 581), not ad-hoc
// reflection** — wherever the surface descriptor already enumerates the
// declaration (laws 1 and 2). Laws 3 and 4 cannot: Phase 581's outcome
// reports `NeedsData` and `ActionDecoder` as `Opaque` entries precisely
// because they are *functions*, so their key sets are not enumerable.
// What IS observable is the function itself, so those two laws PROBE:
// the `NeedsData` predicate is evaluated against the ids the composition
// advertises, and the `ActionDecoder` is called with each action key the
// module's own server-side `AITools` declare. That is a genuine
// approximation, not the full law, and its limits are stated at each law
// below rather than papered over.
//
// GP 9 — the SDK names no module here. Every value comes from the
// witness. This file ships no runtime surface (test/build infra only)
// and is byte-for-byte absent from any consumer build (GP 13).

// ── the witness ───────────────────────────────────────────────────────

/// Everything the module laws need, as data. A module repo binds this
/// once in its own test project against its real registrations; nothing
/// in the pack knows the module's name, types, or domain.
type ModuleConformanceWitness = {
    /// The module's server-tier registration.
    ServerRegistration: ServerModule
    /// The module's erased client-tier registration — what
    /// `ClientModule.register` returns.
    ClientRegistration: ErasedModule
    /// The single top-level namespace root every type the module package
    /// exports must sit under (e.g. `"Contoso.Orders"`).
    NamespaceRoot: string
    /// The types the module package exports. Derive them from the
    /// module's own assembly with `exportedTypesOf`, or list them
    /// explicitly when the client tier is source-injected into the
    /// consumer's assembly (the `.Client.props` pattern) and therefore
    /// has no assembly of its own.
    ExportedTypes: Type list
    /// The data-type ids the composition advertises to this module.
    /// Defaults to the module's OWN provided ids — the strictest
    /// reading, and the one the law is stated against. A module that
    /// legitimately consumes another module's data widens it here, and
    /// the widening is then a declaration a reviewer can see.
    AvailableDataTypes: string list
    /// The payload the action-coverage law hands the decoder for a given
    /// action key. Defaults to `"{}"`. A decoder that validates its
    /// payload shape needs a realistic sample here, otherwise the law
    /// reports a decode failure that is really a probe artefact.
    ActionProbePayload: string -> string
}

/// The public, non-compiler-generated types an assembly exports —
/// the usual derivation for the namespace-root law.
let exportedTypesOf (assembly: Assembly) : Type list =
    assembly.GetExportedTypes()
    |> Array.filter (fun t ->
        not (isNull t.FullName)
        && not (t.FullName.StartsWith("<", StringComparison.Ordinal)))
    |> List.ofArray

/// Build a witness from a module's two registrations and its declared
/// namespace root. `ExportedTypes` starts empty (declare them — the
/// namespace law refuses a witness that declares none), `AvailableDataTypes`
/// defaults to the module's own provides, and the action probe defaults
/// to an empty JSON object.
let witness
    (serverRegistration: ServerModule, clientRegistration: ErasedModule, namespaceRoot: string)
    : ModuleConformanceWitness =
    let surface =
        ModuleSurface.describeWith (serverRegistration, Some(box clientRegistration))

    let ownDataTypes =
        surface.Provides
        |> List.filter (fun e -> e.Kind = "datatype" || e.Kind = "datatype-display")
        |> List.map _.Key
        |> List.distinct

    {
        ServerRegistration = serverRegistration
        ClientRegistration = clientRegistration
        NamespaceRoot = namespaceRoot
        ExportedTypes = []
        AvailableDataTypes = ownDataTypes
        ActionProbePayload = fun _ -> "{}"
    }

/// Declare the types the module package exports (law 5).
let withExportedTypes (types: Type list) (w: ModuleConformanceWitness) = { w with ExportedTypes = types }

/// Widen the data-type ids the composition advertises to this module (law 3).
let withAvailableDataTypes (ids: string list) (w: ModuleConformanceWitness) = { w with AvailableDataTypes = ids }

/// Supply a realistic payload per action key for the decoder probe (law 4).
let withActionProbePayload (payload: string -> string) (w: ModuleConformanceWitness) = {
    w with
        ActionProbePayload = payload
}

// ── shared derivation ─────────────────────────────────────────────────

let private surfaceOf (w: ModuleConformanceWitness) : ModuleSurface =
    ModuleSurface.describeWith (w.ServerRegistration, Some(box w.ClientRegistration))

let private duplicatesOf (keys: string list) : string list =
    keys |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

let private underRoot (root: string) (t: Type) : bool =
    match t.FullName with
    | null -> false
    | name -> name = root || name.StartsWith(root + ".", StringComparison.Ordinal)

// ── the laws as standalone checks (raise on violation) ────────────────
// Each takes a witness and asserts one law with Expect.*, so it throws
// an AssertException on a non-conforming module — which is what the
// self-test below relies on to prove the pack has teeth.

/// Law 1 — the server registration's `Name` and the client
/// registration's `Definition.Id` resolve to the same `ComponentId`.
/// Two independent composition roots key on this token; when they
/// disagree, RBAC gates a module id the sidebar never renders and the
/// AI side-panel's `ActiveModule` payload addresses nothing.
let lawModuleIdParity (w: ModuleConformanceWitness) : unit =
    let serverId = w.ServerRegistration.Name
    let clientId = w.ClientRegistration.Definition.Id

    Expect.equal
        (ModuleIdentity.componentIdOf serverId)
        (ModuleIdentity.componentIdOf clientId)
        (sprintf
            "server/client module-id parity: ServerModule.Name = \"%s\" but ClientModule.Definition.Id = \"%s\". The server Name IS the client id token (an id left unset is derived from the client Name with spaces stripped) — pin them with `ClientModule.withId` or by naming the server module with the id."
            serverId
            clientId)

/// Law 2 — the wire `TypeName`s a module registers are unique within
/// the module. `DataType.Id` is the `TypeName` its `Process` stamps onto
/// the emitted `ProcessedData`, so a repeat is an unrecoverable
/// collision on the wire; the client-side display registrations carry
/// the same ids and are checked alongside.
let lawWireTypeNameUnique (w: ModuleConformanceWitness) : unit =
    let surface = surfaceOf w

    let keysOf kind =
        surface.Provides |> List.filter (fun e -> e.Kind = kind) |> List.map _.Key

    Expect.isEmpty
        (duplicatesOf (keysOf "datatype"))
        "wire-TypeName uniqueness: a DataType.Id is the wire TypeName stamped onto the emitted ProcessedData — two server registrations may not share one"

    Expect.isEmpty
        (duplicatesOf (keysOf "datatype-display"))
        "wire-TypeName uniqueness: two client DataTypeDisplay registrations may not share one data-type id — the second silently shadows the first"

/// Law 3 — the module's `NeedsData` gate is satisfiable by the data
/// types the composition advertises (by default, the module's own
/// provides).
///
/// **Probe, not enumeration.** `NeedsData` is
/// `((DataTypeId -> bool) -> bool)` — an opaque predicate, reported as
/// such by `ModuleSurface.Opaque`, with no enumerable key set. What is
/// observable is the predicate's VALUE, so the law evaluates it against
/// the available set. A module declaring no gate passes vacuously; the
/// failure message reports which ids are individually sufficient (a
/// second probe, one id at a time) so a violation names what the gate
/// would have accepted.
let lawNeedsDataSatisfiable (w: ModuleConformanceWitness) : unit =
    match w.ClientRegistration.NeedsData with
    | None -> ()
    | Some predicate ->
        let available = Set.ofList w.AvailableDataTypes

        let individuallySufficient =
            w.AvailableDataTypes
            |> List.filter (fun id -> predicate (fun candidate -> candidate = id))

        Expect.isTrue
            (predicate available.Contains)
            (sprintf
                "NeedsData satisfiability: the module's data gate is not satisfied by the advertised data types %A (individually sufficient: %A). A gate no composition can satisfy renders the module's empty state forever — register the data type, or widen the witness with `withAvailableDataTypes` when another module provides it."
                w.AvailableDataTypes
                individuallySufficient)

/// Law 4 — every client-side action a module's own tools declare is
/// decoded by that module's `ActionDecoder`. `ActionDeclaration`'s own
/// doc records the failure mode: "undeclared decoders drop silently".
///
/// **Probe, not enumeration.** `ActionDecoder` is
/// `(actionKey, payloadJson) -> Msg option`, reported as opaque by
/// `ModuleSurface` for the same reason as law 3. The declared side IS
/// enumerable (`AIToolDefinition.EmitsActions`), so the law drives the
/// decoder with each declared key. Only declarations targeting THIS
/// module are checked — a tool may legitimately emit into another
/// module, whose own binding of this pack covers it. The reverse
/// direction (a decoder key no tool emits) is not observable at all and
/// is deliberately not asserted.
let lawActionKeyCoverage (w: ModuleConformanceWitness) : unit =
    let ownModule = ModuleIdentity.componentIdOf w.ClientRegistration.Definition.Id

    let declared =
        w.ServerRegistration.AITools
        |> List.collect (fun (definition, _) -> definition.EmitsActions |> Option.defaultValue [])
        |> List.filter (fun action -> ModuleIdentity.componentIdOf action.ModuleId = ownModule)
        |> List.map _.ActionKey
        |> List.distinct

    if not (List.isEmpty declared) then
        match w.ClientRegistration.ActionDecoder with
        | None ->
            failtestf
                "action key coverage: the module's tools declare EmitsActions %A against this module, but the client registration has no ActionDecoder — every emitted action is dropped silently. Chain `ClientModule.withActionDecoder`."
                declared
        | Some decoder ->
            let undecoded =
                declared
                |> List.filter (fun key -> (decoder (key, w.ActionProbePayload key)).IsNone)

            Expect.isEmpty
                undecoded
                (sprintf
                    "action key coverage: the module's ActionDecoder returned None for declared action key(s) %A — those actions are dropped silently. (If the decoder rejected the probe PAYLOAD rather than the key, supply a realistic one with `withActionProbePayload`.)"
                    undecoded)

/// Law 5 — every type the module package exports sits under the single
/// declared namespace root. Two packages each exporting a bare
/// `DatasetView` cannot compose into one deployment.
///
/// A witness declaring no exported types FAILS rather than passing
/// vacuously — a law that is satisfied by declaring nothing is not a
/// law.
let lawNamespaceRoot (w: ModuleConformanceWitness) : unit =
    Expect.isFalse
        (String.IsNullOrWhiteSpace w.NamespaceRoot)
        "namespace-root convention: the witness must declare the module package's top-level namespace root"

    Expect.isNonEmpty
        w.ExportedTypes
        "namespace-root convention: the witness must declare the types the module package exports (`exportedTypesOf myAssembly`, or an explicit list when the client tier is source-injected)"

    let stray =
        w.ExportedTypes
        |> List.filter (underRoot w.NamespaceRoot >> not)
        |> List.map _.FullName
        |> List.sort

    Expect.isEmpty
        stray
        (sprintf
            "namespace-root convention: every exported type must sit under the declared root \"%s\" — a type outside it collides with any other package that picks the same top-level name."
            w.NamespaceRoot)

/// The reusable pack: run every module law against `witness`.
let laws (name: string) (w: ModuleConformanceWitness) : Test =
    testList $"{name} — IModuleContract" [
        testCase "server Name and client Definition.Id resolve to the same ComponentId"
        <| fun _ -> lawModuleIdParity w
        testCase "registered wire TypeNames are unique within the module"
        <| fun _ -> lawWireTypeNameUnique w
        testCase "the NeedsData gate is satisfiable by the advertised data types"
        <| fun _ -> lawNeedsDataSatisfiable w
        testCase "every declared self-targeted action key is decoded"
        <| fun _ -> lawActionKeyCoverage w
        testCase "every exported type sits under the declared namespace root"
        <| fun _ -> lawNamespaceRoot w
    ]

// ── a conforming reference module (the self-test's baseline) ──────────
//
// A synthetic module pair that satisfies all five laws. It exists to be
// MUTATED below — each mutation breaks exactly one law, which is how the
// pack proves it has teeth. Its `Model` / `Msg` also serve as the
// exported types the namespace law measures (this file's own root is
// `ToolUp`).

type ConformanceReferenceModel = { Ready: bool }

type ConformanceReferenceMsg = | ApplyBudget

let private referenceDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process =
        fun _ -> async {
            return
                { TypeName = id; Payload = "{}" },
                {
                    FileName = ""
                    DataType = id
                    ProcessedAt = DateTime.UnixEpoch
                    Info = None
                    Error = None
                }
        }
}

let private referenceAction: ActionDeclaration = {
    ModuleId = "conformance-reference"
    ActionKey = "apply-budget"
    Description = "apply the proposed budget"
    PayloadSchema = None
}

let private referenceTool: AIToolDefinition = {
    Name = "conformance_reference.run"
    Description = "reference tool"
    Parameters = []
    SourceModule = "conformance-reference"
    EmitsActions = Some [ referenceAction ]
    Location = ServerResident
    Surface = Both
}

let private referenceServer () : ServerModule =
    ServerModule.create "conformance-reference"
    |> ServerModule.withDataTypes [ referenceDataType "ConformanceSales" ]
    |> ServerModule.withAITools [ referenceTool, (fun _ _ -> async { return "" }) ]

let private referenceClient () : ErasedModule =
    ClientModule.create {
        Init = fun () -> { Ready = false }, Cmd.none
        Update = fun _ model -> model, Cmd.none
        Name = "conformance-reference"
        Icon = Unchecked.defaultof<ReactElement>
    }
    |> ClientModule.withView (fun _ _ -> Unchecked.defaultof<ReactElement>, Unchecked.defaultof<ReactElement>)
    // Phase 621 — the SDK's own canonical module declares both halves.
    // `withRequiredDataTypes` is the same gate `withNeedsData (fun has ->
    // has "ConformanceSales")` expressed once, so the predicate law 3
    // evaluates and the key set the surface descriptor reports cannot
    // drift apart; `withActionKeys` makes law 4's un-observable direction
    // (a decoded key no tool emits) enumerable for the first time.
    |> ClientModule.withRequiredDataTypes [ "ConformanceSales" ]
    |> ClientModule.withActionDecoder (fun (key, _) -> if key = "apply-budget" then Some ApplyBudget else None)
    |> ClientModule.withActionKeys [ "apply-budget" ]
    |> ClientModule.register

let private conformingWitness () =
    witness (referenceServer (), referenceClient (), "ToolUp")
    |> withExportedTypes [ typeof<ConformanceReferenceModel>; typeof<ConformanceReferenceMsg> ]

/// The pack bound to the synthetic reference module — the proof that a
/// module CAN satisfy all five laws at once, and the baseline every
/// mutation below is measured against.
let referenceTests = laws "conformance reference module" (conformingWitness ())

// ── self-test: the pack fails a non-conforming module ─────────────────
// One deliberately non-conforming witness per law, each mutating exactly
// one thing off the conforming baseline.

/// Client id no longer matches the server `Name` (violates law 1).
let private idMismatchWitness () =
    let w = conformingWitness ()

    let drifted = {
        w.ClientRegistration with
            Definition = {
                w.ClientRegistration.Definition with
                    Id = "conformanceReference"
            }
    }

    { w with ClientRegistration = drifted }

/// Two data types sharing one wire `TypeName` (violates law 2).
let private duplicateTypeNameWitness () =
    let w = conformingWitness ()

    let server =
        referenceServer ()
        |> ServerModule.withDataTypes [ referenceDataType "ConformanceSales"; referenceDataType "ConformanceSales" ]

    { w with ServerRegistration = server }

/// A data gate keyed on an id nothing in the composition provides
/// (violates law 3).
let private orphanNeedsDataWitness () =
    let w = conformingWitness ()

    let drifted = {
        w.ClientRegistration with
            NeedsData = Some(fun has -> has "NeverRegistered")
    }

    { w with ClientRegistration = drifted }

/// A declared action the module's decoder does not decode (violates law 4).
let private undecodedActionWitness () =
    let w = conformingWitness ()

    let drifted = {
        w.ClientRegistration with
            ActionDecoder = Some(fun _ -> None)
    }

    { w with ClientRegistration = drifted }

/// A module registration with no `ActionDecoder` at all, against a tool
/// that declares one (also violates law 4 — the other branch).
let private noDecoderWitness () =
    let w = conformingWitness ()

    {
        w with
            ClientRegistration = {
                w.ClientRegistration with
                    ActionDecoder = None
            }
    }

/// An exported type outside the declared root — the bare-`DatasetView`
/// collision shape (violates law 5).
let private strayTopLevelWitness () =
    conformingWitness ()
    |> withExportedTypes [ typeof<ConformanceReferenceModel>; typeof<Uri> ]

/// A witness that declares no exported types at all — the law must fail
/// rather than pass vacuously (violates law 5).
let private undeclaredExportsWitness () =
    conformingWitness () |> withExportedTypes []

let selfTests =
    testList "IModuleContract — self-test (pack has teeth)" [
        testCase "a module whose client id drifts from the server Name fails the parity law"
        <| fun _ ->
            Expect.throws (fun () -> lawModuleIdParity (idMismatchWitness ())) "an id-mismatched module must fail"

        testCase "a module registering a duplicate wire TypeName fails the uniqueness law"
        <| fun _ ->
            Expect.throws
                (fun () -> lawWireTypeNameUnique (duplicateTypeNameWitness ()))
                "a duplicate-TypeName module must fail"

        testCase "a module gating on an unprovided data type fails the satisfiability law"
        <| fun _ ->
            Expect.throws
                (fun () -> lawNeedsDataSatisfiable (orphanNeedsDataWitness ()))
                "an orphan NeedsData gate must fail"

        testCase "a module whose decoder rejects a declared action key fails the coverage law"
        <| fun _ ->
            Expect.throws
                (fun () -> lawActionKeyCoverage (undecodedActionWitness ()))
                "an undecoded declared action must fail"

        testCase "a module declaring actions with no decoder at all fails the coverage law"
        <| fun _ ->
            Expect.throws (fun () -> lawActionKeyCoverage (noDecoderWitness ())) "a missing ActionDecoder must fail"

        testCase "a package exporting a type outside its root fails the namespace law"
        <| fun _ ->
            Expect.throws (fun () -> lawNamespaceRoot (strayTopLevelWitness ())) "a stray top-level type must fail"

        testCase "a witness declaring no exported types fails the namespace law rather than passing vacuously"
        <| fun _ ->
            Expect.throws
                (fun () -> lawNamespaceRoot (undeclaredExportsWitness ()))
                "an undeclared export set must fail"
    ]

// ── the in-repo sample module as the reference binding ────────────────
//
// `samples/HelloWorld` is the canonical minimum module, and this is what
// adopting the pack looks like: one witness, one `laws` call.
//
// **Why the registrations are restated here rather than imported.** The
// sample's server registration lives inside `HelloWorld.Server`'s
// `main`, and its client tier (`ClientModel.fs` / `Icons.fs` /
// `ClientView.fs`) is `<None>` in the module fsproj — those files are
// source-INJECTED into a consuming Fable client project via
// `HelloWorld.Module.Client.props`, and they call Fable-only interop
// (`importDefault "./icons/chart.svg?react"`), so they cannot be
// compiled into a .NET test assembly at all. The two registration chains
// below therefore mirror the sample's source line-for-line, with the
// Fable render machinery (icon / view / init / update) replaced by inert
// stand-ins — the laws read declarations, not renderers.
//
// A module repo adopting this pack has no such split: it binds its OWN
// `Server.fs` / `ClientView.register ()` values directly, which is the
// shape `docs/platform/modules.md` documents.
//
// The namespace law is NOT restated — it measures the sample's real
// compiled assembly, reached through the `HelloWorld.Module`
// ProjectReference.

let private helloWorldServer () : ServerModule =
    // samples/HelloWorld/HelloWorld.Server/Server.fs
    ServerModule.create "HelloWorld"
    |> ServerModule.withHandlers [ Giraffe.Core.setStatusCode 200 ]

let private helloWorldClient () : ErasedModule =
    // samples/HelloWorld/HelloWorld.Module/ClientView.fs — `register ()`
    ClientModule.create {
        Init = fun () -> (), Cmd.none
        Update = fun (_: ConformanceReferenceMsg) model -> model, Cmd.none
        Name = "Hello World"
        Icon = Unchecked.defaultof<ReactElement>
    }
    |> ClientModule.withView (fun _ _ -> Unchecked.defaultof<ReactElement>, Unchecked.defaultof<ReactElement>)
    |> ClientModule.withAvailability DebugOnly
    |> ClientModule.withGroup "Debug"
    |> ClientModule.register

let private helloWorldWitness () =
    witness (helloWorldServer (), helloWorldClient (), "HelloWorld")
    |> withExportedTypes (exportedTypesOf typeof<HelloWorld.Module.SharedTypes.EchoRequest>.Assembly)

/// The pack bound to the in-repo sample module — wired into
/// `Build.fsproj -- VerifyAll`, so a regression in the sample (the shape
/// every new module is copied from) fails CI.
let tests = laws "samples/HelloWorld module" (helloWorldWitness ())