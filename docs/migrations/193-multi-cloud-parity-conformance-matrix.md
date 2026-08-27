# Phase 193 — Emulator-backed multi-cloud parity conformance matrix

Adds `src/ToolUp.Cloud.Parity.Tests`, a test pack that runs the **shared** `IBlobStorage` /
`ISecretStore` / `IAuditSink` contract packs against each cloud's local emulator — **Azurite**
(Azure), **LocalStack** (AWS), and **fake-gcs-server** (GCP) — plus `compose.parity.yml` at the
repo root that boots them.

> **Updated 2026-08-27 (Phase 732).** The lane described below was, until that date, **not
> runnable as documented**: the emulators lived behind a `parity` profile inside
> `src/Hosts/Docker/compose.yml.template`, a `dotnet new` scaffold whose unsubstituted tokens make
> it unparseable, and the documented invocation named a `compose.yml` at the repo root that did
> not exist. Phase 732 moved the services to a real root `compose.parity.yml`, **ran the matrix
> for the first time**, and corrected the coverage table below against what was actually
> measured rather than what was predicted. Every number in this document is now a measurement.

Until now, "the same image runs on any cloud" was validated by *deploying* to a second cloud. This
makes it a repeatable gate that runs before the deployment.

## Consumer impact: none

**Test/CI-only substrate. No runtime surface changes, no new package, nothing to adopt.** No
`ToolUp.*` package gains, loses, or retypes a member; no default changes; every deployed app is
byte-for-byte identical (GP 13). The new project is `IsPackable=false` and is not published.

Two things that could look like consumer-facing changes and are not:

- **`compose.parity.yml`** is a new file at the repo root carrying only the emulator services. It is
  not packed, not scaffolded, and not referenced by any deployment: nothing reads it unless
  `-f compose.parity.yml` names it explicitly. (Phase 193 originally put these services behind a
  `profiles: ["parity"]` gate inside `src/Hosts/Docker/compose.yml.template`; Phase 732 moved them
  out and that template no longer carries them.)
- **The contract packs are source-linked, not copied.** The `.fsproj` `<Compile Include>`s
  `../ToolUp.Platform.Tests/Contracts/{IBlobStorage,ISecretStore,IAuditSink}Contract.fs` and
  `InMemoryBlobStorage.fs`. One copy on disk, compiled into two assemblies — so a case added to a
  contract pack extends every cloud in this matrix with no edit here. Nothing in
  `ToolUp.Platform.Tests` changes.

## Running the matrix locally

This block is **verified** — it is exactly what Phase 732 ran to produce the measured results below.

```powershell
# From the repo root. Brings up Azurite + LocalStack (+ its bucket) + fake-gcs (+ its bucket).
docker compose -f compose.parity.yml up -d --wait

$env:TOOLUP_PARITY_AZURITE                     = "UseDevelopmentStorage=true"
$env:TOOLUP_PARITY_LOCALSTACK                  = "http://localhost:4566"
$env:TOOLUP_PARITY_FAKEGCS                     = "http://localhost:4443/storage/v1/"
$env:TOOLUP_PARITY_LOCALSTACK_SECRETS          = "us-east-1"
$env:TOOLUP_PARITY_LOCALSTACK_SECRETS_ENDPOINT = "http://localhost:4566"

# LocalStack accepts any credentials, but the AWS SDK refuses to sign without some.
$env:AWS_ACCESS_KEY_ID     = "test"
$env:AWS_SECRET_ACCESS_KEY = "test"

dotnet build ToolUp.Forge.sln
dotnet src/ToolUp.Cloud.Parity.Tests/bin/Debug/net10.0/ToolUp.Cloud.Parity.Tests.dll

# When you are done:
docker compose -f compose.parity.yml down -v
```

Invoke the **built dll** rather than `dotnet run --project`, per the Phase 731 finding that the
`run` launch path intermittently hangs before the suite starts.

