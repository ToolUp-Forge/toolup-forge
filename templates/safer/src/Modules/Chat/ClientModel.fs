module Chat.ClientModel

open System
open Elmish
open ToolUp.Platform
open Chat.SharedTypes

// ─── Model ──────────────────────────────────────────────────────────────
//
// `OptimisticMessage` tracks messages the user has typed but the server
// hasn't yet acknowledged. After `Cmd.OfRemoting.callWithRetry`
// succeeds, the optimistic row is removed (the server's confirmed
// message arrives via the next `ListMessages` poll). After the retry
// policy exhausts, the optimistic row gets a "failed" mark + the
// structured `ChatError` so the view can render a red banner with the
// classified failure.

type OptimisticMessage = {
    LocalId: Guid
    Body: string
    SubmittedAt: DateTimeOffset
}

type ErrorBanner = {
    Reason: ChatError
    Detail: string
    Correlation: Guid
}

type Model = {
    DisplayName: string
    InputDraft: string
    ServerMessages: Message list
    Optimistic: OptimisticMessage list
    LastError: ErrorBanner option
}

type Msg =
    | NameChanged of string
    | DraftChanged of string
    | SendRequested
    | SendSucceeded of localId: Guid * confirmed: Message
    | SendFailed of localId: Guid * reason: ChatError option * detail: string
    | MessagesLoaded of Message list
    | MessagesLoadFailed of exn
    | PollTick
    | DismissError

// ─── ToolUp.Remoting proxy ──────────────────────────────────────────────
//
// Module-level value (Phase 64 convention): one construction at module
// load, then every call reuses the proxy. Identity / CSRF / correlation
// headers are attached at *send* time by the SDK's request-guard
// (`UserSession.withRequestHeaders` is the pinned passthrough
// customiser). Do NOT add per-call construction — see
// `docs/platform/client-remoting-proxies.md` for the full rationale.

let private chatApi: ChatApi =
    Api.makeProxy<ChatApi> (customOptions = UserSession.withRequestHeaders)

// ─── Retry policy for Cmd.OfRemoting.callWithRetry ──────────────────────
//
// 3 attempts, 250 ms initial delay, exponential backoff (×2). On retry
// exhaustion the failure flows through `SendFailed` with the structured
// `ChatError` (when the server returned `Result.Error`) or a plain `exn`
// message (when the transport itself failed). The view renders a red
// banner with the classified reason + correlation id. `ShouldRetry`
// returns `true` for every exception — chat sends are short and idempotent
// from the user's perspective (an optimistic row is already on screen).

let private sendRetryPolicy: Cmd.RetryPolicy = {
    MaxAttempts = 3
    InitialDelayMs = 250
    BackoffMultiplier = 2.0
    ShouldRetry = fun _ -> true
}

let init () : Model * Cmd<Msg> =
    let model = {
        DisplayName = ""
        InputDraft = ""
        ServerMessages = []
        Optimistic = []
        LastError = None
    }

    let initialLoad =
        Cmd.OfRemoting.call chatApi.ListMessages () MessagesLoaded MessagesLoadFailed

    model, initialLoad

let private classifyServerError (result: Result<Message, ChatError>) (localId: Guid) =
    match result with
    | Ok confirmed -> SendSucceeded(localId, confirmed)
    | Error err -> SendFailed(localId, Some err, "Server rejected the message.")

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | NameChanged name -> { model with DisplayName = name }, Cmd.none

    | DraftChanged draft -> { model with InputDraft = draft }, Cmd.none

    | SendRequested ->
        let trimmed = model.InputDraft.Trim()
        let name = model.DisplayName.Trim()

        if trimmed = "" || name = "" then
            model, Cmd.none
        else
            let localId = Guid.NewGuid()

            let optimistic = {
                LocalId = localId
                Body = trimmed
                SubmittedAt = DateTimeOffset.UtcNow
            }

            let request = { SenderName = name; Body = trimmed }

            let sendCmd =
                Cmd.OfRemoting.callWithRetry
                    sendRetryPolicy
                    chatApi.SendMessage
                    request
                    (fun result -> classifyServerError result localId)
                    (fun ex -> SendFailed(localId, None, ex.Message))

            let model' = {
                model with
                    InputDraft = ""
                    Optimistic = optimistic :: model.Optimistic
                    LastError = None
            }

            model', sendCmd

    | SendSucceeded(localId, confirmed) ->
        let model' = {
            model with
                Optimistic = model.Optimistic |> List.filter (fun o -> o.LocalId <> localId)
                ServerMessages = (confirmed :: model.ServerMessages) |> List.sortBy _.SentAt
        }

        model', Cmd.none

    | SendFailed(localId, reason, detail) ->
        let banner = {
            Reason = reason |> Option.defaultValue EmptyBody
            Detail = detail
            Correlation = localId
        }

        let model' = {
            model with
                Optimistic = model.Optimistic |> List.filter (fun o -> o.LocalId <> localId)
                LastError = Some banner
        }

        model', Cmd.none

    | MessagesLoaded fresh ->
        let model' = {
            model with
                ServerMessages = fresh |> List.sortBy _.SentAt
        }

        model', Cmd.none

    | MessagesLoadFailed _ -> model, Cmd.none

    | PollTick ->
        let cmd =
            Cmd.OfRemoting.call chatApi.ListMessages () MessagesLoaded MessagesLoadFailed

        model, cmd

    | DismissError -> { model with LastError = None }, Cmd.none