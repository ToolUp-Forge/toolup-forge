module ToolUp.Platform.Jobs.PlatformAdminStore

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Config

// ─── Phase 4b — blob-backed Platform Admin store ──────────────────────
//
// `BlobBackedPlatformAdminStore` is the SDK-default `IPlatformAdminStore`
// implementation. Persists a flat JSON array of userIds at
// `_platform/platform-admins.json` and emits `PlatformAdminAssigned` /
// `PlatformAdminRevoked` audit events under `_platform` scope on every
// successful mutation.
//
// **Concurrency.** The list is one document in the platform's durable
// document store (`BlobDocumentStore`): a read that fails is a refusal, never
// an empty list, and assign / revoke are one guarded read-modify-write — a
// conditional write on a backend with the ETag seam, so a lost update is
// refused and replayed. Writes also serialise on one lease on
// `IDistributedLock` (`toolup:platform-admin:write`): on a backend WITHOUT
// conditional writes the lease is the only cross-instance guard, and it keeps
// the audit emission in the order of the writes. A concurrent last-admin
// revoke can brick the platform, which is why
// `MultiInstanceAdminCoherenceValidator` refuses `ReplicaCount > 1` for this
// store unless the lock is store-backed.
//
// **Bootstrap.** The `bootstrap` function reads
// `TOOLUP_INITIAL_PLATFORM_ADMIN` and assigns the named user as the
// initial admin if and only if the current admin list is empty. Wired
// into `compose` (Phase 4b commit 4c). One-shot — once any admin
// exists, subsequent restarts no-op even if the env var is still set.
// Wipe + re-bootstrap is the documented disaster-recovery path.

let private platformContainer = ScopeKey.PlatformContainer
let private adminsPath = DocPath.ofString "platform-admins.json"
let private bootstrapActor = "_bootstrap"

/// The single lock id every admin write serialises on.
/// Namespaced so it cannot collide with another subsystem's ids in a
/// shared store.
let private writeLockId = "toolup:platform-admin:write"

/// Lease TTL for one admin write. The critical section is a blob
/// download, a list mutation, a blob upload and an audit emission — small
/// and bounded, so a minute is generous while still reclaiming promptly
/// if a holder dies mid-write.
let private writeLeaseTtl = TimeSpan.FromMinutes 1.0

[<Literal>]
let private initialAdminEnvVar = ConfigKeys.Names.initialPlatformAdmin

/// Phase 230 — explicit second opt-in required for the
/// `AutoBootstrapDevAdmin` fallback to elevate in an auth-requiring
/// deployment. A dev-convenience field that leaks into production
/// (especially behind a TLS-terminating proxy, where `RequireHttps = false`
/// makes the deployment indistinguishable from local dev) can otherwise
/// silently grant Platform Admin to the first sign-in. Requiring a second,
/// deliberate env var means a single leaked field can never escalate.
/// Public so the preflight validator references the same name.
[<Literal>]
let allowDevAdminBootstrapEnvVar = ConfigKeys.Names.allowDevAdminBootstrap

/// The admin list is a JSON array of user ids. It keeps the serializer's
/// default encoder, not `PlatformJson`'s relaxed one: the two escape a `+` or
/// `<` in an id differently, and a persisted document keeps its bytes. A list
/// that does not decode is an error the store quarantines, never `[]`.
let private adminsCodec: BlobCodec<string list> =
    let options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    {
        Encode = fun admins -> JsonSerializer.SerializeToUtf8Bytes(List.toArray admins, options)
        Decode =
            fun content ->
                try
                    match JsonSerializer.Deserialize<string array>(content, options) with
                    | null -> Error "the admin list is the JSON literal null"
                    | admins -> Ok(Array.toList admins)
                with ex ->
                    Error ex.Message
    }