| Variable | Arms | Value |
|---|---|---|
| `TOOLUP_PARITY_AZURITE` | Azure blob + audit legs | Azure Storage connection string; `UseDevelopmentStorage=true` for Azurite |
| `TOOLUP_PARITY_LOCALSTACK` | AWS blob + audit legs | LocalStack endpoint URL |
| `TOOLUP_PARITY_LOCALSTACK_BUCKET` | *(optional)* | S3 bucket name, default `toolup-parity`. Also moves the bucket the compose init service creates. |
| `TOOLUP_PARITY_LOCALSTACK_REGION` | *(optional)* | default `us-east-1` |
| `TOOLUP_PARITY_FAKEGCS` | GCP blob + audit legs | fake-gcs-server API base — **must include the `/storage/v1/` path**, see below |
| `TOOLUP_PARITY_FAKEGCS_BUCKET` | *(optional)* | GCS bucket name, default `toolup-parity`. Also moves the bucket the compose init service creates. |
| `TOOLUP_PARITY_LOCALSTACK_SECRETS` | AWS secrets leg | region — also requires `TOOLUP_PARITY_LOCALSTACK_SECRETS_ENDPOINT` (or `AWS_ENDPOINT_URL_SECRETS_MANAGER`), see below |
| `TOOLUP_PARITY_LOCALSTACK_SECRETS_ENDPOINT` | AWS secrets leg | LocalStack endpoint URL |

### Two values that are not guessable from the variable names

**`TOOLUP_PARITY_FAKEGCS` must carry the `/storage/v1/` path.** It is passed to
`GoogleCloudStorageConfig.EndpointUrl`, which reaches `StorageClientBuilder.BaseUri` — and Google's
`BaseUri` means the **full API base**, not the host. Pointed at a bare `http://localhost:4443` the
client builds `/b/{bucket}/o` instead of `/storage/v1/b/{bucket}/o` and every call 404s. This is a
real asymmetry with `AwsS3StorageConfig.EndpointUrl`, which the field was modelled on and which
*does* take a bare host: the two fields share a name and a docstring but not their semantics.
Measured cost of getting it wrong: **18 of the 26 GCP cases red**, with a `NotFound` that names
nothing leading you to the URL shape.

**LocalStack's `:latest` tag is the Pro image and will not boot without a paid licence.**
`compose.parity.yml` therefore pins `localstack/localstack:4`. See the comment in that file; the
short version is that `:latest` and `:stable` are labelled *"LocalStack Pro Docker image"* and exit
55 with `License activation failed!`, while `:4` and `:3` are the community image and need no
token. A paid-by-default dependency in the default lane is also a GP 2 violation.

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

**Measured 2026-08-27, after Phase 733** — the first run (Phase 732, same day) scored 78/87 with 16
red; the wrapped-vendor-exception fix in the AWS Secrets Manager and GCS companions closed 10, and
Phase 733 closed the last 2 that were companion defects by carrying the same fix into the Azure and
S3 blob companions. Phase 733 also added one `IBlobStorage` case (partial overlap). Cell format is
*passing / armed*:

| Seam | Azure / Azurite | AWS / LocalStack | GCP / fake-gcs-server |
|---|---|---|---|
| `IBlobStorage` | ✅ 21/21 | ✅ 21/21 | ⚠️ 16/21 |
| `IAuditSink` | ✅ 6/6 | ✅ 6/6 | ✅ 6/6 |
| `ISecretStore` | ⛔ no emulator exists | ✅ 9/9 | ⛔ no emulator exists |

**90 cases armed, 85 passing, 5 red, 2 permanently skipped** (the two `ISecretStore` cells with no
emulator in existence), plus the 7 always-on cases that need no emulator — 97 run in total, against
**7 run and 9 skipped** on an unarmed checkout.

**Every remaining red is one cause on one leg:** the GCP `DownloadRange` cases, an emulator-fidelity
limit rather than a companion defect (§ *The GCP blob rows*). **The Azure and AWS legs are wholly
green**, which is what made the CI arming below possible.

Two earlier clusters are closed. **The wrapped-vendor-exception class** — which had `ISecretStore`
on AWS at 1/9, broke GCP `Delete` idempotency, and (found later) killed the past-EOF handler on
Azure and S3 — is fixed across all four companions (§ *The AWS secrets rows*). **The unanimous
`DownloadRange past EOF` failure** turned out to be that same class rather than the contract
disagreement it looked like (§ below).

