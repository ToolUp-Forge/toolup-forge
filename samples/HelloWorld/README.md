# HelloWorld sample

End-to-end runnable demonstration of a ToolUp Platform deployment with one minimal module.

## Status

**Module shape: complete.** `HelloWorld.Module/` is the canonical 4-file module — `SharedTypes.fs`, `Server.fs`, `ClientModel.fs`, `ClientView.fs` + `Template.fsproj` + `Template.Client.props`. Renaming `Template` to `HelloWorld` throughout is a follow-up edit.

**Server + Client composition roots: not yet authored.** Per the docs in [`../../docs/platform/architecture.md`](../../docs/platform/architecture.md), the minimum composition roots are ~30 lines each. Authoring them + verifying `dotnet run --project HelloWorld.Server` starts cleanly + Vite builds the client + the module loads end-to-end is the work to finish this sample.

When complete, the structure will be:

```
samples/HelloWorld/
├── HelloWorld.Module/      # 4-file module + Module fsproj + .Client.props
├── HelloWorld.Server/      # minimal Giraffe composition root
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
