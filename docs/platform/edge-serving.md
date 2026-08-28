# Edge serving — origin, CDN, and what to purge when

A deployment serving bytes at scale puts a CDN in front of the origin. Once it does, three things
that used to be true stop being true, and each one is silent:

1. **Your caches are no longer the only caches.** A republished page or a replaced media rendition
   keeps being served from an edge node until that node's TTL expires. The origin is correct; the
   viewer sees last week's page.
2. **Your gate is no longer on the path.** A viewer served from an edge never reaches the origin, so
   nothing verifies the origin's signed-URL token. An object cached at a public edge is public.
3. **Nothing you did not declare is decided by you.** A response carrying no `Cache-Control` is
   cached according to whatever heuristic the CDN applies — which may be "not at all", and may be
   "forever".

Phase 472 adds two seams for the first two, and a per-response-class declaration for the third.
Everything here is opt-in and defaults to the pre-472 behaviour exactly (GP 11 / GP 13): a
deployment that composes nothing emits no new headers, schedules no purges, and mints the same URLs
it minted before.

## The two topologies

**Origin-only.** Requests reach your server. `IRenderCache` (Phase 84) and the media range handlers
serve them; the origin's own caches are the only caches. Nothing in this guide is needed.

**CDN in front.** Requests reach an edge POP first. A hit is served without touching you; a miss is
forwarded. Two consequences drive everything below: **you must be able to tell the edge to forget
something**, and **you must be able to gate a request the origin never sees.**

## `IEdgeCache` — purge only, never read-through

```fsharp skip=signature
type IEdgeCache =
    abstract Name: string
    abstract Propagation: EdgePurgePropagation
    abstract PurgePaths: paths: string list -> Async<Result<unit, EdgePurgeError>>
    abstract PurgePrefix: prefix: string -> Async<Result<unit, EdgePurgeError>>
    abstract PurgeTags: tags: string list -> Async<Result<unit, EdgePurgeError>>
```

There is no `Get` and no `Set`, deliberately. The CDN caches; the SDK invalidates. A read path here
would make the SDK a second, weaker implementation of what the CDN already does well, and would put
a network hop back on the serve path — which is the thing an edge exists to remove.

Three properties are worth stating because callers depend on them:

- **Failure is data.** Every verb returns `Result`, never an exception. A purge is a call to someone
  else's network, and a caller has to be able to decide what a failure means.
- **`PurgeNotSupported` is not a silent success.** An edge with no tag invalidation says so. A `Ok`
  there would read as "the tagged objects are gone" when they are not.
- **Propagation is declared, not assumed.** `PurgeEventualUnbounded` is the honest default. Nothing
  here promises an object is gone from every POP when the `Async` completes, because no CDN offers
  that.

### Purging never blocks the request path (GP 7)

Every in-tree caller purges through `EdgeCache.purgeDetached`, which schedules the purge and returns.
A CDN outage must not turn a successful publish into a failed one — the publish already committed,
and a failed purge is a stale edge object that expires on its own TTL. Retry is data
(`EdgePurgeRetry`), and a terminal failure is written to the logger, not raised.

An **absent** edge cache, the **declared no-op**, and an **empty** purge set all short-circuit before
anything is scheduled, so an unconfigured deployment pays nothing.

## Composing an edge

```fsharp skip=fragment
// Public rendering: a publish purges the origin render cache, then the edge.
PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withRenderCache (InMemoryRenderCache.create ())
|> PublicRenderingServerApp.withEdgeCache edge
|> ...

// Media: an upload or a delete purges the item's edge objects.
MediaLibraryServerApp.create ()
|> MediaLibraryServerApp.withEdgeCache edge
|> ...
```

`ToolUp.Hosts.EdgeCache` ships `HttpEdgeCache`, a vendor-neutral adapter over any authenticated HTTP
purge API — the endpoint, auth header and request body are supplied as data, and the credential is
read from `ISecretStore` on every call so a rotated token needs no restart. See that package's
README.

`NoopEdgeCache.create ()` composes a declared no-op, which is free and is a useful explicit statement
that a deployment has no edge.

### Ordering: origin cache first, edge second

`withEdgeCache` wraps the composed `IRenderCacheInvalidation` rather than adding a second call at the
publish site, and the wrap is what fixes the order. Purging the edge first would let an edge node
re-fetch while the origin render cache still held the stale render — repopulating the edge with
exactly the bytes the purge was removing.

