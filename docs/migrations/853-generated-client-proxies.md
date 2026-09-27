# Phase 853 — generated client proxies and encoders

**What changes for you: nothing at a call site.** `Api.makeProxy<'TApi>` is generated-first. For
the platform's own API records it now returns a GENERATED proxy (`PlatformClientProxies`, checked
in): a record of closures over generated argument encoders and response decoders, built with no
reflection and serialising each call with no `Convert.serialize`. Any other record gets the reflective
proxy it always had — byte for byte on the wire — built on its FIRST CALL instead of at import.
`withBinarySerialization` and `withMultipartOptimization` proxies stay reflective. The surface grows
additively: `JsonEncode` / `JsonEncoder<'T>` (`ToolUp.Platform.Core`); `GeneratedProxies`,
`Proxy.generatedApi` / `generatedMethod`, `Remoting.buildLazyProxy`, `Api.tryGenerated` /
`Api.resolveProxy` and `PlatformClientProxies` (`ToolUp.Platform.Client`); the generator's
`Plan.forJsonEncoders` / `forJsonResponseDecoders` / `forClientProxies` / `clientNamespaces` /
`namespacesReachedBy` / `readPoliciesOf`, `Emit.clientCompilationUnit` / `clientSkipReport` and the
`Client*Plan` / `ClientEmitOptions` records.

## Generating your own records' proxies (opt in)

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Remoting.Generator" PrivateAssets="all" />
  <ToolUpRemotingClientProxies Include="..\MyApp.Client\Generated\ClientProxies.fs"
                               ApiRecords="MyApp.IOrdersApi" />
</ItemGroup>
```

Add the emitted file to the client project's `<Compile>` list and call, once, at composition:

```fsharp skip=fragment
GeneratedClientProxies.registerAll ()
```

A module-level proxy made before that line still resolves to the generated one, on its first call.
`[<Cacheable>]` / `[<Invalidates>]` are emitted as the record's `ReadPolicies.register`, so drop the
hand-written `ReadPolicies.registerFor<IOrdersApi>` for a generated record. The server-side twin
(Phase 841's argument decoders) is now reachable too:
`<ToolUpRemotingJsonDecoders Include="…" ApiRecords="…" />`, then `GeneratedJsonDecoders.registerAll ()`.

## What moves on the wire

A generated call's request body is the System.Text.Json writer's form (`"+42"` for an `int64`, no
spaces); the reflective one's is Fable.SimpleJson's. The server reads both to the same value, through
the algebra decoder and through the converter set (853.D, over seeded draws of every platform
argument type). Two identical declared reads from a generated and a reflective proxy of one record no
longer share one in-flight request, since their bodies differ.

## Measured (ClientBench, node v25.9.0 win32/x64, production React, seed 849001, a loaded machine)

| | before | after |
|---|---|---|
| reflective proxy builds at SDK-shell import | 45 | **0** (43 generated, 2 deferred to first call) |
| SDK-shell import, min of 3 (three runs each) | 999 / 950 / 825 ms | 816 / 850 / 948 ms |
| MinimalClient boot to first render, min of 6 | 999 / 1004 / 895 ms | 870 / 915 / 924 ms (it builds no proxy either way) |
| request body per call, 3 fixture arguments | 18.2 / 17.0 / 17.1 us (`Convert.serialize`) | 6.7 / 6.9 / 6.6 us (generated encoder) |

The build count is the phase's claim and it holds. The import WALL TIME does not measurably move:
Phase 849's "45 proxies, 1341 ms" was the whole shell import, not the proxies' share of it, and the
difference sits inside this machine's run-to-run noise. Per call, serialisation is ~2.5x cheaper.

## Verify, and roll back

`ToolUp.Platform.Tests --filter-test-list "Phase 853"` (pin, census, cross-host agreement) and
`VerifyFable` (the transpiled encoders and proxies). To roll back one record, opt its proxy out with
`Remoting.createApi () |> Remoting.buildProxy<IOrdersApi>`; to roll back a consumer's generation,
remove the item and the `registerAll ()` call.
