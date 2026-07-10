// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyStores

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders.Passkey.PasskeyTypes

// ─── Challenge store — short-TTL, single-use ─────────────────────────
//
// A begin-ceremony issues a random challenge; the matching complete
// call must present it before it expires and it is consumed on first
// take (single-use — replay defence). The pending record also carries
// the exact Fido2 options object the verifier needs as `OriginalOptions`
// plus the resolved identity, so the complete leg is stateless w.r.t.
// the browser payload.
//
// **Distributed-readiness: dev/single-instance by default.** The
// default `InMemoryPasskeyChallengeStore` holds challenges in-process,
// so a multi-instance deployment behind a round-robin balancer must run
// sticky sessions for the ~2-minute ceremony window, or supply a shared
// `IPasskeyChallengeStore`. Documented in the companion README; the
// interface exists precisely so a Redis-backed store can drop in.

/// One in-flight ceremony challenge. `Options` is the boxed Fido2
/// options object (`CredentialCreateOptions` for registration,
/// `AssertionOptions` for assertion) fed back to the verifier as
/// `OriginalOptions`.
type PendingChallenge = {
    Kind: ChallengeKind
    Options: obj
    UserId: string option
    UserHandle: string option
    DisplayName: string option
    Email: string option
    Username: string option
    /// The registration grant ground (`ExistingSession` / `Bootstrap`
    /// / `PendingInvite` / `OpenRegistration`) carried to the complete
    /// leg for the audit trail. `None` for assertion challenges.
    Grant: string option
    ExpiresAt: DateTime
}

/// Pluggable challenge store. Identity by value (`challengeId : string`),
/// async-free by design (the operation is a memory / cache poke, not an
/// I/O boundary — a distributed impl wraps its own client). Single-use
/// take.
type IPasskeyChallengeStore =
    /// Store a pending challenge under `challengeId`. Overwrites silently
    /// (challenge ids are random and unique in practice).
    abstract Put: challengeId: string * challenge: PendingChallenge -> unit
    /// Atomically remove and return the challenge if present AND not
    /// expired. Returns `None` for a missing, already-consumed, or
    /// expired challenge — all indistinguishable to the caller.
    abstract TryTake: challengeId: string -> PendingChallenge option

/// In-memory single-instance default. Lazy expiry on take plus an
/// opportunistic sweep when the map grows, so an abandoned-ceremony
/// backlog can't leak unboundedly.
type InMemoryPasskeyChallengeStore() =
    let entries = ConcurrentDictionary<string, PendingChallenge>()

    let sweepExpired () =
        let now = DateTime.UtcNow

        for kv in entries do
            if kv.Value.ExpiresAt < now then
                entries.TryRemove(kv.Key) |> ignore

    interface IPasskeyChallengeStore with
        member _.Put(challengeId, challenge) =
            if entries.Count > 1024 then
                sweepExpired ()

            entries[challengeId] <- challenge

        member _.TryTake(challengeId) =
            match entries.TryRemove challengeId with
            | true, c when c.ExpiresAt >= DateTime.UtcNow -> Some c
            | _ -> None

// ─── Credential store — blob-backed, `_platform` scope ───────────────
//
// One blob per credential at
// `_platform/auth/passkeys/{credentialIdB64Url}.json`. The credential
// id (base64url) is a blob-safe name segment (URL-safe alphabet, no
// path separators). Per-user lookups list the prefix and filter by the
// record's `UserId` — fine at the credential counts a passwordless
// deployment carries; a large fleet would key a secondary index.

let private jsonOptions: JsonSerializerOptions =
    // The F#-aware STJ converter set (Option / list / record) — the SDK
    // wire-format serializer, shared with the audit-sink companions. A
    // plain `JsonSerializerOptions()` would mangle `Email: string option`
    // and `Transports: string list`.
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

