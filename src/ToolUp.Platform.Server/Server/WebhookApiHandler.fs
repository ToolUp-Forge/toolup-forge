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

                        let sub: WebhookSubscription = {
                            SubscriptionId = Guid.NewGuid()
                            ScopeId = scope.ScopeId
                            TargetUrl = req.TargetUrl
                            Secret = req.Secret
                            EventTypes = req.EventTypes
                            Status = WebhookStatus.Active
                            CreatedBy = accessContext.UserId
                            CreatedAt = DateTime.UtcNow
                            ConsecutiveFailures = 0
                        }

                        match! registry.CreateSubscription sub with
                        | Error e ->
                            logger.Warn $"Webhook: create failed scope={scope.ScopeId} target={req.TargetUrl}: {e}"

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

                            // Return the unmasked record so the admin UI
                            // can show the secret once for copy-out.
                            return Ok sub
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

        DeleteSubscription =
            fun id ->
                withWriteScope (fun scope -> async {
                    let! prior = registry.GetSubscription(scope.ScopeId, id)

                    match! registry.DeleteSubscription(scope.ScopeId, id) with
                    | Error e ->
                        logger.Warn $"Webhook: delete failed sub={id:N}: {e}"

                        return Error e
                    | Ok() ->
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