### CI arming (Phase 733)

The **Azurite leg is armed in CI** — the `cloud-parity` job in `.github/workflows/checks.yml` boots
Azurite, runs this pack, and asserts a case FLOOR as well as zero failures. The floor is the point:
an unarmed pack reports `Success!` and exits 0 having measured nothing, so a gate that read only the
exit status would report green while the leg silently returned to `pending`. **The LocalStack leg is
green too and is the next arming candidate** — it needs more setup (the `localstack-init` bucket
service, AWS credentials, and three env vars), which is the only reason it was not taken in the same
pass. The GCP leg is not armable while its emulator limit stands.

The `IAuditSink` row rides on the blob row: the three archive sinks each take an `IBlobStorage`
(`create name settings blobStorage`), so each cloud's sink binds over that same cloud's
emulator-backed storage. That is what makes audit parity reachable without a fourth emulator — and
it is, notably, the one row that is green on every cloud.

### The one finding that looked unanimous — `DownloadRange past EOF`, CLOSED 2026-08-27

`IBlobStorageContract` asserts that a range read starting at or beyond the blob's size returns
`Ok [||]`. **All three clouds failed it on the first run**, and the local in-memory double passed
it. That reads as a cross-cloud contract disagreement — the contract encoding local-only behaviour
no real object store exhibits, since HTTP 416 is what the protocol specifies — and Phase 732
recorded it as a contract decision needing its own phase rather than patching it.

**It was not a contract disagreement.** Phase 733 opened that decision and found the premise false
on inspection: all three companions **already** mapped 416 to `Ok [||]`, deliberately, each with a
comment saying so, since Phase 455. The mapping was **unreachable**. `Async.AwaitTask` can surface a
faulted task's `AggregateException` rather than the vendor exception inside it, so
`| :? RequestFailedException as ex when ex.Status = 416` (and the S3 and GCS equivalents) never
matched, and the 416 fell through the catch-all as an `Error`. The measured error strings say it
outright — Azure's read `One or more errors occurred. (The range specified is invalid …)`, which is
`AggregateException.Message` verbatim.

So this is the same wrapped-vendor-exception class as § *The AWS secrets rows*, in a fourth and
fifth companion. **The claim in that section that `ToolUp.Storage.AzureBlobStorage` is "not
affected: its equivalent cases pass" was wrong**, and the way it was wrong is worth keeping:
Azure's `404` arms are equally unreachable, but their fall-through is *also* an `Error`, so no case
could see the difference. Only the `416` arm has a fall-through that changes the ANSWER. A dead
catch clause is invisible wherever it and the catch-all agree.

| Cloud | Before | Now |
|---|---|---|
| Azure / Azurite | 416 wrapped → `Error` from the catch-all | ✅ matched through `(\|Unwrapped\|)` |
| AWS / LocalStack | 416 wrapped → `Error` from the catch-all | ✅ matched through `(\|Unwrapped\|)` |
| GCP / fake-gcs-server | same class, fixed 2026-08-27 | ✅ |

**The contract decision was still taken, on the evidence, and recorded** in the doc-comment on
`IBlobStorage.DownloadRange`: the seam keeps `Ok [||]` and implementations normalise. The consumer
sweep is why — `IContentRangeReader.ReadContentRange` and `IStreamingDatasetCodec.DecodeChunk` both
re-state past-EOF → `Ok [||]` as their own documented semantics, and the streaming paging path
treats any range-read error as "streaming unavailable" and silently falls back to materialising the
whole blob. Making end-of-stream an error would not raise anything; it would quietly abandon the
bounded read path on exactly the multi-GB vintages it exists for.

Phase 733 also pinned the **partial-overlap** clause (`offset < size < offset + length` → the
overlapping bytes only) with its own contract case, including the `offset = size` boundary where it
meets the fully-past clause. The clouds all clamp partial overlap natively and refuse only the
fully-past case, so the two clauses are answered by different backend paths — which is precisely why
the boundary between them is worth a case.

