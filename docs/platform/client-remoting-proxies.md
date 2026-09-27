# Client Remoting proxy convention

Every `*.Client` companion in this SDK constructs its ToolUp.Remoting proxy (built via `Remoting.buildProxy` under the `ToolUp.Remoting.Client` namespace — the in-tree transport keeps the upstream Fable.Remoting API shape under the renamed namespace) as a **module-level value**, not as a per-call function. Header freshness — identity, CSRF token, anything else the deployment attaches to outgoing requests — is owned by the **send-time request-guard** the SDK installs at the XHR + fetch seam, not by the proxy's construction-time customiser.

This convention is load-bearing for codebase consistency and contributor onboarding. Read this page before adding a new `*.Client` companion or refactoring an existing one.

## Module-level is canonical

```fsharp
// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
let private fooApi: IFooApi =
    Api.makeProxy<IFooApi> (customOptions = UserSession.withRequestHeaders)
```

The proxy is constructed once at module load. Every dispatcher call uses the same proxy value:

```fsharp
let private loadFooCmd () =
    Cmd.OfAsync.either fooApi.GetFoo () FooLoaded (fun e -> FooLoaded(Error e.Message))
```

No `()` on the binding; no `()` at the call sites; no wrapping parens around `(fooApi ())`.

**Why module-level is preferred:**

1. **One construction, faster boot.** `Api.makeProxy` does a small reflection pass over the API interface to build the dispatcher; doing it once per process beats doing it once per call.
2. **One canonical re-introduction point for any future header-snapshot defect.** If someone ever changes `UserSession.withRequestHeaders` to splice header state, the defect surfaces uniformly at the customiser file — not silently across every per-call site that "looks defensive".
3. **Reads as a value, not as a thunk.** Calling code says `fooApi.Method args` — the same shape as any other module-level Remoting client. New contributors don't have to grok why one type of API is "called twice" (once to construct, once to invoke).
4. **Uniform with the rest of the SDK.** The Category-B proxies (`FormsClient`, `AIAssistantUI`, `AISettingsUI`, `AIClientConfig`, `KnowledgeBase/ClientModel`, `KnowledgeBase/PlatformKnowledgeAdminUI`) have always been module-level; the rest of `ToolUp.Platform.Client/Client/` has been converged to match.

## Why this is safe — the send-time seam

Module-level construction is safe because **the customiser is a no-op passthrough**, and the actual identity + CSRF headers are attached at *send* time by `CsrfClient`'s request-guard reading live caches per request:

