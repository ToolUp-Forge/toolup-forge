// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeySessionToken

open System
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform
open ToolUp.AuthProviders.Passkey.PasskeyTypes

// ─── Session issuance ────────────────────────────────────────────────
//
// After a successful assertion the companion mints a short-lived HS256
// JWT carrying the resolved platform identity. This is deliberately the
// SAME shape `StaticJwtAuthProvider` validates — sub / exp / nbf / name
// / email (+ optional iss / aud) signed HS256 over `header.payload` —
// so there is no parallel session model: the paired
// `PasskeyAuthProvider` is literally a `StaticJwtAuthProvider` bound to
// the same signing secret (see `PasskeyAuthProvider.fs`). Assembly
// mirrors `InterPlatform`'s `PeerJwt.encode`; the primitives
// (`Base64Url` / `JwtCrypto.computeHmac`) are the shared SDK crypto.

/// The identity a minted session token authenticates as.
type SessionIdentity = {
    UserId: string
    DisplayName: string
    Email: string option
}

/// Mint a short-lived HS256 platform session JWT for `identity`, signed
/// with `secret`, honouring the config's TTL and optional issuer /
/// audience binding. `now` is unix seconds (injected for testability).
let mintAt (secret: string) (config: PasskeyConfig) (now: int64) (identity: SessionIdentity) : string =
    let header = JsonObject()
    header["alg"] <- JsonValue.Create "HS256"
    header["typ"] <- JsonValue.Create "JWT"

    let payload = JsonObject()
    payload["sub"] <- JsonValue.Create identity.UserId
    payload["name"] <- JsonValue.Create identity.DisplayName

    identity.Email |> Option.iter (fun e -> payload["email"] <- JsonValue.Create e)
    config.Issuer |> Option.iter (fun i -> payload["iss"] <- JsonValue.Create i)
    config.Audience |> Option.iter (fun a -> payload["aud"] <- JsonValue.Create a)

    payload["iat"] <- JsonValue.Create now
    payload["nbf"] <- JsonValue.Create now
    payload["exp"] <- JsonValue.Create(now + int64 config.SessionTokenTtlSeconds)

    let encodedHeader = Base64Url.encode (Encoding.UTF8.GetBytes(header.ToJsonString()))

    let encodedPayload =
        Base64Url.encode (Encoding.UTF8.GetBytes(payload.ToJsonString()))

    let signingInput = $"{encodedHeader}.{encodedPayload}"

    let signature =
        Base64Url.encode (JwtCrypto.computeHmac (Encoding.UTF8.GetBytes secret) (Encoding.UTF8.GetBytes signingInput))

    $"{signingInput}.{signature}"

/// Production entry — mints against the current wall clock.
let mint (secret: string) (config: PasskeyConfig) (identity: SessionIdentity) : string =
    mintAt secret config (DateTimeOffset.UtcNow.ToUnixTimeSeconds()) identity