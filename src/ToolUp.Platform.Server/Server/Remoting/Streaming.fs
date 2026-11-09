namespace ToolUp.Remoting.Server

open System
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open ToolUp.Remoting

// =============================================================================
// Server-sent streaming via IAsyncEnumerable<'T>
// =============================================================================
//
// An API record field whose function shape returns `IAsyncEnumerable<'T>`
// is a streaming method. It is a call like any other: it runs through every
// stage of the request pipeline (authorisation, validation, rate limit,
// audit), its argument decodes through the proxy's own argument seam, and
// the proxy answers it with a `StreamSource` instead of a written result.
// The response writer then iterates the source under the request's abort
// token and frames each element as an `event: chunk` carrying
// `id: <correlation-id>-<chunk-index>`, then `event: complete`, or
// `event: error` carrying `{"message":"…"}` when the producer faults. The
// bytes are Core's `Sse.encode`, the grammar the Fable client parses with.

/// Public helpers for *producing* the `IAsyncEnumerable<'T>`
/// a streaming method returns. The dispatcher (internal `Streaming` module
/// below) handles classification + SSE framing; this module gives consumers
/// a ready-made bridge for the common case where the value source is a
/// callback / sink (an agent loop, a progress reporter, any push-based
/// producer) rather than a natural pull-based sequence.
[<RequireQualifiedAccess>]
module AsyncStream =

    /// How many values a bridge holds for a consumer that has not read them.
    [<Literal>]
    let Capacity = 64

    /// `emit` for a callback producer over a bounded channel: a value the
    /// channel has room for is written at once; on a full channel the
    /// producer is HELD until the consumer reads (back-pressure: a
    /// synchronous callback has no other way to wait, and it is the
    /// producer's thread that waits, never the request's). A value emitted
    /// after the consumer left (the channel completed, or `ct` signalled) is
    /// dropped: `emit` never throws into the producer, whose detached tail
    /// would otherwise fault on the thread pool.
    let private holdingEmit (channel: Channel<'T>) (ct: CancellationToken) (value: 'T) : unit =
        if not (channel.Writer.TryWrite value) then
            try
                channel.Writer.WriteAsync(value, ct).AsTask().Wait()
            with _ ->
                ()

    /// The consumer's side of a bridge: ends after the first value satisfying
    /// `isTerminal` (that value IS yielded), and on disposal runs `release`
    /// and closes the channel.
    let private terminating
        (isTerminal: 'T -> bool)
        (channel: Channel<'T>)
        (release: unit -> unit)
        (ct: CancellationToken)
        : IAsyncEnumerator<'T> =
        let inner = channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator ct

        { new IAsyncEnumerator<'T> with
            member _.Current = inner.Current

            member _.MoveNextAsync() =
                ValueTask<bool>(
                    task {
                        let! moved = inner.MoveNextAsync()

                        if moved && isTerminal inner.Current then
                            // Terminal value yielded: close the writer so the
                            // reader drains and the next move ends.
                            channel.Writer.TryComplete() |> ignore

                        return moved
                    }
                )

            member _.DisposeAsync() =
                release ()
                channel.Writer.TryComplete() |> ignore
                inner.DisposeAsync()
        }

    /// Bridge a push-based producer to an `IAsyncEnumerable<'T>`.
    ///
    /// `runProducer emit` runs the producer, calling `emit` for each value.
    /// Each emitted value is yielded to the consumer. The stream ends after
    /// the first value for which `isTerminal` returns `true` (that terminal
    /// value IS yielded first), or when `runProducer` faults (the exception
    /// completes the stream, which the dispatcher frames as `event: error`).
    ///
    /// `runProducer` is started detached at enumeration time, so a producer
    /// that *outlives* its synchronous entry point — e.g. an agent turn whose
    /// foreground returns once the background loop is kicked off, and whose
    /// `emit` keeps firing afterwards — works correctly: the stream stays
    /// open until a terminal value is emitted (not until `runProducer`
    /// returns).
    ///
    /// **Contract:** `runProducer` MUST emit a value satisfying `isTerminal`
    /// (or fault) to end the stream. A producer that returns without ever
    /// emitting a terminal value and never faults leaves the stream open —
    /// by design, since the detached-tail case can't distinguish "foreground
    /// done" from "production done". Producers that are fully synchronous
    /// simply emit their terminal value last.
    ///
    /// The producer is started under the consumer's token, which the
    /// dispatcher passes as the request's `RequestAborted`: a client that
    /// leaves cancels it. At most `Capacity` values wait for the consumer; a
    /// producer that outruns it is held in `emit` until it reads.
    let fromCallback (isTerminal: 'T -> bool) (runProducer: ('T -> unit) -> Async<unit>) : IAsyncEnumerable<'T> =
        { new IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(ct: CancellationToken) =
                let channel =
                    Channel.CreateBounded<'T>(BoundedChannelOptions(Capacity, FullMode = BoundedChannelFullMode.Wait))

                Async.Start(
                    async {
                        try
                            do! runProducer (holdingEmit channel ct)
                        with ex ->
                            // Foreground fault before any terminal was emitted —
                            // surface it as the stream's terminal so the consumer
                            // never hangs (the dispatcher frames `event: error`).
                            channel.Writer.TryComplete ex |> ignore
                    },
                    ct
                )

                terminating isTerminal channel ignore ct
        }

    /// Bridge a SUBSCRIPTION-based producer to an
    /// `IAsyncEnumerable<'T>`.
    ///
    /// `fromCallback` above is for a producer that RUNS - an agent turn, a
    /// replay - and its stream ends when that run emits its terminal value.
    /// A subscription has no run: it is a feed that already exists, the
    /// values arrive from somewhere else entirely, and the thing that has
    /// to happen when the consumer goes away is that the subscription is
    /// DISPOSED. `fromCallback` cannot do that - it holds no handle - so a
    /// feed bridged through it leaks its subscriber on every aborted
    /// connection, which is precisely the case a streaming endpoint meets
    /// most: a browser tab closing mid-stream.
    ///
    /// `subscribe emit` is called once, at enumeration time, and returns
    /// the handle for that subscription. Values passed to `emit` are
    /// yielded in order; a value emitted synchronously from inside
    /// `subscribe` (seeding the consumer with the feed's current state) is
    /// yielded first. The stream ends after the first value satisfying
    /// `isTerminal` - that value IS yielded - and the subscription is
    /// disposed then, on consumer disposal, and on a `subscribe` that
    /// throws.
    ///
    /// **Contract:** the feed MUST eventually emit a value satisfying
    /// `isTerminal`, or the consumer MUST dispose. A feed that does
    /// neither holds the subscription open, exactly as `fromCallback`'s
    /// contract says of a producer that never emits its terminal.
    ///
    /// A feed is never held by a slow consumer, since it serves every
    /// subscriber from one publisher: at most `Capacity` values wait, and the
    /// OLDEST is dropped to make room, so the newest value, the terminal one
    /// included, always reaches the consumer. A feed's values are states, not
    /// increments, so a dropped intermediate state loses nothing.
    let fromSubscription (isTerminal: 'T -> bool) (subscribe: ('T -> unit) -> IDisposable) : IAsyncEnumerable<'T> =
        { new IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(ct: CancellationToken) =
                let channel =
                    Channel.CreateBounded<'T>(
                        BoundedChannelOptions(Capacity, FullMode = BoundedChannelFullMode.DropOldest)
                    )

                let subscription =
                    try
                        subscribe (fun v -> channel.Writer.TryWrite v |> ignore)
                    with ex ->
                        // A feed that refuses to subscribe is the stream's
                        // error, not an exception thrown past the consumer
                        // out of `GetAsyncEnumerator`.
                        channel.Writer.TryComplete ex |> ignore

                        { new IDisposable with
                            member _.Dispose() = ()
                        }

                // The whole reason this bridge exists: the feed is told,
                // exactly once, that nobody is listening.
                terminating isTerminal channel subscription.Dispose ct
        }

