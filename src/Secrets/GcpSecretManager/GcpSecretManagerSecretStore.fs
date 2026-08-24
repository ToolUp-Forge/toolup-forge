module ToolUp.Secrets.GcpSecretManager

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform.Secrets

// ─── Configuration ───────────────────────────────────────────────────
//
// GCP Secret Manager implementation of `ISecretStore`. Mirrors the
// `src/Secrets/AwsSecretsManager` companion shape: one record-typed
// config, a `create` helper, and a `fromEnv` resolver. Activated via
// `TOOLUP_SECRET_STORE=gcp-secret-manager` + `TOOLUP_GCP_PROJECT_ID`
// in the consumer composition root.
//
// Transport. Pure BCL `HttpClient` against the Secret Manager REST API
// at `secretmanager.googleapis.com/v1`. Matches the
// "Companion-authoring guide" steer for HTTP-shaped companions
// ("use BCL `HttpClient` rather than a vendor SDK where the API is
// permissive") — the older revision of this companion went through
// `Google.Cloud.SecretManager.V1` and dragged ~20 transitive packages
// (`Google.Api.Gax`, `Grpc.*`, `Google.Protobuf`, `Newtonsoft.Json`)
// into every consumer; the REST shape is permissive and the auth
// implementation small enough that the SDK dep stopped being worth
// the supply-chain surface.
//
// IAM. The caller's service account must hold the five Secret
// operations on the configured project (or on resources matching the
// `toolup_*` name prefix when finer-grained policies are needed):
//   secretmanager.secrets.create
//   secretmanager.secrets.delete
//   secretmanager.secrets.list
//   secretmanager.versions.access
//   secretmanager.versions.add
// Pre-defined roles:
//   - `roles/secretmanager.secretAccessor` — read (versions.access)
//   - `roles/secretmanager.secretVersionAdder` — write
//     (secrets.create / versions.add)
//   - `roles/secretmanager.admin` — full access (covers delete + list)
// A minimum-privilege policy spec ships in the companion README.
//
// Credential resolution. Two-mode chain — equivalent to the
// previously-used SDK's Application Default Credentials chain
// narrowed to the modes that matter for the deployment shapes
// ToolUp supports:
//
//   1. `GOOGLE_APPLICATION_CREDENTIALS` set → load the service-account
//      JSON key file at that path; exchange a signed JWT for an OAuth
//      access token at `token_uri` (default
//      `https://oauth2.googleapis.com/token`). This is the off-GCP
//      path — dev shells, cross-cloud deploys, CI.
//   2. Env var unset → fall back to the GCE / Cloud Run metadata
//      server at
//      `http://metadata.google.internal/computeMetadata/v1/instance/`
//      `service-accounts/default/token`. On Cloud Run / GKE / GCE /
//      App Engine this returns a workload-identity-bound token with
//      no on-disk key.
//
// Both paths return `{access_token, expires_in}`; the companion
// caches the token in-process behind a `SemaphoreSlim` and refreshes
// 30s before expiry, so steady-state cost is one in-memory read for
// ~55 minutes per process.
//
// Cross-tenant isolation (GP 4). Single GCP project holds every
// ToolUp scope as an underscore-prefixed secret ID:
// `toolup_{scopeId}_{key}`. GCP secret IDs allow `[A-Za-z0-9_-]` only
// — no slashes, no dots — so underscore is the separator. The prefix
// encodes the scope; a Cloud Audit Log entry shows exactly which
// scope each request touched. Per-scope project isolation is possible
// (one project per tenant) but operationally heavy and outside this
// companion's scope.
//
// Cloud-KMS-native at-rest encryption. Secret Manager encrypts every
// secret at rest with Google-managed AES-256 keys by default;
// customer-managed encryption keys (CMEK) are configurable at the
// secret-creation level via the
// `replication.automatic.customerManagedEncryption` field (not
// exercised by this companion's default `SetSecret`). Wrapping this
// companion in `EncryptedSecretStore` would add a redundant envelope
// — the composition root deliberately does NOT wrap cloud-KMS
// companions, and the `EncryptedSecretStoreModeValidator`
// recognises `TOOLUP_SECRET_STORE=gcp-secret-manager` as equivalent
// to `EncryptedSecretStore` for the master-key gate.
//
// Versioning. Secret Manager treats each `SetSecret` as a new
// immutable version of the secret. `GetSecret name` resolves the
// `latest` version (the canonical alias for the highest-numbered
// enabled version). Callers that need a pinned version pass
// `name@versionId` (e.g. `"db-password@3"` or `"db-password@latest"`).
//
// Deletion. `DeleteSecret` removes the secret resource AND every
// version irreversibly. Unlike Azure Key Vault (soft-delete + purge)
// or AWS Secrets Manager (scheduled deletion), GCP has no recovery
// window — re-creating the same name immediately after delete is
// unconstrained. Tests still use GUID-suffixed scope IDs for
// hygiene.

