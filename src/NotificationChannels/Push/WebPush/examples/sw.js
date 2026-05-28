// Phase 6f Web Push service-worker example.
//
// Deploy this as the registered service worker for your app's origin
// (typically `/sw.js` at the document root). The companion sink
// produces JSON payloads with `title`, `body`, optional `url`, and
// optional `correlation_id`; this worker renders them as system
// notifications and routes click-throughs to `url` when set.
//
// To use:
//   1. Copy this file to your deployment's static-asset root.
//   2. Register it client-side once per session:
//        if ('serviceWorker' in navigator) {
//          navigator.serviceWorker.register('/sw.js');
//        }
//   3. Subscribe to push and POST the resulting subscription JSON to
//      your back-end so it lands in `UserContact.PushTokens`:
//        const reg = await navigator.serviceWorker.ready;
//        const sub = await reg.pushManager.subscribe({
//          userVisibleOnly: true,
//          applicationServerKey: <your VAPID public key as Uint8Array>
//        });
//        await fetch('/api/users/me/push-token', {
//          method: 'POST',
//          headers: { 'Content-Type': 'application/json' },
//          body: JSON.stringify(sub),
//        });
//      The body of `sub` (an object with `endpoint` + `keys`) is
//      exactly what the WebPush companion's `parseSubscription`
//      expects in `PushToken.Token`.

self.addEventListener('push', event => {
  let payload = {};

  if (event.data) {
    try {
      payload = event.data.json();
    } catch (err) {
      payload = { title: 'Notification', body: event.data.text() };
    }
  }

  const title = payload.title || 'Notification';

  const options = {
    body: payload.body || '',
    data: {
      url: payload.url,
      correlation_id: payload.correlation_id,
    },
  };

  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', event => {
  event.notification.close();

  const url = event.notification.data && event.notification.data.url;

  if (!url) {
    return;
  }

  event.waitUntil(
    clients.matchAll({ type: 'window' }).then(matched => {
      // Reuse an existing tab when the URL is already open.
      for (const client of matched) {
        if (client.url === url && 'focus' in client) {
          return client.focus();
        }
      }

      if (clients.openWindow) {
        return clients.openWindow(url);
      }
    })
  );
});
