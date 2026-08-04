# Phase 193 — Emulator-backed multi-cloud parity conformance matrix

Adds `src/ToolUp.Cloud.Parity.Tests`, a test pack that runs the **shared** `IBlobStorage` /
`ISecretStore` / `IAuditSink` contract packs against each cloud's local emulator — **Azurite**
(Azure), **LocalStack** (AWS), and **fake-gcs-server** (GCP) — plus a `parity` profile in
`src/Hosts/Docker/compose.yml.template` that boots them.

Until now, "the same image runs on any cloud" was validated by *deploying* to a second cloud. This
makes it a repeatable gate that runs before the deployment.

## Consumer impact: none

**Test/CI-only substrate. No runtime surface changes, no new package, nothing to adopt.** No
`ToolUp.*` package gains, loses, or retypes a member; no default changes; every deployed app is
byte-for-byte identical (GP 13). The new project is `IsPackable=false` and is not published.

Two things that could look like consumer-facing changes and are not:

- **`compose.yml.template`** gains four services, all behind a `profiles: ["parity"]` gate. A plain
  `docker compose up` resolves to exactly the service set it did before — verified with
  `docker compose config --services`, which still reports only `app`. The emulators appear only under
  `docker compose --profile parity up`.
- **The contract packs are source-linked, not copied.** The `.fsproj` `<Compile Include>`s
  `../ToolUp.Platform.Tests/Contracts/{IBlobStorage,ISecretStore,IAuditSink}Contract.fs` and
  `InMemoryBlobStorage.fs`. One copy on disk, compiled into two assemblies — so a case added to a
  contract pack extends every cloud in this matrix with no edit here. Nothing in
  `ToolUp.Platform.Tests` changes.

## Running the matrix locally

```powershell
# From the repo root. Brings up Azurite + LocalStack (+ its bucket) + fake-gcs.
docker compose --profile parity up -d

$env:TOOLUP_PARITY_AZURITE    = "UseDevelopmentStorage=true"
$env:TOOLUP_PARITY_LOCALSTACK = "http://localhost:4566"
$env:TOOLUP_PARITY_FAKEGCS    = "http://localhost:4443"

dotnet run --project src/ToolUp.Cloud.Parity.Tests/ToolUp.Cloud.Parity.Tests.fsproj
```

| Variable | Arms | Value |
|---|---|---|
| `TOOLUP_PARITY_AZURITE` | Azure blob + audit legs | Azure Storage connection string; `UseDevelopmentStorage=true` for Azurite |
| `TOOLUP_PARITY_LOCALSTACK` | AWS blob + audit legs | LocalStack endpoint URL |
| `TOOLUP_PARITY_LOCALSTACK_BUCKET` | *(optional)* | S3 bucket name, default `toolup-parity`. Also moves the bucket the compose init service creates. |
| `TOOLUP_PARITY_LOCALSTACK_REGION` | *(optional)* | default `us-east-1` |
| `TOOLUP_PARITY_FAKEGCS` | GCP legs | fake-gcs-server endpoint URL |
| `TOOLUP_PARITY_LOCALSTACK_SECRETS` | AWS secrets leg | region — **also requires `AWS_ENDPOINT_URL_SECRETS_MANAGER`**, see below |

**Set a variable only when that emulator is actually up.** Each leg is gated on its variable and
reports a `pending` case naming the reason when unset, so a machine with no Docker skips cleanly and
a fresh checkout is green. An *armed* leg pointed at nothing is correctly red — that is the
distinction the gating exists to preserve.

**The `TOOLUP_PARITY_LOCALSTACK_BUCKET` bucket must exist.** `AwsS3Storage` deliberately does not
create its bucket, so the `localstack-init` service in the `parity` profile does it. Running
LocalStack outside compose means creating the bucket yourself, or every S3 assertion fails on
`NoSuchBucket`.

## What the matrix actually covers

Honest per-cell coverage, because a matrix that implies coverage it does not have is worse than one
that does not exist:

| Seam | Azure / Azurite | AWS / LocalStack | GCP / fake-gcs-server |
|---|---|---|---|
| `IBlobStorage` | ✅ full pack | ✅ full pack | ⛔ no companion seam |
| `IAuditSink` | ✅ full pack | ✅ full pack | ⛔ inherits the blob gap |
| `ISecretStore` | ⛔ no emulator exists | ✅ full pack (see caveat) | ⛔ no emulator exists |

The `IAuditSink` row rides on the blob row: the three archive sinks each take an `IBlobStorage`
(`create name settings blobStorage`), so each cloud's sink binds over that same cloud's
emulator-backed storage. That is what makes audit parity reachable without a fourth emulator.

### The GCP blob gap — the finding this phase was written to catch

