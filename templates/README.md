# ToolUp Platform SDK — `dotnet new` templates

Templates for scaffolding ToolUp.Platform consumers. Phase 11.B Step 3.

| Template | Purpose |
|---|---|
| `platformsdk-solution` | Full F# full-stack solution with primary `{AppName}-Server` + `{AppName}-Client` pair, one Starter module, `Build.fs`, `compose.yml`, CI workflow, `ToolUp.Sdk` `<PackageReference>` pre-wired. |
| `platformsdk-application` | Adds a second / Nth Server+Client pair to an existing `platformsdk-solution`. For multi-Application projects (e.g. seller / buyer testbed). |
| `platformsdk-module` | Four-file analysis module (`SharedTypes` / `Server` / `ClientModel` / `ClientView`) plus `.fsproj` + `.Client.props`. Compiles against `ToolUp.Platform.Core` only — proves the minimum-viable-module dependency floor. **In-tree shape**: the module lives inside the deployment. |
| `platformsdk-module-packaged` | The same module seam, as **its own repository shipped as a NuGet package**: packable project + `fable/` shadow project carrying the client tier as source, `Pack` into a configurable folder feed, `run.ps1`, CPM, tool manifest, README, licence placeholder — and **both conformance layers pre-wired**, so the discipline is the default rather than a retrofit. |
| `platformsdk-datamanager` | External data manager module: same shape as `platformsdk-module` but registers via `ExternalDataManager` mode and ships an `IDataSource` skeleton. |

## Install

```powershell
dotnet new install .\templates\platformsdk-solution
dotnet new install .\templates\platformsdk-application
dotnet new install .\templates\platformsdk-module
dotnet new install .\templates\platformsdk-module-packaged
dotnet new install .\templates\platformsdk-datamanager
```

## Port-clash enforcement

Each ToolUp application reserves a 10-port band for its server (5000-band) and Vite dev server (8080-band). When multiple applications live in adjacent directories, the bands must not clash — pre-validation can scan a chosen workspace root for existing reservations before scaffolding.

The `dotnet new` template engine has no native filesystem scan-and-reject. Pre-validation happens via `tools/ToolUp.PortGuard` (F# console tool) — wrapped by `New-ToolUpApp.ps1` for convenience:

```powershell
# Convenience wrapper — runs PortGuard first, then dotnet new
pwsh templates/New-ToolUpApp.ps1 platformsdk-solution -Name MyTestApp `
    --port-server 5010 --port-client-vite 8100

# Or call PortGuard directly when scripting
dotnet run --project tools/ToolUp.PortGuard -- `
    --server-port 5010 --client-port 8100 `
    --workspace-root C:\path\to\workspace
```

PortGuard exits with `1` on clash and `0` on no-clash. The wrapper aborts before invoking `dotnet new` on a non-zero exit, so a clash never writes files.

The plain `dotnet new platformsdk-solution -n Foo` invocation still works — it just skips pre-validation. Consumers writing scripts that drive the templates programmatically should call PortGuard themselves.

## Usage examples

```powershell
# New solution at the workspace default ports
pwsh templates/New-ToolUpApp.ps1 platformsdk-solution -Name MyTestApp `
    --port-server 5010 --port-client-vite 8100

# Add a second app pair to the same solution
cd MyTestApp
pwsh ../templates/New-ToolUpApp.ps1 platformsdk-application -Name MyTestApp2 `
    --port-server 5020 --port-client-vite 8110

# Add a module to the multi-app solution
dotnet new platformsdk-module -n MyTestModule -o src/Modules --app MyTestApp2

# Add an external data manager
dotnet new platformsdk-datamanager -n MyDataManager -o src/Modules --source api

# Scaffold a module that ships as its OWN repository + NuGet package
dotnet new platformsdk-module-packaged -n Contoso.Orders `
    --datatype OrderExport --feed ../local-nuget-feed --sdk-version 0.22.0
cd Contoso.Orders
pwsh ./run.ps1 -Pack
```

## Programmatic invocation

The acceptance bar requires byte-identical output from `dotnet new` and direct programmatic invocation. The templates are pure `dotnet new` artefacts with no scripted post-actions altering content, so calling the templating engine programmatically (e.g. `Microsoft.TemplateEngine.Edge`) yields the same files. Port-clash enforcement is a separate concern handled by PortGuard.

## Implementation notes

- All templates target `net10.0`. Every project is `IsPackable=false` except the module project in
  `platformsdk-module-packaged`, whose whole point is to pack.
- **Two classes of template, and the difference decides how each is gated.** The *root-inheriting*
  ones (`platformsdk-application`, `platformsdk-datamanager`, `platformsdk-module`) are project
  fragments that build against the repo's own `Directory.*.props` + `nuget.config`, so
  `VerifyTemplates` compiles them in place. The *standalone* ones (`safer`,
  `platformsdk-solution`, `platformsdk-module-packaged`) carry their own MSBuild roots and a
  literal `TOOLUP_SDK_VERSION` placeholder, and are not buildable in-repo without rewriting what
  makes them templates. `platformsdk-module-packaged` is the first of that class with a gate:
  `VerifyPackagedModuleTemplate` instantiates it into a scratch directory outside the repo and runs
  the scaffold's own pipeline end to end. See the root `CLAUDE.md` for what that gate checks
  beyond "it compiles".
- ToolUp.* packages are consumed via the `ToolUpSdkVersion` property in `Directory.Packages.props`; bump the property to bump every transitive ToolUp package.
- The `platformsdk-solution` template includes a `Directory.Build.props`, `Directory.Packages.props`, `nuget.config`, `global.json`, and `.gitignore` so the scaffold is a complete buildable repo root.
- Modules ship the same 4-file convention demonstrated by `samples/HelloWorld/HelloWorld.Module/`.
- Fantomas should be run against generated `.fs` files before `dotnet build` — the same pre-commit cadence applies.

## Future packaging

A `ToolUp.Templates` NuGet package will pack these four templates plus PortGuard so consumers can `dotnet new install ToolUp.Templates` without cloning the source tree. That's a follow-up; the current shape is source-tree-relative.
