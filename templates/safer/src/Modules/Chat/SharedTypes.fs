module Chat.SharedTypes

open System

type Message = {
    Id: Guid
    SenderName: string
    Body: string
    SentAt: DateTimeOffset
}

type SendMessageRequest = { SenderName: string; Body: string }

type ChatError =
    | EmptyBody
    | EmptyName
    | BodyTooLong of maxChars: int

type ChatApi = {
    SendMessage: SendMessageRequest -> Async<Result<Message, ChatError>>
    ListMessages: unit -> Async<Message list>
}