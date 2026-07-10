// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyCeremony

open System
open System.Collections.Generic
open System.Threading
open System.Text.Json
open Fido2NetLib
open Fido2NetLib.Objects
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyStores
open ToolUp.AuthProviders.Passkey.PasskeyRegistrationPolicy

// ─── 443.A — WebAuthn ceremony orchestration over Fido2NetLib ────────
//
// Four operations: registration begin (attestation options) / complete
// (verify attestation, persist credential) and assertion begin (options)
// / complete (verify assertion, clone-detect, resolve identity). The
// verifier is the `IFido2` interface — injected so tests can drive the
// orchestration (counter update, persistence, identity resolution)
// through a stub without hand-authoring real attestation crypto, while
// production supplies the concrete `Fido2` over the deployment's RP
// configuration. Fido2NetLib itself is exercised against its own test
// vectors upstream (GP 1 — the vendor crypto stays behind this seam).

/// Build the concrete Fido2 verifier from the companion config. Origins
/// are the exact browser-origin allow-list; `ServerDomain` is the RP id.
let buildFido2 (config: PasskeyConfig) : IFido2 =
    let fidoConfig =
        Fido2Configuration(
            ServerDomain = config.RelyingPartyId,
            ServerName = config.RelyingPartyName,
            Origins = HashSet<string>(config.Origins)
        )

    Fido2(fidoConfig) :> IFido2

/// Clone detection. A stored counter that has already advanced past
/// (or equals) the counter presented on assertion signals a duplicated
/// authenticator — reject. A stored counter of `0` means the
/// authenticator does not implement signature counters (spec-legal for
/// many platform passkeys), so no regression can be asserted.
let isCounterRegression (stored: uint32) (incoming: uint32) : bool = stored <> 0u && incoming <= stored

let private descriptorFor (record: PasskeyCredentialRecord) : PublicKeyCredentialDescriptor =
    PublicKeyCredentialDescriptor(Base64Url.decode record.CredentialId)

// ─── Registration ────────────────────────────────────────────────────

/// Begin registration — build attestation `CredentialCreateOptions` for
/// `identity`, excluding any credentials the user already enrolled, and
/// stash the pending challenge. Returns the options JSON for the browser
/// plus the correlating challenge id.
let beginRegistration
    (fido: IFido2)
    (creds: PasskeyCredentialStore)
    (challenges: IPasskeyChallengeStore)
    (config: PasskeyConfig)
    (grant: RegistrationGrant)
    (identity: RegistrationIdentity)
    : Async<Result<CeremonyOptionsResponse, string>> =
    async {
        try
            let handle = userHandleFor identity.UserId

            let user =
                Fido2User(Id = Base64Url.decode handle, Name = identity.UserId, DisplayName = identity.DisplayName)

            let! existing = creds.ListByUserId identity.UserId
            let excludeCredentials = ResizeArray(existing |> List.map descriptorFor)

            let selection =
                AuthenticatorSelection(
                    ResidentKey = ResidentKeyRequirement.Preferred,
                    UserVerification = UserVerificationRequirement.Preferred
                )

            let requestParams =
                RequestNewCredentialParams(
                    User = user,
                    ExcludeCredentials = excludeCredentials,
                    AuthenticatorSelection = selection,
                    AttestationPreference = AttestationConveyancePreference.None
                )

            let options = fido.RequestNewCredential requestParams
            let challengeId = Guid.NewGuid().ToString "N"

            let pending: PendingChallenge = {
                Kind = Registration
                Options = box options
                UserId = Some identity.UserId
                UserHandle = Some handle
                DisplayName = Some identity.DisplayName
                Email = identity.Email
                Username = Some identity.UserId
                Grant = Some(string grant)
                ExpiresAt = DateTime.UtcNow.AddSeconds(float config.ChallengeTtlSeconds)
            }

            challenges.Put(challengeId, pending)

            return
                Ok {
                    ChallengeId = challengeId
                    OptionsJson = options.ToJson()
                }
        with ex ->
            return Error $"Failed to begin passkey registration: {ex.Message}"
    }

/// Complete registration — verify the attestation against the stashed
/// options and persist the new credential. Rejects a duplicate
/// credential id (`IsCredentialIdUniqueToUser`). Returns the persisted
/// record.
let completeRegistration
    (fido: IFido2)
    (creds: PasskeyCredentialStore)
    (pending: PendingChallenge)
    (rawResponseJson: string)
    : Async<Result<PasskeyCredentialRecord, string>> =
    async {
        match pending.Kind, pending.Options with
        | Registration, (:? CredentialCreateOptions as originalOptions) ->
            try
                let attestation =
                    JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(rawResponseJson)

                let uniqueCallback =
                    IsCredentialIdUniqueToUserAsyncDelegate(fun prms (_: CancellationToken) ->
                        async {
                            let! exists = creds.Exists(Base64Url.encode prms.CredentialId)
                            return not exists
                        }
                        |> Async.StartAsTask)

                let makeParams =
                    MakeNewCredentialParams(
                        AttestationResponse = attestation,
                        OriginalOptions = originalOptions,
                        IsCredentialIdUniqueToUserCallback = uniqueCallback
                    )

                let! credential =
                    fido.MakeNewCredentialAsync(makeParams, CancellationToken.None)
                    |> Async.AwaitTask

                let transports =
                    match credential.Transports with
                    | null -> []
                    | ts -> ts |> Array.map string |> Array.toList

                let record: PasskeyCredentialRecord = {
                    CredentialId = Base64Url.encode credential.Id
                    PublicKey = Base64Url.encode credential.PublicKey
                    SignCount = credential.SignCount
                    UserHandle = pending.UserHandle |> Option.defaultValue (Base64Url.encode credential.User.Id)
                    UserId = pending.UserId |> Option.defaultValue ""
                    DisplayName = pending.DisplayName |> Option.defaultValue ""
                    Email = pending.Email
                    Transports = transports
                    CreatedAt = DateTime.UtcNow
                }

                match! creds.Save record with
                | Ok() -> return Ok record
                | Error e -> return Error $"Passkey registered but failed to persist: {e}"
            with ex ->
                return Error $"Passkey attestation verification failed: {ex.Message}"
        | _ -> return Error "Challenge is not a pending registration."
    }

