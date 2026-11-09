namespace ToolUp.Remoting.Server.Stages

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open ToolUp.Remoting.Server
open ToolUp.Remoting.Server.Proxy

/// What a call answers with. Every byte the dispatcher writes is written by
/// `Response.write` from one of these.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type internal Response =
    /// An envelope or a value, serialised at its runtime type.
    | Json of status: int * body: obj
    /// A value serialised at a declared type (a companion's status).
    | Typed of status: int * body: obj * declared: Type
    /// A method's serialised result.
    | Bytes of status: int * contentType: string * body: Stream
    /// A memoised response, replayed for a repeated idempotency key.
    | Replay of MemoisedResponse
    /// A streaming call: the writer iterates the source as SSE frames.
    | Stream of label: string * source: StreamSource
    /// The documentation page or schema.
    | Text of contentType: string * text: string
    /// Not this record's route: the next handler may own it.
    | Halt

/// How a stream ended: its outcome and the chunks written before the
/// terminal frame.
[<NoComparison; NoEquality>]
type internal Streamed = { Outcome: MethodOutcome; Chunks: int }

[<RequireQualifiedAccess>]
module internal Response =

    [<Literal>]
    let private JsonContentType = "application/json; charset=utf-8"

    let private writeBytes (http: HttpContext) (contentType: string) (bytes: byte[]) : Task = task {
        http.Response.ContentType <- contentType
        http.Response.ContentLength <- Nullable(int64 bytes.Length)

        if not (HttpMethods.IsHead http.Request.Method) then
            do! http.Response.Body.WriteAsync(bytes, 0, bytes.Length)
    }

    let private writeJson
        (backend: JsonSerializerBackend)
        (logger: (string -> unit) option)
        (http: HttpContext)
        (body: obj)
        =
        task {
            match logger with
            | Some _ ->
                // A composed logger sees the body, so it is materialised once
                // (the cold debug path).
                use buffer = new MemoryStream()
                jsonSerializeWithBackend backend body buffer
                let text = Encoding.UTF8.GetString(buffer.ToArray())
                Diagnostics.outputPhase logger text
                do! writeBytes http JsonContentType (Encoding.UTF8.GetBytes text)
            | None ->
                http.Response.ContentType <- JsonContentType
                // Kestrel refuses a synchronous write to the response body.
                do! jsonSerializeWithBackendAsync backend body http.Response.Body
        }

    let private writeFrame (http: HttpContext) (frame: byte[]) : Task = task {
        do! http.Response.Body.WriteAsync(frame, 0, frame.Length)
        // Flushed per frame, so a client observes each event as it is
        // produced, the terminal one included.
        do! http.Response.Body.FlushAsync()
    }

    /// Write one frame after the stream failed; the client may be gone.
    let private writeFinalError (http: HttpContext) (message: string) : Task = task {
        try
            do! writeFrame http (Streaming.formatError message)
        with _ ->
            ()
    }

    /// Iterate a stream source under the request's abort token, framing each
    /// element as an `event: chunk` carrying `id: <correlation>-<index>`, then
    /// `event: complete` — or `event: error` when the producer faults. The
    /// producer is disposed after the loop, awaited.
    let private writeStream (correlation: string) (http: HttpContext) (source: StreamSource) : Task<Streamed> = task {
        http.Response.StatusCode <- 200
        http.Response.ContentType <- "text/event-stream"
        http.Response.Headers["Cache-Control"] <- StringValues "no-cache"
        http.Response.Headers["X-Accel-Buffering"] <- StringValues "no"

        let opened =
            try
                Ok(source.Produce http.RequestAborted)
            with ex ->
                Error ex

        match opened with
        | Error setup ->
            if not http.Response.HasStarted then
                http.Response.StatusCode <- 500

            do! writeFinalError http setup.Message

            return {
                Outcome = MethodOutcome.Failed setup
                Chunks = 0
            }
        | Ok producer ->
            let mutable chunks = 0

            let! ended = task {
                try
                    let mutable more = true

                    while more do
                        let! moved = producer.MoveNextAsync()

                        if moved then
                            let json = source.Encode producer.Current
                            do! writeFrame http (Streaming.formatChunkWithId (sprintf "%s-%d" correlation chunks) json)
                            chunks <- chunks + 1
                        else
                            more <- false

                    do! writeFrame http (Streaming.formatComplete ())
                    return MethodOutcome.Succeeded
                with ex ->
                    do! writeFinalError http ex.Message
                    return MethodOutcome.Failed ex
            }

            try
                do! producer.DisposeAsync()
            with _ ->
                // The stream's outcome is decided; a producer that faults on
                // disposal changes nothing the client can observe.
                ()

            return { Outcome = ended; Chunks = chunks }
    }

    /// Write `response`; a stream answers how it ended.
    let write
        (backend: JsonSerializerBackend)
        (logger: (string -> unit) option)
        (correlation: string)
        (http: HttpContext)
        (response: Response)
        : Task<Streamed option> =
        task {
            match response with
            | Response.Json(status, body) ->
                http.Response.StatusCode <- status
                do! writeJson backend logger http body
                return None
            | Response.Typed(status, body, declared) ->
                http.Response.StatusCode <- status
                http.Response.ContentType <- JsonContentType

                match backend with
                | SystemTextJson options ->
                    do! System.Text.Json.JsonSerializer.SerializeAsync(http.Response.Body, body, declared, options)

                return None
            | Response.Bytes(status, contentType, body) ->
                http.Response.StatusCode <- status
                http.Response.ContentType <- contentType
                do! body.CopyToAsync http.Response.Body
                return None
            | Response.Replay memoised ->
                http.Response.StatusCode <- memoised.StatusCode
                http.Response.ContentType <- memoised.ContentType
                http.Response.Headers["x-idempotency-replay"] <- StringValues "true"
                do! http.Response.Body.WriteAsync(memoised.Body, 0, memoised.Body.Length)
                return None
            | Response.Stream(_, source) ->
                let! streamed = writeStream correlation http source
                return Some streamed
            | Response.Text(contentType, text) ->
                do! writeBytes http contentType (Encoding.UTF8.GetBytes text)
                return None
            | Response.Halt -> return None
        }