/// Configuration for `GcpSecretManagerSecretStore`. Takes the GCP
/// project ID; credentials flow through either a service-account JSON
/// file at `GOOGLE_APPLICATION_CREDENTIALS` or the GCE metadata
/// server.
type GcpSecretManagerConfig = {
    /// GCP project ID — the slug from the GCP Console URL, e.g.
    /// `"my-project-12345"`. Read from `TOOLUP_GCP_PROJECT_ID`.
    ProjectId: string
}

module GcpSecretManagerConfig =
    let defaults = { ProjectId = "" }

// ─── Auth ────────────────────────────────────────────────────────────

module private Auth =
    let cloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform"

    let defaultTokenUri = "https://oauth2.googleapis.com/token"

    let metadataTokenUrl =
        "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token"

    type ServiceAccountKey = {
        ``type``: string
        client_email: string
        private_key: string
        token_uri: string
    }

    type AccessToken = {
        Token: string
        ExpiresAtUtc: DateTime
    }

    // Wire bodies are parsed with `JsonDocument` property reads rather
    // than `JsonSerializer.Deserialize` into the records above: this
    // module (and everything in it) is deliberately non-public, and
    // System.Text.Json's reflection serialiser only sees public
    // getters/constructors — deserialising a non-public record throws
    // `NotSupportedException` at runtime. Field names on the wire stay
    // snake_case, matching Google's responses.
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

    let private readServiceAccount (path: string) =
        let json = File.ReadAllText path

        use doc =
            try
                JsonDocument.Parse json
            with :? JsonException ->
                invalidArg
                    "GOOGLE_APPLICATION_CREDENTIALS"
                    (sprintf "Service-account JSON at '%s' could not be parsed." path)

        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            invalidArg
                "GOOGLE_APPLICATION_CREDENTIALS"
                (sprintf "Service-account JSON at '%s' could not be parsed." path)

        let clientEmail = tryReadString root "client_email"
        let privateKey = tryReadString root "private_key"
        let tokenUri = tryReadString root "token_uri"

        if String.IsNullOrWhiteSpace clientEmail then
            invalidArg
                "GOOGLE_APPLICATION_CREDENTIALS"
                (sprintf "Service-account JSON at '%s' is missing 'client_email'." path)

        if String.IsNullOrWhiteSpace privateKey then
            invalidArg
                "GOOGLE_APPLICATION_CREDENTIALS"
                (sprintf "Service-account JSON at '%s' is missing 'private_key'." path)

        {
            ``type`` = tryReadString root "type"
            client_email = clientEmail
            private_key = privateKey
            token_uri =
                if String.IsNullOrWhiteSpace tokenUri then
                    defaultTokenUri
                else
                    tokenUri
        }

    let private buildJwt (key: ServiceAccountKey) =
        let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

        let headerJson = JsonSerializer.Serialize {| alg = "RS256"; typ = "JWT" |}

        let claimsJson =
            JsonSerializer.Serialize {|
                iss = key.client_email
                scope = cloudPlatformScope
                aud = key.token_uri
                iat = now
                exp = now + 3600L
            |}

        let unsigned =
            base64UrlEncodeString headerJson + "." + base64UrlEncodeString claimsJson

        use rsa = RSA.Create()
        rsa.ImportFromPem(key.private_key.AsSpan())

        let signature =
            rsa.SignData(Encoding.UTF8.GetBytes unsigned, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

        unsigned + "." + base64UrlEncode signature

    let private parseToken (body: string) =
        let accessToken, expiresIn =
            try
                use doc = JsonDocument.Parse body
                let root = doc.RootElement

                if root.ValueKind <> JsonValueKind.Object then
                    null, 0
                else
                    let mutable expiresEl = Unchecked.defaultof<JsonElement>

                    let expiresIn =
                        if
                            root.TryGetProperty("expires_in", &expiresEl)
                            && expiresEl.ValueKind = JsonValueKind.Number
                        then
                            expiresEl.GetInt32()
                        else
                            0

                    tryReadString root "access_token", expiresIn
            with :? JsonException ->
                null, 0

        if String.IsNullOrWhiteSpace accessToken then
            failwithf "GCP token endpoint returned an unparseable body: %s" body

        {
            Token = accessToken
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(float expiresIn)
        }

    let private fetchServiceAccountToken (http: HttpClient) (key: ServiceAccountKey) = async {
        let jwt = buildJwt key

        let form =
            new FormUrlEncodedContent(
                [
                    KeyValuePair("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer")
                    KeyValuePair("assertion", jwt)
                ]
            )

        let! response = http.PostAsync(key.token_uri, form) |> Async.AwaitTask
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        if not response.IsSuccessStatusCode then
            failwithf "GCP token exchange failed (%d): %s" (int response.StatusCode) body

        return parseToken body
    }

    let private fetchMetadataToken (http: HttpClient) = async {
        use request = new HttpRequestMessage(HttpMethod.Get, metadataTokenUrl)
        request.Headers.Add("Metadata-Flavor", "Google")
        let! response = http.SendAsync request |> Async.AwaitTask
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        if not response.IsSuccessStatusCode then
            failwithf
                "GCE metadata-server token fetch failed (%d): %s. Set GOOGLE_APPLICATION_CREDENTIALS to a service-account JSON key file when running off-GCP."
                (int response.StatusCode)
                body

        return parseToken body
    }

    /// Per-store token cache. Resolves the credential source once at
    /// construction (service-account JSON file vs metadata server),
    /// then fetches + caches the access token. Thread-safe; refreshes
    /// 30s before expiry so concurrent requests don't all race the
    /// renewal.
    type TokenProvider(http: HttpClient) =
        let serviceAccountKey =
            match Environment.GetEnvironmentVariable "GOOGLE_APPLICATION_CREDENTIALS" with
            | null
            | "" -> None
            | path -> Some(readServiceAccount path)

        let semaphore = new SemaphoreSlim(1, 1)
        let mutable cached: AccessToken option = None
        let refreshSafetyWindow = TimeSpan.FromSeconds 30.0

        let fetch () =
            match serviceAccountKey with
            | Some key -> fetchServiceAccountToken http key
            | None -> fetchMetadataToken http

        let isFresh (token: AccessToken) =
            token.ExpiresAtUtc - DateTime.UtcNow > refreshSafetyWindow

        member _.GetAsync() = async {
            match cached with
            | Some t when isFresh t -> return t.Token
            | _ ->
                do! semaphore.WaitAsync() |> Async.AwaitTask

                try
                    match cached with
                    | Some t when isFresh t -> return t.Token
                    | _ ->
                        let! fresh = fetch ()
                        cached <- Some fresh
                        return fresh.Token
                finally
                    semaphore.Release() |> ignore
        }

// ─── Naming ──────────────────────────────────────────────────────────

module private Naming =
    // GCP Secret Manager secret IDs: 1-255 chars, `[A-Za-z0-9_-]` only.
    // No slashes, no dots, no `+`/`=`/`@` — strictly narrower than
    // AWS. Most ToolUp scope shapes (`_platform`, `team-{guid}`,
    // `user-{guid}`) pass through with only minor sanitisation; the
    // `@`-version-pin convention is parsed BEFORE sanitisation
    // because `@` is not a legal char in the underlying GCP secret
    // ID.
    let private isAllowed c =
        (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c = '_'
        || c = '-'

    let sanitise (s: string) =
        s |> String.map (fun c -> if isAllowed c then c else '_')

    let secretId (scopeId: string) (key: string) =
        $"toolup_{sanitise scopeId}_{sanitise key}"

    let scopePrefix (scopeId: string) = $"toolup_{sanitise scopeId}_"

    let stripPrefix (prefix: string) (name: string) =
        if name.StartsWith prefix then
            Some(name.Substring prefix.Length)
        else
            None

    /// Split `"name@versionId"` into `(name, version)`, defaulting to
    /// `"latest"` when no `@` is present. GCP's `latest` alias
    /// resolves to the highest-numbered enabled version.
    let parseVersion (key: string) =
        match key.IndexOf '@' with
        | -1 -> key, "latest"
        | i -> key.Substring(0, i), key.Substring(i + 1)

    /// Extract the trailing `{secretId}` from a resource name of the
    /// form `projects/{p}/secrets/{secretId}`. Defensive against
    /// future resource-name shape changes.
    let extractSecretId (resourceName: string) =
        let segments = resourceName.Split('/')

        if segments.Length >= 4 && segments[2] = "secrets" then
            Some segments[3]
        else
            None

// ─── REST plumbing ───────────────────────────────────────────────────

module private Rest =
    let baseUrl = "https://secretmanager.googleapis.com/v1"

    let projectResource (projectId: string) = $"projects/{projectId}"

    let secretsCollection (projectId: string) =
        $"{baseUrl}/{projectResource projectId}/secrets"

    let secretResource (projectId: string) (secretId: string) =
        $"{baseUrl}/{projectResource projectId}/secrets/{secretId}"

    let accessUrl (projectId: string) (secretId: string) (version: string) =
        $"{baseUrl}/{projectResource projectId}/secrets/{secretId}/versions/{version}:access"

    let addVersionUrl (projectId: string) (secretId: string) =
        $"{secretResource projectId secretId}:addVersion"

    let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// GCP error responses have shape `{"error":{"code":..,"message":..,"status":".."}}`.
    /// Extract the `status` field; returns `None` when the body is
    /// missing or not in the expected shape (treat as a non-canonical
    /// error and surface the raw body).
    let tryGetErrorStatus (body: string) =
        try
            use doc = JsonDocument.Parse body
            let mutable error = Unchecked.defaultof<JsonElement>
            let mutable status = Unchecked.defaultof<JsonElement>

            if
                doc.RootElement.TryGetProperty("error", &error)
                && error.TryGetProperty("status", &status)
                && status.ValueKind = JsonValueKind.String
            then
                Some(status.GetString())
            else
                None
        with _ ->
            None

    let send (http: HttpClient) (token: string) (request: HttpRequestMessage) = async {
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        return! http.SendAsync request |> Async.AwaitTask
    }

    let private jsonContent (payload: obj) =
        let body = JsonSerializer.Serialize payload
        new StringContent(body, Encoding.UTF8, "application/json")

    let buildGet (url: string) =
        new HttpRequestMessage(HttpMethod.Get, url)

    let buildDelete (url: string) =
        new HttpRequestMessage(HttpMethod.Delete, url)

    let buildPost (url: string) (payload: obj) =
        let req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Content <- jsonContent payload
        req

    /// AccessSecretVersion response: `{"name":"...","payload":{"data":"<base64>"}}`.
    /// Returns the decoded UTF-8 secret string, or None when the
    /// shape is missing.
    let tryReadSecretPayload (body: string) =
        use doc = JsonDocument.Parse body
        let mutable payload = Unchecked.defaultof<JsonElement>
        let mutable data = Unchecked.defaultof<JsonElement>

        if
            doc.RootElement.TryGetProperty("payload", &payload)
            && payload.TryGetProperty("data", &data)
            && data.ValueKind = JsonValueKind.String
        then
            let bytes = Convert.FromBase64String(data.GetString())
            Some(Encoding.UTF8.GetString bytes)
        else
            None

    /// ListSecrets response: `{"secrets":[{"name":"projects/.../secrets/.."}], "nextPageToken":".."}`.
    let readSecretListPage (body: string) =
        use doc = JsonDocument.Parse body
        let mutable secretsArray = Unchecked.defaultof<JsonElement>

        let names =
            if
                doc.RootElement.TryGetProperty("secrets", &secretsArray)
                && secretsArray.ValueKind = JsonValueKind.Array
            then
                [
                    for el in secretsArray.EnumerateArray() do
                        let mutable nameProp = Unchecked.defaultof<JsonElement>

                        if
                            el.TryGetProperty("name", &nameProp)
                            && nameProp.ValueKind = JsonValueKind.String
                        then
                            nameProp.GetString()
                ]
            else
                []

        let mutable nextTokenProp = Unchecked.defaultof<JsonElement>

        let nextToken =
            if
                doc.RootElement.TryGetProperty("nextPageToken", &nextTokenProp)
                && nextTokenProp.ValueKind = JsonValueKind.String
            then
                let t = nextTokenProp.GetString()

                if String.IsNullOrEmpty t then None else Some t
            else
                None

        names, nextToken

// ─── ISecretStore implementation ─────────────────────────────────────

/// GCP Secret Manager implementation of `ISecretStore`. One
/// `HttpClient` per instance; reused across the process lifetime per
/// Microsoft's guidance for `HttpClient`. The token provider caches
/// an in-process OAuth access token so steady-state per-call cost is
/// one cached read plus one REST round-trip.
type GcpSecretManagerSecretStore(config: GcpSecretManagerConfig) =
    do
        if String.IsNullOrWhiteSpace config.ProjectId then
            invalidArg "ProjectId" "GcpSecretManagerConfig.ProjectId is required (read from TOOLUP_GCP_PROJECT_ID)."

    let http = new HttpClient()
    let tokenProvider = Auth.TokenProvider http

    let withToken (build: unit -> HttpRequestMessage) = async {
        let! token = tokenProvider.GetAsync()
        use request = build ()
        return! Rest.send http token request
    }

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            let keyName, version = Naming.parseVersion key
            let secretId = Naming.secretId scopeId keyName
            let url = Rest.accessUrl config.ProjectId secretId version
            use! response = withToken (fun () -> Rest.buildGet url)
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            match int response.StatusCode with
            | 200 -> return Rest.tryReadSecretPayload body
            | 404 -> return None
            | 400 when Rest.tryGetErrorStatus body = Some "FAILED_PRECONDITION" ->
                // Version is disabled or destroyed; surface as
                // "not found" per the ISecretStore contract.
                return None
            | code -> return failwithf "GCP AccessSecretVersion failed (%d): %s" code body
        }

        member _.SetSecret(scopeId, key, value) = async {
            // Strip any `@version` suffix on writes — versions are
            // assigned by Secret Manager, not the caller.
            let keyName, _ = Naming.parseVersion key
            let secretId = Naming.secretId scopeId keyName
            let projectId = config.ProjectId
            let payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes value)

            let addVersion () =
                Rest.buildPost (Rest.addVersionUrl projectId secretId) {|
                    payload = {| data = payloadBase64 |}
                |}

            // AddSecretVersion is the hot path (the secret container
            // already exists from a prior write). On 404, fall back
            // to CreateSecret + AddSecretVersion. Cheaper than a
            // probe + branch for the common idempotent-write case.
            use! firstAttempt = withToken addVersion
            let! firstBody = firstAttempt.Content.ReadAsStringAsync() |> Async.AwaitTask

            match int firstAttempt.StatusCode with
            | code when code >= 200 && code < 300 -> return Ok()
            | 404 ->
                let createUrl =
                    Rest.secretsCollection projectId + "?secretId=" + Uri.EscapeDataString secretId

                use! createResponse =
                    withToken (fun () ->
                        Rest.buildPost createUrl {|
                            replication = {| automatic = obj () |}
                        |})

                let! createBody = createResponse.Content.ReadAsStringAsync() |> Async.AwaitTask

                if not createResponse.IsSuccessStatusCode then
                    return Error(sprintf "GCP CreateSecret failed (%d): %s" (int createResponse.StatusCode) createBody)
                else
                    use! secondAttempt = withToken addVersion
                    let! secondBody = secondAttempt.Content.ReadAsStringAsync() |> Async.AwaitTask

                    if secondAttempt.IsSuccessStatusCode then
                        return Ok()
                    else
                        return
                            Error(
                                sprintf
                                    "GCP AddSecretVersion after CreateSecret failed (%d): %s"
                                    (int secondAttempt.StatusCode)
                                    secondBody
                            )
            | code -> return Error(sprintf "GCP AddSecretVersion failed (%d): %s" code firstBody)
        }

        member _.DeleteSecret(scopeId, key) = async {
            let keyName, _ = Naming.parseVersion key
            let secretId = Naming.secretId scopeId keyName
            let url = Rest.secretResource config.ProjectId secretId
            use! response = withToken (fun () -> Rest.buildDelete url)
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            match int response.StatusCode with
            | code when code >= 200 && code < 300 -> return Ok()
            | 404 -> return Ok()
            | code -> return Error(sprintf "GCP DeleteSecret failed (%d): %s" code body)
        }

        member _.ListKeys(scopeId) = async {
            let prefix = Naming.scopePrefix scopeId
            let projectId = config.ProjectId
            let results = System.Collections.Generic.List<string>()
            let mutable pageToken: string option = None
            let mutable keepGoing = true

            while keepGoing do
                let queryParts = [
                    // Server-side filter on the `name` field —
                    // Secret Manager accepts the prefix-as-substring
                    // form `name:toolup_{scopeId}_`. The Uri.Escape
                    // keeps the colon legible on the wire.
                    "filter=" + Uri.EscapeDataString(sprintf "name:%s" prefix)
                    match pageToken with
                    | Some t -> "pageToken=" + Uri.EscapeDataString t
                    | None -> ()
                ]

                let url = Rest.secretsCollection projectId + "?" + String.concat "&" queryParts

                use! response = withToken (fun () -> Rest.buildGet url)
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                if not response.IsSuccessStatusCode then
                    return! async.Return(failwithf "GCP ListSecrets failed (%d): %s" (int response.StatusCode) body)

                let names, nextToken = Rest.readSecretListPage body

                for fullName in names do
                    // `name` is the full resource name
                    // `projects/{p}/secrets/{secretId}`; recover the
                    // secretId, then strip the scope prefix to get
                    // the original key.
                    match Naming.extractSecretId fullName with
                    | Some sid ->
                        match Naming.stripPrefix prefix sid with
                        | Some k -> results.Add k
                        | None -> ()
                    | None -> ()

                match nextToken with
                | Some t -> pageToken <- Some t
                | None -> keepGoing <- false

            return results |> List.ofSeq
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `ISecretStore` from a `GcpSecretManagerConfig`. The
/// `HttpClient` + token cache are initialised eagerly here; the first
/// secret operation triggers the lazy token fetch. Bad project ID,
/// missing credentials, or an unreachable metadata server surface on
/// first request, not at composition.
let create (config: GcpSecretManagerConfig) : ISecretStore =
    new GcpSecretManagerSecretStore(config) :> ISecretStore

/// Read the project ID from `TOOLUP_GCP_PROJECT_ID` and construct a
/// `GcpSecretManagerSecretStore`. Returns `None` when the env var is
/// unset / empty so the deployment falls back to whatever the
/// composition root chose (typically `EncryptedSecretStore` over
/// `FileSecretStore`).
let fromEnv () : ISecretStore option =
    match Environment.GetEnvironmentVariable ToolUp.Platform.ConfigKeys.Names.gcpProjectId with
    | null
    | "" -> None
    | projectId -> Some(create { ProjectId = projectId })

// ─── Phase 9c portability audit (six rules) ──────────────────────────
//
// 1. Identity by value — `ISecretStore` returns `string option` and
//    `Result<unit, string>`; never an HttpClient handle or a cached
//    bearer token.
// 2. Async at every boundary — every interface method returns
//    `Async<_>`; HttpClient Tasks bridged via `Async.AwaitTask`.
// 3. Retry as data — none expressed by this companion. HttpClient's
//    `SocketsHttpHandler` can be configured for retry / timeout if
//    the deployment needs it; the companion stays pure pass-through.
// 4. Stateless between calls — the cached OAuth access token is
//    process-local, derived from the same on-disk / metadata-server
//    credential each node observes. Distributed-safe: any node with
//    the same ADC identity produces identical results.
// 5. No cross-shard ordering — Secret Manager makes no global
//    ordering promises across secrets, and this companion makes
//    none either. `ListKeys` order is whatever the REST `ListSecrets`
//    endpoint returns (page-by-page, no guaranteed sort).
// 6. Precision at the lower bound — N/A; `ISecretStore` has no
//    timing semantics.