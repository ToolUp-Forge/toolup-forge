// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleDirectoryAuth

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading

// ─── Google service-account JWT-grant token exchange ─────────────────
//
// The single module that knows how a Google Workspace service account
// turns into a bearer token. Everything above it — the directory
// lookups, the mail send, the health probe, the preflight validator —
// asks for a token by (subject, scopes) and never sees a private key,
// an assertion, or the token endpoint. Kept separate so the auth model
// can be read, reviewed and tested on its own; it is the only part of
// this companion with any cryptography in it.
//
// **The grant.** Google Workspace application access is
// *domain-wide delegation* (DWD): a service account is authorised, by
// a Workspace super-admin, to impersonate users in the domain for an
// explicit list of OAuth scopes. The wire ceremony is RFC 7523's
// JWT bearer grant:
//
//   1. Build a JWT whose claims name the service account (`iss`), the
//      scopes wanted (`scope`), the token endpoint (`aud`), the user
//      being impersonated (`sub`), and a ≤1h validity window
//      (`iat` / `exp`).
//   2. Sign it RS256 with the service account's private key — the
//      `private_key` PEM inside the JSON key file.
//   3. POST it to the token endpoint as
//      `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`
//      &`assertion=<jwt>`.
//   4. Receive `{access_token, expires_in}` and use it as a bearer.
//
// The `sub` claim is what makes this domain-wide delegation rather
// than plain service-account auth, and it is why the two capabilities
// this companion offers need two different tokens: the directory read
// impersonates a Workspace ADMIN (only an admin may list users), while
// the invitation mail impersonates the SENDER MAILBOX (Gmail sends as
// whoever the token impersonates). Tokens are therefore cached per
// `(subject, scopes)` pair — one cache entry per capability, not one
// per process.
//
// **No Google client SDK.** BCL `HttpClient` + `System.Security.
// Cryptography.RSA` + `System.Text.Json`, per the companion-authoring
// guide's steer for HTTP-shaped companions (GP 1). The equivalent
// `Google.Apis.Auth` path drags ~15 transitive packages into every
// consumer for an exchange that is ~60 lines.
//
// **Credentials never come from the environment.** This module is
// handed a *loader* — the calling companion resolves the
// service-account JSON through `ISecretStore` and passes a thunk. There
// is no `GOOGLE_APPLICATION_CREDENTIALS` read here, deliberately: a key
// with domain-wide delegation can impersonate any user in the
// Workspace domain, so it belongs in the deployment's secret store
// where rotation and access are audited, not in a process env var or
// on the container filesystem.

/// OAuth scope for read-only Admin SDK Directory API access. The
/// narrowest scope that serves `SearchUsers` / `ResolveUsers`; grant
/// this one in the admin console, not the read-write
/// `admin.directory.user`.
[<Literal>]
let DirectoryReadonlyScope =
    "https://www.googleapis.com/auth/admin.directory.user.readonly"

/// OAuth scope for sending mail as the impersonated user. `gmail.send`
/// is send-only — it grants no read access to the mailbox, which is
/// the right grant for a transactional invitation sender.
[<Literal>]
let GmailSendScope = "https://www.googleapis.com/auth/gmail.send"

/// Google's OAuth 2.0 token endpoint. Overridable on
/// `GoogleDirectoryConfig` for test doubles and for the rare private
/// egress proxy; the service-account JSON's own `token_uri` wins when
/// present, since that is what the key was minted against.
[<Literal>]
let DefaultTokenEndpoint = "https://oauth2.googleapis.com/token"

/// The three fields this companion needs out of a Google
/// service-account JSON key file. Parsed rather than deserialised
/// wholesale so a key file carrying extra fields (or a future field
/// set) still loads.
type ServiceAccountKey = {
    /// `client_email` — the service account's own address, e.g.
    /// `toolup-directory@my-project.iam.gserviceaccount.com`. Becomes
    /// the assertion's `iss`.
    ClientEmail: string
    /// `private_key` — the PKCS#8 PEM the assertion is RS256-signed
    /// with. Never logged, never surfaced in an error message.
    PrivateKeyPem: string
    /// `token_uri` — the endpoint the assertion is redeemed at, and
    /// therefore also its `aud`. Defaults to
    /// `DefaultTokenEndpoint` when the key file omits it.
    TokenUri: string
}

