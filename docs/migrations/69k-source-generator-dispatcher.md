# Phase 69k — generated dispatch and generated decoders

> **Substrate status: the generator ships as a build-time package; nothing in your deployment changes until you declare something.** `ToolUp.Remoting.Generator` reads an assembly's API records and emits two things: closed-algebra response decoders, and a typed server-side argument-parse table. Since Phase 804 it is a **`PackageReference`-able build-time package** (no runtime assembly, no dependencies, `PrivateAssets="all"`): its packed targets run the generator after the build of any project that declares what to emit, and are inert everywhere else. **The consumer action is one item declaration — see [Adopt](#adopt-phase-804).** This doc records what the generator does, the route it takes and why that route is not the one the phase originally named, and — since 804 — what was measured: the AOT proof and the generated-versus-reflection numbers.

## The headline, for a consumer: compile-time errors, not speed

Most consumers will never measure the perf difference at the application layer, and this doc will not pretend otherwise. What the generated path buys you is that a whole class of failure moves from **first request** to **build**:

- A response type whose wire shape the closed algebra cannot express is named at generation time, by type, with the reason — rather than decoding "successfully" through the open reflection reader and surfacing as a wrong value or a raw BCL exception.
- A method whose argument type changes without its handler changing is a compile error in generated source, not a `JsonException` on the first call that exercises it.
- The missing-authorisation-classification gate is already available at edit time — see [69k.F, below](#69kf-was-already-shipped-as-an-analyzer), which is the one task of this phase that turned out to be complete before the phase ran.

Cold-start and AOT were the stated motivations, and Phase 804 measured both rather than assuming them — see [Measured](#measured-phase-804). The short version: the generated **decode** path is the one that survives native AOT end to end, and it is not faster than the reflection reader on a warm JIT; the generated **argument table** is reflection-free only up to the System.Text.Json seam it calls, which native AOT does not serve for F# collections, unions or records.

## Why this is not a Roslyn source generator

The phase asked for `Microsoft.CodeAnalysis.CSharp` / "a custom F# source generator" emitting at compile time. **Roslyn source generators are a C#/VB compiler feature; the F# compiler does not run them.** There is no `ISourceGenerator` hook in `fsc`, no F# project in this repository references `Microsoft.CodeAnalysis`, and the F# ecosystem's analyzer SDK — which this repository already uses — is deliberately diagnostics-and-codefix only, with no compilation-unit emission at all.

So the route is build-time generation over **built metadata**, and the output is checked in and compiled like any other source. Two consequences are worth stating because they are improvements rather than concessions:

- **Field order comes from the same enumeration the writer uses.** Records go onto this wire positionally: `Write.writeRecord` emits `FSharpType.GetRecordFields` order, and `Decode.field name position` is positional for exactly that reason. A generator that parsed the *syntax* would be re-deriving that order from a second source, and two derivations of an ordering can disagree — silently, since a swapped pair of same-typed fields still decodes. Reading the same metadata cannot disagree with it.
- **The two risks the phase called its worst simply do not arise.** "Incremental build correctness" and "IDE integration" are properties of a generator that runs inside the compiler. Generated source that is committed has neither.

The cost is that generation is a deliberate act rather than an automatic one. Given that the emitted artefact is a wire decoder, that is the right trade: it is reviewable in a diff.

## What the generator emits

### Response decoders (the client seam)

For every wire type an API record's methods return, a `Decoder<'T>` composed from the [closed decoder algebra](../platform/remoting-decoder-algebra.md)'s combinators, plus the `RemotingDecoders.register<'T>` calls that mount them. The emitted module is the same shape as the hand-written `PlatformDecoders`: named decoders in dependency order, then `covered`, then `registerAll`, then `coveredApiRecords` — so a reader who has read one has read the other, and `RemotingDecoderFacet.inspect` consumes both through one function.

This matters beyond convenience: decoders composed from those combinators are covered by the algebra's totality result *by construction*, where a hand-written or bespoke reader would have to be argued about separately.

```pwsh
# From the repository root, against a built assembly.
dotnet run --project src/ToolUp.Remoting.Generator -- census `
    --assembly src/ToolUp.Platform.Core/bin/Debug/net10.0/ToolUp.Platform.Core.dll

dotnet run --project src/ToolUp.Remoting.Generator -- decoders `
    --assembly src/ToolUp.Platform.Core/bin/Debug/net10.0/ToolUp.Platform.Core.dll `
    --api-record ToolUp.Platform.IHealthMonitorApi `
    --open ToolUp.Platform `
    --out src/MyApp/GeneratedDecoders.fs
```

A wire type the algebra cannot express is **refused by name, with its reason**, and nothing is emitted for it — so it keeps the reflection path it already had. A generator that guessed would emit a decoder that compiles, runs, and is wrong, which is the one outcome worse than not generating.

### The typed argument-parse table (the server seam)

Per API record, a module whose every method has a generated argument parse going through the statically-typed `FableConverters.tryDeserialise<'inp>` seam rather than a reflective `MethodInfo` walk over a boxed `obj`, plus a `methods` manifest the adapter can register routes from without walking the record's fields at startup.

**Only the argument side is emitted, and this is deliberate.** Server arguments do not arrive as MsgPack — the binary reader has exactly one production call site, the client decoding the server's binary *response*; server arguments arrive as `Choice<byte[], JsonElement>` and decode through System.Text.Json. The decision recorded by Phase 785 is that the same algebra extends to JSON in a **follow-on phase**, and not over the JSON value model as it stands, whose numeric case cannot carry an `int64` past 2^53, an exact decimal, or a source width. So a generated argument decoder composed of algebra combinators today would be a decoder for bytes that never reach it. The emitted shape is the one that follow-on swaps a combinator-composed body into without moving a call site.

Handler invocation and result serialisation are **not** emitted: both sit behind private composition in the dispatcher and the adapter's HTTP plumbing, and emitting them would mean widening that surface.

## How far the algebra actually reaches — measured

The phase's stated trigger was "the composition-profile facet's `Reflection` record count". **That count is 0 today and structurally always will be.** The facet classifies the records a composition root *declares*, and the platform declares exactly the two records the hand-written set covers — both `Algebra`. A record nobody declared is not a `Reflection` binding; it is absent.

The quantity that does move is **coverage**, and the generator's `census` command measures it. Over `ToolUp.Platform.Core` at the time of writing:

| | |
|---|---|
| API records declared by the assembly | **36** |
| Wholly expressible in the closed algebra | **23** |
| Declared to the facet today (the hand-written set) | **2** |

The 13 that are not expressible are capped by exactly two gaps in the combinator set, and both are named by the census rather than worked around:

1. **Tuples** — `string * TeamRole`, `int * ModelExecutionRefusal` and friends. There is no tuple combinator.
2. **A union case carrying more than one field** — `Decode.payload` takes a single payload decoder. `ErasureError`, `FlagValue`, `ScheduleError`, `ConfigFieldKind`, `ModuleQueryError`, `ProvenanceQueryError`, `UserSchemaError`, `WebhookDeliveryOutcome` and `ColumnExpr` are all blocked on this one.

Neither is fixed here. Adding combinators to the algebra while a concurrent phase is proving the shipped set total would invalidate the surface that proof pins — so the census records the demand, with counts, and the extension is a separate deliberate act. A contract test asserts both refusal classes are still present, so it goes red the day either is closed.

`[<StringEnum>]` unions are refused for a different reason and one that is unlikely to change: the wire spelling of a case is Fable's casing rule plus any `[<CompiledName>]` override, and neither is recoverable from .NET metadata with enough confidence to emit.

## 69k.F was already shipped — as an analyzer

The phase asked the generator to emit compile-time diagnostics on a missing authorisation classification. **That shipped, separately, as `ToolUp.Remoting.Analyzers`**: `TUR0001` flags an API-record method carrying none of `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]`, with a fail-closed codefix, and its decision core is source-linked into the SDK's test pack so it cannot silently diverge from the runtime classifier it mirrors. `TUR0002` covers the audit heuristic, opt-in.

If you want the compile-time classification gate, that is the package to install — see its [README](../../src/ToolUp.Remoting.Analyzers/README.md). It is a strictly better delivery vehicle than a generator diagnostic would have been, because it works in the editor and does not require the consumer to adopt generated source.

## Reflection fallback compatibility

Unchanged, and structural rather than configured. `RemotingDecoders.tryGet` returning `None` **is** the reflection path — a type with no registered decoder decodes exactly as it did before. Registration is explicit (`registerAll`, called by a composition root), never a static initialiser, so a deployment can state whether the algebra path is live. A consumer that never runs the generator is byte-for-byte unchanged.

The same holds for the server side: `GeneratedDispatchRegistry` is untouched by this phase and still has no adapter consulting it.

## Adopt (Phase 804)

One line of reference and one item per emitted file, on the project whose **built assembly carries the API records** (the contract / shared-types project). The emitted file lands in the sibling that compiles it; add it to that sibling's `<Compile>` list and commit it like any other source.

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Remoting.Generator" PrivateAssets="all" />
  <ToolUpRemotingDecoders Include="..\MyApp.Client\Generated\Decoders.fs"
                          ApiRecords="MyApp.IOrdersApi;MyApp.ICustomersApi" Opens="MyApp" />
  <ToolUpRemotingDispatch Include="..\MyApp.Server\Generated\OrdersDispatch.fs"
                          ApiRecord="MyApp.IOrdersApi" Opens="MyApp" />
</ItemGroup>
```

Under central package management the version resolves through the `ToolUp.Sdk` manifest, so no `Version` attribute is needed. The item metadata — `ApiRecords`, `Opens`, `Namespace`, `Module`, `CorpusCovered` (decoders); `ApiRecord`, `Opens`, `Namespace` (dispatch) — is documented in the package's `build/ToolUp.Remoting.Generator.targets`. `<ToolUpRemotingGenerate>false</ToolUpRemotingGenerate>` silences a declaring project for one build. A project that declares no item is byte-for-byte unchanged; the reference alone does nothing.

The generator writes only when the text changed, so an unchanged assembly leaves the emitted file's timestamp alone and the sibling does not recompile. Inside this repository the same targets are imported by `Directory.Build.props`, so `samples/HelloWorld-AOT` dogfoods exactly the wiring a consumer gets.

**The `sdk-adoption.json` row.** Phase 804 is `consumer_facing`; a consumer records its stance in its own manifest per the workspace's derived-registry model:

```json
{ "refactor": "804", "status": "adopted", "sha": "<the commit that declared the items>" }
```

or `"status": "n-a"` with a reason for a consumer that has no MsgPack client and no AOT target — the generator buys such a consumer nothing.

**Before adopting, and in this order:**

1. Run `census` against your own assembly (`dotnet exec <package>/tools/net10.0/any/ToolUp.Remoting.Generator.dll census --assembly <your.dll>`). It tells you which of your API records could go onto the algebra path and, for the rest, exactly which wire type is blocking and why. That is useful on its own.
2. If a record comes back expressible and you want it on the algebra path, declare the `ToolUpRemotingDecoders` item for it, build, commit the emitted file, and call its `registerAll ()` from your composition root. Review the diff — it is ordinary F#.
3. Declare what you registered to the facet by composing your generated `coveredApiRecords` with `PlatformDecoders.coveredApiRecords` and passing the result to `RemotingDecoderFacet.inspect`. The corpus-coverage flag is **yours to declare and defaults to `false`**: a deployment's own assertion that its decoder works is not evidence that it does, and the report renders `Observed` rather than `Verified` for it.

Two emitter corrections landed with 804 and matter only if you generated before it: an emitted module now opens `System` and `ToolUp.Remoting` itself, so it compiles in any namespace you choose; and a record declared inside a module (a type spelled `Module.Record`) is constructed with an annotated record expression, so its labels resolve without that module being opened. Regenerate and the diff is those lines.

## Verification

The generator's contract pack runs inside the SDK's own test suite (`Remoting/GeneratorFidelityTests.fs`). What it proves, and the shape of the argument, is worth knowing before trusting generated output:

Generated decoders are source text, and the pack has no compiler, so it cannot execute them. What it does instead is close a chain across two already-checked claims. Phase 785's differential pack proves the **hand-written** decoders agree with the reflection reader over the wire corpus. This pack proves the **generator plans the same decode** for the same records — the same covered set (asserted executably against the shipped list, not transcribed), the same field positions and combinators for the shapes a generator could plausibly get wrong, the same union tags. Generated therefore agrees with reflection by composition, rather than by assertion.

A case at the end perturbs a pin and asserts the comparison rejects it, so the fidelity cases are known to be capable of failing. The perturbation is chosen to be the invisible kind — two same-typed fields swapped, which still decodes and which only the position catches.

## Measured (Phase 804)

### The AOT proof — `samples/HelloWorld-AOT`

A contract project links the remoting wire corpus's declarations and declares one echo method per corpus type; the console host decodes every pinned fixture under `tests/remoting-corpus/` through the generated decoders (the `.msgpack` side) and the generated argument table (the `.json` side), compares each against the corpus's own declaration, and is published with `PublishAot`. Measured 2026-09-15 on .NET 10.0.8 with MSVC 14.44 (the sample's README records the `vcvarsall` + `IlcUseEnvironmentalTools` incantation a machine needs when the VS installer has not registered the C++ workload with `vswhere`):

| | Result |
|---|---|
| Trim / AOT warnings (itemised, `TrimmerSingleWarn=false`) | 161 in total; **0** in the sample, the contract, either generated module, or `ToolUp.Platform.Core`; 104 in `ToolUp.Platform.Server`, all on the System.Text.Json converter set behind `FableConverters.tryDeserialise`; 57 upstream in FSharp.Core |
| Native run, generated **decoders** | `dynamic code supported: False`; **58 of 58** expressible fixtures decode to their declaration. The 10 refusals (tuples, multi-field union cases, `DateOnly` / `TimeOnly`, and the records holding them) are the algebra's recorded reach, and the sample fails if that set changes |
| Native run, generated **argument table** | **46 of 68** parse; the other 22 — every option, list, set, map, tuple, union and record — fail inside the reflection converter set the typed seam calls (no native instantiation for generic converters over value types; a reflective invoke for record / union construction). Under the JIT all 68 parse |

Two things the proof found that a green publish would not have. **F#'s `printf` family is a runtime failure under native AOT, not a build warning** — it builds its formatter through `MethodInfo.MakeGenericMethod`, so a `printfn "%d"` publishes with FSharp.Core's blanket warning and fail-fasts on its first call. The algebra's happy path had exactly one such call: `Decode.index` built its refusal path segment eagerly, as the argument to the `Result.mapError` function, so every successful decode through it ran `sprintf`; the first native run died in `asDecimal`'s four-word read. Moved into the refusal arm (the shape `list` and `entries` already had), and the decode path is now printf-free end to end. And **`PublishAot` turns System.Text.Json's reflection-based serialization off for the whole app, JIT builds included**, so the argument table is refused before it runs unless the app opts the switch back on — the sample does, explicitly, so the native run measures whether the seam survives AOT rather than whether a default forbids it.

So the reflection-free claim holds for the **decode** path, which is the client's binary path and the one the algebra was built for. The **argument** path is reflection-free only up to the seam it calls, which is the boundary Phase 785 recorded (785.F): the argument side decodes bytes that arrive as JSON, and the algebra's JSON extension is a separate phase. Until it ships, a native-AOT server cannot take the generated table past primitives, strings, dates, `Guid`, `byte[]` and `string[]`.

### Generated versus reflection — `src/ToolUp.Remoting.Benchmarks`

Stopwatch over the same corpus, Release, 16 logical cores, .NET 10.0.8, 2026-09-15. Cold start is the first pass in a **fresh process** per arm, min of five boots (a warm loop cannot see reflection's cache build or the generated path's registration and JIT); per request is min / median per-operation over nine rounds. Every operation is checked, not just timed.

| Path | Arm | Cold start (first pass) | Per request |
|---|---|---|---|
| Response decode, 58 expressible fixtures | generated | 40.2 ms | 0.57 / 0.59 µs per fixture |
| Response decode, 58 expressible fixtures | reflection | 34.8 ms | 0.54 / 0.55 µs per fixture |
| Dispatch, five methods (proxy build included) | generated | 69.6 ms | see below |
| Dispatch, five methods (proxy build included) | reflection | 101.2 ms | see below |

| Dispatch per request (argument parse + handler + serialise) | generated | reflection |
|---|---|---|
| `int` | 1.48 / 1.55 µs | 2.22 / 2.32 µs |
| `string` | 1.51 / 1.69 µs | 2.25 / 2.54 µs |
| `Address` (flat record) | 1.91 / 2.02 µs | 2.11 / 2.20 µs |
| `Address list` | 2.60 / 2.68 µs | 2.80 / 2.83 µs |
| `Consignment` (nested record) | 6.96 / 7.22 µs | 7.10 / 7.37 µs |

A decomposition of the `int` echo, for where the microseconds go: the argument-array parse (`JsonDocument` + clone) is ~0.45 µs, the generated typed parse ~0.30 µs, the handler bound in a task plus the serialise ~0.45 µs. The two arms are measured in the same JIT state — warmed together, a settle for tiered compilation, rounds interleaved — because the first cut of this harness timed them back to back and read the generated arm at 6.7 µs, a tier-0 artefact that vanished when the order was swapped.

Read them as the phase's own framing should: **the generated decoders are not faster than the reflection reader** — warm, both are half a microsecond per fixture (the reader's shape caches are hot and the algebra allocates a `Value` tree); cold, registration plus the JIT of the combinator closures costs about what the reader's cache build costs. The generated dispatch path is marginally ahead warm on primitives and level on records, and ~30 ms ahead cold — the proxy's build over 39 methods is the term that goes. The whole request through the composed pipeline is a measured 0.36 ms on the same machine (`perf-budgets.json`); none of the numbers above is a term that moves it. What the generated path buys is what the headline said: compile-time refusal, and a decode path that native AOT can run.

### 69k.C, decided — the middleware walk stays

The task asked for the attribute-aware pre-flight chain (auth → validation → idempotency → rate-limit → dispatch → audit → telemetry) emitted inline in the generated table as direct calls, or a recorded decision that the walk stays, with the measured cost as the evidence either way. **The walk stays**, on four grounds:

1. **The cost is not there.** The table's per-request cost is single- to low-double-digit microseconds either way, against a 360 µs whole-request hot path; the chain's per-stage dispatch is not the term that moves. Inlining direct calls would remove a few virtual dispatches from a path that is two orders of magnitude cheaper than the request.
2. **Cold start, where the generated path does win, is not improved by inlining.** The win is the proxy build over N methods, which the manifest already removes; the chain's stages are built once regardless.
3. **The stages sit behind private composition.** Emitting them widens the surface 69k escalated, and a table that inlines them has to be regenerated whenever a stage's semantics change — a coupling in the wrong direction, from the generated artefact into the runtime's internals.
4. **The AOT measurement puts the boundary elsewhere.** What stops a native server today is the argument seam, not the chain. Until the JSON follow-on makes the table the adapter's dispatch path, an inlined chain would be optimising a stage the request never reaches natively.

Revisit only if both change: the JSON follow-on lands and a request-level measurement names the chain. Neither is on the table.

## What is deliberately not here

| | Why |
|---|---|
| Tuple and multi-field-union-case combinators | Demand is recorded with counts above. Phase 800 owns both; the AOT sample's recorded refusal set is where their arrival will show. |
| The algebra over the JSON request wire | 785.F's decision stands: a separate phase, over a value model that can carry an `int64`, an exact decimal and a source width. Until it ships the generated argument table is reflection-free only up to the seam it calls, as measured above. |
| A `GeneratedDispatchRegistry` consumer in the adapter | The registry is unchanged since 69k. The typed table is the manifest and the parse; wiring it into the adapter's route registration is the JSON follow-on's companion, not a step to take while its parse cannot run natively. |

Closed by Phase 804: the package and the consumer wiring (69k.E), the AOT sample (69k.H), the benchmarks (69k.G), and the pre-flight-chain decision (69k.C).

## See also

- [69-family-overview.md](69-family-overview.md) — family map and adoption sequence.
- [The closed decoder algebra](../platform/remoting-decoder-algebra.md) — the combinators the generator composes.
- Generator: `src/ToolUp.Remoting.Generator/` — `Plan.fs` carries the design argument.
- Analyzer (69k.F): `src/ToolUp.Remoting.Analyzers/README.md`.
- Runtime contract: `src/ToolUp.Platform.Server/Server/Remoting/SourceGenDispatch.fs`.
