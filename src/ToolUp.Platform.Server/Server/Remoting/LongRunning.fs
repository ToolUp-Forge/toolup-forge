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

/// Phase 69i — which companion a request addresses.
[<RequireQualifiedAccess>]
type internal JobCompanion =
    | Status
    | Progress
    | Cancel

module internal LongRunning =

    // Reflect over public AND non-public records, for the same reason the
    // streaming and auth classifiers do: an internal API record must arm
    // the classification exactly like a public one.
    let private reflectionFlags = BindingFlags.Public ||| BindingFlags.NonPublic

    /// Phase 69i.D — the interval at which the progress companion polls the
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

    /// Cache per-method classification at startup. Returns a map from
    /// method name → the handle's result type `'T` ONLY for methods whose
    /// function shape finally returns `Async<JobHandle<'T>>`; other methods
    /// are absent so per-call lookup is a fast Map.tryFind miss.
    let classify (apiType: Type) : Map<string, Type> =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            Map.empty
        else
            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.choose (fun apiField ->
                if FSharpType.IsFunction apiField.PropertyType then
                    resultTypeOfJobHandleAsync (finalReturn apiField.PropertyType)
                    |> Option.map (fun resultType -> apiField.Name, resultType)
                else
                    None)
            |> Map.ofArray

    /// The closed `JobHandle<'T>` type for a result type.
    let handleType (resultType: Type) : Type =
        typedefof<JobHandle<_>>.MakeGenericType([| resultType |])

    /// The closed `JobStatus<'T>` type for a result type.
    let statusType (resultType: Type) : Type =
        typedefof<JobStatus<_>>.MakeGenericType([| resultType |])

    /// Resolve `<method>/<companion>` from a request path: the last two
    /// segments name a classified long-running method and one of the three
    /// companion suffixes. Every ordinary method route ends in ONE segment
    /// after the record name, so a real method can never match unless a
    /// record is itself named for a long-running method AND a method is
    /// named `status` / `progress` / `cancel` — and even then only when the
    /// record carries long-running methods, since the map is otherwise
    /// empty and this is a fast miss.
    let tryResolveCompanion (shapes: Map<string, Type>) (path: string) : (string * JobCompanion * Type) option =
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
                | Some c, Some resultType -> Some(methodName, c, resultType)
                | _ -> None

    /// Phase 69i.E — the subject a resolved auth context contributes to the
    /// call context: its `SubjectId`, unless the caller is anonymous (an
    /// anonymous "subject" would let every anonymous caller read every
    /// anonymous job, which is the same as no owner — so say so).
    let subjectOf (authContext: IAuthContext option) : string option =
        match authContext with
        | Some ctx when not (ctx.IsAnonymous()) -> Some ctx.SubjectId
        | _ -> None

    /// Build a `JobHandle<'T>` value for `resultType` from a bare job id —
    /// used when a companion body carried no parseable handle, so the call
    /// resolves through the dispatcher's own not-found arm rather than a
    /// second error shape.
    let emptyHandle (resultType: Type) : obj =
        let case = FSharpType.GetUnionCases(handleType resultType)[0]
        FSharpValue.MakeUnion(case, [| box "" |])

    let private isTerminal (status: JobStatus<'T>) =
        match status with
        | JobStatus.Succeeded _
        | JobStatus.Failed _
        | JobStatus.Cancelled -> true
        | JobStatus.Queued
        | JobStatus.Running _ -> false

    // ── Typed bridges, closed over `'T` via MakeGenericMethod ─────────────
    //
    // The companion route knows `'T` only as a `Type` (from the
    // classification), while `IJobDispatcher` is generic per call. Each
    // bridge below is a generic method the route closes with
    // `MakeGenericMethod resultType` and invokes with the boxed handle; the
    // bridge does the typed call and boxes the answer back.

    /// 69i.C — the poll companion: `GetStatus` on the composed dispatcher.
    let statusOf<'T> (dispatcher: IJobDispatcher) (handle: obj) : Async<obj> = async {
        let! status = dispatcher.GetStatus<'T>(handle :?> JobHandle<'T>)
        return box status
    }

    /// 69i.G — the cancel companion: `Cancel` on the composed dispatcher.
    let cancelOf<'T> (dispatcher: IJobDispatcher) (handle: obj) : Async<obj> = async {
        do! dispatcher.Cancel<'T>(handle :?> JobHandle<'T>)
        return box ()
    }

    /// 69i.D — the progress companion: a cold `IAsyncEnumerable<JobStatus<'T>>`
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

        AsyncStream.fromCallback isTerminal (fun emit -> async {
            use _caller = CallContext.beginSubject subject jobAdmin
            let mutable last: obj = null
            let mutable finished = false

            while not finished do
                let! status = dispatcher.GetStatus<'T> typedHandle

                if not (obj.Equals(box status, last)) then
                    emit status
                    last <- box status

                if isTerminal status then
                    finished <- true
                else
                    do! Async.Sleep PollInterval
        })

    let private bridge (name: string) (resultType: Type) : MethodInfo =
        let openMethod =
            typeof<JobCompanion>.Assembly
                .GetType("ToolUp.Remoting.Server.LongRunning")
                .GetMethod(name, BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static)

        openMethod.MakeGenericMethod([| resultType |])

    /// Run the `status` or `cancel` companion for `resultType`, returning
    /// the boxed `JobStatus<'T>` (status) or boxed `unit` (cancel).
    let invokeCompanion
        (companion: JobCompanion)
        (dispatcher: IJobDispatcher)
        (resultType: Type)
        (handle: obj)
        : Async<obj> =
        let name =
            match companion with
            | JobCompanion.Status -> "statusOf"
            | JobCompanion.Cancel -> "cancelOf"
            | JobCompanion.Progress -> invalidArg "companion" "progress is served by the SSE path (subscribeCompanion)"

        (bridge name resultType).Invoke(null, [| box dispatcher; handle |]) :?> Async<obj>

    /// Build the boxed `IAsyncEnumerable<JobStatus<'T>>` the progress
    /// companion streams, for `resultType`.
    let subscribeCompanion
        (dispatcher: IJobDispatcher)
        (resultType: Type)
        (subject: string option)
        (jobAdmin: bool)
        (handle: obj)
        : obj =
        (bridge "subscribe" resultType).Invoke(null, [| box dispatcher; box subject; box jobAdmin; handle |])