module ToolUp.Platform.Tests.InProcess.JobHandleTests

open System
open System.Net.Http
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Phase 69i.H — long-running typed handles over the real dispatcher ──
//
// Mirrors 69c's `StreamingDispatchTests`: an API record whose method returns
// `Async<JobHandle<'T>>` is mounted on a real ToolUp.Remoting dispatcher
// (TestServer) with an `InMemoryJobDispatcher` composed via
// `Remoting.withJobDispatcher`, and the contract is asserted end to end:
//
//   * enqueue + poll through the auto-served `<method>/status` companion
//     reaches `Succeeded` carrying the typed result — and the companion's
//     bytes are IDENTICAL to a hand-wired `JobHandle<'T> -> Async<JobStatus<'T>>`
//     method's (the wire-compat pin for 69i.C);
//   * enqueue + stream through `<method>/progress` yields status frames and
//     terminates on the terminal one (69i.D);
//   * an idempotency key submitted twice returns the same handle and enqueues
//     ONE job (69i.F);
//   * cross-tenant isolation: another subject sees not-found, the submitter
//     and a job-admin see the job, and the job body itself sees the
//     submitting subject (69i.E);
//   * cancel propagates: status → Cancelled, the progress subscriber gets
//     the terminal frame, only the owner or an admin may cancel (69i.G).
//
// "Status persists across server restart" is NOT here: the in-memory
// dispatcher is documented as lifetime-bound, and a store-backed
// `IJobDispatcher` is the recorded decision (see the phase's deviations).

/// The long-running request. `Steps` × `StepDelayMs` of cooperative work
/// (`Async.Sleep` binds observe the job's cancellation token); `FailAt > 0`
/// faults the work at that step.
type SlowRequest = {
    Steps: int
    StepDelayMs: int
    FailAt: int
}

/// NOT private — the wire converters + dispatcher proxy reflect over the
/// shape. `Poll` is the v0 hand-wired companion, kept so the auto-served
/// `Start/status` route can be pinned byte-for-byte against it.
type LongOpApi = {
    [<AllowAnonymous>]
    Start: SlowRequest -> Async<JobHandle<string>>

    [<AllowAnonymous>]
    Poll: JobHandle<string> -> Async<JobStatus<string>>

    [<AllowAnonymous>]
    Discard: JobHandle<string> -> Async<unit>
}

/// The idempotent variant (69i.F): same shape, `[<Idempotent>]` on the
/// submit method so a repeated `X-Idempotency-Key` replays the handle.
type IdempotentLongOpApi = {
    [<AllowAnonymous; Idempotent>]
    Start: SlowRequest -> Async<JobHandle<string>>
}

/// The work: sleeps `Steps` times, faults at `FailAt`, and returns a result
/// that names the subject the job body observed — the 69i.E probe.
let private work (req: SlowRequest) = async {
    for step in 1 .. req.Steps do
        if req.FailAt > 0 && step >= req.FailAt then
            failwith "work fault"

        do! Async.Sleep req.StepDelayMs

    let subject = CallContext.subjectId () |> Option.defaultValue "-"
    return sprintf "done:%d:%s" req.Steps subject
}

let private impl (jobs: IJobDispatcher) : LongOpApi = {
    Start = fun req -> jobs.Enqueue(work req)
    Poll = fun handle -> jobs.GetStatus handle
    Discard = fun handle -> jobs.Cancel handle
}

let private idempotentImpl (jobs: IJobDispatcher) : IdempotentLongOpApi = {
    Start = fun req -> jobs.Enqueue(work req)
}

/// Header-driven `IAuthContext`: `x-subject` names the caller (absent ⇒
/// anonymous), `x-roles` is a comma-separated role list.
let private headerAuth (ctx: HttpContext) : Async<IAuthContext> = async {
    let header (name: string) =
        match ctx.Request.Headers.TryGetValue name with
        | true, v when not (String.IsNullOrWhiteSpace(v.ToString())) -> Some(v.ToString())
        | _ -> None

    let subject = header "x-subject"

    let roles =
        header "x-roles"
        |> Option.map (fun r -> r.Split(',') |> Array.map _.Trim() |> Set.ofArray)
        |> Option.defaultValue Set.empty

    return
        { new IAuthContext with
            member _.HasRole r = roles.Contains r
            member _.HasClaim(_, _) = false
            member _.HasTenant() = subject.IsSome
            member _.IsAnonymous() = subject.IsNone
            member _.SubjectId = subject |> Option.defaultValue "anonymous"
        }
}

