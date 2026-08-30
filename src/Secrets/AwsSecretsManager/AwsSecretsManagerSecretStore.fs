module ToolUp.Secrets.AwsSecretsManager

open System
open Amazon
open Amazon.SecretsManager
open Amazon.SecretsManager.Model
open ToolUp.Platform.Secrets

// ─── Configuration ───────────────────────────────────────────────────
//
// Phase 2a — AWS Secrets Manager implementation of `ISecretStore`.
// Mirrors the `src/Storage/AwsS3Storage` companion shape: one record-
// typed config, a `create` helper, and a `fromEnv` resolver. Activated
// via `TOOLUP_SECRET_STORE=aws-secrets-manager` + `TOOLUP_AWS_SECRETS_REGION`
// in `src/ToolUpApp-Server/Server.fs`.
//
// IAM. The caller's IAM role / user must hold the four Secret operations
// on resources matching the deployment's name prefix:
//   secretsmanager:GetSecretValue
//   secretsmanager:CreateSecret + secretsmanager:PutSecretValue
//   secretsmanager:DeleteSecret
//   secretsmanager:ListSecrets   (no resource-level filter — listing is
//                                 account-wide; we filter client-side
//                                 by name prefix)
// Resource scoping example:
//   arn:aws:secretsmanager:eu-west-2:123:secret:toolup/*
// Minimum useful policy snippet in the companion README.
//
// Credential resolution. AWS SDK default chain — env vars
// (`AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY`), `~/.aws/credentials`
// + `AWS_PROFILE`, EC2 instance profile, IAM role for ECS tasks, IAM
// Roles Anywhere, AWS SSO. ToolUp code never touches credentials
// directly. The companion takes only the region in config.
//
// Cross-tenant isolation (GP 4). Single AWS account / region holds
// every ToolUp scope as a slash-prefixed secret: `toolup/{scopeId}/{key}`.
// AWS Secrets Manager allows alphanumeric + `/`, `_`, `+`, `=`, `.`,
// `@`, `-` in secret names, so most reasonable scope shapes pass
// through unmodified. The prefix encodes the scope; a CloudTrail audit
// shows exactly which scope each request touched.
//
// Cloud-KMS-native at-rest encryption. Secrets Manager encrypts every
// secret at rest with KMS (default: AWS-managed `aws/secretsmanager`
// CMK; deployments can specify a customer-managed CMK at Create time).
// Wrapping this companion in `EncryptedSecretStore` would add a
// redundant envelope — `Server.fs` does NOT wrap cloud-KMS companions,
// and the Phase 6l.E plaintext-secrets validator recognises
// `TOOLUP_SECRET_STORE=aws-secrets-manager` as equivalent to
// `EncryptedSecretStore` for the master-key gate.
//
// Deletion semantics. AWS Secrets Manager defaults to **scheduled
// deletion** with a 7-30 day recovery window. During the window
// `GetSecretValue` returns `ResourceNotFoundException` (the secret is
// invisible to the caller), but the secret can be recovered via
// `RestoreSecret`. This companion uses scheduled deletion with the
// default 7-day window. Re-creating a secret with the same name during
// the window fails with `InvalidRequestException`; if a deployment
// needs immediate re-creation, override the deletion call externally
// or set `ForceDeleteWithoutRecovery=true` via a config knob (deferred
// — no current need). Tests avoid this by using GUID-suffixed scope
// IDs that don't collide across runs.

/// Configuration for `AwsSecretsManagerSecretStore`. Takes the AWS
/// region; credentials flow through the AWS SDK default chain.
type AwsSecretsManagerConfig = {
    /// AWS region in SDK string form ("us-east-1", "eu-west-2", ...).
    /// Read from `TOOLUP_AWS_SECRETS_REGION`.
    Region: string
    /// Optional Secrets-Manager-compatible endpoint override (LocalStack,
    /// or any AWS-API-compatible endpoint) — the mirror of
    /// `AwsS3StorageConfig.EndpointUrl`. `None` (default) preserves today's
    /// behaviour exactly: the region alone selects the endpoint, subject to
    /// the AWS SDK's own `AWS_ENDPOINT_URL_SECRETS_MANAGER` resolution.
    ///
    /// Prefer this field over that variable wherever the endpoint is known
    /// at composition time. The variable's failure mode is silent and
    /// expensive: when it is absent the SDK resolves the REAL Secrets
    /// Manager for the region, and a caller that believed it was pointed at
    /// an emulator writes and deletes secrets in a live account — with a
    /// 7-30 day deletion-recovery window making the mess durable. An
    /// explicit `Some` cannot be absent by accident.
    EndpointUrl: string option
}

