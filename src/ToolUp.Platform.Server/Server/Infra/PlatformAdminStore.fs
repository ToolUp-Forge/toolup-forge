module ToolUp.Platform.PlatformAdminStore

open System
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 4b — blob-backed Platform Admin store ──────────────────────
//
// `BlobBackedPlatformAdminStore` is the SDK-default `IPlatformAdminStore`
// implementation. Persists a flat JSON array of userIds at
// `_platform/platform-admins.json` and emits `PlatformAdminAssigned` /
// `PlatformAdminRevoked` audit events under `_platform` scope on every
// successful mutation.
//
// **Concurrency.** Reads are lock-free — a single blob fetch + parse.
// Writes serialise through a process-wide `SemaphoreSlim(1, 1)` because
// the assign / revoke operations are read-modify-write: load list,
// mutate, save. Without the lock, two concurrent assignments would each
// load the same baseline, append their target, and the second write
// would clobber the first. Admin operations are rare, so global write
// serialisation is fine — per-key locking would be over-engineering.
// Single-instance only; the planned distributed companion (Phase 9c
// half 2) will move to a CAS-based blob update or a Redis-backed
// counterpart.
//
// **Bootstrap.** The `bootstrap` function reads
// `TOOLUP_INITIAL_PLATFORM_ADMIN` and assigns the named user as the
// initial admin if and only if the current admin list is empty. Wired
// into `compose` (Phase 4b commit 4c). One-shot — once any admin
// exists, subsequent restarts no-op even if the env var is still set.
// Wipe + re-bootstrap is the documented disaster-recovery path.

let private platformContainer = "_platform"
let private blobName = "platform-admins.json"
let private bootstrapActor = "_bootstrap"

[<Literal>]
let private initialAdminEnvVar = "TOOLUP_INITIAL_PLATFORM_ADMIN"

/// Phase 230 — explicit second opt-in required for the
/// `AutoBootstrapDevAdmin` fallback to elevate in an auth-requiring
/// deployment. A dev-convenience field that leaks into production
/// (especially behind a TLS-terminating proxy, where `RequireHttps = false`
/// makes the deployment indistinguishable from local dev) can otherwise
/// silently grant Platform Admin to the first sign-in. Requiring a second,
/// deliberate env var means a single leaked field can never escalate.
/// Public so the preflight validator references the same name.
[<Literal>]
let allowDevAdminBootstrapEnvVar = "TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP"

module private Json =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let serialize (admins: string list) : byte[] =
        admins
        |> List.toArray
        |> fun arr -> JsonSerializer.Serialize(arr, options)
        |> Encoding.UTF8.GetBytes

    let deserialize (bytes: byte[]) : string list =
        try
            let json = Encoding.UTF8.GetString bytes

            JsonSerializer.Deserialize<string array>(json, options) |> Array.toList
        with _ -> []

/// Default `IPlatformAdminStore` impl. Persists to `_platform/platform-
/// admins.json` via the injected `IBlobStorage`; emits audit events via
/// the injected `IAuditLog` after every successful mutation.
type BlobBackedPlatformAdminStore(storage: IBlobStorage, auditLog: IAuditLog) =
    let writeLock = new SemaphoreSlim(1, 1)

    let load () = async {
        let! result = storage.Download(platformContainer, blobName)

        match result with
        | Ok bytes -> return Json.deserialize bytes
        | Error _ -> return []
    }

    let save (admins: string list) = async {
        let bytes = Json.serialize admins
        let! result = storage.Upload(platformContainer, blobName, bytes)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error e
    }

    let withWriteLock (op: unit -> Async<'T>) = async {
        do! writeLock.WaitAsync() |> Async.AwaitTask

        try
            return! op ()
        finally
            writeLock.Release() |> ignore
    }

    interface IPlatformAdminStore with
        member _.IsPlatformAdmin userId = async {
            let! admins = load ()
            return List.contains userId admins
        }

        member _.AssignPlatformAdmin(actorUserId, targetUserId) =
            withWriteLock (fun () -> async {
                let! existing = load ()

                if List.contains targetUserId existing then
                    return Ok() // idempotent — no audit re-emission
                else
                    let updated = targetUserId :: existing
                    let! result = save updated

                    match result with
                    | Ok() ->
                        do!
                            auditLog.Record(
                                platformContainer,
                                PlatformAdminAssigned {
                                    Actor = actorUserId
                                    TargetUserId = targetUserId
                                }
                            )

                        return Ok()
                    | Error e -> return Error e
            })

        member _.RevokePlatformAdmin(actorUserId, targetUserId) =
            withWriteLock (fun () -> async {
                let! existing = load ()

                if not (List.contains targetUserId existing) then
                    return Ok() // idempotent — no audit re-emission
                elif existing.Length <= 1 then
                    return Error "cannot revoke the last remaining Platform Admin"
                else
                    let updated = existing |> List.filter (fun id -> id <> targetUserId)
                    let! result = save updated

                    match result with
                    | Ok() ->
                        do!
                            auditLog.Record(
                                platformContainer,
                                PlatformAdminRevoked {
                                    Actor = actorUserId
                                    TargetUserId = targetUserId
                                }
                            )

                        return Ok()
                    | Error e -> return Error e
            })

        member _.ListPlatformAdmins() = load ()

