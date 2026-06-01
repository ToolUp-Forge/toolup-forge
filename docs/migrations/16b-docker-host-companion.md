# Migration — Phase 16b: Docker host companion (`ToolUp.Hosts.Docker`)

Adds a maintained Dockerfile + .dockerignore + healthcheck script + sample compose.yml so deployments do not have to hand-roll Docker artefacts.

## What changes

- New companion package **`ToolUp.Hosts.Docker`**. Ships no `.fs` bridge code — the SDK already speaks HTTP via Kestrel; the container just runs the published binary. The deliverable is four template files (`Dockerfile.template`, `.dockerignore.template`, `healthcheck.sh`, `compose.yml.template`) under `contentFiles/` in the nupkg.
- New `dotnet new platformsdk-docker` template — emits the same four files at the consumer's solution root with `{{server-project}}` / `{{server-dll}}` / `{{image-name}}` / `{{host-port}}` tokens substituted.
- New TECHNICAL_GUIDE chapter [`14. Docker Hosting`](../../src/ToolUp.Platform/technical-guide/14-docker-hosting.md). Image layout, signal handling, non-root convention, healthcheck, `ProcessProfile` interaction, per-platform deployment entry points.

The companion is **purely additive**. Existing deployments that ship their own hand-rolled Dockerfile keep working without change. No SDK-side behaviour or API moves.

## How to adopt

### Option A — `dotnet new platformsdk-docker` (recommended)

```bash
dotnet new platformsdk-docker \
    --server-project MyApp-Server \
    --server-dll MyApp-Server \
    --image-name myapp \
    --host-port 8080
```

Emits `Dockerfile`, `.dockerignore`, `healthcheck.sh`, `compose.yml` at the solution root. Token-substitutes for your project names.

### Option B — `<PackageReference>` + manual rename

```xml
<PackageReference Include="ToolUp.Hosts.Docker" />
```

NuGet restore drops the four template files into the consumer's `contentFiles/` cache. Copy / rename them at the solution root and edit the `{{...}}` tokens by hand:

| Template name              | Final name      |
|----------------------------|-----------------|
| `Dockerfile.template`      | `Dockerfile`    |
| `.dockerignore.template`   | `.dockerignore` |
| `healthcheck.sh`           | `healthcheck.sh` (use as-is) |
| `compose.yml.template`     | `compose.yml`   |

## Verification

After scaffolding the artefacts:

```bash
# 1. Build the image (cold; warm cache after the first run is much faster)
docker build -t myapp:dev .

# 2. Run with the default ProcessProfile=AllInOne
docker run --rm -d -p 8080:5000 --name myapp-dev myapp:dev

# 3. Confirm Liveness probe is green within ~30 seconds
curl -fsS http://localhost:8080/health   # → 2xx
docker inspect --format '{{.State.Health.Status}}' myapp-dev   # → "healthy"

# 4. Confirm graceful shutdown (no SIGKILL fallthrough)
docker stop myapp-dev
# Expect: "myapp-dev" printed within the grace period (default 10s).
# If docker stop takes the full grace period and then prints SIGKILL,
# tini is not forwarding signals — check the Dockerfile's ENTRYPOINT.

# 5. Confirm non-root execution
docker run --rm myapp:dev id   # → uid=10001(app) gid=10001(app) groups=10001(app)
```

Profile-shape verification (one image, env-var-driven role):

```bash
# WebOnly — Kestrel binds, no background services tick
docker run --rm -d -p 8080:5000 -e TOOLUP_PROCESS_PROFILE=WebOnly --name myapp-web myapp:dev
curl -fsS http://localhost:8080/health   # → 2xx
docker logs myapp-web 2>&1 | grep -E '(scheduler|webhook|dispatcher)' | head
# Expect: zero or near-zero matches — every IHostedService is gated off.
docker stop myapp-web

# WorkerOnly — silo runs background services, HTTP pipeline not configured
docker run --rm -d -e TOOLUP_PROCESS_PROFILE=WorkerOnly --name myapp-worker myapp:dev
docker logs myapp-worker 2>&1 | grep -i 'scheduler\|tick' | head
# Expect: scheduler tick lines appearing within ~30 seconds.
docker stop myapp-worker
```

## Rollback

The companion is purely additive — there is nothing to revert in your SDK composition.

- Delete the four files (`Dockerfile`, `.dockerignore`, `healthcheck.sh`, `compose.yml`) and the `<PackageReference Include="ToolUp.Hosts.Docker" />` line from your solution.
- The deployment falls back to whatever hand-rolled Docker config you were using before (or to a non-containerised deployment if there was none).

## Caveats

- **`WorkerOnly` + multi-replica.** Phase 9i (`IDistributedLock`) is unshipped; a `WorkerOnly` (or `DispatcherOnly`) silo with `ReplicaCount > 1` duplicate-fires scheduler ticks. Pin `ReplicaCount = 1` on those silos. Web silos scale freely.
- **CLI command `dotnet toolup docker emit` deferred.** Phase 16b's task list includes a `toolup-admin` CLI command for re-emitting a customised Dockerfile after changing the deployment's companion set. The CLI substrate (`toolup-admin`) is unshipped today; the command lands in a follow-up phase once the CLI is in place. Until then, re-emit via `dotnet new platformsdk-docker --force` (which overwrites at the solution root).
- **No image signing / SBOM step.** The Dockerfile produces an OCI image; signing (cosign / Notary v2) and SBOM generation (`syft`, `dotnet sbom-tool`) are consumer responsibilities. Long-term forge phase.
- **Streaming.** SSE works through the standard Kestrel-on-Docker stack with no special config — pass-through behaviour, unlike `ToolUp.Hosts.AwsLambda` where buffered-response is the default and streaming is a separate code path.

## Forwarded-headers interaction

Phase 16d (forwarded-headers default-on, shipped 0.4.4) made `TrustForwardedHeaders = true` the SDK default. This companion's `Dockerfile.template` does **not** set `TOOLUP_TRUST_FORWARDED_HEADERS=1` — it would be redundant. Reverse-proxy deployments (App Service ingress / Cloud Run front door / ALB / nginx / Traefik) work out of the box.

Set `TOOLUP_TRUST_FORWARDED_HEADERS=0` explicitly only when you intend to opt **out** of the default trust (e.g. Kestrel exposed directly to the public internet without a proxy).
