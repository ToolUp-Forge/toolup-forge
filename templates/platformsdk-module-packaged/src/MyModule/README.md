# MODULE_DISPLAY_NAME

A ToolUp Platform module, distributed as a NuGet package.

The package ships two tiers in one artefact:

- **Server** — the compiled assembly. `MODULE_NAMESPACE_ROOT.Server.serverModule` is the
  `ServerModule` registration a deployment appends to its module list.
- **Client** — F# **source**, under `fable/` in the package. Fable compiles F#, not IL, so the
  client tier travels as source and is compiled by the consumer's own Fable build. The shadow
  project `fable/MyModule.fsproj` is the compile list Fable follows.

## Consuming it

```xml
<!-- the consumer's server project -->
<PackageReference Include="MyModule" />
```

```fsharp
// the consumer's server composition root
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.addModule MODULE_NAMESPACE_ROOT.Server.serverModule
|> ServerApp.run
```

```fsharp
// the consumer's client entry point
let private modules: ErasedModule list = [ MODULE_NAMESPACE_ROOT.ClientRegister.register () ]

Client.run config modules
```

The consumer's Fable build picks the client sources up from the package automatically; the icon
assets under `fable/icons/` resolve through `vite-plugin-svgr`'s `?react` query at bundle time.

## Layout contract

| Path | Tier | Packed under `fable/` |
|---|---|---|
| `SharedTypes.fs` | both | yes |
| `Server.fs` | server only | **no** |
| `ClientModel.fs` | client | yes |
| `Icons.fs` | client | yes |
| `ClientRegister.fs` | client | yes |
| `icons/*.svg` | client asset | yes |
| `fable/MyModule.fsproj` | shadow project | yes |

That table is not documentation of intent — it is checked. `dotnet run --project Build.fsproj --
VerifyPackagedModule` runs ahead of `Pack` and fails on a client file missing from the shadow
list, a server file leaking into it, compile-order drift between the two projects, or a declared
asset with no packed path.
