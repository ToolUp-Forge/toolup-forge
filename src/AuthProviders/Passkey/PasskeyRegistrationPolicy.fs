// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyRegistrationPolicy

open System.Text
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.AuthProviders.Passkey.PasskeyTypes

// ─── 443.B — registration policy (secure by default) ─────────────────
//
// Enrolling a passkey creates a credential that can authenticate as an
// identity, so registration is INVITE-GATED by default (Wave 19 posture
// — secure by default). A caller may register only when one of:
//   • an existing authenticated session drives the identity, OR
//   • a valid one-time bootstrap token is presented (fresh deployment's
//     FIRST credential, before any session or invite exists), OR
//   • a pending team invite exists for the supplied email
//     (`IPendingInviteStore` — Phase 3d.A / 5h substrate).
// `AllowOpenRegistration = true` is an EXPLICIT opt-in that bypasses the
// gate entirely (surfaced by preflight so an operator sees it).

/// The identity a registration ceremony enrols a credential for.
type RegistrationIdentity = {
    UserId: string
    DisplayName: string
    Email: string option
}

/// Why a registration was permitted — surfaced in the audit trail.
type RegistrationGrant =
    | ExistingSession
    | Bootstrap
    | PendingInvite
    | OpenRegistration

/// The pure gate decision. Kept effect-free so the policy is unit-
/// testable without stores: given the three resolved booleans, decide
/// whether (and on what grounds) registration proceeds.
let decide
    (config: PasskeyConfig)
    (isAuthenticated: bool)
    (bootstrapMatches: bool)
    (hasPendingInvite: bool)
    : Result<RegistrationGrant, string> =
    if config.AllowOpenRegistration then
        Ok OpenRegistration
    elif isAuthenticated then
        Ok ExistingSession
    elif bootstrapMatches then
        Ok Bootstrap
    elif hasPendingInvite then
        Ok PendingInvite
    else
        Error
            "Passkey registration is invite-gated: sign in first, present a valid pending-invite email, or supply the one-time bootstrap token. Set AllowOpenRegistration = true to permit open enrolment."

/// Constant-time comparison of the presented bootstrap token against the
/// configured one. Both must be `Some` and non-empty; a `None` on either
/// side (bootstrap disabled, or no token presented) is `false`.
let bootstrapMatches (config: PasskeyConfig) (presented: string option) : bool =
    match config.BootstrapToken, presented with
    | Some expected, Some provided when expected <> "" && provided <> "" ->
        JwtCrypto.fixedTimeEquals (Encoding.UTF8.GetBytes expected) (Encoding.UTF8.GetBytes provided)
    | _ -> false

/// Peek (non-consuming) whether a pending invite exists for `email`. The
/// actual consume + team-membership add happens post-login through the
/// SDK's existing `tryConsumePendingForUser` path once the minted
/// session resolves — this gate only checks existence.
let private hasPendingInviteFor (pendingStore: IPendingInviteStore option) (email: string option) : Async<bool> = async {
    match pendingStore, email with
    | Some store, Some e when e <> "" ->
        match! store.ListAll() with
        | Ok entries ->
            return
                entries
                |> List.exists (fun (k, _) -> System.String.Equals(k, e, System.StringComparison.OrdinalIgnoreCase))
        | Error _ -> return false
    | _ -> return false
}

/// Resolve the registration identity, applying the gate. `currentUser`
/// is the request's resolved principal (anonymous when unauthenticated).
/// On the non-session grants the identity is taken from the request
/// (username required, sanitised the same way `StaticJwtAuthProvider`
/// sanitises `sub`).
let resolveIdentity
    (config: PasskeyConfig)
    (currentUser: AuthenticatedUser)
    (pendingStore: IPendingInviteStore option)
    (req: RegisterBeginRequest)
    : Async<Result<RegistrationGrant * RegistrationIdentity, string>> =
    async {
        let isAuthenticated = not (AuthenticatedUser.isAnonymous currentUser)
        let bootstrapOk = bootstrapMatches config req.BootstrapToken
        let! pendingOk = hasPendingInviteFor pendingStore req.Email

        match decide config isAuthenticated bootstrapOk pendingOk with
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
                                currentUser.UserId
                        Email = currentUser.Email |> Option.orElse req.Email
                    }
                )
        | Ok grant ->
            // Bootstrap / PendingInvite / OpenRegistration all take the
            // identity from the request. A username is required and is
            // sanitised into a safe scope id (path-traversal / control-char
            // defence), exactly as StaticJwt sanitises `sub`.
            match req.Username with
            | None
            | Some "" -> return Error "A username is required to enrol a passkey on this registration path."
            | Some raw ->
                match IdentitySanitiser.sanitiseScopeId raw with
                | Error reason -> return Error $"Invalid username: {reason}"
                | Ok userId ->
                    return
                        Ok(
                            grant,
                            {
                                UserId = userId
                                DisplayName = req.DisplayName |> Option.defaultValue userId
                                Email = req.Email
                            }
                        )
    }