/// SHA-256 of the platform UserId, base64url-encoded — the stable,
/// non-PII WebAuthn user handle. Deterministic so every credential a
/// user enrols shares one handle and the assertion path can tie a
/// resolved credential back to its owner.
let userHandleFor (userId: string) : string =
    use sha = SHA256.Create()
    Base64Url.encode (sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes userId))

/// Blob-backed passkey credential persistence.
type PasskeyCredentialStore(blobs: IBlobStorage) =
    let container = PasskeyConfig.PlatformContainer

    let blobName (credentialIdB64Url: string) =
        PasskeyConfig.CredentialBlobPrefix + credentialIdB64Url + ".json"

    /// Persist (or overwrite) a credential record.
    member _.Save(record: PasskeyCredentialRecord) : Async<Result<unit, string>> = async {
        let json = JsonSerializer.Serialize(record, jsonOptions)
        let bytes = System.Text.Encoding.UTF8.GetBytes json
        let! r = blobs.Upload(container, blobName record.CredentialId, bytes)
        return r |> Result.map ignore
    }

    /// Fetch a single credential by its base64url id.
    member _.TryGet(credentialIdB64Url: string) : Async<PasskeyCredentialRecord option> = async {
        match! blobs.Download(container, blobName credentialIdB64Url) with
        | Ok bytes ->
            try
                let json = System.Text.Encoding.UTF8.GetString bytes
                return Some(JsonSerializer.Deserialize<PasskeyCredentialRecord>(json, jsonOptions))
            with _ ->
                return None
        | Error _ -> return None
    }

    /// True when no credential with this id is stored yet — the
    /// `IsCredentialIdUniqueToUser` guard.
    member _.Exists(credentialIdB64Url: string) : Async<bool> =
        blobs.Exists(container, blobName credentialIdB64Url)

    /// Every stored credential (used for per-user filtering + assertion
    /// allow-lists). Small-scale linear scan of the prefix.
    member _.ListAll() : Async<PasskeyCredentialRecord list> = async {
        let! names = blobs.List(container, PasskeyConfig.CredentialBlobPrefix)

        let! records =
            names
            |> List.map (fun name -> async {
                match! blobs.Download(container, name) with
                | Ok bytes ->
                    try
                        let json = System.Text.Encoding.UTF8.GetString bytes
                        return Some(JsonSerializer.Deserialize<PasskeyCredentialRecord>(json, jsonOptions))
                    with _ ->
                        return None
                | Error _ -> return None
            })
            |> Async.Parallel

        return records |> Array.choose id |> Array.toList
    }

    /// Credentials enrolled by one platform identity.
    member this.ListByUserId(userId: string) : Async<PasskeyCredentialRecord list> = async {
        let! all = this.ListAll()
        return all |> List.filter (fun r -> r.UserId = userId)
    }

    /// Delete a credential (idempotent).
    member _.Remove(credentialIdB64Url: string) : Async<Result<unit, string>> =
        blobs.Delete(container, blobName credentialIdB64Url)

// ─── Session signing secret ──────────────────────────────────────────

/// Resolve the HS256 session signing secret from `ISecretStore` under
/// `_platform`, auto-generating a 32-byte random secret on first use
/// (mirrors `ShareTokenStore`'s key bootstrap). Re-reads after a
/// generate so concurrent first-callers converge on whichever write
/// landed — a token minted under a superseded secret would otherwise
/// fail validation.
let resolveSigningSecret (secrets: ISecretStore) : Async<string> = async {
    match! secrets.GetSecret(PasskeyConfig.PlatformContainer, PasskeyConfig.SigningKeySecretName) with
    | Some s when s <> "" -> return s
    | _ ->
        let generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes 32)

        let! _ = secrets.SetSecret(PasskeyConfig.PlatformContainer, PasskeyConfig.SigningKeySecretName, generated)

        match! secrets.GetSecret(PasskeyConfig.PlatformContainer, PasskeyConfig.SigningKeySecretName) with
        | Some s when s <> "" -> return s
        | _ -> return generated
}