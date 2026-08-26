# Phase 335 — Auth-attribute matching by CLR identity (consumer migration)

**What changes.** The dispatcher's Phase 69d authorisation classifier recognised the five auth
markers by **bare simple type name** (`a.GetType().Name = "PublicEndpointAttribute"`, and the
equivalent for `AllowAnonymousAttribute` / `RequiresRoleAttribute` / `RequiresClaimAttribute` /
`TenantScopedAttribute`). It now matches by **CLR type identity** against an allow-list of exactly the
two sanctioned families:

- `ToolUp.Remoting.Server.*` — the server-tier set, for API records compiled only on the server;
- `ToolUp.Platform.*` — the tier-shared mirror in `ToolUp.Platform.Core`, for API records the Fable
  client also compiles.

An attribute of the same name from any other namespace **or assembly** no longer classifies anything,
and a record carrying one **refuses composition** with a diagnostic naming the field and the offending
attribute's assembly-qualified type.

**Why.** Simple-name matching made the authorisation input forgeable. Any attribute applicable to a
record field whose type name happened to collide with a marker was honoured as a security
classification — and `PublicEndpointAttribute` / `AllowAnonymousAttribute` are names a consumer or a
third-party package may perfectly reasonably define. A consumer's own `[<PublicEndpoint>]` on an API
record field classified the method `Public`; `evaluate` then returned `Allow` for every caller.

The part that made it hard to find is that there was **no startup signal**. The Phase 69d default-deny
gate refuses to start on an *unclassified* method — and a field carrying a foreign marker *was*
classified, so the gate was satisfied and said nothing. The deployment started clean and served an
open method.

**Scope.** Server-side classification only. No wire change, no route change, no data migration. The
per-request evaluation path and the `AuthRequirement` normalisation are untouched.

**Version.** Minor bump under the SemVer-on-`0.x` policy.

## Am I affected?

Only if an API record field carries an attribute whose **type name** is one of:

```
RequiresRoleAttribute   RequiresClaimAttribute   TenantScopedAttribute
AllowAnonymousAttribute PublicEndpointAttribute
```

…and whose type is **not** the `ToolUp.Remoting.Server` or `ToolUp.Platform` one. If every marker on
your records came from `open ToolUp.Remoting.Server` or `open ToolUp.Platform` (or was written
fully-qualified as `[<ToolUp.Platform.AllowAnonymous>]`), nothing changes for you — classification is
byte-for-byte what it was (GP 11).

The likely way to hit this is an `open` ordering accident: your own `AllowAnonymousAttribute`, or one
from another package, shadowing the sanctioned marker at the point the record is declared. That
compiled fine before and quietly meant something other than what it looked like.

## The error you will see

```
ToolUp.Remoting refused to start: API record 'ReportsApi' has 1 field(s) carrying an attribute
whose name matches an authorisation marker but which is NOT one of the two sanctioned families:
[ExportAll carries 'Contoso.Web.PublicEndpointAttribute, Contoso.Web, Version=1.0.0.0, ...'].
Only ToolUp.Remoting.Server.* (server-tier) and ToolUp.Platform.* (tier-shared mirror) markers
classify a method; an attribute of the same name from any other namespace or assembly is refused
rather than honoured, because a name collision must never decide a method's authorisation.
Replace it with the sanctioned attribute of the same name, or rename your own attribute.
```

It fires at composition time (`Api.make` / `Remoting.buildHttpHandler`), before the first request —
the same place the Phase 69d unclassified refusal fires, and deliberately **ahead** of it, so you get
the cause rather than the symptom.

## The fix

**If the method was meant to be open**, qualify the sanctioned marker:

```diff
-[<PublicEndpoint>]                       // resolved to YOUR PublicEndpointAttribute
+[<ToolUp.Platform.PublicEndpoint>]       // the tier-shared mirror
 ExportAll: unit -> Async<Report list>
```

or fix the `open` ordering so the sanctioned family wins, and re-check by reading the diagnostic
disappear rather than by assuming.

**Read this as a security review, not a rename.** A method that has been reaching `Allow` for every
caller because of a collision was open in production, whatever the record's author intended. Before
you make the refusal go away, decide what the gate should actually be — the honest fix is frequently
`[<RequiresRole …>]`, not the `[<PublicEndpoint>]` the collision was impersonating.

**If your attribute is unrelated** to authorisation and merely shares a name, rename it. The
classifier cannot tell the two apart — that is the whole finding — and refusing is the only safe
answer:

```diff
-type PublicEndpointAttribute() =         // collides with the marker
+type PublicRouteMarkerAttribute() =
     inherit Attribute()
```

**If you deliberately declared your own type into `namespace ToolUp.Remoting.Server`**, note that
identity matching pins the *assembly* too, so it is still foreign. That is intentional: a
namespace-only check would leave the forgery open to anyone willing to type the namespace.

## What did not change

- The two sanctioned families classify identically to pre-335, including multi-attribute AND
  semantics, the `Value`-qualified claim form, and the Fable-mirror equivalence (GP 11).
- An unannotated method still refuses startup with the unchanged Phase 69d diagnostic.
- Both checks remain gated on an auth-context resolver being composed. With no resolver the auth
  system is dormant exactly as before, and a colliding attribute is inert rather than refused — there
  is no classification for it to forge.
- `AuthClassifier` is internal; no public API surface moved.

## See also

- [`69d-authorization-metadata.md`](69d-authorization-metadata.md) — the classifier this hardens.
- [`627-content-admin-api-authorization.md`](627-content-admin-api-authorization.md) — the companion
  failure mode from the other direction: attributes that were correct but never armed.
