# NativeClient — non-web reference consumer (Phase 244)

A plain .NET **console** app that consumes the *same* typed contract the web client and
server use (`HelloWorld.Module.SharedTypes`, source-linked here) — demonstrating that a
ToolUp.Remoting contract is **framework-neutral**: no Fable, no browser, no ASP.NET Core is
needed to call it. See [`docs/platform/native-clients.md`](../../docs/platform/native-clients.md).

This is the lightweight console form of the Phase 244 reference. A GUI consumer
(MAUI / Avalonia) is the same idea with a UI on top — out of scope here (needs the MAUI
workload); the wire + contract consumption shown below is identical.

## What it shows

- The contract (`HelloWorldApi.Echo : EchoRequest -> Async<EchoResponse>`) consumed over the
  documented wire: `POST {baseUrl}/HelloWorldApi/Echo`, request body = the args as a JSON
  array (`[{"Text":"..."}]`), response = the result (`{"Echoed":"..."}`).
- BCL only (`HttpClient` + `System.Text.Json`) — the Echo contract is plain records. A richer
  contract (F# DU / Option / Map) adds the shared `FableConverters` STJ set (the one seam).
- The per-request identity seam (read the token per call, never bake it in).

## Run

```pwsh
# Demo (no server) — prints the exact request bytes; builds + runs in CI:
dotnet run --project samples/NativeClient

# Live round-trip against a running HelloWorld server:
#   1. start the server (see samples/HelloWorld)
#   2. dotnet run --project samples/NativeClient -- http://localhost:5000 "hi there"
```
