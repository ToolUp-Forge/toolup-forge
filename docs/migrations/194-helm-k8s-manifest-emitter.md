# Phase 194 — `toolup k8s emit` (Helm / Kubernetes manifest emitter)

**Class: additive.** No existing type, member, default or emitted artefact changed. A deployment
that never runs the new command is byte-for-byte unchanged (GP 13), and nothing this phase adds is
loaded at runtime — the emitter is build-time only.

## What changes

`ToolUp.Cli` gains one subcommand:

```
toolup k8s emit --manifest <deploy.json> [--profile <ProcessProfile[,...]>]
                [--format helm|flat] [--out <dir>] [--image <ref>] [--force]
```

It reads a deploy manifest, validates it, and writes either a Helm chart (`Chart.yaml`,
`values.yaml`, `.helmignore`, `templates/`) or a flat manifest set for `kubectl apply`. It is the
sibling of `toolup docker emit` and shares that command's machinery: embedded `{{token}}` templates
read through `Templating.readEmbedded`, the same all-or-nothing write (every target is checked for a
clash before any file is created), and the same `--force` escape.

New files:

| File | Role |
|---|---|
| `src/ToolUp.Cli/Cli/K8sEmitCommand.fs` | the command |
| `templates/platformsdk-k8s/*.template` | the emitted artefacts, embedded into the tool assembly |
| `src/ToolUp.Cli.Tests/K8sEmitTests.fs` | structural + parity tests |
| `docs/migrations/194-helm-k8s-manifest-emitter.md` | this file |

`src/ToolUp.Platform/technical-guide/14-docker-hosting.md` §Kubernetes no longer carries
hand-copiable YAML; it points at the command and documents where each emitted value comes from.

## Adopting it

Nothing to migrate. To use it, write a deploy manifest and run the command:

```json
{
  "schemaVersion": 1,
  "app": { "name": "Acme Portal", "slug": "acme-portal", "region": "eu-west" },
  "runtime": {
    "framework": "dotnet:10",
    "image": "ghcr.io/acme/portal:v1.4.2",
    "healthcheck": { "path": "/health", "port": 5000, "initialDelaySeconds": 30, "intervalSeconds": 30 }
  },
  "secrets": [ { "name": "DATABASE_URL", "source": "vault://kv/prod/db-url" } ],
  "domains": [ { "hostname": "app.acme.example", "tlsMode": "acme-le" } ],
  "modules": [],
  "dependencies": []
}
```

```powershell
toolup k8s emit --manifest deploy.json --profile WebOnly,WorkerOnly --out ./chart
helm install acme ./chart          # after creating the Secret the chart references
```

The wire shape is `DeployManifest` field-for-field in camelCase. Its two `TimeSpan` fields are
carried as whole seconds (`initialDelaySeconds` / `intervalSeconds`) because Kubernetes probe fields
are integer seconds — the wire shape does not pretend to a precision the target cannot express.
Unknown top-level fields are preserved verbatim, so a manifest authored against a later schema
round-trips through this reader without losing data.

## The decisions worth knowing

- **The CLI mirrors `DeployManifest` rather than referencing it.** `ToolUp.Cli` is deliberately
  pure-BCL — its only package reference is `FSharp.Core`, and `ToolUp.Cli.Tests.fsproj` records why
  ("the `toolup` CLI itself stays pure-BCL"). So the manifest records, `isValidSlug` and `validate`
  are a mirror, and a parity test pins the mirror to the substrate over the whole error vocabulary,
  the schema version, and the `ProcessProfile` union. That is the mechanism `memberships doctor`
  already uses against `MembershipDoctor`. A fifth `ProcessProfile` case fails the parity test
  rather than being silently skipped by the emitter.
- **Probe values are derived, not invented.** Liveness hits the manifest's
  `runtime.healthcheck.path` (default `/health`); readiness hits `/ready`, which is not
  operator-overridable because it is a route the SDK mounts rather than a policy. `timeoutSeconds`
  is `IHealthCheck.defaultTimeout` (5s) for readiness and twice that for liveness — a readiness
  timeout de-lists a pod, a liveness timeout restarts it, so the more destructive verdict gets the
  wider margin. That reproduces the 10 / 5 split the technical guide hand-wrote, from one constant.
- **Worker and dispatcher roles are pinned to one replica.** `IDistributedLock` (Phase 9i) is
  unshipped, so a second worker replica duplicate-fires every scheduler tick. The chapter already
  said so in prose; the emitter now enforces it.
- **Secrets are referenced, never written.** Each `secrets[]` entry becomes a `secretKeyRef` env
  entry against a Secret named `<slug>-secrets`, with its `source` emitted as a YAML comment beside
  it. The emitter writes no `Secret` object at all — secret material does not cross the manifest
  boundary, and a generated chart is exactly the wrong place to learn otherwise.
- **Ingress hosts are literal, in both formats.** A host set is a manifest fact, not a per-install
  knob; `values.yaml` carries only what an install genuinely re-decides (image coordinates, replica
  counts, the Secret's name). Change the manifest and re-emit.
- **Three manifest fields are deliberately not consumed**: `app.region` (a deploy-plane placement
  label, not a Kubernetes object), `modules` (a composition-root fact, though conflicting versions
  are still rejected by validation) and `dependencies` (services the operator provisions). They are
  read and preserved rather than reinterpreted.
- **What the emitter refuses**, all before writing anything: a manifest that fails validation, a
  manifest with no `runtime.image` and no `--image`, domains declared with no HTTP-serving role
  selected, more than one HTTP-serving role (the Ingress backend would be ambiguous), `AllInOne`
  paired with a split role, and an output clash without `--force`.

## Verification

`src/ToolUp.Cli.Tests/K8sEmitTests.fs` emits for every `ProcessProfile` × both formats and asserts
over the **parsed** YAML — kinds, one Deployment per role, resource names, replica counts, probe
paths and timeouts, `secretKeyRef` entries, Service targets, ingress hosts and TLS secret names.

A Helm chart is a Go template and not valid YAML until rendered, so the helm arm is rendered first
by a small `{{ .Values.x }}` resolver reading the chart's own `values.yaml`. One assertion body
therefore covers both formats, and a template referencing a value the chart never defines fails the
test instead of installing as an empty string.

A real `helm lint` runs as an env-gated case when `TOOLUP_HELM_PATH` names a binary, and reports
Pending otherwise — no helm or kubectl exists in this repo or in CI.

## Rollback

Delete `src/ToolUp.Cli/Cli/K8sEmitCommand.fs`, `src/ToolUp.Cli.Tests/K8sEmitTests.fs` and
`templates/platformsdk-k8s/`; remove their `<Compile>` / `<EmbeddedResource>` items, the
`K8sEmitCommand.command` registration in `src/ToolUp.Cli/Program.fs`, the `K8sEmitTests.tests`
registration in `src/ToolUp.Cli.Tests/Program.fs`, the test-only `YamlDotNet` reference, and
regenerate `api-baselines/ToolUp.Cli.approved.txt`. Nothing else in the SDK refers to any of it.
