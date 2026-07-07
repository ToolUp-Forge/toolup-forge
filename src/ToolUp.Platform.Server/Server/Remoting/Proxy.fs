module ToolUp.Remoting.Server.Proxy

open TypeShape
open ToolUp.Remoting
open System
open System.Buffers
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Net.Http.Headers
open Microsoft.AspNetCore.WebUtilities

/// Serialise the value to the output stream using the configured backend.
/// Backend is `SystemTextJson opts` → `JsonSerializer.Serialize` with the
/// provided options. Public so sibling adapters (Giraffe, AspNetCore,
/// AwsLambda, AzureFunctions.Worker) can route their response-path
/// serialisation (docs schema, error bodies, etc.) through the same code
/// path the main proxy uses.
let jsonSerializeWithBackend (backend: JsonSerializerBackend) (o: 'a) (stream: Stream) =
    match backend with
    | SystemTextJson stjOptions -> JsonSerializer.Serialize<'a>(stream, o, stjOptions)

/// Async overload of `jsonSerializeWithBackend` for hot-path direct-writes
/// into `HttpResponse.Body`. Kestrel runs with `AllowSynchronousIO=false`
/// by default, which rejects the sync `JsonSerializer.Serialize(stream,...)`
/// overload with `InvalidOperationException: Synchronous operations are
/// disallowed.` Callers writing the envelope directly into the response
/// body (Giraffe `setJsonBody` / `setResponseBody` hot path, AspNetCore
/// middleware equivalent) must use this overload.
let jsonSerializeWithBackendAsync (backend: JsonSerializerBackend) (o: 'a) (stream: Stream) =
    match backend with
    | SystemTextJson stjOptions -> JsonSerializer.SerializeAsync<'a>(stream, o, stjOptions)

/// Phase 69m — parse the outer arguments-array JSON into a list of
/// per-argument `JsonElement` clones. Each element is `Clone()`d so it
/// survives the parent JsonDocument's disposal (the clone produces a
/// self-contained JsonElement backed by a fresh small JsonDocument
/// holding only that element's bytes). The per-argument deserialise
/// path consumes the JsonElement directly — no re-parse per argument.
///
/// Previously this function returned `string list` (raw JSON text per
/// argument) and the deserialise path re-parsed each slice into its
/// own JsonDocument, costing N+1 parses for N arguments.
let private parseArgumentArray
    (_backend: JsonSerializerBackend)
    (functionName: string)
    (expectedArgCount: int)
    (text: string)
    : JsonElement list =
    use doc = JsonDocument.Parse(text)

    if doc.RootElement.ValueKind <> JsonValueKind.Array then
        failwithf
            "The record function '%s' expected %d argument(s) to be received in the form of a JSON array but the input JSON was not an array"
            functionName
            expectedArgCount

    doc.RootElement.EnumerateArray() |> Seq.map _.Clone() |> Seq.toList

/// Phase 69m — parse the outer arguments-array from cached bytes
/// directly (no string materialisation). Used when the adapter's body
/// cache already holds the bytes for upstream pre-flight stages
/// (validation / audit / idempotency-hash); the proxy reads from there
/// instead of re-allocating a string + re-reading the request stream.
let private parseArgumentArrayBytes
    (_backend: JsonSerializerBackend)
    (functionName: string)
    (expectedArgCount: int)
    (bytes: byte[])
    : JsonElement list =
    use doc = JsonDocument.Parse(System.ReadOnlyMemory bytes)

    if doc.RootElement.ValueKind <> JsonValueKind.Array then
        failwithf
            "The record function '%s' expected %d argument(s) to be received in the form of a JSON array but the input JSON was not an array"
            functionName
            expectedArgCount

    doc.RootElement.EnumerateArray() |> Seq.map _.Clone() |> Seq.toList

/// Phase 69m — deserialise one `JsonElement` argument into `'inp`.
/// `JsonElement.Deserialize` walks the existing element's tokens; no
/// re-parse. Previously this function took raw JSON text and called
/// `JsonSerializer.Deserialize<'inp>(text, opts)`, which re-parsed
/// the slice into its own JsonDocument per argument.
let private deserialiseArgWithBackend<'inp> (backend: JsonSerializerBackend) (argElement: JsonElement) : 'inp =
    match backend with
    | SystemTextJson stjOptions -> argElement.Deserialize<'inp>(stjOptions)

type private MsgPackSerializer<'a> =
    static let serializer = MsgPack.Write.makeSerializer<'a> ()
    static member Serialize(o, stream) = serializer.Invoke(o, stream)

let private recyclableMemoryStreamManager =
    Lazy<Microsoft.IO.RecyclableMemoryStreamManager>()

let getRecyclableMemoryStreamManager options =
    options.RmsManager
    |> Option.defaultWith (fun _ -> recyclableMemoryStreamManager.Value)

let private typeNames inputTypes =
    inputTypes
    |> Array.map Diagnostics.typePrinter
    |> String.concat ", "
    |> sprintf "[%s]"

let private (|FSharpAsync|_|) (s: TypeShape) =
    match s.ShapeInfo with
    | Generic(td, ta) when td = typedefof<Async<_>> ->
        Activator.CreateInstanceGeneric<ShapeFSharpAsyncOrTask<_>>(ta) :?> IShapeFSharpAsyncOrTask
        |> Some
    | _ -> None

let private (|Task|_|) (s: TypeShape) =
    match s.ShapeInfo with
    | Generic(td, ta) when td = typedefof<Task<_>> ->
        Activator.CreateInstanceGeneric<ShapeFSharpAsyncOrTask<_>>(ta) :?> IShapeFSharpAsyncOrTask
        |> Some
    | _ -> None

/// 0.1.15 — copy a multipart section's body into the supplied stream
/// with a hard byte cap. Raises a clear error if the cap is exceeded
/// before materialising the entire section's bytes (would otherwise
/// OOM the host on a hostile request).
let private copyWithCap (source: Stream) (target: Stream) (cap: int64) (sectionIdx: int) : System.Threading.Tasks.Task =
    task {
        let buffer = ArrayPool<byte>.Shared.Rent(64 * 1024)

        try
            let mutable copied = 0L
            let mutable keepReading = true

            while keepReading do
                let! n = source.ReadAsync(buffer, 0, buffer.Length)

                if n <= 0 then
                    keepReading <- false
                else
                    copied <- copied + int64 n

                    if copied > cap then
                        // 0.1.16 — typed exception so the adapter can map
                        // to `ErrorCategory.User` + 413 Payload Too Large
                        // rather than the generic `System` category any
                        // plain failwithf would land in.
                        raise (
                            MultipartCapExceededException(
                                sprintf
                                    "Multipart section %d exceeds the configured byte cap (%d bytes). Adjust via Remoting.withMaxMultipartSectionBytes if your application requires larger uploads."
                                    sectionIdx
                                    cap
                            )
                        )

                    do! target.WriteAsync(buffer, 0, n)
        finally
            ArrayPool<byte>.Shared.Return buffer
    }
    :> System.Threading.Tasks.Task

let private readMultipartArgs props (options: RemotingOptions<_, _>) = task {
    let mediaType = MediaTypeHeaderValue.Parse props.InputContentType
    let boundary = HeaderUtilities.RemoveQuotes mediaType.Boundary

    if
        Microsoft.Extensions.Primitives.StringSegment.IsNullOrEmpty boundary
        || boundary.Length > 70
    then
        failwith "Multipart boundary missing or too long"

    let reader = MultipartReader(boundary.ToString(), props.Input)
    let parts = ResizeArray()
    let mutable go = true
    let mutable sectionIdx = 0
    let sectionCap = options.MaxMultipartSectionBytes
    let sectionCountCap = options.MaxMultipartSections

    while go do
        let! section = reader.ReadNextSectionAsync()

        if isNull section then
            go <- false
        else
            if sectionIdx >= sectionCountCap then
                // 0.1.16 — typed exception (see copyWithCap above).
                raise (
                    MultipartCapExceededException(
                        sprintf
                            "Multipart request exceeds the configured section count cap (%d). Adjust via Remoting.withMaxMultipartSections if your application requires more sections."
                            sectionCountCap
                    )
                )

            if section.ContentType.Equals("application/octet-stream", StringComparison.Ordinal) then
                use buffer =
                    (getRecyclableMemoryStreamManager options).GetStream "remoting-input-multipart"

                do! copyWithCap section.Body buffer sectionCap sectionIdx
                parts.Add(buffer.GetReadOnlySequence().ToArray() |> Choice1Of2)
            else
                // Text sections also need to be bounded — drain into a
                // capped MemoryStream then decode as UTF-8.
                use buffer =
                    (getRecyclableMemoryStreamManager options).GetStream "remoting-input-multipart-text"

                do! copyWithCap section.Body buffer sectionCap sectionIdx

                // Phase 69m — multipart JSON sections are single values
                // (one argument per multipart part), so the section's
                // payload IS the per-argument JSON. Parse directly from
                // the buffer's bytes into a `JsonElement` (then `Clone`
                // for survival beyond the JsonDocument's `using` scope).
                let bytes = buffer.GetReadOnlySequence().ToArray()
                use sectionDoc = JsonDocument.Parse(System.ReadOnlyMemory bytes)
                parts.Add(Choice2Of2(sectionDoc.RootElement.Clone()))

            sectionIdx <- sectionIdx + 1

    return Seq.toList parts
}

let rec private makeEndpointProxy<'fieldPart>
    (makeProps: MakeEndpointProps)
    : 'fieldPart -> InvocationPropsInt -> Task<InvocationResult> =
    let wrap (p: 'a -> InvocationPropsInt -> Task<InvocationResult>) =
        unbox<'fieldPart -> InvocationPropsInt -> Task<InvocationResult>> p

    // Check that no arguments are left
    let validateArgumentCount props makeProps =
        match props.Arguments with
        | _ :: _ ->
            let typeInfo =
                typeNames makeProps.FlattenedTypes[0 .. makeProps.FlattenedTypes.Length - 2]

            failwithf
                "The record function '%s' expected %d argument(s) of the types %s but got %d argument(s) in the input JSON array"
                makeProps.FieldName
                (makeProps.FlattenedTypes.Length - 1)
                typeInfo
                props.Arguments.Length
        | _ -> ()

    let writeToOutputMemoryStream isBinaryOutput (props: InvocationPropsInt) result =
        if
            isBinaryOutput
            && props.IsProxyHeaderPresent
            && makeProps.ResponseSerialization.IsJson
        then
            let data = box result :?> byte[]
            props.Output.Write(data, 0, data.Length)
        elif makeProps.ResponseSerialization.IsJson then
            jsonSerializeWithBackend makeProps.JsonSerializer result props.Output
        else
            MsgPackSerializer.Serialize(result, props.Output)

        props.Output.Position <- 0L

    match shapeof<'fieldPart> with
    | FSharpAsync a ->
        a.Element.Accept
            { new ITypeVisitor<'fieldPart -> InvocationPropsInt -> Task<InvocationResult>> with
                member _.Visit<'result>() =
                    let isBinaryOutput = typeof<'result> = typeof<byte[]>

                    wrap (fun (s: Async<'result>) props -> task {
                        validateArgumentCount props makeProps
                        let! result = s
                        writeToOutputMemoryStream isBinaryOutput props result
                        return Success isBinaryOutput
                    })
            }
    | Task t ->
        t.Element.Accept
            { new ITypeVisitor<'fieldPart -> InvocationPropsInt -> Task<InvocationResult>> with
                member _.Visit<'result>() =
                    let isBinaryOutput = typeof<'result> = typeof<byte[]>

                    wrap (fun (s: Task<'result>) props -> task {
                        validateArgumentCount props makeProps
                        let! result = s
                        writeToOutputMemoryStream isBinaryOutput props result
                        return Success isBinaryOutput
                    })
            }
    | Shape.FSharpFunc func ->
        func.Accept
            { new IFSharpFuncVisitor<'fieldPart -> InvocationPropsInt -> Task<InvocationResult>> with
                member _.Visit<'inp, 'out>() =
                    let outp = makeEndpointProxy<'out> makeProps

                    wrap (fun (f: 'inp -> 'out) props ->
                        match props.Arguments with
                        | Choice1Of2 bytes :: t ->
                            if typeof<'inp> <> typeof<byte[]> then
                                failwithf
                                    "The record function '%s' expected an argument of type %s, but got binary input"
                                    makeProps.FieldName
                                    typeof<'inp>.Name

                            let inp = box bytes :?> 'inp
                            outp (f inp) { props with Arguments = t }
                        | Choice2Of2 argElement :: t ->
                            // Phase 69m: argElement is a self-contained
                            // (Clone'd) `JsonElement`. `JsonElement.Deserialize`
                            // walks the existing element tokens — no re-parse.
                            // Previous behaviour was N+1 parses (outer array
                            // parse + per-argument re-parse from text); the
                            // new shape is one parse total.
                            let inp = deserialiseArgWithBackend<'inp> makeProps.JsonSerializer argElement
                            outp (f inp) { props with Arguments = t }
                        | [] when typeof<'inp> = typeof<unit> ->
                            let inp = box () :?> _
                            outp (f inp) { props with Arguments = [] }
                        | [] ->
                            let typeInfo =
                                typeNames makeProps.FlattenedTypes[0 .. makeProps.FlattenedTypes.Length - 2]

                            failwithf
                                "The record function '%s' expected %d argument(s) of the types %s but got %d argument(s) in the input"
                                makeProps.FieldName
                                (makeProps.FlattenedTypes.Length - 1)
                                typeInfo
                                props.Arguments.Length)
            }
    | _ ->
        // Phase 69c — streaming methods returning IAsyncEnumerable<'T>
        // are handled by the adapter's SSE short-circuit BEFORE the proxy
        // dispatches. Emit a no-op endpoint here so startup classification
        // doesn't reject the record; the adapter never invokes this stub
        // for streaming methods at request time.
        let returnT = typeof<'fieldPart>

        let isStreamingReturn =
            returnT.IsGenericType
            && returnT.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IAsyncEnumerable<_>>

        let isStreamingFuncReturn =
            FSharp.Reflection.FSharpType.IsFunction returnT
            && (let _, ret = FSharp.Reflection.FSharpType.GetFunctionElements returnT

                ret.IsGenericType
                && ret.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IAsyncEnumerable<_>>)

        if isStreamingReturn || isStreamingFuncReturn then
            fun (_: 'fieldPart) (_: InvocationPropsInt) ->
                Task.FromResult(
                    InvocationResult.Exception(
                        exn
                            "Phase 69c streaming method invoked through proxy fallback — adapter SSE short-circuit was skipped",
                        makeProps.FieldName,
                        None
                    )
                )
        else
            failwithf
                "The type '%s' of the record field '%s' for record type '%s' is not valid. It must either be Async<'t>, Task<'t> or a function that returns either (i.e. 'u -> Async<'t>)"
                typeof<'fieldPart>.Name
                makeProps.FieldName
                makeProps.RecordName

let makeApiProxy<'impl, 'ctx>
    (options: RemotingOptions<'ctx, 'impl>)
    : InvocationProps<'impl> -> Task<InvocationResult> =
    let wrap (p: InvocationProps<'a> -> Task<InvocationResult>) =
        unbox<InvocationProps<'impl> -> Task<InvocationResult>> p

    let memberVisitor (shape: IShapeMember<'impl>, flattenedTypes: Type[]) =
        shape.Accept
            { new IReadOnlyMemberVisitor<'impl, InvocationProps<'impl> -> Task<InvocationResult>> with
                member _.Visit(shape: ReadOnlyMember<'impl, 'field>) =
                    let fieldProxy =
                        makeEndpointProxy<'field> {
                            FieldName = shape.MemberInfo.Name
                            RecordName = typeof<'impl>.Name
                            ResponseSerialization = options.ResponseSerialization
                            JsonSerializer = options.JsonSerializer
                            FlattenedTypes = flattenedTypes
                        }

                    let isNoArg =
                        flattenedTypes.Length = 1
                        || (flattenedTypes.Length = 2 && flattenedTypes[0] = typeof<unit>)

                    wrap (fun (props: InvocationProps<'impl>) -> task {
                        let mutable requestBodyText = None

                        try
                            if
                                not (props.HttpVerb.Equals("POST", StringComparison.OrdinalIgnoreCase))
                                && not (isNoArg && props.HttpVerb.Equals("GET", StringComparison.OrdinalIgnoreCase))
                            then
                                return InvalidHttpVerb
                            elif
                                props.InputContentType.StartsWith("multipart/form-data", StringComparison.Ordinal)
                            then
                                let! args = readMultipartArgs props options

                                let props' = {
                                    Arguments = args
                                    IsProxyHeaderPresent = props.IsProxyHeaderPresent
                                    Output = props.Output
                                }

                                return! fieldProxy (props.ImplementationBuilder() |> shape.Get) props'
                            else
                                // Phase 69m — if the adapter populated
                                // `InputBytes` (cached body bytes from an
                                // upstream pre-flight stage), parse directly
                                // from bytes — no second string materialisation
                                // and no second stream read on `ctx.Request.Body`.
                                // Fallback (no cache) reads the stream as before.
                                let! args = task {
                                    match props.InputBytes with
                                    | Some bytes when bytes.Length > 0 ->
                                        // `requestBodyText` stays None on the
                                        // happy path; the exception arm
                                        // materialises text from these bytes
                                        // lazily for error reporting.
                                        return
                                            parseArgumentArrayBytes
                                                options.JsonSerializer
                                                shape.MemberInfo.Name
                                                (flattenedTypes.Length - 1)
                                                bytes
                                            |> List.map Choice2Of2
                                    | Some _ ->
                                        // Empty cached bytes — same shape as
                                        // an empty stream read.
                                        return []
                                    | None ->
                                        // LOAD-BEARING: this `use` disposes
                                        // `props.Input` (= ctx.Request.Body, a
                                        // FileBufferingReadStream) when the proxy
                                        // finishes. Any dispatcher stage that
                                        // reads the request body AFTER dispatch
                                        // (audit payload, idempotency hash, …)
                                        // MUST pre-seed the body cache
                                        // (`readCachedBodyBytes` in GiraffeAdapter)
                                        // BEFORE this point — otherwise its
                                        // post-dispatch read hits a disposed
                                        // stream and throws ObjectDisposedException
                                        // after the response has started, which
                                        // resets the connection and surfaces as a
                                        // gateway 502 even though the handler
                                        // succeeded. See the eager-materialise
                                        // guards in GiraffeAdapter (idempotency +
                                        // audit) for the established pattern.
                                        use sr = new StreamReader(props.Input)
                                        let! text = sr.ReadToEndAsync()

                                        if String.IsNullOrEmpty text then
                                            return []
                                        else
                                            requestBodyText <- Some text

                                            return
                                                parseArgumentArray
                                                    options.JsonSerializer
                                                    shape.MemberInfo.Name
                                                    (flattenedTypes.Length - 1)
                                                    text
                                                |> List.map Choice2Of2
                                }

                                let props' = {
                                    Arguments = args
                                    IsProxyHeaderPresent = props.IsProxyHeaderPresent
                                    Output = props.Output
                                }

                                return! fieldProxy (props.ImplementationBuilder() |> shape.Get) props'
                        with e ->
                            // Phase 69m — when the cached-bytes path was
                            // taken, `requestBodyText` is None on the happy
                            // path. Materialise text from cached bytes here
                            // (cold path; cost only paid on actual errors)
                            // so error reporting still carries the request
                            // body context.
                            let resolvedBodyText =
                                match requestBodyText with
                                | Some _ -> requestBodyText
                                | None ->
                                    match props.InputBytes with
                                    | Some bytes when bytes.Length > 0 ->
                                        Some(System.Text.Encoding.UTF8.GetString bytes)
                                    | _ -> None

                            return InvocationResult.Exception(e, shape.MemberInfo.Name, resolvedBodyText)
                    })
            }

    match shapeof<'impl> with
    | Shape.FSharpRecord(:? ShapeFSharpRecord<'impl> as shape) ->
        let endpoints =
            shape.Fields
            |> Array.map (fun f ->
                options.RouteBuilder typeof<'impl>.Name f.MemberInfo.Name,
                memberVisitor (f, TypeInfo.flattenFuncTypes f.Member.Type))
            |> Map.ofArray

        wrap (fun (props: InvocationProps<'impl>) ->
            match Map.tryFind props.EndpointName endpoints with
            | Some endpoint -> endpoint props
            | _ -> Task.FromResult EndpointNotFound)
    | _ ->
        failwithf
            "Protocol definition must be encoded as a record type. The input type '%s' was not a record."
            typeof<'impl>.Name