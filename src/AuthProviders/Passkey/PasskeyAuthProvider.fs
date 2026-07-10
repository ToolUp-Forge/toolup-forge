// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.PasskeyAuthProvider

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyStores

// ─── Request-time session validator ──────────────────────────────────
//
// The session a passkey assertion mints is the SAME short-lived HS256
// platform JWT `StaticJwtAuthProvider` established — so the auth
// provider that validates it on every subsequent request IS a
// `StaticJwtAuthProvider`, bound to the same auto-generated signing
// secret the ceremony mints with. No parallel session model, no second
// token format, no bespoke validation path: the hardened HS256
// validation (mandatory `exp`, `alg: HS256` gate, sanitised `sub`) is
// inherited verbatim.
//
// The signing secret lives in `ISecretStore` under `_platform`. It is
// resolved lazily from the request's DI container on first validation
// and cached — so the provider needs nothing at compose time (the
// resolved `ISecretStore` isn't available until the container is built),
// and `PasskeyCompose` can wire it with a plain `ServerApp.withAuth`.

/// `IAuthProvider` that validates the platform session JWTs the passkey
/// ceremony mints. Cryptographically verified (HS256 signature over the
/// shared secret) — reports `IsCryptographicallyVerified = true`, so it
/// satisfies the startup auth-mode gate.
type PasskeyAuthProvider(config: PasskeyConfig) =
    let gate = obj ()
    let mutable inner: IAuthProvider option = None

    let buildAsync (ctx: HttpContext) : Async<IAuthProvider> = async {
        match inner with
        | Some p -> return p
        | None ->
            let secrets = ctx.RequestServices.GetService(typeof<ISecretStore>) :?> ISecretStore
            let! secret = resolveSigningSecret secrets

            let provider =
                StaticJwtAuthProvider.StaticJwtAuthProvider(
                    {
                        Secret = secret
                        Issuer = config.Issuer
                        Audience = config.Audience
                    }
                )
                :> IAuthProvider

            lock gate (fun () ->
                if inner.IsNone then
                    inner <- Some provider)

            return inner.Value
    }

    interface IAuthProvider with
        member _.GetUser ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! provider = buildAsync httpCtx
            return! provider.GetUser ctx
        }

        member _.ValidateRequest ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! provider = buildAsync httpCtx
            return! provider.ValidateRequest ctx
        }

        // The minted session JWT is HS256-signed over the shared
        // secret — identity is cryptographically proven, not
        // header-trusted.
        member _.IsCryptographicallyVerified = true