namespace ToolUp.Remoting.Server

open System
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open ToolUp.Remoting

// =============================================================================
// Server-sent streaming via IAsyncEnumerable<'T>
// =============================================================================
//
// API record fields whose F# function shape returns `IAsyncEnumerable<'T>`
// are classified as streaming methods at startup. At request time the
// dispatcher bypasses the proxy entirely:
//   * Body is parsed; the first argument is deserialised via STJ
//   * The method's function value is invoked via reflection on the impl
//   * The returned IAsyncEnumerable is iterated; each element is framed as
//     an `event: chunk` carrying `id: <correlation-id>-<chunk-index>`
//   * On normal end of sequence: `event: complete`
//   * On exception: `event: error` carrying `{"message":"…"}`
// The bytes are Core's `Sse.encode`, the grammar the Fable client parses with.
//
// v0 supports unary methods only (single arg → IAsyncEnumerable). Multi-arg
// curried methods are a follow-up (need recursive Invoke unfurling).
// Method shape is NON-async outer (`'arg -> IAsyncEnumerable<'T>`) to keep
// the reflection path simple; an Async<IAsyncEnumerable> shape is a
// follow-up that adds the `Async.StartAsTask` reflection bridge.

/// Public helpers for *producing* the `IAsyncEnumerable<'T>`
/// a streaming method returns. The dispatcher (internal `Streaming` module
/// below) handles classification + SSE framing; this module gives consumers
/// a ready-made bridge for the common case where the value source is a
/// callback / sink (an agent loop, a progress reporter, any push-based
/// producer) rather than a natural pull-based sequence.
[<RequireQualifiedAccess>]
module AsyncStream =

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
    let fromCallback (isTerminal: 'T -> bool) (runProducer: ('T -> unit) -> Async<unit>) : IAsyncEnumerable<'T> =
        { new IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(ct: System.Threading.CancellationToken) =
                let channel = Channel.CreateUnbounded<'T>()

                Async.Start(
                    async {
                        try
                            do! runProducer (fun v -> channel.Writer.TryWrite v |> ignore)
                        with ex ->
                            // Foreground fault before any terminal was emitted —
                            // surface it as the stream's terminal so the consumer
                            // never hangs (the dispatcher frames `event: error`).
                            channel.Writer.TryComplete ex |> ignore
                    },
                    ct
                )

                let inner = channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator ct

                { new IAsyncEnumerator<'T> with
                    member _.Current = inner.Current

                    member _.MoveNextAsync() =
                        ValueTask<bool>(
                            task {
                                let! moved = inner.MoveNextAsync()

                                if moved && isTerminal inner.Current then
                                    // Terminal value yielded — close the writer so
                                    // the reader drains and the next move ends.
                                    channel.Writer.TryComplete() |> ignore

                                return moved
                            }
                        )

                    member _.DisposeAsync() =
                        channel.Writer.TryComplete() |> ignore
                        inner.DisposeAsync()
                }
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
    let fromSubscription (isTerminal: 'T -> bool) (subscribe: ('T -> unit) -> IDisposable) : IAsyncEnumerable<'T> =
        { new IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(ct: System.Threading.CancellationToken) =
                let channel = Channel.CreateUnbounded<'T>()

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

                let inner = channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator ct

                { new IAsyncEnumerator<'T> with
                    member _.Current = inner.Current

                    member _.MoveNextAsync() =
                        ValueTask<bool>(
                            task {
                                let! moved = inner.MoveNextAsync()

                                if moved && isTerminal inner.Current then
                                    channel.Writer.TryComplete() |> ignore

                                return moved
                            }
                        )

                    member _.DisposeAsync() =
                        // The whole reason this bridge exists: the feed is
                        // told, exactly once, that nobody is listening.
                        subscription.Dispose()
                        channel.Writer.TryComplete() |> ignore
                        inner.DisposeAsync()
                }
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

    /// Cache per-method classification at startup. Returns a map from
    /// method name → (arg type, element type) ONLY for streaming
    /// methods; non-streaming methods are absent so per-call lookup is
    /// a fast Map.tryFind miss.
    let classify (apiType: Type) : Map<string, Type * Type> =
        ApiRecord.fields apiType
        |> Array.choose (fun apiField -> shapeOf apiField |> Option.map (fun shape -> apiField.Name, shape))
        |> Map.ofArray

    // ── Align this diagnostic with the dispatch classifiers.
    //
    // This diagnostic once matched every marker by bare simple type name,
    // which was consistent with the classifiers of the day. The auth
    // classifier then moved to CLR type identity and left this one
    // behind, so two matchers disagreed about what counts as
    // `[<RequiresRole>]` — exactly the drift that move was written to end.
    //
    // The diagnostic is fail-SAFE (a missed match produces a diagnostic,
    // not an authorisation), so this is a correctness-of-message item
    // rather than a hole — with ONE exception that is a real defect: the
    // pre-727 matcher recognised `RateLimitAttribute` only via a
    // server-tier type test, and its name arm had no `RateLimitAttribute`
    // case, so a tier-shared `ToolUp.Platform.RateLimitAttribute` on a
    // streaming method was recognised by NEITHER path. The adapter
    // started, and the declared budget was silently dropped by the SSE
    // short-circuit — which is the precise outcome this refusal exists to
    // prevent, on the family (Fable-compiled Core API records) that
    // carries the mirror in the first place. Identity matching over both
    // families fixes that by construction.
    //
    // A foreign attribute of a colliding name is still REPORTED, rendered
    // as a collision rather than as a declared guard: the refusal is
    // preserved (never weakened), and the message stops asserting that
    // the consumer declared a `[<RequiresRole>]` this SDK would honour.
    // That is what makes the two matchers agree on the forged case — both
    // now say "this is not one of our markers", where before one silently
    // ignored it and the other named it as a guard.
    let private preflightMarkers =
        MarkerFamily [
            // Server-tier family (this assembly).
            typeof<RequiresRoleAttribute>
            typeof<RequiresClaimAttribute>
            typeof<TenantScopedAttribute>
            typeof<RateLimitAttribute>
            typeof<AuditAttribute>
            typeof<IdempotentAttribute>
            // Tier-shared mirror family (`ToolUp.Platform.Core`).
            typeof<ToolUp.Platform.RequiresRoleAttribute>
            typeof<ToolUp.Platform.RequiresClaimAttribute>
            typeof<ToolUp.Platform.TenantScopedAttribute>
            typeof<ToolUp.Platform.RateLimitAttribute>
            typeof<ToolUp.Platform.AuditAttribute>
            typeof<ToolUp.Platform.IdempotentAttribute>
        ]

    let private stringProp (name: string) (a: obj) : string option =
        match a.GetType().GetProperty name with
        | null -> None
        | p ->
            match p.GetValue a with
            | :? string as s -> Some s
            | _ -> None

    let private intProp (name: string) (a: obj) : int option =
        match a.GetType().GetProperty name with
        | null -> None
        | p ->
            match p.GetValue a with
            | :? int as i -> Some i
            | _ -> None

    /// Describe one attribute on a streaming method: `Some` when it is a
    /// sanctioned pre-flight marker the SSE path cannot enforce, or a
    /// foreign attribute colliding with one of their names; `None` when
    /// it is neither. The name dispatch runs INSIDE the identity gate, so
    /// it decodes a type already established as ours rather than guessing
    /// from a name (the same shape as `AuthClassifier.tryRequirement`).
    let private describeUnenforceable (a: obj) : string option =
        let t = a.GetType()

        if preflightMarkers.IsForeign t then
            Some(
                sprintf
                    "foreign '%s' — name collides with a pre-flight marker; not honoured"
                    (preflightMarkers.Render t)
            )
        elif not (preflightMarkers.IsSanctioned t) then
            None
        else
            match t.Name with
            | "RequiresRoleAttribute" ->
                Some(sprintf "RequiresRole(\"%s\")" (stringProp "Role" a |> Option.defaultValue "?"))
            | "RequiresClaimAttribute" ->
                Some(sprintf "RequiresClaim(\"%s\")" (stringProp "Claim" a |> Option.defaultValue "?"))
            | "TenantScopedAttribute" -> Some "TenantScoped"
            | "RateLimitAttribute" ->
                let count = intProp "Count" a |> Option.defaultValue 0
                let window = intProp "WindowSeconds" a |> Option.defaultValue 0
                Some(sprintf "RateLimit(%d, %ds)" count window)
            | "AuditAttribute" -> Some(sprintf "Audit(\"%s\")" (stringProp "KindName" a |> Option.defaultValue "?"))
            | "IdempotentAttribute" -> Some "Idempotent"
            | _ -> None

    /// Return the names of streaming methods that carry pre-flight
    /// attributes the streaming dispatch path doesn't honour today.
    /// `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` /
    /// `[<RateLimit>]` / `[<Audit>]` / `[<Idempotent>]` on a streaming
    /// method are silently ignored by the SSE short-circuit; the
    /// adapter refuses to start when any are present, forcing the
    /// composer to either drop the attribute or convert the method
    /// off the streaming shape until per-frame composition lands.
    /// `[<AllowAnonymous>]` and `[<PublicEndpoint>]` are explicit
    /// "no auth needed" markers — those are honoured (they're
    /// requirements only at the level of the auth classifier, not
    /// enforced per-request).
    let unenforceableOf (apiField: PropertyInfo) : string list =
        match shapeOf apiField with
        | None -> []
        | Some _ ->
            apiField.GetCustomAttributes(true)
            |> Array.choose describeUnenforceable
            |> Array.toList

    /// Every streaming method with its unenforceable attributes, in field order.
    let streamingMethodsCarryingUnenforceableAttributes (apiType: Type) : (string * string list) list =
        ApiRecord.fields apiType
        |> Array.toList
        |> List.choose (fun apiField ->
            match unenforceableOf apiField with
            | [] -> None
            | attrs -> Some(apiField.Name, attrs))

    /// Read the request body and deserialise the first array element
    /// as the streaming method's argument. Same shape as the validation
    /// engine's parser; factored to a local helper to avoid coupling
    /// the streaming dispatch to the Validation module.
    let parseFirstArg (bodyText: string) (argType: Type) (options: JsonSerializerOptions) : obj option =
        try
            use doc = JsonDocument.Parse bodyText

            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                None
            elif doc.RootElement.GetArrayLength() = 0 then
                None
            else
                let firstArg = doc.RootElement[0]
                let raw = firstArg.GetRawText()
                Some(JsonSerializer.Deserialize(raw, argType, options))
        with _ ->
            None

    /// Invoke the streaming method on `impl` with the deserialised
    /// `arg`, returning the resulting `IAsyncEnumerable` as a non-
    /// generic interface (`IEnumerator`-shaped). Uses reflection over
    /// the FSharpFunc's `Invoke` method.
    ///
    /// Null-guards on both `pi` (property missing) AND on
    /// `funcObj` (the property's value is null — e.g. the impl
    /// record was constructed with `Unchecked.defaultof<_>` or a
    /// partial-record builder that didn't populate every field).
    /// Previously the latter NREd silently outside any try/with;
    /// now both paths surface a clear `invalidOp` the adapter's
    /// streaming-branch try/with can convert to an SSE error frame.
    let invokeMethod (impl: obj) (methodName: string) (arg: obj) : obj =
        let pi = impl.GetType().GetProperty methodName

        if isNull pi then
            invalidOp (sprintf "method '%s' not found on %s" methodName (impl.GetType().Name))
        else
            let funcObj = pi.GetValue impl

            if isNull funcObj then
                invalidOp (
                    sprintf
                        "streaming method '%s' on %s is null — the impl record was constructed without populating this field"
                        methodName
                        (impl.GetType().Name)
                )

            let invokeMethod = funcObj.GetType().GetMethod "Invoke"
            invokeMethod.Invoke(funcObj, [| arg |])

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