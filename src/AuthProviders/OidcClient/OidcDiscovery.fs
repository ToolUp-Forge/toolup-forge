// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcDiscovery

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.AuthProviders.Oidc.OidcTypes

// ─── OIDC discovery ──────────────────────────────────────────────────
//
// Every OIDC-compliant issuer publishes a JSON metadata document at
// `{issuer}/.well-known/openid-configuration` that names the
// authorization and token endpoints (among other things). We fetch it
// once per issuer URL and cache it for the lifetime of the page.
//
// Caching is in-memory only — we deliberately avoid localStorage so a
// deployment that rotates its issuer configuration isn't stuck with a
// stale cache after the page reloads.
//
// Returned fields: we only extract the handful we use. Rather than
// declaring a strongly-typed record (which forces either snake_case
// field names or `[<CompiledName>]` attributes everywhere), we keep
// the raw `obj` and pull fields via `?` interop. This keeps the file
// small and avoids JSON deserialisation dependencies.

/// Discovery document fields we consume.
type DiscoveryDoc = {
    AuthorizationEndpoint: string
    TokenEndpoint: string
    EndSessionEndpoint: string option
    UserInfoEndpoint: string option
    /// JWKS endpoint URL. Required for client-side id_token validation
    /// (Phase 3b.A); `None` when the issuer's discovery doc omits
    /// `jwks_uri` (spec violation — every OIDC issuer must publish one,
    /// but treat absence as a recoverable condition rather than a fatal
    /// discovery error so flows that don't validate the id_token client-
    /// side still work).
    JwksUri: string option
}

/// In-memory cache keyed by normalised issuer URL.
let mutable private cache: Map<string, DiscoveryDoc> = Map.empty

let private normalise (issuer: string) : string = issuer.TrimEnd('/')

[<Emit("fetch($0)")>]
let private fetchUrl (url: string) : JS.Promise<obj> = jsNative

let private getStringField (o: obj) (field: string) : string option =
    let value = o?(field)

    if isNullOrUndefined value then
        None
    else
        Some(unbox<string> value)

/// Fetch the discovery document for `issuer`, caching the result.
/// Returns `Error DiscoveryFailed` on network error, non-2xx status,
/// or missing required fields.
let fetchDiscovery (issuer: string) : Async<Result<DiscoveryDoc, AuthError>> = async {
    let issuer = normalise issuer

    match Map.tryFind issuer cache with
    | Some doc -> return Ok doc
    | None ->
        try
            let url = issuer + "/.well-known/openid-configuration"
            let! response = fetchUrl url |> Async.AwaitPromise
            let ok = unbox<bool> response?ok

            if not ok then
                let status = unbox<int> response?status
                return Error(DiscoveryFailed(sprintf "HTTP %d from %s" status url))
            else
                let! json = response?json () |> Async.AwaitPromise

                match getStringField json "authorization_endpoint", getStringField json "token_endpoint" with
                | Some authEp, Some tokenEp ->
                    let doc = {
                        AuthorizationEndpoint = authEp
                        TokenEndpoint = tokenEp
                        EndSessionEndpoint = getStringField json "end_session_endpoint"
                        UserInfoEndpoint = getStringField json "userinfo_endpoint"
                        JwksUri = getStringField json "jwks_uri"
                    }

                    cache <- Map.add issuer doc cache
                    return Ok doc
                | _ ->
                    return
                        Error(DiscoveryFailed "missing authorization_endpoint or token_endpoint in discovery document")
        with ex ->
            return Error(DiscoveryFailed ex.Message)
}

// ─── JWKS fetch + sessionStorage cache (Phase 3b.A) ──────────────────
//
// Client-side id_token validation needs the issuer's JWKS to verify the
// signature via WebCrypto. We cache the parsed key set in
// `sessionStorage` for 10 minutes — short enough to follow a key
// rotation, long enough that a multi-page session doesn't refetch on
// every navigation. Mirrors the server-side
// `OidcAuthProvider.Jwks.fs` cache (10-min TTL there too).

