// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.OAuth1aSigner

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open ToolUp.Platform

// ─── OAuth 1.0a request signing (RFC 5849 §3.4, Phase 10g) ──────────────
//
// HMAC-SHA1 request signing over the canonical request string. The pure
// functions (`percentEncode` / `signatureBaseString` / `computeSignature`
// / `authorizationHeader`) are provider-agnostic and validated directly
// against the RFC-5849 / widely-published HMAC-SHA1 reference vector; the
// `signRequest` wrapper attaches the resulting `Authorization: OAuth …`
// header to an outgoing `HttpRequestMessage`.

/// Percent-encode per RFC 3986 / RFC 5849 §3.6 — unreserved characters
/// (`ALPHA` / `DIGIT` / `-` / `.` / `_` / `~`) pass through; every other
/// byte of the UTF-8 encoding becomes `%XX` with upper-case hex. Space is
/// `%20`, never `+`.
let percentEncode (value: string) : string =
    let sb = StringBuilder()

    for b in Encoding.UTF8.GetBytes value do
        let c = char b

        if
            (c >= 'A' && c <= 'Z')
            || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9')
            || c = '-'
            || c = '.'
            || c = '_'
            || c = '~'
        then
            sb.Append c |> ignore
        else
            sb.Append('%').Append(b.ToString("X2")) |> ignore

    sb.ToString()

/// The signature base string (RFC 5849 §3.4.1): uppercase HTTP method,
/// the percent-encoded base URL (scheme + authority + path, no query/
/// fragment), and the percent-encoded normalised parameter string — each
/// joined by `&`. Parameters are percent-encoded, then sorted by encoded
/// key (ties broken by encoded value) using byte-ordinal comparison, then
/// joined as `key=value` with `&`.
let signatureBaseString (httpMethod: string) (baseUrl: string) (parameters: (string * string) list) : string =
    let normalised =
        parameters
        |> List.map (fun (k, v) -> percentEncode k, percentEncode v)
        |> List.sortWith (fun (k1, v1) (k2, v2) ->
            let byKey = String.CompareOrdinal(k1, k2)
            if byKey <> 0 then byKey else String.CompareOrdinal(v1, v2))
        |> List.map (fun (k, v) -> k + "=" + v)
        |> String.concat "&"

    httpMethod.ToUpperInvariant()
    + "&"
    + percentEncode baseUrl
    + "&"
    + percentEncode normalised

/// The signing key (RFC 5849 §3.4.2): the percent-encoded consumer secret
/// and token secret joined by `&`. The token secret is empty for the
/// leg-1 request-token fetch (`consumerSecret&`).
let signingKey (consumerSecret: string) (tokenSecret: string) : string =
    percentEncode consumerSecret + "&" + percentEncode tokenSecret

/// HMAC-SHA1 signature (base64) of `baseString` under `key`.
let private hmacSha1Base64 (key: string) (message: string) : string =
    use hmac = new HMACSHA1(Encoding.UTF8.GetBytes key)
    hmac.ComputeHash(Encoding.UTF8.GetBytes message) |> Convert.ToBase64String

/// Compute the `oauth_signature` (base64 HMAC-SHA1) for a request. The
/// `parameters` list MUST include every `oauth_*` protocol parameter
/// (except `oauth_signature` itself) plus every request query/body
/// parameter — exactly the set that goes into the base string.
let computeSignature
    (httpMethod: string)
    (baseUrl: string)
    (parameters: (string * string) list)
    (consumerSecret: string)
    (tokenSecret: string)
    : string =
    hmacSha1Base64 (signingKey consumerSecret tokenSecret) (signatureBaseString httpMethod baseUrl parameters)

/// The `oauth_*` protocol parameters (excluding the signature). `oauth_token`
/// is included only when a non-empty token is supplied (absent on the
/// leg-1 request-token fetch).
let protocolParameters
    (consumerKey: string)
    (token: string option)
    (nonce: string)
    (timestamp: string)
    : (string * string) list =
    [
        "oauth_consumer_key", consumerKey
        "oauth_nonce", nonce
        "oauth_signature_method", OAuth1aSignatureMethod.wireName OAuth1aSignatureMethod.HmacSha1
        "oauth_timestamp", timestamp
        "oauth_version", "1.0"
    ]
    @ (match token with
       | Some t when t <> "" -> [ "oauth_token", t ]
       | _ -> [])