type private CapturingTelemetry() =
    let events = System.Collections.Concurrent.ConcurrentQueue<MethodTelemetry>()
    member _.Events = events |> Seq.toList

    interface IRemotingTelemetry with
        member _.OnMethodCompleted t = events.Enqueue t

let private buildHost (handler: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe handler)
            |> ignore)
        .Build()

/// A started host + client over `handler`. Callers dispose the host.
let private start (handler: HttpHandler) = async {
    let host = buildHost handler
    do! host.StartAsync() |> Async.AwaitTask
    return host, host.GetTestClient()
}

/// One Remoting POST. `subject` / `roles` drive `headerAuth`.
let private post
    (client: HttpClient)
    (route: string)
    (body: string)
    (subject: string option)
    (roles: string option)
    (idempotencyKey: string option)
    =
    async {
        use request = new HttpRequestMessage(HttpMethod.Post, route)
        request.Headers.Add("x-remoting-proxy", "true")
        subject |> Option.iter (fun s -> request.Headers.Add("x-subject", s))
        roles |> Option.iter (fun r -> request.Headers.Add("x-roles", r))

        idempotencyKey
        |> Option.iter (fun k -> request.Headers.Add("X-Idempotency-Key", k))

        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        let! resp = client.SendAsync request |> Async.AwaitTask
        let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        return int resp.StatusCode, text
    }

let private startBody (steps: int) (delayMs: int) (failAt: int) =
    sprintf """[{"Steps":%d,"StepDelayMs":%d,"FailAt":%d}]""" steps delayMs failAt

/// Poll `route` with `[handle]` until the body names a terminal status or
/// `attempts` run out; returns the last body.
let private pollUntilTerminal (client: HttpClient) (route: string) (handleJson: string) (subject: string option) = async {
    let body = sprintf "[%s]" handleJson
    let mutable last = ""
    let mutable attempts = 0

    let isTerminal (text: string) =
        text.Contains "Succeeded" || text.Contains "Failed" || text.Contains "Cancelled"

    while attempts < 100 && not (isTerminal last) do
        let! _, text = post client route body subject None None
        last <- text
        attempts <- attempts + 1

        if not (isTerminal last) then
            do! Async.Sleep 25

    return last
}

let private anonymousHandler (jobs: IJobDispatcher) : HttpHandler =
    Remoting.createApi ()
    |> Remoting.fromValue (impl jobs)
    |> Remoting.withJobDispatcher jobs
    |> Remoting.buildHttpHandler

let private authedHandler (jobs: IJobDispatcher) (telemetry: IRemotingTelemetry option) : HttpHandler =
    let options =
        Remoting.createApi ()
        |> Remoting.fromValue (impl jobs)
        |> Remoting.withJobDispatcher jobs
        |> Remoting.withAuthContext headerAuth

    match telemetry with
    | Some sink -> options |> Remoting.withTelemetry sink |> Remoting.buildHttpHandler
    | None -> options |> Remoting.buildHttpHandler

