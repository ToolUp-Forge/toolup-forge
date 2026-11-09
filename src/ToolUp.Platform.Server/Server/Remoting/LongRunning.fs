namespace ToolUp.Remoting.Server

open System
open System.Collections.Generic
open System.Reflection
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69i.B/C/D/G — long-running method classification + the companions
// =============================================================================
//
// API record fields whose F# function shape finally returns
// `Async<JobHandle<'T>>` are classified as LONG-RUNNING at startup — the
// same startup reflection `Streaming.classify` performs for
// `IAsyncEnumerable<'T>` — and, when an `IJobDispatcher` is composed via
// `Remoting.withJobDispatcher`, the dispatcher serves three COMPANION routes
// beside the method's own route, none of which the consumer authors:
//
//   * `<route>/status`   — `JobHandle<'T> -> Async<JobStatus<'T>>`   (69i.C)
//   * `<route>/progress` — `JobHandle<'T> -> IAsyncEnumerable<JobStatus<'T>>`
//                          over the Phase 69c SSE framing                (69i.D)
//   * `<route>/cancel`   — `JobHandle<'T> -> Async<unit>`               (69i.G)
//
// The submit method itself is an ORDINARY method: it runs the whole
// pre-flight chain (auth / validation / idempotency / rate-limit / audit)
// and its handler enqueues through the composed dispatcher — which is how
// 69i.E (authorization before enqueue) and 69i.F (an idempotency key
// replays the same handle without a second job) hold by construction rather
// than by a second mechanism. The companions are served by the dispatcher
// directly, off the same instance, so the handle a client holds resolves.
//
// Why routes off the classification rather than generated members: Phase
// 69k's generator is build-time over built metadata with checked-in output,
// so "auto-acquire at compose time" through it would mean a generation step
// and a consumer edit per adopting record. A route the dispatcher exposes
// from the classification it already computes needs neither, and the
// companion's `'T` is fixed per METHOD (a record field cannot be generic),
// which is exactly the shape the per-method route carries. Recorded as the
// 69i.C decision in `docs/migrations/69i-long-running-handle.md`.

/// Which companion of a long-running method a request addresses.
[<RequireQualifiedAccess>]
type JobCompanion =
    | Status
    | Progress
    | Cancel

