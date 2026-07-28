# Phase 582 — `IModuleContract` conformance pack (consumer migration)

**What changes.** A reusable contract pack — `ModuleContract` in `ToolUp.Platform.Tests` — asserts
five module-seam laws against any module's `(ServerModule, ErasedModule)` registration pair. A
module is registered twice, through two composition roots that never see each other, and the SDK had
nowhere to check that the two halves agree. This is that check. Mirrors Phase 285
(`IComponentRegistryContract`): parameterised laws over a witness, plus a self-test proving the pack
has teeth.

The laws:

1. **Server/client id parity.** `ServerModule.Name` and `ClientModule.Definition.Id` resolve to the
   same `ComponentId` (via Phase 580's `ModuleIdentity.componentIdOf`).
2. **Wire-`TypeName` uniqueness.** No two of the module's `DataType` / `DataTypeDisplay`
   registrations share an id — that id *is* the wire `TypeName`.
3. **NeedsData satisfiability.** The module's data gate is satisfied by the data types the
   composition advertises (by default, the module's own provides).
4. **Action emitter↔decoder key coverage.** Every `EmitsActions` declaration targeting this module is
   decoded by this module's `ActionDecoder`.
5. **Top-level-namespace convention.** Every type the module package exports sits under one declared
   namespace root.

Laws 1 and 2 read the Phase 581 `ModuleSurface` descriptor. Laws 3 and 4 cannot — `NeedsData` and
`ActionDecoder` are reported by `ModuleSurface` as `Opaque` precisely because they are *functions*
with no enumerable key set — so those two **probe** instead: the predicate is evaluated against the
advertised ids, and the decoder is called with each declared action key. That is a stated
approximation, not the full law (see "Known limits" below).

**Scope.** Test/build infrastructure only. No runtime surface, no public API, byte-for-byte absent
from any consumer build (GP 11 / GP 13). Consumer action is **optional but recommended**: one test
file per module repo.

## Adopting the pack in a module repo

```fsharp
open ToolUp.Platform.Tests.Contracts

let myModuleWitness =
    ModuleContract.witness (
        MyModule.Server.serverModule,          // the ServerModule registration
        MyModule.ClientView.register (),       // the ErasedModule registration
        "Contoso.Orders"                       // the declared namespace root
    )
    |> ModuleContract.withExportedTypes (
        ModuleContract.exportedTypesOf typeof<MyModule.SharedTypes.Order>.Assembly)

let tests = ModuleContract.laws "Contoso.Orders module" myModuleWitness
```

Three optional widenings, each of which makes a genuine declaration visible to a reviewer rather than
loosening the law silently:

| Chainer | When |
|---|---|
| `withExportedTypes` | always — the namespace law **fails** a witness that declares no exported types rather than passing vacuously. Pass an explicit `typeof<…>` list when the client tier is source-injected via `.Client.props` and therefore has no assembly of its own. |
| `withAvailableDataTypes` | the module legitimately consumes a data type ANOTHER module registers, so its `NeedsData` gate is not satisfiable from its own provides. |
| `withActionProbePayload` | the module's `ActionDecoder` validates its payload shape, so the default `"{}"` probe would report a decode failure that is really a probe artefact. |

## Packaging note — where the pack lives, and why you copy it

The pack ships at `src/ToolUp.Platform.Tests/Contracts/ModuleContract.fs`, beside the Phase 285
registry pack and every other contract pack. That project is `IsPackable=false` (the Phase 436
finding), so an external module repo **cannot `PackageReference` it** — as with all the existing
contract packs, an out-of-tree consumer copies the file into its own test project. The pack is
written to make that cheap: it depends only on `Expecto` plus the `ToolUp.Platform.*` packages a
module repo already references, and every value it reads comes from the witness (GP 9 — the SDK names
no module).

If the laws are ever wanted *outside* a test project, the Phase 436 precedent applies: an
Expecto-free `shouldConform`-shaped affordance would belong in `ToolUp.Platform.Server`, the package
every consumer already references. Not shipped by this phase.

## Two violations this found in the shipped sample

Both in `samples/HelloWorld` — the module every new module is copied from — and both fixed in the
Phase 582 commit:

1. **Id parity.** `HelloWorld.Server/Server.fs` registered `ServerModule.create "Hello World"` while
   the client's `ClientModule.create { Name = "Hello World" }` derived `Definition.Id = "HelloWorld"`
   (spaces stripped). The server's RBAC key and `ServerConfig.ModuleNames` entry therefore addressed
   a module id the client never used. Fixed by naming the server module with the id token
   (`"HelloWorld"`) and commenting why `Name` is an id, not a display name.
2. **Namespace root.** `HelloWorld.Module/Icons.fs` declared `module Toolup.HelloWorld.Module.Icons`
   while every sibling file sat under `HelloWorld.Module.*` — two top-level roots in one module
   package, which is exactly the collision law 5 exists to prevent. Renamed to
   `HelloWorld.Module.Icons` (one call site in `ClientView.fs`).

Neither had a symptom yet; both were the shape the laws describe.

## Known limits (stated, not papered over)

- **Laws 3 and 4 are probes.** The predicate / decoder key sets are not enumerable, so the laws
  evaluate the functions rather than reading a declaration. A gate or decoder with behaviour outside
  the probed inputs is not covered.
- **The reverse of law 4 is not assertable.** A decoder key no tool emits is invisible — there is no
  way to enumerate what a decoder accepts.
- **Cross-module action declarations are skipped.** A tool emitting into a *different* module is that
  module's binding to check, not this one's.
- **The `samples/HelloWorld` binding restates its two registration chains** rather than importing
  them: the sample's server registration lives inside `main`, and its client tier is `<None>` in the
  module fsproj (source-injected via `.Client.props`, and calling Fable-only interop), so it cannot be
  compiled into a .NET test assembly. The namespace law is not restated — it measures the sample's
  real compiled assembly. A module repo adopting the pack has no such split and binds its real values.

## Verification

- `Contracts/ModuleContract.fs`: `tests` (the `samples/HelloWorld` binding) and `referenceTests` (a
  synthetic conforming module) run green under `Build.fsproj -- VerifyAll`; `selfTests` binds seven
  deliberately non-conforming witnesses — id mismatch, duplicate `TypeName`, orphan `NeedsData` key,
  a decoder that rejects a declared key, a missing decoder entirely, a stray top-level export, and an
  undeclared export set — and proves each fails its law.

## Rollback

Revert the Phase 582 forge commit. No runtime code path changes; nothing outside the test project and
the sample references the pack. The two sample fixes are independently valuable and can be kept.

## SDK-ADOPTION

🟡 Optional — test/build infra. A module repo adopts it by adding one test file; no runtime or API
change is required of any consumer.
