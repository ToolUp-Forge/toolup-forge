// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.PublicFormApi

open ToolUp.Platform // 0.4.1 — forge-native auth attributes
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission

// ─── Phase 21b — Public-form Fable.Remoting wire contract ──────────
//
// Surface served at `/api/public/forms/...` — gated by share-token
// validation against `IShareTokenStore` instead of by `AccessContext`.
// Per-route `SurfaceRequirement.claimBearerOnly` declarations
// registered by `FormsCompose` (Phase 66 Stream B.6) tell
// `SurfaceEnforcementMiddleware` to admit only `ClaimBearerKind`
// subjects on these routes; the server-side handler then re-reads
// the token from the request body, re-validates against
// `IShareTokenStore`, and derives every downstream parameter from
// the resolved `ShareTokenClaim`.
//
// **What this surface returns** is deliberately narrow — only the
// schema needed to render the form, plus the result of submitting
// against it. It never lists previous submissions, never returns
// aggregations, never enumerates the recipient list. Anyone with a
// valid token can render their form and submit; nothing else.
//
// Token validation rules enforced server-side per call:
//   * Signature valid against the platform signing key
//   * `ResourceKind = "forms.publishable"`
//   * `ResourceId = schemaId` matches the requested form
//   * Schema's `Visibility = Publishable`
//   * Token not expired, revoked, or use-exhausted
//
// Mismatch on any rule fails closed.

/// Stable wire constant for the share-token `ResourceKind` field.
/// `IPublicFormApi` rejects tokens whose `ResourceKind` doesn't
/// match — guards against a token issued for one resource being
/// re-used to access another.
[<Literal>]
let ResourceKind = "forms.publishable"

/// `SubmitWithToken` request payload. `Token` is the opaque wire
/// string returned by `IShareTokenStore.Issue`. Server fills in
/// `Id`, `SubmittedAt`, `Author = InvitedRespondent (tokenId,
/// claim.AttributedHandle)`, `SchemaVersion`, `WorkflowId = None`,
/// and the entity-reflection fields from the resolved claim and
/// the schema's current state.
type SubmitWithTokenRequest = {
    Token: string
    /// Field values keyed by `FieldSchema.Key`. Validated server-side.
    Values: Map<string, FieldValue>
}

/// `IPublicFormApi` — anonymous, token-gated public surface. Routed
/// at `/api/public/forms/<MethodName>` by Fable.Remoting via
/// `routeBuilder` below.
type IPublicFormApi = {
    /// Resolve the schema referenced by a valid token. Returns ONLY
    /// the schema — never historical submissions, aggregations, or
    /// the recipient list. Returns `Error` for malformed / expired /
    /// revoked / use-exhausted tokens, and for tokens that don't
    /// match `forms.publishable` or whose schema isn't `Publishable`.
    ///
    /// Token-gated public surface — server enforces the gate
    /// per-handler via `IShareTokenStore`, not the auth context. The
    /// dispatcher SHOULD NOT consult an `IAuthContext` resolver for
    /// these methods.
    [<PublicEndpoint>]
    GetSchemaByToken: string -> Async<Result<FormSchema, FormError>>
    /// Submit field values against the form referenced by a valid
    /// token. On success the submission is persisted with `Author =
    /// InvitedRespondent (tokenId, attributedHandle)`, the token's
    /// `UsedCount` is bumped, and a `FormSubmitted` audit event is
    /// emitted. Validation rejection (`FormError.ValidationFailed`)
    /// does NOT bump the use count — the respondent can correct
    /// errors and re-submit until the count is exhausted.
    [<PublicEndpoint>]
    SubmitWithToken: SubmitWithTokenRequest -> Async<Result<Submission, FormError>>
}

/// HTTP route prefix for the public-forms API. Lives under
/// `/api/public/forms/`. `FormsCompose` (Phase 66 Stream B.6)
/// registers per-route `SurfaceRequirement.claimBearerOnly` overrides
/// against `(POST, "/api/public/forms/<MethodName>")` pairs derived
/// from this builder so `SurfaceEnforcementMiddleware` only admits
/// `ClaimBearerKind` subjects on these routes.
let routeBuilder (typeName: string) (methodName: string) : string =
    sprintf "/api/public/forms/%s" methodName