### The AWS secrets rows — a dead write path, found on the first run, FIXED 2026-08-27

`ISecretStore` on LocalStack scores **1/9**. The emulator is not the problem: a direct
`aws secretsmanager` probe against the same container does `create-secret`, `get-secret-value`,
`put-secret-value` and a correct `ResourceNotFoundException` on a missing id, all successfully.

The defect is in `ToolUp.Secrets.AwsSecretsManager`, and it is one root cause with three symptoms.
Every handler in that companion matches the vendor exception directly:

```fsharp skip=fragment
try
    let! response = client.GetSecretValueAsync req |> Async.AwaitTask
    return Some response.SecretString
with
| :? ResourceNotFoundException -> return None
```

but the exception arriving at that `with` is an **`AggregateException` wrapping**
`ResourceNotFoundException`, so `:? ResourceNotFoundException` never matches. The proof is in the
one case that has a catch-all: `DeleteSecret is idempotent on missing keys` returns
`Error "One or more errors occurred. (Secrets Manager can't find the specified secret.)"` — an
`AggregateException.Message` verbatim, reached via `| ex -> return Error ex.Message`. Consequently:

- **`GetSecret` on a missing key throws** instead of returning `None` (no catch-all, so it escapes).
- **`DeleteSecret` on a missing key returns `Error`** instead of `Ok`, breaking idempotency.
- **`SetSecret` cannot create a secret that does not already exist.** This is the serious one: the
  write path is `PutSecretValue`, falling back to `CreateSecret` *on `ResourceNotFoundException`* —
  and that fallback never fires. The companion's entire new-key write path is dead.

**The same class affects `ToolUp.Storage.GoogleCloudStorage`**, whose `Delete` carries
`| :? GoogleApiException as ex when ex.HttpStatusCode = HttpStatusCode.NotFound -> return Ok()` and
falls through to its catch-all for exactly the same reason. So this is a pattern across the cloud
companions, not a single slip.

> **Correction (Phase 733, 2026-08-27).** This paragraph originally continued
> "`ToolUp.Storage.AzureBlobStorage` is **not** affected: its equivalent cases pass." **That was
> wrong**, and it was wrong in an instructive way. Azure's `404` arms are equally unreachable — but
> their fall-through is *also* an `Error`, so no contract case can tell a matched 404 from an
> unmatched one. The only arm whose reachability changes an ANSWER is `416`, and that case *was*
> red; it was attributed to the contract rather than to this class. `AwsS3Storage` was affected
> identically. Both are fixed. **"Its cases pass" is not evidence a catch clause fires** — it is
> only evidence that the clause and the catch-all agree on the cases you happen to have.

**Fixed 2026-08-27.** Both companions gained a private `(|Unwrapped|)` active pattern (flatten the
`AggregateException`, take the single inner exception a one-Task await carries; a bare exception
passes through unchanged) and every vendor-typed catch site — plus the generic
`ex -> Error ex.Message` handlers, whose messages were the "One or more errors occurred" symptom —
now matches through it. Re-measured armed: `ISecretStore` on AWS **9/9**, GCP `Delete` idempotency
and GCP `DownloadRange past EOF` both green. An unmatched wrapped exception still rethrows the
original `AggregateException`, so nothing that used to escape is now swallowed.