/// Default `IPlatformAdminStore` impl. Persists to `_platform/platform-
/// admins.json` via the injected `IBlobStorage`; emits audit events via
/// the injected `IAuditLog` after every successful mutation.
type BlobBackedPlatformAdminStore(storage: IBlobStorage, auditLog: IAuditLog, ?distributedLock: IDistributedLock) =
    /// Defaults to the process-wide in-process lock (the same instance
    /// `compose` registers), so every admin writer in the process serialises
    /// on one lease.
    let writeLock = defaultArg distributedLock InProcessDistributedLock.shared

    let docs =
        BlobDocumentStore<string list>(storage, adminsCodec, Config.ConsoleLogger.ConsoleLogger() :> ILogger)

    /// The admin list; an absent document is no admins. One that cannot be
    /// read or decoded raises `DocumentUnavailableException`: an unreadable
    /// list is never "no admins".
    let load () = async {
        let! read = docs.Read(ScopeKey.platform, adminsPath)
        return read |> DocumentUnavailable.orRaise |> Option.defaultValue []
    }

    /// One guarded read-modify-write of the list under the write lease, then
    /// the audit event when the transform wrote.
    let change (transform: string list -> BlobUpdate<string list, Result<bool, string>>) (audit: AuditEvent) =
        // `withLease` waits for the lease, releases it on every exit path
        // and re-raises an exception with its stack intact.
        DistributedLock.withLease writeLock writeLockId writeLeaseTtl (fun _lease -> async {
            let! outcome =
                docs.Update(ScopeKey.platform, adminsPath, fun current -> transform (Option.defaultValue [] current))

            match outcome with
            | Error e -> return Error(BlobMapStoreError.describe e)
            | Ok(Error refusal) -> return Error refusal
            | Ok(Ok false) -> return Ok()
            | Ok(Ok true) ->
                // The row is propagated: a refused row answers the change's
                // own error channel.
                let! recorded =
                    AuditRow.emit auditLog AuditScope.platform (AuditSubject.system "PlatformAdminStore") audit

                return recorded |> Result.mapError AuditWriteError.describe
        })

    interface IPlatformAdminStore with
        /// Raises `DocumentUnavailableException` on an unreadable list: the
        /// question is refused, not answered "no".
        member _.IsPlatformAdmin userId = async {
            let! admins = load ()
            return List.contains userId.Value admins
        }

        member _.AssignPlatformAdmin(actorUserId, targetUserId) =
            change
                (fun existing ->
                    if List.contains targetUserId.Value existing then
                        BlobUpdate.Keep(Ok false) // idempotent — no audit re-emission
                    else
                        BlobUpdate.Write(targetUserId.Value :: existing, Ok true))
                (PlatformAdminAssigned {
                    Actor = actorUserId.Value
                    TargetUserId = targetUserId
                })

        member _.RevokePlatformAdmin(actorUserId, targetUserId) =
            change
                (fun existing ->
                    if not (List.contains targetUserId.Value existing) then
                        BlobUpdate.Keep(Ok false) // idempotent — no audit re-emission
                    elif existing.Length <= 1 then
                        BlobUpdate.Keep(Error "cannot revoke the last remaining Platform Admin")
                    else
                        BlobUpdate.Write(existing |> List.filter (fun id -> id <> targetUserId.Value), Ok true))
                (PlatformAdminRevoked {
                    Actor = actorUserId.Value
                    TargetUserId = targetUserId
                })

        /// Raises `DocumentUnavailableException` on an unreadable list.
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
        // Phase 698 — through the Phase-696 `ConfigResolution` seam. `tryResolve`
        // rather than `tryValue` because this bootstrap RECORDS where the
        // elevated identity came from, and "env var TOOLUP_INITIAL_PLATFORM_ADMIN"
        // would be a false statement about a manifest-declared admin — on the one
        // audit line that says who was granted platform-wide privilege.
        let resolvedAdmin = ConfigResolution.tryResolve initialAdminEnvVar

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
                match ConfigResolution.tryValue allowDevAdminBootstrapEnvVar with
                | None -> false
                | Some v ->
                    match v.Trim().ToLowerInvariant() with
                    | "1"
                    | "true"
                    | "yes"
                    | "on" -> true
                    | _ -> false

        let target =
            match resolvedAdmin with
            | Some(value, source) when not (String.IsNullOrWhiteSpace value) ->
                Some(
                    value.Trim(),
                    sprintf "%s %s" (ConfigResolution.ConfigSource.label source) initialAdminEnvVar,
                    false
                )
            | _ ->
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
            // An unreadable list raises `DocumentUnavailableException` here
            // rather than reading as empty: bootstrapping over it would
            // replace every admin with the target, so the fault refuses the
            // boot (the composition root turns a raised bootstrap into a
            // preflight refusal).
            let! existing = store.ListPlatformAdmins()

            match existing with
            | existing when not (List.isEmpty existing) ->
                // Already-populated — bootstrap is one-shot. No-op.
                ()
            | _ ->
                let! result = store.AssignPlatformAdmin(UserId bootstrapActor, UserId trimmed)

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