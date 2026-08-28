# Phase 472 — `IEdgeCache` + delegated URL signing (consumer migration)

**What changes.** Two new opt-in seams and one new options record:

- **`IEdgeCache`** (`ToolUp.Platform.Server`) — purge by path / prefix / tag, with `NoopEdgeCache`
  as the declared no-op. Composed on public rendering (`withEdgeCache`) and/or the media library
  (`withEdgeCache`), a publish or a media upload / delete fans the affected origin-relative paths
  out to the CDN, fire-and-forget.
- **`IDelegatedUrlSigner`** (`ToolUp.MediaLibrary.SignedUrl`) — mint a CDN-native signed URL from the
  deployment's own signing callback instead of an origin-relative `/media/signed/{id}?token=` one.
- **`MediaLibraryOptions.EdgeCache`** — a per-response-class `Cache-Control` declaration for the
  media routes.

A new companion, **`ToolUp.Hosts.EdgeCache`**, ships vendor-neutral reference implementations of
both seams (BCL `HttpClient` only, no cloud SDK).

**Scope.** Additive. A deployment that composes none of it emits no new headers, schedules no
purges, and mints exactly the URLs it minted before — the media routes' response bytes and headers
are byte-for-byte unchanged (GP 11 / GP 13). **Nothing here requires a consumer edit.** The two
diffs below are the only source changes any consumer needs, and only if it hits the case.

## 1. If you construct `MediaLibraryOptions` positionally

The record gained one field, `EdgeCache`, so the compiler-generated constructor widened from 8
arguments to 9. This is the same ripple Phase 469 and Phase 471 produced; a record-expression
construction is unaffected.

```fsharp
// Before — positional, 8 args:
let options =
    MediaLibraryOptions(
        2_147_483_648L, mimeTypes, TimeSpan.FromHours 1.0, true,
        1_048_576, 8_388_608, TimeSpan.FromHours 24.0, false)
```

```fsharp
// After — add the edge declaration. `defaults` declares nothing, which
// emits no Cache-Control on any media route (the pre-472 behaviour).
let options =
    MediaLibraryOptions(
        2_147_483_648L, mimeTypes, TimeSpan.FromHours 1.0, true,
        1_048_576, 8_388_608, TimeSpan.FromHours 24.0, false,
        MediaEdgeCacheOptions.defaults)
```

**Better: use a record expression instead**, which never needs this edit again:

```fsharp
let options = {
    MediaLibraryOptions.defaults with
        MaxBytes = 2_147_483_648L
}
```

## 2. If you construct `DefaultMediaLibrary` directly

The primary constructor gained two arguments (`edgeCache`, `delegatedSigner`). **Both prior arities
are preserved as explicit secondary constructors**, so no existing call site changes — the 7-argument
(pre-471) and 8-argument (471) shapes both still compile and both resolve to "no edge cache, no
delegated signer".

Most consumers do not construct it at all; `MediaLibraryServerApp.run` does.

## 3. If you construct `PublicRenderingServerApp` positionally

The record gained `EdgeCache` and `EdgeCachePathsForSlug`, both `None` by default. Consumers build
it with `PublicRenderingServerApp.create ()` and `with*` helpers, so this is a theoretical case;
noted for completeness because the public-API baseline records the widened constructor.

## Adopting the edge (optional)

```fsharp skip=fragment
open ToolUp.Platform
open ToolUp.Hosts.EdgeCache

// A purge adapter over your CDN's HTTP purge API. Endpoint, auth scheme
// and body shape are yours; the credential is read from ISecretStore on
// every call, so rotation needs no restart.
let edge =
    HttpEdgeCacheConfig.create "media-edge" purgeEndpoint renderPurgeBody
    |> HttpEdgeCacheConfig.withCredential {
        SecretContainer = "_platform"
        SecretName = "cdn_purge_token"
        Scheme = EdgeBearerToken
    }
    |> HttpEdgeCacheConfig.withPrefixSupport
    |> HttpEdgeCache.create httpClient (Some secretStore)

MediaLibraryServerApp.create ()
|> MediaLibraryServerApp.withOptions {
    MediaLibraryOptions.defaults with
        EdgeCache = MediaEdgeCacheOptions.cdnEncrypted
}
|> MediaLibraryServerApp.withEdgeCache edge
|> MediaLibraryServerApp.run
```

## Two compose-time refusals to know about before you declare

Both abort startup (`ValidationResult.Error` from `media_library:options`) rather than warn, because
the symptom of getting either wrong is "someone else can watch the video" and it is invisible from
inside the deployment:

- **`EdgeCache.Original = EdgePublic …` is refused.** Both routes serving the original are gated —
  one by scope, one by signature — so a shared cache holding the response serves it to callers who
  passed neither gate. Use `EdgePrivate` or leave it unset.
- **`EdgeCache.Manifest = EdgePublic …` with `EncryptHlsByDefault = true` is refused.** An encrypted
  manifest is rewritten per request (Phase 471 makes its `#EXT-X-KEY` URI origin-absolute and carries
  any `?token=` onto it), so a shared cache would hand one viewer's token to the next.
  `MediaEdgeCacheOptions.cdnEncrypted` already leaves it unset. An **unencrypted** manifest is
  returned byte-for-byte and caches safely.

The HLS key route (`/api/media/hls-key/{id}`) has no knob at all — it is hard-wired `no-store` and
`MediaEdgeCacheOptions` has no field for it.

## Verification

1. `dotnet build` your solution — a positional `MediaLibraryOptions` construction is the only thing
   that can break, and it breaks at compile time with a named arity error.
2. Start the app. If you declared an edge posture, the `media_library:options` preflight runs the
   two refusals above; a clean start means neither fired.
3. With no edge composed, confirm nothing changed: `curl -I` a media route and check there is **no**
   `Cache-Control` header, and that `GET /media/signed/{id}?token=…` still serves.
4. With an edge composed, publish a page (or delete a media item) and confirm the purge reached your
   CDN's dashboard/API log. A failed purge is a `Warn` line naming the edge, never an error on the
   publish.

## Rollback

Remove the `withEdgeCache` / `withDelegatedUrlSigner` calls and set
`EdgeCache = MediaEdgeCacheOptions.defaults`. That restores the pre-472 behaviour exactly — no
purges, no `Cache-Control` on media routes, and origin-minted signed URLs. Tokens already minted by a
delegated signer stop being produced but any origin token still verifies, because the origin route
and its verification were never removed.

## See also

- [`../platform/edge-serving.md`](../platform/edge-serving.md) — origin vs CDN topology and what to
  purge when.
- `src/Hosts/EdgeCache/README.md` — the reference sub-companion.
