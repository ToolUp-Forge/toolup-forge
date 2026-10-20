# Phase 782 — The reflection door: the deployment's self-description as AI tools and one CLI verb

**Applies to:** AI compositions that want an assistant to answer "what is composed here, what may it
reach, does it verify" without reading source; drivers and CI jobs that want the same answers without
a running server; tooling that reads one module's `ModuleSurface`.
**Breaking:** no wire change. Additive surface on `ToolUp.Platform.Server` (`ReflectionTools`, the
`StartupModes.Describe` case with its `--describe*` flags and the `DescribeRenderer` seam,
`CompositionInspectorHandler.readGate` / `panelJson` / `provenanceView` / `resolveSnapshot` /
`canonicalJson`, `DeploymentVerificationReport.readAllowed`), on `ToolUp.AI.Server`
(`AIServerApp.withReflectionTools`) and on `ToolUp.Cli` (`toolup describe`). `StartupModes.StartupMode`
gains a case: a consumer that matches it exhaustively gets an incomplete-match warning.
**Action required:** none. Nothing is registered and no behaviour changes until a composition opts in.

## What changes

1. **Three read-only AI tools** — `_platform.reflection.describe_composition`, `.verification_report`
   and `.module_reach` — returning the bytes the composition inspector, the verification report API
   and the seam-authority evidence already produce. Each applies its API's own gate (the inspector's
   Owner/Admin gate; the report's Platform-Admin gate as well for the report and the reach), and every
   call writes a `RemotingMethodAudited` row of kind `Custom:DeploymentDescribed`.
2. **A `--describe` startup mode**, beside `--verify-deployment`: the composition root composes, emits
   the documents as JSON (to `--describe-out <file>` or stdout) and exits without binding a listener,
   with the verification report's exit code.
3. **`toolup describe`** drives that mode against a composition root (`--project` / `--dll`), and
   `toolup describe --module` emits one built module's `ModuleSurface` with no composition root.

## Adopting it

Expose the tools to an AI composition:

```fsharp skip=fragment
AIServerApp.create factory providerProfile
|> AIServerApp.withConfig config
|> AIServerApp.withReflectionTools
|> AIServerApp.run
```

For a RAG composition, `RAGServerApp.mapAI AIServerApp.withReflectionTools`.

Describe a deployment offline (the root must be on a `ToolUp.Platform.Server` that ships the
`--describe` mode — earlier versions ignore the flag and start serving, and the CLI kills them after
`--timeout`):

```powershell
toolup describe --json --project src/App-Server
toolup describe --module bin/Release/net10.0/Acme.Sales.dll --json
```

A composition root composed through the lower-level `compose` entry point (rather than
`ServerApp.run`) registers no renderer; `--describe` there says so and exits `1`.

Full reference: [`docs/ai/reflection-tools.md`](../ai/reflection-tools.md).

## Verification

- `ToolUp.Platform.Tests` → `Phase 782 — reflection tools`: each tool byte-equals the API it fronts on
  a fixture composition; anonymous, a team Member and (for the report and the reach) an authenticated
  non-admin are refused; every call is an audit row; no composition carries the tools without the
  opt-in; the `--describe` renderer agrees with the tools.
- `ToolUp.Cli.Tests` → `Phase 782 — toolup describe`: parsing, the root command line and exit-code
  passthrough, and the module form's `surface` byte-equal to `ModuleSurface.describeJson` over the
  `platformsdk-module-packaged` template's own server registration.

## Rollback

Revert the phase's commits. Nothing is persisted in a new shape: the audit rows ride the existing
`RemotingMethodAudited` case.
