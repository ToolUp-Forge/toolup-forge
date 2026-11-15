// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyRegistrationPolicy

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Config.Auth

// ─── Registration policy (secure by default) ─────────────────────────
//
// Enrolling a passkey creates a credential that authenticates as an
// identity, and `register/complete` signs that identity in, so the
// identity is decided by the server and never by the request. A caller
// may register only when one of these holds, in this order:
//   • an authenticated session drives the identity (its own id and its
//     own email — the body's are not read), OR
//   • the one-time bootstrap token is presented and has not been spent
//     (a fresh deployment's FIRST credential), OR
//   • an invitation link's token validates: the server mints the
//     identity, and the complete leg redeems one use of the link and adds
//     the member with the invited role, OR
//   • `AllowOpenRegistration = true`, an explicit opt-in surfaced by
//     preflight.
// On the bootstrap and open paths the request may propose a NEW user id
// only: an id that holds a passkey, is a platform admin or is a member
// of any team is refused at begin, and checked and claimed again at
// complete before anything is persisted.

/// The identity a registration ceremony enrols a credential for.
type RegistrationIdentity = {
    UserId: UserId
    DisplayName: string
    Email: string option
}

/// Why a registration was permitted — carried to the complete leg, which
/// admits the enrolment on it, and surfaced in the audit trail.
type RegistrationGrant =
    /// A signed-in session enrols a passkey for its own user.
    | ExistingSession
    /// The unspent bootstrap token enrols a fresh deployment's first credential.
    | Bootstrap
    /// An invitation link's token enrols a server-minted identity into the invited team.
    | PendingInvite
    /// `AllowOpenRegistration` enrols a new user id.
    | OpenRegistration

/// Functions over `RegistrationGrant`.
module RegistrationGrant =
    /// Read back the grant a pending challenge carries (`string grant`).
    let tryParse (name: string) : RegistrationGrant option =
        match name with
        | "ExistingSession" -> Some ExistingSession
        | "Bootstrap" -> Some Bootstrap
        | "PendingInvite" -> Some PendingInvite
        | "OpenRegistration" -> Some OpenRegistration
        | _ -> None

/// What the gate reads and writes about identities, invitations and the
/// bootstrap token — every one a server-side lookup, never the request.
/// The host builds it over the composed stores; tests supply it directly.
type RegistrationLookups = {
    /// The id already holds an enrolled passkey.
    HasCredential: UserId -> Async<bool>
    /// The id is a principal the deployment knows: a platform admin or a
    /// member of any team.
    IsKnownPrincipal: UserId -> Async<bool>
    /// Claim a new id for one enrolment, create-only, so two ceremonies
    /// cannot both enrol it. `Error` when it is already claimed.
    ClaimIdentity: UserId -> Async<Result<unit, string>>
    /// Release a claim whose enrolment was refused after the claim.
    ReleaseIdentity: UserId -> Async<unit>
    /// Validate an invitation link token without redeeming it.
    ValidateInvite: string -> Async<Result<unit, string>>
    /// Redeem one use of an invitation link for a new principal.
    RedeemInvite: string -> UserId -> Async<Result<unit, string>>
    /// The bootstrap token has been spent.
    BootstrapConsumed: unit -> Async<bool>
    /// Spend the bootstrap token; `Error` when another enrolment won it.
    ConsumeBootstrap: UserId -> Async<Result<unit, string>>
}

/// The pure gate decision: given the resolved booleans, decide whether
/// (and on what grounds) registration proceeds. A session wins, so a
/// signed-in caller always enrols for itself.
let decide
    (config: PasskeyConfig)
    (isAuthenticated: bool)
    (bootstrapMatches: bool)
    (hasValidInvite: bool)
    : Result<RegistrationGrant, string> =
    if isAuthenticated then
        Ok ExistingSession
    elif bootstrapMatches then
        Ok Bootstrap
    elif hasValidInvite then
        Ok PendingInvite
    elif config.AllowOpenRegistration then
        Ok OpenRegistration
    else
        Error
            "Passkey registration is invite-gated: sign in first, open an invitation link, or supply the one-time bootstrap token. Set AllowOpenRegistration = true to permit open enrolment."

/// Constant-time comparison of the presented bootstrap token against the
/// configured one. Both must be `Some` and non-empty; a `None` on either
/// side (bootstrap disabled, or no token presented) is `false`.
let bootstrapMatches (config: PasskeyConfig) (presented: string option) : bool =
    match config.BootstrapToken, presented with
    | Some expected, Some provided when expected <> "" && provided <> "" ->
        JwtCrypto.fixedTimeEquals (Encoding.UTF8.GetBytes expected) (Encoding.UTF8.GetBytes provided)
    | _ -> false

let private refusedExisting =
    "A passkey cannot be enrolled for an existing user without that user's own session: sign in as them and add the passkey from the session, or choose another username."

