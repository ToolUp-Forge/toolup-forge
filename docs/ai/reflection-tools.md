# Reflection tools — the deployment describes itself

A running deployment already knows what it is. The composition inspector (Phase 593) serves its
governance declarations, the deployment verification report (Phase 686) renders every integrity
section with what it does **not** prove, and the seam-authority evidence (Phases 691 / 770) holds what
each composed component may reach and did reach. Phase 782 opens those three to two more readers:

- **an AI assistant**, as three read-only tools, so "what is composed here, what may each module
  reach, does it verify" is three tool calls rather than a read of the composition root; and
- **a driver or CI job**, as `toolup describe --json`, which emits the same documents from the
  composition root's own process without a running server.

Both doors call the projections the HTTP APIs already serve and serialise them with the one function
the inspector's export uses, so there is no fourth opinion about the deployment: a tool's output is
the bytes its API returns, and the CLI's documents are the tools' bytes.

## The three tools

| Tool | Returns | Gate |
|---|---|---|
| `_platform.reflection.describe_composition` | One inspector panel's canonical JSON — exactly what `ICompositionInspectorApi.ExportPanel` returns. `panel` (optional): `composition` (default), `surfaces`, `rules`, `disclosure`, `provenance`. | The inspector's Owner/Admin gate. |
| `_platform.reflection.verification_report` | The verification report record `IDeploymentVerificationApi.GetVerificationReport` returns, serialised. `sections` (optional): section ids to narrow to; `Outcome`, `VerdictDigest` and `NotProved` always describe the whole report. | The inspector's gate **and** the report's Platform-Admin gate. |
| `_platform.reflection.module_reach` | Per composed component: the declared seam grant, the reach its registrations imply (derived), and the reach its code resolved since boot (observed). | The inspector's gate **and** the report's Platform-Admin gate. |

In the provider's tool list the dots become underscores (`_platform_reflection_describe_composition`),
as for every platform tool.

### Composing them

The tools are an AI composition's opt-in:

```fsharp skip=fragment
AIServerApp.create factory providerProfile
|> AIServerApp.withConfig config
|> AIServerApp.withReflectionTools
|> AIServerApp.run
```

They ride the composition's ordinary tool accumulator, so `composeAI` registers them beside every
module tool, the tool-name collision check sees them, and a `ToolPolicy` reads their declared effects
(`read-content`) like any other tool's. A composition with no AI provider has no `AIServerApp` to call
this on and registers nothing; an AI composition that does not call it offers the model exactly the
tools it offered before.

### The gates, and why two of them are doubled

Each tool calls the API it fronts, so that API's gate applies as written rather than as a copy:

- `describe_composition` goes through `ICompositionInspectorApi.ExportPanel`, behind the inspector's
  Owner/Admin gate (`TeamRoles.canWriteTeamConfig` for a team member; refused for anonymous and
  publication-reader callers).
- `verification_report` and `module_reach` apply that gate first and then the report's own
  Platform-Admin gate (`AccessContext.canModifyPlatformConfig`). The report was already
  Platform-Admin-only; the inspector gate alone would widen it to team owners. Applying both means a
  tool never exposes anything either surface would refuse.

### What they never expose

- **No caller data.** Every document describes the deployment — its composition, its integrity
  verdicts, its components' reach. None reads a tenant's records, so there is nothing one tenant could
  learn about another (the Phase 593 rationale).
- **No secrets.** The inspector's knobs and the report's configuration-conformance section carry the
  redaction their APIs already apply; the tools add no path around it.
- **No new opinion.** No tool re-derives a verdict, recomputes a digest or re-renders a panel.

### Every call is on the ledger

Each call — served, refused, or rejected for a bad argument — writes one `RemotingMethodAudited` row
in the `_platform` scope with kind `Custom:DeploymentDescribed`, the tool name as `MethodName`, the
caller's subject id, and an `outcome` (`served`, `refused`, `invalidargument`, `unavailable`) plus the
panel or sections asked for. An assistant's self-inspection is therefore on the same trail as an
operator's. The report tool additionally writes the report's own `DeploymentVerified` row, because it
runs the report.

