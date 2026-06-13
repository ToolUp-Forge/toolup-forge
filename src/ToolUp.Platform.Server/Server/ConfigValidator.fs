module ToolUp.Platform.ConfigValidator

open System
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets

// ─── First-party config validators (Phase 9m) ────────────────────────
//
// Two SDK-built-in validators against the abstract storage / secret
// interfaces. Both register automatically in `compose` whenever the
// corresponding service is registered (i.e. always, since both are
// mandatory for any non-trivial deployment). Companion validators for
// remote dependencies (OIDC, Redis, SMTP) live in the companion's own
// project and self-register the same way.

/// `_platform` container is reserved for SDK-level state — using it
/// for the preflight sentinel avoids polluting any tenant scope and
/// runs against a container the SDK already creates at startup.
[<Literal>]
let private platformContainer = "_platform"

[<Literal>]
let private blobSentinelKey = "preflight/sentinel.bin"

[<Literal>]
let private secretSentinelKey = "_platform_preflight_canary"

/// Shared body for the "gated-refusal" auth validators (`csrf-default-mode`,
/// `sse-auth-mode`, `header-auth-mode`, `encrypted-secret-store-mode`,
/// `oauth-secret-encryption-mode`). Each emits its result only when the
/// deployment requires auth (`DeploymentConfig.requiresAnyAuth`) AND its
/// own deployment-unsafe condition holds; `Ok` otherwise. Centralises the
/// `requiresAnyAuth` gate every gated validator repeats, so each
/// validator's `Validate()` now expresses only its own predicate +
/// message. `whenUnsafe` is a thunk so a validator can compute message
/// detail (e.g. the secret-store state description) lazily — only on the
/// failing path. Validators with extra ctor deps (`authProvider`,
/// `secretStore`) close over them in the `unsafeCondition` thunk.
let gatedAuthValidation
    (config: ServerConfig)
    (unsafeCondition: unit -> bool)
    (whenUnsafe: unit -> ValidationResult)
    : Async<ValidationResult> =
    async {
        if DeploymentConfig.requiresAnyAuth config && unsafeCondition () then
            return whenUnsafe ()
        else
            return Ok
    }

/// Storage preflight: writes a sentinel blob, reads it back byte-for-
/// byte, and deletes it. Heavier than Phase 9k's `BlobStorageHealthCheck`
/// (which only calls `Exists`) — preflight runs once at startup so the
/// extra round-trip cost is paid once per deploy, not on every
/// `/ready` poll. A round-trip mismatch surfaces silent corruption /
/// permission gaps that an `Exists` probe would miss.
type BlobStorageValidator(storage: IBlobStorage, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "blob-storage"
        member _.Timeout = timeout

        member _.Validate() = async {
            let payload =
                System.Text.Encoding.UTF8.GetBytes(sprintf "preflight-%s" (DateTime.UtcNow.ToString("o")))

            try
                let! uploadResult = storage.Upload(platformContainer, blobSentinelKey, payload)

                match uploadResult with
                | Result.Error msg -> return Error(sprintf "sentinel upload failed: %s" msg)
                | Result.Ok _ ->
                    let! downloadResult = storage.Download(platformContainer, blobSentinelKey)

                    match downloadResult with
                    | Result.Error msg -> return Error(sprintf "sentinel download failed: %s" msg)
                    | Result.Ok readback ->
                        // Best-effort delete — a delete failure isn't an
                        // outage signal (the sentinel is keyed at a fixed
                        // path so it'll be overwritten on the next deploy).
                        let! _ = storage.Delete(platformContainer, blobSentinelKey)

                        if readback = payload then
                            return Ok
                        else
                            return
                                Error "sentinel readback mismatch (write succeeded but read returned unexpected bytes)"
            with ex ->
                return Error(sprintf "sentinel write/read/delete threw: %s" ex.Message)
        }

/// Secret-store preflight: reads a sentinel key under the `_platform`
/// scope. `None` is `Ok` — the sentinel is intentionally optional;
/// merely calling `GetSecret` exercises the read path and surfaces
/// permission / decryption / connectivity failures. An exception is
/// `Error` because it means the secret store itself is broken, not
/// that a particular key is missing.
type SecretStoreValidator(secretStore: ISecretStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "secret-store"
        member _.Timeout = timeout

        member _.Validate() = async {
            try
                let! _ = secretStore.GetSecret(platformContainer, secretSentinelKey)
                return Ok
            with ex ->
                return Error(sprintf "secret store read failed: %s" ex.Message)
        }