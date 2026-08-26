module ToolUp.Cloud.Parity.Tests.EmulatorLegs

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets

// ─── Phase 193 — the emulator matrix's leg model ──────────────────────
//
// One "leg" is one cloud, represented by its local emulator. The matrix
// runs the SHARED contract packs across the legs so a behavioural
// difference between two clouds shows up as a contract failure in CI,
// rather than as a surprise during a second-cloud deployment (GP 12).
//
// Every leg is env-gated and every leg that cannot run says WHY, in one
// of three distinct categories (`LegSkip` below). The distinction is the
// point: "you didn't set the env var" and "this can never run as the SDK
// currently ships" are opposite signals, and collapsing them into a bare
// "skipped" is how a matrix quietly stops measuring anything. A fresh
// checkout with no emulators sets none of these vars and is green.

/// A cloud under parity test, named by the emulator that stands in for it.
type CloudLeg =
    /// Azure, via Azurite (Blob / Queue / Table emulator).
    | Azurite
    /// AWS, via LocalStack (S3 + Secrets Manager among many services).
    | LocalStack
    /// GCP, via fake-gcs-server (Cloud Storage emulator).
    | FakeGcs

/// Why a leg's contract pack is not executing. Three categories, because
/// they demand three different responses from whoever reads the CI log.
type LegSkip =
    /// The leg's gating env var is unset — the ordinary path on a fresh
    /// checkout and on any machine without the compose matrix running.
    /// Response: none needed; this is the designed default.
    | NotConfigured of envVar: string
    /// An emulator for this seam exists and could be configured, but the
    /// shipped companion exposes no way to point it at a non-production
    /// endpoint. Response: add the seam to the companion, then this leg
    /// starts running with no change to this harness.
    | NoCompanionSeam of companion: string * detail: string
    /// This cloud has no local emulator for this seam at all. Response:
    /// none available — the seam is not emulator-testable for this cloud,
    /// and the matrix should say so rather than imply coverage.
    | NoEmulatorForSeam of emulator: string * detail: string
    /// The leg is configured but a required companion setting is missing,
    /// and proceeding would reach the REAL cloud rather than the emulator.
    /// Response: fix the configuration. Refusing to run is deliberate —
    /// see `secretStoreFactory`.
    | UnsafeConfiguration of detail: string

module CloudLeg =

    /// Every leg, in matrix order.
    let all = [ Azurite; LocalStack; FakeGcs ]

    /// Display name — the cloud plus the emulator standing in for it, so a
    /// CI log line identifies both.
    let name leg =
        match leg with
        | Azurite -> "Azure/Azurite"
        | LocalStack -> "Aws/LocalStack"
        | FakeGcs -> "Gcp/fake-gcs-server"

    /// The single env var that arms this leg. Holds a connection string
    /// (Azurite) or an emulator endpoint URL (LocalStack, fake-gcs).
    let envVar leg =
        match leg with
        | Azurite -> "TOOLUP_PARITY_AZURITE"
        | LocalStack -> "TOOLUP_PARITY_LOCALSTACK"
        | FakeGcs -> "TOOLUP_PARITY_FAKEGCS"

module LegSkip =

    /// One-line reason, used as the `ptestCase` label so the skip and its
    /// cause are both visible in the CI output without opening a doc.
    let describe (skip: LegSkip) =
        match skip with
        | NotConfigured envVar -> $"skipped — %s{envVar} not set (emulator absent)"
        | NoCompanionSeam(companion, detail) -> $"skipped — no emulator seam on %s{companion}: %s{detail}"
        | NoEmulatorForSeam(emulator, detail) -> $"skipped — %s{emulator} does not emulate this seam: %s{detail}"
        | UnsafeConfiguration detail -> $"skipped — refusing to run against a non-emulator target: %s{detail}"

// ─── Env helpers ─────────────────────────────────────────────────────

let private env name =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

let private envOr fallback name =
    env name |> Option.defaultValue fallback

/// A fresh, collision-proof logical container per factory invocation. The
/// contract packs call the factory once per test case and several of them
/// assert on `List`, so two tests sharing a container would cross-
/// contaminate. All three companions map a ToolUp logical container to a
/// key prefix inside one bucket / root container, so a unique name here is
/// a unique prefix there — no provisioning per test.
let private unique (stem: string) =
    stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)

// ─── IBlobStorage per leg ────────────────────────────────────────────

