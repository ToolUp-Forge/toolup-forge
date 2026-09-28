# Phase 854 — client read policies: in-flight sharing and stale-while-revalidate

**What changes for you: nothing, until you declare a read.** Every proxy method is undeclared by
default and behaves byte for byte as before, interceptor events included; with no policy registered
anywhere, `Cmd.OfRemoting` runs its pre-854 path exactly. The SDK itself declares nothing in this
phase. The public surface grows additively: `CacheableAttribute` / `InvalidatesAttribute` in
`ToolUp.Platform.Core`, and `ReadPolicy` / `ReadPolicies` in `ToolUp.Platform.Client`.

## Adopting it

1. Put `[<Cacheable(maxAgeSeconds)>]` on a side-effect-free read and `[<Invalidates("Read", …)>]` on
   the mutations that make it stale, on the shared API record.
2. Register the same declaration on the client — Fable cannot read attributes at runtime:

   ```fsharp skip=fragment
   ReadPolicies.registerFor<CatalogApi> [
       "GetCatalog", ReadPolicy.cacheable 60
       "AddItem", ReadPolicy.invalidates [ "GetCatalog" ]
   ]
   ```

3. Pin the two equal in a .NET test:
   `Expect.equal (ReadPolicies.ofAttributes typeof<CatalogApi>) declarations "declared once"`.
4. Nothing to do for identity: since Phase 908 every identity route the SDK owns (tokens, subject
   kind, dev identity, token storage, the team switch, another tab) clears the cache through
   `UserSession.identityChanged`. Call that yourself only from an identity route of your own that
   bypasses `UserSession`.

An Elmish `update` that handles `ofSuccess` needs no change: a cache hit dispatches it twice (the
cached value, then the refreshed one), which is the point. Semantics in full:
[`docs/platform/client-remoting-proxies.md`](../platform/client-remoting-proxies.md#declared-reads--in-flight-sharing-and-stale-while-revalidate-phase-854).

## Verification

- `dotnet run --project Build.fsproj -- VerifyFable` — the `Phase 854` list in
  `ToolUp.AI.Client.Tests` drives the real proxy and `Cmd.OfRemoting` against a counting
  XMLHttpRequest stub.
- The `Phase 854 — client read-policy attributes` list in `ToolUp.Platform.Tests` pins
  `ofAttributes` to the declaration the Fable pack registers.

## Measured — requests, before and after

The shard asked for Phase 849's requests-per-boot on the minimal sample, before and after. That
number is **0 before and 0 after, and cannot move**: `samples/MinimalClient` never loads the remoting
module (849 measured it building no proxy at all), and nothing in the SDK declares a read. So the
before/after is taken where the mechanism acts, on the transpiled client, counting the requests the
scripted XMLHttpRequest receives:

| Scenario (Fable-tier pack) | Undeclared (before) | Declared (after) |
|---|---|---|
| Two identical calls in flight at once | 2 requests | **1 request**, both callers answered |
| The same through two `Cmd.OfRemoting.callWithName` | 2 requests, `OnCalling` ×2, `OnSuccess` ×2 | **1 request**, `OnCalling` ×1, `OnSuccess` ×1, 2 messages |
| A read repeated within its max age | waits one round trip | **returns synchronously**; one background refresh |
| Two identical mutations | 2 requests | 2 requests — mutations are never shared |

A cache hit does not reduce the request count — each hit refreshes, as the shard specifies — it
removes the wait. The request count falls with in-flight sharing, and scales with the number of
**distinct** reads rather than call sites (four `TeamApi` proxies in the shell share one table).

## Rollback

Remove the `ReadPolicies.register` calls: with the table empty every call takes the pre-854 path.
The attributes are inert without a registration.