**`GoogleCloudStorageConfig` cannot be pointed at an emulator.** It carries `BucketName`,
`CredentialsJson` and `CredentialsJsonProvider`, and the companion builds its client via
`StorageClient.Create`, which does **not** consult `STORAGE_EMULATOR_HOST` — verified locally: with
that variable set, `Create` still walks the Application Default Credentials chain and throws *"Your
default credentials were not found."* Emulator support in `Google.Cloud.Storage.V1` lives on
`StorageClientBuilder` (`BaseUri` / `EmulatorDetection` / `UnauthenticatedAccess`), which the
companion does not expose.

So the emulator is not the obstacle — the companion's config surface is. This is exactly the class of
surprise the phase existed to surface, and finding it in CI rather than mid-migration is the payoff.

**Closing it:** add an `EndpointUrl: string option` field to `GoogleCloudStorageConfig`, mirroring
`AwsS3StorageConfig`, which already has one for MinIO / R2 / B2. That arms both GCP legs with **no
change to this harness** — the `FakeGcs` branch of `EmulatorLegs.blobStorageFactory` is the only edit,
and the compose service is already there. It is not folded into this phase because it is a runtime
surface change: adding a field to an F# record breaks every full-record construction site, so it needs
a version bump and a public-API-baseline regeneration, neither of which belongs in a CI-only change.

`EmulatorSeamCoverageTests` is the ratchet that stops this being forgotten — it fails the moment
`GoogleCloudStorageConfig` gains an endpoint-shaped field, with a message saying to arm the leg and
delete the test.

### The AWS secrets caveat — why it refuses rather than runs

LocalStack does emulate Secrets Manager, but `AwsSecretsManagerConfig` is `{ Region }` with no
endpoint override, so the endpoint has to come from the AWS SDK's own resolution
(`AWS_ENDPOINT_URL_SECRETS_MANAGER`). **If `TOOLUP_PARITY_LOCALSTACK_SECRETS` is set and that variable
is not, the leg refuses to run and says why.** That guard is load-bearing: without the endpoint, the
SDK resolves the *real* Secrets Manager for the region, and the contract pack would write and delete
secrets against a live account on whatever ambient credentials the machine has — with Secrets
Manager's 7–30 day deletion-recovery window making the mess durable. A parity leg must never be one
missing variable away from that.

Azure and GCP `ISecretStore` conformance stays where it already is: the env-gated live-account
bindings in `ToolUp.Platform.Tests`, against the same shared pack.

## The divergence fixture — why this gate cannot pass vacuously

Every emulator leg can legitimately skip, so on a machine with no Docker the parity rows say nothing
either way. That is right for a fresh checkout and dangerous for a gate: *"found no divergence"* and
*"cannot detect divergence"* produce the same green tick.

`DivergenceFixtureTests` closes that hole and needs no emulator, so it runs in every job. It drives
the same shared pack over `DivergentBlobStorage` — the estate's own in-memory double, perturbed in one
known way — and asserts the pack **fails** it, at the **specific case** that models that divergence:

| Perturbation | Must be caught by |
|---|---|
| `Delete` of a missing blob returns `Error` | `Delete is idempotent on missing blobs` |
| `List` returns backslash-delimited names | ``List returns `/`-delimited names for nested blobs, never the OS separator`` |
| `Upload` silently does not overwrite | `Upload overwrites existing blob` |

Each models a real cross-cloud divergence class; the second is the Phase 617 defect that let the last
Owner of a team be removed on Windows.

It is deliberately **two-sided**. Asserting only that a broken provider fails would pass equally well
if the harness failed everything, so the first test asserts the *unperturbed* double **passes** the
same pack in full. Together they show the fixture discriminates rather than merely agreeing with what
was expected. The backslash perturbation uses a literal `\`, not
`Path.DirectorySeparatorChar` — on Linux the separator *is* `/`, so the platform-derived form would be
a silent no-op on exactly the runner CI uses.

## Verification

```powershell
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Cloud.Parity.Tests/ToolUp.Cloud.Parity.Tests.fsproj
```

With no emulators running: **7 passed, 9 ignored, 0 failed** — the 7 always-on cases (1 divergence
control + 3 divergence classes + 3 seam ratchets) and 9 skips (3 seams × 3 clouds). The pack is
sequenced by default per the Phase 617 Expecto/console deadlock rule.

`--list-tests` prints each skip's reason, which is the fastest way to see what a given machine is and
is not covering.

## Rollback

Delete `src/ToolUp.Cloud.Parity.Tests/`, remove it from `ToolUp.Forge.sln`, and revert the `parity`
profile block in `src/Hosts/Docker/compose.yml.template`. Nothing else references any of it; no
shipped package or deployed app is affected either way.

## Not covered

- **`ISecretStore` on Azure and GCP** — no emulator exists for either (above).
- **Cross-cloud *performance* parity.** The matrix asserts behavioural equivalence — same operations,
  same observable results. Latency and throughput differ per cloud by nature and are not asserted.
- **Emulator fidelity itself.** Azurite, LocalStack and fake-gcs-server approximate their services;
  a divergence only the real service exhibits stays invisible here. This narrows the live-deployment
  surprise surface, it does not eliminate it — the env-gated live-account bindings in
  `ToolUp.Platform.Tests` remain the check against the real thing.