Composing an edge with **no** render cache is a perfectly ordinary CDN-fronted shape (the edge *is*
that deployment's cache), and it works: the wrap falls back to a no-op origin purge so the fan-out
still rides the one publish hook.

## What to purge, when

| Event | What is purged | Verb |
|---|---|---|
| A page is published or republished | `/{slug}` **and** `/{slug}/` | `PurgePaths` |
| A media item is uploaded | `/api/media/hls/{id}/` | `PurgePrefix` |
| | `/api/media/stream/{id}`, `/media/signed/{id}` | `PurgePaths` |
| A media item is deleted | the same set as an upload | both |

Two details that look like implementation trivia and are not:

- **Both slash variants.** A CDN keys on the request URI as received, so `/hello` and `/hello/` are
  two edge objects even where the origin routes them to one page. Purging only the form the SDK
  happens to spell leaves the other stale — invisibly, because whoever checks types one of the two.
  Override the mapping with `withEdgeCachePathsForSlug` when your public URLs are not `"/" + slug`.
- **The rendition is a prefix, not a path list.** An HLS rendition is an arbitrary number of segment
  files. Enumerating them would mean listing blob storage from a path that must not block, and the
  list would be wrong the moment a re-transcode changed the segmentation. If your purge API has no
  prefix verb, `HttpEdgeCache` reports `PurgeNotSupported` rather than guessing.

## Knowing whether your purges are landing

A purge is fire-and-forget: it never blocks the publish that triggered it, and a terminal failure
lands as one `Warn` line naming the edge. That is enough to diagnose a purge you already suspect and
nothing at all to *notice* one you do not — a mis-credentialed or rate-limited adapter fails
identically on every publish, forever, while pages and media keep serving stale from the edge.

Three counters close that gap. They are emitted at the single point every in-tree purge passes
through, so nothing you compose changes where they come from:

| Metric | Kind | Tags |
|---|---|---|
| `toolup.edge.purge.attempted` | counter | `edge` |
| `toolup.edge.purge.succeeded` | counter | `edge` |
| `toolup.edge.purge.failed` | counter | `edge`, `class` |

`edge` is the adapter's own `Name` — the same token the `Warn` line prints, so a dashboard and a log
search agree about which edge. `attempted` counts **purges, not retry attempts**, so it is the
denominator of the other two.

`class` is the failure's remedy, not its status code:

| Class | What it means | What to do |
|---|---|---|
| `transport` | the endpoint could not be reached (DNS, TLS, timeout, 5xx) | usually transient; alert on a sustained rate |
| `auth` | the endpoint refused the credential (401 / 403 / 407) | rotate or re-scope the secret |
| `rate-limit` | the endpoint refused on quota (429) | purge less, or widen a path set into one prefix |
| `unsupported` | this adapter does not offer the verb that was called | declare the capability, or purge with a verb it has |
| `other` | a rejection that could not be refined further | read the `Warn` line — it carries the adapter's own detail |

`auth` and `rate-limit` both arrive from an adapter as the same typed error (both are 4xx), so they
are separated by reading the HTTP status out of the rejection detail. An adapter that formats its
detail as `"<endpoint> returned <status> <reason>"` — which `HttpEdgeCache` does — gets that
refinement for free. One that formats it some other way is classified `other`, never as a guess at
one of the specific classes.

**Nothing is emitted unless you have composed a metrics endpoint.** With none composed, the
composition is byte-for-byte what it was without this: no wrapper object, no allocation, and the
declared no-op edge short-circuits before any of it is reached (GP 13). An operator running an
existing deployment sees the series appear the moment metrics are enabled, with no other change.

## Declaring cacheability on media routes

The media routes emitted no `Cache-Control` at all before this phase. `MediaLibraryOptions.EdgeCache`
declares one per response class:

```fsharp skip=fragment
{ MediaLibraryOptions.defaults with EdgeCache = MediaEdgeCacheOptions.cdnEncrypted }
```

`MediaEdgeCacheOptions.defaults` declares nothing — no header on any route, which is byte-for-byte
the pre-472 behaviour. Two worked postures are offered as starting points rather than defaults,
because a default that silently started caching would be exactly the accident this record exists to
prevent:

| Class | What it is | `cdnUnencrypted` | `cdnEncrypted` |
|---|---|---|---|
| `Segment` | HLS `.ts` / `.m4s` — never rewritten; ciphertext when encrypted | public | public |
| `Manifest` | HLS `.m3u8` | public, short | **unset** |
| `Poster` | derived stills — identical for every viewer | public | public |
| `Original` | `/api/media/stream/{id}` + `/media/signed/{id}` — gated | private | private |

### The three safety rules, and why they are enforced rather than documented

**A manifest is rewritten per request when the rendition is encrypted.** Phase 471 makes its
`#EXT-X-KEY` URI origin-absolute on the way out, and carries any `?token=` from the manifest request
onto it. A shared cache holding that response hands one viewer's token to the next. So
`cdnEncrypted` withholds the manifest, and composing `EncryptHlsByDefault = true` alongside an
`EdgePublic` manifest is **refused at startup** by the `media_library:options` validator.

An **unencrypted** manifest carries no key tag, comes back byte-for-byte, and is safe to cache.

**The original is always gated.** `/api/media/stream/{id}` requires a resolved scope;
`/media/signed/{id}` requires a valid signature and expiry. A shared cache holding either serves a
response that was authorised for someone else, so `EdgePublic` on `Original` is likewise refused at
startup. `EdgePrivate` — a browser may hold it, a shared cache may not — is the strongest posture
that is correct.

**The HLS key route cannot be declared at all.** `/api/media/hls-key/{id}` is hard-wired
`Cache-Control: no-store` + `Pragma: no-cache`, and `MediaEdgeCacheOptions` has no field for it. A
cached decryption key is the whole encryption scheme defeated, so the one response class where a
wrong declaration would be catastrophic is the one class a deployment cannot express.

Both refusals abort startup rather than warn. The symptom of getting either wrong is "someone else
can watch the video", and it is invisible from inside the deployment: the origin behaves perfectly,
and the exposure lives in a cache you do not own.

## Delegated URL signing — gating a request the origin never sees

The origin HMAC (`/media/signed/{id}?token=`) binds `(MediaId, ScopeId, Container, ExpiresAt)` and is
verified by this origin's range handler. That is exactly right when the origin serves the bytes, and
exactly wrong once a CDN does: the viewer never reaches the origin, so nothing verifies the token.

`IDelegatedUrlSigner` mints a URL the **edge** verifies instead:

```fsharp skip=signature
type IDelegatedUrlSigner =
    abstract Name: string
    abstract TtlPrecision: SignedUrlTtlPrecision
    abstract SignUrl: id: MediaId * scope: StorageScope * ttl: TimeSpan -> Async<Result<string, SignedUrlError>>
```

The seam takes **the deployment's signing callback** — there is no cloud SDK in the interface, the
same choice `ICloudTranscodeProvider` makes for transcoding. A signing scheme needs a private key,
and a private key belongs to the deployment; modelling key provisioning, rotation and per-vendor
canonicalisation inside the SDK would put the most security-critical code in the system furthest
from the people who own the key. `ToolUp.Hosts.EdgeCache.CallbackUrlSigner` is the reference
implementation.

Four behaviours to know:

- **It replaces minting, not verification.** The origin route stays mounted and keeps verifying its
  own tokens, so URLs minted before the switch work until they expire and removing the signer
  restores the prior behaviour exactly.
- **It replaces rather than supplements.** Minting both would leave an origin token issued and
  unrevoked — a second live grant nobody asked for.
- **A failing signer is an error, never a fall-through.** Falling back to the origin URL would hand a
  CDN-fronted viewer a link it cannot reach — a broken link dressed as a success — and would make a
  permanently broken signer indistinguishable from a working one.
- **The scope is passed to the callback.** A signer can bind the viewing tenant into its signature
  or its policy document, exactly as the origin HMAC does. One that ignores it has widened the gate;
  the seam is shaped so that is a visible choice rather than an impossible one.

## Checklist for putting a CDN in front of an existing deployment

1. Compose an `IEdgeCache` on the surfaces that publish — public rendering, media, or both.
2. Declare `MediaLibraryOptions.EdgeCache`. Start from `cdnEncrypted` if you encrypt renditions,
   `cdnUnencrypted` if you do not. Start conservative; the validator will refuse the two postures
   that leak.
3. Configure the CDN to **forward the `Range` header** and to key on it. Media serving is
   `206 Partial Content`; an edge that collapses ranges breaks seeking.
4. Configure it **not to cache** `/api/media/hls-key/*`. The origin says `no-store`; an edge
   configured to ignore origin directives on that path defeats encryption entirely.
5. If viewers are served from the edge, compose an `IDelegatedUrlSigner`. Until you do, a signed URL
   is verified only at the origin — which a CDN-fronted viewer does not reach.
6. Verify a purge end to end before trusting it: publish, then request the edge URL. `Propagation`
   tells you how long to wait, and an unbounded one means "no promise".

## See also

- [`storage.md`](storage.md) — `IBlobStorage` companions and the encryption-at-rest decorator.
- [`dynamic-ssr.md`](dynamic-ssr.md) — request-time content sources and the render cache the edge
  fan-out rides.
- [`portability-rules.md`](portability-rules.md) — the six rules both seams here satisfy.
