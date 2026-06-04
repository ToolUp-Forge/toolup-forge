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
// In ToolUp.Platform.Client/Client/UserSession.fs (line 342):

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

The request-guard itself is installed by `SDK.Client.fs`'s `installRequestGuard` at app boot, which patches the browser's `XMLHttpRequest` and `fetch` to attach `identityHeaderPairs ()` (re-reads per call) and the CSRF token (re-reads from `CsrfClient`'s cache per call) before every outgoing `/api/*` request.

So: the proxy's construction-time customiser doesn't matter for header freshness — the work happens at the wire, not in the proxy's options record.

## When per-call would be needed — re-introducing a snapshot customiser is a regression

The per-call construction pattern (`let private fooApi () = …`) was the workaround for a now-fixed defect where `UserSession.withRequestHeaders` spliced live header state into the options record at construction time, freezing whatever was cached at that moment. That defect is gone. Workaround comments still appearing in pre-2026-05 history are descriptive of an architecture that no longer exists.

**Per-call is correct again only if** a future change re-introduces a header-snapshot customiser. That would itself be a regression worth discussing on its own merits — the right place to relitigate the trade-off is the customiser's PR review, not a defensive scattering across consumer modules. If that change ever lands, update this doc in the same PR and bring the per-call shape back along with the rationale.

**Don't** add per-call construction "just to be safe" against a hypothetical future change. The docstring on `UserSession.withRequestHeaders` and this convention doc are the canonical defence against quiet re-introduction. Defensive ceremony scattered across consumer modules is the wrong layer.

## Special case: class-body bindings

`ToolUp.Platform.Client/Client/ModuleQueryClient.fs`'s proxy lives inside a `type ClientModuleQueryBus(registry) = …` class body rather than at module scope. Same shape applies (drop the `()`, add the type annotation) — F# evaluates the `let` once per class instance, and the bus itself is constructed once per app at the composition root. End result: the proxy is built once per app, same as a module-level value.

```fsharp
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
