# Credential rotation — the per-call credential-provider seam

A secret-bearing HTTP companion that **captures its credential at
construction** cannot survive out-of-band rotation. The client (SDK
client, `HttpClient`, options record) is built once at DI composition and
holds the first credential for the process lifetime; when the operator
rotates that credential out of band — an IAM key roll, a regenerated
Azure `AccountKey`, an expired SAS, a rolled GCP service-account key, a
renewed Vault token — the cached client keeps presenting the stale
credential. Every call then fails `401` / `403` with a cryptic
vendor-SDK exception, far from the config that set it, and the only
recovery is a process restart.

This is the **build-once / read-per-call seam mismatch**: a value that is
async / lazily rotated is snapshotted at construction but assumed to be
re-read per call.

## The canonical shape — a per-call provider closure

Give the companion's config an **optional provider closure**, defaulting
to the constant (today's behaviour, back-compat):

```fsharp
type MyCompanionConfig = {
    // ... static fields ...
    Secret: string
    /// `None` (default) = use the static `Secret`, captured once.
    /// `Some f` = call `f ()` per operation; the closure typically
    /// closes over `ISecretStore.GetSecret`, so a rotated secret is
    /// picked up without a restart.
    SecretProvider: (unit -> string) option
}
```

- **`None` is the anti-pattern-free default.** `fromEnv` wires `None`, so
  every existing deployment keeps its exact current behaviour — the
  credential is read once and held. Nothing changes until a deployment
  opts in.
- **`Some f` re-reads per call.** The closure closes over the secret
  backend (`ISecretStore.GetSecret`, an orchestrator-managed token file,
  a renew loop). The companion stays free of a *direct* dependency on the
  secret backend — the caller supplies the resolution (GP 12,
  interface-first).

Constructor-captured credentials are the anti-pattern. The provider
closure is the fix.

### Precedent — `PeerBearerAuthMiddleware`

`PeerBearerAuthMiddleware` already re-reads its bearer secret **per
request** rather than snapshotting it into a header at construction —
the same discipline, applied to the inbound side. The seam here
generalises that re-read-per-call precedent to every outbound
secret-bearing companion.

## Two application shapes

The right mechanics depend on how expensive re-reading + re-applying the
secret is.

### 1. Per-request header (cheap) — HTTP token companions

When the secret is just a header value, apply it per request. Do **not**
snapshot it into `HttpClient.DefaultRequestHeaders` — build a
`HttpRequestMessage` per call and set the header from the provider:

```fsharp
open System.Net.Http
open ToolUp.Secrets.HashiCorpVault

let resolveToken (config: VaultConfig) =
    match config.TokenProvider with
    | Some provider -> provider ()
    | None -> config.Token

let send (client: HttpClient) (config: VaultConfig) (method: HttpMethod) (path: string) (content: HttpContent) = async {
    use request = new HttpRequestMessage(method, path)
    request.Headers.TryAddWithoutValidation("X-Vault-Token", resolveToken config) |> ignore
    // ... send, and map 401/403 to a named error (see below) ...
}
```

`ToolUp.Secrets.HashiCorpVault.VaultSecretStore` (Phase 2c) is the
reference implementation.

### 2. Change-detection cache (expensive) — cloud-SDK clients

When re-applying the secret means **rebuilding a whole SDK client**
(`BlobServiceClient`, `StorageClient`, `AmazonS3Client`), a naive
per-call rebuild is wasteful — construction parses connection strings and
builds HTTP pipelines. Cache the client and rebuild **only when the
resolved secret changes**:

```fsharp skip=fragment
let gate = obj ()
let mutable cachedSecret = ""
let mutable cachedClient = Unchecked.defaultof<_>

let client () =
    let resolved =
        match config.SecretProvider with
        | Some provider -> provider ()
        | None -> config.StaticSecret
    lock gate (fun () ->
        if isNull (box cachedClient) || resolved <> cachedSecret then
            cachedClient <- buildClient resolved
            cachedSecret <- resolved
        cachedClient)
```

For the static (`None`) path this rebuilds exactly once — identical to
the original build-once behaviour. For the provider path it rebuilds only
on rotation. The `ToolUp.Storage.AzureBlobStorage` and
`ToolUp.Storage.GoogleCloudStorage` companions (Phase 2c) implement this
shape.

**TTL'd-cache middle ground.** For a hot path where even reading the
secret per call is too expensive (e.g. a network round-trip to the secret
backend on every request), cache the *resolved secret* itself behind a
short TTL with stale-fallback — the JWKS-cache shape: a 10-minute TTL plus
"serve the last-known-good value if the refresh fails". Rotation is then
picked up within one TTL window rather than instantly, trading recency for
call cost.

## Ambient credentials need no seam

Credentials resolved from an **ambient chain** that the vendor SDK
refreshes itself are already rotation-transparent — do not add a provider
seam, just document it:

- **AWS** default credential chain (env / profile / IMDS / EC2 instance
  profile / ECS-EKS task role) — the AWS SDK refreshes role / IMDS
  credentials internally.
- **GCP** Application Default Credentials (metadata server / workload
  identity) — the ADC chain refreshes tokens itself.
- **Azure** managed identity via `DefaultAzureCredential` (where a
  deployment uses it instead of a connection string).

The `ToolUp.Storage.AwsS3` companion is ambient-only and ships **no**
credential-provider seam for this reason. Prefer ambient / workload
identity over static keys wherever the platform allows it — it removes
the rotation problem entirely.

## Name the auth failure

When a request fails `401` / `403`, don't let the caller's
`EnsureSuccessStatusCode` throw an anonymous `HttpRequestException`. Map
it to a **named error** whose message names the offending env var and the
renewal path, so the failure is legible where it lands:

```
Vault rejected the request token (403) on '<path>' — VAULT_TOKEN has
expired or been revoked. Renew the token (or restart with a fresh
VAULT_TOKEN); if a TokenProvider is configured, ensure it returns a
currently-valid token.
```

## Make rotation observable

A health probe that only checks "the client constructs cleanly" cannot
see a revoked credential. Probe with a **live authenticated read/list**
against the configured backend, and make sure the probe path does not
swallow the auth failure:

- The Phase 2c storage health probes (`blob_storage:aws-s3` /
  `:azure` / `:gcs`) switched from `IBlobStorage.Exists` (which swallows
  every exception and returns `false` → Healthy) to `IBlobStorage.List`,
  which propagates a `403` and surfaces it as `Unhealthy` with the
  vendor's status message within one probe cycle.

## Checklist for a new secret-bearing companion

- [ ] Config carries an optional provider closure; `None` is the default
      and `fromEnv` wires it.
- [ ] The secret is applied **per call** (per-request header) or via a
      **change-detection cache** (SDK client) — never snapshotted at
      construction with no re-read path.
- [ ] `401` / `403` maps to a named error that names the env var + the
      renewal path.
- [ ] The health probe does a live authenticated read/list and does not
      swallow the auth failure.
- [ ] Ambient-credential companions document "rotation transparent" and
      deliberately ship no seam.

## See also

- `src/Secrets/HashiCorpVault/VaultSecretStore.fs` — per-request token seam.
- `src/Storage/AzureBlobStorage/` and `src/Storage/GoogleCloudStorage/` —
  change-detection cache; per-companion rotation contract in each README.
- `src/Storage/AwsS3Storage/README.md` — ambient / rotation-transparent.
</content>
</invoke>
