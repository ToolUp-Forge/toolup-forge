# 1056 — The request pipeline is a list of stages, with streaming inside it

## What changes

The remoting dispatcher's per-request body is one list of nine stages over one immutable `RequestContext`
(`src/ToolUp.Platform.Server/Server/Remoting/Stages/`): correlate, negotiate, batch backstop, authorize,
validate, idempotency, rate limit, invoke, audit. The order is the doctrine — authorisation before argument
decoding, idempotency before the rate limit, the call before its audit row — and one writer renders every
response. The Giraffe adapter and the ASP.NET Core middleware both run that list, so the middleware now
enforces every seam composed on the options (telemetry, authorisation, rate limit, idempotency, audit,
validation, schema negotiation, companions) instead of refusing to start, and supports `fromContextAsync`.

**A streaming call is a call.** A method returning `IAsyncEnumerable<'T>` runs through every stage:

- it may carry `[<RequiresRole>]`, `[<RequiresClaim>]` or `[<TenantScoped>]` (an unauthorised caller gets 401
  before the producer runs), `[<RateLimit>]` (charged when the stream opens), `[<Audit>]` (one row when it
  opens) and wire-schema versioning. `[<Idempotent>]` is refused at start (`ContractRefusal.IdempotentStreaming`):
  a stream has no single response to memoise;
- its argument decodes through the same seam as a unary method's: a malformed body is 400 with the `validation`
  envelope (`decodeError`), never a call on a constructed default. The same holds for the long-running
  companions' handle;
- the producer receives the request's abort token: a client that leaves cancels it;
- `AsyncStream.fromCallback` holds at most 64 values for a slow consumer and then holds the producer in `emit`
  until it reads; `AsyncStream.fromSubscription` drops the oldest waiting value instead (a feed serves every
  subscriber from one publisher, and its values are states). A value emitted after the consumer left is dropped.

The wire bytes of every existing response are unchanged. Two header differences on the ASP.NET Core middleware
only: a JSON envelope's `Content-Type` is set before its body (it was set after, so a started response could
lose it), and the docs page is `text/html; charset=utf-8`, as on Giraffe.

## Breaking changes and what a consumer does

| Removed / changed | Instead |
|---|---|
| `InvocationResult`, `Proxy.makeApiProxy`, `InvocationProps` (now internal) | Serve the record through `Remoting.buildHttpHandler` / `app.UseRemoting`; the proxy is the `invoke` stage's |
| `GeneratedArguments.Complete` returns `Task<InvocationResult>` | It returns `Task`. Generated tables compile unchanged (the type is inferred) |
| `MethodClassification.Unclassified`, `AuthClassifier.evaluate`'s unclassified arm | An unmarked method is absent from `AuthClassifier.classify`; `AuthClassifier.unclassified` takes the record's `Type` |
| `ContractRefusal.StreamingCarriesPreflight`, `ContractRefusal.VersionedStreaming` | Gone: both are served. New: `ContractRefusal.IdempotentStreaming` |
| `ErrorResult`, `ProtocolImplementation`, `SerializationType`, `BodyNormalisation`, `AuthDecision`, `MethodClassification` cases unqualified | `[<RequireQualifiedAccess>]`: `ErrorResult.Propagate x`, `ProtocolImplementation.StaticValue impl`, `SerializationType.MessagePack`, `BodyNormalisation.Disabled`, `AuthDecision.Deny reason` |

The codemod rewrites an error handler's `Propagate` / `PropagateCategorised` and reports the rest:

```powershell
dotnet run --project Build.fsproj -- Codemod <path-to-your-repo> --phase 1056
```

```diff
 let errorHandler (ex: exn) (_: RouteInfo<HttpContext>) =
-    Propagate ex.Message
+    ErrorResult.Propagate ex.Message
```

A producer that relied on unbounded buffering, or on running past a client disconnect, is reviewed: it now
awaits its consumer and is cancelled with the request.

## Verify

1. `dotnet build` your server: the qualified cases compile.
2. A streaming method with `[<RequiresRole "Admin">]` and an auth resolver: a caller without the role gets 401
   and no frame; one with it streams to `event: complete`.
3. POST a streaming method a body whose first argument does not decode: 400, `"category":"validation"`.
4. Close the tab mid-stream: the producer's token is signalled (`InProcess/StreamingPipelineTests.fs` pins
   all four).

## Rollback

Pin the previous SDK version. The change is one minor; there is no partial adoption.
