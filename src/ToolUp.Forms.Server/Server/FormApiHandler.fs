module ToolUp.Forms.FormApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.EntityQuery
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow
open ToolUp.Forms.FormApi
open ToolUp.Forms.FormValidator
open ToolUp.Forms.IFormStore
open ToolUp.Forms.IWorkflowEngine
open ToolUp.Forms.PublicFormApi
open ToolUp.Forms.AggregationTypes
open ToolUp.Forms.AnalyserCache
open ToolUp.Forms.IFormSubmissionAnalyser

// ─── Phase 21 — Fable.Remoting handler ──────────────────────────
//
// Builds a per-request `IFormApi` from the resolved `AccessContext`
// (scopeId / userId / role) and the DI-resolved `IFormStore` /
// `IWorkflowEngine` / `CustomValidatorRegistry` / `ITeamStore` /
// `IAuditLog`.
//
// **Permission gating** (mirrors ConfigHandler / HealthMonitorApiHandler):
//   - SaveSchema / DeleteSchema: Owner/Admin in Team mode; ungated
//     in user-scoped modes (Individual / AuthenticatedEphemeral)
//   - GetSchema / ListSchemas: any authenticated caller
//   - Submit / UpdateDraft / GetSubmission / ApplyTransition: any
//     authenticated caller
//   - ListSubmissions: Members see only their own submissions
//     (forced And-of-Eq("Author", "u:" + userId) into the query);
//     Owner/Admin see everything in scope
//
// `ScopeId` / `Author` / `SubmittedAt` are server-set from
// `AccessContext` — never trust client-supplied identity.

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.Items.TryGetValue "ToolUp.AccessContext" with
    | true, (:? AccessContext as ac) -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        let teamId =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
            | _ -> None

        AccessContext.unrestricted (AnonymousSession userId)

let private resolveScopeId (ctx: HttpContext) (accessContext: AccessContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as scope) -> scope.ScopeId
    | _ -> accessContext.UserId

let private isWriteAllowed (ctx: HttpContext) (accessContext: AccessContext) : Async<bool> = async {
    match accessContext.Subject with
    | TeamMember(userId, teamId) ->
        match ctx.RequestServices.GetService(typeof<ITeamStore>) with
        | :? ITeamStore as ts ->
            let! role = ts.GetMemberRole(teamId, userId)

            return
                match role with
                | Some r -> TeamRoles.canWriteTeamConfig r
                | None -> false
        | _ -> return false
    | _ -> return true
}

