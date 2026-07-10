# ToolUp.Hosts.Docker

Container host companion for `ToolUp.Platform`. Ships a maintained `Dockerfile` + `.dockerignore` + healthcheck script + sample `compose.yml` so deployments do not have to hand-roll Docker artefacts and re-discover the same gotchas (static-path warning, signal forwarding, layer caching, non-root user conventions).

## Status

Phase 16b host-adapter companion. Sibling to [`ToolUp.Hosts.AwsLambda`](../AwsLambda/README.md), [`ToolUp.Hosts.AzureFunctions`](../AzureFunctions/README.md), and [`ToolUp.Hosts.GoogleCloudFunctions`](../GoogleCloudFunctions/README.md) — same `IServerHost`-driven Phase 16 substrate, different host shape. Unlike the serverless adapters, this companion ships no `.fs` bridge code — the SDK already speaks HTTP via Kestrel; the container just runs the published binary. The deliverable is **template assets**.

## Install

```xml
<PackageReference Include="ToolUp.Hosts.Docker" />
```

The package's `contentFiles/` payload drops these four files into the consumer's working tree on restore:

| Source                          | Destination (typical)             |
|---------------------------------|-----------------------------------|
| `Dockerfile.template`           | `Dockerfile` (rename + edit)      |
| `.dockerignore.template`        | `.dockerignore` (rename + edit)   |
| `healthcheck.sh`                | `healthcheck.sh` (use as-is)      |
| `compose.yml.template`          | `compose.yml` (rename + edit)     |