/// A redeemed bearer token and the instant it stops being usable.
type AccessToken = {
    /// The `access_token` value, presented as `Authorization: Bearer`.
    Token: string
    /// UTC instant derived from the response's `expires_in`. The token
    /// provider refreshes ahead of this by a safety window rather than
    /// waiting for a 401.
    ExpiresAtUtc: DateTime
}

let private tryReadString (root: JsonElement) (name: string) =
    let mutable el = Unchecked.defaultof<JsonElement>

    if root.TryGetProperty(name, &el) && el.ValueKind = JsonValueKind.String then
        el.GetString()
    else
        null

let private base64UrlEncode (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private base64UrlEncodeString (s: string) =
    base64UrlEncode (Encoding.UTF8.GetBytes s)

/// Parse a Google service-account JSON key file. Returns `Error` with
/// a message naming the missing / malformed field rather than throwing
/// — the caller surfaces it through the preflight validator or as a
/// `directory unavailable: …` at request time, and neither wants an
/// exception carrying key material in its context.
let parseServiceAccountJson (json: string) : Result<ServiceAccountKey, string> =
    if String.IsNullOrWhiteSpace json then
        Result.Error "service-account JSON is empty"
    else
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Result.Error "service-account JSON is not a JSON object"
            else
                let clientEmail = tryReadString root "client_email"
                let privateKey = tryReadString root "private_key"
                let tokenUri = tryReadString root "token_uri"

                if String.IsNullOrWhiteSpace clientEmail then
                    Result.Error "service-account JSON is missing 'client_email'"
                elif String.IsNullOrWhiteSpace privateKey then
                    Result.Error "service-account JSON is missing 'private_key'"
                else
                    Result.Ok {
                        ClientEmail = clientEmail
                        PrivateKeyPem = privateKey
                        TokenUri =
                            if String.IsNullOrWhiteSpace tokenUri then
                                DefaultTokenEndpoint
                            else
                                tokenUri
                    }
        with :? JsonException as ex ->
            Result.Error(sprintf "service-account JSON could not be parsed: %s" ex.Message)

/// Build the RS256-signed JWT assertion for a domain-wide-delegation
/// grant. `subject` is the Workspace user being impersonated (the
/// `sub` claim); `scopes` is the space-joined `scope` claim. `now` is
/// passed in rather than read from the clock so the assertion is a
/// pure function of its inputs and can be asserted on in tests.
///
/// Returns `Error` when the PEM will not import — a truncated or
/// wrong-format `private_key` is the single most common
/// service-account-file defect, and it should read as a configuration
/// message, not a `CryptographicException` from a request path.
let buildAssertion
    (key: ServiceAccountKey)
    (subject: string)
    (scopes: string list)
    (now: DateTimeOffset)
    : Result<string, string> =
    let issuedAt = now.ToUnixTimeSeconds()

    let headerJson = JsonSerializer.Serialize {| alg = "RS256"; typ = "JWT" |}

    let claimsJson =
        JsonSerializer.Serialize {|
            iss = key.ClientEmail
            scope = String.Join(" ", scopes)
            aud = key.TokenUri
            sub = subject
            iat = issuedAt
            exp = issuedAt + 3600L
        |}

    let unsigned =
        base64UrlEncodeString headerJson + "." + base64UrlEncodeString claimsJson

    try
        use rsa = RSA.Create()
        rsa.ImportFromPem(key.PrivateKeyPem.AsSpan())

        let signature =
            rsa.SignData(Encoding.UTF8.GetBytes unsigned, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

        Result.Ok(unsigned + "." + base64UrlEncode signature)
    with ex ->
        // Deliberately reports the exception TYPE and message only —
        // never the PEM, and never a prefix of it.
        Result.Error(sprintf "service-account private key could not be loaded (%s)" ex.Message)

let private parseTokenResponse (body: string) : Result<AccessToken, string> =
    try
        use doc = JsonDocument.Parse body
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Result.Error "token endpoint returned a non-object body"
        else
            let accessToken = tryReadString root "access_token"
            let mutable expiresEl = Unchecked.defaultof<JsonElement>

            let expiresIn =
                if
                    root.TryGetProperty("expires_in", &expiresEl)
                    && expiresEl.ValueKind = JsonValueKind.Number
                then
                    expiresEl.GetInt32()
                else
                    0

            if String.IsNullOrWhiteSpace accessToken then
                Result.Error "token endpoint returned no access_token"
            else
                Result.Ok {
                    Token = accessToken
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(float expiresIn)
                }
    with :? JsonException ->
        Result.Error "token endpoint returned an unparseable body"

/// Redeem one assertion at the token endpoint. A non-2xx response is
/// returned as `Error` carrying the status and a bounded prefix of the
/// body — Google's `unauthorized_client` / `invalid_grant` payloads
/// name exactly which half of the delegation is missing, and dropping
/// them turns a five-minute admin-console fix into a guessing game.
let exchangeToken
    (http: HttpClient)
    (key: ServiceAccountKey)
    (subject: string)
    (scopes: string list)
    : Async<Result<AccessToken, string>> =
    async {
        match buildAssertion key subject scopes DateTimeOffset.UtcNow with
        | Result.Error e -> return Result.Error e
        | Result.Ok assertion ->
            try
                let! ct = Async.CancellationToken

                use form =
                    new FormUrlEncodedContent(
                        [
                            KeyValuePair("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer")
                            KeyValuePair("assertion", assertion)
                        ]
                    )

                use! response = http.PostAsync(key.TokenUri, form, ct) |> Async.AwaitTask
                let! body = response.Content.ReadAsStringAsync ct |> Async.AwaitTask

                if not response.IsSuccessStatusCode then
                    let detail =
                        if isNull body then
                            ""
                        else
                            body.Substring(0, min 200 body.Length)

                    return
                        Result.Error(
                            sprintf
                                "token exchange failed for subject '%s' (%d): %s"
                                subject
                                (int response.StatusCode)
                                detail
                        )
                else
                    return parseTokenResponse body
            with
            | :? OperationCanceledException -> return Result.Error "token exchange cancelled"
            | ex -> return Result.Error(sprintf "token exchange failed for subject '%s': %s" subject ex.Message)
    }

/// Per-companion token cache. Holds one entry per `(subject, scopes)`
/// pair — the directory-read token and the mail-send token impersonate
/// different users under different scopes and must not share a slot.
///
/// The service-account key itself is loaded once, through the caller's
/// `ISecretStore`-backed thunk, and memoised: a secret-store round-trip
/// per request would put the deployment's secret backend on the
/// typeahead's hot path. A rotated key is picked up on the next process
/// start, which matches how every other credential-holding companion in
/// the SDK behaves.
///
/// Internal by construction: nothing outside this companion should be
/// able to ask for a token, and the type holds decrypted key material
/// for the process lifetime.
type internal TokenProvider(http: HttpClient, loadKey: unit -> Async<Result<ServiceAccountKey, string>>) =
    let cache = ConcurrentDictionary<string, AccessToken>()
    let gate = new SemaphoreSlim(1, 1)
    let refreshSafetyWindow = TimeSpan.FromSeconds 60.0
    let mutable key: Result<ServiceAccountKey, string> option = None

    let isFresh (token: AccessToken) =
        token.ExpiresAtUtc - DateTime.UtcNow > refreshSafetyWindow

    let cacheKey (subject: string) (scopes: string list) =
        subject + "\n" + String.Join(" ", scopes)

    let ensureKey () = async {
        match key with
        | Some k -> return k
        | None ->
            let! loaded = loadKey ()
            key <- Some loaded
            return loaded
    }

    /// Resolve a bearer token for `subject` over `scopes`, serving a
    /// cached one whenever it has more than the safety window left.
    member _.GetAsync(subject: string, scopes: string list) : Async<Result<string, string>> = async {
        let k = cacheKey subject scopes

        match cache.TryGetValue k with
        | true, token when isFresh token -> return Result.Ok token.Token
        | _ ->
            do! gate.WaitAsync() |> Async.AwaitTask

            try
                // Double-checked: a sibling request may have refreshed
                // this exact (subject, scopes) slot while we queued.
                match cache.TryGetValue k with
                | true, token when isFresh token -> return Result.Ok token.Token
                | _ ->
                    match! ensureKey () with
                    | Result.Error e -> return Result.Error e
                    | Result.Ok sa ->
                        match! exchangeToken http sa subject scopes with
                        | Result.Error e -> return Result.Error e
                        | Result.Ok token ->
                            cache[k] <- token
                            return Result.Ok token.Token
            finally
                gate.Release() |> ignore
    }