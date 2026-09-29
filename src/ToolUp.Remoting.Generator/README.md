# ToolUp.Remoting.Generator

A **build-time** generator for the ToolUp.Remoting wire. It reads an assembly's API records
(records whose every field is a function ending in `Async<_>`) and emits two kinds of ordinary
F# source you commit and compile like any other:

- **Response decoders** — a `Decoder<'T>` per wire type, composed from the closed decoder
  algebra's combinators, plus the `registerAll ()` that mounts them. A registered decoder is a
  closed composition with no reflective call on any path, which is what lets a client decode
  binary responses under `PublishAot`.
- **Typed argument-parse tables** — per API record, one typed parse per method through the
  statically-typed System.Text.Json seam, a `methods` manifest, and (since Phase 906) the
  record's generated **invocations** with a `register ()`: call it from the server's composition
  root before the remoting handler is built, and the remoting proxy serves every covered method
  through its typed call — inside the adapter's pre-flight chain, never beside it — without
  building the reflective endpoint for it.
- **JSON argument decoders** — per API record, a JSON algebra decoder for every argument type its
  methods take, registered scoped to the record: what the server's argument seam reads through.
- **Client proxies** — per API record, argument encoders, response decoders and a proxy builder
  that `Api.makeProxy` uses in place of the reflective proxy: no `createTypeInfo` when the proxy
  is built, no `Convert.serialize` per call.

There is no Roslyn source-generator route for F#, so generation runs over the **built** assembly —
which is also why field positions and union tags are the writer's own enumeration rather than a
second derivation of it.

## Adopt in one line

Reference the package as a development dependency and declare what you want emitted, on the
project whose assembly carries the API records:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Remoting.Generator" PrivateAssets="all" />
  <ToolUpRemotingDecoders Include="..\MyApp.Client\Generated\Decoders.fs"
                          ApiRecords="MyApp.IOrdersApi" Opens="MyApp" />
  <ToolUpRemotingDispatch Include="..\MyApp.Server\Generated\OrdersDispatch.fs"
                          ApiRecord="MyApp.IOrdersApi" Opens="MyApp" />
</ItemGroup>
```

The two JSON emissions are declared the same way:

```xml
<ItemGroup>
  <ToolUpRemotingJsonDecoders Include="..\MyApp.Shared\Generated\JsonDecoders.fs"
                              ApiRecords="MyApp.IOrdersApi" />
  <ToolUpRemotingClientProxies Include="..\MyApp.Client\Generated\ClientProxies.fs"
                               ApiRecords="MyApp.IOrdersApi" />
</ItemGroup>
```

Build. The files appear (or are left byte-identical when nothing changed); add them to the
sibling projects' `<Compile>` lists and call `GeneratedDecoders.registerAll ()` (and
`GeneratedJsonDecoders.registerAll ()` on the server, `GeneratedClientProxies.registerAll ()` in the
client) from the composition root. With no `Opens`, the JSON emissions open every namespace their
types reach. A project that declares no item is untouched, and
`<ToolUpRemotingGenerate>false</ToolUpRemotingGenerate>` opts a declaring project out for one build.

A wire type the algebra cannot express is refused **by name, with its reason**, and nothing is
emitted for it — it keeps the reflection path it already had. Run `census` to see how far the
algebra reaches over your records before adopting anything:

```pwsh
dotnet exec <package>/tools/net10.0/any/ToolUp.Remoting.Generator.dll census --assembly bin/Debug/net10.0/MyApp.Shared.dll
```

The package carries no runtime assembly and declares no dependencies; everything it needs to run
ships under `tools/`.
