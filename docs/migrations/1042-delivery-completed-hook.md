# A module registers a delivery-completed callback

**Ships in:** ToolUp.Platform.Server: the `DeliveryCompleted` module (`Delivery`, `DeliveredRun`,
`DeliveredOutcome`, `DeliveryOrigin`, `DeliveryCompletedCallback`, `DeliveryCompletedHook`, `aroundJob`, the
`Delivery.committed` / `failed` / `isComplete` readers), `ServerModule.withDeliveryCompleted`,
`ServerApp.withDeliveryCompleted`, and the new `ServerModule.DeliveryCompletedHooks` field. ToolUp.DataSources.Common:
a declared watch's poll is now one delivery. Rides the 0.31.0 draft.

**Affected:** a module that runs work after its data arrives by observing its own ingestion routes, for example a
handler ahead of the routes that arms a `Response.OnCompleted` callback and falls through with `skipPipeline`.
Nothing changes for a deployment that registers no hook: the ingestor is not decorated and no middleware is
mounted.

**What breaks, and for whom.** `ServerModule` gains a field (`DeliveryCompletedHooks`). Code that builds it through
`ServerModule.create` or `{ m with … }` is unaffected. A full-literal construction, which is rare and unsupported,
needs `DeliveryCompletedHooks = []`.

## What changes

1. **A module registers post-delivery work.** `ServerModule.withDeliveryCompleted (name, callback)` (or
   `ServerApp.withDeliveryCompleted` at the composition root). The callback takes the application's services and
   the completed `Delivery`.
2. **A delivery is one unit of work's ingestion.** Every `IDataIngestor.RunIngestion` call one request makes, or one
   ingestion job attempt makes: a declared scheduled pull, a watched location's poll, a queued refresh. It fires
   once, after the response is written or the job attempt returns, with one `DeliveredRun` per table, failed tables
   included.
3. **It runs under the delivery's resolved scope.** That is the request's resolved scope, or the scope the job
   carried. A delivery that resolved none calls no hook.
4. **A throwing callback is contained.** It is logged at `Warn` and audited as `DeliveryCallbackFailed`
   (`_platform.delivery`), and the response, the job result and the other callbacks are unaffected.
5. **`IDataIngestionApi.TriggerRefresh` schedules under the request's resolved scope** (the typed `Schedule`
   overload) when the scope-resolution middleware recorded one, so a queued refresh's job runs under that scope
   and its delivery reaches the hook. The job's `ScopeId` is unchanged. A request with no recorded scope keeps the
   string overload, as before.

## What to do

Replace the route observer with a hook:

```diff
-let observer: HttpHandler =
-    fun next ctx ->
-        if HttpMethods.IsPost ctx.Request.Method && ctx.Request.Path.Value = uploadRoute then
-            let scope = ScopeResolution.forRequest ctx
-            ctx.Response.OnCompleted(fun () ->
-                if ctx.Response.StatusCode = 200 then Async.StartAsTask(afterDelivery scope) :> Task
-                else Task.CompletedTask)
-        skipPipeline
-
-let withAfterDelivery (m: ServerModule) = m |> ServerModule.withHandlers [ observer ]
+let withAfterDelivery (m: ServerModule) =
+    m
+    |> ServerModule.withDeliveryCompleted ("after-delivery", fun _ delivery -> async {
+        if not (List.isEmpty (DeliveryCompleted.Delivery.committed delivery)) then
+            do! afterDelivery delivery.Scope })
```

The hook replaces the status-code test with the delivery's own outcomes. It also fires for scheduled and watched
deliveries, which the route observer never saw. Only ingestion through the platform's `IDataIngestor` is recorded:
a route that commits rows some other way still has to call the ingestor for its delivery to be seen.

A module job that ingests on its own can make each attempt a delivery too: register its handler as
`DeliveryCompleted.aroundJob "<handler-name>" handler`.

## Verify

The Phase 1042 list in `ToolUp.Platform.Tests` (`DeliveryCompletedTests`) drives the shipped registration,
middleware and composed ingestor. In it, a three-table request delivery fires once after its response, with three
runs under the request's scope. A failed table and a refused one are reported beside the committed one. A
scheduled pull fires under the job's carried scope, and a job with none fires nothing. Synchronous and asynchronous
throwing callbacks leave the response untouched and are logged and audited. `TriggerRefresh` schedules under the
request's scope end to end. Registering no hook leaves the ingestor undecorated, and the new sources hold no
scope-mint spelling.

## Rollback

Remove the `withDeliveryCompleted` registrations. With none left, the deployment composes as before.