**Phase 733 carried the same pattern into `AzureBlobStorage` and `AwsS3Storage`, scoped to their
`DownloadRange` handlers** — the arms its parity cases actually measure. **The broader sweep is
Phase 734, done the same day (2026-08-27):** every remaining direct vendor-exception type test
over an async await in the estate's companions now matches through `(|Unwrapped|)` — the rest of
`AwsS3Storage` (`Download`, `Exists`, `GetMetadata`, `currentETag`, and the conditional-write seam
733 flagged, whose `PreconditionFailed` test distinguishes `ETagMismatch` from
`ConditionalWriteFailure` — an arm whose reachability changes an answer, uncovered by any parity
case) + its encryption-at-rest validator, the rest of `AzureBlobStorage` (same seam),
`AzureKeyVaultSecretStore` (whose `GetSecret` 404 handler had no catch-all, so a wrapped 404
escaped raw), the three `IBlobEncryptionKeyResolver` companions (AWS KMS / Azure Key Vault / GCP
KMS — the KeyNotFound / KeyDestroyed crypto-shred classification), the SMTP and WebPush
notification sinks (vendor classification over non-generic `do! Task` awaits), the GA4 live
transport (no catch-alls — a wrapped exception escaped raw), and the `DbException` sites in
`ConnectorSupport.classify` + the Sql/Snowflake/Synapse connectors. Audited and deliberately
unchanged: `GcpSecretManager` + `VaultSecretStore` (pure BCL `HttpClient`, status-code branching,
no vendor exception types), **the audit sinks** (same — no vendor type tests), `LdapConnection`
(its vendor branch and catch-all produce identical results, and the discriminating
`ErrorCode = 49` test runs synchronously inside `Task.Run`), and the Voice providers (every
branch yields the same `Transient` classification). Re-measured armed after the sweep: two runs,
94 cases — 90/2/4 and 89/2/5 — the only red being the GCP `DownloadRange` emulator-hash cluster
below plus one flaky GCP `GetMetadata` DateTime parse (pre-existing, tracked separately).

### The GCP blob rows — 16/20 (14/20 before the `AggregateException` fix)

Four red, one group:

- **`DownloadRange` × 4 — emulator fidelity, not a companion defect.** Each fails with
  `Incorrect hash: expected 'wcrr5Q==' (base64), was 'AiwhMQ=='`. The Google client validates the
  downloaded bytes against the object's stored **whole-object** checksum; fake-gcs-server returns
  that whole-object hash on a *partial* response, so validation cannot succeed on any ranged read
  that returns bytes. Real GCS suppresses hash validation for ranged requests. Confirming which
  side is at fault needs the live-account binding, not this emulator — recorded here as a known
  emulator limit.

Two of the first run's six red were the `AggregateException` class above, **fixed 2026-08-27**:
`Delete is idempotent on missing blobs`, and `DownloadRange past EOF` (its 416 handler could not
fire through the wrapper; past-EOF returns no bytes, so the hash limit does not bite it).

### The GCP blob gap — the finding this phase was written to catch, now CLOSED

When Phase 193 shipped, **`GoogleCloudStorageConfig` could not be pointed at an emulator.** It
carried `BucketName`, `CredentialsJson` and `CredentialsJsonProvider`, and the companion built its
client via `StorageClient.Create`, which does **not** consult `STORAGE_EMULATOR_HOST` — verified at
the time: with that variable set, `Create` still walked the Application Default Credentials chain
and threw *"Your default credentials were not found."* Emulator support in
`Google.Cloud.Storage.V1` lives on `StorageClientBuilder` (`BaseUri` / `EmulatorDetection` /
`UnauthenticatedAccess`), which the companion did not expose. So the emulator was never the
obstacle — the companion's config surface was, which is exactly the class of surprise the phase
existed to surface.

**Closed 2026-08-26.** `GoogleCloudStorageConfig` gained `EndpointUrl: string option`, routed
through `StorageClientBuilder.BaseUri`, mirroring `AwsS3StorageConfig`. `AwsSecretsManagerConfig`
gained one in the same pass. Both legs are armed in `EmulatorLegs`, and Phase 732 then ran them —
note the `/storage/v1/` caveat above, which is the one way the GCS field does *not* mirror its AWS
model.

**The ratchet inverted rather than being deleted, and that distinction is the point.**
`EmulatorSeamCoverageTests` originally asserted the seams were *absent*, so that a companion could
not gain one silently while its leg stayed switched off. Now that both seams exist, the same three
tests assert they are **present** — a companion that quietly drops `EndpointUrl` again would
otherwise switch its leg back off in silence, which is the exact failure the file exists to
prevent. (An earlier version of this document told you to *delete* the test once the seam landed.
That was wrong, and following it would have removed the only guard on the seam at the moment the
seam started being depended upon. The ratchet's job did not end when the seam landed; it changed
direction.)

