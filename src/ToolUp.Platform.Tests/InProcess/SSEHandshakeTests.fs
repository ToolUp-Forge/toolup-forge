module ToolUp.Platform.Tests.InProcess.SSEHandshakeTests

open System.Diagnostics
open System.IO
open System.Text
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs

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

/// An `IAuditLog` that keeps what it is handed.
type private RecordingAuditLog() =
    let rows = System.Collections.Concurrent.ConcurrentQueue<AuditEvent>()
    member _.Rows = rows |> Seq.toList

    interface IAuditLog with
        member _.Record(_scope, _actor, audit) = async {
            rows.Enqueue audit
            return Ok()
        }

        member _.GetAuditTrail(_, _, _) = async { return [] }

/// A team member's live-session partition: its team, the shape the
/// live-session endpoint names through `resolveWith`.
let private teamPartition (access: AccessContext) =
    access.TeamId |> Option.defaultValue access.UserId

let private connect (access: AccessContext option) (query: string) =
    let audit = RecordingAuditLog()
    let services = ServiceCollection()
    services.AddSingleton<IAuditLog>(audit) |> ignore
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Request.Method <- "GET"
    ctx.Request.Path <- PathString "/api/live-sessions/s-1"
    ctx.Request.QueryString <- QueryString query
    access |> Option.iter (AccessContext.stamp ctx.Items)
    ctx, audit

let private waitForRows (audit: RecordingAuditLog) =
    let deadline = System.DateTime.UtcNow.AddSeconds 5.0

    while List.isEmpty audit.Rows && System.DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep 5

    audit.Rows

/// The one SSE scope resolver, driven the way the live-session endpoint
/// drives it (`resolveWith` naming the team partition). The endpoint itself
/// is pinned in `LiveSessionHostTests`.
let private resolverTests =
    testList "SSE scope resolution — the live-session shape" [
        test "CookieRequired refuses a connect with no validated principal" {
            let ctx, _ = connect None "?userId=alice"

            Expect.equal
                (SseScopeResolution.resolveWith teamPartition CookieRequired ctx)
                SseScopeResolution.SseUnauthenticated
                "no client-asserted id is trusted under CookieRequired"
        }

        test "an anonymous session is not a validated principal under CookieRequired" {
            let ctx, _ = connect (Some(AccessContexts.anonymous "tab-1")) ""

            Expect.equal
                (SseScopeResolution.resolveWith teamPartition CookieRequired ctx)
                SseScopeResolution.SseUnauthenticated
                "the anonymous caller id does not open a stream"
        }

        test "a validated team member subscribes under its team partition" {
            let ctx, _ = connect (Some(AccessContexts.teamMember "alice" "team-42")) ""

            Expect.equal
                (SseScopeResolution.resolveWith teamPartition CookieRequired ctx)
                (SseScopeResolution.SseScope "team-42")
                "the partition the endpoint named"
        }

        test "a mismatching ?userId= is ignored and audited as a SurfaceDenied row" {
            let ctx, audit =
                connect (Some(AccessContexts.teamMember "alice" "team-42")) "?userId=mallory"

            Expect.equal
                (SseScopeResolution.resolveWith teamPartition QueryParamFallback ctx)
                (SseScopeResolution.SseScope "team-42")
                "the principal wins over the asserted id"

            match waitForRows audit with
            | [ SurfaceDenied row ] ->
                Expect.equal row.DenialCode SseScopeResolution.mismatchDenialCode "the mismatch denial code"
                Expect.equal row.Hint (Some "asserted=mallory") "names the asserted id"
            | other -> failtestf "expected one SurfaceDenied row, got %A" other
        }

        test "QueryParamFallback honours ?userId= when no principal is stamped" {
            let ctx, audit = connect None "?userId=tab-7"

            Expect.equal
                (SseScopeResolution.resolveWith teamPartition QueryParamFallback ctx)
                (SseScopeResolution.SseScope "tab-7")
                "the documented dev / Anonymous path"

            Expect.isEmpty audit.Rows "nothing to audit"
        }
    ]

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

        resolverTests
    ]