# A forge server binds the address it is configured to

**Ships in:** ToolUp.Platform.Core (`ServerConfig.BindAddress`), ToolUp.Platform.Server (the `ServerBinding` module,
the `server-bind-address` preflight, `ComposeBootstrap.listenPlanFor` / `applyListenPlan`; `buildHostBuilder` takes the
bind address and returns the listen plan). Ships in 0.26.0 (Phase 1000).

**Affected:** every deployment that must be reachable from outside its own host or container; anything that sets
`ASPNETCORE_URLS` or runs with a launch profile `applicationUrl`; code that builds `ServerConfig` as a full record literal;
a direct caller of `ComposeBootstrap.buildHostBuilder`.

## What changes

**1. The default is loopback.** An unconfigured forge server listens on `http://127.0.0.1:<port>`. Until 0.25.1 the
host builder hard-coded `UseUrls("http://0.0.0.0:<port>")`, so every server, local and test runs included, listened on
every interface: on Windows that raised a firewall prompt per executable, and it exposed a dev server to the network.

**2. `ASPNETCORE_URLS` and a launch profile's `applicationUrl` now take effect.** The hard-coded `UseUrls` silently
overrode them. Forge now applies its own address only when neither they nor a Kestrel endpoint is configured.
**Check your `launchSettings.json` and environment:** an `applicationUrl` or `ASPNETCORE_URLS` whose port differs from
`SERVER_PORT` used to lose to `SERVER_PORT` and now wins. `dotnet run` with a launch profile sets `ASPNETCORE_URLS`
from `applicationUrl`. A Dockerfile copied from an older template that sets `ASPNETCORE_URLS=http://+:5000` now binds
port 5000 even when `SERVER_PORT` says otherwise. Make the two agree, or drop `ASPNETCORE_URLS` (step 1 below).

**Precedence, first match wins:**

| # | Setting | Bound by |
|---|---|---|
| 1 | `Kestrel:Endpoints:*` (`Kestrel__Endpoints__<name>__Url`) | ASP.NET Core |
| 2 | the `urls` host setting: `ASPNETCORE_URLS`, `DOTNET_URLS`, an `urls` key, `applicationUrl` | ASP.NET Core |
| 3 | `SERVER_BIND_ADDRESS` on `SERVER_PORT` / `ServerConfig.Port` | forge |
| 4 | `ServerConfig.BindAddress` on that port | forge |
| 5 | `127.0.0.1` on that port | forge |

A bind address is an IPv4 or IPv6 literal or `localhost`: `127.0.0.1`, `0.0.0.0` (all IPv4 interfaces), `::` (all
interfaces). Anything else fails loud at startup, naming the value.

**3. A deployed server on the default loopback refuses to start.** The `server-bind-address` preflight recognises a
deployed posture by `DOTNET_RUNNING_IN_CONTAINER=true` (set by the .NET container images), `KUBERNETES_SERVICE_HOST`
(every Kubernetes pod) or `ServerConfig.ReplicaCount > 1`. If every listen URL is loopback:

- on the **default** loopback it is an **Error**, naming the signal and `SERVER_BIND_ADDRESS=0.0.0.0`;
- on a loopback you **configured** (any of rungs 1–4) it is a **Warning**. A sidecar proxy in the same pod reaches
  loopback, so it is a legitimate topology, but the warning names the setting to change if it is not yours.

## What a deployment sets

1. **Container / Kubernetes / any host reached from outside:** set `SERVER_BIND_ADDRESS=0.0.0.0` (or `::`) beside
   `SERVER_PORT`, or `ServerConfig.BindAddress = Some "0.0.0.0"` in code. The shipped `Dockerfile.template`s now do
   this. If you kept `ASPNETCORE_URLS=http://+:<port>`, it still binds every interface. The Kubernetes manifest
   `toolup k8s emit` writes sets `ASPNETCORE_URLS=http://+:<port>`, so it needs no change.
2. **A reverse proxy on the same host** (nginx, Caddy, IIS) forwarding to `127.0.0.1:<port>`: nothing. Loopback is
   the right binding.
3. **Local runs:** nothing. They stop exposing the port.

```fsharp skip=fragment
{ ServerConfig.defaults with BindAddress = Some "0.0.0.0" }
```

**Record literals.** `ServerConfig` gains `BindAddress: string option`. A full record literal adds
`BindAddress = None`; `{ ServerConfig.defaults with … }` needs no change.

## Verify

- Local: start the app and check that `netstat -an` / `Get-NetTCPConnection -LocalPort <port>` shows `127.0.0.1:<port>`,
  not `0.0.0.0:<port>`.
- Deployed: the startup log's `[preflight] server-bind-address:` line reads `Ok`.

## Rollback

Set `SERVER_BIND_ADDRESS=0.0.0.0` everywhere to restore the pre-0.26.0 binding without downgrading.
