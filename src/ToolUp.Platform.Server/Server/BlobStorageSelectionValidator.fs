module ToolUp.Platform.BlobStorageSelectionValidator

open System
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation

// ─── Gap #2 — TOOLUP_BLOB_STORAGE silent fallback to local ──────────
//
// The composition root in `ToolUpApp-Server/Server.fs` reads
// `TOOLUP_BLOB_STORAGE` and dispatches to a cloud companion's
// `fromEnv ()` resolver. If the cloud-required env vars (e.g.
// `TOOLUP_AWS_S3_BUCKET` for s3) are unset or malformed, the resolver
// returns `None`, the composition logs a `Warn`, and silently falls
// back to `LocalFileStorage`.
//
// Production failure mode: operator intends S3 / Azure / GCS, sets
// `TOOLUP_BLOB_STORAGE=s3`, but typoes or forgets `TOOLUP_AWS_S3_BUCKET`.
// Deployment boots successfully on `LocalFileStorage`. On a multi-
// replica deployment each replica writes to its own ephemeral disk;
// cross-replica reads return 404 or stale data. The Warn line is buried
// in startup logs.
//
// This validator catches the gap at preflight: when the env var
// declares a cloud backend but the resolved `IBlobStorage` is
// `LocalFileStorage`, refuse startup with `Error`. Escape hatch
// `TOOLUP_ACCEPT_LOCAL_FALLBACK=1` for operators who legitimately want
// the fallback (e.g. graceful staging without all credentials).

[<Literal>]
let private BlobStorageEnvVar = "TOOLUP_BLOB_STORAGE"

[<Literal>]
let private EscapeHatchEnvVar = "TOOLUP_ACCEPT_LOCAL_FALLBACK"

let private cloudBackends = Set.ofList [ "azure"; "s3"; "gcs" ]

let private envValue (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some(v.Trim().ToLowerInvariant())

let private isEscapeHatchSet () =
    match envValue EscapeHatchEnvVar with
    | Some("1" | "true" | "yes" | "on") -> true
    | _ -> false

/// Gap #2 — config validator that refuses startup when
/// `TOOLUP_BLOB_STORAGE` declares a cloud backend (azure / s3 / gcs)
/// but the resolved `IBlobStorage` runtime is `LocalFileStorage`.
type BlobStorageSelectionValidator(storage: IBlobStorage, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "blob-storage-selection"
        member _.Timeout = timeout

        member _.Validate() = async {
            match envValue BlobStorageEnvVar with
            | Some declared when Set.contains declared cloudBackends ->
                let isLocal = storage.GetType() = typeof<LocalFileStorage.LocalFileStorage>

                if isLocal && not (isEscapeHatchSet ()) then
                    return
                        Error(
                            sprintf
                                "TOOLUP_BLOB_STORAGE=%s but the resolved IBlobStorage is LocalFileStorage — the cloud companion's fromEnv() returned None (missing or malformed credential env vars), and the composition root silently fell back to local disk. On a multi-replica deployment this means each replica writes to its own ephemeral disk; cross-replica reads return 404 or stale data. Set the cloud companion's required env vars (e.g. TOOLUP_AWS_S3_BUCKET for s3, TOOLUP_AZURE_STORAGE_CONNECTION_STRING for azure, TOOLUP_GCS_BUCKET for gcs — see DEPLOYMENT.md), or set TOOLUP_BLOB_STORAGE=local to use local disk explicitly. Override with TOOLUP_ACCEPT_LOCAL_FALLBACK=1 if you legitimately want the fallback (e.g. staging without all credentials)."
                                declared
                        )
                else
                    return Ok
            | _ ->
                // Env var unset or =local — the composition root used
                // local explicitly. Nothing to refuse.
                return Ok
        }