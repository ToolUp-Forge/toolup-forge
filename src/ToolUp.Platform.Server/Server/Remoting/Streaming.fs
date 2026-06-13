namespace ToolUp.Remoting.Server

open System
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Text.Json
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69c — server-sent streaming via IAsyncEnumerable<'T>
// =============================================================================
//
// API record fields whose F# function shape returns `IAsyncEnumerable<'T>`
// are classified as streaming methods at startup. At request time the
// dispatcher bypasses the proxy entirely:
//   * Body is parsed; the first argument is deserialised via STJ
//   * The method's function value is invoked via reflection on the impl
//   * The returned IAsyncEnumerable is iterated; each element framed as
//     an SSE event `event: chunk\ndata: <json>\n\n`
//   * On normal end of sequence: `event: complete\ndata: {}\n\n`
//   * On exception: `event: error\ndata: <message>\n\n`
//
// v0 supports unary methods only (single arg → IAsyncEnumerable). Multi-arg
// curried methods are a follow-up (need recursive Invoke unfurling).
// Method shape is NON-async outer (`'arg -> IAsyncEnumerable<'T>`) to keep
// the reflection path simple; an Async<IAsyncEnumerable> shape is a
// follow-up that adds the `Async.StartAsTask` reflection bridge.

module internal Streaming =

    // Reflect over public AND non-public records so an internal / private
    // streaming API record arms the classifier (and the unenforceable-
    // attribute refusal) exactly like a public one — the same fail-open
    // hole Phase 69d.tail closed for the auth classifier. Without it a
    // non-public record's streaming method carrying `[<RequiresRole>]`
    // would silently start with the requirement UNenforced.
    let private reflectionFlags = BindingFlags.Public ||| BindingFlags.NonPublic

    /// True if `t` is a closed generic `IAsyncEnumerable<'T>`.
    let isAsyncEnumerable (t: Type) : bool =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<IAsyncEnumerable<_>>

    /// Pair of `(argType, elementType)` for a streaming method shape
    /// `'arg -> IAsyncEnumerable<'element>`. Returns `None` for non-
    /// streaming shapes.
    let private streamingShape (apiField: PropertyInfo) : (Type * Type) option =
        let fieldType = apiField.PropertyType

        if FSharpType.IsFunction fieldType then
            let argT, returnT = FSharpType.GetFunctionElements fieldType

            if isAsyncEnumerable returnT then
                let elementT = returnT.GetGenericArguments().[0]
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
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            Map.empty
        else
            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.choose (fun apiField ->
                match streamingShape apiField with
                | Some shape -> Some(apiField.Name, shape)
                | None -> None)
            |> Map.ofArray

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
    let streamingMethodsCarryingUnenforceableAttributes (apiType: Type) : (string * string list) list =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            []
        else
            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.choose (fun apiField ->
                match streamingShape apiField with
                | None -> None
                | Some _ ->
                    let attrs = apiField.GetCustomAttributes(true)

                    // Simple-name matching so the tier-shared
                    // `ToolUp.Platform.*` attribute mirrors are caught
                    // alongside this assembly's own family (Phase
                    // 69d.tail — same rationale as `AuthClassifier`).
                    let stringProp (name: string) (a: obj) : string option =
                        match a.GetType().GetProperty name with
                        | null -> None
                        | p ->
                            match p.GetValue a with
                            | :? string as s -> Some s
                            | _ -> None

                    let unenforceable =
                        attrs
                        |> Array.choose (fun a ->
                            match a with
                            | :? RateLimitAttribute as rl ->
                                Some(sprintf "RateLimit(%d, %ds)" rl.Count rl.WindowSeconds)
                            | _ ->
                                match a.GetType().Name with
                                | "RequiresRoleAttribute" ->
                                    Some(
                                        sprintf
                                            "RequiresRole(\"%s\")"
                                            (stringProp "Role" a |> Option.defaultValue "?")
                                    )
                                | "RequiresClaimAttribute" ->
                                    Some(
                                        sprintf
                                            "RequiresClaim(\"%s\")"
                                            (stringProp "Claim" a |> Option.defaultValue "?")
                                    )
                                | "TenantScopedAttribute" -> Some "TenantScoped"
                                | "AuditAttribute" ->
                                    Some(sprintf "Audit(\"%s\")" (stringProp "KindName" a |> Option.defaultValue "?"))
                                | "IdempotentAttribute" -> Some "Idempotent"
                                | _ -> None)
                        |> Array.toList

                    if List.isEmpty unenforceable then
                        None
                    else
                        Some(apiField.Name, unenforceable))
            |> Array.toList

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
    /// 0.1.16 — null-guards on both `pi` (property missing) AND on
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

    /// 0.1.16 — split a string containing line terminators into one
    /// `data:` line per source line per the SSE spec
    /// (https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation).
    /// Previously `formatChunk` used a single `data: %s` line, which
    /// silently corrupted the stream when `json` contained literal
    /// `\n` (e.g. when the consumer composed
    /// `JsonSerializerOptions(WriteIndented = true)`) — the first
    /// embedded newline ended the SSE event prematurely.
    let private dataFraming (payload: string) : string =
        // Normalise CRLF / CR to LF first so the spec's `\n\n` event
        // delimiter isn't mis-parsed.
        let normalised = payload.Replace("\r\n", "\n").Replace("\r", "\n")
        // Per the SSE spec, every line of the value is its own
        // `data:` line. Lines are joined with `\n`; the event ends
        // with the standard `\n\n` delimiter (which `formatChunk` /
        // `formatComplete` / `formatError` append).
        normalised.Split('\n')
        |> Array.map (fun line -> "data: " + line)
        |> String.concat "\n"

    /// Frame a single element as an SSE `chunk` event.
    let formatChunk (json: string) : byte[] =
        let frame = sprintf "event: chunk\n%s\n\n" (dataFraming json)
        Encoding.UTF8.GetBytes frame

    /// Frame the terminal `complete` event.
    let formatComplete () : byte[] =
        Encoding.UTF8.GetBytes "event: complete\ndata: {}\n\n"

    /// Frame an exception as an SSE `error` event. Carries the message
    /// only — stack traces stay server-side.
    let formatError (message: string) : byte[] =
        let safe = JsonEncodedText.Encode(message).ToString()
        // The JSON-encoded string never contains literal newlines (they
        // become `\n` escape sequences), so single-line framing is
        // safe here. Kept consistent with `formatChunk`'s shape.
        let payload = sprintf "{\"message\":\"%s\"}" safe
        let frame = sprintf "event: error\n%s\n\n" (dataFraming payload)
        Encoding.UTF8.GetBytes frame