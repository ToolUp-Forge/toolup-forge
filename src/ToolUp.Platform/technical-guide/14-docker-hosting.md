# ToolUp.Platform Technical Guide — 14. Docker Hosting

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 13. Deployment Shapes](13-deployment-shapes.md) · [Index ↑](../TECHNICAL_GUIDE.md) · Next: _(none)_

---

Chapter [12](12-hosting-models.md) covered the serverless-front-door deployment shapes and [chapter 13](13-deployment-shapes.md) the pure-Kestrel deployment shapes partitioned via `ServerConfig.ProcessProfile`. This chapter is the reference for **packaging a Kestrel deployment as a container** — the OCI image layout, the conventions the SDK assumes about the runtime environment, and the entry points each major container platform consumes.

The companion package is [`ToolUp.Hosts.Docker`](../../Hosts/Docker/README.md). It ships no `.fs` bridge code — the SDK already speaks HTTP via Kestrel; the container just runs the published binary. The deliverable is four template files (`Dockerfile.template`, `.dockerignore.template`, `healthcheck.sh`, `compose.yml.template`) that the consumer scaffolds via `dotnet new platformsdk-docker` or copies by hand.

## Image layout

The Dockerfile is a two-stage F# build. The SDK image (`mcr.microsoft.com/dotnet/sdk:10.0`) performs `dotnet restore` + `dotnet publish` against the consumer's solution; the runtime image (`mcr.microsoft.com/dotnet/aspnet:10.0`) carries only the published output, the `tini` init binary, `curl` for the healthcheck wrapper, and a non-root `app` user.

```text
┌─ Stage 1 build (sdk:10.0) ────────────────┐
│ /src/                                     │
│   ├─ *.sln, Directory.{Build,Packages}.props
│   ├─ global.json, nuget.config
│   └─ src/                                 │
│       └─ <Server-project>/...             │
│ dotnet restore  →  cached layer           │
│ dotnet publish  →  /app/publish/          │
└───────────────────────────────────────────┘
                  │
                  ▼ copy /app/publish
┌─ Stage 2 runtime (aspnet:10.0) ───────────┐
│ /app/                                     │
│   ├─ <Server>.dll                         │
│   ├─ <Server>.deps.json                   │
│   └─ ToolUp.Platform.*.dll, companions    │
│ /usr/local/bin/healthcheck.sh             │
│ /usr/bin/tini                             │
│ USER app  (uid 10001, no home)            │
│ EXPOSE 5000                               │
│ HEALTHCHECK → /health                     │
│ ENTRYPOINT  tini -- dotnet <Server>.dll   │
└───────────────────────────────────────────┘
```

The restore layer is cached separately from the publish layer so a source edit that does not touch a `.fsproj` / `.props` / `nuget.config` file re-uses the cached restore. Layer ordering matters: copying `src/` last in stage 1 maximises the cache hit rate during local iteration.

## Why `tini`

ASP.NET Core's `IHostApplicationLifetime` shutdown sequence — the path that flushes in-flight `IAuditSink` batches, drains the webhook dispatcher, closes the SSE notification channel — is triggered by `SIGTERM`. Linux's PID-1 signal-handling rules mean that a `dotnet` process running as PID 1 does **not** receive `SIGTERM` from `docker stop`; the kernel reserves PID-1 signal delivery for processes that opt in via `signal(2)` or `sigaction(2)`, which `dotnet` does not.

Without `tini` (or another init replacement), `docker stop` waits the grace period (default 10 seconds) and then falls through to `SIGKILL`. The SDK does not get a chance to flush. In-flight `IAuditSink` writes are lost; the webhook dispatcher's current batch is dropped; the `IEventStore` ledger may have appended an entry without its replication paired.

`tini` solves this with two responsibilities:

1. **Signal forwarding.** `tini` registers handlers for `SIGTERM` / `SIGINT` / `SIGHUP` and re-delivers them to the child `dotnet` process. The SDK then runs its normal shutdown sequence.
2. **Zombie reaping.** Child processes re-parented to PID 1 need a PID-1 reaper to `wait()` on them. No SDK component spawns subprocesses today, but [Phase 9b](../../../../ToolUp-Diametrical/roadmap/phases/09b-background-job-scheduler-infrastructure.md) background workers may grow this. `TINI_SUBREAPER=1` makes `tini` the subreaper.

