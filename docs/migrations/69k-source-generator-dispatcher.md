# Phase 69k — generated dispatch and generated decoders

> **Substrate status: the generator ships; nothing in your deployment changes.** `ToolUp.Remoting.Generator` is a **dev-time tool** that reads an assembly's API records and emits two things: closed-algebra response decoders, and a typed server-side argument-parse table. It is `IsPackable=false`, it runs when you run it, and no build or runtime path consults it. **There is no consumer action today.** This doc records what it does, the route it takes and why that route is not the one the phase originally named, so that a reader deciding whether to adopt it is deciding from the real design rather than from the plan.

## The headline, for a consumer: compile-time errors, not speed

Most consumers will never measure the perf difference at the application layer, and this doc will not pretend otherwise. What the generated path buys you is that a whole class of failure moves from **first request** to **build**:

- A response type whose wire shape the closed algebra cannot express is named at generation time, by type, with the reason — rather than decoding "successfully" through the open reflection reader and surfacing as a wrong value or a raw BCL exception.
- A method whose argument type changes without its handler changing is a compile error in generated source, not a `JsonException` on the first call that exercises it.
- The missing-authorisation-classification gate is already available at edit time — see [69k.F, below](#69kf-was-already-shipped-as-an-analyzer), which is the one task of this phase that turned out to be complete before the phase ran.

Cold-start and AOT remain real motivations, and both are unmeasured here (see [Deferred](#what-is-deliberately-not-here)).

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

## What you can do today

1. Run `census` against your own assembly. It tells you which of your API records could go onto the algebra path and, for the rest, exactly which wire type is blocking and why. That is useful on its own, before any adoption.
2. If a record comes back expressible and you want it on the algebra path, run `decoders` for it, commit the emitted file, and call its `registerAll ()` from your composition root. Review the diff — it is ordinary F#.
3. Declare what you registered to the facet by composing your generated `coveredApiRecords` with `PlatformDecoders.coveredApiRecords` and passing the result to `RemotingDecoderFacet.inspect`. The corpus-coverage flag is **yours to declare and defaults to `false`**: a deployment's own assertion that its decoder works is not evidence that it does, and the report renders `Observed` rather than `Verified` for it.

## Verification

The generator's contract pack runs inside the SDK's own test suite (`Remoting/GeneratorFidelityTests.fs`). What it proves, and the shape of the argument, is worth knowing before trusting generated output:

Generated decoders are source text, and the pack has no compiler, so it cannot execute them. What it does instead is close a chain across two already-checked claims. Phase 785's differential pack proves the **hand-written** decoders agree with the reflection reader over the wire corpus. This pack proves the **generator plans the same decode** for the same records — the same covered set (asserted executably against the shipped list, not transcribed), the same field positions and combinators for the shapes a generator could plausibly get wrong, the same union tags. Generated therefore agrees with reflection by composition, rather than by assertion.

A case at the end perturbs a pin and asserts the comparison rejects it, so the fidelity cases are known to be capable of failing. The perturbation is chosen to be the invisible kind — two same-typed fields swapped, which still decodes and which only the position catches.

## What is deliberately not here

| | Why |
|---|---|
| A NuGet package + automatic wiring into consumer builds | The generator is dev-time and its output is committed, so there is nothing for a build hook to do. Packaging is a deliberate later act. |
| The attribute-aware pre-flight chain emitted inline | The chain's stages sit behind private composition; emitting it means widening that surface, which is a bigger call than this phase should take. |
| Cold-start and per-request benchmarks | The generated dispatch path is not wired into the adapter, so there is nothing end-to-end to time. A benchmark of an unwired path measures the benchmark. |
| An AOT sample | Same reason: the claim is about a request path that is not yet served by generated code. |
| Tuple and multi-field-union-case combinators | Demand is recorded with counts above. The algebra's shipped surface is under proof concurrently, and widening it underneath that proof is the wrong order. |

## See also

- [69-family-overview.md](69-family-overview.md) — family map and adoption sequence.
- [The closed decoder algebra](../platform/remoting-decoder-algebra.md) — the combinators the generator composes.
- Generator: `src/ToolUp.Remoting.Generator/` — `Plan.fs` carries the design argument.
- Analyzer (69k.F): `src/ToolUp.Remoting.Analyzers/README.md`.
- Runtime contract: `src/ToolUp.Platform.Server/Server/Remoting/SourceGenDispatch.fs`.