/// Refuse an id the deployment already knows. The bootstrap and open
/// paths call it at begin and again at complete.
let requireNewIdentity (lookups: RegistrationLookups) (userId: UserId) : Async<Result<unit, string>> = async {
    let! hasCredential = lookups.HasCredential userId

    if hasCredential then
        return Error refusedExisting
    else
        let! known = lookups.IsKnownPrincipal userId
        return if known then Error refusedExisting else Ok()
}

let private presented (value: string option) : string option =
    value |> Option.filter (String.IsNullOrWhiteSpace >> not)

/// Resolve the registration identity, applying the gate. `currentUser`
/// is the request's resolved principal (anonymous when unauthenticated).
let resolveIdentity
    (config: PasskeyConfig)
    (currentUser: AuthenticatedUser)
    (lookups: RegistrationLookups)
    (req: RegisterBeginRequest)
    : Async<Result<RegistrationGrant * RegistrationIdentity, string>> =
    async {
        let isAuthenticated = not (AuthenticatedUser.isAnonymous currentUser)
        let bootstrapOk = not isAuthenticated && bootstrapMatches config req.BootstrapToken

        let! bootstrapSpent =
            if bootstrapOk then
                lookups.BootstrapConsumed()
            else
                async.Return false

        let! invite = async {
            match presented req.InviteToken with
            | Some token when not isAuthenticated && not bootstrapOk ->
                let! validated = lookups.ValidateInvite token
                return Some validated
            | _ -> return None
        }

        match invite with
        | _ when bootstrapSpent ->
            return
                Error "The bootstrap token has already been used. Sign in, or ask an administrator for an invitation."
        | Some(Error refusal) -> return Error refusal
        | _ ->
            match decide config isAuthenticated bootstrapOk (invite = Some(Ok())) with
            | Error e -> return Error e
            | Ok ExistingSession ->
                return
                    Ok(
                        ExistingSession,
                        {
                            UserId = currentUser.UserId
                            DisplayName =
                                if currentUser.DisplayName <> "" then
                                    currentUser.DisplayName
                                else
                                    currentUser.UserId.Value
                            Email = currentUser.Email
                        }
                    )
            | Ok PendingInvite ->
                // The invitation names a team and a role, not a person, so
                // the identity is minted here; the body's username is not
                // read.
                let userId = UserId("pk-" + Guid.NewGuid().ToString "N")

                return
                    Ok(
                        PendingInvite,
                        {
                            UserId = userId
                            DisplayName = presented req.DisplayName |> Option.defaultValue userId.Value
                            Email = None
                        }
                    )
            | Ok grant ->
                // Bootstrap / OpenRegistration propose a new id. It is
                // sanitised into a safe scope id (path-traversal /
                // control-char defence), exactly as StaticJwt sanitises
                // `sub`, and must be unknown to the deployment.
                match presented req.Username with
                | None -> return Error "A username is required to enrol a passkey on this registration path."
                | Some raw ->
                    match IdentitySanitiser.sanitiseScopeId raw with
                    | Error reason -> return Error $"Invalid username: {reason}"
                    | Ok userId ->
                        match! requireNewIdentity lookups (UserId userId) with
                        | Error e -> return Error e
                        | Ok() ->
                            return
                                Ok(
                                    grant,
                                    {
                                        UserId = UserId userId
                                        DisplayName = presented req.DisplayName |> Option.defaultValue userId
                                        Email = None
                                    }
                                )
    }

/// The complete leg's admission, run after the attestation verifies and
/// before the credential is persisted. A session enrols for itself. Every
/// other grant re-checks that the id is new and claims it, then spends
/// what the grant rested on: the bootstrap token, or one use of the
/// invitation (which also adds the member). A refusal after the claim
/// releases it.
let admitEnrolment
    (lookups: RegistrationLookups)
    (grant: RegistrationGrant)
    (inviteToken: string option)
    (userId: UserId)
    : Async<Result<unit, string>> =
    async {
        match grant with
        | ExistingSession -> return Ok()
        | _ ->
            match! requireNewIdentity lookups userId with
            | Error e -> return Error e
            | Ok() ->
                match! lookups.ClaimIdentity userId with
                | Error e -> return Error e
                | Ok() ->
                    let! spent = async {
                        match grant, inviteToken with
                        | Bootstrap, _ -> return! lookups.ConsumeBootstrap userId
                        | PendingInvite, Some token -> return! lookups.RedeemInvite token userId
                        | PendingInvite, None ->
                            return Error "This registration carries no invitation; open the invitation link again."
                        | _ -> return Ok()
                    }

                    match spent with
                    | Ok() -> return Ok()
                    | Error e ->
                        do! lookups.ReleaseIdentity userId
                        return Error e
    }