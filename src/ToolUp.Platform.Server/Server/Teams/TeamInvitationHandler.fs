// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Teams.TeamInvitationHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement

// ─── Phase 3d — team-invitation handler ──────────────────────────────
//
// Constructs an `ITeamInviteApi` per request, gating each operation on
// the caller's role and threading the invite-specific claim payload
// (role + email hint) through the share-token substrate's
// `AttributedHandle` field.
//
// The share-token substrate already enforces:
//   * Signature validation
//   * Expiry / revocation / use-limit gates
//   * Per-scope persistence + listing
//
// This handler adds:
//   * `ResourceKind = "platform.team_invite"` discrimination
//   * Role + email-hint carriage via `AttributedHandle`
//   * `ITeamStore.AddMember` invocation on acceptance
//   * Audit emission for the five `TeamInvite*` lifecycle events
//   * `Owner`/`Admin` gating on issue / revoke / list
//   * `Owner`-role refusal at issue time (ownership transfer is a
//     separate Phase 5f surface, not a delegation flow)

// ─── AttributedHandle carrier ────────────────────────────────────────
//
// The role + optional email hint ride as a simple delimited string in
// the substrate's free-form `AttributedHandle` field:
//
//     "<role>|<emailHint?>"      e.g. "Member|alice@example.com"
//                                or   "Admin|"
//
// Plain-text was chosen over JSON for round-trip stability — the
// substrate's blob serialiser is `FableConverters`, which can
// interact awkwardly with JSON nested inside a `string option` field
// across the writeback-on-MarkUsed path. The simple format is
// trivially round-trippable through any serialiser.

let private serialiseAttribution (role: TeamRole) (emailHint: string option) : string =
    sprintf "%s|%s" (TeamRoles.displayName role) (emailHint |> Option.defaultValue "")

let private parseRole (s: string) : TeamRole option =
    match s with
    | "Owner" -> Some Owner
    | "Admin" -> Some Admin
    | "Member" -> Some Member
    | _ -> None

let private parseAttribution (claim: ShareTokenClaim) : Result<TeamRole * string option, string> =
    match claim.AttributedHandle with
    | None -> Error "Invitation token is missing its role attribution."
    | Some raw ->
        match raw.IndexOf('|') with
        | -1 ->
            // No pipe — treat the entire string as the role (compat
            // with future shorter serialisations).
            match parseRole raw with
            | Some role -> Ok(role, None)
            | None -> Error "Invitation token carries an unknown role value."
        | i ->
            let rolePart = raw.Substring(0, i)
            let hintPart = raw.Substring(i + 1)

            let emailHint =
                if String.IsNullOrEmpty hintPart then
                    None
                else
                    Some hintPart

            match parseRole rolePart with
            | Some role -> Ok(role, emailHint)
            | None -> Error "Invitation token carries an unknown role value."

// ─── Helpers ────────────────────────────────────────────────────────

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
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

let private teamScopeId (teamId: string) : string = $"team-{teamId}"

let private ensureManageMembers (ts: ITeamStore) (callerUserId: string) (teamId: string) : Async<Result<unit, string>> = async {
    let! role = ts.GetMemberRole(teamId, callerUserId)

    match role with
    | Some r when TeamRoles.canManageMembers r -> return Ok()
    | Some r ->
        return Error $"Only team owners and admins can manage invitations. Your role: {TeamRoles.displayName r}."
    | None -> return Error "You are not a member of this team."
}

let private buildInviteUrl (publicBaseUrl: string option) (token: string) : Result<string, string> =
    match publicBaseUrl with
    | Some baseUrl -> Ok(sprintf "%s/invite/%s" (baseUrl.TrimEnd('/')) token)
    | None ->
        Error
            "ServerConfig.PublicBaseUrl is not configured — cannot build an invitation URL. \
             Set it to the deployment's public origin (e.g. https://app.example.com) and retry."

