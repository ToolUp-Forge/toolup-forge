# Deploying SSE behind a reverse proxy

Two SDK surfaces stream events via Server-Sent Events (`text/event-stream`):

- **`/api/notifications`** — `ToolUp.Platform.Server`'s `SSEConnectionManager`. Client side is `NotificationClient` in `ToolUp.Platform.Client`.
- **`/api/ai/events`** — `ToolUp.AI.Server`'s AI agent-loop streaming. Client subscribes per-conversation through `AIClient`.

Both connections are long-lived (open until the client navigates away or the server closes after an idle window). The SDK sets the right response headers at the source — but most reverse proxies need explicit configuration to forward the stream **without buffering and without compression**. Deployments that skip this configuration see one of three failure modes:

| Symptom | Root cause |
|---|---|
| Events arrive in batches every 30 s instead of immediately. | Reverse proxy is buffering the response. |
| The connection appears to open but no events ever arrive. | Reverse proxy is `gzip`/`brotli`-compressing the stream; the client's EventSource fails to decode. |
| The connection drops every ~60 s and reconnects in a loop. | Reverse proxy's idle timeout is shorter than the SDK's keep-alive interval. |

What the SDK already does: the server sets `Cache-Control: no-cache`, `X-Accel-Buffering: no`, `Content-Type: text/event-stream`, and sends a heartbeat comment every 25 s to keep mid-tier proxies from idling out. The proxy-side changes below are still required because not every proxy honours `X-Accel-Buffering` and most apply compression at a layer that runs after the application's response headers.

---

## nginx

Disable response buffering, compression, and raise the read/keep-alive timeouts on the SSE paths. Match against the SSE prefixes specifically so the rest of `/api/` keeps its default optimisations:

```nginx
location /api/notifications {
    proxy_pass http://upstream;
    proxy_http_version 1.1;
    proxy_set_header Connection "";

    # SSE — must not buffer, must not compress, must stay open.
    proxy_buffering off;
    proxy_cache off;
    gzip off;

    proxy_read_timeout 1h;
    proxy_send_timeout 1h;
    chunked_transfer_encoding on;
}

location /api/ai/events {
    proxy_pass http://upstream;
    proxy_http_version 1.1;
    proxy_set_header Connection "";

    proxy_buffering off;
    proxy_cache off;
    gzip off;

    proxy_read_timeout 1h;
    proxy_send_timeout 1h;
    chunked_transfer_encoding on;
}
```

Notes:

- `proxy_http_version 1.1` is mandatory for streaming; nginx defaults to 1.0 for upstream.
- `Connection ""` overrides the default `Connection: close` so the upstream keeps the socket open.
- `gzip off` covers the case where a top-level `gzip on` directive includes `text/event-stream` in its `gzip_types`. Even if the SSE MIME isn't explicitly in `gzip_types`, listing `gzip off` per-location is defence-in-depth.
- `proxy_read_timeout 1h` — pick a value comfortably above your longest expected idle. The SDK's 25 s heartbeat will keep activity flowing, but a 60 s read timeout still occasionally drops.
- If you front nginx with a separate ingress (Kubernetes, an external CDN), repeat the same rules at every tier — nginx-on-the-app-node will not save you from a load balancer above it that compresses.

---

## HAProxy

```haproxy
frontend public
    bind :443 ssl crt /etc/ssl/app.pem

    # Match SSE paths and disable response buffering.
    acl is_sse path_beg /api/notifications /api/ai/events
    http-response set-header X-Accel-Buffering no if is_sse

    default_backend app

backend app
    timeout server 1h
    timeout tunnel 1h
    option http-buffer-request

    # Required for SSE: turn off response compression on this backend
    # entirely if the global section enables it for `text/`-class MIMEs.
    # If you need compression for /api/* JSON responses, scope the
    # comp algorithm with `compression type` that excludes
    # `text/event-stream` explicitly.
    compression algo none
```

Notes:

- HAProxy's `timeout server` is the relevant idle timeout for streaming responses; raise both it and `timeout tunnel` on the SSE backend.
- HAProxy does not buffer SSE responses by default in modern versions (2.4+), but verify on your version — the `option http-no-delay` directive helps if you observe latency under load.

---

## Azure App Service / Azure Front Door

App Service does not perform request/response buffering for `text/event-stream` by default; the SDK's `X-Accel-Buffering: no` header is sufficient at the App Service tier. The deploy-time gotchas live one layer up:

- **Application Gateway / Front Door compression.** If you front App Service with an Application Gateway or Azure Front Door rule that compresses responses, exclude `text/event-stream` from the compressed-MIME list. Front Door's default profile does not compress SSE, but custom rules can override.
- **`WEBSITE_WARMUP_STATUSES`.** Per the workspace lessons doc, App Service warmup probes need `WEBSITE_WARMUP_STATUSES=200,404` so the platform doesn't fail the warmup against an SSE endpoint that responds with a long-lived 200 stream. The probe times out otherwise.
- **`TOOLUP_REQUIRE_HTTPS`.** If you set this for the app, the warmup probe (which hits the app over HTTP locally before exposing it externally) fails. Either don't set the var, or configure App Service to use HTTPS internally for warmup.
- **Idle timeout.** App Service's default outbound idle is 4 minutes. The SDK's 25 s heartbeat keeps the connection active so this normally doesn't surface. If you observe drops at exactly 4 minutes, audit any middleware between the app and the client (a custom WAF rule, a session-affinity cookie that drops, etc.).

---

## Cloudflare

Cloudflare's free / pro tiers compress responses globally and will compress `text/event-stream` unless explicitly told not to. Two options:

1. **Bypass the cache and disable compression for SSE paths.** Create a page rule (or a Configuration Rule in the new dashboard) matching `your.host/api/notifications*` and `your.host/api/ai/events*` with:
   - Cache Level: Bypass
   - Disable Performance (covers Auto Minify + Compression)
   - Disable Apps
2. **Use Cloudflare Workers / Pages Functions to pass-through.** A Worker route at the SSE prefix that forwards `request.headers` and returns the upstream `Response` body without modification. More flexible but requires authoring a Worker.

Cloudflare's Enterprise plan supports per-route compression configuration via a Compression Rule; on lower tiers the page-rule path above is the standard fix.

---

## Smoke-testing the configuration

Before declaring a deployment SSE-ready, verify end-to-end with `curl` from a host that traverses the same proxy chain a real client would:

```bash
curl -N -H "Accept: text/event-stream" \
     -H "Authorization: Bearer $TOKEN" \
     https://your-app.example.com/api/notifications
```

Expected: each `data:` line appears in the terminal within a few hundred milliseconds of the server emitting it. Failure modes to watch for:

- Multi-second delays between server emit and `curl` output → proxy is buffering.
- `curl` exits after a few seconds with no data → proxy is compressing and the SDK closed the stream.
- `curl` shows binary garbage at the start → response is `Content-Encoding: gzip` somewhere upstream.

For the AI stream, replace the path with `/api/ai/events` and supply the conversation id per the AI client's subscription contract.

---

## See also

- `SSEConnectionManager.fs` — server-side connection lifecycle, heartbeat cadence, idle close.
- `NotificationClient.fs` — client-side EventSource wrapper, reconnection backoff, scope subscription.
- `docs/ai/getting-started.md` — AI streaming client contract.
- `docs/platform/auth.md` — Bearer-token attachment for SSE endpoints behind OIDC auth.
