// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Client

open Fable.Core.JsInterop
open Fable.SimpleJson
open ToolUp.Remoting

/// Phase 783 — a minimal, total JSON scanner, used only to lift the
/// `decodeError` detail out of an error envelope.
///
/// It exists because neither obvious tool works on both hosts.
/// `Fable.SimpleJson.parse` is Fable-RUNTIME-only — its parser is built
/// on `Fable.Parsimmon`, whose .NET assembly is binding stubs that throw
/// "You've hit dummy code used for Fable bindings" — so a client
/// classifier written on it cannot be exercised in the .NET test harness
/// at all, and `SimpleJson.parseNative` is `JSON.parse` and does not
/// exist off the browser. `System.Text.Json` is not Fable-compilable.
/// A regex (the shape `ScopeDenial` reached for, and for this same
/// reason) is host-agnostic but cannot read a value containing an escaped
/// quote — and one of the two fields read here is a serialiser's own
/// exception message, which is precisely the field most likely to carry
/// one.
///
/// So: one implementation, string-escape-aware, running identically under
/// Fable and under .NET, which is what makes the client half of this
/// phase testable rather than merely written.
module private DecodeErrorWire =

    /// Decode the JSON string literal whose opening quote is at `start`.
    /// Returns the value and the index just past the closing quote;
    /// `None` if `start` is not a quote or the literal is unterminated.
    let readString (source: string) (start: int) : (string * int) option =
        if start >= source.Length || source[start] <> '"' then
            None
        else
            let sb = System.Text.StringBuilder()
            let mutable i = start + 1
            let mutable closed = false

            while not closed && i < source.Length do
                let c = source[i]

                if c = '\\' && i + 1 < source.Length then
                    match source[i + 1] with
                    | 'n' -> sb.Append '\n' |> ignore
                    | 't' -> sb.Append '\t' |> ignore
                    | 'r' -> sb.Append '\r' |> ignore
                    | 'b' -> sb.Append '\b' |> ignore
                    | 'f' -> sb.Append '\f' |> ignore
                    | 'u' when i + 5 < source.Length ->
                        let mutable code = 0
                        let mutable valid = true

                        for k in 2..5 do
                            let d = source[i + k]

                            let digit =
                                if d >= '0' && d <= '9' then
                                    int d - int '0'
                                elif d >= 'a' && d <= 'f' then
                                    int d - int 'a' + 10
                                elif d >= 'A' && d <= 'F' then
                                    int d - int 'A' + 10
                                else
                                    valid <- false
                                    0

                            code <- code * 16 + digit

                        if valid then
                            sb.Append(char code) |> ignore
                    | other -> sb.Append other |> ignore

                    i <-
                        i
                        + (if source[i + 1] = 'u' && i + 5 < source.Length then
                               6
                           else
                               2)
                elif c = '"' then
                    closed <- true
                    i <- i + 1
                else
                    sb.Append c |> ignore
                    i <- i + 1

            if closed then Some(sb.ToString(), i) else None

    /// Index of the first character of the VALUE bound to `key`. Only
    /// matches an occurrence that is actually in key position (followed
    /// by a colon), so a key name appearing inside some other value does
    /// not divert the scan.
    let valueIndexOf (source: string) (key: string) : int option =
        let needle = "\"" + key + "\""

        let rec search (from: int) =
            let idx = source.IndexOf(needle, from)

            if idx < 0 then
                None
            else
                let mutable k = idx + needle.Length

                while k < source.Length && System.Char.IsWhiteSpace source[k] do
                    k <- k + 1

                if k < source.Length && source[k] = ':' then
                    let mutable v = k + 1

                    while v < source.Length && System.Char.IsWhiteSpace source[v] do
                        v <- v + 1

                    Some v
                else
                    search (idx + needle.Length)

        search 0

    /// The complete JSON object whose opening brace is at `start`, braces
    /// included. Depth-counted and string-aware, so a brace inside a
    /// string value does not close the object early.
    let objectAt (source: string) (start: int) : string option =
        if start >= source.Length || source[start] <> '{' then
            None
        else
            let mutable depth = 0
            let mutable i = start
            let mutable inString = false
            let mutable result = None

            while result.IsNone && i < source.Length do
                let c = source[i]

                if inString then
                    if c = '\\' then
                        i <- i + 1
                    elif c = '"' then
                        inString <- false
                elif c = '"' then
                    inString <- true
                elif c = '{' then
                    depth <- depth + 1
                elif c = '}' then
                    depth <- depth - 1

                    if depth = 0 then
                        result <- Some(source.Substring(start, i - start + 1))

                i <- i + 1

            result

    /// The array of JSON strings whose opening bracket is at `start`.
    /// Stops at the first element that is not a string — the path is
    /// always a string array, and a partial read is preferable to a throw.
    let readStringArray (source: string) (start: int) : string list =
        if start >= source.Length || source[start] <> '[' then
            []
        else
            let items = ResizeArray<string>()
            let mutable i = start + 1
            let mutable go = true

            while go && i < source.Length do
                while i < source.Length && (System.Char.IsWhiteSpace source[i] || source[i] = ',') do
                    i <- i + 1

                match readString source i with
                | Some(value, next) ->
                    items.Add value
                    i <- next
                | None -> go <- false

            List.ofSeq items

