# MyModule

A ToolUp Platform module distributed as its own NuGet package, in its own repository.

## What this shape is for

The in-tree module convention — four files plus a `.Client.props` the consuming client project
imports off disk — assumes the module lives *inside* the deployment. A module that ships to
deployments it does not own cannot do that. Its client tier has to travel as **source**, because
Fable compiles F# rather than IL, so the `.fs` files and the project file that orders them are
packed into the nupkg under `fable/` and compiled by the consumer's own Fable build.

That arrangement has four silent failure modes, every one of which is discovered by someone else:
a client file missing from the shadow project, a server-only file leaking into the Fable set,
compile-order drift between the two project files, and an asset with no packed path. None of them
fails this repo's build, its tests, or `dotnet pack` — unless something checks. This scaffold is
born with both checks already wired.

## Layout

```
MyModule/
├── run.ps1                          one-shot happy path
├── Build.fs / Build.fsproj          FAKE driver: Format / Build / Test / VerifyPackagedModule / Pack
├── Directory.Build.props            package metadata + version
├── Directory.Packages.props         central package management; one ToolUp.* pin
├── nuget.config                     nuget.org + the configured local feed
├── global.json                      SDK pin
├── LICENSE                          placeholder — replace before publishing
├── src/MyModule/
│   ├── SharedTypes.fs               both tiers: the id literals, the API record
│   ├── Server.fs                    server only — never packed under fable/
│   ├── ClientModel.fs               Elmish Model / Msg / init / update
│   ├── Icons.fs                     the module icon
│   ├── ClientRegister.fs            the view + the client registration
│   ├── icons/module-icon.svg        packed to fable/icons/
│   ├── fable/MyModule.fsproj        the SHADOW project — the compile list Fable follows
│   └── README.md                    packed as the package readme
└── tests/MyModule.Tests/
    ├── Contracts/ModuleContract.fs  the SDK's module conformance pack, vendored
    ├── ModuleConformanceTests.fs    the binding — this module's real registrations
    └── Program.fs
```

## The two conformance layers

Both run ahead of `Pack` in the target chain, so a release cannot reach the feed without them.

**The module seam** (`tests/MyModule.Tests`). Five laws over the module's two registrations:
server/client id parity, wire-`TypeName` uniqueness, `NeedsData` satisfiability, action
emitter-to-decoder coverage, and the top-level-namespace convention. The test binds the real
`Server.serverModule` and the real client chain — only the icon is substituted, because
`importDefault` is Fable-only and none of the five laws reads it.

**The packaging layout** (`VerifyPackagedModule`). Four laws over the two project files plus the
pack declarations: shadow-subset, server-exclusion, compile-order, asset-path. Pure comparison —
no MSBuild evaluation, no Fable invocation, no consumer app — so it runs in milliseconds, and
before anything is packed.

## First steps

```powershell
pwsh ./run.ps1            # format check -> build -> conformance -> tests
pwsh ./run.ps1 -Pack      # ... and pack into the configured local feed
```

Then, before the first publish:

1. Replace `LICENSE` with real licence text and check it matches `PackageLicenseExpression` in
   `Directory.Build.props`.
2. Set `Authors` in `Directory.Build.props`.
3. Replace the echo routine and the placeholder data type with your domain.

## Adding a client file

Three places, and the check names the one you missed:

1. `<Compile>` in `src/MyModule/MyModule.fsproj`, in the right position.
2. `<Compile>` in `src/MyModule/fable/MyModule.fsproj`, in the **same relative order**.
3. The `PackagePath="fable\"` content declaration in the main project.

A server-only file goes in (1) only, and is added to `ServerOnlyFiles` in `Build.fs` so the
exclusion law knows it is deliberate.

## Taking an SDK upgrade

Bump `ToolUpSdkVersion` in `Directory.Packages.props`, and re-copy
`tests/MyModule.Tests/Contracts/ModuleContract.fs` from the SDK's own test project — it is
vendored rather than referenced because the SDK's test project is not packable, which is the
documented adoption route for every SDK contract pack. Advance `<Version>` in
`Directory.Build.props` in the same commit as any change to what the package exposes.
