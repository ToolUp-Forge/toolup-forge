# ToolUp.Platform.Transport

The lowest tier of the ToolUp SDK: the portable outbound HTTP seam and the
retry discipline every outbound caller shares. It depends on `FSharp.Core`
only and compiles to both .NET and [Fable](https://fable.io), so one mapping
serves a server host and a browser host.

## What it gives you

- **The seam** — `HttpRequest` / `HttpResponse` (host-agnostic records) and
  `IHttpTransport` (one `Send: HttpRequest -> Async<HttpResponse>`). A host
  injects how bytes travel: on the server, `HttpClientTransport` in
  `ToolUp.Platform.Server` over a BCL `HttpClient`; in a browser, `fetch`.
- **`RetryPolicy`** — the SDK's unified retry policy as data
  (`MaxAttempts`, `InitialBackoff`, `MaxBackoff`, `Timeout`) with
  `RetryPolicy.delayFor` and `RetryPolicy.clampTimeoutMs`.
- **`TransportError`** + **`TransportClassifier`** — the failure taxonomy:
  `Network`, `Transient` (429 / 5xx, with the provider's `Retry-After`),
  `Permanent`, `Refused` (a local budget said no), `Exhausted`.
- **`TransportRetry`** — the generic retry loop (`runWith` over any error
  type, `run` over `TransportError`), and **`HttpCall`** — one classified
  call under a policy.

## Example

```fsharp
open ToolUp.Platform.Transport

let fetchItems (transport: IHttpTransport) =
    HttpCall.send RetryPolicy.defaults transport (HttpCall.get "/v1/items" [ "Accept", "application/json" ])
```

Retry is data, never a callback (GP 12 rule 3). Domain taxonomies — the AI
provider error type, for one — are projections of `TransportClassifier`, so
the classification rule exists once.
