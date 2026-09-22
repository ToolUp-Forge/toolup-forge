# Phase 69j — Schema-versioned wire envelopes (consumer migration)

**What changes.** The remoting dispatcher now reads an `X-Remoting-Schema: <n>` request header,
publishes a per-method supported-version vector on the docs schema, and can route one logical method
to a version-specific handler. Two attributes are new — `[<SupportsSchema>]` and
`[<DeprecatedSchema>]` — and one client builder — `Remoting.withSchemaVersion`. `__schema_version`
on the error envelope shipped earlier in this phase and is unchanged.

**Nothing is required of you.** Every method with no `_V<n>` suffix and no attribute serves exactly
one version — the composed `RemotingOptions.SchemaVersion`, whose default is `1` — and a caller that
sends no header is served precisely what it was served before, byte for byte (GP 11). The only
behaviour a deployment gains without opting in is a **refusal**: a caller that sends
`X-Remoting-Schema` naming a version the server does not serve now gets a `400` naming what is
supported, instead of being handed a shape it did not ask for. No shipped client sends the header.

---

## The pattern: shipping version N+1 beside version N

The wire format evolves by adding a handler, not by changing one.

```fsharp
// BEFORE — one shape, implicitly version 1.
type ReportApi = {
    [<TenantScoped>]
    Summarise: ReportRequest -> Async<ReportSummary>
}

// AFTER — version 2 returns a restructured summary; version 1 is untouched
// and still served to every caller pinned to it.
type ReportApi = {
    [<TenantScoped>]
    [<DeprecatedSchema "2027-06-30">]
    Summarise_V1: ReportRequest -> Async<ReportSummary>

    [<TenantScoped>]
    Summarise_V2: ReportRequest -> Async<ReportSummaryV2>
}
```

The rules the dispatcher applies:

- A field named `<Name>_V<n>` is a handler for the **logical method** `<Name>` at version `n`. The
  logical name is what callers address: `/api/ReportApi/Summarise`.
- `[<SupportsSchema [| 1; 2 |]>]` on a handler overrides the suffix, so **one** handler can serve
  several versions when a bump changed nothing for that method.
- `X-Remoting-Schema: 1` routes to `Summarise_V1`; `2` routes to `Summarise_V2`; **no header routes
  to the highest supported version.** A version no handler serves is refused with the vector.
- **Each handler carries its own attributes, and they are the ones enforced.** The route is rewritten
  before the auth / validation / rate-limit / audit / idempotency chain runs, so `Summarise_V2`'s
  `[<TenantScoped>]` is what gates a version-2 call. Annotate every handler; an unclassified one
  refuses startup exactly as any other method would.
- `[<DeprecatedSchema "<ISO-8601 date>">]` emits one line per call to the composed
  `DiagnosticsLogger` and **does not refuse** — the whole point of the window is that old callers
  keep working. `retireAfter` is a string, not a `DateTime`: CLR attribute arguments must be
  compile-time constants, which `DateTime` cannot be. An unparseable date refuses at startup.
- **Retirement is your act, on the date you published.** Delete the handler; the vector shrinks and
  a pinned caller starts getting the refusal instead of a shape you no longer maintain.

### Client — opt in per proxy

```fsharp
Api.makeProxy<ReportApi> (customOptions = Remoting.withSchemaVersion 1)
```

It appends `X-Remoting-Schema` to the proxy's custom headers. Deliberately **not** a default:
sending it everywhere would change every existing deployment's request bytes, to assert a version the
caller never chose. Pin when you want the guarantee; leave it off to track the server's default.

### Discovering what a deployment serves

The docs schema (`<docsUrl>/$schema`, served when `Remoting.withDocs` is composed) grows an additive
`schemaVersions` array per route. A versioned method appears **once**, under its logical name:

```json
{ "remoteFunction": "Summarise", "route": "/api/ReportApi/Summarise", "schemaVersions": [1, 2] }
```

`SchemaVersion.describe serverDefault typeof<ReportApi>` returns the same vector as F# data for a
host that composes no docs surface.

---

## The refusal on the wire

```json
{
  "error": {
    "message": "Unsupported wire-schema version '99' for method 'Summarise'. Supported: [1; 2].",
    "methodName": "Summarise",
    "schemaVersion": { "requested": "99", "supported": [1, 2] }
  },
  "ignored": false,
  "handled": true,
  "category": "user",
  "__schema_version": 1
}
```

`400`, and `category` is the existing `"user"` — a caller-side mistake. **No new `ErrorCategory`
case was added**, deliberately: that union is closed, public and matched exhaustively downstream, so
a new case would be a compile break for every consumer in exchange for information the payload
already carries. Branch on `error.schemaVersion` being present.

---

## Two things the dispatcher refuses at startup

Both are annotations that could not take effect, and an annotation that silently does nothing is
worse than none — the author stops looking for the negotiation they believe they composed.

1. **A streaming method (`'a -> IAsyncEnumerable<'b>`) carrying either attribute.** The SSE
   short-circuit resolves its handler before the negotiated version is applied, so the declared
   versions would be ignored. Drop the annotation, or move the method off the streaming shape.
2. **Any versioned record composed against the AspNetCore middleware adapter.** That adapter reads no
   request header and runs no pre-flight chain; it already refuses every other seam in this family
   for the same reason. Use the Giraffe adapter.

Also refused: two handlers claiming the same version for one logical method, a declared version that
is not positive, and a `[<DeprecatedSchema>]` date that does not parse.

---

## What is NOT in scope

- **Success bodies are not enveloped.** A successful remoting response is a bare value on this wire;
  `__schema_version` rides the error envelope, which is the only envelope there is. Wrapping success
  bodies would be a wire evolution in its own right.
- **No SDK method ships a multi-version implementation.** This phase lays the substrate; the first
  actual `_V2` is a future change that uses it.
- A `_V<n>` field is an ordinary record field, so **its own route also dispatches**
  (`/api/ReportApi/Summarise_V1`), with that field's attributes enforced identically. It is not the
  published surface — the docs export lists the logical name only — but it is not blocked either.

## Verification

```powershell
dotnet build ToolUp.Forge.sln --nologo
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 69j"
```

The pack drives the real dispatcher on a TestServer: header-driven routing to each handler, the
highest-version default, the refusal's exact wire bytes, the deprecation warning, the docs vector,
and each compose-time refusal.

## Rollback

Remove the `_V<n>` suffixes and the two attributes; the record returns to unversioned dispatch and
the header stops being meaningful for those methods. No stored data and no persisted format is
involved — the negotiation is per request.