/// Resolve an `IBlobStorage` factory for a leg, or the reason it cannot
/// run. The factory is invoked once per contract test case.
let blobStorageFactory (leg: CloudLeg) : Result<(unit -> IBlobStorage), LegSkip> =
    match leg with
    | Azurite ->
        // Azurite's well-known development connection string works
        // verbatim (`UseDevelopmentStorage=true`), which is why this leg
        // needs nothing but the one env var. Same convention as the
        // existing `AzureBlobStorageTests` binding.
        match env (CloudLeg.envVar Azurite) with
        | None -> Error(NotConfigured(CloudLeg.envVar Azurite))
        | Some connectionString ->
            Ok(fun () ->
                ToolUp.Storage.AzureBlobStorage.create {
                    ConnectionString = connectionString
                    RootContainer = unique "parity"
                    ConnectionStringProvider = None
                })

    | LocalStack ->
        // `EndpointUrl` is the companion's existing S3-compatible override
        // (MinIO / R2 / B2) — LocalStack is the same shape, so this leg
        // needs no companion change. NOTE: the companion deliberately does
        // NOT create the bucket, so the compose lane pre-creates it; see
        // `docs/migrations/193-multi-cloud-parity-conformance-matrix.md`.
        match env (CloudLeg.envVar LocalStack) with
        | None -> Error(NotConfigured(CloudLeg.envVar LocalStack))
        | Some endpointUrl ->
            let bucket = envOr "toolup-parity" "TOOLUP_PARITY_LOCALSTACK_BUCKET"
            let region = envOr "us-east-1" "TOOLUP_PARITY_LOCALSTACK_REGION"

            Ok(fun () ->
                ToolUp.Storage.AwsS3Storage.create {
                    BucketName = bucket
                    Region = region
                    EndpointUrl = Some endpointUrl
                })

    | FakeGcs ->
        // ARMED 2026-08-26 (tidy-drain). Phase 193 left this cell a named
        // skip because the emulator was fine and the SEAM was missing:
        // `GoogleCloudStorageConfig` carried no endpoint override, and the
        // companion built its client via `StorageClient.Create`, which does
        // NOT consult `STORAGE_EMULATOR_HOST` — verified then: with that
        // variable set, `Create` still walked the Application Default
        // Credentials chain and threw "Your default credentials were not
        // found." Emulator support in this SDK lives on
        // `StorageClientBuilder` (`BaseUri` / `UnauthenticatedAccess`).
        //
        // The companion now exposes `EndpointUrl`, mirroring
        // `AwsS3StorageConfig`, and routes an override through the builder
        // — so this leg arms exactly as 193 predicted it would. Like the
        // LocalStack leg above, the companion does NOT create the bucket:
        // the compose lane pre-creates it (fake-gcs-server takes a seeded
        // `-data` directory or a `POST /storage/v1/b` call). See
        // `docs/migrations/193-multi-cloud-parity-conformance-matrix.md`.
        match env (CloudLeg.envVar FakeGcs) with
        | None -> Error(NotConfigured(CloudLeg.envVar FakeGcs))
        | Some endpointUrl ->
            let bucket = envOr "toolup-parity" "TOOLUP_PARITY_FAKEGCS_BUCKET"

            Ok(fun () ->
                ToolUp.Storage.GoogleCloudStorage.create {
                    BucketName = bucket
                    CredentialsJson = None
                    CredentialsJsonProvider = None
                    EndpointUrl = Some endpointUrl
                })

// ─── ISecretStore per leg ────────────────────────────────────────────

