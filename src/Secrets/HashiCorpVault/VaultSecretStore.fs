module ToolUp.Secrets.HashiCorpVault

open System
open System.Net.Http
open System.Text
open System.Text.Json
open ToolUp.Platform.Secrets

/// Raised when Vault rejects the request token with `401 Unauthorized`
/// or `403 Forbidden`. Named (rather than the bare
/// `HttpRequestException` that `EnsureSuccessStatusCode` would throw)
/// so a token that expires mid-flight surfaces with the offending env
/// var + the renewal path in the message, close to where the caller
/// can act, instead of a generic `403` far downstream. See Phase 2c —
/// per-call credential-provider seam.
exception SecretStoreError of message: string

// ─── Configuration ───────────────────────────────────────────────────
//
// Phase 2a — HashiCorp Vault implementation of `ISecretStore`. Pure
// BCL `HttpClient` against Vault's KV v2 secrets engine HTTP API; no
// vendor SDK dependency (matches the CLAUDE.md "Companion-authoring
// guide" steer for HTTP-shaped companions: "use BCL `HttpClient`
// rather than a vendor SDK where the API is permissive").
//
// Activated via:
//   TOOLUP_SECRET_STORE=vault
//   VAULT_ADDR=https://vault.example.com:8200
//   VAULT_TOKEN=<token>
//   VAULT_NAMESPACE=<namespace>     (optional; Vault Enterprise only)
//
// Auth model. Token auth only in MVP. The token is sent as
// `X-Vault-Token` on every request. `fromEnv` reads `VAULT_TOKEN` once
// and sends it as a constant. For out-of-band token rotation without a
// restart, build the config via `create` with `TokenProvider = Some f`
// (Phase 2c): `f ()` is called fresh on every request, so a renewed
// token (AppRole / Kubernetes / OIDC auth methods that refresh through
// the orchestrator's secret-injection flow) is picked up automatically.
// A `401`/`403` from an expired-or-revoked token surfaces as the named
// `SecretStoreError` rather than a bare `HttpRequestException`.
//
// Token policy. The token must hold capabilities on
// `secret/data/toolup/*` and `secret/metadata/toolup/*`:
//   - read  → GetSecret
//   - create+update → SetSecret
//   - delete → DeleteSecret  (deletes ALL versions via metadata path)
//   - list  → ListKeys      (HTTP LIST on metadata path)
// A minimum-privilege policy snippet ships in the companion README.
//
// KV v2 only. The companion assumes the configured `MountPath` runs
// the KV v2 secrets engine. KV v1's flat path layout (no `data` /
// `metadata` namespacing) is a different protocol and deliberately
// out of scope — KV v2 is the default since Vault 1.1 (2019) and the
// operator-recommended choice. Detection of KV v1 mounts is not
// attempted; calls against a KV v1 mount surface as 404s from Vault.
//
// Cross-tenant isolation (GP 4). Single Vault holds every ToolUp
// scope as a slash-separated path: `{mount}/data/toolup/{scopeId}/{key}`.
// Vault's audit log captures every request with the path, so cross-
// scope reads are visible at the audit layer. A deployment that
// requires per-scope policy boundaries can issue scope-specific tokens
// (one token per scope, each with a path-scoped policy); this
// companion's caller manages that orchestration externally.
//
// Cloud-KMS-native at-rest encryption. Vault encrypts every secret at
// rest with its barrier key (typically auto-unsealed via cloud KMS or
// HSM in production). Wrapping this companion in `EncryptedSecretStore`
// would add a redundant envelope — `Server.fs` does NOT wrap cloud-KMS
// companions, and the Phase 6l.E plaintext-secrets validator
// recognises `TOOLUP_SECRET_STORE=vault` as equivalent to
// `EncryptedSecretStore` for the master-key gate.

