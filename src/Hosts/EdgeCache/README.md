# ToolUp.Hosts.EdgeCache

CDN / edge host adapter for ToolUp.Platform. Two vendor-neutral implementations of the
Phase 472 edge seams, with **no cloud SDK dependency** — BCL `HttpClient` only.

| Type | Implements | What it is |
|---|---|---|
| `HttpEdgeCache` | `IEdgeCache` | Purge over any authenticated HTTP purge API. |
| `CallbackUrlSigner` | `IDelegatedUrlSigner` | CDN-native signed URLs from the deployment's own signing callback. |

Production-ready, and stateless between calls, so an instance is safe to share across
requests and safe to recycle.

## Why these are vendor-neutral

Commercial purge APIs share one shape: an authenticated HTTP call carrying a list of paths
or tags as JSON. They differ in three places, and all three are supplied as **data** rather
than compiled in:

- the endpoint URL and HTTP method — values on `HttpEdgeCacheConfig`;
- the auth header name and scheme — `EdgeAuthScheme`, with the credential read from
  `ISecretStore` **on every call**, so a rotated token takes effect without a restart;
- the request body — a `EdgePurgeRequest -> string` callback, so the vendor's JSON shape
  lives in the deployment's own code.

Signing is the same argument taken further. A signing scheme needs a private key, and a
private key belongs to the deployment — so `CallbackUrlSigner` decides *what is being
granted* (which item, to which scope, until when, at which URL) and hands that to the
deployment's callback to sign.

## Purging

```fsharp
open System
open System.Net.Http
open System.Text.Json
open ToolUp.Platform
open ToolUp.Hosts.EdgeCache

let purgeBody (request: EdgePurgeRequest) =
    match request with
    | PurgeRequestPaths paths -> JsonSerializer.Serialize {| files = paths |}
    | PurgeRequestPrefix prefix -> JsonSerializer.Serialize {| prefixes = [ prefix ] |}
    | PurgeRequestTags tags -> JsonSerializer.Serialize {| tags = tags |}

let edge =
    HttpEdgeCacheConfig.create "media-edge" (Uri "https://api.example-cdn.test/v1/purge") purgeBody
    |> HttpEdgeCacheConfig.withCredential {
        SecretContainer = "_platform"
        SecretName = "cdn_purge_token"
        Scheme = EdgeBearerToken
    }
    |> HttpEdgeCacheConfig.withPrefixSupport
    |> HttpEdgeCacheConfig.withPropagation (PurgeEventualWithin(TimeSpan.FromMinutes 5.0))
    |> HttpEdgeCache.create httpClient (Some secretStore)
```

Then compose it — on the media library, on public rendering, or both:

```fsharp skip=fragment
MediaLibraryServerApp.create ()
|> MediaLibraryServerApp.withEdgeCache edge
|> ...
```

Three details worth knowing:

- **Declare only the capabilities the endpoint has.** `SupportsPrefix` and `SupportsTags`
  default to `false`, and a verb that is not supported returns
  `Error (PurgeNotSupported …)` rather than sending a request the API will reject — or,
  worse, one it silently accepts as a literal path.
- **It does not retry.** Retry is the caller's, expressed as an `EdgePurgeRetry` record and
  applied by `EdgeCache.purgeDetached` (GP 12 rule 3).
- **It does not parse response bodies.** An HTTP status is the whole contract it can
  honestly claim across vendors; 5xx becomes `PurgeTransportFailure` (retryable), 4xx
  becomes `PurgeRejected` (not). The status line is carried into the error verbatim.

## Signing

```fsharp skip=fragment
let signer =
    CallbackUrlSignerConfig.create "media-edge-signer" "https://media.example.test" (fun request -> async {
        // The deployment's own key + canonicalisation.
        let signature = signWithDeploymentKey request.UnsignedUrl request.ExpiresAt
        return Ok(sprintf "%s?Expires=%d&Signature=%s" request.UnsignedUrl (request.ExpiresAt.ToUnixTimeSeconds()) signature)
    })
    |> CallbackUrlSigner.create

MediaLibraryServerApp.create ()
|> MediaLibraryServerApp.withDelegatedUrlSigner signer
|> ...
```

`IMediaLibrary.SignedUrl` then mints that URL instead of an origin-relative
`/media/signed/{id}?token=` one. The origin HMAC route stays mounted and keeps verifying,
so tokens minted before the switch work until they expire and removing the signer restores
the prior behaviour exactly.

Two behaviours are deliberate and are pinned by the SDK's contract pack:

- **The expiry is rounded DOWN** to the precision the signer declares (`TtlPrecision`).
  Rounding up would silently extend a grant past what the caller asked for.
- **A failing callback is an error, never a fall-through** to the origin HMAC. Falling back
  would hand a CDN-fronted viewer an origin-relative URL it cannot reach — a broken link
  dressed as a success — and would make a permanently broken signer indistinguishable from
  a working one.

## See also

- `docs/platform/edge-serving.md` — origin vs CDN topology, and what to purge when.
- `docs/migrations/472-edge-cache-seam.md` — what changes for an existing deployment.