```fsharp
// In ToolUp.Platform.Client/Client/UserSession.fs:

/// Kept for source compatibility (every `Api.makeProxy
/// (customOptions = UserSession.withRequestHeaders)` call site). Both
/// the identity headers AND `X-CSRF-Token` are now attached at *send*
/// time by the `CsrfClient` request-guard (the single seam, over XHR +
/// fetch), reading the live caches per request. This no longer splices
/// a frozen `Remoting.withCustomHeader` list — that proxy-build-time
/// freeze is exactly what caused 401/403 under
/// `DefaultSecurityHardening` for proxies built before sign-in / the
/// CSRF prefetch. Passthrough.
let withRequestHeaders (options: RemoteBuilderOptions) = options
```

The request-guard itself is installed by `SDK.Client.fs`'s `installRequestGuard` at app boot, which wraps the browser's `fetch` (the proxy's default transport since Phase 855) and `XMLHttpRequest` (the opt-out transport) to attach `identityHeaderPairs ()` (re-reads per call) and the CSRF token (re-reads from `CsrfClient`'s cache per call) before every outgoing `/api/*` request. On the `fetch` path the headers are set on the request's own `Headers` object; the `XMLHttpRequest.prototype` wrap is only reached by a proxy that opted back into XHR.

So: the proxy's construction-time customiser doesn't matter for header freshness — the work happens at the wire, not in the proxy's options record.

## Guard-owned header keys — don't set them anywhere else

The request-guard owns four header keys: `Authorization`, `X-User-Id`, `X-CSRF-Token`, and `x-correlation-id`. It is the **single sanctioned seam** for them. Do not set those keys via `Remoting.withAuthorizationHeader`, `Remoting.withCustomHeader`, or a raw `xhr.setRequestHeader` / `fetch` headers object:

- A value set at proxy-build time is **frozen** — it goes stale on sign-in / token refresh.
- The guard tracks **every** header write (it wraps `XMLHttpRequest.prototype.setRequestHeader` as well as `fetch` headers), and it defers to a header the request already carries rather than appending a duplicate. So a stale proxy-set `Authorization` doesn't produce a malformed double header — it produces a request that keeps sending the stale token, which 401s in a way that looks random.

The doc-comments on `Remoting.withAuthorizationHeader` / `withCustomHeader` carry the same warning at the call site. Use those helpers only for app-specific keys the guard doesn't own, or on proxies that deliberately target an origin the guard excludes.

One more guard behaviour worth knowing: a state-changing request issued before the CSRF token cache is populated **waits (up to 2s) on the shared in-flight token fetch** before dispatching — a fast first click after paint no longer 403s. Both transports wait: the `fetch` wrapper awaits the token before calling through, and the XHR wrapper defers the real `send()` while the request is still open.

## When per-call would be needed — re-introducing a snapshot customiser is a regression

The per-call construction pattern (`let private fooApi () = …`) was the workaround for a now-fixed defect where `UserSession.withRequestHeaders` spliced live header state into the options record at construction time, freezing whatever was cached at that moment. That defect is gone. Workaround comments still appearing in pre-2026-05 history are descriptive of an architecture that no longer exists.

**Per-call is correct again only if** a future change re-introduces a header-snapshot customiser. That would itself be a regression worth discussing on its own merits — the right place to relitigate the trade-off is the customiser's PR review, not a defensive scattering across consumer modules. If that change ever lands, update this doc in the same PR and bring the per-call shape back along with the rationale.

**Don't** add per-call construction "just to be safe" against a hypothetical future change. The docstring on `UserSession.withRequestHeaders` and this convention doc are the canonical defence against quiet re-introduction. Defensive ceremony scattered across consumer modules is the wrong layer.

## Special case: class-body bindings

`ToolUp.Platform.Client/Client/ModuleQueryClient.fs`'s proxy lives inside a `type ClientModuleQueryBus(registry) = …` class body rather than at module scope. Same shape applies (drop the `()`, add the type annotation) — F# evaluates the `let` once per class instance, and the bus itself is constructed once per app at the composition root. End result: the proxy is built once per app, same as a module-level value.

```fsharp skip=fragment
type ClientModuleQueryBus(registry: Map<string, Map<string, ModuleQueryHandler>>) =
    // Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
    // Constructed once per ClientModuleQueryBus instance (the bus itself is a singleton in the composition root).
    let remoteApi: IModuleQueryBusApi =
        Api.makeProxy<IModuleQueryBusApi> (customOptions = UserSession.withRequestHeaders)
    ...
```

If a future class-bound proxy author is tempted to write `let remoteApi () : … = …` as a per-call function inside a class body by analogy with the old workaround, the same logic applies: don't.

## Authoring checklist for new `*.Client` companions

When you add a new `*.Client` companion:

1. Construct the proxy as a module-level value (or, for class-bound proxies, a class-body `let` without `()`).
2. Annotate with the interface type so a future grep can find every Remoting proxy by type signature.
3. Add the one-line canonical pointer comment above the proxy.
4. Use the standard customiser: `(customOptions = UserSession.withRequestHeaders)` — this is the SDK's pinned passthrough; pinning is the contract.
5. Don't add per-call construction. Don't add a header-snapshot customiser.

If any of these feel wrong for your use case, that's a conversation worth having on a PR review — not a unilateral deviation in your companion.

## Declared reads — in-flight sharing and stale-while-revalidate (Phase 854)

A proxy sends every call it is asked for, unless the method is **declared** a read. Two policies,
both opt-in per method (GP 11), both a **client** policy — the server's dispatcher reads neither
attribute and serves a declared method exactly as it serves an undeclared one:

| Declaration | What the client proxy does |
|---|---|
| `[<Cacheable(maxAgeSeconds)>]` | Identical calls in flight at once share **one** request, and every caller receives its result. A result is then served for `maxAgeSeconds` without waiting on the network; each such hit starts a background refresh. `Cacheable(0)` shares in-flight calls and caches nothing. |
| `[<Invalidates("GetA", "GetB")>]` | A **successful** call drops the named reads' cached results and detaches their in-flight requests, so the next read goes to the server. The method itself is never shared: two identical mutations are two requests. |
| neither | Unchanged, byte for byte — never shared, never cached, its own set of interceptor events per call. |

"Identical" means identical on the wire: the same HTTP method, URL, proxy headers and request body.
The table is one per page, not one per proxy — the shell builds several proxies of one API record,
and the second caller the policy exists for is usually on another one. Multipart uploads are never
shared (their bodies cannot be compared). Declare `Cacheable` only on a method with no side effects.

### Declaring it — the attribute, and the data a Fable client registers

The attributes (`ToolUp.Platform`, beside `[<RequiresRole>]` and the rest) go on the shared API
record. **Fable's reflection carries no custom attributes**, so a browser proxy cannot read them off
the record at runtime; the client registers the same declaration as data, once, at composition:

```fsharp skip=fragment
// Shared — compiled by both hosts.
type CatalogApi = {
    [<Cacheable(60)>] GetCatalog: unit -> Async<Catalog>
    [<Invalidates("GetCatalog")>] AddItem: Item -> Async<unit>
}

let catalogReadPolicies = [
    "GetCatalog", ReadPolicy.cacheable 60
    "AddItem", ReadPolicy.invalidates [ "GetCatalog" ]
]

// Client composition — before or after the proxy is built; the table is read per call.
ReadPolicies.registerFor<CatalogApi> catalogReadPolicies

// A .NET test keeps the two declarations from drifting.
Expect.equal (ReadPolicies.ofAttributes typeof<CatalogApi>) catalogReadPolicies "declared once"
```

`ReadPolicies.ofAttributes` is .NET-only; it is the drift check, not a Fable code path. The forge's
own pinned example is `src/ToolUp.Platform.Tests/Remoting/ReadPolicyFixture.fs`, which both test
packs compile.

### What a consumer sees

- **Plain `Async`** (`let! x = api.GetCatalog ()`): a shared call returns the shared result; a
  cache hit returns **synchronously**, and the refresh updates the cache for the next caller.
- **`Cmd.OfRemoting.call` / `callWithName`**: a cache hit dispatches `ofSuccess cached`, then — when
  the refresh lands — `ofSuccess fresh` as an ordinary second message. A refresh that fails
  dispatches nothing more: the served value stands, the interceptor chain's `OnError` observes the
  failure, and the stale entry is evicted so the next call goes to the server. `callWithRetry`
  receives the shared or cached value but not the second message.
- **Interceptors observe requests, not call sites.** The first `Cmd.OfRemoting` call to reach a
  shared request owns its chain: `OnCalling` once, then `OnSuccess` or `OnError` once, and every
  waiting call receives that chain's outcome — an `OnError` replacement exception included. A cache
  hit sends nothing, so fires nothing; its refresh is a request and fires the chain.

### Identity

The request guard attaches the caller's identity at send time, below the table, so a cached result
belongs to the identity that fetched it. `UserSession.setAuthToken` (for a different subject, or one
whose subject cannot be read) and `UserSession.clearAuthToken` call `ReadPolicies.clear`. A
deployment that changes identity another way calls it too. `ReadPolicies.invalidate` is there for a
change the client learns of some other way (a notification, say).

With nothing registered, `Cmd.OfRemoting` runs its pre-854 path exactly — the check is one count.
Migration notes and the measured before/after: [`docs/migrations/854-client-read-policies.md`](../migrations/854-client-read-policies.md).

## The transport, and same-tick batching (Phase 855)

### `fetch` is the default transport

Every proxy request goes out through `fetch`. What a call sends is unchanged — the same URL, method,
headers and body, cookies exactly as XHR sent them without `withCredentials` (`credentials:
"same-origin"`; `withCredentials = true` is `"include"`) — and so is what a caller sees: a network
failure is still a `ProxyRequestException` with status 0, which is how XHR reported it, and every other
status reaches the same error categorisation. Connection reuse (HTTP/1.1 keep-alive, HTTP/2
multiplexing) is the browser's behaviour for every `fetch`; the `keepalive` request flag is
deliberately **not** set — it lets a request outlive the page, and the browser caps the bodies of all
in-flight `keepalive` requests at 64 KiB together, so it would refuse every large upload.

A consumer that needs the old transport opts out page-wide, once, at composition:

```fsharp skip=fragment
Http.useTransport Http.Transport.Xhr
```

Streaming methods (`RemoteStream`) always used `fetch` and are unaffected.

### Same-tick calls travel as one request — opt in on both sides

Batching is off until both halves are composed (GP 11):

```fsharp skip=fragment
// Server composition root — serves POST /api/_batch.
ServerApp.empty |> ServerApp.withRemotingBatching // ...the rest of the composition

// Client composition — once, before or after the proxies are built.
RemoteBatching.enable RemoteBatching.DefaultRoute
```

From then on a proxy call is queued when it is made, and at the next microtask boundary every queued
call is sent: **one plain request when a call is alone** (exactly the request it sends today), **one
envelope when more than one is pending** — `POST /api/_batch` with a JSON array of `{ route, body }`.
The server runs each element through the rest of its pipeline on a request of its own and answers
`{ status, body }` per element, in order; each call's promise resolves from its element through the
same decode and error handling a response of its own would have taken.

| Guarantee | How it holds |
|---|---|
| Each element is authorised, rate-limited, audited, idempotency-checked and validated individually | the element is a request of its own through scope resolution, surface enforcement, the inbound rate limiters and the dispatcher's per-call seams; the CSRF middleware (which sits ahead of the batch point) is re-applied per element by `ServerApp.withRemotingBatching` |
| A refusal of one element is that element's, not the envelope's | the element's `status` carries it (401 / 403 / 429 / 400 …); the others are served |
| Streaming and long-running methods are never batched | the server refuses a whole envelope naming such a route (or a long-running method's `/status`, `/progress`, `/cancel`) with `400 remoting_batch_refused` before running anything, and the client then re-sends those calls one request each |
| No duplicates in an envelope | the Phase 854 read table runs first: identical in-flight declared reads are one call before anything is queued |
| Calls with different headers never share an envelope | calls are grouped by base URL, credentials and the proxy's header list |

Not batched: multipart uploads, binary (`withBinarySerialization` / `byte[]`-returning) methods, and
streaming. Elements run one after another on the server, in order. An envelope carries at most 32
calls (`RemotingBatchOptions.MaxElements` on the server, `RemoteBatching.enableWith` on the client);
a larger tick is sent as several envelopes.

**What "same tick" catches today.** Calls started in the same JavaScript task. An Elmish
`Cmd.OfAsync` / `Cmd.OfRemoting` command currently starts its async behind a `setTimeout` hop, one
per command, so commands issued by one `update` start in separate tasks and are not coalesced until
that hop is removed (Phase 851's "no timer hop"); calls started directly (`Async.StartImmediate`,
promise-returning proxies, several calls inside one async) are coalesced now.

Migration notes and the measured before/after: [`docs/migrations/855-fetch-and-batching.md`](../migrations/855-fetch-and-batching.md).