/// Configuration for `VaultSecretStore`. Construct from environment
/// values (`VAULT_ADDR`, `VAULT_TOKEN`, optional `VAULT_NAMESPACE`)
/// via `fromEnv`, or directly via `create`.
type VaultConfig = {
    /// Vault base URL — `https://vault.example.com:8200` (no trailing
    /// slash). Read from `VAULT_ADDR`.
    Address: string
    /// Auth token. Read from `VAULT_TOKEN`.
    Token: string
    /// Optional Vault Enterprise namespace. Sent as `X-Vault-Namespace`
    /// when set. Read from `VAULT_NAMESPACE` (empty / unset → no
    /// namespace header).
    Namespace: string option
    /// KV v2 mount path (no leading or trailing slash). Default
    /// `"secret"` matches Vault's out-of-the-box mount.
    MountPath: string
    /// Phase 2c — optional per-call token provider. `None` (default)
    /// preserves today's behaviour: the constant `Token` above is sent
    /// on every request. `Some f` calls `f ()` fresh on *each* request
    /// and sends the result as `X-Vault-Token`, so a TTL'd / renewable
    /// Vault token (AppRole, Kubernetes, OIDC auth methods that refresh
    /// out of band) is picked up without restarting the process. The
    /// closure typically closes over the orchestrator's token-file
    /// reload or a renew loop. Contrast the anti-pattern this replaces:
    /// snapshotting the token into `HttpClient.DefaultRequestHeaders` at
    /// construction, which pins the first token for the process lifetime
    /// (see `PeerBearerAuthMiddleware` for the re-read-per-call precedent).
    TokenProvider: (unit -> string) option
}

module VaultConfig =
    let defaults = {
        Address = ""
        Token = ""
        Namespace = None
        MountPath = "secret"
        TokenProvider = None
    }

// ─── HTTP plumbing ───────────────────────────────────────────────────

