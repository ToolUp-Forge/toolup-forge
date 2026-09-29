# Phase 902 — the remoting verify takes its writer, and the reflection engine leaves Core

**What changes for you.** Two things, and a project that never called a generated module's
`verifyAll` / `registerAllVerified` has only the first:

1. **Regenerate your decoder modules** with the new `ToolUp.Remoting.Generator`. The generated
   `verifyAll` and `registerAllVerified` now take the gate as their first argument and are no longer
   wrapped in `#if !FABLE_COMPILER`, so a generated module compiles in a project that references
   `ToolUp.Platform.Core` alone, under .NET and under Fable.
2. **Pass the gate where you call them** — from a project that references `ToolUp.Platform.Server`,
   where the gate now lives.

A project that references the server tier and calls `RemotingDecoders.verify`,
`RemotingDecoders.registerVerified`, `DecoderShapes.draw`, `Write.makeSerializer`,
`Write.serializeObj`, `JsonDecoders.verifyWith` / `verifyBothWith` / `registerVerifiedWith` /
`browserOracle`, `BrowserJsonWriter.serialize` or `FableConverters.*` changes nothing in its
source: every one keeps its namespace and module path. What moved is the ASSEMBLY. Binary
compatibility breaks, as at any 0.x minor; rebuild against the new packages.

## The call-site diff

```diff
 // a server-side build gate over a generated MessagePack module
-match MyDecoders.registerAllVerified 64 801 with
+match MyDecoders.registerAllVerified RemotingDecoders.gate 64 801 with

-let outcomes = MyDecoders.verifyAll draws seed
+let outcomes = MyDecoders.verifyAll RemotingDecoders.gate draws seed

 // over a generated JSON-argument module
-MyJsonDecoders.verifyAll FableConverters.decoderOracle draws seed
+MyJsonDecoders.verifyAll (JsonDecoders.gateWith FableConverters.decoderOracle) draws seed
```

`RemotingDecoders.gate` checks against the shipped TypeShape writer. To check against another writer,
`RemotingDecoders.gateWith { Write = … }` (a `DecoderWriter`); the typed primitive is
`RemotingDecoders.verifyWith writer draws seed decoder`, and `verify` is it over the shipped writer.
`JsonDecoders.gateWith oracle` runs Phase 885's two-writer gate, exactly what the generated JSON
`verifyAll` ran before.

**A project that references Core alone and called `verify` / `verifyAll`** could only have done so on
.NET, from code that also needed the writer this phase moved. Move the call to the server project, or
add `<PackageReference Include="ToolUp.Platform.Server" />`.

## What moved, and what Core gained

| Member | From (Core) | To (Server) |
|---|---|---|
| `TypeShape`, `TypeShape_Utils` (`module internal`, as before) | `Shared/Remoting/MsgPack/TypeShape*.fs` | `Server/Remoting/MsgPack/TypeShape*.fs` |
| The .NET writer: `Write.makeSerializer`, `makeSerializerObj`, `serializeObj`, the `write*` primitives | `Shared/Remoting/MsgPack/Write.fs` | `Server/Remoting/MsgPack/Write.fs` |
| `DecoderShapes`; `RemotingDecoders.verify`, `registerVerified`, `DefaultDraws`, `DefaultSeed` | `Shared/Remoting/DecoderRegistry.fs` | `Server/Remoting/DecoderVerification.fs` |
| `JsonDecoderOracle`, `BrowserJsonWriter`; `JsonDecoders.verifyWith`, `verifyByTypeWith`, `browserOracle`, `BrowserLossPrefix`, `verifyBoth*`, `describeRefusal`, `registerVerified*With` | `Shared/Remoting/Json/JsonDecoderRegistry.fs` | `Server/Remoting/Json/JsonDecoderVerification.fs` |

`Write`, `RemotingDecoders` and `JsonDecoders` are split modules: Core keeps the half Fable compiles
(`Write.Fable`, the two registration tables) and the server tier declares a module of the same name,
which F# resolves as one. New: `DecoderGate` and `JsonDecoderGate` (Core — the gate as a value, a
function type), `RemotingDecoders.verifyThrough` and `JsonDecoders.verifyThrough` (Core — the call a
generated module emits), and in Server `DecoderWriter`, `RemotingDecoders.shippedWriter`,
`verifyByTypeWith`, `verifyWith`, `gateWith`, `gate`, and `JsonDecoders.gateWith`. The verification
records (`DecoderVerification`, `DecoderRefusal`, `JsonDecoderVerification`, …) stay in Core and are
no longer behind a guard.

## Reading the API baselines

`api-baselines/ToolUp.Platform.Core.approved.txt` loses 76 lines and gains 6;
`ToolUp.Platform.Server.approved.txt` gains 84. The moved members are the SAME members, read as
removals from one and additions to the other, plus the `(class)` entries of the three split modules
and the new Server members above. Core's six additions are `verifyThrough` twice and the four generated
`verifyAll` / `registerAllVerified`, whose only change is the gate argument.

Measured: Core's packed `fable/` source goes from 210 files, 73,441 lines, 3,539,239 bytes to 208
files, 69,704 lines, 3,402,919 bytes; its .NET-only arms from 4,741 lines in 44 arms across 11 files
to 358 lines in 35 arms across 5 files, every one a dual implementation.

## Verification

- `ArchitectureFitness` ("Phase 880 — no server-only region in the packed fable/ project") passes with
  one pin left, `Read.fs` (dual arms both tiers run).
- The Phase 801 and 841.C drift tests hold `PlatformDecoders.fs` / `PlatformJsonDecoders.fs` byte for
  byte to the generator's emission; the remoting wire corpus lists are unchanged.

## Rollback

Pin the previous SDK version and regenerate your decoder modules with its generator. No data, wire
format or stored state changed: the bytes on the wire and every decode are identical.