let private projectSummary (claim: ShareTokenClaim) : TeamInviteSummary option =
    match parseAttribution claim with
    | Error _ -> None
    | Ok(role, emailHint) ->
        let remaining =
            match claim.UseLimit with
            | Some limit -> max 0 (limit - claim.UsedCount)
            | None -> Int32.MaxValue

        Some {
            TokenId = claim.TokenId
            TeamId = claim.ResourceId
            Role = role
            InviterUserId = claim.IssuedBy
            EmailHint = emailHint
            ExpiresAt = claim.ExpiresAt.UtcDateTime
            RemainingUses = remaining
            Revoked = claim.Revoked
        }

// ─── Per-request API construction ───────────────────────────────────

let teamInvitationApi
    (shareTokenStore: IShareTokenStore)
    (teamStore: ITeamStore)
    (auditLog: IAuditLog)
    (config: ServerConfig)
    (ctx: HttpContext)
    : ITeamInviteApi =
    let access = resolveAccessContext ctx
    let scopeOf (teamId: string) = teamScopeId teamId

    {
        IssueInvite =
            fun req -> async {
                if req.Role = Owner then
                    return
                        Error
                            "Owner role cannot be granted via an invitation. \
                             Use the team ownership-transfer flow instead."
                else
                    match! ensureManageMembers teamStore access.UserId req.TeamId with
                    | Error msg -> return Error msg
                    | Ok() ->
                        let expiresIn = req.ExpiresIn |> Option.defaultValue TeamInviteTypes.DefaultExpiry
                        let expiresAt = DateTimeOffset.UtcNow.Add expiresIn

                        let maxUses =
                            req.MaxUses
                            |> Option.filter (fun n -> n > 0)
                            |> Option.defaultValue TeamInviteTypes.DefaultMaxUses

                        let issueRequest: ShareTokenIssueRequest = {
                            ScopeId = scopeOf req.TeamId
                            ResourceKind = TeamInviteTypes.ResourceKind
                            ResourceId = req.TeamId
                            AttributedHandle = Some(serialiseAttribution req.Role req.EmailHint)
                            IssuedBy = access.UserId
                            ExpiresAt = Some expiresAt
                            UseLimit = Some(Some maxUses)
                            RateLimit = None
                        }

                        match! shareTokenStore.Issue issueRequest with
                        | Error err -> return Error(sprintf "Could not issue invitation: %A" err)
                        | Ok token ->
                            match buildInviteUrl config.PublicBaseUrl token.Token with
                            | Error msg -> return Error msg
                            | Ok url ->
                                let payload: TeamInviteIssuedPayload = {
                                    TeamId = req.TeamId
                                    TokenId = token.Claim.TokenId
                                    InviterUserId = access.UserId
                                    Role = req.Role
                                    EmailHint = req.EmailHint
                                    ExpiresAt = expiresAt.UtcDateTime
                                    MaxUses = maxUses
                                }

                                do! auditLog.Record(scopeOf req.TeamId, TeamInviteIssued payload)

                                return
                                    Ok {
                                        InviteUrl = url
                                        TokenId = token.Claim.TokenId
                                    }
            }

        AcceptInvite =
            fun token -> async {
                if
                    AuthenticatedUser.isAnonymous {
                        AuthenticatedUser.anonymous with
                            UserId = access.UserId
                    }
                then
                    return Error "You must sign in before accepting an invitation."
                else
                    match! shareTokenStore.Validate token with
                    | Error err ->
                        let message =
                            match err with
                            | ShareTokenError.Malformed
                            | ShareTokenError.InvalidSignature
                            | ShareTokenError.NotFound -> "This invitation link is invalid."
                            | ShareTokenError.Expired -> "This invitation has expired."
                            | ShareTokenError.RevokedToken -> "This invitation has been revoked."
                            | ShareTokenError.UseLimitExceeded -> "This invitation has been used."
                            // Phase 21e — RateLimited is never raised by
                            // Validate (the limiter gate is applied at
                            // submit-time on the publishable-forms path,
                            // not by the share-token store). Surface a
                            // generic invalid-link message if it ever
                            // reaches here so the match stays exhaustive.
                            | ShareTokenError.RateLimited -> "This invitation link is invalid."
                            | ShareTokenError.StorageFailed s -> sprintf "Invitation lookup failed: %s" s

                        return Error message
                    | Ok claim when claim.ResourceKind <> TeamInviteTypes.ResourceKind ->
                        return Error "This link is not a team invitation."
                    | Ok claim ->
                        match parseAttribution claim with
                        | Error msg -> return Error msg
                        | Ok(role, _emailHint) ->
                            let teamId = claim.ResourceId
                            let scope = scopeOf teamId

                            // Already-member is a benign success — the
                            // recipient may have followed the link a
                            // second time. Bump the use count to keep
                            // accounting honest.
                            let! existingRole = teamStore.GetMemberRole(teamId, access.UserId)

                            let addResult = async {
                                match existingRole with
                                | Some _ -> return Ok()
                                | None ->
                                    match! teamStore.AddMember(teamId, access.UserId, role) with
                                    | Ok() -> return Ok()
                                    | Error e -> return Error e
                            }

                            match! addResult with
                            | Error msg -> return Error(sprintf "Could not add you to the team: %s" msg)
                            | Ok() ->
                                // First-team-becomes-active: without
                                // this the invitee resolves as
                                // `AuthenticatedUser` (personal scope)
                                // on every request after accepting —
                                // empty data, empty module list. Only
                                // writes when their pointer is unset;
                                // also heals already-member re-redeems
                                // left stranded by pre-fix adds.
                                do! ActiveTeamPolicy.ensureActiveTeam teamStore access.UserId teamId

                                // Best-effort use-count bump. A failure
                                // here does not roll back the add —
                                // the membership is durable and the
                                // substrate's `UseLimitExceeded` gate
                                // catches double-redemption on the
                                // next call.
                                let! markResult = shareTokenStore.MarkUsed(scope, claim.TokenId)

                                let remaining =
                                    match markResult, claim.UseLimit with
                                    | Ok(), Some limit -> max 0 (limit - claim.UsedCount - 1)
                                    | _, Some limit -> max 0 (limit - claim.UsedCount)
                                    | _ -> 0

                                let teamName = async {
                                    match! teamStore.GetTeam teamId with
                                    | Some info -> return info.Name
                                    | None -> return teamId
                                }

                                let! resolvedTeamName = teamName

                                do!
                                    auditLog.Record(
                                        scope,
                                        TeamInviteAccepted {
                                            TeamId = teamId
                                            TokenId = claim.TokenId
                                            InviteeUserId = access.UserId
                                            InviterUserId = claim.IssuedBy
                                            Role = role
                                        }
                                    )

                                do!
                                    auditLog.Record(
                                        scope,
                                        TeamInviteRedeemed {
                                            TeamId = teamId
                                            TokenId = claim.TokenId
                                            RemainingUses = remaining
                                        }
                                    )

                                return
                                    Ok {
                                        TeamId = teamId
                                        TeamName = resolvedTeamName
                                        Role = role
                                    }
            }

        RevokeInvite =
            fun tokenId -> async {
                // Resolve the team id from any outstanding claim so
                // the gate can target the right team.
                // Cheap path: try the active team first via the
                // caller's access context.
                let candidates =
                    match access.TeamId with
                    | Some t -> [ t ]
                    | None -> []

                let rec findInTeams remaining = async {
                    match remaining with
                    | [] -> return None
                    | teamId :: rest ->
                        let! list = shareTokenStore.ListByResource(scopeOf teamId, TeamInviteTypes.ResourceKind, teamId)

                        match list |> List.tryFind (fun c -> c.TokenId = tokenId) with
                        | Some claim -> return Some(teamId, claim)
                        | None -> return! findInTeams rest
                }

                let! initial = findInTeams candidates

                let! resolved = async {
                    match initial with
                    | Some _ -> return initial
                    | None ->
                        // Fall back to walking every team the
                        // caller is a member of. Bounded by the
                        // caller's own membership cardinality.
                        let! callerTeams = teamStore.GetTeamsForUser access.UserId

                        return!
                            findInTeams (
                                callerTeams
                                |> List.map _.TeamId
                                |> List.filter (fun id -> not (List.contains id candidates))
                            )
                }

                match resolved with
                | None -> return Error "Invitation not found."
                | Some(teamId, claim) ->
                    match! ensureManageMembers teamStore access.UserId teamId with
                    | Error msg -> return Error msg
                    | Ok() ->
                        match! shareTokenStore.Revoke(scopeOf teamId, claim.TokenId, access.UserId) with
                        | Error err -> return Error(sprintf "Revoke failed: %A" err)
                        | Ok() ->
                            do!
                                auditLog.Record(
                                    scopeOf teamId,
                                    TeamInviteRevoked {
                                        TeamId = teamId
                                        TokenId = claim.TokenId
                                        ActorUserId = access.UserId
                                    }
                                )

                            return Ok()
            }

        ListPendingInvites =
            fun teamId -> async {
                match! ensureManageMembers teamStore access.UserId teamId with
                | Error msg -> return Error msg
                | Ok() ->
                    let! claims = shareTokenStore.ListByResource(scopeOf teamId, TeamInviteTypes.ResourceKind, teamId)

                    return Ok(claims |> List.choose projectSummary)
            }

        IssuePendingInviteByEmail =
            fun req -> async {
                // 0.5.9 — Unconditional stderr breadcrumbs, no DI / no try/with.
                // The 0.5.8 ILogger-based breadcrumbs did not appear in the App
                // Service log, which leaves two hypotheses on the table: the
                // 0.5.8 code didn't ship (CI/NuGet cache hit) or the silent
                // no-op ILogger fallback was selected and our exception
                // escaped before any logger.Info ran. eprintfn writes to
                // stderr unconditionally — Docker captures stderr into the
                // default_docker log so it shows up regardless of DI state,
                // regardless of forge's logger plumbing.
                eprintfn "[invite] HANDLER ENTRY 0.5.9: Email=%s TeamId=%s Role=%A" req.Email req.TeamId req.Role

                if req.Role = Owner then
                    eprintfn "[invite] EXIT: Owner role refused"

                    return
                        Error
                            "Owner role cannot be granted via a pending-by-email invitation. \
                             Use the team ownership-transfer flow instead."
                elif System.String.IsNullOrWhiteSpace req.Email then
                    eprintfn "[invite] EXIT: Email empty"
                    return Error "Email is required for a pending-by-email invitation."
                else
                    eprintfn "[invite] calling ensureManageMembers"

                    match! ensureManageMembers teamStore access.UserId req.TeamId with
                    | Error msg ->
                        eprintfn "[invite] ensureManageMembers Error: %s" msg
                        return Error msg
                    | Ok() ->
                        eprintfn "[invite] ensureManageMembers Ok"
                        let expiresIn = req.ExpiresIn |> Option.defaultValue TeamInviteTypes.DefaultExpiry
                        let expiresAt = DateTime.UtcNow.Add expiresIn

                        let pending: PendingInviteByEmail = {
                            TeamId = req.TeamId
                            Role = req.Role
                            ExpiresAt = expiresAt
                            InviterUserId = access.UserId
                        }

                        let pendingStore =
                            ctx.RequestServices.GetService(typeof<IPendingInviteStore>) :?> IPendingInviteStore

                        match! pendingStore.Upsert(req.Email, pending) with
                        | Error err -> return Error(sprintf "Could not store pending invite: %A" err)
                        | Ok() ->
                            // 0.5.8 — Diagnostic logging path. Resolve the
                            // SDK logger so each step in the post-Upsert
                            // chain emits a breadcrumb. The 0.5.7 path was
                            // 500ing somewhere between Upsert and the
                            // function-return; the logs identify which
                            // line. Logger resolution falls back to a
                            // throwaway no-op if DI hasn't wired one (test
                            // paths) so logging itself never throws.
                            let logger =
                                match ctx.RequestServices.GetService(typeof<ILogger>) with
                                | :? ILogger as l -> l
                                | _ ->
                                    // No-op fallback for test paths that bypass
                                    // compose-time DI registration. ConsoleLogger
                                    // requires LogLevel + Set<string> args; the
                                    // simpler floor is a synthetic logger that
                                    // swallows every level so logging itself
                                    // never throws.
                                    { new ILogger with
                                        member _.Debug(_: string) = ()
                                        member _.Info(_: string) = ()
                                        member _.Warn(_: string) = ()
                                        member _.Error(_: string, _: exn option) = ()
                                    }

                            let payload: TeamInviteIssuedPayload = {
                                TeamId = req.TeamId
                                // No token-id for the pending-by-email
                                // flow — the email is the "token". Use a
                                // synthetic prefix so audit consumers can
                                // tell the two flows apart at a glance.
                                TokenId = sprintf "pending:%s" (req.Email.ToLowerInvariant())
                                InviterUserId = access.UserId
                                Role = req.Role
                                EmailHint = Some req.Email
                                ExpiresAt = expiresAt
                                MaxUses = 1
                            }

                            // 0.5.8 — wrap audit-log emission. The
                            // pending-invite store has already accepted
                            // the row, so the user-facing invitation is
                            // durable regardless of audit success. If
                            // Record throws (audit sink misconfigured,
                            // serialisation bug, EventStore unreachable),
                            // log it and continue — failing the invite
                            // would be the wrong trade.
                            logger.Info "[invite] audit Record: starting"

                            try
                                do! auditLog.Record(scopeOf req.TeamId, TeamInviteIssued payload)
                                logger.Info "[invite] audit Record: ok"
                            with ex ->
                                logger.Error(
                                    sprintf "[invite] audit Record FAILED: %s | %s" (ex.GetType().FullName) ex.Message,
                                    Some ex
                                )

                            // 0.5.7 — opportunistic invitation email.
                            // Resolves IUserDirectory from DI; when registered
                            // (e.g. ToolUp.AuthProviders.EntraDirectory with
                            // a SenderUserId), sends the invitee a branded
                            // "you've been added to <team>" email. Failure is
                            // non-fatal — the pending-invite store entry is
                            // already written, the invitee can sign in and
                            // pick up the membership without the email; the
                            // operator just has to tell them out of band.
                            // The audit row recording the invite issuance is
                            // also already durable.
                            //
                            // 0.5.8 — each step emits a breadcrumb so the
                            // App Service log identifies the failing line
                            // when the operator reports a 500.
                            logger.Info "[invite] opportunistic email: entering"

                            try
                                match ctx.RequestServices.GetService(typeof<IUserDirectory>) with
                                | :? IUserDirectory as directory ->
                                    logger.Info "[invite] IUserDirectory: resolved"

                                    // Resolve the inviter's display name from the
                                    // directory itself when possible; the invite
                                    // path doesn't otherwise carry one. A single
                                    // SearchUsers call against the inviter's own
                                    // id is cheap (one Graph round-trip) and
                                    // surfaces a friendly From-line in the email.
                                    let! inviterSummary = directory.SearchUsers(access.UserId, 1)
                                    logger.Info(sprintf "[invite] SearchUsers: %A" inviterSummary)

                                    let inviterName =
                                        match inviterSummary with
                                        | Ok(s :: _) -> s.DisplayName
                                        | _ -> None

                                    let! teamLookup = teamStore.GetTeam req.TeamId
                                    logger.Info(sprintf "[invite] GetTeam: %A" teamLookup)

                                    let teamName =
                                        match teamLookup with
                                        | Some t -> t.Name
                                        | None ->
                                            // Race against deletion — fall back to
                                            // the team id so the recipient still
                                            // sees a meaningful identifier rather
                                            // than a blank.
                                            req.TeamId

                                    // The SDK has no consumer-supplied app name
                                    // field on ServerConfig. Walk
                                    // TOOLUP_APP_NAME → request host header →
                                    // "ToolUp" fallback so the email's brand line
                                    // matches whatever the deployment surfaces to
                                    // its users.
                                    let appName =
                                        match System.Environment.GetEnvironmentVariable "TOOLUP_APP_NAME" with
                                        | null
                                        | "" ->
                                            let host = ctx.Request.Host.Value

                                            if System.String.IsNullOrWhiteSpace host then
                                                "ToolUp"
                                            else
                                                host
                                        | n -> n

                                    let redirectUrl =
                                        // Server has no first-class knowledge of
                                        // its public URL; walk the request's
                                        // Origin / scheme+host headers.
                                        let originHeader = ctx.Request.Headers.["Origin"].ToString()

                                        if not (System.String.IsNullOrWhiteSpace originHeader) then
                                            originHeader.TrimEnd('/') + "/"
                                        else
                                            let scheme = ctx.Request.Scheme
                                            let host = ctx.Request.Host.Value
                                            sprintf "%s://%s/" scheme host

                                    let notification = {
                                        Email = req.Email
                                        TeamName = teamName
                                        InviterName = inviterName
                                        AppName = appName
                                        RedirectUrl = redirectUrl
                                        Role = req.Role
                                    }

                                    logger.Info "[invite] NotifyInvitation: calling"

                                    // Best-effort. The Error branch carries the
                                    // companion's reason (e.g. "notification
                                    // disabled: SenderUserId not set") which is
                                    // useful for log grepping but not actionable
                                    // at the API layer — the invite is durable
                                    // either way.
                                    let! notifyResult = directory.NotifyInvitation notification
                                    logger.Info(sprintf "[invite] NotifyInvitation: %A" notifyResult)
                                | _ ->
                                    // No directory companion wired — the
                                    // pre-0.5.7 "operator tells the invitee out
                                    // of band" path. Nothing to do.
                                    logger.Info "[invite] IUserDirectory: not registered"
                            with ex ->
                                // Defensive — a Graph 5xx, a Network exception,
                                // a DI surprise — any of these would otherwise
                                // surface to the operator as a failed invite,
                                // which is wrong. The invite succeeded; the
                                // notification didn't. Swallow but log so
                                // the operator can diagnose.
                                logger.Error(
                                    sprintf
                                        "[invite] opportunistic email FAILED: %s | %s | inner=%s"
                                        (ex.GetType().FullName)
                                        ex.Message
                                        (if isNull ex.InnerException then
                                             "<none>"
                                         else
                                             sprintf
                                                 "%s: %s"
                                                 (ex.InnerException.GetType().FullName)
                                                 ex.InnerException.Message),
                                    Some ex
                                )

                            logger.Info "[invite] returning Ok"
                            return Ok()
            }

        ListPendingInvitesByEmail =
            fun teamId -> async {
                match! ensureManageMembers teamStore access.UserId teamId with
                | Error msg -> return Error msg
                | Ok() ->
                    let pendingStore =
                        ctx.RequestServices.GetService(typeof<IPendingInviteStore>) :?> IPendingInviteStore

                    match! pendingStore.ListAll() with
                    | Error err -> return Error(sprintf "Could not list pending invites: %A" err)
                    | Ok all -> return Ok(all |> List.filter (fun (_, entry) -> entry.TeamId = teamId))
            }

        RevokePendingInviteByEmail =
            fun email -> async {
                if System.String.IsNullOrWhiteSpace email then
                    return Error "Email is required."
                else
                    let pendingStore =
                        ctx.RequestServices.GetService(typeof<IPendingInviteStore>) :?> IPendingInviteStore

                    match! pendingStore.ListAll() with
                    | Error err -> return Error(sprintf "Could not look up pending invite: %A" err)
                    | Ok all ->
                        let normalised = email.ToLowerInvariant()

                        match all |> List.tryFind (fun (e, _) -> e = normalised) with
                        | None ->
                            // Idempotent — removing an absent entry succeeds.
                            return Ok()
                        | Some(_, entry) ->
                            match! ensureManageMembers teamStore access.UserId entry.TeamId with
                            | Error msg -> return Error msg
                            | Ok() ->
                                // `NotFound` here is benign — racing
                                // revokes can drain the entry between
                                // the ListAll above and the Remove call;
                                // collapse to success. Other errors
                                // propagate so the caller can retry.
                                match! pendingStore.Remove normalised with
                                | Ok()
                                | Error PendingInviteStoreError.NotFound ->
                                    do!
                                        auditLog.Record(
                                            scopeOf entry.TeamId,
                                            TeamInviteRevoked {
                                                TeamId = entry.TeamId
                                                TokenId = sprintf "pending:%s" normalised
                                                ActorUserId = access.UserId
                                            }
                                        )

                                    return Ok()
                                | Error err -> return Error(sprintf "Could not revoke pending invite: %A" err)
            }
    }

