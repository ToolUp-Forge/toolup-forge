module MODULE_NAMESPACE_ROOT.Tests.Conformance

open Feliz
open ToolUp.Platform.Tests.Contracts
open MODULE_NAMESPACE_ROOT

// ─── The module seam's conformance pack, bound ───────────────────
//
// The SDK's `ModuleContract` pack is a parameterised law set; this file
// is the whole of adopting it. A module is registered TWICE — once
// server-side, once client-side — through composition roots that never
// see each other, and nothing else checks that the two halves agree.
//
// The pack lives in the SDK's own test project, which is not packable,
// so it is vendored beside this file (`Contracts/ModuleContract.fs`) —
// the documented adoption route for an out-of-tree module repo. It
// depends on nothing but Expecto and the ToolUp packages this module
// already references.

/// The witness: this module's REAL registrations, not restatements.
///
/// The icon is the one substitution. `Icons.moduleIcon ()` calls
/// `importDefault`, which is Fable-only — it cannot be evaluated in a
/// .NET test process. `registerWith` exists so the rest of the chain
/// (id, view, data-type gate, group) is the shipped one; none of the
/// five laws reads the icon.
let private witness =
    ModuleContract.witness (
        Server.serverModule,
        ClientRegister.registerWith Unchecked.defaultof<ReactElement>,
        "MODULE_NAMESPACE_ROOT"
    )
    |> ModuleContract.withExportedTypes (ModuleContract.exportedTypesOf typeof<SharedTypes.EchoRequest>.Assembly)

/// Five laws: server/client id parity, wire-`TypeName` uniqueness,
/// `NeedsData` satisfiability, action emitter-decoder coverage, and the
/// top-level-namespace convention.
///
/// Two of them are widened by chainers when a module legitimately needs
/// it, and each widening is a visible declaration rather than a silent
/// loosening: `withAvailableDataTypes` when this module consumes a data
/// type ANOTHER module registers, and `withActionProbePayload` when the
/// action decoder validates payload shape and the default `"{}"` probe
/// would misreport.
let tests = ModuleContract.laws "MODULE_DISPLAY_NAME" witness