If you choose to drop `tini`, the alternative is `--init` on `docker run` (Docker injects its own init shim) or `terminationGracePeriodSeconds: 60+` on Kubernetes plus an application-side `SIGTERM` handler that calls `IHostApplicationLifetime.StopApplication()`. Both are heavier than baking `tini` into the image.

## Non-root by convention

The runtime stage creates an `app` user with uid/gid 10001, no login shell, and no home directory. The `WORKDIR /app` and `COPY --chown=app:app` lines transfer ownership before the `USER app` switch, so the dotnet process runs unprivileged from the first byte of execution.

Every major container platform accepts non-root images:

| Platform                        | Non-root supported? | Notes |
|---------------------------------|---------------------|-------|
| Azure App Service Linux (container mode) | Yes              | The platform's reverse-proxy ingress runs root-side; the container itself is unprivileged. |
| GCP Cloud Run                   | Yes              | Cloud Run mandates non-root for second-gen execution environment. |
| AWS ECS Fargate                 | Yes              | Task role binds at platform level; container user can be anything. |
| AWS Lambda container images     | Yes              | Lambda's bootstrap runs unprivileged regardless of `USER`. |
| Kubernetes                      | Yes              | Pair with `securityContext.runAsNonRoot: true` to enforce. |

Running as root is a defensive failure mode we choose not to ship as the default. A consumer that needs root (legacy mount, debugging) can flip `USER app` to `USER root` in their copy of the Dockerfile.

## Healthcheck

`HEALTHCHECK` in the Dockerfile fires `/usr/local/bin/healthcheck.sh` every 30 seconds (the standard Docker default). The script is a bounded `curl` against the Phase 9k Liveness probe (`/health`) — a 2xx response is healthy, anything else is unhealthy, and `--max-time` caps the total probe duration so a half-open TCP connection does not hang the orchestrator's healthcheck loop.

Two env vars override the defaults:

- `TOOLUP_HEALTHCHECK_URL` — probe URL (default `http://localhost:5000/health`). Override when the orchestrator routes the probe through a sidecar hostname or when the container exposes a non-default port.
- `TOOLUP_HEALTHCHECK_TIMEOUT` — total curl invocation cap in seconds (default `5`).

Liveness vs Readiness: Phase 9k registers both probes at `/health` (Liveness) and `/ready` (Readiness). The Docker `HEALTHCHECK` directive only models one state, so the script targets Liveness; orchestrators that distinguish the two (Kubernetes especially) should configure a separate `readinessProbe` against `/ready`.

## `ProcessProfile` interaction — one image, env-var-driven role

The image runs every `ServerConfig.ProcessProfile` ([chapter 13](13-deployment-shapes.md), [Phase 16a](../../../../ToolUp-Diametrical/roadmap/phases/16a-process-model-split-serverconfig-processprofile.md)) — set the value at container start:

```bash
docker run -e TOOLUP_PROCESS_PROFILE=AllInOne       myapp:dev
docker run -e TOOLUP_PROCESS_PROFILE=WebOnly        myapp:dev
docker run -e TOOLUP_PROCESS_PROFILE=WorkerOnly     myapp:dev
docker run -e TOOLUP_PROCESS_PROFILE=DispatcherOnly myapp:dev
```

`AllInOne` is the default — both the HTTP pipeline and every background subsystem run. Pure-Kestrel multi-silo deployments scale the web tier and the worker tier independently by running the same image with different profile values:

```text
┌─ web tier (replicas: N) ──┐    ┌─ worker tier (replicas: 1) ─┐
│ TOOLUP_PROCESS_PROFILE=   │    │ TOOLUP_PROCESS_PROFILE=     │
│   WebOnly                 │    │   WorkerOnly                │
│ Kestrel binds :5000       │    │ No HTTP pipeline            │
│ HEALTHCHECK → /health     │    │ HEALTHCHECK → /health       │
│ IJobScheduler skipped     │    │ IJobScheduler runs          │
└───────────────────────────┘    └─────────────────────────────┘
              │                                  │
              └──── shared substrate ────────────┘
        IBlobStorage / IEventStore / Redis INotificationChannel
```

