module ToolUp.Platform.WebhookApiHandler

open System
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.WebhookDispatcher

// ─── Audit-event payloads ────────────────────────────────────────
//
// Persisted audit-event payloads use `FableConverters` because
// the admin UI deserialises them via Fable.Remoting/SimpleJson.
// Same converter the dispatcher uses for its own audit writes —
// keep both sides consistent so admin tooling can render any
// payload uniformly.

let private auditJsonOptions = FableConverters.create ()

let private toAuditJson (value: 'T) =
    JsonSerializer.Serialize(value, auditJsonOptions)

/// Generate a fresh high-entropy signing secret for server-side
/// rotation. 32 cryptographically-random bytes, base64-encoded (~44
/// chars) — the same shape the admin UI's client-side "Generate" button
/// produces at create time (32 random bytes → base64), so receivers see
/// a consistent secret format across create and rotate.
let private generateSecret () : string =
    System.Security.Cryptography.RandomNumberGenerator.GetBytes 32
    |> Convert.ToBase64String

/// Build the `IWebhookApi` Fable.Remoting handler. Resolves
/// `IWebhookRegistry`, `IWebhookDeliveryLog`, `IWebhookDispatcher`,
/// `IEventStore`, and `AccessContext` lazily from DI per request —
/// mirrors `ConfigHandler.configApi` and `FeatureFlagHandler.featureFlagApi`.
///
/// Scope isolation and write gating:
/// - Reads and writes target `AccessContext.configScope`. Anonymous mode
///   has no persistent scope and short-circuits with an error — webhooks
///   require a stable scope that can outlive a session.
/// - Writes additionally require `TeamRoles.canWriteTeamConfig` in
///   Team / MultiTeam modes (Owner/Admin only). Individual and
///   AuthenticatedEphemeral users own their user-scope subscriptions
///   outright.
/// - Cross-scope reads are structurally impossible — every registry
///   call takes the resolved `scope.ScopeId`; the registry never
///   widens the lookup. A Team A admin cannot enumerate Team B
///   subscriptions, even by guessing a Guid.
///
/// Secret masking: `List` / `Get` mask the secret before crossing the
/// wire. Only `CreateSubscription` returns the unmasked value (so the
/// admin can copy it into the receiving service); every other path
/// uses `WebhookSubscription.maskSecret`.
let webhookApi (ctx: HttpContext) : IWebhookApi =

    let registry =
        ctx.RequestServices.GetService(typeof<IWebhookRegistry>) :?> IWebhookRegistry

    let deliveryLog =
        ctx.RequestServices.GetService(typeof<IWebhookDeliveryLog>) :?> IWebhookDeliveryLog

    let dispatcher =
        ctx.RequestServices.GetService(typeof<IWebhookDispatcher>) :?> IWebhookDispatcher

    let eventStore = ctx.RequestServices.GetService(typeof<IEventStore>) :?> IEventStore

    // Phase 6d.A — the signing secret lives in ISecretStore (encrypted at
    // rest), not the subscription blob. Resolved per request; create /
    // rotate write to it, delete removes from it.
    let secretStore =
        ctx.RequestServices.GetService(typeof<Secrets.ISecretStore>) :?> Secrets.ISecretStore

    // Gap audit pass-2 #1 — webhook URL SSRF defence. Read the
    // operator-supplied allowlist from DI-registered ServerConfig
    // (compose registers the live config so the validator picks up
    // the same WebhookUrlAllowedHosts the operator declared).
    let webhookUrlPolicy: WebhookUrlValidator.WebhookUrlPolicy =
        match ctx.RequestServices.GetService(typeof<ServerConfig>) with
        | :? ServerConfig as cfg -> {
            AllowedHosts = cfg.WebhookUrlAllowedHosts
          }
        | _ -> WebhookUrlValidator.WebhookUrlPolicy.empty

    let logger: ILogger =
        match ctx.RequestServices.GetService(typeof<ILogger>) with
        | :? ILogger as l -> l
        | _ ->
            { new ILogger with
                member _.Debug _ = ()
                member _.Info _ = ()
                member _.Warn _ = ()
                member _.Error(_, _) = ()
            }

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback for tests that bypass ScopeResolutionMiddleware
            // — same pattern as ConfigHandler / FeatureFlagHandler.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            let teamId =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
                | _ -> None

            AccessContext.unrestricted (AnonymousSession userId)

    let scopeOpt = AccessContext.configScope accessContext

    /// Phase 6d.A — resolve a subscription's CURRENT signing-secret value
    /// for the rotate path: from `ISecretStore` when a ref is set, else
    /// the legacy inline value on a not-yet-migrated blob. `None` only for
    /// a structurally-broken subscription (no ref, no inline value) — the
    /// rotation then simply has no prior secret to carry into the grace
    /// window.
    let resolveCurrentSecret (sub: WebhookSubscription) : Async<string option> = async {
        if not (String.IsNullOrEmpty sub.SecretRef) then
            let! resolved = secretStore.GetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf sub.SecretRef)

            match resolved with
            | Some _ -> return resolved
            | None -> return sub.Secret
        else
            return sub.Secret
    }

    /// Team-mode write gate. Mirrors `ConfigHandler.ensureWriteAllowed`:
    /// Owner/Admin may write team subscriptions; Member is read-only;
    /// other modes are ungated (user-scope users own their scope).
    let ensureWriteAllowed () : Async<Result<unit, string>> = async {
        match accessContext.Subject with
        | TeamMember(userId, teamId) ->
            match ctx.RequestServices.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as ts ->
                let! role = ts.GetMemberRole(teamId, userId)

                match role with
                | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                | Some r ->
                    return
                        Error $"Only team owners and admins can manage webhooks. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    let withScope (f: StorageScope -> Async<Result<_, string>>) = async {
        match scopeOpt with
        | None ->
            return Error "Webhooks are not available in this mode. Sign in or join a team to manage subscriptions."
        | Some scope -> return! f scope
    }

    let withWriteScope (f: StorageScope -> Async<Result<_, string>>) = async {
        let! rbac = ensureWriteAllowed ()

        match rbac with
        | Error msg -> return Error msg
        | Ok() -> return! withScope f
    }

    /// Audit-event emit. Routes through the DI-registered `IEventStore`
    /// — the SDK wires `HookedEventStore` here so the same write also
    /// triggers `dispatcher.Dispatch`, letting admins subscribe webhooks
    /// to `WebhookSubscriptionCreated` / `StatusChanged` / `Deleted`.
    /// Failures are logged but do not fail the request — audit gaps are
    /// preferable to user-visible errors on a successful CRUD path.
    let emitAudit (scopeId: string) (eventType: string) (payload: 'T) = async {
        try
            let evt = Events.create scopeId "_platform.webhooks" eventType (toAuditJson payload)
            do! eventStore.Write evt
        with ex ->
            logger.Error($"[WebhookApi] audit write failed eventType={eventType}", Some ex)
    }

    {
        CreateSubscription =
            fun req ->
                withWriteScope (fun scope -> async {
                    // Gap audit pass-2 #1 — SSRF guard. Validate the
                    // tenant-supplied URL against the loopback /
                    // link-local / RFC1918 / unique-local IPv6 deny
                    // list before the URL hits the registry. Refusal
                    // returns the structured Error string straight to
                    // the registrar — no audit emit, no registry write.
                    match WebhookUrlValidator.validate webhookUrlPolicy req.TargetUrl with
                    | Error reason ->
                        logger.Warn $"Webhook: target rejected scope={scope.ScopeId} target={req.TargetUrl}: {reason}"
                        return Error reason
                    | Ok() ->

                        let subscriptionId = Guid.NewGuid()
                        let secretRef = WebhookSecretRef.current subscriptionId

                        // Phase 6d.A — persist the signing secret in
                        // ISecretStore (encrypted at rest) FIRST, then store
                        // only the reference on the blob. A store-write
                        // failure aborts before any blob is written, so we
                        // never persist a subscription whose secret is
                        // unrecoverable.
                        match!
                            secretStore.SetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf secretRef, req.Secret)
                        with
                        | Error e ->
                            logger.Warn
                                $"Webhook: secret persist failed scope={scope.ScopeId} target={req.TargetUrl}: {e}"

                            return Error(sprintf "Failed to persist webhook signing secret: %s" e)
                        | Ok() ->

                            let sub: WebhookSubscription = {
                                SubscriptionId = subscriptionId
                                ScopeId = scope.ScopeId
                                TargetUrl = req.TargetUrl
                                SecretRef = secretRef
                                Secret = None
                                EventTypes = req.EventTypes
                                Status = WebhookStatus.Active
                                CreatedBy = accessContext.UserId
                                CreatedAt = DateTime.UtcNow
                                ConsecutiveFailures = 0
                                PreviousSecretRef = None
                                PreviousSecret = None
                                PreviousSecretExpiresAt = None
                            }

                            match! registry.CreateSubscription sub with
                            | Error e ->
                                logger.Warn $"Webhook: create failed scope={scope.ScopeId} target={req.TargetUrl}: {e}"

                                // Best-effort cleanup of the orphaned secret so
                                // a failed create doesn't leak a dangling entry.
                                try
                                    do!
                                        secretStore.DeleteSecret(
                                            WebhookSecretRef.Scope,
                                            WebhookSecretRef.keyOf secretRef
                                        )
                                        |> Async.Ignore
                                with cleanupEx ->
                                    logger.Warn
                                        $"Webhook: orphaned-secret cleanup failed sub={subscriptionId:N}: {cleanupEx.Message}"

                                return Error e
                            | Ok() ->
                                do!
                                    emitAudit scope.ScopeId WebhookEventTypes.SubscriptionCreated {|
                                        SubscriptionId = sub.SubscriptionId
                                        TargetUrl = req.TargetUrl
                                        EventTypes = req.EventTypes
                                        CreatedBy = sub.CreatedBy
                                    |}

                                logger.Info
                                    $"Webhook: created sub={sub.SubscriptionId:N} scope={scope.ScopeId} target={req.TargetUrl}"

                                // Reveal the secret once (transiently — never
                                // persisted with a value) so the admin UI can
                                // copy it into the receiving service.
                                return Ok { sub with Secret = Some req.Secret }
                })

        ListSubscriptions =
            fun () ->
                withScope (fun scope -> async {
                    let! subs = registry.ListSubscriptions scope.ScopeId
                    return Ok(subs |> List.map WebhookSubscription.maskSecret)
                })

        GetSubscription =
            fun id ->
                withScope (fun scope -> async {
                    match! registry.GetSubscription(scope.ScopeId, id) with
                    | None -> return Error "Subscription not found."
                    | Some sub -> return Ok(WebhookSubscription.maskSecret sub)
                })

        UpdateStatus =
            fun (id, status) ->
                withWriteScope (fun scope -> async {
                    match! registry.GetSubscription(scope.ScopeId, id) with
                    | None -> return Error "Subscription not found."
                    | Some prior ->
                        match! registry.UpdateStatus(scope.ScopeId, id, status) with
                        | Error e ->
                            logger.Warn $"Webhook: status change failed sub={id:N}: {e}"

                            return Error e
                        | Ok() ->
                            do!
                                emitAudit scope.ScopeId WebhookEventTypes.SubscriptionStatusChanged {|
                                    SubscriptionId = id
                                    Previous = prior.Status
                                    Current = status
                                    ChangedBy = accessContext.UserId
                                |}

                            logger.Info $"Webhook: status sub={id:N} {prior.Status} → {status}"

                            return Ok()
                })

        RotateSecret =
            fun id ->
                withWriteScope (fun scope -> async {
                    match! registry.GetSubscription(scope.ScopeId, id) with
                    | None -> return Error "Subscription not found."
                    | Some existing ->
                        // Generate the new secret server-side (the admin
                        // never supplies it on rotation — they copy out
                        // the returned value once). Grace window keeps the
                        // prior secret valid so deliveries are not missed
                        // while the receiver updates.
                        let newSecret = generateSecret ()
                        let currentRef = WebhookSecretRef.current id
                        let previousRef = WebhookSecretRef.previous id

                        let graceExpiresAt = DateTime.UtcNow + WebhookSubscription.secretRotationGracePeriod

                        // Phase 6d.A — move the secret VALUES in ISecretStore
                        // before recording the ref bookkeeping on the blob:
                        //   1. copy the current secret to the grace-window
                        //      `.secret.previous` key (skipped only for a
                        //      structurally-broken sub with no prior secret);
                        //   2. write the new secret to the canonical current
                        //      key. On a legacy blob this also lands the
                        //      first-ever ref value.
                        let! currentValueOpt = resolveCurrentSecret existing

                        let! graceWrite = async {
                            match currentValueOpt with
                            | Some currentValue ->
                                return!
                                    secretStore.SetSecret(
                                        WebhookSecretRef.Scope,
                                        WebhookSecretRef.keyOf previousRef,
                                        currentValue
                                    )
                            | None -> return Ok()
                        }

                        match graceWrite with
                        | Error e ->
                            logger.Warn $"Webhook: grace-secret persist failed sub={id:N}: {e}"
                            return Error(sprintf "Failed to persist grace-window secret: %s" e)
                        | Ok() ->

                            match!
                                secretStore.SetSecret(
                                    WebhookSecretRef.Scope,
                                    WebhookSecretRef.keyOf currentRef,
                                    newSecret
                                )
                            with
                            | Error e ->
                                logger.Warn $"Webhook: new-secret persist failed sub={id:N}: {e}"
                                return Error(sprintf "Failed to persist rotated secret: %s" e)
                            | Ok() ->

                                match!
                                    registry.RotateSecret(scope.ScopeId, id, currentRef, previousRef, graceExpiresAt)
                                with
                                | Error e ->
                                    logger.Warn $"Webhook: secret rotation failed sub={id:N}: {e}"

                                    return Error e
                                | Ok rotated ->
                                    // Audit the rotation — actor + subscription id
                                    // + grace-window expiry, but NEVER the secret
                                    // value (old or new).
                                    do!
                                        emitAudit scope.ScopeId WebhookEventTypes.SubscriptionSecretRotated {|
                                            SubscriptionId = id
                                            RotatedBy = accessContext.UserId
                                            PreviousSecretExpiresAt = graceExpiresAt
                                        |}

                                    logger.Info
                                        $"Webhook: secret rotated sub={id:N} scope={scope.ScopeId} grace-until={graceExpiresAt:o}"

                                    // Reveal the new secret once (transiently — never
                                    // persisted with a value) for copy-out.
                                    return Ok { rotated with Secret = Some newSecret }
                })

        DeleteSubscription =
            fun id ->
                withWriteScope (fun scope -> async {
                    let! prior = registry.GetSubscription(scope.ScopeId, id)

                    match! registry.DeleteSubscription(scope.ScopeId, id) with
                    | Error e ->
                        logger.Warn $"Webhook: delete failed sub={id:N}: {e}"

                        return Error e
                    | Ok() ->
                        // Phase 6d.A — remove the signing secret(s) from
                        // ISecretStore (current + any grace-window
                        // previous). Best-effort: a failure is logged but
                        // does not fail the delete — the subscription blob
                        // is already gone and `ISecretStore.DeleteSecret`
                        // is idempotent, so a re-run or the migration's
                        // orphan handling stays safe.
                        try
                            do!
                                secretStore.DeleteSecret(
                                    WebhookSecretRef.Scope,
                                    WebhookSecretRef.keyOf (WebhookSecretRef.current id)
                                )
                                |> Async.Ignore

                            do!
                                secretStore.DeleteSecret(
                                    WebhookSecretRef.Scope,
                                    WebhookSecretRef.keyOf (WebhookSecretRef.previous id)
                                )
                                |> Async.Ignore
                        with ex ->
                            logger.Warn $"Webhook: secret cleanup after delete failed sub={id:N}: {ex.Message}"

                        // Cascade-delete the delivery log for this
                        // subscription. `Prune` filters by
                        // `olderThan`; `DateTime.MaxValue` matches
                        // every row. Pruning is best-effort — log
                        // failures but don't fail the request.
                        try
                            do! deliveryLog.Prune(Some scope.ScopeId, DateTime.MaxValue)
                        with ex ->
                            logger.Warn $"Webhook: delivery-log prune after delete failed sub={id:N}: {ex.Message}"

                        do!
                            emitAudit scope.ScopeId WebhookEventTypes.SubscriptionDeleted {|
                                SubscriptionId = id
                                TargetUrl = prior |> Option.map _.TargetUrl |> Option.defaultValue ""
                                DeletedBy = accessContext.UserId
                            |}

                        logger.Info $"Webhook: deleted sub={id:N} scope={scope.ScopeId}"

                        return Ok()
                })

        TestFire =
            fun id ->
                // Test-fire is a write-shaped action: it sends an
                // outbound HTTP request from the deployment's IP.
                // Gate it on the same Owner/Admin role as create/delete.
                withWriteScope (fun scope -> async { return! dispatcher.TestFire(scope.ScopeId, id) })

        ListDeliveries =
            fun id ->
                withScope (fun scope -> async {
                    match! registry.GetSubscription(scope.ScopeId, id) with
                    | None -> return Error "Subscription not found."
                    | Some _ ->
                        let! rows = deliveryLog.ListRecent(scope.ScopeId, id, 100)
                        return Ok rows
                })
    }