/// Phase 854 — the client read policies: identical in-flight reads share
/// one request, and a `Cacheable` read may be served stale-while-
/// revalidate.
///
/// **Declared, never inferred (GP 11).** A method is shared or cached only
/// when its API record's policy says so. The declaration is the
/// `[<Cacheable>]` / `[<Invalidates>]` attributes on the record
/// (`ToolUp.Platform`); because Fable's reflection metadata carries no
/// custom attributes, a browser proxy receives it as data through
/// `register`, and on .NET `ofAttributes` reads the attributes into the
/// same data so a test can pin the two equal. With nothing registered every
/// proxy call — and every `Cmd.OfRemoting` call — takes the pre-854 path
/// unchanged.
///
/// **One table per page, not per proxy.** The shell builds several proxies
/// of one API record (four of `TeamApi`), so a per-proxy table would not
/// see the second caller it exists for. A request is identified by what
/// goes on the wire — HTTP method, URL, the proxy's own headers and the
/// exact request body — so two proxies that would send the same bytes
/// share, and two that would not, do not. The key is the text itself, not
/// a hash of it: a hash would need a collision story, the text does not.
///
/// **Identity.** The request guard adds the caller's identity at send
/// time, below this table, so a cached result belongs to the identity that
/// fetched it; `clear` drops everything and is called by `UserSession`
/// whenever the signed-in identity changes.
module ReadPolicies =

    open System
    open System.Collections.Generic

    /// One request shared by every caller that reached it while it was in
    /// flight. It is sent at most once, by the first caller that awaits it,
    /// and settles once: every raw awaiter receives the raw outcome, and
    /// every `AwaitFinal` awaiter receives it after the interceptor chain
    /// that `TryClaimChain` installed (when one did).
    [<AllowNullLiteral>]
    type SharedRequest
        internal
        (
            key: string,
            methodId: string,
            cacheFor: TimeSpan option,
            send: unit -> Async<obj>,
            onSettled: SharedRequest -> Result<obj, exn> -> unit
        ) =
        let rawWaiters = List<Result<obj, exn> -> unit>()
        let finalWaiters = List<Result<obj, exn> -> unit>()
        let mutable started = false
        let mutable outcome: (Result<obj, exn> * Result<obj, exn>) option = None
        let mutable chain: (Result<obj, exn> -> Result<obj, exn>) option = None

        member internal _.Key = key
        member internal _.MethodId = methodId
        member internal _.CacheFor = cacheFor

        /// Set when an invalidation or `clear` detached the request from
        /// the shared table: its waiters still receive its result, but the
        /// result is not cached.
        member val internal Detached = false with get, set

        /// Whether the request has completed.
        member _.IsSettled = outcome.IsSome

        /// Install the interceptor chain this request's outcome passes
        /// through, once. `false` when another caller installed one first,
        /// or the request already settled — either way the caller is a
        /// waiter, not the chain's owner.
        member _.TryClaimChain(intercept: Result<obj, exn> -> Result<obj, exn>) : bool =
            if outcome.IsSome || chain.IsSome then
                false
            else
                chain <- Some intercept
                true

        member internal this.Start() =
            if not started then
                started <- true

                let work =
                    try
                        send ()
                    with ex -> async { return raise ex }

                Async.StartWithContinuations(
                    work,
                    (fun value -> this.Settle(Ok value)),
                    (fun ex -> this.Settle(Error ex)),
                    (fun cancelled -> this.Settle(Error(cancelled :> exn)))
                )

        member private this.Settle(raw: Result<obj, exn>) =
            if outcome.IsNone then
                let final =
                    match chain with
                    | Some intercept ->
                        try
                            intercept raw
                        with _ ->
                            raw
                    | None -> raw

                outcome <- Some(raw, final)
                // The table is brought up to date BEFORE any waiter runs, so
                // a continuation that calls again sees the settled state.
                onSettled this raw
                let raws = rawWaiters.ToArray()
                let finals = finalWaiters.ToArray()
                rawWaiters.Clear()
                finalWaiters.Clear()

                for resolve in raws do
                    resolve raw

                for resolve in finals do
                    resolve final

        member private this.Await
            (pick: Result<obj, exn> * Result<obj, exn> -> Result<obj, exn>, waiters: List<Result<obj, exn> -> unit>)
            : Async<Result<obj, exn>> =
            Async.FromContinuations(fun (resolve, _, _) ->
                match outcome with
                | Some settled -> resolve (pick settled)
                | None ->
                    waiters.Add resolve
                    this.Start())

        /// Await the outcome after the interceptor chain (sending the
        /// request if nothing has yet). What `Cmd.OfRemoting` awaits.
        member this.AwaitFinal() : Async<Result<obj, exn>> = this.Await(snd, finalWaiters)

        member internal this.AwaitRaw() : Async<obj> = async {
            let! raw = this.Await(fst, rawWaiters)

            match raw with
            | Ok value -> return value
            | Error ex -> return raise ex
        }

    /// What invoking a declared read found. Read by `Cmd.OfRemoting`.
    type ReadLeg =
        /// No result within its max age: the caller awaits this request —
        /// one already in flight, or a new one it will send.
        | Awaiting of request: SharedRequest
        /// A result within its max age, and the request that refreshes it.
        | Served of cached: obj * refresh: SharedRequest

    /// What `tryObserve` saw of one call.
    type Observation<'r> =
        /// An undeclared call (or not a proxy call at all): run it as is.
        | Plain of call: Async<'r>
        /// A declared read's leg.
        | Shared of leg: ReadLeg

    type private Cached = {
        Value: obj
        StoredAt: DateTime
        MaxAge: TimeSpan
        MethodId: string
    }

    /// A bound on the cached results one page holds; past it the expired
    /// ones go, then the oldest.
    [<Literal>]
    let private MaxCachedResults = 256

    let private registry = Dictionary<string, Dictionary<string, ReadPolicy>>()
    let private inFlight = Dictionary<string, SharedRequest>()
    let private cache = Dictionary<string, Cached>()

    /// The declared read an invocation produced, with the call it
    /// returned — read back by `tryObserve`, synchronously.
    let mutable private lastInvocation: (obj * ReadLeg) option = None

    let private methodIdOf (apiFullName: string) (methodName: string) = apiFullName + "." + methodName

    /// Declare the read policies of one API record, by the record type's
    /// `FullName` and each method's field name. Additive: a later
    /// declaration of the same method replaces the earlier one. Proxies
    /// read the table per call, so registration need not precede the
    /// proxy's construction.
    let register (apiFullName: string) (declarations: (string * ReadPolicy) list) : unit =
        let table =
            match registry.TryGetValue apiFullName with
            | true, table -> table
            | _ ->
                let table = Dictionary<string, ReadPolicy>()
                registry.[apiFullName] <- table
                table

        for methodName, policy in declarations do
            table.[methodName] <- policy

    /// `register` for the API record `'TApi`.
    let inline registerFor<'TApi> (declarations: (string * ReadPolicy) list) : unit =
        register typeof<'TApi>.FullName declarations

    /// Whether any read policy is registered. `false` keeps every call on
    /// the pre-854 path.
    let anyDeclared () : bool = registry.Count > 0

    /// The policy declared for one method, if any.
    let tryFind (apiFullName: string) (methodName: string) : ReadPolicy option =
        if registry.Count = 0 then
            None
        else
            match registry.TryGetValue apiFullName with
            | true, table ->
                match table.TryGetValue methodName with
                | true, policy -> Some policy
                | _ -> None
            | _ -> None

    let private detach (request: SharedRequest) =
        request.Detached <- true

        match inFlight.TryGetValue request.Key with
        | true, current when obj.ReferenceEquals(current, request) -> inFlight.Remove request.Key |> ignore
        | _ -> ()

    /// Make the named reads of one API record stale: drop their cached
    /// results and detach their in-flight requests, so the next call goes
    /// to the server. What a successful `Invalidates` method does; callable
    /// directly for a change the client learns of another way.
    let invalidate (apiFullName: string) (methodNames: string list) : unit =
        let ids = methodNames |> List.map (methodIdOf apiFullName) |> Set.ofList

        let staleKeys = [
            for KeyValue(key, cached) in cache do
                if ids.Contains cached.MethodId then
                    key
        ]

        for key in staleKeys do
            cache.Remove key |> ignore

        let detached = [
            for KeyValue(_, request) in inFlight do
                if ids.Contains request.MethodId then
                    request
        ]

        for request in detached do
            detach request

    /// Drop every cached result and detach every in-flight request. Called
    /// when the signed-in identity changes, since a cached result belongs to
    /// the identity that fetched it.
    let clear () : unit =
        cache.Clear()

        for request in List.ofSeq inFlight.Values do
            request.Detached <- true

        inFlight.Clear()

    let private store (request: SharedRequest) (value: obj) (maxAge: TimeSpan) =
        let now = DateTime.UtcNow

        cache.[request.Key] <- {
            Value = value
            StoredAt = now
            MaxAge = maxAge
            MethodId = request.MethodId
        }

        if cache.Count > MaxCachedResults then
            let expired = [
                for KeyValue(key, cached) in cache do
                    if now - cached.StoredAt >= cached.MaxAge then
                        key
            ]

            for key in expired do
                cache.Remove key |> ignore

            if cache.Count > MaxCachedResults then
                let oldest = cache |> Seq.minBy (fun entry -> entry.Value.StoredAt)
                cache.Remove oldest.Key |> ignore

    let private settled
        (apiFullName: string)
        (invalidates: string list)
        (request: SharedRequest)
        (raw: Result<obj, exn>)
        =
        let wasDetached = request.Detached
        detach request

        match raw with
        | Ok value ->
            match request.CacheFor with
            | Some maxAge when not wasDetached -> store request value maxAge
            | _ -> ()

            if not invalidates.IsEmpty then
                invalidate apiFullName invalidates
        | Error _ ->
            // A failed refresh must not leave its stale predecessor being
            // served: the next call goes to the server.
            if not wasDetached then
                cache.Remove request.Key |> ignore

    let private legFor
        (apiFullName: string)
        (methodName: string)
        (policy: ReadPolicy)
        (maxAgeSeconds: int)
        (key: string)
        (send: unit -> Async<obj>)
        : ReadLeg =
        let request =
            match inFlight.TryGetValue key with
            | true, request -> request
            | _ ->
                let cacheFor =
                    if maxAgeSeconds > 0 then
                        Some(TimeSpan.FromSeconds(float maxAgeSeconds))
                    else
                        None

                let request =
                    SharedRequest(
                        key,
                        methodIdOf apiFullName methodName,
                        cacheFor,
                        send,
                        settled apiFullName policy.Invalidates
                    )

                inFlight.[key] <- request
                request

        match cache.TryGetValue key with
        | true, cached when DateTime.UtcNow - cached.StoredAt < cached.MaxAge -> Served(cached.Value, request)
        | true, _ ->
            cache.Remove key |> ignore
            Awaiting request
        | _ -> Awaiting request

    /// The proxy's half: `send` is the undeclared request, `key` the
    /// request's wire identity (`None` for a body that cannot be compared,
    /// a multipart upload). A read decides its leg HERE, at invocation, so
    /// `Cmd.OfRemoting` can read it synchronously; the returned call uses
    /// that leg on its first run and decides afresh on any later run, so a
    /// re-run `Async` is a new call, as an undeclared one is.
    let internal invoke
        (apiFullName: string)
        (methodName: string)
        (policy: ReadPolicy)
        (key: string option)
        (send: unit -> Async<obj>)
        : Async<obj> =
        match policy.MaxAgeSeconds, key with
        | Some maxAgeSeconds, Some key ->
            let decide () =
                legFor apiFullName methodName policy maxAgeSeconds key send

            let leg = decide ()
            let first = ref (Some leg)

            let call = async {
                let leg =
                    match first.Value with
                    | Some leg ->
                        first.Value <- None
                        leg
                    | None -> decide ()

                match leg with
                | Served(value, refresh) ->
                    refresh.Start()
                    return value
                | Awaiting request -> return! request.AwaitRaw()
            }

            lastInvocation <- Some(box call, leg)
            call
        | _ when not policy.Invalidates.IsEmpty -> async {
            let! value = send ()
            invalidate apiFullName policy.Invalidates
            return value
          }
        | _ -> send ()

    /// `Cmd.OfRemoting`'s half: invoke a proxy call and report whether it
    /// was a declared read. `None` — without invoking — when nothing is
    /// registered, so the caller runs its pre-854 path exactly. A call
    /// that throws while being invoked is reported as a `Plain` call that
    /// raises the same exception when run.
    let tryObserve (invoke: unit -> Async<'r>) : Observation<'r> option =
        if registry.Count = 0 then
            None
        else
            let saved = lastInvocation
            lastInvocation <- None

            let observation =
                try
                    let call = invoke ()

                    match lastInvocation with
                    | Some(recorded, leg) when obj.ReferenceEquals(recorded, box call) -> Shared leg
                    | _ -> Plain call
                with ex ->
                    Plain(async { return raise ex })

            lastInvocation <- saved
            Some observation

#if !FABLE_COMPILER
    /// The .NET host's reading of an API record's `[<Cacheable>]` /
    /// `[<Invalidates>]` attributes, as the declarations `register` takes.
    /// A Fable client cannot run this (its reflection carries no
    /// attributes); a .NET test asserting it equal to the list the client
    /// registers is what keeps the two declarations from drifting.
    let ofAttributes (apiType: Type) : (string * ReadPolicy) list = [
        for field in
            Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(
                apiType,
                System.Reflection.BindingFlags.Public
                ||| System.Reflection.BindingFlags.NonPublic
            ) do
            let maxAge =
                field.GetCustomAttributes(typeof<ToolUp.Platform.CacheableAttribute>, true)
                |> Array.tryHead
                |> Option.map (fun attribute -> (attribute :?> ToolUp.Platform.CacheableAttribute).MaxAgeSeconds)

            let invalidates =
                field.GetCustomAttributes(typeof<ToolUp.Platform.InvalidatesAttribute>, true)
                |> Array.collect (fun attribute -> (attribute :?> ToolUp.Platform.InvalidatesAttribute).MethodNames)
                |> List.ofArray

            if maxAge.IsSome || not invalidates.IsEmpty then
                field.Name,
                {
                    MaxAgeSeconds = maxAge
                    Invalidates = invalidates
                }
    ]
#endif

/// Phase 855 — same-tick calls travel as one request.
///
/// Opt-in, page-wide (GP 11): `enable` names the route the server's
/// `RemotingBatch` middleware serves (`ServerApp.withRemotingBatching`
/// composes it at `DefaultRoute`). From then on a proxy call does not go
/// to the wire when it is made; it is queued, and at the next microtask
/// boundary every queued call is sent — one plain request when a call is
/// alone, one ENVELOPE (`POST <route>`, a JSON array of `{ route, body }`)
/// when more than one is pending. The server runs each element through
/// its ordinary per-call pipeline and answers `{ status, body }` per
/// element, in order; each call's own response pipeline (decode, error
/// categorisation, `ProxyRequestException`) then reads its element
/// exactly as it would have read a response of its own.
///
/// **What is never batched.** Streaming methods (`RemoteStream` sends
/// with its own `fetch`), multipart uploads, and binary
/// (`withBinarySerialization` / `byte[]`-returning) methods travel alone,
/// as before. Calls whose proxies send different headers, credentials or
/// base URLs never share an envelope.
///
/// **Composition with Phase 854.** The read-policy table sits ABOVE this
/// queue: identical in-flight reads are shared before anything is queued,
/// so an envelope never carries a duplicate, and a stale-while-revalidate
/// refresh is an ordinary queued call.
///
/// **Refusal.** The server refuses a whole envelope that names a
/// streaming or long-running route, or exceeds its element bound, with
/// `400` + `remoting_batch_refused` before running any element; the
/// calls are then re-sent one request each, so enabling batching never
/// changes a call's outcome.
module RemoteBatching =

    open System.Collections.Generic
    open Fable.Core

    /// The route `RemotingBatch` serves unless composed otherwise.
    [<Literal>]
    let DefaultRoute = "/api/_batch"

    /// The most calls one envelope carries — the server's default bound.
    /// A larger tick is sent as several envelopes.
    [<Literal>]
    let DefaultMaxElements = 32

    /// The error code the server answers a refused envelope with.
    [<Literal>]
    let RefusalCode = "remoting_batch_refused"

    type private Pending = {
        Route: string
        Url: string
        Body: string option
        Cancelled: bool ref
        Resolve: HttpResponse -> unit
        Reject: exn -> unit
    }

    type private Group = {
        BaseUrl: string option
        Headers: (string * string) list
        WithCredentials: bool
        Calls: List<Pending>
    }

    let mutable private batchRoute: string option = None
    let mutable private maxElements = DefaultMaxElements
    let private groups = Dictionary<string, Group>()
    let private groupOrder = List<Group>()
    let mutable private flushScheduled = false

    /// Batch same-tick calls through the envelope route `route` (usually
    /// `DefaultRoute`), at most `DefaultMaxElements` per envelope.
    let enable (route: string) : unit =
        batchRoute <- Some route
        maxElements <- DefaultMaxElements

    /// `enable`, with the element bound set explicitly (at least 2). Keep it
    /// at or under the server's `RemotingBatchOptions.MaxElements`.
    let enableWith (route: string) (maxPerEnvelope: int) : unit =
        if maxPerEnvelope < 2 then
            invalidArg (nameof maxPerEnvelope) "an envelope carries at least two calls"

        batchRoute <- Some route
        maxElements <- maxPerEnvelope

    /// Stop batching: every call from the next one on is sent on its own.
    let disable () : unit = batchRoute <- None

    /// Whether proxy calls are being batched.
    let isEnabled () : bool = batchRoute.IsSome

    [<Emit("Promise.resolve().then(function () { $0(); })")>]
    let private atMicrotaskBoundary (work: unit -> unit) : unit = jsNative

    [<Emit("JSON.stringify($0)")>]
    let private jsonString (text: string) : string = jsNative

    // `[status, body][]` when `text` is an envelope answer of `count`
    // elements, else `null`.
    [<Emit("""(function (text, count) { try { var v = JSON.parse(text); if (!Array.isArray(v) || v.length !== count) { return null; } var out = []; for (var i = 0; i < v.length; i++) { var e = v[i]; if (!e || typeof e.status !== 'number' || typeof e.body !== 'string') { return null; } out.push([e.status, e.body]); } return out; } catch (x) { return null; } })($0, $1)""")>]
    let private decodeAnswer (text: string) (count: int) : (int * string)[] = jsNative

    let private withBase (baseUrl: string option) (route: string) =
        match baseUrl with
        | None -> route
        | Some url -> url.TrimEnd('/') + route

    let private start (request: HttpRequest) (onResponse: HttpResponse -> unit) (onError: exn -> unit) =
        Async.StartWithContinuations(Http.send request, onResponse, onError, (fun cancelled -> onError cancelled))

    let private prepare (group: Group) (request: HttpRequest) =
        request
        |> Http.withHeaders group.Headers
        |> Http.withCredentials group.WithCredentials

    /// A call sent on its own — exactly the request the proxy would have
    /// sent without batching.
    let private sendAlone (group: Group) (call: Pending) =
        let request =
            match call.Body with
            | Some body -> Http.post call.Url |> Http.withBody (RequestBody.Json body)
            | None -> Http.get call.Url

        start (prepare group request) call.Resolve call.Reject

    let private sendEnvelope (group: Group) (route: string) (calls: Pending[]) =
        let body =
            calls
            |> Array.map (fun call ->
                "{\"route\":"
                + jsonString call.Route
                + ",\"body\":"
                + (match call.Body with
                   | Some text -> jsonString text
                   | None -> "null")
                + "}")
            |> String.concat ","

        let request =
            Http.post (withBase group.BaseUrl route)
            |> Http.withBody (RequestBody.Json("[" + body + "]"))
            |> prepare group

        start
            request
            (fun response ->
                if response.StatusCode = 200 then
                    match decodeAnswer response.ResponseBody calls.Length with
                    | null ->
                        let error =
                            exn (
                                sprintf
                                    "The batch envelope answer from %s is not an array of %d { status, body } elements"
                                    route
                                    calls.Length
                            )

                        for call in calls do
                            call.Reject error
                    | answers ->
                        for i in 0 .. calls.Length - 1 do
                            let status, text = answers.[i]

                            calls.[i].Resolve {
                                StatusCode = status
                                ResponseBody = text
                            }
                elif response.StatusCode = 400 && response.ResponseBody.Contains RefusalCode then
                    // Nothing ran: send each call as it would have gone.
                    for call in calls do
                        sendAlone group call
                else
                    // The envelope itself failed (network, CSRF, auth, a
                    // server without the route): every call fails with it,
                    // as each would have failed alone.
                    for call in calls do
                        call.Resolve response)
            (fun error ->
                for call in calls do
                    call.Reject error)

    let private flush () =
        flushScheduled <- false
        let pending = groupOrder.ToArray()
        groups.Clear()
        groupOrder.Clear()

        for group in pending do
            let calls =
                group.Calls |> Seq.filter (fun call -> not call.Cancelled.Value) |> Array.ofSeq

            match batchRoute with
            | Some route when calls.Length > 1 ->
                for chunk in Array.chunkBySize maxElements calls do
                    if chunk.Length = 1 then
                        sendAlone group chunk.[0]
                    else
                        sendEnvelope group route chunk
            | _ ->
                for call in calls do
                    sendAlone group call

    /// The proxy's half: queue one JSON call (`body = None` for a
    /// parameterless GET) until the microtask boundary.
    let internal send
        (baseUrl: string option)
        (route: string)
        (url: string)
        (headers: (string * string) list)
        (withCredentials: bool)
        (body: string option)
        : Async<HttpResponse> =
        async {
            let! token = Async.CancellationToken

            return!
                Async.FromContinuations(fun (resolve, reject, cancel) ->
                    let settled = ref false
                    let cancelled = ref false

                    let settle (continuation: unit -> unit) =
                        if not settled.Value then
                            settled.Value <- true
                            continuation ()

                    let call = {
                        Route = route
                        Url = url
                        Body = body
                        Cancelled = cancelled
                        Resolve = fun response -> settle (fun () -> resolve response)
                        Reject = fun error -> settle (fun () -> reject error)
                    }

                    token.Register(fun _ ->
                        cancelled.Value <- true
                        settle (fun () -> cancel (System.OperationCanceledException(token))))
                    |> ignore

                    let key =
                        (match baseUrl with
                         | Some url -> url
                         | None -> "")
                        + "\n"
                        + string withCredentials
                        + "\n"
                        + (headers
                           |> List.map (fun (name, value) -> name + ": " + value)
                           |> String.concat "\n")

                    let group =
                        match groups.TryGetValue key with
                        | true, group -> group
                        | _ ->
                            let group = {
                                BaseUrl = baseUrl
                                Headers = headers
                                WithCredentials = withCredentials
                                Calls = List<Pending>()
                            }

                            groups.[key] <- group
                            groupOrder.Add group
                            group

                    group.Calls.Add call

                    if not flushScheduled then
                        flushScheduled <- true
                        atMicrotaskBoundary flush)
        }

module Proxy =
    /// Phase 783 — recover the server's decode refusal from an error
    /// response body, so `ProxyRequestException.DecodeError` is populated
    /// without any caller parsing message text.
    ///
    /// The shape is the Phase 69e categorised envelope carrying the Phase
    /// 783 detail: `{ error: { methodName, decodeError: { expected,
    /// found, path }, message }, category: "validation", … }`. Anything
    /// else — a body that is not JSON, a `validation` envelope from the
    /// 69e attribute validators (which carries `violations`, not
    /// `decodeError`), a server that predates this phase — yields `None`,
    /// which is the correct answer rather than a degraded one: absence of
    /// the detail means the failure was not a decode refusal.
    ///
    /// Deliberately total. A client that threw while classifying a server
    /// error would replace a legible failure with an illegible one.
    let tryReadDecodeError (responseBody: string) : DecodeError option =
        if System.String.IsNullOrWhiteSpace responseBody then
            None
        else
            try
                DecodeErrorWire.valueIndexOf responseBody "decodeError"
                |> Option.bind (DecodeErrorWire.objectAt responseBody)
                |> Option.bind (fun detail ->
                    let field key =
                        DecodeErrorWire.valueIndexOf detail key
                        |> Option.bind (DecodeErrorWire.readString detail)
                        |> Option.map fst

                    match field "expected", field "found" with
                    | Some expected, Some found ->
                        let path =
                            DecodeErrorWire.valueIndexOf detail "path"
                            |> Option.map (DecodeErrorWire.readStringArray detail)
                            |> Option.defaultValue []

                        Some {
                            Path = path
                            Expected = expected
                            Found = found
                        }
                    | _ -> None)
            with _ ->
                None

    let combineRouteWithBaseUrl route (baseUrl: string option) =
        match baseUrl with
        | None -> route
        | Some url -> sprintf "%s%s" (url.TrimEnd('/')) route

    let isByteArray =
        function
        | TypeInfo.Array getElemType ->
            match getElemType () with
            | TypeInfo.Byte -> true
            | otherwise -> false
        | otherwise -> false

    let isAsyncOfByteArray =
        function
        | TypeInfo.Async getAsyncType ->
            match getAsyncType () with
            | TypeInfo.Array getElemType ->
                match getElemType () with
                | TypeInfo.Byte -> true
                | otherwise -> false
            | otherwise -> false
        | otherwise -> false

    let rec getReturnType typ =
        if Reflection.FSharpType.IsFunction typ then
            let _, res = Reflection.FSharpType.GetFunctionElements typ
            getReturnType res
        elif typ.IsGenericType then
            typ.GetGenericArguments() |> Array.head
        else
            typ

    /// The request headers every proxy call sends. Multipart requests leave
    /// `Content-Type` to XHR (it sets the boundary); the guard-owned keys
    /// (`Authorization` etc.) are attached at send time by the SDK's request
    /// guard — see `Remoting.withCustomHeader`.
    let private requestHeaders (options: RemoteBuilderOptions) (isMultipart: bool) = [
        if not isMultipart then
            yield "Content-Type", "application/json; charset=utf-8"

        yield "x-remoting-proxy", "true"
        yield! options.CustomHeaders
        match options.Authorization with
        | Some authToken -> yield "Authorization", authToken
        | None -> ()
    ]

    /// Phase 854 — the wire identity of a call, less its body: two calls
    /// of one method whose bodies are also equal send identical bytes.
    let private readKeyPrefixOf (funcNeedParameters: bool) (url: string) (headers: (string * string) list) =
        (if funcNeedParameters then "POST " else "GET ")
        + url
        + "\n"
        + (headers
           |> List.map (fun (name, value) -> name + ": " + value)
           |> String.concat "\n")
        + "\n\n"

    /// Phase 853 — the JSON request both proxies send, the reflective one
    /// and the generated one: a body-carrying call is a POST, a
    /// parameterless one a GET, and with batching enabled (Phase 855) the
    /// call joins this tick's envelope instead; its element comes back as
    /// the response the caller reads, unchanged.
    let private sendJson
        (options: RemoteBuilderOptions)
        (route: string)
        (url: string)
        (headers: (string * string) list)
        (funcNeedParameters: bool)
        (isMultipart: bool)
        (requestBody: RequestBody)
        : Async<HttpResponse> =
        if RemoteBatching.isEnabled () && not isMultipart then
            let body =
                match requestBody with
                | RequestBody.Json text when funcNeedParameters -> Some text
                | _ -> None

            RemoteBatching.send options.BaseUrl route url headers options.WithCredentials body
        elif funcNeedParameters then
            Http.post url
            |> Http.withBody requestBody
            |> Http.withHeaders headers
            |> Http.withCredentials options.WithCredentials
            |> Http.send
        else
            Http.get url
            |> Http.withHeaders headers
            |> Http.withCredentials options.WithCredentials
            |> Http.send

    /// Phase 853 — a JSON response that is not a 200, raised as the
    /// `ProxyRequestException` a caller has always received for it.
    let private raiseStatus (url: string) (response: HttpResponse) : 'T =
        match response.StatusCode with
        | 500 ->
            raise (
                ProxyRequestException(
                    response,
                    sprintf "Internal server error (500) while making request to %s" url,
                    response.ResponseBody,
                    // Phase 783 — a 500 should no longer be able to carry
                    // one (a decode refusal is a 400 now), but the
                    // recovery is cheap and total, and a consumer pinned
                    // to an older server still reads whatever arrives.
                    tryReadDecodeError response.ResponseBody
                )
            )
        | n ->
            raise (
                ProxyRequestException(
                    response,
                    sprintf "Http error (%d) from server occured while making request to %s" n url,
                    response.ResponseBody,
                    // Phase 783 — the decode-refusal path: the server
                    // answers 400 + a `validation` envelope carrying the
                    // structured refusal.
                    tryReadDecodeError response.ResponseBody
                )
            )

    /// Phase 843 — a 200 body through a JSON algebra decoder: read ONCE
    /// into the lexical value model (`JsonText`, numbers kept as written)
    /// and decoded by total combinators; a malformed body is a named
    /// refusal on the same `ProxyRequestException` the binary path raises,
    /// never an exception from inside the parse.
    let private decodeJsonResponse
        (url: string)
        (response: HttpResponse)
        (decoder: ToolUp.Remoting.Json.JsonValue -> Result<'T, DecodeError>)
        : 'T =
        match
            ToolUp.Remoting.Json.JsonText.tryParse response.ResponseBody
            |> Result.bind decoder
        with
        | Ok value -> value
        | Error error ->
            raise (
                ProxyRequestException(
                    response,
                    sprintf "The server's response to %s did not decode: %s" url (DecodeError.render error),
                    response.ResponseBody,
                    Some error
                )
            )

    /// Phase 854 — `proxyFetch` with the method's read policy applied:
    /// `apiFullName` is the API record's `FullName`, the key
    /// `ReadPolicies` declarations are registered under (`None` never
    /// consults them). The policy is looked up per CALL, so a policy
    /// registered after the proxy was built still applies, and with
    /// nothing registered the lookup is one count check.
    let internal proxyFetchWithPolicies (apiFullName: string option) options typeName (func: RecordField) fieldType =
        let funcArgs: (TypeInfo[]) =
            match func.FieldType with
            | TypeInfo.Async inner -> [| func.FieldType |]
            | TypeInfo.Promise inner -> [| func.FieldType |]
            | TypeInfo.Func getArgs -> getArgs ()
            | _ -> failwithf "Field %s does not have a valid definiton" func.FieldName

        let argumentCount = (Array.length funcArgs) - 1
        let returnTypeAsync = Array.last funcArgs

        let isMultipart =
            match func.FieldType with
            | TypeInfo.Func getArgs -> options.IsMultipartEnabled && getArgs () |> Array.exists isByteArray
            | otherwise -> false

        let route = options.RouteBuilder typeName func.FieldName
        let url = combineRouteWithBaseUrl route options.BaseUrl

        let funcNeedParameters =
            match funcArgs with
            | [| TypeInfo.Async _ |] -> false
            | [| TypeInfo.Promise _ |] -> false
            | [| TypeInfo.Unit; TypeInfo.Async _ |] -> false
            | otherwise -> true

        let inputArgumentTypes = Array.take argumentCount funcArgs

        let headers = requestHeaders options isMultipart

        // Phase 854 — the wire identity of a call, less its body.
        let readKeyPrefix = readKeyPrefixOf funcNeedParameters url headers

        let executeRequest =
            if options.CustomResponseSerialization.IsSome || isAsyncOfByteArray returnTypeAsync then
                let onOk =
                    match options.CustomResponseSerialization with
                    | Some serializer ->
                        let returnType = getReturnType fieldType
                        fun response -> serializer response returnType
                    | _ -> box

                fun requestBody -> async {
                    // read as arraybuffer and deserialize
                    let! (response, statusCode) =
                        if funcNeedParameters then
                            Http.post url
                            |> Http.withBody requestBody
                            |> Http.withHeaders headers
                            |> Http.withCredentials options.WithCredentials
                            |> Http.sendAndReadBinary
                        else
                            Http.get url
                            |> Http.withHeaders headers
                            |> Http.withCredentials options.WithCredentials
                            |> Http.sendAndReadBinary

                    match statusCode with
                    | 200 ->
                        // Phase 783 — this is the binary-response path, so
                        // `onOk` runs the MsgPack reader. A malformed
                        // reply now refuses with a `DecodeError` instead
                        // of a `failwithf`, and it is reported through the
                        // SAME record the server's refusals arrive in: the
                        // caller asks "did this fail to decode?" once,
                        // regardless of which side failed to decode.
                        try
                            return onOk response
                        with DecodeException error ->
                            let failed = { StatusCode = 200; ResponseBody = "" }

                            return!
                                raise (
                                    ProxyRequestException(
                                        failed,
                                        sprintf
                                            "The server's response to %s did not decode: %s"
                                            url
                                            (DecodeError.render error),
                                        "",
                                        Some error
                                    )
                                )
                    | n ->
                        let responseAsBlob =
                            InternalUtilities.createBlobWithMimeType !^response "text/plain"

                        let! responseText = InternalUtilities.readBlobAsText responseAsBlob

                        let response = {
                            StatusCode = statusCode
                            ResponseBody = responseText
                        }

                        let errorMsg =
                            if n = 500 then
                                sprintf "Internal server error (500) while making request to %s" url
                            else
                                sprintf "Http error (%d) while making request to %s" n url

                        return!
                            raise (
                                ProxyRequestException(
                                    response,
                                    errorMsg,
                                    response.ResponseBody,
                                    tryReadDecodeError response.ResponseBody
                                )
                            )
                }
            else
                let returnType =
                    match returnTypeAsync with
                    | TypeInfo.Async getAsyncTypeArgument -> getAsyncTypeArgument ()
                    | TypeInfo.Promise getPromiseTypeArgument -> getPromiseTypeArgument ()
                    | TypeInfo.Any getReturnType ->
                        let t = getReturnType ()

                        if t.FullName.StartsWith "System.Threading.Tasks.Task`1" then
                            t.GetGenericArguments().[0] |> createTypeInfo
                        else
                            failwithf "Expected field %s to have a return type of Async<'t> or Task<'t>" func.FieldName
                    | _ -> failwithf "Expected field %s to have a return type of Async<'t> or Task<'t>" func.FieldName

                // Phase 843 — the key the JSON algebra's registry is read
                // by: the same `System.Type` the binary branch above hands
                // its serializer, so one registration identifies one type
                // identically on both wires.
                let returnClrType = getReturnType fieldType

                fun requestBody -> async {
                    // make plain RPC request and let it go through the deserialization pipeline
                    let! response = sendJson options route url headers funcNeedParameters isMultipart requestBody

                    match response.StatusCode with
                    | 200 ->
                        // Phase 843 — the opt-in branch, the JSON twin of
                        // `withBinarySerialization`'s. A return type with a
                        // registered JSON algebra decoder is decoded through
                        // it (`decodeJsonResponse`). A MISS is the reflection
                        // path below, unchanged, so a consumer that registers
                        // nothing sees nothing different (GP 11). The lookup
                        // is per call, as the binary branch's is:
                        // registration happens at composition and need not
                        // precede the proxy's construction.
                        match ToolUp.Remoting.Json.JsonDecoders.tryGet None returnClrType with
                        | Some decoder -> return decodeJsonResponse url response decoder
                        | None ->
                            let parsedJson = SimpleJson.parseNative response.ResponseBody
                            return Convert.fromJsonAs parsedJson returnType
                    | _ -> return raiseStatus url response
                }

        fun arg0 arg1 arg2 arg3 arg4 arg5 arg6 arg7 ->
            let inputArguments =
                if funcNeedParameters then
                    Array.take argumentCount [|
                        box arg0
                        box arg1
                        box arg2
                        box arg3
                        box arg4
                        box arg5
                        box arg6
                        box arg7
                    |]
                else
                    [||]

            let requestBody =
                if isMultipart then
                    inputArguments
                    |> Array.mapi (fun i x ->
                        let typ = inputArgumentTypes.[i]

                        // in theory the input byte array could be untyped, so it's better to check the expected type
                        // than `instanceof Uint8Array` on the actual value
                        if isByteArray typ then
                            InternalUtilities.createBlobWithMimeType (x :?> _) "application/octet-stream"
                        else
                            let json = Convert.serialize x typ
                            InternalUtilities.createBlobWithMimeType !^json "application/json")
                    |> RequestBody.Multipart
                else
                    match inputArgumentTypes.Length with
                    | 1 when not (Convert.arrayLike inputArgumentTypes.[0]) ->
                        let typeInfo = TypeInfo.Tuple(fun _ -> inputArgumentTypes)

                        let requestBodyJson =
                            inputArguments
                            |> Array.tryHead
                            |> Option.map (fun arg -> Convert.serialize arg typeInfo)
                            |> Option.defaultValue "{}"

                        RequestBody.Json requestBodyJson
                    | 1 ->
                        // for array-like types, use an explicit array surranding the input array argument
                        let requestBodyJson =
                            Convert.serialize [| inputArguments.[0] |] (TypeInfo.Array(fun _ -> inputArgumentTypes.[0]))

                        RequestBody.Json requestBodyJson
                    | n ->
                        let typeInfo = TypeInfo.Tuple(fun _ -> inputArgumentTypes)
                        let requestBodyJson = Convert.serialize inputArguments typeInfo
                        RequestBody.Json requestBodyJson

            match apiFullName |> Option.bind (fun api -> ReadPolicies.tryFind api func.FieldName) with
            | None -> executeRequest requestBody
            | Some policy ->
                let key =
                    match requestBody with
                    | RequestBody.Json body -> Some(readKeyPrefix + body)
                    | RequestBody.Empty -> Some readKeyPrefix
                    | RequestBody.Multipart _ -> None

                ReadPolicies.invoke apiFullName.Value func.FieldName policy key (fun () -> executeRequest requestBody)

    /// Build the request/response proxy function for one API method. Reads
    /// no Phase 854 read policy; `Remoting.buildProxy` uses
    /// `proxyFetchWithPolicies`.
    let proxyFetch options typeName (func: RecordField) fieldType =
        proxyFetchWithPolicies None options typeName func fieldType

    /// Phase 853 — one generated proxy's call context: the record's
    /// registry key (the key its read policies are registered under), its
    /// route type name, and the builder options. Built by a generated
    /// proxy builder (`ToolUp.Remoting.Generator`'s `client-proxies`) once
    /// per proxy.
    let generatedApi (apiKey: string) (typeName: string) (options: RemoteBuilderOptions) = apiKey, typeName, options

    /// Phase 853 — one generated proxy method: a closure over the
    /// transport and the method's generated response decoder, taking the
    /// request body the generated ENCODERS wrote (`JsonEncode.arguments`)
    /// and returning the call. Route, URL, headers and the read-policy key
    /// prefix are computed here, once per proxy; a call does no reflection
    /// — no `createTypeInfo`, no `Convert.serialize`, no `Convert.fromJsonAs`
    /// — and composes with the Phase 854 read table and the Phase 855
    /// transport and batching exactly as the reflective proxy's call does:
    /// both go through `sendJson`, `raiseStatus` and `ReadPolicies.invoke`.
    ///
    /// `sendsBody = false` is a GET (a method that is an `Async<_>` value or
    /// takes `unit`); its body text is then only the read-policy key's
    /// tail, the one the reflective proxy uses (`[]` / `{}`).
    let generatedMethod<'R>
        (api: string * string * RemoteBuilderOptions)
        (methodName: string)
        (sendsBody: bool)
        (decoder: ToolUp.Remoting.Json.JsonValue -> Result<'R, DecodeError>)
        : string -> Async<'R> =
        let apiKey, typeName, options = api
        let route = options.RouteBuilder typeName methodName
        let url = combineRouteWithBaseUrl route options.BaseUrl
        let headers = requestHeaders options false
        let readKeyPrefix = readKeyPrefixOf sendsBody url headers

        let execute (requestBody: RequestBody) : Async<obj> = async {
            let! response = sendJson options route url headers sendsBody false requestBody

            match response.StatusCode with
            | 200 -> return box (decodeJsonResponse url response decoder)
            | _ -> return raiseStatus url response
        }

        fun (body: string) ->
            let requestBody = RequestBody.Json body

            let call =
                match ReadPolicies.tryFind apiKey methodName with
                | None -> execute requestBody
                | Some policy ->
                    ReadPolicies.invoke apiKey methodName policy (Some(readKeyPrefix + body)) (fun () ->
                        execute requestBody)

#if FABLE_COMPILER
            // The SAME Async object — `ReadPolicies.tryObserve` recognises a
            // declared read's call by reference, as it does the reflective
            // proxy's (whose boxed `Async<obj>` Fable hands back uncast).
            unbox<Async<'R>> call
#else
            async {
                let! value = call
                return unbox<'R> value
            }
#endif

    /// Phase 69c.D — `Some elementType` when `fieldType` is a streaming
    /// field (`'arg -> IAsyncEnumerable<'T>`), recognised at proxy-build time
    /// exactly as the server classifies it at startup. `IAsyncEnumerable<'T>`
    /// has no Fable runtime shape, so `createTypeInfo` renders it as
    /// TypeInfo.Any (a Fable.SimpleJson case the .NET surface does not own, so
    /// it is cited bare); the element `'T` is read off the generic argument for
    /// the per-chunk deserialiser. `None` for every request/response shape.
    let tryStreamingElementType (fieldType: TypeInfo) : TypeInfo option =
        match fieldType with
        | TypeInfo.Func getArgs ->
            match Array.last (getArgs ()) with
            | TypeInfo.Any getReturnType ->
                let t = getReturnType ()

                if t.FullName.StartsWith "System.Collections.Generic.IAsyncEnumerable`1" then
                    Some(t.GetGenericArguments().[0] |> createTypeInfo)
                else
                    None
            | _ -> None
        | _ -> None

    /// Phase 69c.D — the proxy function for a streaming field. Takes the
    /// method's single argument and returns a COLD `RemoteStream<'T>`: the
    /// same route, headers and JSON-array body `proxyFetch` would send, sent
    /// only when the consumer subscribes (`RemoteStream.subscribe`). The
    /// value is boxed through the field's `IAsyncEnumerable<'T>` type — the
    /// erasure boundary the `Streaming.fs` header documents. Unary only,
    /// matching the server's v0 streaming dispatch.
    let proxyStream options typeName (func: RecordField) (elementType: TypeInfo) : obj -> obj =
        let argumentTypes =
            match func.FieldType with
            | TypeInfo.Func getArgs ->
                let args = getArgs ()
                Array.take (Array.length args - 1) args
            | _ -> [||]

        if argumentTypes.Length <> 1 then
            failwithf
                "Streaming method %s must take exactly one argument (it takes %d) — the server's streaming dispatch is unary"
                func.FieldName
                argumentTypes.Length

        let argumentType = argumentTypes.[0]
        let route = options.RouteBuilder typeName func.FieldName
        let url = combineRouteWithBaseUrl route options.BaseUrl
        let headers = requestHeaders options false

        fun arg ->
            // The same body shapes `proxyFetch` produces for one argument.
            let body =
                if Convert.arrayLike argumentType then
                    Convert.serialize [| arg |] (TypeInfo.Array(fun _ -> argumentType))
                else
                    Convert.serialize arg (TypeInfo.Tuple(fun _ -> [| argumentType |]))

            box (RemoteStream.create url headers body options.WithCredentials tryReadDecodeError elementType)

/// Phase 853 — the registry of GENERATED client proxy builders, by API
/// record key (its `FullName`, a nested type's `+` written `.`).
/// `Api.makeProxy` consults it before building a reflective proxy; a
/// generated module's `registerAll` fills it. The platform's own records
/// are not registered here — `Api.makeProxy` reads their generated module
/// directly, so no composition step is needed for them.
[<RequireQualifiedAccess>]
module GeneratedProxies =

    let private builders =
        System.Collections.Generic.Dictionary<string, RemoteBuilderOptions -> obj>()

    /// Register `build` as the generated proxy builder for `apiKey`.
    /// Idempotent: a later registration of the same key replaces it.
    let register (apiKey: string) (build: RemoteBuilderOptions -> obj) : unit = builders.[apiKey] <- build

    /// How many builders are registered.
    let count () : int = builders.Count

    /// Whether a generated proxy reproduces what the reflective proxy
    /// would do with `options`. It does not for binary responses
    /// (`withBinarySerialization` — the generated proxy reads JSON) or
    /// multipart uploads (`withMultipartOptimization`), which keep the
    /// reflective proxy.
    let admits (options: RemoteBuilderOptions) : bool =
        options.CustomResponseSerialization.IsNone && not options.IsMultipartEnabled

    /// The registered builder for `apiKey`, applied to `options`, when one
    /// is registered and admits them.
    let tryBuild (apiKey: string) (options: RemoteBuilderOptions) : obj option =
        if not (admits options) then
            None
        else
            match builders.TryGetValue apiKey with
            | true, build -> Some(build options)
            | _ -> None

    /// The key an API record type is registered under.
    let keyOf (apiType: System.Type) : string = apiType.FullName.Replace('+', '.')