> **WorkerOnly + multi-replica caveat.** Phase 9i (`IDistributedLock`) is unshipped; a `WorkerOnly` (or `DispatcherOnly`) deployment with `ReplicaCount > 1` will duplicate-fire scheduler ticks across replicas. Pin `ReplicaCount = 1` on those silos until Phase 9i lands. Web silos scale freely (`WebOnly` runs no `IHostedService`s — there is nothing to duplicate).

The healthcheck still fires under `WorkerOnly` even though no HTTP pipeline is mounted — Kestrel binds the port, ASP.NET Core's built-in healthcheck endpoint responds, but the SDK's middleware graph is not configured to route module APIs. The script returns 2xx and the orchestrator reports the silo healthy. (A future iteration may switch `WorkerOnly` to `Host.CreateApplicationBuilder()` per the Phase 16a follow-up — the silo would then bind no port at all and the orchestrator's HTTP-shaped healthcheck would need to flip to a process-presence probe.)

## Forwarded-headers trust

Phase 16d (forwarded-headers default-on, shipped 0.4.4) made `ServerConfig.TrustForwardedHeaders = true` the SDK default. The Dockerfile does **not** set `TOOLUP_TRUST_FORWARDED_HEADERS=1` — that would be redundant. Reverse-proxy deployments behind nginx / Traefik / Azure App Service ingress / Cloud Run front door / ALB work without any Dockerfile knob.

Set the env var explicitly only to opt **out** (`=0`) for a deployment that intentionally exposes Kestrel directly without a proxy.

## Per-platform deployment

The same image targets every major container platform. The Dockerfile is portable; only the orchestrator's deployment manifest differs.

### Azure App Service Linux (container mode)

```bash
az webapp create \
    --resource-group <rg> \
    --plan <linux-plan> \
    --name <app-name> \
    --deployment-container-image-name <registry>/<image>:<tag>

az webapp config appsettings set \
    --resource-group <rg> --name <app-name> \
    --settings TOOLUP_PROCESS_PROFILE=AllInOne \
               WEBSITES_PORT=5000
```

`WEBSITES_PORT` tells App Service that the container listens on 5000 (App Service maps the public 443 ingress to that internal port). The platform handles TLS termination + forwarded headers; the SDK's `TrustForwardedHeaders = true` default reads the `X-Forwarded-Proto` / `X-Forwarded-For` chain correctly.

### GCP Cloud Run

```bash
gcloud run deploy <service-name> \
    --image <registry>/<image>:<tag> \
    --port 5000 \
    --set-env-vars TOOLUP_PROCESS_PROFILE=AllInOne \
    --allow-unauthenticated
```

Cloud Run's `--port` flag is the in-container port; the platform exposes the service on 443 + 80. The `--allow-unauthenticated` flag is optional — depending on whether the SDK's `IAuthProvider` handles its own auth or whether Cloud Run's IAM gates ingress.

### AWS ECS Fargate

ECS task definitions describe the container in JSON / YAML; the relevant fragment:

```json
{
  "containerDefinitions": [
    {
      "name": "<app-name>",
      "image": "<registry>/<image>:<tag>",
      "essential": true,
      "portMappings": [
        { "containerPort": 5000, "protocol": "tcp" }
      ],
      "environment": [
        { "name": "TOOLUP_PROCESS_PROFILE", "value": "AllInOne" }
      ],
      "healthCheck": {
        "command": ["CMD", "/usr/local/bin/healthcheck.sh"],
        "interval": 30,
        "timeout": 10,
        "retries": 3,
        "startPeriod": 30
      }
    }
  ]
}
```

The task definition's `healthCheck` block re-states the Dockerfile's `HEALTHCHECK` — ECS prefers the explicit task-definition declaration over the image-level one for managed-rolling-update sequencing.

### Kubernetes (Deployment + Service)

```yaml
apiVersion: apps/v1
kind: Deployment
metadata: { name: <app-name> }
spec:
  replicas: 2
  selector: { matchLabels: { app: <app-name> } }
  template:
    metadata: { labels: { app: <app-name> } }
    spec:
      securityContext:
        runAsNonRoot: true
        runAsUser: 10001
      containers:
        - name: app
          image: <registry>/<image>:<tag>
          ports:
            - containerPort: 5000
          env:
            - name: TOOLUP_PROCESS_PROFILE
              value: WebOnly
          livenessProbe:
            httpGet: { path: /health, port: 5000 }
            initialDelaySeconds: 30
            periodSeconds: 30
            timeoutSeconds: 10
          readinessProbe:
            httpGet: { path: /ready, port: 5000 }
            initialDelaySeconds: 10
            periodSeconds: 10
            timeoutSeconds: 5
```

Kubernetes is the case where `livenessProbe` and `readinessProbe` separate cleanly: Liveness against `/health` (restart on failure), Readiness against `/ready` (de-list from Service on failure, do not restart). A second `Deployment` with `replicas: 1` and `TOOLUP_PROCESS_PROFILE=WorkerOnly` shares the substrate via the same Redis Service + the same persistent-volume-claim-backed `IBlobStorage`.

## Build-context hygiene — `.dockerignore`

The `.dockerignore` ships with sensible defaults that exclude:

- `**/bin/`, `**/obj/` — .NET build artefacts re-created inside the build stage.
- `**/node_modules/` — Node toolchain re-installed inside the build stage if at all.
- `**/output/`, `**/*.fs.js` — Fable transpilation output regenerated by the build stage's Fable target.
- `.git/`, `.gitignore`, `.gitattributes` — VCS state.
- `data/`, `**/data/` — local-filesystem stateful blobs (dev-only `InMemoryEntityStore` / `LocalBlobStorage`).
- `.vs/`, `.vscode/`, `.idea/`, `**/*.user` — editor state.
- `compose.override.yml` family — local-dev orchestration overrides.
- `**/.DS_Store`, `**/Thumbs.db` — OS noise.
- `**/TestResults/`, `**/coverage/` — test-runner output.
- `*.env` (except `*.env.example`) — secrets / dotenvs.

A consumer's repo with even modest node_modules / bin / obj baggage typically pushes the unfiltered build context past 500 MB; the filtered context is usually <50 MB. The cache invalidation surface shrinks proportionally.

## Limitations

- **No streaming-response native support.** The SDK speaks SSE for `IClient.notifications` / AI streaming through standard ASP.NET Core SSE — Docker passes this through transparently because there is no Lambda-style buffered-response intermediary. (Contrast: `ToolUp.Hosts.AwsLambda` buffers the full body before returning to the Lambda runtime; SSE there requires the `RESPONSE_STREAM` Function URL mode covered in that companion's README.)
- **No multi-replica `WorkerOnly`.** Phase 9i unshipped — see the caveat block above.
- **No image-signing pipeline shipped.** The Dockerfile produces an OCI image; signing with cosign / Notary v2 is the consumer's responsibility (and a long-term forge phase — see Wave 11 in the forge roadmap).
- **No SBOM generation step.** The publish stage produces the bits; adding `dotnet sbom-tool` or `syft` to the build stage is a consumer call. The companion does not opine.

## See also

- [`ToolUp.Hosts.Docker` README](../../Hosts/Docker/README.md) — package overview and `dotnet new platformsdk-docker` walkthrough.
- [Chapter 12 — Hosting Models](12-hosting-models.md) — serverless host adapters (Azure Functions / AWS Lambda / GCF) and the hybrid serverless + Kestrel worker silo.
- [Chapter 13 — Deployment Shapes](13-deployment-shapes.md) — pure-Kestrel single-process / web+worker / web+worker+dispatcher partitioning via `ProcessProfile`.
- [Phase 16b](../../../../ToolUp-Diametrical/roadmap/phases/16b-container-companion-toolup-platform-hosts-docker.md) — the roadmap phase that authored this companion.
- [Phase 16d migration doc](../../../docs/migrations/16d-forwarded-headers-default-on.md) — forwarded-headers default-flip rationale.