/// What a streaming call answers with: the producer, opened under the
/// request's abort token, and how one element is encoded. Built for a typed
/// stream by `Streaming.sourceOf<'T>`, whose element type is closed once per
/// method when the proxy is built, so iterating it makes no reflective call.
[<NoComparison; NoEquality>]
type internal StreamSource = {
    /// The element type `'T`.
    Element: Type
    /// Open the producer; the token is the request's `RequestAborted`.
    Produce: CancellationToken -> IAsyncEnumerator<obj>
    /// One element's JSON: the composed serializer at the element type.
    Encode: obj -> string
}

module internal Streaming =

    /// True if `t` is a closed generic `IAsyncEnumerable<'T>`.
    let isAsyncEnumerable (t: Type) : bool =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<IAsyncEnumerable<_>>

    /// Pair of `(argType, elementType)` for a streaming method shape
    /// `'arg -> IAsyncEnumerable<'element>`. Returns `None` for non-
    /// streaming shapes.
    let shapeOf (apiField: PropertyInfo) : (Type * Type) option =
        let fieldType = apiField.PropertyType

        if FSharpType.IsFunction fieldType then
            let argT, returnT = FSharpType.GetFunctionElements fieldType

            if isAsyncEnumerable returnT then
                let elementT = returnT.GetGenericArguments()[0]
                Some(argT, elementT)
            else
                None
        else
            None

    /// The source of a typed stream, encoding each element with `options` at
    /// its declared type, so a `null` element (a `None`) encodes as such
    /// rather than faulting on its runtime type.
    let sourceOf<'T> (options: JsonSerializerOptions) (stream: IAsyncEnumerable<'T>) : StreamSource = {
        Element = typeof<'T>
        Produce =
            fun ct ->
                let inner = stream.GetAsyncEnumerator ct

                { new IAsyncEnumerator<obj> with
                    member _.Current = box inner.Current
                    member _.MoveNextAsync() = inner.MoveNextAsync()
                    member _.DisposeAsync() = inner.DisposeAsync()
                }
        Encode = fun element -> JsonSerializer.Serialize<'T>(unbox<'T> element, options)
    }

    type private SourceOf =
        static member For<'T>() : JsonSerializerOptions -> obj -> StreamSource =
            fun options stream -> sourceOf<'T> options (stream :?> IAsyncEnumerable<'T>)

    /// `sourceOf` for an element type known only as a `Type`, closed over it
    /// ONCE: the function returned makes no reflective call.
    let sourceFor (element: Type) : JsonSerializerOptions -> obj -> StreamSource =
        typeof<SourceOf>
            .GetMethod("For", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
            .MakeGenericMethod([| element |])
            .Invoke(null, [||])
        :?> JsonSerializerOptions -> obj -> StreamSource

    /// Frame one element as an `event: chunk` carrying the dispatcher's
    /// `id: <correlation-id>-<chunk-index>` (zero-based), so every chunk is
    /// attributable to its call from the wire alone: the browser's
    /// `EventSource.lastEventId` and any proxy log carry the token the
    /// telemetry event and the `x-correlation-id` response header carry.
    let formatChunkWithId (id: string) (json: string) : byte[] =
        Encoding.UTF8.GetBytes(Sse.encode (StreamFrame.Chunk(Some id, json)))

    /// Frame the terminal `complete` event.
    let formatComplete () : byte[] =
        Encoding.UTF8.GetBytes(Sse.encode StreamFrame.Complete)

    /// Frame an exception as an `event: error` carrying `{"message":"…"}`.
    /// The message only: stack traces stay server-side.
    let formatError (message: string) : byte[] =
        let payload =
            sprintf "{\"message\":\"%s\"}" (JsonEncodedText.Encode(message).ToString())

        Encoding.UTF8.GetBytes(Sse.encode (StreamFrame.Error payload))