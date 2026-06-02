module Chat.Server

open System
open System.Collections.Concurrent
open Chat.SharedTypes

// ─── In-memory bounded ring buffer ──────────────────────────────────────
//
// 50-message cap, FIFO eviction. No persistence — process restart wipes.
// For production-grade persistence + multi-instance broadcast wire an
// `IEntityStore` (Phase 19) over `IBlobStorage` and an `INotificationChannel`
// for SSE fan-out. SAFER stays single-instance + in-memory by design;
// see README "When to use platformsdk-solution instead".

let private bufferSize = 50
let private maxBodyChars = 1_000
let private buffer = ConcurrentQueue<Message>()

let private trimToSize () =
    while buffer.Count > bufferSize do
        buffer.TryDequeue() |> ignore

let private sendMessage (request: SendMessageRequest) : Async<Result<Message, ChatError>> = async {
    if String.IsNullOrWhiteSpace request.Body then
        return Error EmptyBody
    elif String.IsNullOrWhiteSpace request.SenderName then
        return Error EmptyName
    elif request.Body.Length > maxBodyChars then
        return Error(BodyTooLong maxBodyChars)
    else
        let msg = {
            Id = Guid.NewGuid()
            SenderName = request.SenderName.Trim()
            Body = request.Body.Trim()
            SentAt = DateTimeOffset.UtcNow
        }

        buffer.Enqueue msg
        trimToSize ()
        return Ok msg
}

let private listMessages () : Async<Message list> = async { return buffer |> Seq.toList |> List.sortBy _.SentAt }

let routes: ChatApi = {
    SendMessage = sendMessage
    ListMessages = listMessages
}