# WebPush transactional push sink (Phase 6f)

Browser push backend over the
[Web Push protocol (RFC 8030)](https://www.rfc-editor.org/rfc/rfc8030)
+ VAPID auth (RFC 8292). Implements `INotificationSink` with
`Kind = "Push"`. Uses the [WebPush NuGet package](https://github.com/web-push-libs/webpush-csharp)
(MIT-licensed) for the protocol detail (HTTP encryption, JWT signing).

## Activation

```bash
TOOLUP_TRANSACTIONAL_PUSH=webpush
WEBPUSH_VAPID_PUBLIC=BEl62iU…   # base64url-encoded VAPID public key
WEBPUSH_VAPID_SUBJECT=mailto:noreply@example.com
```

The VAPID **private** key comes from `ISecretStore` under
`_platform/WEBPUSH_VAPID_PRIVATE`. Public key + subject are
deployment-stable and surface to the browser at subscription time;
the private key signs each push delivery and never leaves the server.

Generate a fresh VAPID key pair once per deployment:
```js
// using the web-push npm package
npx web-push generate-vapid-keys
```
or any equivalent ECDSA P-256 generator.

## Service worker

The companion includes a reference service worker at
`examples/sw.js`. Deploy it at your origin's document root
(typically `/sw.js`) and register it once per session:

```js
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js');
}
```

Subscribe and POST the resulting subscription JSON to your back-end:

```js
const reg = await navigator.serviceWorker.ready;
const sub = await reg.pushManager.subscribe({
  userVisibleOnly: true,
  applicationServerKey: vapidPublicKeyAsUint8Array,
});
await fetch('/api/users/me/push-token', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(sub),
});
```

The body of `sub` is a JSON object with `endpoint` + `keys.p256dh` +
`keys.auth`. Persist it directly into
`UserContact.PushTokens.Token` (the sink parses that exact shape).

## Failure classification

| HTTP response | `SinkResult` |
|---|---|
| 200 / 201 | `Delivered` |
| 410 Gone, 404 | `PermanentFailure` (subscription expired — evict from address book) |
| 429 | `TransientFailure` |
| 5xx | `TransientFailure` |
| Other 4xx | `PermanentFailure` |

## Native iOS / Android push

Out of scope for Phase 6f. Web Push works in PWA contexts on iOS
Safari 16.4+ and every modern Android browser, which covers most
deployments without device-token management. Native APNs / FCM
pipelines need device-token registration, expiry tracking, and badge
counts that the SDK doesn't currently model. Deployments needing
native push write their own `INotificationSink` against
APNs / FCM and register it via `withTransactionalSink`.

## Per-token send

The sink iterates the resolved `PushToken list` (one entry per
registered device); a `410 Gone` for one token does NOT short-circuit
deliveries to the user's other tokens.