/// Render the `Authorization: OAuth …` header value from the `oauth_*`
/// parameters (which MUST include `oauth_signature`). RFC 5849 §3.5.1 —
/// only protocol parameters appear in the header; each key/value is
/// percent-encoded and quoted, joined by `, `.
let authorizationHeader (oauthParameters: (string * string) list) : string =
    let rendered =
        oauthParameters
        |> List.map (fun (k, v) -> percentEncode k + "=\"" + percentEncode v + "\"")
        |> String.concat ", "

    "OAuth " + rendered

/// Build the full `Authorization` header value for a request: assemble the
/// protocol parameters, compute the signature over them plus the request
/// parameters, and render the header. `requestParameters` are the query +
/// form-body parameters that participate in the base string but NOT the
/// header. Deterministic given `nonce` + `timestamp` — the seam the unit
/// tests pin against the reference vector.
let buildAuthorizationHeaderValue
    (consumer: OAuth1aConsumerCredentials)
    (token: OAuth1aTokenPair option)
    (httpMethod: string)
    (baseUrl: string)
    (requestParameters: (string * string) list)
    (nonce: string)
    (timestamp: string)
    : string =
    let tokenValue = token |> Option.map _.Token
    let tokenSecret = token |> Option.map _.TokenSecret |> Option.defaultValue ""
    let protocol = protocolParameters consumer.ConsumerKey tokenValue nonce timestamp

    let signature =
        computeSignature httpMethod baseUrl (protocol @ requestParameters) consumer.ConsumerSecret tokenSecret

    authorizationHeader (("oauth_signature", signature) :: protocol)

let private freshNonce () = Guid.NewGuid().ToString("N")

let private unixTimestamp () =
    int64 (DateTime.UtcNow - DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds
    |> string

/// Split a request URI into the RFC 5849 base URL (scheme + authority +
/// path, lower-cased scheme/host, default ports dropped) and its query
/// parameters.
let private splitUri (uri: Uri) : string * (string * string) list =
    let scheme = uri.Scheme.ToLowerInvariant()
    let host = uri.Host.ToLowerInvariant()

    let authority =
        if
            (scheme = "http" && uri.Port = 80)
            || (scheme = "https" && uri.Port = 443)
            || uri.IsDefaultPort
        then
            host
        else
            sprintf "%s:%d" host uri.Port

    let baseUrl = sprintf "%s://%s%s" scheme authority uri.AbsolutePath

    let queryParams =
        if String.IsNullOrEmpty uri.Query then
            []
        else
            uri.Query.TrimStart('?').Split('&')
            |> Array.filter (fun p -> p <> "")
            |> Array.map (fun pair ->
                match pair.Split([| '=' |], 2) with
                | [| k; v |] -> Uri.UnescapeDataString k, Uri.UnescapeDataString v
                | [| k |] -> Uri.UnescapeDataString k, ""
                | _ -> pair, "")
            |> Array.toList

    baseUrl, queryParams

/// Sign an outgoing `HttpRequestMessage` in place — attaches the
/// `Authorization: OAuth …` header derived from `consumer` + `token`. The
/// signature covers the request's query parameters and, when the content
/// is `application/x-www-form-urlencoded`, its form-body parameters
/// (RFC 5849 §3.4.1.3). Signing is per-call; nothing is cached.
let signRequest
    (consumer: OAuth1aConsumerCredentials)
    (token: OAuth1aTokenPair)
    (request: HttpRequestMessage)
    : Async<unit> =
    async {
        let httpMethod = request.Method.Method

        let baseUrl, queryParams =
            match request.RequestUri with
            | null -> "", []
            | uri -> splitUri uri

        let! formParams = async {
            match request.Content with
            | :? FormUrlEncodedContent as form ->
                let! body = form.ReadAsStringAsync() |> Async.AwaitTask

                return
                    if String.IsNullOrEmpty body then
                        []
                    else
                        body.Split('&')
                        |> Array.filter (fun p -> p <> "")
                        |> Array.map (fun pair ->
                            match pair.Split([| '=' |], 2) with
                            | [| k; v |] -> Uri.UnescapeDataString k, Uri.UnescapeDataString v
                            | [| k |] -> Uri.UnescapeDataString k, ""
                            | _ -> pair, "")
                        |> Array.toList
            | _ -> return []
        }

        let header =
            buildAuthorizationHeaderValue
                consumer
                (Some token)
                httpMethod
                baseUrl
                (queryParams @ formParams)
                (freshNonce ())
                (unixTimestamp ())

        request.Headers.TryAddWithoutValidation("Authorization", header) |> ignore
    }