The cleaner path is to scaffold the same files via `dotnet new platformsdk-docker` (see [Usage](#usage) below), which substitutes the `{{...}}` tokens for you.

## Usage

### 1. Scaffold the Docker artefacts

```bash
dotnet new platformsdk-docker \
    --server-project MyApp-Server \
    --server-dll MyApp-Server \
    --image-name myapp \
    --host-port 8080
```

The template emits `Dockerfile`, `.dockerignore`, `healthcheck.sh`, and `compose.yml` at the solution root with the tokens filled in for your project.

### 2. Build the image

```bash
docker build -t myapp:dev .
```

The Dockerfile uses a two-stage build:

- **Stage 1** (`mcr.microsoft.com/dotnet/sdk:10.0`) — restores the NuGet graph, then `dotnet publish -c Release` produces `/app/publish/`.
- **Stage 2** (`mcr.microsoft.com/dotnet/aspnet:10.0`) — copies the publish output, installs `tini` + `curl`, creates a non-root `app` user (uid/gid 10001), exposes port 5000, and wires the `HEALTHCHECK` to `/health`.

### 3. Run

```bash
docker run --rm -p 8080:5000 \
    -e TOOLUP_PROCESS_PROFILE=AllInOne \
    myapp:dev
```

`/health` reports green once the SDK's Phase 9k Liveness probe passes. `docker stop` propagates `SIGTERM` through `tini` to the `dotnet` process for a graceful shutdown (no `SIGKILL` after the 10-second default grace period).

## ProcessProfile — one image, env-var-driven role

The same image runs every [Phase 16a](../../../docs/migrations/16a-process-profile-gating.md) `ProcessProfile` — set `TOOLUP_PROCESS_PROFILE` at container start:

| Value             | Role                                       |
|-------------------|--------------------------------------------|
| `AllInOne` (default) | HTTP pipeline mounted + every background subsystem |
| `WebOnly`         | HTTP pipeline mounted, no background subsystems |
| `WorkerOnly`      | No HTTP pipeline, every background subsystem runs |
| `DispatcherOnly`  | HTTP pipeline mounted, only outbound dispatchers (transactional + webhook) |

Multi-silo deployments scale the web tier and the worker tier independently by running the same image with different profile values + the same Redis `INotificationChannel` + same `IBlobStorage` / `IEventStore`.

> **WorkerOnly + multi-replica caveat.** Phase 9i (`IDistributedLock`) is unshipped; a `WorkerOnly` deployment with `ReplicaCount > 1` will duplicate-fire scheduler ticks. Pin `ReplicaCount = 1` on `WorkerOnly` / `DispatcherOnly` silos until Phase 9i lands. Web silos scale freely.

## Signal handling

`tini` runs as PID 1 and handles two POSIX subtleties:

1. **Signal forwarding.** Without `tini`, `dotnet` running as PID 1 doesn't get SIGTERM delivered correctly by Docker — the kernel reserves PID-1 signal handling for `init`. `tini` re-forwards SIGTERM to `dotnet`, which then triggers ASP.NET Core's `IHostApplicationLifetime` shutdown sequence.
2. **Zombie reaping.** Subprocesses re-parented to PID 1 (none today, but Phase 9b background workers may spawn helpers in the future) need a PID-1 reaper. `TINI_SUBREAPER=1` makes `tini` the subreaper.

If you remove `tini` from the Dockerfile, `docker stop` will fall through to `SIGKILL` after the grace period — the SDK does not get a chance to flush in-flight writes or close out audit ledger entries cleanly.

## Forwarded-headers trust

Phase 16d (forwarded-headers default-on, shipped 0.4.4) made `TrustForwardedHeaders = true` the SDK default. This Dockerfile **does not** set `TOOLUP_TRUST_FORWARDED_HEADERS=1` — the SDK already trusts `X-Forwarded-*` headers in the inbound request when Kestrel runs behind a reverse proxy (nginx / Traefik / Azure App Service ingress / Cloud Run front door / ALB). Set the env var explicitly only to opt **out** (`=0`), not to opt in.

**Phase 325 — auth-requiring modes must scope the trust.** The knob-free posture above holds only for **anonymous-mode** deployments. On any auth-requiring (non-Anonymous) surface, `TrustForwardedHeaders = true` with no CIDR allowlist is now a preflight **Error** — the container refuses to start until you either scope the trust to the terminator's network(s) with `TOOLUP_TRUSTED_PROXY_CIDRS` (e.g. `10.0.0.0/8`) or attest a single header-stripping proxy fronts every request path with `TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY=1`. See `docs/migrations/325-forwarded-headers-cidr-trust.md` in the forge repo.

## Healthcheck wrapper

`healthcheck.sh` wraps `curl -fsS http://localhost:5000/health` with `--max-time` to bound the probe duration. Two env vars override the defaults:

- `TOOLUP_HEALTHCHECK_URL` — overrides the probe URL (default `http://localhost:5000/health`). Useful when the container exposes a non-default port or when the orchestrator routes the probe via a sidecar hostname.
- `TOOLUP_HEALTHCHECK_TIMEOUT` — bounds curl's total invocation time in seconds (default `5`).

## Choosing this companion

| Use the Docker companion when... | Use a serverless adapter instead when... |
|----------------------------------|------------------------------------------|
| Deploying to Azure App Service Linux (container mode) | Deploying to Azure Functions Consumption / Premium |
| Deploying to GCP Cloud Run / GKE | Deploying to GCF (Gen 1 / Gen 2 HTTP) |
| Deploying to AWS ECS / EKS | Deploying to AWS Lambda (API Gateway / ALB) |
| Deploying to bare-metal or VM Kubernetes | Per-invocation billing matters more than steady-state cost |
| You want one image across every cloud | You want zero-idle-cost serverless |

The image produced by this Dockerfile is portable across every container-platform target above — same OCI image, different orchestrator entry points. Per-cloud notes live in [chapter 14 of the technical guide](../../ToolUp.Platform/technical-guide/14-docker-hosting.md).

## Six-rule portability audit

This package is a host adapter, not a substrate interface. It does not implement any of the six portability rules directly — it consumes `IServerHost`, which is itself purely an in-process composition seam and exempt from cross-shard portability.

## License

Apache-2.0. See `LICENSE` at the repo root.