module internal LongRunning =

    /// The interval at which the progress companion polls the
    /// dispatcher between frames. The v0 dispatcher has no push seam, so
    /// the subscription is a poll loop that emits only status CHANGES; a
    /// dispatcher with its own change feed can shorten this to nothing.
    let PollInterval = TimeSpan.FromMilliseconds 100.0

    /// The final return type of a (possibly curried) F# function type.
    let rec private finalReturn (t: Type) : Type =
        if FSharpType.IsFunction t then
            finalReturn (snd (FSharpType.GetFunctionElements t))
        else
            t

    /// `Some 'T` when `t` is `Async<JobHandle<'T>>`, else `None`.
    let resultTypeOfJobHandleAsync (t: Type) : Type option =
        if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Async<_>> then
            let inner = t.GetGenericArguments()[0]

            if
                inner.IsGenericType
                && inner.GetGenericTypeDefinition() = typedefof<JobHandle<_>>
            then
                Some(inner.GetGenericArguments()[0])
            else
                None
        else
            None

    /// One field's long-running result type `'T`, when its function shape
    /// finally returns `Async<JobHandle<'T>>`.
    let resultOf (apiField: PropertyInfo) : Type option =
        if FSharpType.IsFunction apiField.PropertyType then
            resultTypeOfJobHandleAsync (finalReturn apiField.PropertyType)
        else
            None

    /// Cache per-method classification at startup. Returns a map from
    /// method name → the handle's result type `'T` ONLY for methods whose
    /// function shape finally returns `Async<JobHandle<'T>>`; other methods
    /// are absent so per-call lookup is a fast Map.tryFind miss.
    let classify (apiType: Type) : Map<string, Type> =
        ApiRecord.fields apiType
        |> Array.choose (fun apiField -> resultOf apiField |> Option.map (fun resultType -> apiField.Name, resultType))
        |> Map.ofArray

    /// Resolve `<method>/<companion>` from a request path: the last two
    /// segments name a classified long-running method and one of the three
    /// companion suffixes. Every ordinary method route ends in ONE segment
    /// after the record name, so a real method can never match unless a
    /// record is itself named for a long-running method AND a method is
    /// named `status` / `progress` / `cancel` — and even then only when the
    /// record carries long-running methods, since the map is otherwise
    /// empty and this is a fast miss.
    let tryResolveCompanion (shapes: Map<string, 'calls>) (path: string) : (string * JobCompanion * 'calls) option =
        if Map.isEmpty shapes || String.IsNullOrEmpty path then
            None
        else
            let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)

            if segments.Length < 2 then
                None
            else
                let methodName = segments[segments.Length - 2]

                let companion =
                    match segments[segments.Length - 1] with
                    | "status" -> Some JobCompanion.Status
                    | "progress" -> Some JobCompanion.Progress
                    | "cancel" -> Some JobCompanion.Cancel
                    | _ -> None

                match companion, Map.tryFind methodName shapes with
                | Some c, Some calls -> Some(methodName, c, calls)
                | _ -> None

    /// The subject a resolved auth context contributes to the
    /// call context: its `SubjectId`, unless the caller is anonymous (an
    /// anonymous "subject" would let every anonymous caller read every
    /// anonymous job, which is the same as no owner — so say so).
    let subjectOf (authContext: IAuthContext option) : string option =
        match authContext with
        | Some ctx when not (ctx.IsAnonymous()) -> Some ctx.SubjectId
        | _ -> None

    // ── Typed companion calls ──────────────────────────────────────────
    //
    // `IJobDispatcher` is generic per call, while the companion route serves
    // a method whose `'T` the dispatcher table knows as data. Each method's
    // three calls are closed over its `'T` ONCE, when its contract is built
    // (`JobCompanionCalls.For<'T>`), so a request runs a typed closure.

    /// The poll companion: `GetStatus` on the composed dispatcher.
    let statusOf<'T> (dispatcher: IJobDispatcher) (handle: obj) : Async<obj> = async {
        let! status = dispatcher.GetStatus<'T>(handle :?> JobHandle<'T>)
        return box status
    }

    /// The cancel companion: `Cancel` on the composed dispatcher.
    let cancelOf<'T> (dispatcher: IJobDispatcher) (handle: obj) : Async<obj> = async {
        do! dispatcher.Cancel<'T>(handle :?> JobHandle<'T>)
        return box ()
    }

    /// The progress companion: a cold `IAsyncEnumerable<JobStatus<'T>>`
    /// that polls the dispatcher every `PollInterval`, yields each status
    /// CHANGE, and ends with the first terminal status (`Succeeded` /
    /// `Failed` / `Cancelled`) — the shape `AsyncStream.fromCallback`
    /// requires. The subject and job-admin standing of the subscribing
    /// caller are re-established inside the producer, so ownership is
    /// evaluated for the subscriber whatever thread the loop lands on.
    let subscribe<'T>
        (dispatcher: IJobDispatcher)
        (subject: string option)
        (jobAdmin: bool)
        (handle: obj)
        : IAsyncEnumerable<JobStatus<'T>> =
        let typedHandle = handle :?> JobHandle<'T>

        AsyncStream.fromCallback JobStatusTerminal.isTerminal (fun emit -> async {
            use _caller = CallContext.beginSubject subject jobAdmin
            let mutable last: obj = null
            let mutable finished = false

            while not finished do
                let! status = dispatcher.GetStatus<'T> typedHandle

                if not (obj.Equals(box status, last)) then
                    emit status
                    last <- box status

                if JobStatusTerminal.isTerminal status then
                    finished <- true
                else
                    do! Async.Sleep PollInterval
        })

/// A long-running method's companion calls, closed over its result type once,
/// when the method's contract is built. Two values are equal when they serve
/// the same result type.
[<Sealed>]
type JobCompanionCalls
    private
    (
        resultType: Type,
        handleType: Type,
        statusType: Type,
        emptyHandle: obj,
        status: IJobDispatcher -> obj -> Async<obj>,
        cancel: IJobDispatcher -> obj -> Async<obj>,
        subscribe: IJobDispatcher -> string option -> bool -> obj -> obj
    ) =

    /// The job's result type `'T`.
    member _.ResultType = resultType

    /// `JobHandle<'T>`: the type a companion's body decodes to.
    member internal _.HandleType = handleType

    /// `JobStatus<'T>`: what the status and progress companions answer with.
    member internal _.StatusType = statusType

    /// A handle naming no job, for a body that carried none: the call then
    /// resolves through the dispatcher's own not-found arm.
    member internal _.EmptyHandle = emptyHandle

    /// The poll companion: `GetStatus` on the composed dispatcher.
    member internal _.Status = status

    /// The cancel companion: `Cancel` on the composed dispatcher.
    member internal _.Cancel = cancel

    /// The progress companion's `IAsyncEnumerable<JobStatus<'T>>`, boxed.
    member internal _.Subscribe = subscribe

    override _.Equals(other: obj) =
        match other with
        | :? JobCompanionCalls as calls -> calls.ResultType = resultType
        | _ -> false

    override _.GetHashCode() = resultType.GetHashCode()

    /// The calls for a method whose handle carries `'T`.
    static member For<'T>() : JobCompanionCalls =
        JobCompanionCalls(
            typeof<'T>,
            typeof<JobHandle<'T>>,
            typeof<JobStatus<'T>>,
            box (JobHandle "": JobHandle<'T>),
            LongRunning.statusOf<'T>,
            LongRunning.cancelOf<'T>,
            fun dispatcher subject jobAdmin handle -> box (LongRunning.subscribe<'T> dispatcher subject jobAdmin handle)
        )