module private Http =
    // `HttpClient` is meant to be reused across the process lifetime per
    // Microsoft's guidance. One per config (keyed by base address) is
    // sufficient; ToolUp deployments use one Vault per process.
    //
    // Phase 2c: the token is NO LONGER snapshotted into
    // `DefaultRequestHeaders` here — that pinned the first token for the
    // client's lifetime, so a TTL'd token expired into a bare
    // `403` far from the store. It is now applied per request (see
    // `send`), read fresh from the config's `TokenProvider` on every
    // call. The static (`TokenProvider = None`) path resolves to the
    // constant `Token` per request, which is behaviourally identical to
    // the old default-header snapshot.
    let buildClient (config: VaultConfig) =
        // Validate the address up front. `Uri config.Address` below throws
        // a generic UriFormatException on a malformed value; this names the
        // offending config (VAULT_ADDR) and the expected shape instead.
        match Uri.TryCreate(config.Address, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> ()
        | _ ->
            invalidArg
                "Address"
                (sprintf
                    "VaultConfig.Address (VAULT_ADDR) = '%s' is not a valid absolute http(s):// URL (expected e.g. https://vault.example.com:8200)."
                    config.Address)

        let client = new HttpClient()
        client.BaseAddress <- Uri config.Address

        // The namespace is deployment-static (not rotated), so it stays a
        // default header. The token does not.
        match config.Namespace with
        | Some ns when not (String.IsNullOrWhiteSpace ns) -> client.DefaultRequestHeaders.Add("X-Vault-Namespace", ns)
        | _ -> ()

        client

    /// Resolve the request token: the per-call provider when set, else
    /// the constant configured token (back-compat).
    let resolveToken (config: VaultConfig) =
        match config.TokenProvider with
        | Some provider -> provider ()
        | None -> config.Token

    /// Send one request with the token applied per-call. Maps a
    /// `401`/`403` to `SecretStoreError` (a token that expired or was
    /// revoked) instead of letting the caller's `EnsureSuccessStatusCode`
    /// throw an anonymous `HttpRequestException`. NotFound and other
    /// statuses pass through for the caller to interpret.
    let send
        (client: HttpClient)
        (config: VaultConfig)
        (method: HttpMethod)
        (path: string)
        (content: HttpContent option)
        =
        async {
            use request = new HttpRequestMessage(method, path)

            request.Headers.TryAddWithoutValidation("X-Vault-Token", resolveToken config)
            |> ignore

            content |> Option.iter (fun c -> request.Content <- c)
            let! response = client.SendAsync request |> Async.AwaitTask

            if
                response.StatusCode = Net.HttpStatusCode.Unauthorized
                || response.StatusCode = Net.HttpStatusCode.Forbidden
            then
                raise (
                    SecretStoreError(
                        sprintf
                            "Vault rejected the request token (%d) on '%s' — VAULT_TOKEN has expired or been revoked. Renew the token (or restart with a fresh VAULT_TOKEN); if a TokenProvider is configured, ensure it returns a currently-valid token."
                            (int response.StatusCode)
                            path
                    )
                )

            return response
        }

// ─── Naming ──────────────────────────────────────────────────────────

module private Naming =
    // Vault paths are slash-separated and allow `[a-zA-Z0-9_./-]`.
    // Most ToolUp scope shapes pass through; non-allowed chars become
    // `-` defensively.
    let private isAllowed c =
        (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c = '_'
        || c = '.'
        || c = '-'

    let sanitise (s: string) =
        s |> String.map (fun c -> if isAllowed c then c else '-')

    let dataPath (mount: string) (scopeId: string) (key: string) =
        $"v1/{mount}/data/toolup/{sanitise scopeId}/{sanitise key}"

    let metadataPath (mount: string) (scopeId: string) (key: string) =
        $"v1/{mount}/metadata/toolup/{sanitise scopeId}/{sanitise key}"

    let scopeListPath (mount: string) (scopeId: string) =
        $"v1/{mount}/metadata/toolup/{sanitise scopeId}/"

// ─── ISecretStore implementation ─────────────────────────────────────

/// HashiCorp Vault KV v2 implementation of `ISecretStore`. One
/// `HttpClient` per instance; reused across the process lifetime per
/// Microsoft's guidance for `HttpClient`.
type VaultSecretStore(config: VaultConfig) =
    let client = Http.buildClient config

    let getValue (path: string) = async {
        let! response = Http.send client config HttpMethod.Get path None

        if response.StatusCode = Net.HttpStatusCode.NotFound then
            return None
        else
            response.EnsureSuccessStatusCode() |> ignore
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            use doc = JsonDocument.Parse body
            // KV v2 shape: { "data": { "data": { "value": "..." }, "metadata": {...} } }
            let mutable outerData = Unchecked.defaultof<JsonElement>
            let mutable innerData = Unchecked.defaultof<JsonElement>
            let mutable valueProp = Unchecked.defaultof<JsonElement>

            if
                doc.RootElement.TryGetProperty("data", &outerData)
                && outerData.TryGetProperty("data", &innerData)
                && innerData.TryGetProperty("value", &valueProp)
            then
                return Some(valueProp.GetString())
            else
                return None
    }

    let putValue (path: string) (value: string) = async {
        // Body: {"data":{"value":"<secret>"}}
        let payload = {| data = {| value = value |} |}
        let json = JsonSerializer.Serialize payload
        // Ownership of `content` passes to the `HttpRequestMessage` inside
        // `Http.send`, which disposes it with the request.
        let content = new StringContent(json, Encoding.UTF8, "application/json")
        let! response = Http.send client config HttpMethod.Post path (Some(content :> HttpContent))
        return response
    }

    let deletePath (path: string) = async {
        // DELETE on the metadata path wipes ALL versions + metadata;
        // GetSecret returns 404 immediately. This matches the
        // ISecretStore "delete then get returns None" contract.
        let! response = Http.send client config HttpMethod.Delete path None
        return response
    }

    let listKeys (path: string) = async {
        // Vault uses the non-standard HTTP LIST verb; most HTTP
        // clients route LIST as a custom method. The friendlier
        // alternative is `GET {path}?list=true` which Vault treats
        // identically.
        let listUrl = $"{path}?list=true"
        let! response = Http.send client config HttpMethod.Get listUrl None

        if response.StatusCode = Net.HttpStatusCode.NotFound then
            return []
        else
            response.EnsureSuccessStatusCode() |> ignore
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            use doc = JsonDocument.Parse body
            // List shape: { "data": { "keys": ["k1", "k2", ...] } }
            let mutable data = Unchecked.defaultof<JsonElement>
            let mutable keys = Unchecked.defaultof<JsonElement>

            if
                doc.RootElement.TryGetProperty("data", &data)
                && data.TryGetProperty("keys", &keys)
            then
                return [ for el in keys.EnumerateArray() -> el.GetString() ]
            else
                return []
    }

    /// Phase 457 — Vault encrypts everything it writes to its storage
    /// backend under the barrier key held by its own seal; nothing lands in
    /// the clear even when the backend is a plain filesystem or Consul.
    /// Declared so the at-rest preflight passes on the composed store's own
    /// evidence rather than on the `TOOLUP_SECRET_STORE` spelling.
    interface ISecretStoreAtRestPosture with
        member _.AtRestPosture =
            EncryptsAtRest "HashiCorp Vault, barrier-encrypted storage backend"

    interface ISecretStore with
        member _.GetSecret(scopeId, key) =
            getValue (Naming.dataPath config.MountPath scopeId key)

        member _.SetSecret(scopeId, key, value) = async {
            let path = Naming.dataPath config.MountPath scopeId key
            let! response = putValue path value

            if response.IsSuccessStatusCode then
                return Ok()
            else
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                return Error $"Vault SetSecret failed ({int response.StatusCode}): {body}"
        }

        member _.DeleteSecret(scopeId, key) = async {
            let path = Naming.metadataPath config.MountPath scopeId key
            let! response = deletePath path

            if
                response.IsSuccessStatusCode
                || response.StatusCode = Net.HttpStatusCode.NotFound
            then
                return Ok()
            else
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                return Error $"Vault DeleteSecret failed ({int response.StatusCode}): {body}"
        }

        member _.ListKeys(scopeId) =
            listKeys (Naming.scopeListPath config.MountPath scopeId)

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `ISecretStore` from a `VaultConfig`. The underlying
/// `HttpClient` is created eagerly here (base address + default
/// headers are construction-time properties).
let create (config: VaultConfig) : ISecretStore =
    new VaultSecretStore(config) :> ISecretStore

/// Read `VAULT_ADDR`, `VAULT_TOKEN`, and (optional) `VAULT_NAMESPACE`
/// from the environment and construct a `VaultSecretStore`. Returns
/// `None` when either of the required vars is unset so the deployment
/// falls back to whatever the composition root chose (typically
/// `EncryptedSecretStore` over `FileSecretStore`).
let fromEnv () : ISecretStore option =
    let addr = Environment.GetEnvironmentVariable "VAULT_ADDR"
    let token = Environment.GetEnvironmentVariable "VAULT_TOKEN"

    if String.IsNullOrWhiteSpace addr || String.IsNullOrWhiteSpace token then
        None
    else
        let ns =
            match Environment.GetEnvironmentVariable "VAULT_NAMESPACE" with
            | null
            | "" -> None
            | s -> Some s

        Some(
            create {
                Address = addr
                Token = token
                Namespace = ns
                MountPath = "secret"
                // `fromEnv` wires the static token read once here. A
                // deployment that rotates Vault tokens out of band builds
                // its config via `create` with `TokenProvider = Some f`
                // instead (Phase 2c) — e.g. a closure that re-reads the
                // orchestrator-managed token file.
                TokenProvider = None
            }
        )

// ─── Phase 9c portability audit (six rules) ──────────────────────────
//
// 1. Identity by value — `ISecretStore` returns `string option` and
//    `Result<unit, string>`; never a live HttpClient handle or a Vault
//    lease ID.
// 2. Async at every boundary — every interface method returns
//    `Async<_>`; HttpClient Tasks bridged via `Async.AwaitTask`.
// 3. Retry as data — none expressed by this companion. HttpClient's
//    `SocketsHttpHandler` can be configured for retry / timeout if
//    the deployment needs it; the companion stays pass-through to
//    keep failure semantics legible to the caller.
// 4. Stateless between calls — the cached `HttpClient` carries only
//    base address + the optional namespace default header; the token is
//    applied per request (from `TokenProvider`, or the constant `Token`),
//    so no per-request auth state survives across method calls.
//    Distributed-safe: any node with an equivalent token provider
//    produces identical results.
// 5. No cross-shard ordering — Vault makes no global ordering promise
//    across paths, and this companion makes none either. `ListKeys`
//    order is whatever Vault returns from the LIST endpoint.
// 6. Precision at the lower bound — N/A; `ISecretStore` has no timing
//    semantics.