# HelloWorld sample

End-to-end runnable demonstration of a ToolUp Platform deployment with one minimal module.

## Status

**Module shape: complete.** `HelloWorld.Module/` is the canonical 4-file module — `SharedTypes.fs`, `Server.fs`, `ClientModel.fs`, `ClientView.fs` + `Template.fsproj` + `Template.Client.props`. Renaming `Template` to `HelloWorld` throughout is a follow-up edit.

**Server composition root: stub shipped (Phase 66 Stream F.5).** `HelloWorld.Server/Server.fs` demonstrates the canonical `Surfaces.individual` shape via `ServerConfigOverrides.referenceApp` — the `fromEnv` helper pins this app to the Individual deployment unless the operator overrides via `TOOLUP_PLATFORM_SURFACES`. The Module is not yet registered into the `ServerApp.ModuleNames` list — wiring the four-file module through `ServerModule.create + withGuardedApi` is the next follow-up, paired with the Template→HelloWorld rename.

**Client composition root: not yet authored.** Per the docs in [`../../docs/platform/architecture.md`](../../docs/platform/architecture.md), the minimum client composition root pairs with a Vite config + `package.json` + `index.html` — see `samples/MinimalClient/` for the in-tree Fable smoke-test shape and `samples/MixedMode/src/Client/` for the multi-module + `Visibility`-predicate shape this sample's Client will eventually adopt.

When complete, the structure will be:

```
samples/HelloWorld/
├── HelloWorld.Module/      # 4-file module + Module fsproj + .Client.props
├── HelloWorld.Server/      # Surfaces.individual composition root (shipped)
├── HelloWorld.Client/      # minimal Elmish + Feliz composition root + package.json + vite.config.mts
└── HelloWorld.sln          # wires Module + Server + Client
```

## Why it matters

The HelloWorld sample is the OSS repo's sole reference deployment. A new contributor / OSS adopter clones the repo, runs `dotnet run --project samples/HelloWorld/HelloWorld.Server` + `cd samples/HelloWorld/HelloWorld.Client && dotnet fable -o output --watch` (alongside `npm run dev`), opens `http://localhost:8080`, sees the module live. Without it, the repo has no "see it work in one terminal" path.

## Module fsproj rename

`HelloWorld.Module/Template.fsproj` ships as the legacy name from the import. Rename to `HelloWorld.Module.fsproj` (and the matching `Template.Client.props` to `HelloWorld.Module.Client.props`) as part of the sample-completion work.

## See also

- [`../../docs/platform/modules.md`](../../docs/platform/modules.md) — module convention.
- [`../../docs/platform/architecture.md`](../../docs/platform/architecture.md) — composition roots.
- [`../../docs/platform/README.md`](../../docs/platform/README.md) — overall SDK orientation.
- [`../MinimalClient/`](../MinimalClient/) — minimal Fable smoke-test sample (trivial Elmish counter that drives `dotnet fable` end-to-end through the full `ToolUp.Platform.Client` source tree; Client-tier phase-boundary check, complementary to this end-to-end deployment sample).