let formApi
    (formStore: IFormStore)
    (workflowEngine: IWorkflowEngine)
    (validatorRegistry: CustomValidatorRegistry)
    (auditLog: IAuditLog)
    (workflows: Map<WorkflowId, WorkflowDefinition>)
    /// Phase 21b — `Some` when `ServerConfig.ShareTokenStore =
    /// EnabledShareTokenStore`. `None` causes `IssueTokens` to fail
    /// with a clear error rather than crashing on a null DI lookup.
    (shareTokenStore: IShareTokenStore option)
    /// Phase 21b — `Some` when `ServerConfig.PublicBaseUrl` is set.
    /// `None` causes `IssueTokens` to fail with a clear error rather
    /// than building broken `https:///r/{token}` URLs.
    (publicBaseUrl: string option)
    /// Phase 21b (slice 6) — `Some` when the deployment registered at
    /// least one transactional notification sink. `None` causes
    /// `DispatchInvitationsByEmail` to fail with a clear error
    /// rather than queueing emails that nothing will deliver.
    (notificationChannel: INotificationChannel option)
    /// Phase 21b (slice 6) — passed through for synthesised-respondent
    /// address-book writes. Always `Some` for any deployment that
    /// uses `FormsServerApp` (the base SDK requires `IBlobStorage`),
    /// but typed `option` here lets the handler refuse cleanly if
    /// somehow absent rather than null-ref'ing.
    (blobStorage: IBlobStorage option)
    /// Phase 21c follow-up — analyser-output cache. `NoOpAnalyserCache`
    /// when no deployment opt-in via `withAnalyserCache` AND no
    /// `IResultStore` is wired (preserves pre-cache behaviour
    /// byte-for-byte); `ResultStoreAnalyserCache` auto-constructed
    /// when an `IResultStore` is in DI but the consumer didn't call
    /// `withAnalyserCache`; the consumer's instance when they did.
    (analyserCache: AnalyserCache.IAnalyserCache)
    /// Phase 21c follow-up — registered submission analysers. Empty
    /// list when no consumer registered any (default); each registered
    /// analyser is invoked per-field via `Analyse` and the results are
    /// folded into `AggregationSummary.AnalyserOutputs`. Resolved at
    /// request time from `IServiceProvider.GetServices<IFormSubmissionAnalyser>()`
    /// so deployments that register multiple analysers (one per concern
    /// — sentiment + topic, etc.) compose cleanly.
    (analysers: IFormSubmissionAnalyser list)
    (ctx: HttpContext)
    : IFormApi =
    let accessContext = resolveAccessContext ctx
    let scopeId = resolveScopeId ctx accessContext
    let userId = accessContext.UserId

    let withWriteGate (op: unit -> Async<Result<'T, FormError>>) : Async<Result<'T, FormError>> = async {
        let! ok = isWriteAllowed ctx accessContext

        if ok then
            return! op ()
        else
            return Error FormError.Unauthorised
    }

    let resolveInitialState (workflowId: WorkflowId option) : SubmissionState =
        match workflowId with
        | None -> Submitted
        | Some wfId ->
            match Map.tryFind wfId workflows with
            | Some wf -> SubmissionState.fromIndexValue wf.InitialState
            | None -> Submitted

    let allocateSubmissionId () : SubmissionId = Guid.NewGuid().ToString()

    let scopeListSubmissions (q: EntityQuery<Submission>) : Async<EntityQuery<Submission>> = async {
        let! ok = isWriteAllowed ctx accessContext

        if ok then
            return q
        else
            return
                q
                |> andWhere (Eq(Submission.indexAuthor, SubmissionAuthor.indexValueForUser userId))
    }

    {
        // ─── Schema management ─────────────────────────────────────
        SaveSchema = fun schema -> withWriteGate (fun () -> formStore.SaveSchema(scopeId, schema))

        GetSchema =
            fun (id, version) -> async {
                let! r = formStore.GetSchema(scopeId, id, version)

                return
                    match r with
                    | Ok s -> Some s
                    | Error _ -> None
            }

        ListSchemas = fun () -> formStore.ListSchemas scopeId

        DeleteSchema = fun id -> withWriteGate (fun () -> formStore.DeleteSchema(scopeId, id))

        // ─── Submission lifecycle ──────────────────────────────────
        Submit =
            fun req -> async {
                // 1. Resolve schema (latest version).
                let! schemaResult = formStore.GetSchema(scopeId, req.FormId, None)

                match schemaResult with
                | Error err -> return Error err
                | Ok schema ->
                    // 2. Validate values against schema.
                    match FormValidator.validate validatorRegistry schema req.Values with
                    | Error errors -> return Error(FormError.ValidationFailed errors)
                    | Ok() ->
                        // 3. Build submission with server-set identity.
                        let initialState = resolveInitialState req.WorkflowId

                        let submission: Submission = {
                            Id = allocateSubmissionId ()
                            Type = Submission.entityType
                            Version = 1
                            FormId = req.FormId
                            SchemaVersion = schema.Version
                            SubmittedAt = DateTimeOffset.UtcNow
                            Author = AuthenticatedUser userId
                            Values = req.Values
                            State = initialState
                            WorkflowId = req.WorkflowId
                        }

                        let! saveResult = formStore.SaveSubmission(scopeId, submission)

                        match saveResult with
                        | Error err -> return Error err
                        | Ok saved ->
                            // Phase 21c follow-up A — a fresh submission
                            // makes the cached analyser outputs stale.
                            // Mark the entry stale at the submitted
                            // schema version so the next
                            // `GetAggregations` miss path overwrites it.
                            // Best-effort: a cache failure shouldn't
                            // undo a saved submission, so we ignore the
                            // count.
                            let! _ = analyserCache.MarkStale(saved.FormId, Some saved.SchemaVersion)

                            let payload: FormSubmittedPayload = {
                                UserId = userId
                                FormId = saved.FormId
                                SubmissionId = saved.Id
                                SchemaVersion = saved.SchemaVersion
                                FieldCount = saved.Values.Count
                                WorkflowId = saved.WorkflowId
                                InitialState = SubmissionState.toIndexValue saved.State
                            }

                            do! auditLog.Record(scopeId, AuditEvent.FormSubmitted payload)
                            return Ok saved
            }

        UpdateDraft =
            fun req -> async {
                let! existingResult = formStore.GetSubmission(scopeId, req.SubmissionId)

                match existingResult with
                | Error err -> return Error err
                | Ok existing ->
                    if existing.Author <> AuthenticatedUser userId then
                        return Error FormError.Unauthorised
                    elif existing.State <> SubmissionState.Draft then
                        return
                            Error(
                                FormError.InvalidTransition(SubmissionState.toIndexValue existing.State, "update-draft")
                            )
                    else
                        // Resolve schema-at-submit for validation.
                        let! schemaResult = formStore.GetSchema(scopeId, existing.FormId, Some existing.SchemaVersion)

                        match schemaResult with
                        | Error err -> return Error err
                        | Ok schema ->
                            match FormValidator.validate validatorRegistry schema req.Values with
                            | Error errors -> return Error(FormError.ValidationFailed errors)
                            | Ok() ->
                                let updated = { existing with Values = req.Values }
                                let! saveResult = formStore.SaveSubmission(scopeId, updated)

                                match saveResult with
                                | Error err -> return Error err
                                | Ok saved ->
                                    let payload: FormSubmissionUpdatedPayload = {
                                        UserId = userId
                                        FormId = saved.FormId
                                        SubmissionId = saved.Id
                                        FieldCount = saved.Values.Count
                                        Version = saved.Version
                                    }

                                    do! auditLog.Record(scopeId, AuditEvent.FormSubmissionUpdated payload)
                                    return Ok saved
            }

        GetSubmission =
            fun id -> async {
                let! r = formStore.GetSubmission(scopeId, id)

                match r with
                | Error _ -> return None
                | Ok submission ->
                    if submission.Author = AuthenticatedUser userId then
                        return Some submission
                    else
                        let! ok = isWriteAllowed ctx accessContext
                        return if ok then Some submission else None
            }

        ListSubmissions =
            fun query -> async {
                let! gatedQuery = scopeListSubmissions query
                return! formStore.ListSubmissions(scopeId, gatedQuery)
            }

        // ─── Workflow operations ───────────────────────────────────
        ApplyTransition = fun req -> workflowEngine.Apply(scopeId, req.SubmissionId, req.Event, accessContext)

        ListPossibleTransitions = fun id -> workflowEngine.ListPossibleTransitions(scopeId, id)

        // ─── Phase 21b — aggregation dashboard ─────────────────────
        GetAggregations =
            fun schemaId ->
                withWriteGate (fun () -> async {
                    match! formStore.GetSchema(scopeId, schemaId, None) with
                    | Error err -> return Error err
                    | Ok schema ->
                        // Pull every committed submission of this form. The
                        // FormId index is the load-bearing one — a deployment
                        // with thousands of forms still pages efficiently
                        // because one index read per (form, scope) is the
                        // worst case.
                        let query =
                            forType<Submission> Submission.entityType
                            |> where (Eq(Submission.indexFormId, schemaId))

                        let! submissionsResult = formStore.ListSubmissions(scopeId, query)

                        match submissionsResult with
                        | Error err -> return Error err
                        | Ok allSubmissions ->
                            // Filter out drafts so the dashboard reflects
                            // committed responses only. Workflow-state
                            // submissions count regardless of state — the
                            // creator usually wants to see "they responded"
                            // even mid-workflow.
                            let committed =
                                allSubmissions |> List.filter (fun s -> s.State <> SubmissionState.Draft)

                            // Issued-token list (only meaningful for
                            // Publishable schemas; Internal forms get an
                            // empty list and 0 InvitedCount).
                            let! issuedTokens =
                                match shareTokenStore with
                                | Some store -> store.ListByResource(scopeId, ResourceKind, schemaId)
                                | None -> async { return [] }

                            // Build the per-recipient status table by
                            // joining issued tokens with the submissions
                            // they produced.
                            let submissionsByToken =
                                committed
                                |> List.choose (fun s ->
                                    match s.Author with
                                    | InvitedRespondent(tokenId, _) -> Some(tokenId, s)
                                    | AuthenticatedUser _ -> None)
                                |> Map.ofList

                            let recipients =
                                issuedTokens
                                |> List.map (fun claim ->
                                    let respondedAt =
                                        Map.tryFind claim.TokenId submissionsByToken |> Option.map _.SubmittedAt

                                    {
                                        Handle = claim.AttributedHandle |> Option.defaultValue ""
                                        TokenId = claim.TokenId
                                        IssuedAt = claim.IssuedAt
                                        ExpiresAt = claim.ExpiresAt
                                        RespondedAt = respondedAt
                                        Revoked = claim.Revoked
                                    })

                            // Per-field aggregations.
                            let fieldAggregations =
                                schema.Fields
                                |> List.map (fun f -> f.Key, Aggregations.aggregateField f committed)
                                |> Map.ofList

                            // Phase 21c follow-up A — fold registered
                            // `IFormSubmissionAnalyser` outputs through the
                            // cache. One cache entry per `(schemaId,
                            // analyserName, schemaVersion)` triple; the
                            // value is `AnalyserOutput list` (one item per
                            // field the analyser produced `Some` for —
                            // `None` fields are absent so the list is
                            // sparser than the schema's field list and
                            // `FieldKey` recovers the association). The
                            // `readThrough` helper invokes `compute` only
                            // on cache miss; subsequent reads at the same
                            // schemaVersion hit the cache without re-
                            // invoking the analyser.
                            let! analyserOutputPairs =
                                analysers
                                |> List.map (fun analyser -> async {
                                    let cacheKey: AnalyserCacheKey = {
                                        SchemaId = schemaId
                                        AnalyserName = analyser.Name
                                        SchemaVersion = schema.Version
                                    }

                                    let compute () = async {
                                        let! perField =
                                            schema.Fields
                                            |> List.map (fun field -> async {
                                                let! result = analyser.Analyse(schema, field, committed)

                                                return
                                                    result
                                                    |> Option.map (fun out -> { out with FieldKey = field.Key })
                                            })
                                            |> Async.Sequential

                                        return perField |> Array.toList |> List.choose id
                                    }

                                    let! outputs = readThrough analyserCache cacheKey compute
                                    return outputs
                                })
                                |> Async.Sequential

                            let analyserOutputs =
                                analyserOutputPairs
                                |> Array.toList
                                |> List.collect id
                                |> List.map (fun out -> (out.FieldKey, out.AnalyserName), out)
                                |> Map.ofList

                            let invitedCount = issuedTokens.Length

                            let responseRate =
                                if invitedCount = 0 then
                                    None
                                else
                                    Some(float committed.Length / float invitedCount)

                            return
                                Ok {
                                    SchemaId = schemaId
                                    SchemaVersion = schema.Version
                                    SubmissionCount = committed.Length
                                    InvitedCount = invitedCount
                                    ResponseRate = responseRate
                                    Recipients = recipients
                                    FieldAggregations = fieldAggregations
                                    AnalyserOutputs = analyserOutputs
                                }
                })

        // ─── Phase 21b — bulk share-link issuance ──────────────────
        IssueTokens =
            fun req ->
                withWriteGate (fun () -> async {
                    match shareTokenStore, publicBaseUrl with
                    | None, _ ->
                        return
                            Error(
                                FormError.StorageFailed
                                    "Share-token substrate is not enabled. Set ServerConfig.ShareTokenStore = EnabledShareTokenStore."
                            )
                    | _, None ->
                        return
                            Error(
                                FormError.StorageFailed
                                    "ServerConfig.PublicBaseUrl is not set; cannot compose embed URLs."
                            )
                    | Some store, Some baseUrl ->
                        match! formStore.GetSchema(scopeId, req.SchemaId, None) with
                        | Error err -> return Error err
                        | Ok schema when schema.Visibility <> Publishable ->
                            return
                                Error(
                                    FormError.StorageFailed
                                        "Form schema is not Publishable; flip Visibility to Publishable before issuing tokens."
                                )
                        | Ok _ ->
                            let trimmedBaseUrl = baseUrl.TrimEnd('/')

                            // Issue tokens sequentially. Per-recipient
                            // failures abort the batch — the caller can
                            // retry the whole list (idempotency at the
                            // wire level is not currently provided; a
                            // future revision could deduplicate via the
                            // `IShareTokenStore.ListByResource`
                            // existing-handle filter).
                            let mutable result: Result<IssuedToken list, FormError> = Ok []
                            let issued = ResizeArray<IssuedToken>()

                            let mutable i = 0

                            while i < req.Recipients.Length && result.IsOk do
                                let recipient = req.Recipients[i]

                                let issueRequest: ShareTokenIssueRequest = {
                                    ScopeId = scopeId
                                    ResourceKind = ResourceKind
                                    ResourceId = req.SchemaId
                                    AttributedHandle = Some recipient.Handle
                                    IssuedBy = userId
                                    ExpiresAt = req.ExpiresAt
                                    UseLimit = req.UseLimit
                                    RateLimit = None
                                }

                                match! store.Issue issueRequest with
                                | Error err ->
                                    result <-
                                        Error(
                                            FormError.StorageFailed(
                                                sprintf
                                                    "Token issuance failed for recipient %s: %A"
                                                    recipient.Handle
                                                    err
                                            )
                                        )
                                | Ok token ->
                                    issued.Add {
                                        Handle = recipient.Handle
                                        TokenId = token.Claim.TokenId
                                        Url = sprintf "%s/r/%s" trimmedBaseUrl token.Token
                                        ExpiresAt = token.Claim.ExpiresAt
                                    }

                                i <- i + 1

                            return
                                match result with
                                | Error e -> Error e
                                | Ok _ -> Ok(List.ofSeq issued)
                })

        // ─── Phase 21b (slice 7) — multi-survey admin ──────────────
        ListSchemasOverview =
            fun () -> async {
                let! schemas = formStore.ListSchemas scopeId

                // For each schema, count tokens (via IShareTokenStore
                // when enabled — N+1 across schemas, acceptable since
                // operator dashboards aren't hot-path) and submissions
                // (via the FormId-indexed query — also one round-trip
                // per schema). A future revision could batch both via
                // a dedicated overview query, but the per-schema cost
                // is bounded by index reads, not full scans.
                let! rows =
                    schemas
                    |> List.map (fun schema -> async {
                        let! issuedTokens =
                            match shareTokenStore with
                            | Some store -> store.ListByResource(scopeId, ResourceKind, schema.Id)
                            | None -> async { return [] }

                        let query =
                            forType<Submission> Submission.entityType
                            |> where (Eq(Submission.indexFormId, schema.Id))

                        let! submissionsResult = formStore.ListSubmissions(scopeId, query)

                        let committedCount =
                            match submissionsResult with
                            | Ok submissions ->
                                submissions
                                |> List.filter (fun s -> s.State <> SubmissionState.Draft)
                                |> List.length
                            | Error _ -> 0

                        let invitedCount = issuedTokens.Length

                        let responseRate =
                            if invitedCount = 0 then
                                None
                            else
                                Some(float committedCount / float invitedCount)

                        let status =
                            match schema.Visibility, invitedCount with
                            | Publishable, _ -> Active
                            | Internal, 0 -> Draft
                            | Internal, _ -> Closed

                        return {
                            Schema = schema
                            SubmissionCount = committedCount
                            InvitedCount = invitedCount
                            ResponseRate = responseRate
                            Status = status
                        }
                    })
                    |> Async.Sequential

                return rows |> Array.toList
            }

        CloseSurvey =
            fun req ->
                withWriteGate (fun () -> async {
                    match! formStore.GetSchema(scopeId, req.SchemaId, None) with
                    | Error err -> return Error err
                    | Ok schema ->
                        // Step 1: revoke tokens if requested. Done
                        // before the visibility flip so an audit
                        // reader sees revocations as the closing
                        // action rather than as side-effects of a
                        // schema edit.
                        let! revokedCount =
                            match req.Mode, shareTokenStore with
                            | (RevokeAllTokens | HideSchemaAndRevoke), Some store -> async {
                                let! tokens = store.ListByResource(scopeId, ResourceKind, req.SchemaId)
                                let activeTokens = tokens |> List.filter (fun c -> not c.Revoked)

                                let mutable count = 0

                                for claim in activeTokens do
                                    match! store.Revoke(scopeId, claim.TokenId, userId) with
                                    | Ok() -> count <- count + 1
                                    | Error _ -> ()

                                return count
                              }
                            | _ -> async { return 0 }

                        // Step 2: flip visibility if requested.
                        // SaveSchema bumps Version monotonically;
                        // submissions reference SchemaVersion at
                        // submit time so historical re-renders
                        // continue to work against the previous
                        // version's Visibility.
                        let! finalVisibility =
                            match req.Mode with
                            | HideSchema
                            | HideSchemaAndRevoke -> async {
                                let updated = { schema with Visibility = Internal }

                                match! formStore.SaveSchema(scopeId, updated) with
                                | Ok _ -> return Internal
                                | Error _ ->
                                    // SaveSchema failure is unusual but
                                    // recoverable — the revocation has
                                    // already happened (or wasn't
                                    // requested). Surface the original
                                    // visibility so the caller sees the
                                    // partial state and can retry.
                                    return schema.Visibility
                              }
                            | RevokeAllTokens -> async { return schema.Visibility }

                        return
                            Ok {
                                SchemaId = req.SchemaId
                                NewVisibility = finalVisibility
                                TokensRevoked = revokedCount
                            }
                })

        // ─── Phase 21b (slice 6) — email-dispatch convenience ──────
        DispatchInvitationsByEmail =
            fun req ->
                withWriteGate (fun () -> async {
                    match shareTokenStore, publicBaseUrl, notificationChannel, blobStorage with
                    | None, _, _, _ ->
                        return
                            Error(
                                FormError.StorageFailed
                                    "Share-token substrate is not enabled. Set ServerConfig.ShareTokenStore = EnabledShareTokenStore."
                            )
                    | _, None, _, _ ->
                        return
                            Error(
                                FormError.StorageFailed
                                    "ServerConfig.PublicBaseUrl is not set; cannot compose embed URLs."
                            )
                    | _, _, None, _ ->
                        return
                            Error(
                                FormError.StorageFailed
                                    "No transactional notification channel registered. Wire INotificationChannel via ServerApp.withTransactionalSink before dispatching."
                            )
                    | _, _, _, None ->
                        return
                            Error(
                                FormError.StorageFailed
                                    "IBlobStorage missing — cannot persist respondent contact records."
                            )
                    | Some store, Some baseUrl, Some channel, Some storage ->
                        match! formStore.GetSchema(scopeId, req.SchemaId, None) with
                        | Error err -> return Error err
                        | Ok schema when schema.Visibility <> Publishable ->
                            return
                                Error(
                                    FormError.StorageFailed
                                        "Form schema is not Publishable; flip Visibility before dispatching invitations."
                                )
                        | Ok _ ->
                            let trimmedBaseUrl = baseUrl.TrimEnd('/')
                            let now = DateTime.UtcNow

                            let recipients = ResizeArray<DispatchedInvitation>()
                            let mutable successCount = 0

                            for invitation in req.Invitations do
                                let issueRequest: ShareTokenIssueRequest = {
                                    ScopeId = scopeId
                                    ResourceKind = ResourceKind
                                    ResourceId = req.SchemaId
                                    AttributedHandle = Some invitation.Email
                                    IssuedBy = userId
                                    ExpiresAt = req.ExpiresAt
                                    UseLimit = req.UseLimit
                                    RateLimit = None
                                }

                                match! store.Issue issueRequest with
                                | Error err ->
                                    recipients.Add {
                                        Email = invitation.Email
                                        TokenId = None
                                        Url = None
                                        Error = Some(sprintf "issue failed: %A" err)
                                    }
                                | Ok token ->
                                    let url = sprintf "%s/r/%s" trimmedBaseUrl token.Token
                                    let respondentUserId = "respondent:" + token.Claim.TokenId

                                    // Persist a one-shot address-book entry so the
                                    // dispatcher can resolve `respondent:{tokenId}`
                                    // → email at sink time. The contact stays in
                                    // place for the token's lifetime so resends and
                                    // delivery-failure audits remain attributable.
                                    let contact: UserContact = {
                                        UserId = respondentUserId
                                        Email =
                                            Some {
                                                Address = invitation.Email
                                                DisplayName = invitation.DisplayName
                                            }
                                        Phone = None
                                        PushTokens = []
                                    }

                                    let! contactResult = NotificationAddressBook.saveContact storage scopeId contact

                                    match contactResult with
                                    | Error e ->
                                        recipients.Add {
                                            Email = invitation.Email
                                            TokenId = Some token.Claim.TokenId
                                            Url = Some url
                                            Error = Some(sprintf "address-book write failed: %s" e)
                                        }
                                    | Ok() ->
                                        let displayName =
                                            invitation.DisplayName |> Option.defaultValue invitation.Email

                                        let body =
                                            req.BodyTemplate
                                                .Replace("{{link}}", url)
                                                .Replace("{{displayName}}", displayName)

                                        let envelope: EmailEnvelope = {
                                            RecipientUserIds = [ respondentUserId ]
                                            Content = InlineEmail(req.Subject, body, None)
                                            CorrelationId = Some token.Claim.TokenId
                                        }

                                        do! channel.Publish(scopeId, TransactionalEmail envelope)
                                        successCount <- successCount + 1

                                        recipients.Add {
                                            Email = invitation.Email
                                            TokenId = Some token.Claim.TokenId
                                            Url = Some url
                                            Error = None
                                        }

                            return
                                Ok {
                                    Recipients = List.ofSeq recipients
                                    SuccessCount = successCount
                                }
                })

        // ─── Phase 21c — analyser-output cache control ─────────────
        //
        // Owner/Admin-gated. Marks every cached entry for the schema
        // (all versions, all analyser names) stale so the next
        // `GetAggregations` re-runs the analyser pipeline. Returns
        // the count of entries that *would* be cleared given a real
        // delete-by-key surface — `IResultStore` has none today, so
        // the count is a telemetry proxy and overwrite happens on
        // the next miss-path compute. With the default
        // `NoOpAnalyserCache` (no `withAnalyserCache` opt-in AND no
        // `IResultStore` to auto-construct from), the cache reports
        // zero — same effective behaviour as before the wiring
        // landed.
        RebuildAnalyserOutputs =
            fun schemaId ->
                withWriteGate (fun () -> async {
                    let! cleared = analyserCache.MarkStale(schemaId, None)
                    return Ok cleared
                })
    }