### The AWS secrets caveat — why it refuses rather than runs

LocalStack does emulate Secrets Manager. Until 2026-08-26 `AwsSecretsManagerConfig` was `{ Region }`
with no endpoint override, so the endpoint had to come from the AWS SDK's own resolution
(`AWS_ENDPOINT_URL_SECRETS_MANAGER`) — and *that* is what made the guard below load-bearing rather
than defensive boilerplate. Without the variable, the SDK resolved the **real** Secrets Manager for
the region, and the contract pack writes and deletes secrets: against a live account, on whatever
ambient credentials the machine has, with Secrets Manager's 7–30 day deletion-recovery window making
the mess durable.

The companion now takes `EndpointUrl`, so the leg passes the endpoint explicitly and that footgun is
gone by construction — an explicit `Some` cannot be absent by accident the way an environment
variable can. **The guard remains**: if `TOOLUP_PARITY_LOCALSTACK_SECRETS` is set and neither
`TOOLUP_PARITY_LOCALSTACK_SECRETS_ENDPOINT` nor `AWS_ENDPOINT_URL_SECRETS_MANAGER` is, the leg
refuses to run and says why. It is now asking a different question — *is this leg pointed
somewhere?* — and it still refuses an unpointed leg rather than defaulting to live AWS. A parity leg
must never be one missing variable away from that.

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
dotnet src/ToolUp.Cloud.Parity.Tests/bin/Debug/net10.0/ToolUp.Cloud.Parity.Tests.dll
```

With no emulators running: **7 passed, 9 ignored, 0 failed** — the 7 always-on cases (1 divergence
control + 3 divergence classes + 3 seam ratchets) and 9 skips (3 seams × 3 clouds). The pack is
sequenced by default per the Phase 617 Expecto/console deadlock rule. Verified unchanged by Phase
732, which is the baseline the armed run below is measured against.

Armed per the invocation at the top of this document: **94 run — 78 passed, 2 ignored, 15 failed,
13 errored** on first measurement, improving to **78 / 2 / 9 / 7** once the two lane defects (the
LocalStack Pro tag; the `/storage/v1/` endpoint shape) were corrected, and to the
**88 passed / 2 ignored / 6 failed / 0 errored** recorded in the coverage table once the
`AggregateException` class was fixed the same day. The remaining 6 are the two finding clusters
above.

**Read the case COUNT, never the exit code, and never the word "pending".** A run that reports the
ungated `7 passed, 9 ignored` under an environment you believe is armed has **failed to arm** — it
has not passed. That is the single most important thing to know about reading this pack's output,
because the two outcomes differ by nine lines of skip text that are easy to scroll past.
`--list-tests` prints each skip's reason and is the fastest way to see what a given machine is and
is not covering.

## Rollback

Delete `src/ToolUp.Cloud.Parity.Tests/`, remove it from `ToolUp.Forge.sln`, and delete
`compose.parity.yml` at the repo root. Nothing else references any of it; no shipped package or
deployed app is affected either way.

## Not covered

- **`ISecretStore` on Azure and GCP** — no emulator exists for either (above).
- **Cross-cloud *performance* parity.** The matrix asserts behavioural equivalence — same operations,
  same observable results. Latency and throughput differ per cloud by nature and are not asserted.
- **Emulator fidelity itself.** Azurite, LocalStack and fake-gcs-server approximate their services;
  a divergence only the real service exhibits stays invisible here. This narrows the live-deployment
  surprise surface, it does not eliminate it — the env-gated live-account bindings in
  `ToolUp.Platform.Tests` remain the check against the real thing. Phase 732's measured run shows
  the limit cutting the other way too: five GCP `DownloadRange` cases fail on an emulator
  *shortcoming* rather than a companion defect, so a red cell here is a question to investigate, not
  automatically a bug in the SDK.
- **CI.** The emulator legs are not armed in any CI job; they run permanently-pending inside
  `verify-all`, so only the 7 always-on cases gate. The reasoning, the blockers and the arming
  recipe are recorded in `.github/workflows/checks.yml` immediately above the `verify-all` job.
