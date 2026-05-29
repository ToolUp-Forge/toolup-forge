module ToolUp.Secrets.HashiCorpVault

open System
open System.Net.Http
open System.Text
open System.Text.Json
open ToolUp.Platform.Secrets

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
// `X-Vault-Token` on every request and is read fresh from
// `VAULT_TOKEN` on each `fromEnv` construction — rotating tokens
// requires restarting the process or re-running `fromEnv` after a new
// env var has landed. AppRole / Kubernetes / OIDC auth methods are
// follow-up enhancements: each issues a Vault token that the operator
// pipes into `VAULT_TOKEN` via their orchestrator's secret-injection
// flow.
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
}

module VaultConfig =
    let defaults = {
        Address = ""
        Token = ""
        Namespace = None
        MountPath = "secret"
    }

// ─── HTTP plumbing ───────────────────────────────────────────────────

module private Http =
    // Lazy singleton — `HttpClient` is meant to be reused across the
    // process lifetime per Microsoft's guidance. One per config (keyed
    // by base address) is sufficient; ToolUp deployments use one
    // Vault per process.
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
        client.DefaultRequestHeaders.Add("X-Vault-Token", config.Token)

        match config.Namespace with
        | Some ns when not (String.IsNullOrWhiteSpace ns) -> client.DefaultRequestHeaders.Add("X-Vault-Namespace", ns)
        | _ -> ()

        client

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
        let! response = client.GetAsync path |> Async.AwaitTask

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
        use content = new StringContent(json, Encoding.UTF8, "application/json")
        let! response = client.PostAsync(path, content) |> Async.AwaitTask
        return response
    }

    let deletePath (path: string) = async {
        // DELETE on the metadata path wipes ALL versions + metadata;
        // GetSecret returns 404 immediately. This matches the
        // ISecretStore "delete then get returns None" contract.
        let! response = client.DeleteAsync path |> Async.AwaitTask
        return response
    }

    let listKeys (path: string) = async {
        // Vault uses the non-standard HTTP LIST verb; most HTTP
        // clients route LIST as a custom method. The friendlier
        // alternative is `GET {path}?list=true` which Vault treats
        // identically.
        let listUrl = $"{path}?list=true"
        let! response = client.GetAsync listUrl |> Async.AwaitTask

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
//    base address + default headers (token, optional namespace); no
//    per-request state survives across method calls. Distributed-
//    safe: any node with the same token produces identical results.
// 5. No cross-shard ordering — Vault makes no global ordering promise
//    across paths, and this companion makes none either. `ListKeys`
//    order is whatever Vault returns from the LIST endpoint.
// 6. Precision at the lower bound — N/A; `ISecretStore` has no timing
//    semantics.