[<Tests>]
let tests =
    testList "Phase 69i — long-running typed handles (integration)" [

        // ── 69i.C — the auto-served status companion ──────────────────────

        testAsync "enqueue + poll through <method>/status reaches Succeeded with the typed result" {
            let jobs = InMemoryJobDispatcher() :> IJobDispatcher
            let! host, client = start (anonymousHandler jobs)
            use _ = host
            use _ = client

            let! code, handle = post client "/LongOpApi/Start" (startBody 2 10 0) None None None
            Expect.equal code 200 "the submit method answers synchronously with the handle"
            Expect.stringContains handle "JobHandle" "the handle serialises as the single-case DU"

            let! final = pollUntilTerminal client "/LongOpApi/Start/status" handle None
            Expect.stringContains final "Succeeded" "the companion reaches the terminal status"

            Expect.stringContains
                final
                "done:2:-"
                "the typed result rides the Succeeded arm (no subject: anonymous host)"
        }

        testAsync "the status companion's bytes are identical to a hand-wired poll method's (wire-compat pin)" {
            let jobs = InMemoryJobDispatcher() :> IJobDispatcher
            let! host, client = start (anonymousHandler jobs)
            use _ = host
            use _ = client

            let! _, handle = post client "/LongOpApi/Start" (startBody 1 10 0) None None None
            let! _ = pollUntilTerminal client "/LongOpApi/Poll" handle None

            let body = sprintf "[%s]" handle
            let! companionCode, companionBody = post client "/LongOpApi/Start/status" body None None None
            let! handWiredCode, handWiredBody = post client "/LongOpApi/Poll" body None None None

            Expect.equal companionCode 200 "companion 200"
            Expect.equal handWiredCode 200 "hand-wired 200"

            Expect.equal
                companionBody
                handWiredBody
                "the auto-served companion and the v0 hand-wired method agree byte for byte"

            Expect.stringContains companionBody "Succeeded" "both report the terminal status"
        }

        testAsync
            "a faulting job reports Failed with the exception message; an unknown handle reports Failed job-not-found" {
            let jobs = InMemoryJobDispatcher() :> IJobDispatcher
            let! host, client = start (anonymousHandler jobs)
            use _ = host
            use _ = client

            let! _, handle = post client "/LongOpApi/Start" (startBody 3 5 2) None None None
            let! final = pollUntilTerminal client "/LongOpApi/Start/status" handle None
            Expect.stringContains final "Failed" "the fault is a Failed status, not a 500"
            Expect.stringContains final "work fault" "the message rides the Failed arm"

            let! code, unknown = post client "/LongOpApi/Start/status" """[{"JobHandle":"nope"}]""" None None None

            Expect.equal code 200 "not-found is a status, not an error"
            Expect.stringContains unknown "job-not-found" "unknown handle"
        }

        // ── 69i.D — the progress companion over the 69c SSE framing ───────

        testAsync "enqueue + stream through <method>/progress yields status frames and terminates on Succeeded" {
            let jobs = InMemoryJobDispatcher() :> IJobDispatcher
            let! host, client = start (anonymousHandler jobs)
            use _ = host
            use _ = client

            let! _, handle = post client "/LongOpApi/Start" (startBody 3 60 0) None None None
            let! code, sse = post client "/LongOpApi/Start/progress" (sprintf "[%s]" handle) None None None
            Expect.equal code 200 "SSE 200"

            let frames = SseFrame.parse sse
            let chunks = frames |> List.filter (fun f -> f.Event = "chunk")
            Expect.isNonEmpty chunks "at least one status frame"

            let last = List.last chunks
            Expect.stringContains last.Data "Succeeded" "the terminal frame carries Succeeded"
            Expect.stringContains last.Data "done:3" "with the typed result"

            Expect.equal
                (List.last frames).Event
                "complete"
                "the stream closes with event: complete after the terminal frame"

            // Only CHANGES are framed: no two consecutive chunks are equal.
            chunks
            |> List.pairwise
            |> List.iter (fun (a, b) -> Expect.notEqual a.Data b.Data "consecutive frames differ")
        }

        // ── 69i.F — idempotency-key dedup ──────────────────────────────────

        testAsync "the same idempotency key submitted twice returns the same handle and enqueues one job" {
            let dispatcher = InMemoryJobDispatcher()
            let jobs = dispatcher :> IJobDispatcher

            let handler =
                Remoting.createApi ()
                |> Remoting.fromValue (idempotentImpl jobs)
                |> Remoting.withJobDispatcher jobs
                |> Remoting.withIdempotencyStore (InMemoryIdempotencyStore())
                |> Remoting.buildHttpHandler

            let! host, client = start handler
            use _ = host
            use _ = client

            let body = startBody 1 5 0
            let! c1, h1 = post client "/IdempotentLongOpApi/Start" body None None (Some "key-42")
            let! c2, h2 = post client "/IdempotentLongOpApi/Start" body None None (Some "key-42")
            Expect.equal c1 200 "first submit"
            Expect.equal c2 200 "replayed submit"
            Expect.equal h2 h1 "the replay returns the SAME handle"
            Expect.equal dispatcher.JobCount 1 "exactly one job was enqueued"

            let! c3, h3 = post client "/IdempotentLongOpApi/Start" body None None (Some "key-43")
            Expect.equal c3 200 "a different key"
            Expect.notEqual h3 h1 "is a different job"
            Expect.equal dispatcher.JobCount 2 "and enqueues a second one"
        }

        // ── 69i.E — subject propagation + cross-tenant isolation ──────────

        testAsync "the job carries the submitting subject: the job body observes it and another subject sees not-found" {
            let jobs = InMemoryJobDispatcher() :> IJobDispatcher
            let! host, client = start (authedHandler jobs None)
            use _ = host
            use _ = client

            let! code, handle = post client "/LongOpApi/Start" (startBody 1 5 0) (Some "tenant-a:alice") None None
            Expect.equal code 200 "submit as alice"

            let! own = pollUntilTerminal client "/LongOpApi/Start/status" handle (Some "tenant-a:alice")
            Expect.stringContains own "Succeeded" "the submitter reaches the terminal status"
            Expect.stringContains own "done:1:tenant-a:alice" "the job body saw the submitting subject via CallContext"

            let body = sprintf "[%s]" handle
            let! _, other = post client "/LongOpApi/Start/status" body (Some "tenant-b:bob") None None
            Expect.stringContains other "job-not-found" "another subject cannot see the job (no disclosure)"

            let! _, anon = post client "/LongOpApi/Start/status" body None None None
            Expect.stringContains anon "job-not-found" "an anonymous caller cannot see an owned job"

            let! _, admin = post client "/LongOpApi/Start/status" body (Some "ops:carol") (Some "Admin") None
            Expect.stringContains admin "Succeeded" "a caller holding JobAdminRole sees every job"

            let! _, progress = post client "/LongOpApi/Start/progress" body (Some "tenant-b:bob") None None

            let chunks = SseFrame.parse progress |> List.filter (fun f -> f.Event = "chunk")
            Expect.equal chunks.Length 1 "the progress companion is owner-bound too: one terminal frame"
            Expect.stringContains chunks.Head.Data "job-not-found" "and it is not-found"
        }

        // ── 69i.G — cancellation ───────────────────────────────────────────

        testAsync
            "cancel propagates: status transitions to Cancelled and the progress subscriber receives the terminal frame" {
            let jobs = InMemoryJobDispatcher() :> IJobDispatcher
            let telemetry = CapturingTelemetry()
            let! host, client = start (authedHandler jobs (Some telemetry))
            use _ = host
            use _ = client

            let! _, handle = post client "/LongOpApi/Start" (startBody 200 20 0) (Some "alice") None None
            let body = sprintf "[%s]" handle

            // Subscribe first, cancel while the stream is open.
            let subscription =
                post client "/LongOpApi/Start/progress" body (Some "alice") None None
                |> Async.StartAsTask

            do! Async.Sleep 150

            let! bobCode, _ = post client "/LongOpApi/Start/cancel" body (Some "bob") None None
            Expect.equal bobCode 200 "a foreign cancel is a silent no-op"
            let! _, afterBob = post client "/LongOpApi/Start/status" body (Some "alice") None None
            Expect.isFalse (afterBob.Contains "Cancelled") "bob's cancel did not touch alice's job"

            let! code, cancelBody = post client "/LongOpApi/Start/cancel" body (Some "alice") None None
            Expect.equal code 200 "the owner's cancel is accepted"

            // Wire-compat pin for the cancel companion: its reply is byte-
            // identical to a hand-wired `JobHandle<'T> -> Async<unit>` method's.
            let! discardCode, discardBody = post client "/LongOpApi/Discard" body (Some "alice") None None
            Expect.equal discardCode 200 "the hand-wired unit method"
            Expect.equal cancelBody discardBody "an Async<unit> companion answers exactly as a unit method does"

            let! _, status = post client "/LongOpApi/Start/status" body (Some "alice") None None
            Expect.stringContains status "Cancelled" "status transitions to Cancelled"

            let! _, sse = subscription |> Async.AwaitTask
            let frames = SseFrame.parse sse
            let last = frames |> List.filter (fun f -> f.Event = "chunk") |> List.last
            Expect.stringContains last.Data "Cancelled" "the subscriber's terminal frame is Cancelled"
            Expect.equal (List.last frames).Event "complete" "and the stream closes"

            let names = telemetry.Events |> List.map _.MethodName |> Set.ofList
            Expect.contains names "Start/cancel" "the cancel companion is visible to telemetry"
            Expect.contains names "Start/status" "so is the status companion"
            Expect.contains names "Start/progress" "and the progress companion"
        }

        // ── the dispatcher on its own ──────────────────────────────────────

        testAsync "InMemoryJobDispatcher: Cancel is first-writer-wins against completion and idempotent afterwards" {
            let jobs = InMemoryJobDispatcher() :> IJobDispatcher
            let! handle = jobs.Enqueue(async { return 42 })
            let mutable status = JobStatus<int>.Queued

            let notSucceeded () =
                match status with
                | JobStatus.Succeeded _ -> false
                | _ -> true

            while notSucceeded () do
                do! Async.Sleep 10
                let! s = jobs.GetStatus handle
                status <- s

            do! jobs.Cancel handle
            let! after = jobs.GetStatus handle
            Expect.equal after (JobStatus.Succeeded 42) "a completed job is not overwritten by a later cancel"

            let! slow = jobs.Enqueue(async { do! Async.Sleep 10_000 })
            do! jobs.Cancel slow
            do! jobs.Cancel slow
            let! cancelled = jobs.GetStatus slow
            Expect.equal cancelled JobStatus<unit>.Cancelled "a live job cancels; a second cancel is a no-op"
        }

        testAsync
            "InMemoryJobDispatcher: ownership binds GetStatus and Cancel to the submitting subject unless job-admin" {
            let jobs = InMemoryJobDispatcher() :> IJobDispatcher

            let! handle = async {
                use _ = CallContext.beginSubject (Some "alice") false
                return! jobs.Enqueue(async { do! Async.Sleep 5_000 })
            }

            let! asBob = async {
                use _ = CallContext.beginSubject (Some "bob") false
                return! jobs.GetStatus handle
            }

            Expect.equal asBob (JobStatus<unit>.Failed "job-not-found") "bob sees not-found"

            let! asAdmin = async {
                use _ = CallContext.beginSubject (Some "carol") true
                return! jobs.GetStatus handle
            }

            Expect.isFalse
                (match asAdmin with
                 | JobStatus.Failed _ -> true
                 | _ -> false)
                "a job-admin sees the live job"

            do! async {
                use _ = CallContext.beginSubject (Some "bob") false
                return! jobs.Cancel handle
            }

            let! asAlice = async {
                use _ = CallContext.beginSubject (Some "alice") false
                return! jobs.GetStatus handle
            }

            Expect.notEqual asAlice JobStatus<unit>.Cancelled "bob's cancel was a no-op"

            do! async {
                use _ = CallContext.beginSubject (Some "alice") false
                return! jobs.Cancel handle
            }

            let! final = async {
                use _ = CallContext.beginSubject (Some "alice") false
                return! jobs.GetStatus handle
            }

            Expect.equal final JobStatus<unit>.Cancelled "the owner's cancel lands"
        }

        test "LongRunning.classify finds Async<JobHandle<'T>> shapes (unary and curried) and nothing else" {
            let shapes = LongRunning.classify typeof<LongOpApi>

            Expect.equal
                (Map.toList shapes)
                [ "Start", typeof<string> ]
                "Start is long-running with 'T = string; Poll and Discard are not"

            Expect.isTrue
                (Map.isEmpty (LongRunning.classify typeof<SlowRequest>))
                "a record with no function fields classifies nothing"
        }
    ]