/// Resolve an `ISecretStore` factory for a leg, or the reason it cannot
/// run. Only one of the three clouds has a secret-manager emulator at all,
/// which is itself a parity finding worth reporting rather than hiding.
let secretStoreFactory (leg: CloudLeg) : Result<(unit -> ISecretStore), LegSkip> =
    match leg with
    | Azurite ->
        // Not a configuration gap — Azurite implements Blob, Queue and
        // Table storage only. There is no Key Vault emulator from
        // Microsoft or anyone else, so `AzureKeyVault` cannot be brought
        // under an emulator-backed gate at all. It stays covered by the
        // env-gated live-account binding in `ToolUp.Platform.Tests`.
        Error(
            NoEmulatorForSeam(
                "Azurite",
                "it emulates Blob/Queue/Table only; no Azure Key Vault emulator exists, so ISecretStore "
                + "parity for Azure remains a live-account test"
            )
        )

    | LocalStack ->
        // LocalStack DOES emulate Secrets Manager. Until 2026-08-26 the
        // companion's config was `{ Region }` with no endpoint override, so
        // the endpoint had to come from the AWS SDK's own endpoint-URL
        // resolution (`AWS_ENDPOINT_URL_SECRETS_MANAGER`) — and THAT is
        // what made the guard below load-bearing rather than defensive
        // boilerplate. Without the variable, `create { Region = ... }`
        // resolved to the REAL AWS Secrets Manager endpoint for that
        // region, and the contract pack writes and deletes secrets: against
        // a live account, on whatever ambient credentials the machine has,
        // with Secrets Manager's 7-30 day deletion-recovery window making
        // the mess durable.
        //
        // The companion now takes `EndpointUrl`, so the leg passes the
        // endpoint explicitly and that footgun is gone by construction: an
        // explicit `Some` cannot be absent by accident the way an
        // environment variable can. The guard stays — it is now asking a
        // different question (is this leg pointed somewhere?) and it still
        // refuses to run an unpointed leg rather than defaulting to live
        // AWS. `AWS_ENDPOINT_URL_SECRETS_MANAGER` is accepted as a fallback
        // source for the URL so an existing compose lane keeps working.
        match env "TOOLUP_PARITY_LOCALSTACK_SECRETS" with
        | None -> Error(NotConfigured "TOOLUP_PARITY_LOCALSTACK_SECRETS")
        | Some region ->
            match
                env "TOOLUP_PARITY_LOCALSTACK_SECRETS_ENDPOINT"
                |> Option.orElse (env "AWS_ENDPOINT_URL_SECRETS_MANAGER")
            with
            | None ->
                Error(
                    UnsafeConfiguration(
                        "TOOLUP_PARITY_LOCALSTACK_SECRETS is set but neither "
                        + "TOOLUP_PARITY_LOCALSTACK_SECRETS_ENDPOINT nor AWS_ENDPOINT_URL_SECRETS_MANAGER is, "
                        + "so the AWS SDK would resolve the real Secrets Manager endpoint and this pack would "
                        + "write secrets to a live account — export an endpoint to arm this leg"
                    )
                )
            | Some endpointUrl ->
                Ok(fun () ->
                    ToolUp.Secrets.AwsSecretsManager.create {
                        Region = region
                        EndpointUrl = Some endpointUrl
                    })

    | FakeGcs ->
        // fake-gcs-server is a Cloud Storage emulator only. Google ships no
        // Secret Manager emulator, so this cell has no emulator-backed form.
        Error(
            NoEmulatorForSeam(
                "fake-gcs-server",
                "it emulates Cloud Storage only; no GCP Secret Manager emulator exists, so ISecretStore "
                + "parity for GCP remains a live-project test"
            )
        )

// ─── IAuditSink per leg ──────────────────────────────────────────────

// The archive sinks each take an `IBlobStorage` (`create name settings
// blobStorage`), so the audit row of the matrix rides on the blob row: each
// cloud's archive sink binds over that same cloud's emulator-backed
// storage. That composition is what makes IAuditSink parity reachable
// without a fourth emulator — and it means a blob-leg skip correctly
// propagates to the audit leg.

// The contract pack's `verifyDelivered` callback is handed only the sink,
// but verifying an archive means listing the container the sink wrote to.
// Each factory invocation mints a unique sink name and registers the
// (storage, container) pair it wrote against, so the callback can recover
// them from `sink.Name`.
let private archiveBindings = ConcurrentDictionary<string, IBlobStorage * string>()

/// Resolve an `IAuditSink` factory plus the pack's `verifyDelivered`
/// callback for a leg, or the reason it cannot run.
let auditSinkBinding
    (leg: CloudLeg)
    : Result<(unit -> IAuditSink) * (IAuditSink -> AuditEnvelope list list -> unit), LegSkip> =
    match blobStorageFactory leg with
    | Error skip -> Error skip
    | Ok storageFactory ->
        let factory () =
            let storage = storageFactory ()
            let container = unique "audit-archive"
            let sinkName = unique "parity-archive"

            let sink =
                match leg with
                | Azurite ->
                    ToolUp.Platform.AuditSinks.AzureBlobArchive.create
                        sinkName
                        {
                            Container = container
                            PathPrefix = None
                        }
                        storage
                | LocalStack ->
                    ToolUp.Platform.AuditSinks.S3Archive.create
                        sinkName
                        {
                            Container = container
                            PathPrefix = None
                        }
                        storage
                | FakeGcs ->
                    ToolUp.Platform.AuditSinks.GcsArchive.create
                        sinkName
                        {
                            Container = container
                            PathPrefix = None
                        }
                        storage

            archiveBindings[sinkName] <- (storage, container)
            sink

        // One archived blob per non-empty delivered batch — the invariant
        // all three archive sinks document and the only cross-cloud
        // observable the pack can check without decoding the gzip.
        let verifyDelivered (sink: IAuditSink) (batches: AuditEnvelope list list) =
            match archiveBindings.TryGetValue sink.Name with
            | false, _ -> failwithf "parity harness: no archive binding registered for sink %s" sink.Name
            | true, (storage, container) ->
                let expected = batches |> List.filter (List.isEmpty >> not) |> List.length
                let archived = storage.List(container, "") |> Async.RunSynchronously

                if List.length archived <> expected then
                    failwithf
                        "%s: expected %d archived blob(s) for %d delivered batch(es), found %d in %s"
                        (CloudLeg.name leg)
                        expected
                        (List.length batches)
                        (List.length archived)
                        container

        Ok(factory, verifyDelivered)