// ─── Assertion ───────────────────────────────────────────────────────

/// Begin assertion — build `AssertionOptions`. When `username` is
/// supplied, `allowCredentials` is scoped to that user's enrolled
/// credentials; omit it for a discoverable-credential (usernameless)
/// flow.
let beginAssertion
    (fido: IFido2)
    (creds: PasskeyCredentialStore)
    (challenges: IPasskeyChallengeStore)
    (config: PasskeyConfig)
    (username: string option)
    : Async<Result<CeremonyOptionsResponse, string>> =
    async {
        try
            let! allowed = async {
                match username with
                | Some u when u <> "" ->
                    match IdentitySanitiser.sanitiseScopeId u with
                    | Ok userId ->
                        let! records = creds.ListByUserId userId
                        return records |> List.map descriptorFor
                    | Error _ -> return []
                | _ -> return []
            }

            let assertionParams =
                GetAssertionOptionsParams(
                    AllowedCredentials = ResizeArray(allowed),
                    UserVerification = System.Nullable(UserVerificationRequirement.Preferred)
                )

            let options = fido.GetAssertionOptions assertionParams
            let challengeId = Guid.NewGuid().ToString "N"

            let pending: PendingChallenge = {
                Kind = Assertion
                Options = box options
                UserId = Option<string>.None
                UserHandle = Option<string>.None
                DisplayName = Option<string>.None
                Email = Option<string>.None
                Username = username
                Grant = Option<string>.None
                ExpiresAt = DateTime.UtcNow.AddSeconds(float config.ChallengeTtlSeconds)
            }

            challenges.Put(challengeId, pending)

            return
                Ok {
                    ChallengeId = challengeId
                    OptionsJson = options.ToJson()
                }
        with ex ->
            return Error $"Failed to begin passkey assertion: {ex.Message}"
    }

/// Complete assertion — verify against the stashed options and the
/// stored credential, clone-detect via the signature counter, and on
/// success advance the stored counter and return the resolved
/// credential (its `UserId` is the identity the session is minted for).
let completeAssertion
    (fido: IFido2)
    (creds: PasskeyCredentialStore)
    (pending: PendingChallenge)
    (rawResponseJson: string)
    : Async<Result<PasskeyCredentialRecord, string>> =
    async {
        match pending.Kind, pending.Options with
        | Assertion, (:? AssertionOptions as originalOptions) ->
            try
                let assertion =
                    JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(rawResponseJson)

                let credentialId = Base64Url.encode assertion.RawId
                let! stored = creds.TryGet credentialId

                match stored with
                | None -> return Error "Unknown passkey credential."
                | Some record ->
                    let ownerCallback =
                        IsUserHandleOwnerOfCredentialIdAsync(fun prms (_: CancellationToken) ->
                            System.Threading.Tasks.Task.FromResult(
                                Base64Url.encode prms.UserHandle = record.UserHandle
                            ))

                    let makeParams =
                        MakeAssertionParams(
                            AssertionResponse = assertion,
                            OriginalOptions = originalOptions,
                            StoredPublicKey = Base64Url.decode record.PublicKey,
                            StoredSignatureCounter = record.SignCount,
                            IsUserHandleOwnerOfCredentialIdCallback = ownerCallback
                        )

                    let! result = fido.MakeAssertionAsync(makeParams, CancellationToken.None) |> Async.AwaitTask

                    // Defence in depth: Fido2NetLib already rejects a
                    // regressed counter, but we re-check explicitly (clone
                    // detection is 443.A acceptance) and only persist a
                    // strictly-advancing counter.
                    if isCounterRegression record.SignCount result.SignCount then
                        return
                            Error
                                "Passkey signature counter regressed — possible cloned authenticator; assertion rejected."
                    else
                        let updated = {
                            record with
                                SignCount = result.SignCount
                        }

                        match! creds.Save updated with
                        | Ok() -> return Ok updated
                        | Error e -> return Error $"Assertion verified but failed to persist counter: {e}"
            with ex ->
                return Error $"Passkey assertion verification failed: {ex.Message}"
        | _ -> return Error "Challenge is not a pending assertion."
    }