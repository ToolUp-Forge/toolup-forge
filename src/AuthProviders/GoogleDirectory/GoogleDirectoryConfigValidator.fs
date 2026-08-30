// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleDirectoryConfigValidator

open System
open System.Net.Http
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders.GoogleDirectory
open ToolUp.AuthProviders.GoogleDirectoryAuth

// ─── Google Workspace credential + delegation preflight ──────────────
//
// Domain-wide delegation is configured in two places that do not know
// about each other — the Cloud project that mints the service-account
// key, and the Workspace admin console that authorises that key's
// client id for a list of scope strings. Getting one and not the other
// is the overwhelmingly common misconfiguration, and its runtime
// symptom is the worst kind: the typeahead simply returns nothing, and
// an operator concludes the directory is empty.
//
// So the validator does not merely check that fields are populated. It
// performs a real JWT-grant token exchange for each scope the
// deployment has actually asked for, because that exchange is exactly
// where the delegation is enforced: Google refuses an undelegated
// (client id, scope, subject) triple with `unauthorized_client` at the
// token endpoint, before any API call is made. A token that mints is
// proof the grant exists.
//
// Verdicts, and why they are graded this way:
//
//   * Missing `Domain` / `ImpersonatedAdmin`, an absent or unparseable
//     credential, or a directory-scope exchange that fails → `Error`.
//     The companion cannot do its primary job; startup aborts rather
//     than serving an empty typeahead that looks like an empty
//     directory.
//   * A `SenderUserId` is configured but its `gmail.send` exchange
//     fails → `Warning`. Invitation email degrades to the pre-companion
//     behaviour (the invite still lands via the pending-by-email store
//     and the invitee is told out of band), which is a real loss but
//     not one worth refusing to boot over. This mirrors the runtime
//     posture, where a mail failure is swallowed by the invite handler
//     and the directory keeps working.
//
// Both exchanges are network calls, so this is an external-probe-class
// validator: `ServerConfig.SkipPreflight = true` bypasses it, as it
// does every other dependency-reaching probe.

type private Impl(config: GoogleDirectoryConfig, secrets: ISecretStore, http: HttpClient, timeout: TimeSpan) =

    let loadKey () = async {
        let! stored = secrets.GetSecret(config.CredentialScopeId, config.CredentialSecretKey)

        match stored with
        | None ->
            return
                Result.Error(
                    sprintf
                        "no service-account credential at secret '%s/%s'. Store the service-account JSON key file's contents there before composing the companion."
                        config.CredentialScopeId
                        config.CredentialSecretKey
                )
        | Some json ->
            match parseServiceAccountJson json with
            | Result.Ok key ->
                return
                    Result.Ok {
                        key with
                            TokenUri =
                                if key.TokenUri = DefaultTokenEndpoint then
                                    config.TokenEndpoint
                                else
                                    key.TokenUri
                    }
            | Result.Error e -> return Result.Error e
    }

    interface IConfigValidator with
        member _.Name =
            sprintf
                "google-directory (%s)"
                (if String.IsNullOrWhiteSpace config.Domain then
                     "<no domain>"
                 else
                     config.Domain)

        member _.Timeout = timeout

        member _.Validate() = async {
            try
                if String.IsNullOrWhiteSpace config.Domain then
                    return
                        Error
                            "GoogleDirectoryConfig.Domain is required — set it to the Workspace primary domain the directory query is scoped to (e.g. \"example.com\")."
                elif String.IsNullOrWhiteSpace config.ImpersonatedAdmin then
                    return
                        Error
                            "GoogleDirectoryConfig.ImpersonatedAdmin is required — the Directory API refuses a non-admin subject, so domain-wide delegation must impersonate a Workspace admin."
                else
                    match! loadKey () with
                    | Result.Error e -> return Error e
                    | Result.Ok key ->
                        match! exchangeToken http key config.ImpersonatedAdmin [ DirectoryReadonlyScope ] with
                        | Result.Error e ->
                            return
                                Error(
                                    sprintf
                                        "directory scope not usable: %s. Authorise client id for '%s' under Workspace admin → Security → Access and data control → API controls → Domain-wide delegation, and confirm '%s' is an admin in '%s'."
                                        e
                                        DirectoryReadonlyScope
                                        config.ImpersonatedAdmin
                                        config.Domain
                                )
                        | Result.Ok _ ->
                            match config.SenderUserId with
                            | None -> return Ok
                            | Some sender ->
                                match! exchangeToken http key sender [ GmailSendScope ] with
                                | Result.Ok _ -> return Ok
                                | Result.Error e ->
                                    return
                                        Warning(
                                            sprintf
                                                "invitation email disabled: %s. Directory search is unaffected; authorise the service account for '%s' and confirm the mailbox '%s' exists to enable it."
                                                e
                                                GmailSendScope
                                                sender
                                        )
            with ex ->
                return Error(sprintf "google-directory preflight failed: %s" ex.Message)
        }

/// Construct the credential + delegation preflight for a Google
/// Workspace directory companion. Pass the same `config` and
/// `ISecretStore` the `IUserDirectory` was composed with, so the
/// validator proves the credential the deployment will actually use.
let create (secrets: ISecretStore) (config: GoogleDirectoryConfig) : IConfigValidator =
    Impl(config, secrets, new HttpClient(Timeout = TimeSpan.FromSeconds 15.0), IConfigValidator.defaultTimeout)
    :> IConfigValidator

/// As `create`, but over a caller-supplied `HttpClient` and validator
/// timeout. Present for tests driving the validator against a stub
/// transport, and for a deployment whose egress needs its own handler
/// chain. The caller owns the client's lifetime.
let createWithClient
    (http: HttpClient)
    (timeout: TimeSpan)
    (secrets: ISecretStore)
    (config: GoogleDirectoryConfig)
    : IConfigValidator =
    Impl(config, secrets, http, timeout) :> IConfigValidator