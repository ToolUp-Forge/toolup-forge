module ToolUp.Platform.PlatformAdminStore

open System
open System.Text
open System.Text.Json
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
// **Concurrency (Phase 9i).** Reads are lock-free — a single blob fetch
// + parse. Writes serialise through a single lease on `IDistributedLock`
// because the assign / revoke operations are read-modify-write: load
// list, mutate, save. Without the lock, two concurrent assignments would
// each load the same baseline, append their target, and the second write
// would clobber the first — and a concurrent last-admin revoke can brick
// the platform, which is why `MultiInstanceAdminCoherenceValidator`
// refuses `ReplicaCount > 1` for this store.
//
// One global lock id (`toolup:platform-admin:write`), not one per admin:
// admin operations are rare and the blob is a single list, so global
// write serialisation is right — per-key locking would be
// over-engineering and would not prevent the lost update anyway (both
// writers rewrite the whole document).
//
// This replaced a process-wide `SemaphoreSlim(1, 1)`. Under the
// in-process default the serialisation is identical; a deployment that
// composes a store-backed lock and passes it here gets the same
// serialisation across instances, which is what lifts the
// single-instance restriction. A CAS-based blob update
// (`IBlobStorage.UploadIfMatch`) is the complementary defence — the lock
// serialises intent, the ETag refuses a lost update even if the lease
// lapsed — and remains the Phase 9c follow-on.
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

/// Phase 9i — the single lock id every admin write serialises on.
/// Namespaced so it cannot collide with another subsystem's ids in a
/// shared store.
let private writeLockId = "toolup:platform-admin:write"

/// Lease TTL for one admin write. The critical section is a blob
/// download, a list mutation, a blob upload and an audit emission — small
/// and bounded, so a minute is generous while still reclaiming promptly
/// if a holder dies mid-write.
let private writeLeaseTtl = TimeSpan.FromMinutes 1.0

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
type BlobBackedPlatformAdminStore(storage: IBlobStorage, auditLog: IAuditLog, ?distributedLock: IDistributedLock) =
    /// Phase 9i — defaults to the process-wide in-process lock (the same
    /// instance `compose` registers), so every admin writer in the process
    /// serialises on one table exactly as the previous `SemaphoreSlim`
    /// did.
    let writeLock = defaultArg distributedLock InProcessDistributedLock.shared

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

    let withWriteLock (op: unit -> Async<'T>) =
        // `withLease` waits for the lease, releases on every exit path
        // including exceptions, and re-raises the original exception with
        // its stack intact — the `try/finally` this replaced, expressed
        // once over the seam instead of per call site.
        DistributedLock.withLease writeLock writeLockId writeLeaseTtl (fun _lease -> op ())

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
                // Recognise only the canonical truthy spellings (1/true/yes/on),
                // matching the workspace-wide boolean-env convention
                // (`envFlag` in SDK.Shared). The previous "anything but empty/0"
                // test treated the disable spellings (`false`/`no`/`off`) as
                // ENABLED — an operator setting `TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP=false`
                // to turn the dev-admin bootstrap OFF got the opposite, keeping
                // a first-sign-in privilege-escalation path open.
                match Environment.GetEnvironmentVariable allowDevAdminBootstrapEnvVar with
                | null -> false
                | v ->
                    match v.Trim().ToLowerInvariant() with
                    | "1"
                    | "true"
                    | "yes"
                    | "on" -> true
                    | _ -> false

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