/// Helper used by `ScopeResolutionMiddleware` (and any other
/// scope-resolution path that gets a freshly-authenticated user) to
/// drain a pending email-keyed invitation and apply it. Returns the
/// consumed entry on success so the caller can refresh in-memory
/// caches; `None` when no pending entry matches the email or the
/// match expired. Safe to call on every sign-in resolve — the default
/// `InMemoryPendingInviteStore` is bounded by its 30-second in-memory
/// cache, and the eventual `BlobPendingInviteStore` (Phase 9c half-2
/// follow-up) accepts the per-call substrate round-trip in exchange
/// for multi-instance correctness.
///
/// On `AddMember` failure the pending entry is still consumed
/// (single-shot semantics — `TryConsumeForEmail` removes it atomically
/// with the read) but a `TeamInviteAcceptedFromPendingFailed` audit
/// event is emitted carrying the failure reason. Without this, a
/// transient store glitch would silently drop the invitation with no
/// trace; the audit row gives operators a fix-forward signal.
///
/// On store-level failure (the `IPendingInviteStore` itself returns
/// `Error` — substrate unreachable, optimistic-concurrency exhaustion
/// on the future ETag impl), this function returns `None`. The pending
/// entry is left in place for the next sign-in resolve to retry; no
/// audit emission because nothing was consumed. Callers treat this
/// as "no pending match", same as the no-entry case.
let tryConsumePendingForUser
    (pendingStore: IPendingInviteStore)
    (teamStore: ITeamStore)
    (auditLog: IAuditLog)
    (user: AuthenticatedUser)
    : Async<PendingInviteByEmail option> =
    async {
        match user.Email with
        | None -> return None
        | Some email ->
            match! pendingStore.TryConsumeForEmail email with
            | Error _ -> return None
            | Ok None -> return None
            | Ok(Some entry) ->
                // Skip if already a member — substrate state is the
                // source of truth, the pending row is a hint.
                let! existing = teamStore.GetMemberRole(entry.TeamId, user.UserId)

                match existing with
                | Some _ ->
                    // Already a member but possibly stranded with no
                    // active-team pointer (pre-fix add paths never set
                    // it). Heal on the sign-in resolve — only writes
                    // when the pointer is unset.
                    do! ActiveTeamPolicy.ensureActiveTeam teamStore user.UserId entry.TeamId
                    return Some entry
                | None ->
                    match! teamStore.AddMember(entry.TeamId, user.UserId, entry.Role) with
                    | Error reason ->
                        do!
                            auditLog.Record(
                                teamScopeId entry.TeamId,
                                TeamInviteAcceptedFromPendingFailed {
                                    TeamId = entry.TeamId
                                    InviteeUserId = user.UserId
                                    InviteeEmail = email
                                    InviterUserId = entry.InviterUserId
                                    Role = entry.Role
                                    Reason = reason
                                }
                            )

                        return Some entry
                    | Ok() ->
                        // First-team-becomes-active — same policy as
                        // the interactive accept path above.
                        do! ActiveTeamPolicy.ensureActiveTeam teamStore user.UserId entry.TeamId

                        do!
                            auditLog.Record(
                                teamScopeId entry.TeamId,
                                TeamInviteAcceptedFromPending {
                                    TeamId = entry.TeamId
                                    InviteeUserId = user.UserId
                                    InviteeEmail = email
                                    InviterUserId = entry.InviterUserId
                                    Role = entry.Role
                                }
                            )

                        return Some entry
    }