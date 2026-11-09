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
/// disallowed.` A caller writing directly into the response body (the
/// pipeline's response writer) must use this overload.
let jsonSerializeWithBackendAsync (backend: JsonSerializerBackend) (o: 'a) (stream: Stream) =
    match backend with
    | SystemTextJson stjOptions -> JsonSerializer.SerializeAsync<'a>(stream, o, stjOptions)

/// Parse the outer arguments array of a body into one self-contained
/// (`Clone`d) `JsonElement` per argument, so each argument deserialises
/// without a re-parse. The outer parse is part of the decode seam: a body
/// that is not JSON, or is JSON but not an array, is a caller-side shape
/// mistake and refuses rather than throws (`JsonDocument.Parse` raises on
/// malformed input, so the `try` is load-bearing).
let private parseArgumentArrayBytes
    (_backend: JsonSerializerBackend)
    (functionName: string)
    (expectedArgCount: int)
    (bytes: byte[])
    : JsonElement list =
    use doc =
        try
            JsonDocument.Parse(System.ReadOnlyMemory bytes)
        with :? JsonException as ex ->
            DecodeError.failAt
                [ sprintf "%s(args)" functionName ]
                (sprintf "a JSON array of %d argument(s)" expectedArgCount)
                (sprintf "malformed JSON (%s)" ex.Message)

    if doc.RootElement.ValueKind <> JsonValueKind.Array then
        DecodeError.failAt
            [ sprintf "%s(args)" functionName ]
            (sprintf "a JSON array of %d argument(s)" expectedArgCount)
            (string doc.RootElement.ValueKind)

    doc.RootElement.EnumerateArray() |> Seq.map _.Clone() |> Seq.toList

/// Deserialise one `JsonElement` argument into `'inp`, refusing rather than
/// throwing. Routes through the one seam in `FableConverters`, so every
/// exception System.Text.Json or the converter set can raise arrives here as
/// a `DecodeError` naming the argument's path, its expected type and what
/// the wire actually held.
///
/// `recordName` is the owning API record's name
/// (`makeProps.RecordName`), passed through to `FableConverters.tryDeserialiseFor`
/// so a decoder registered for THIS record wins over one registered
/// unscoped for the same argument type.
let private tryDeserialiseArgWithBackend<'inp>
    (recordName: string option)
    (backend: JsonSerializerBackend)
    (argElement: JsonElement)
    : Result<'inp, DecodeError> =
    match backend with
    | SystemTextJson stjOptions ->
        ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseFor<'inp> recordName argElement stjOptions

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

/// Copy a multipart section's body into the supplied stream
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
                        // A typed exception so the pipeline can map
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
                // A typed exception (see copyWithCap above).
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

                // Multipart JSON sections are single values
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

// ─── The endpoint's steps, shared by both routes ────────────────────────
//
// The reflective endpoint below (`makeEndpointProxy`) and a generated
// method (`GeneratedArguments`, SourceGenDispatch.fs) walk a call through
// the SAME steps: take the next argument, then complete the call. The
// generated route is these functions called from typed code rather than a
// second implementation of them.

// An arity mismatch is a decode refusal, not a server fault: the caller
// sent a shape the method cannot accept, the handler never runs, and
// resending the identical bytes fails identically.
let private arityRefusal (makeProps: MakeEndpointProps) (actual: int) : DecodeError =
    let typeInfo =
        typeNames makeProps.FlattenedTypes[0 .. makeProps.FlattenedTypes.Length - 2]

    DecodeError.at
        [ sprintf "%s(args)" makeProps.FieldName ]
        (sprintf "%d argument(s) of the types %s" (makeProps.FlattenedTypes.Length - 1) typeInfo)
        (sprintf "%d argument(s)" actual)

let private validateArgumentCount (props: InvocationPropsInt) (makeProps: MakeEndpointProps) =
    match props.Arguments with
    | _ :: _ -> DecodeError.fail (arityRefusal makeProps props.Arguments.Length)
    | _ -> ()

let private writeToOutputMemoryStream
    (makeProps: MakeEndpointProps)
    (isBinaryOutput: bool)
    (props: InvocationPropsInt)
    (result: 'result)
    =
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

/// What one call through the proxy came to. Internal: the request pipeline's
/// `invoke` stage turns it into the response, and nothing else sees it.
[<RequireQualifiedAccess>]
type internal Invoked =
    /// The handler's result is serialised into the call's output stream;
    /// `binary` when the method returns `byte[]`.
    | Written of binary: bool
    /// A streaming method answered with its source; nothing is written yet.
    | Streamed of StreamSource
    /// No method of this record has the route: another handler may own it.
    | NotRouted
    /// The verb cannot call the method (only POST, or GET for no argument).
    | VerbRefused
    /// The request did not DECODE: a caller-side mistake, the handler never
    /// ran, and the identical bytes would fail identically.
    | Refused of error: DecodeError * methodName: string
    /// The handler (or the serialiser) threw.
    | Faulted of exn * methodName: string * requestBodyText: string option

/// The completion of an `Async<'result>` method: refuse a surplus argument,
/// await, write the result.
let private completeAsync
    (makeProps: MakeEndpointProps)
    (props: InvocationPropsInt)
    (s: Async<'result>)
    : Task<Invoked> =
    let isBinaryOutput = typeof<'result> = typeof<byte[]>

    task {
        validateArgumentCount props makeProps
        let! result = s
        writeToOutputMemoryStream makeProps isBinaryOutput props result
        return Invoked.Written isBinaryOutput
    }

/// The completion of a `Task<'result>` method.
let private completeTask (makeProps: MakeEndpointProps) (props: InvocationPropsInt) (s: Task<'result>) : Task<Invoked> =
    let isBinaryOutput = typeof<'result> = typeof<byte[]>

    task {
        validateArgumentCount props makeProps
        let! result = s
        writeToOutputMemoryStream makeProps isBinaryOutput props result
        return Invoked.Written isBinaryOutput
    }

/// One argument step: the value of the next parameter (typed `'inp`) and
/// the call's state with that argument consumed. Raises a `DecodeException`
/// for a refusal, which the endpoint's `invoke` turns into `Invoked.Refused`.
let private nextArgument<'inp>
    (makeProps: MakeEndpointProps)
    (props: InvocationPropsInt)
    : struct ('inp * InvocationPropsInt) =
    match props.Arguments, props.FirstArgument with
    | Choice1Of2 bytes :: t, _ ->
        if typeof<'inp> <> typeof<byte[]> then
            // A multipart binary section landing on a
            // non-`byte[]` parameter is a shape refusal
            // like any other.
            DecodeError.failAt [ sprintf "%s(args)" makeProps.FieldName ] typeof<'inp>.Name "binary input"

        let inp = box bytes :?> 'inp
        struct (inp, { props with Arguments = t })
    | Choice2Of2 _ :: t, ValueSome first ->
        // The validation stage already decoded this
        // argument through the same
        // `tryDeserialiseArgWithBackend<'inp>` (see
        // `ApiProxy.ParseFirst`); the value is reused,
        // never deserialised twice.
        let rest = {
            props with
                Arguments = t
                FirstArgument = ValueNone
        }

        struct (unbox<'inp> first, rest)
    | Choice2Of2 argElement :: t, ValueNone ->
        // argElement is a self-contained (cloned)
        // `JsonElement`, deserialised without a re-parse.
        // An `Error` is annotated with the argument's
        // index and raised as a refusal, which the
        // endpoint turns into `Invoked.Refused`, so `f` is
        // never applied.
        let argIndex = makeProps.FlattenedTypes.Length - 1 - props.Arguments.Length

        match tryDeserialiseArgWithBackend<'inp> (Some makeProps.RecordName) makeProps.JsonSerializer argElement with
        | Ok inp -> struct (inp, { props with Arguments = t })
        | Error error ->
            error
            |> DecodeError.under (sprintf "args[%d]" (max argIndex 0))
            |> DecodeError.under makeProps.FieldName
            |> DecodeError.fail
    | [], _ when typeof<'inp> = typeof<unit> ->
        let inp = box () :?> 'inp
        struct (inp, { props with Arguments = [] })
    | [], _ -> DecodeError.fail (arityRefusal makeProps props.Arguments.Length)

let rec private makeEndpointProxy<'fieldPart>
    (makeProps: MakeEndpointProps)
    : 'fieldPart -> InvocationPropsInt -> Task<Invoked> =
    let wrap (p: 'a -> InvocationPropsInt -> Task<Invoked>) =
        unbox<'fieldPart -> InvocationPropsInt -> Task<Invoked>> p

    match shapeof<'fieldPart> with
    | FSharpAsync a ->
        a.Element.Accept
            { new ITypeVisitor<'fieldPart -> InvocationPropsInt -> Task<Invoked>> with
                member _.Visit<'result>() =
                    wrap (fun (s: Async<'result>) props -> completeAsync makeProps props s)
            }
    | Task t ->
        t.Element.Accept
            { new ITypeVisitor<'fieldPart -> InvocationPropsInt -> Task<Invoked>> with
                member _.Visit<'result>() =
                    wrap (fun (s: Task<'result>) props -> completeTask makeProps props s)
            }
    | Shape.FSharpFunc func ->
        func.Accept
            { new IFSharpFuncVisitor<'fieldPart -> InvocationPropsInt -> Task<Invoked>> with
                member _.Visit<'inp, 'out>() =
                    let outp = makeEndpointProxy<'out> makeProps

                    wrap (fun (f: 'inp -> 'out) props ->
                        let struct (inp, rest) = nextArgument<'inp> makeProps props
                        outp (f inp) rest)
            }
    | _ when Streaming.isAsyncEnumerable typeof<'fieldPart> ->
        // A streaming method answers with its source, built once per method
        // for its element type; the response writer iterates it.
        let toSource = Streaming.sourceFor (typeof<'fieldPart>.GetGenericArguments()[0])

        let options =
            match makeProps.JsonSerializer with
            | SystemTextJson options -> options

        wrap (fun (stream: 'fieldPart) props ->
            validateArgumentCount props makeProps

            if isNull (box stream) then
                invalidOp (
                    sprintf "streaming method '%s' returned null instead of an IAsyncEnumerable<_>" makeProps.FieldName
                )

            Task.FromResult(Invoked.Streamed(toSource options (box stream))))
    | _ ->
        failwithf
            "The type '%s' of the record field '%s' for record type '%s' is not valid. It must either be Async<'t>, Task<'t>, IAsyncEnumerable<'t> or a function that returns one of them (i.e. 'u -> Async<'t>)"
            typeof<'fieldPart>.Name
            makeProps.FieldName
            makeProps.RecordName

/// A request's arguments, parsed and with the FIRST one decoded, by the
/// proxy's own argument path, ahead of dispatch.
type internal ParsedArguments = {
    /// The outer array's elements, exactly as the dispatch path parses them.
    Elements: JsonElement list
    /// `Elements.Head`, decoded through `tryDeserialiseArgWithBackend<'inp>`
    /// for the method's first parameter type — the value the handler gets.
    First: obj
}

/// The proxy, with its argument parse exposed to the request pipeline.
///
/// `ParseFirst` is the dispatch path's own parse and first-argument decode,
/// run early: handing its result to `Invoke` means the body is parsed once
/// and the first argument decoded once, and validation sees exactly the value
/// the handler is given (the record-scoped decoder included). It answers
/// `ValueNone` for anything it cannot parse or decode — the refusal stays the
/// dispatch path's to make, as `Invoked.Refused`.
type internal ApiProxy<'impl> = {
    Invoke: InvocationProps<'impl> -> ParsedArguments voption -> Task<Invoked>
    /// Endpoint name (the built route, as `InvocationProps.EndpointName`),
    /// then the cached body bytes.
    ParseFirst: string -> byte[] -> ParsedArguments voption
}

type private ProxyEndpoint<'impl> = {
    Invoke: InvocationProps<'impl> -> ParsedArguments voption -> Task<Invoked>
    ParseFirst: byte[] -> ParsedArguments voption
}

/// One method's endpoint: the verb check, the body read and the
/// argument-array parse, the early first-argument parse and the refusal
/// mapping, around `dispatch` — the method's own call. A reflective endpoint
/// and a generated one are this same function with a different `dispatch`.
let private makeEndpoint<'impl, 'ctx>
    (options: RemotingOptions<'ctx, 'impl>)
    (methodName: string)
    (flattenedTypes: Type[])
    (decodeFirst: (JsonElement -> Result<obj, DecodeError>) option)
    (dispatch: 'impl -> InvocationPropsInt -> Task<Invoked>)
    : ProxyEndpoint<'impl> =
    let isNoArg =
        flattenedTypes.Length = 1
        || (flattenedTypes.Length = 2 && flattenedTypes[0] = typeof<unit>)

    let parseFirst (bytes: byte[]) : ParsedArguments voption =
        match decodeFirst with
        | Some decode when bytes.Length > 0 ->
            try
                match parseArgumentArrayBytes options.JsonSerializer methodName (flattenedTypes.Length - 1) bytes with
                | first :: _ as elements ->
                    match decode first with
                    | Ok value -> ValueSome { Elements = elements; First = value }
                    | Error _ -> ValueNone
                | [] -> ValueNone
            with DecodeException _ ->
                ValueNone
        | _ -> ValueNone

    /// The request body: the bytes the pipeline already read, or the stream
    /// the proxy BORROWS (read, left open, rewound, so a stage after dispatch
    /// still sees the whole body — `PostDispatchBodyReadTests`).
    let bodyOf (props: InvocationProps<'impl>) : Task<byte[]> =
        match props.InputBytes with
        | Some bytes -> Task.FromResult bytes
        | None -> task {
            use buffer = new MemoryStream()
            do! props.Input.CopyToAsync buffer

            if props.Input.CanSeek then
                props.Input.Position <- 0L

            return buffer.ToArray()
          }

    let invoke (props: InvocationProps<'impl>) (parsed: ParsedArguments voption) = task {
        try
            if
                not (props.HttpVerb.Equals("POST", StringComparison.OrdinalIgnoreCase))
                && not (isNoArg && props.HttpVerb.Equals("GET", StringComparison.OrdinalIgnoreCase))
            then
                return Invoked.VerbRefused
            elif props.InputContentType.StartsWith("multipart/form-data", StringComparison.Ordinal) then
                let! args = readMultipartArgs props options

                let props' = {
                    Arguments = args
                    FirstArgument = ValueNone
                    IsProxyHeaderPresent = props.IsProxyHeaderPresent
                    Output = props.Output
                }

                return! dispatch (props.ImplementationBuilder()) props'
            else
                let! args = task {
                    match parsed with
                    | ValueSome p ->
                        // Parsed ahead of dispatch, from these same bytes.
                        return p.Elements |> List.map Choice2Of2
                    | ValueNone ->
                        let! bytes = bodyOf props

                        if bytes.Length = 0 then
                            return []
                        else
                            return
                                parseArgumentArrayBytes
                                    options.JsonSerializer
                                    methodName
                                    (flattenedTypes.Length - 1)
                                    bytes
                                |> List.map Choice2Of2
                }

                let props' = {
                    Arguments = args
                    FirstArgument =
                        match parsed with
                        | ValueSome p -> ValueSome p.First
                        | ValueNone -> ValueNone
                    IsProxyHeaderPresent = props.IsProxyHeaderPresent
                    Output = props.Output
                }

                return! dispatch (props.ImplementationBuilder()) props'
        // A decode refusal is its own outcome, never a fault. The decode
        // happens deep inside the curried call, so this is the one place the
        // refusal exists. Argument decoding stays here, AFTER the pipeline's
        // authorisation and rate limit: an unauthenticated caller learns no
        // field names and is charged for what it sends.
        with
        | DecodeException error -> return Invoked.Refused(error, methodName)
        | e ->
            // The body text rides the fault for the error handler; read on
            // this cold path only.
            let bodyText =
                match props.InputBytes with
                | Some bytes when bytes.Length > 0 -> Some(Encoding.UTF8.GetString bytes)
                | _ -> None

            return Invoked.Faulted(e, methodName, bodyText)
    }

    {
        Invoke = invoke
        ParseFirst = parseFirst
    }

let private makeReflectiveApiProxy<'impl, 'ctx> (options: RemotingOptions<'ctx, 'impl>) : ApiProxy<'impl> =
    let memberVisitor (shape: IShapeMember<'impl>, flattenedTypes: Type[]) : ProxyEndpoint<'impl> =
        shape.Accept
            { new IReadOnlyMemberVisitor<'impl, ProxyEndpoint<'impl>> with
                member _.Visit(shape: ReadOnlyMember<'impl, 'field>) =
                    let fieldProxy =
                        makeEndpointProxy<'field> {
                            FieldName = shape.MemberInfo.Name
                            RecordName = typeof<'impl>.Name
                            ResponseSerialization = options.ResponseSerialization
                            JsonSerializer = options.JsonSerializer
                            FlattenedTypes = flattenedTypes
                        }

                    // The first parameter's decode, built once per method.
                    // Absent for a method with no JSON first argument to
                    // decode (no-argument, or `unit`).
                    let decodeFirst: (JsonElement -> Result<obj, DecodeError>) option =
                        if flattenedTypes.Length < 2 || flattenedTypes[0] = typeof<unit> then
                            None
                        else
                            Some(
                                TypeShape.Create(flattenedTypes[0]).Accept
                                    { new ITypeVisitor<JsonElement -> Result<obj, DecodeError>> with
                                        member _.Visit<'inp>() =
                                            fun element ->
                                                tryDeserialiseArgWithBackend<'inp>
                                                    (Some typeof<'impl>.Name)
                                                    options.JsonSerializer
                                                    element
                                                |> Result.map box
                                    }
                            )

                    makeEndpoint options shape.MemberInfo.Name flattenedTypes decodeFirst (fun impl props ->
                        fieldProxy (shape.Get impl) props)
            }

    match shapeof<'impl> with
    | Shape.FSharpRecord(:? ShapeFSharpRecord<'impl> as shape) ->
        let endpoints =
            shape.Fields
            |> Array.map (fun f ->
                options.RouteBuilder typeof<'impl>.Name f.MemberInfo.Name,
                memberVisitor (f, TypeInfo.flattenFuncTypes f.Member.Type))
            |> Map.ofArray

        {
            Invoke =
                fun (props: InvocationProps<'impl>) parsed ->
                    match Map.tryFind props.EndpointName endpoints with
                    | Some endpoint -> endpoint.Invoke props parsed
                    | _ -> Task.FromResult Invoked.NotRouted
            ParseFirst =
                fun endpointName bytes ->
                    match Map.tryFind endpointName endpoints with
                    | Some endpoint -> endpoint.ParseFirst bytes
                    | None -> ValueNone
        }
    | _ ->
        failwithf
            "Protocol definition must be encoded as a record type. The input type '%s' was not a record."
            typeof<'impl>.Name

/// The proxy's side of `GeneratedArguments`: generated code
/// takes each argument and completes the call through the endpoint's own
/// steps (`nextArgument`, `completeAsync`), never a copy of them.
type private ProxyGeneratedArguments(makeProps: MakeEndpointProps, initial: InvocationPropsInt) =
    inherit GeneratedArguments()
    let mutable props = initial

    override _.Next<'T>() : 'T =
        let struct (value, rest) = nextArgument<'T> makeProps props
        props <- rest
        value

    override _.Complete<'T>(call: Async<'T>) : Task =
        completeAsync makeProps props call :> Task

/// The proxy over a generated invocation table. Each covered
/// method's endpoint is `makeEndpoint` around the generated call; a route
/// the table does not cover is served by the reflective proxy, built on the
/// first request that needs it (so a record every method of which is
/// covered never pays for it). Building this reflects over nothing: the
/// method names and flattened types are the table's, emitted at build time.
let private makeGeneratedApiProxy<'impl, 'ctx>
    (table: GeneratedInvocationTable<'impl>)
    (options: RemotingOptions<'ctx, 'impl>)
    : ApiProxy<'impl> =
    let recordName = typeof<'impl>.Name

    let endpoint (m: GeneratedMethod<'impl>) : ProxyEndpoint<'impl> =
        let makeProps = {
            FieldName = m.Name
            RecordName = recordName
            ResponseSerialization = options.ResponseSerialization
            JsonSerializer = options.JsonSerializer
            FlattenedTypes = m.FlattenedTypes
        }

        let decodeFirst =
            m.DecodeFirst
            |> Option.map (fun decode -> decode recordName options.JsonSerializer)

        // A generated method returns `Async<'r>`; its result is binary
        // exactly when `'r` is `byte[]`.
        let binary =
            (Array.last m.FlattenedTypes).GetGenericArguments() = [| typeof<byte[]> |]

        makeEndpoint options m.Name m.FlattenedTypes decodeFirst (fun impl props -> task {
            do! m.Call (ProxyGeneratedArguments(makeProps, props)) impl
            return Invoked.Written binary
        })

    let endpoints =
        table.Methods
        |> List.map (fun m -> options.RouteBuilder recordName m.Name, endpoint m)
        |> Map.ofList

    let reflective = lazy (makeReflectiveApiProxy options)

    {
        Invoke =
            fun (props: InvocationProps<'impl>) parsed ->
                match Map.tryFind props.EndpointName endpoints with
                | Some endpoint -> endpoint.Invoke props parsed
                | None -> reflective.Value.Invoke props parsed
        ParseFirst =
            fun endpointName bytes ->
                match Map.tryFind endpointName endpoints with
                | Some endpoint -> endpoint.ParseFirst bytes
                | None -> reflective.Value.ParseFirst endpointName bytes
    }

/// The proxy the request pipeline dispatches through. When the
/// generator's table for `'impl` is registered (`GeneratedInvocation.register`),
/// its methods are served by their generated invocations; otherwise, and
/// for any method the table does not cover, by reflection. Either way it is
/// the same `ApiProxy`, so every stage of the pipeline runs around a
/// generated method too.
let internal makeApiProxyWithParse<'impl, 'ctx> (options: RemotingOptions<'ctx, 'impl>) : ApiProxy<'impl> =
    match GeneratedDispatchRegistry.tryGet<'impl> () with
    | Some(:? GeneratedInvocationTable<'impl> as table) -> makeGeneratedApiProxy table options
    | _ -> makeReflectiveApiProxy options

/// The contract of `'impl`: the registered generated table's, when there is
/// one, so the record is not walked at boot; otherwise `Contract.read`.
let internal contractOf<'impl> () : ApiContract =
    match GeneratedDispatchRegistry.tryGet<'impl> () with
    | Some(:? GeneratedInvocationTable<'impl> as table) -> table.Contract
    | _ -> Contract.read typeof<'impl>

/// The outer arguments array of a body read by the pipeline, as the proxy
/// parses it: a refusal for a body that is not a JSON array.
let internal parseArguments
    (backend: JsonSerializerBackend)
    (functionName: string)
    (expectedArgCount: int)
    (bytes: byte[])
    : Result<JsonElement list, DecodeError> =
    try
        Ok(parseArgumentArrayBytes backend functionName expectedArgCount bytes)
    with DecodeException error ->
        Error error