module AwsSecretsManagerConfig =
    let defaults = {
        Region = "us-east-1"
        EndpointUrl = None
    }

// ─── Naming ──────────────────────────────────────────────────────────

module private Naming =
    // AWS Secrets Manager secret names: 1-512 chars, `[a-zA-Z0-9/_+=.@-]`.
    // Most ToolUp scope shapes pass through unmodified — `_platform`,
    // `team-{guid}`, `user-{guid}` all use only allowed chars.
    // Non-allowed chars become `-` defensively; the round-trip via
    // `ListKeys` returns whatever AWS stored, so naming round-trips
    // perfectly for any in-spec input.
    let private isAllowed c =
        (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c = '_'
        || c = '+'
        || c = '='
        || c = '.'
        || c = '@'
        || c = '-'

    let sanitise (s: string) =
        s |> String.map (fun c -> if isAllowed c then c else '-')

    let secretName (scopeId: string) (key: string) =
        $"toolup/{sanitise scopeId}/{sanitise key}"

    let scopePrefix (scopeId: string) = $"toolup/{sanitise scopeId}/"

    let stripPrefix (prefix: string) (name: string) =
        if name.StartsWith prefix then
            Some(name.Substring prefix.Length)
        else
            None

// ─── Exception unwrapping ────────────────────────────────────────────

/// AWS SDK exceptions surface at this companion's `with` handlers
/// wrapped in `AggregateException` — proven by the first armed
/// cloud-parity run (2026-08-27): every direct
/// `:? ResourceNotFoundException` test below sat dead, so `SetSecret`'s
/// CreateSecret fallback never fired and the companion could not create
/// a secret that did not already exist. Match through the wrapper:
/// flatten and take the single inner exception a one-Task await
/// carries; a bare exception passes through unchanged, so an unmatched
/// case still rethrows the original.
let private (|Unwrapped|) (ex: exn) =
    match ex with
    | :? AggregateException as aggregate ->
        match Seq.tryHead (aggregate.Flatten().InnerExceptions) with
        | Some inner -> inner
        | None -> ex
    | _ -> ex

// ─── ISecretStore implementation ─────────────────────────────────────

/// AWS Secrets Manager implementation of `ISecretStore`. One
/// `AmazonSecretsManagerClient` per instance; the SDK client is
/// thread-safe and designed for reuse.
type AwsSecretsManagerSecretStore(config: AwsSecretsManagerConfig) =
    let clientConfig = AmazonSecretsManagerConfig()
    do clientConfig.RegionEndpoint <- RegionEndpoint.GetBySystemName config.Region

    do
        match config.EndpointUrl with
        | Some url -> clientConfig.ServiceURL <- url
        | None -> ()
    // Concrete client, not `IAmazonSecretsManager`: under AWS SDK v4 the
    // interface carries static abstract members and naming it as an
    // ordinary type raises FS3536. Only instance methods are used.
    let client = new AmazonSecretsManagerClient(clientConfig)

    /// Phase 457 — Secrets Manager encrypts every secret at rest under a
    /// KMS key (the `aws/secretsmanager` managed key unless the secret was
    /// created against a customer-managed one). Declared so a deployment on
    /// this companion passes the at-rest preflight on the store's own
    /// evidence rather than on the `TOOLUP_SECRET_STORE` spelling — the
    /// switch says what was ASKED for, this says what was composed.
    interface ISecretStoreAtRestPosture with
        member _.AtRestPosture =
            EncryptsAtRest "AWS Secrets Manager, KMS-managed encryption at rest"

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            let name = Naming.secretName scopeId key
            let req = GetSecretValueRequest()
            req.SecretId <- name

            try
                let! response = client.GetSecretValueAsync req |> Async.AwaitTask
                // SecretString is null for binary secrets; ToolUp only
                // stores strings. Both shapes return None — the caller
                // sees the same "secret missing" result.
                return
                    if isNull response.SecretString then
                        None
                    else
                        Some response.SecretString
            with
            | Unwrapped(:? ResourceNotFoundException) -> return None
            | Unwrapped(:? InvalidRequestException) ->
                // Secret in a scheduled-deletion or otherwise non-
                // active state; surface as "not found" per the
                // ISecretStore contract.
                return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            let name = Naming.secretName scopeId key
            // PutSecretValue is the hot path (overwrite existing). On
            // ResourceNotFound, fall back to CreateSecret. Cheaper than
            // a probe + branch for the common idempotent-write case.
            let put = PutSecretValueRequest()
            put.SecretId <- name
            put.SecretString <- value

            try
                let! _ = client.PutSecretValueAsync put |> Async.AwaitTask
                return Ok()
            with Unwrapped(:? ResourceNotFoundException) ->
                let create = CreateSecretRequest()
                create.Name <- name
                create.SecretString <- value

                try
                    let! _ = client.CreateSecretAsync create |> Async.AwaitTask
                    return Ok()
                with Unwrapped ex ->
                    return Error ex.Message
        }

        member _.DeleteSecret(scopeId, key) = async {
            let name = Naming.secretName scopeId key
            let req = DeleteSecretRequest()
            req.SecretId <- name
            // Default scheduled-deletion (7-day recovery window).
            // GetSecretValue immediately returns "not found" once
            // delete is accepted — the contract's "delete then get
            // returns None" assertion passes.

            try
                let! _ = client.DeleteSecretAsync req |> Async.AwaitTask
                return Ok()
            with
            | Unwrapped(:? ResourceNotFoundException) -> return Ok()
            | Unwrapped(:? InvalidRequestException) ->
                // Already in scheduled-deletion state — idempotent.
                return Ok()
            | Unwrapped ex -> return Error ex.Message
        }

        member _.ListKeys(scopeId) = async {
            let prefix = Naming.scopePrefix scopeId
            let results = System.Collections.Generic.List<string>()
            let mutable nextToken: string = null
            let mutable firstPage = true

            while firstPage || not (isNull nextToken) do
                firstPage <- false
                let req = ListSecretsRequest()
                // Server-side name filter narrows the wire payload to
                // only this scope's secrets; the prefix-strip below
                // still runs in case AWS's prefix matcher is looser
                // than we want.
                let nameFilter = Filter()
                nameFilter.Key <- FilterNameStringType.Name
                nameFilter.Values <- System.Collections.Generic.List<string>([ prefix ])
                req.Filters <- System.Collections.Generic.List<Filter>([ nameFilter ])

                if not (isNull nextToken) then
                    req.NextToken <- nextToken

                let! response = client.ListSecretsAsync req |> Async.AwaitTask

                // AWS SDK v4 returns null (not an empty list) when the page
                // has no secrets — e.g. a scope with nothing stored yet.
                if not (isNull response.SecretList) then
                    for entry in response.SecretList do
                        match Naming.stripPrefix prefix entry.Name with
                        | Some k -> results.Add k
                        | None -> ()

                nextToken <- response.NextToken

            return results |> List.ofSeq
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `ISecretStore` from an `AwsSecretsManagerConfig`. The
/// `AmazonSecretsManagerClient` is initialised lazily on first request
/// — no startup work, no eager auth probe. Bad region / missing
/// credentials surface on first call as an SDK exception, not at
/// composition.
let create (config: AwsSecretsManagerConfig) : ISecretStore =
    new AwsSecretsManagerSecretStore(config) :> ISecretStore

/// Read the region from `TOOLUP_AWS_SECRETS_REGION` and construct an
/// `AwsSecretsManagerSecretStore`. Returns `None` when the env var is
/// unset / empty so the deployment falls back to whatever the
/// composition root chose (typically `EncryptedSecretStore` over
/// `FileSecretStore`).
let fromEnv () : ISecretStore option =
    match Environment.GetEnvironmentVariable ToolUp.Platform.ConfigKeys.Names.awsSecretsRegion with
    | null
    | "" -> None
    | region ->
        // No env key for the endpoint override, deliberately — see the
        // field's doc comment. The whole point of the field is to replace
        // an environment-resolved endpoint with an explicit one.
        Some(create { Region = region; EndpointUrl = None })

// ─── Phase 9c portability audit (six rules) ──────────────────────────
//
// 1. Identity by value — `ISecretStore` returns `string option` and
//    `Result<unit, string>`; never an AWS SDK live handle.
// 2. Async at every boundary — every interface method returns
//    `Async<_>`; SDK Tasks bridged via `Async.AwaitTask`.
// 3. Retry as data — none expressed by this companion. Caller-supplied
//    retry / backoff lives in the AWS SDK's `RetryPolicy` on
//    `AmazonSecretsManagerConfig` (set by the deployment if needed);
//    the companion stays a pure pass-through.
// 4. Stateless between calls — the cached `AmazonSecretsManagerClient`
//    is per-region not per-secret; no in-memory state survives across
//    method calls. Distributed-safe: any node with the same IAM
//    identity produces identical results.
// 5. No cross-shard ordering — Secrets Manager makes no global
//    ordering promises, and this companion makes none either.
//    `ListKeys` order is whatever `ListSecretsAsync` returns
//    (token-paginated, no guaranteed sort).
// 6. Precision at the lower bound — N/A; `ISecretStore` has no timing
//    semantics.