/// A JWKS entry the client-side validation pipeline cares about.
/// `RawJwk` is the raw JSON object — handed verbatim to WebCrypto's
/// `importKey` (which accepts JWK format directly), so we don't have
/// to do field-by-field projection client-side. `Kid` is lifted out for
/// the kid-match step before importKey is even called.
type Jwk = { Kid: string; RawJwk: obj }

[<Literal>]
let private jwksCacheKeyPrefix = "toolup-oidc-jwks:"

[<Literal>]
let private jwksTtlMs = 600_000.0 // 10 minutes — mirrors server-side

[<Emit("Date.now()")>]
let private nowMs () : float = jsNative

[<Emit("JSON.stringify($0)")>]
let private jsonStringify (o: obj) : string = jsNative

[<Emit("JSON.parse($0)")>]
let private jsonParse (s: string) : obj = jsNative

/// Read a fresh-enough JWKS map from `sessionStorage`, if one is
/// cached. Returns `None` on cache miss, TTL expiry, or any
/// parse error (a corrupt cache entry is treated as a miss; the
/// caller refetches).
let private readCachedJwks (jwksUrl: string) : Map<string, Jwk> option =
    try
        let key = jwksCacheKeyPrefix + jwksUrl

        match Browser.Dom.window.sessionStorage.getItem key with
        | null
        | "" -> None
        | raw ->
            let parsed = jsonParse raw
            let fetchedAt = unbox<float> parsed?fetchedAt

            if nowMs () - fetchedAt > jwksTtlMs then
                None
            else
                let keysObj = parsed?keys
                let keysList = unbox<obj[]> keysObj

                let entries =
                    keysList
                    |> Array.choose (fun jwk ->
                        let kidValue = jwk?kid

                        if isNullOrUndefined kidValue then
                            None
                        else
                            Some(
                                unbox<string> kidValue,
                                {
                                    Kid = unbox<string> kidValue
                                    RawJwk = jwk
                                }
                            ))

                Some(Map.ofArray entries)
    with _ ->
        None

let private writeCachedJwks (jwksUrl: string) (rawKeys: obj[]) : unit =
    try
        let key = jwksCacheKeyPrefix + jwksUrl

        let entry = createObj [ "fetchedAt" ==> nowMs (); "keys" ==> rawKeys ]

        Browser.Dom.window.sessionStorage.setItem (key, jsonStringify entry)
    with _ ->
        // sessionStorage may be full or disabled — failure to cache is
        // not a failure to validate. Caller has the keys in hand.
        ()

/// Fetch the JWKS from `jwksUrl`, returning a map of `kid → Jwk`.
/// `forceRefresh = true` bypasses the sessionStorage cache (used on
/// kid-miss to pick up rotated keys, mirrors server-side pattern).
let fetchJwks (jwksUrl: string) (forceRefresh: bool) : Async<Result<Map<string, Jwk>, AuthError>> = async {
    let cached = if forceRefresh then None else readCachedJwks jwksUrl

    match cached with
    | Some keys -> return Ok keys
    | None ->
        try
            let! response = fetchUrl jwksUrl |> Async.AwaitPromise
            let ok = unbox<bool> response?ok

            if not ok then
                let status = unbox<int> response?status
                return Error(DiscoveryFailed(sprintf "HTTP %d from %s" status jwksUrl))
            else
                let! json = response?json () |> Async.AwaitPromise
                let keysValue = json?keys

                if isNullOrUndefined keysValue then
                    return Error(DiscoveryFailed(sprintf "JWKS at %s has no 'keys' array" jwksUrl))
                else
                    let keysArray = unbox<obj[]> keysValue

                    let entries =
                        keysArray
                        |> Array.choose (fun jwk ->
                            let kidValue = jwk?kid

                            if isNullOrUndefined kidValue then
                                None
                            else
                                Some(
                                    unbox<string> kidValue,
                                    {
                                        Kid = unbox<string> kidValue
                                        RawJwk = jwk
                                    }
                                ))

                    writeCachedJwks jwksUrl keysArray
                    return Ok(Map.ofArray entries)
        with ex ->
            return Error(DiscoveryFailed ex.Message)
}