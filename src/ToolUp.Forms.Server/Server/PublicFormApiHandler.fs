module ToolUp.Forms.PublicFormApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.PublicFormApi
open ToolUp.Forms.FormValidator
open ToolUp.Forms.IFormStore

// ─── Phase 21b — Public-form Fable.Remoting handler ────────────────
//
// Per-request `IPublicFormApi` builder. Resolves DI dependencies
// (`IShareTokenStore`, `IFormStore`, `IAuditLog`,
// `CustomValidatorRegistry`) and serves token-gated reads + writes.
//
// **Identity model.** This handler does NOT consult `AccessContext`
// — `FormsCompose` declares `SurfaceRequirement.claimBearerOnly` on
// both `IPublicFormApi` routes via the Phase 66 Stream B.6 per-route
// override API, so `SurfaceEnforcementMiddleware` rejects every
// subject whose kind is not `ClaimBearerKind` before the handler
// runs. The handler then re-reads the token from the request body
// and resolves identity from the validated `ShareTokenClaim`:
//   * `claim.ScopeId` → effective storage scope.
//   * `claim.ResourceId` → schema id.
//   * `claim.AttributedHandle` → echoed onto the persisted
//     submission's `Author = InvitedRespondent`.
//
// **Single source of truth.** Token validity is re-checked on every
// call against `IShareTokenStore.Validate` — a tampered token, an
// expired token, or a token whose use limit has been reached fails
// closed regardless of what the wire string carries. The defence-in-
// depth shape (middleware admits by subject kind, handler re-validates
// the claim) is preserved across Phase 66.

let private toFormError (e: ShareTokenError) : FormError =
    match e with
    | ShareTokenError.Malformed
    | ShareTokenError.InvalidSignature
    | ShareTokenError.NotFound -> FormError.Unauthorised
    | ShareTokenError.Expired -> FormError.NotFound("token", "expired")
    | ShareTokenError.RevokedToken -> FormError.NotFound("token", "revoked")
    | ShareTokenError.UseLimitExceeded -> FormError.NotFound("token", "use-limit-exceeded")
    // Phase 21e — per-token rate-limit denial. Distinct from the
    // four other token-failure cases server-side so audit log can
    // still tell them apart; `PublicEmbed.describeError` collapses
    // to the same user-visible string as the rest (L1).
    | ShareTokenError.RateLimited -> FormError.RateLimited
    | ShareTokenError.StorageFailed s -> FormError.StorageFailed s

/// Validate the wire token AND check the cross-cutting rules:
/// `ResourceKind = "forms.publishable"`, schema is `Publishable`.
/// Returns `(claim, schema)` on success.
let private resolveTokenAndSchema
    (shareTokenStore: IShareTokenStore)
    (formStore: IFormStore)
    (token: string)
    : Async<Result<ShareTokenClaim * FormSchema, FormError>> =
    async {
        match! shareTokenStore.Validate token with
        | Error err -> return Error(toFormError err)
        | Ok claim ->
            if claim.ResourceKind <> ResourceKind then
                return Error FormError.Unauthorised
            else
                match! formStore.GetSchema(claim.ScopeId, claim.ResourceId, None) with
                | Error err -> return Error err
                | Ok schema ->
                    if schema.Visibility <> Publishable then
                        // Schema was downgraded after token issue — reject so
                        // distributed link revocation works without per-token
                        // bookkeeping. Operators can flip Internal → Publishable
                        // → Internal to invalidate every outstanding token in one
                        // schema edit.
                        return Error FormError.Unauthorised
                    else
                        return Ok(claim, schema)
    }

let publicFormApi
    (shareTokenStore: IShareTokenStore)
    (formStore: IFormStore)
    (validatorRegistry: CustomValidatorRegistry)
    (auditLog: IAuditLog)
    (rateLimiter: IShareTokenRateLimiter)
    (logger: ILogger)
    (_ctx: HttpContext)
    : IPublicFormApi =

    let allocateSubmissionId () : SubmissionId = Guid.NewGuid().ToString()

    {
        GetSchemaByToken =
            fun token -> async {
                match! resolveTokenAndSchema shareTokenStore formStore token with
                | Error err -> return Error err
                | Ok(_, schema) -> return Ok schema
            }

        SubmitWithToken =
            fun req -> async {
                match! resolveTokenAndSchema shareTokenStore formStore req.Token with
                | Error err -> return Error err
                | Ok(claim, schema) ->
                    // Phase 21e (L2) — consult the per-token rate
                    // limiter before any validation or persistence
                    // side-effect. A token whose claim has no
                    // `RateLimit` configured skips this gate entirely
                    // — `None` = no per-token rate.
                    let! rateDecision =
                        match claim.RateLimit with
                        | None -> async { return Ok() }
                        | Some rate -> rateLimiter.Admit(claim.ScopeId, claim.TokenId, rate)

                    match rateDecision with
                    | Error err -> return Error(toFormError err)
                    | Ok() ->
                        // Validate FIRST so a bad submission doesn't burn the
                        // respondent's only use slot. The token is only marked
                        // used after the submission persists.
                        match FormValidator.validate validatorRegistry schema req.Values with
                        | Error errors -> return Error(FormError.ValidationFailed errors)
                        | Ok() ->
                            let submission: Submission = {
                                Id = allocateSubmissionId ()
                                Type = Submission.entityType
                                Version = 1
                                FormId = schema.Id
                                SchemaVersion = schema.Version
                                SubmittedAt = DateTimeOffset.UtcNow
                                Author = InvitedRespondent(claim.TokenId, claim.AttributedHandle)
                                Values = req.Values
                                State = Submitted
                                WorkflowId = None
                            }

                            match! formStore.SaveSubmission(claim.ScopeId, submission) with
                            | Error err -> return Error err
                            | Ok saved ->
                                // Bump the use count AFTER successful persist.
                                // Phase 116 — the result was previously
                                // discarded (`let! _ = ...`), so a storage
                                // failure, a token revoked between validation
                                // and now, or a use-limit lost to a concurrent
                                // submitter all returned success silently —
                                // letting a `UseLimit = 1` token be consumed
                                // more than once. Surface it instead: the
                                // submission is already durable, but the
                                // response is an error so the caller and the
                                // use-count invariant both see the truth.
                                match! shareTokenStore.MarkUsed(claim.ScopeId, claim.TokenId) with
                                | Error markErr ->
                                    logger.Error(
                                        sprintf
                                            "[PublicFormApi] submission %s persisted for form %s but MarkUsed failed (token %s, scope %s): %A"
                                            saved.Id
                                            saved.FormId
                                            claim.TokenId
                                            claim.ScopeId
                                            markErr,
                                        None
                                    )

                                    return Error(toFormError markErr)
                                | Ok() ->
                                    // FormSubmitted audit carries the synthesised
                                    // respondent identity (`respondent:{tokenId}`)
                                    // — never the raw email.
                                    let payload: FormSubmittedPayload = {
                                        UserId = "respondent:" + claim.TokenId
                                        FormId = saved.FormId
                                        SubmissionId = saved.Id
                                        SchemaVersion = saved.SchemaVersion
                                        FieldCount = saved.Values.Count
                                        WorkflowId = None
                                        InitialState = SubmissionState.toIndexValue saved.State
                                    }

                                    do! auditLog.Record(claim.ScopeId, AuditEvent.FormSubmitted payload)
                                    return Ok saved
            }
    }