### Reading the reach honestly

`module_reach` is the seam-authority evidence the report's seam-authority section is gathered from.
Two absences are stated rather than rendered as zero:

- When the composition root supplied no seam-authority evidence
  (`DeploymentVerificationEvidence.withSeamAuthority`), `SeamAuthority` is `null` and `Note` says so.
  That is not a claim that components reach nothing.
- `ObservedReach` is `null` on a component when no runtime seam confinement is composed — nothing
  was observed. A component observed reaching nothing carries an empty list.

## The offline door: `toolup describe`

```powershell
toolup describe --json                                  # the project in the current directory
toolup describe --json --project src/App-Server         # a project
toolup describe --json --dll bin/Release/net10.0/App-Server.dll --section verification-report
```

The CLI runs the composition root in its `--describe` startup mode: the application composes, the
mode renders the requested documents and exits **without binding a listener**. The documents are:

| `--section` | Document |
|---|---|
| `composition` | the inspector panel (`--panel`, default `composition`) — the bytes `describe_composition` returns |
| `verification-report` | the verification report — the record `verification_report` returns, with `Actor` = `cli:--describe` |
| `module-reach` | the seam reach — the bytes `module_reach` returns |

One `--section` emits that document alone; none (or several) emits one JSON object keyed by section,
each document embedded verbatim. The exit code is the verification report's, as for
`--verify-deployment`: `0` when every composed section verified or was not composed, non-zero when a
composed section failed or would not answer; `2` for a usage error. The read is audited under the
`cli:--describe` actor. The offline door is ungated, as `--verify-deployment` is: whoever can run the
deployment's binary with its configuration already holds more than any document here.

The root's own startup logging goes to the CLI's stderr; stdout carries only the description. An
application on an SDK version that predates the startup mode ignores the flag and starts serving, so
the CLI kills it after `--timeout` seconds (default 300) and says why.

### One module, no composition root

```powershell
toolup describe --module bin/Release/net10.0/Acme.Sales.dll --json
toolup describe --module Acme.Sales --version 1.2.0 --json      # from the NuGet global packages folder
toolup describe --module Acme.Sales.1.2.0.nupkg --json
```

Loads the module and the `ToolUp.Platform.Server` it was built against into an isolated load context,
finds the `ServerModule` value it exports (a module-level `let`, or a parameterless function;
`--member Namespace.Module.value` when there are several) and emits:

```json
{ "assembly": "Acme.Sales.dll", "member": "Acme.Sales.Server.serverModule",
  "declaredBy": "module", "note": "…", "surface": { "…": "the ModuleSurface" } }
```

`surface` is `ModuleSurface.describeJson` over that value, computed by the module's own SDK version:
the server half of the descriptor the module contract pack reads. It is what the module **itself
declares** — anything a consuming composition root applies to the registration, and the client
registration (which lives in the Fable tier), are not part of it, and `note` says so. A module that
exports no `ServerModule` of its own — one whose registration is assembled inside its host, like
`samples/HelloWorld`'s — has no module-declared surface, and the command says so and exits `1`.

## Driver recipe

An agent orienting in a forge deployment should ask before it reads:

1. **Ask `describe_composition` before reading `Server.fs`.** The composition panel names every
   module, companion slot, data type, tool, metric, subject, purpose and composition knob by its
   stable id. `rules` adds the deployment's composition defects; `disclosure` its grounding envelope.
2. **Ask `module_reach` before reasoning about what a module can touch.** Declared against derived
   reach is the review question; observed reach is what actually happened since boot.
3. **Ask `verification_report` before claiming the deployment is sound.** Read the `NotComposed`
   sections and the `NotProved` statements as the report's boundary, not as a pass.
4. **Offline (CI, a driver with no running server):** `toolup describe --json` answers all three in
   one call, with the report's exit code.
