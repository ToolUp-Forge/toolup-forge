# HelloWorld sample

End-to-end runnable demonstration of a ToolUp Platform deployment with one minimal module: the
server, the client shell, and the built-in admin modules, in one command.

## Run it

```powershell
pwsh ./run.ps1
```

It restores the tools and the client's npm tree, compiles the client with Fable, then starts the
server, `dotnet fable watch` and the Vite dev server together. Open **http://localhost:8190**
(the server itself is on 5140; both are the sample's claimed ports, [`ports.json`](ports.json)).
Ctrl+C stops all three. `-SkipInstall` skips the restore and `npm ci` once they have run.

What you see: the SDK shell, signed in as **`dev-admin`**, a platform admin who owns the
**Hello World team**. The navigation rail on the left groups the pages:

- **Team Management** — **Teams** (the team manager: the team, its members, invitations),
  **Module Visibility** (the profile editor that curates which modules are surfaced), Permissions,
  Configuration, Usage and the Audit Trail.
- **Platform Management** — Platform Management itself (admins, every team, platform settings),
  Service Status, Tenant Lifecycle and the Composition inspector.
- **Debug** — the **Hello World** module: type a message and the server echoes it back.

**Ctrl+K** (Cmd+K on a Mac) opens the command palette over every page you can reach.

## The development identity

`run.ps1` sets `TOOLUP_PROFILE=helloworld-dev-identity`. That one variable switches both halves:

- **The server** (`HelloWorld.Server/Server.fs`) declares a configuration profile of that name in
  a **Debug build only**. Selected, it puts the deployment on the team surface, trusts the
  `X-User-Id` header (the SDK's development auth provider, named explicitly), makes `dev-admin`
  the platform admin and the owner of a bootstrap team, and states the acknowledgements a
  loopback developer machine needs. A **Release** build does not declare the profile, so selecting
  it there refuses startup and lists the profiles that exist: the identity cannot be on in a
  production build.
- **The client** (`HelloWorld.Client/vite.config.mts`) reads the same variable and, only when it
  names the profile, defines the team surface, the `dev-admin` header identity and the debug-only
  modules for the bundle. Without it, the client runs on the individual surface the server's
  `ServerConfigOverrides.referenceApp` declares.

Anyone who can reach the port can claim any identity on the header provider, which is why this is
a development switch and nothing more. A real deployment configures an identity provider
(`TOOLUP_AUTH_MODE=oidc`) and `TOOLUP_INITIAL_PLATFORM_ADMIN`.

## Structure

```
samples/HelloWorld/
├── HelloWorld.Module/      # the 4-file module + Module fsproj + .Client.props
├── HelloWorld.Server/      # the server composition root
├── HelloWorld.Client/      # the client composition root: Client.fs, Vite config, the styling block
├── ports.json              # the sample's claimed ports
└── run.ps1                 # both halves, one command
```

- **`HelloWorld.Module/`** is the canonical four-file module — `SharedTypes.fs`, `Server.fs`,
  `ClientModel.fs`, `ClientView.fs` + `HelloWorld.Module.fsproj` + `HelloWorld.Module.Client.props`.
- **`HelloWorld.Server/Server.fs`** composes `Surfaces.individual` through
  `ServerConfigOverrides.referenceApp` (the operator overrides it with `TOOLUP_PLATFORM_SURFACES`),
  registers the module with `ServerModule.create` + `withHandlers` (a console-printing
  `IRemotingTelemetry` sink and a per-call `ForgeAuthContext` resolver on its handler), and
  composes the server halves of what the shell opens: module-visibility profiles and the
  notification stream.
- **`HelloWorld.Client/Client.fs`** runs the SDK shell (`Client.run`) over the module, which arrives
  through the module's `.Client.props`. The shell adds its built-in admin modules itself. The
  stylesheet is the canonical Tailwind block (`client-styling/tailwind/`), embedded in `index.css`
  and held to its source by `client-styling/check-drift.ps1`.

To serve a production bundle instead of the dev server: `npx vite build` in `HelloWorld.Client`,
then run the server with `TOOLUP_PUBLIC_PATH` pointing at `HelloWorld.Client/dist`.

## Checked by

- **`VerifySamples`** builds all three projects, and transpiles the client with Fable in the full lane.
- **`VerifyBrowserSmoke`** builds both halves as `run.ps1` does, starts the server on the claimed
  port, and opens the team manager, the module-visibility editor, Platform Management and the
  command palette in a real browser, from the shell's own navigation
  ([`tests/BrowserSmoke/ToolUp.BrowserSmoke.Tests/ShellSmokeTests.fs`](../../tests/BrowserSmoke/ToolUp.BrowserSmoke.Tests/ShellSmokeTests.fs)).
  Stop a running `run.ps1` first: the scenarios need the same port.

## Static-corpus retrieval

The [`docs/`](docs/) folder doubles as a build-time-precomputed retrieval corpus:
the sample's own documentation is the knowledge base a docs-aware assistant
answers from. [`staticcorpus.json`](staticcorpus.json) configures the pack, and
[`docs/wiring.md`](docs/wiring.md) walks through packing the index
(`toolup-rag pack-docs`) and composing it via
`RagOptions.withRetrievalPipeline (StaticCorpusRetrievalPipeline.loadFromFile …)`.
Because the pipeline is overridden and no module contributes a
`VectorisationHandler`, the deployment carries no live-ingestion machinery — only
the per-query embedding runs at runtime. See
[`../../docs/rag/static-corpus.md`](../../docs/rag/static-corpus.md) for the full
reference. The generated `out/docs.scidx` is git-ignored.

## See also

- [`../../docs/platform/modules.md`](../../docs/platform/modules.md) — module convention.
- [`../../docs/platform/architecture.md`](../../docs/platform/architecture.md) — composition roots.
- [`../../docs/platform/README.md`](../../docs/platform/README.md) — overall SDK orientation.
- [`../MinimalClient/`](../MinimalClient/) — minimal Fable smoke-test sample (a plain component
  driven through the full `ToolUp.Platform.Client` source tree by `dotnet fable`).
- [`../MixedMode/`](../MixedMode/) — the per-module `Visibility` predicates across a mixed-mode deployment.
