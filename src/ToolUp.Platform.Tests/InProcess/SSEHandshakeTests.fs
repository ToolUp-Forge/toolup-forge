module ToolUp.Platform.Tests.InProcess.SSEHandshakeTests

open System.Diagnostics
open System.IO
open System.Text
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform

/// Run `SSE.writeReadyResponse` once into a throwaway context to
/// warm up JIT + assembly-load overhead (DefaultHttpContext drags
/// in a chunk of Microsoft.AspNetCore.Http on first touch in the
/// process). Without warmup, the timed call below can be 800ms+
/// purely on cold-start cost, with no signal about handshake speed.
let private warmup () = task {
    let ctx = DefaultHttpContext()
    ctx.Response.Body <- new MemoryStream()
    do! SSE.writeReadyResponse ctx.Response
}

[<Tests>]
let tests =
    testList "SSE handshake regression (Phase 6i.F)" [
        testTask "writeReadyResponse sets unbuffer headers + flushes ready frame in <50ms" {
            do! warmup ()

            let ctx = DefaultHttpContext()
            let body = new MemoryStream()
            ctx.Response.Body <- body

            let sw = Stopwatch.StartNew()
            do! SSE.writeReadyResponse ctx.Response
            sw.Stop()

            // Headers — production reverse proxies (nginx, Cloudflare)
            // need both Cache-Control: no-cache AND X-Accel-Buffering:
            // no to disable response buffering on a streaming response.
            Expect.equal (string ctx.Response.Headers.ContentType) "text/event-stream" "Content-Type"
            Expect.equal (string ctx.Response.Headers.CacheControl) "no-cache" "Cache-Control"
            Expect.equal (string ctx.Response.Headers["X-Accel-Buffering"]) "no" "X-Accel-Buffering"

            // Body — initial ":ready\n\n" comment gives proxies that
            // flush on first newline a reason to release the buffer
            // even if they ignore X-Accel-Buffering.
            let written = Encoding.UTF8.GetString(body.ToArray())
            Expect.equal written ": ready\n\n" "ready frame"

            // Wall-clock — post-warmup the helper is sub-millisecond
            // in practice; 50ms is the regression ceiling. The class
            // we are guarding against is "reintroduce a slow await
            // before the flush" (e.g. `channel.Subscribe()` moving
            // back above the FlushAsync), which would consistently
            // be hundreds of ms or seconds, not warmup noise.
            Expect.isLessThan sw.ElapsedMilliseconds 50L "TTFB"
        }
    ]