/// Bootstrap the initial Platform Admin. Wired into `compose` (Phase 4b
/// commit 4c); fires once at startup.
///
/// **Source priority:**
///   1. `TOOLUP_INITIAL_PLATFORM_ADMIN` env var — production path.
///   2. `ServerConfig.AutoBootstrapDevAdmin` — dev convenience set by
///      deployment composition roots inside their own `#if DEBUG`. Logs
///      at `Warn` to clearly mark the dev path in startup output.
///
/// No-ops when both sources are unset, OR when the admin list is already
/// populated (one-shot). The audit event is emitted by
/// `IPlatformAdminStore.AssignPlatformAdmin` itself with
/// `Actor = "_bootstrap"`. Failures log at `Error` and do NOT abort
/// startup — a deployment without a bootstrap admin can still run; the
/// operator can assign one later via direct blob manipulation or by
/// re-running with a source set against an empty admin list.
let bootstrap
    (logger: ILogger)
    (autoBootstrapDevAdmin: string option)
    (requiresAuth: bool)
    (store: IPlatformAdminStore)
    =
    async {
        let envValue = Environment.GetEnvironmentVariable initialAdminEnvVar

        // Phase 230 — the dev fallback only elevates in an auth-requiring
        // deployment when TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP is explicitly set.
        // A non-auth (anonymous) deployment needs no opt-in; the env path
        // (priority 1) is never gated.
        let devFallbackAllowed =
            if not requiresAuth then
                true
            else
                let v = Environment.GetEnvironmentVariable allowDevAdminBootstrapEnvVar
                not (String.IsNullOrWhiteSpace v) && v.Trim() <> "0"

        let target =
            if not (String.IsNullOrWhiteSpace envValue) then
                Some(envValue.Trim(), sprintf "env var %s" initialAdminEnvVar, false)
            else
                match autoBootstrapDevAdmin with
                | Some id when not (System.String.IsNullOrWhiteSpace id) ->
                    if devFallbackAllowed then
                        Some(id.Trim(), "ServerConfig.AutoBootstrapDevAdmin (dev convenience)", true)
                    else
                        // Auth-requiring deployment without the explicit
                        // TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP opt-in — fail closed
                        // (no elevation) and surface it loudly.
                        logger.Error(
                            sprintf
                                "[PlatformAdmin] AutoBootstrapDevAdmin = Some \"%s\" is set in an auth-requiring deployment but %s is not set — REFUSING to bootstrap a dev admin (this would be a first-sign-in privilege escalation in production). Set %s for production, or %s=1 for a deliberate local auth-dev bootstrap."
                                (id.Trim())
                                allowDevAdminBootstrapEnvVar
                                initialAdminEnvVar
                                allowDevAdminBootstrapEnvVar,
                            None
                        )

                        None
                | _ -> None

        match target with
        | None -> ()
        | Some(trimmed, source, isDevFallback) ->
            let! existing = store.ListPlatformAdmins()

            if not (List.isEmpty existing) then
                // Already-populated — bootstrap is one-shot. No-op.
                ()
            else
                let! result = store.AssignPlatformAdmin(bootstrapActor, trimmed)

                match result with
                | Ok() ->
                    let message =
                        sprintf "[PlatformAdmin] Bootstrapped initial admin from %s: %s" source trimmed

                    if isDevFallback then
                        logger.Warn(
                            message
                            + " — production deployments MUST set TOOLUP_INITIAL_PLATFORM_ADMIN instead."
                        )
                    else
                        logger.Info message
                | Error msg ->
                    logger.Error(
                        sprintf "[PlatformAdmin] Bootstrap failed to assign initial admin %s: %s" trimmed msg,
                        None
                    )
    }