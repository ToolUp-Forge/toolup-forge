module Chat.ClientView

open System
open Fable.Core
open Feliz
open ToolUp.Platform
open Chat.SharedTypes
open Chat.ClientModel

// ─── 2-second polling tick ──────────────────────────────────────────────
//
// v1 design choice: polling, not SSE. The SDK ships an SSE-based
// `NotificationClient` with per-event-kind dispatch, reconnection, and
// a typed `subscribe` API — the production way to wire live updates.
// For a minimal starter that demonstrates the consumer-facing primitives
// without inventing a server-side broadcast story, polling is honest
// and small. See README "How to extend SAFER → SSE upgrade" for the
// migration path.

[<Emit("setInterval($0, $1)")>]
let private setInterval (handler: unit -> unit) (ms: int) : int = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval (id: int) : unit = jsNative

let private pollingTicker (dispatch: Msg -> unit) : unit =
    let id = setInterval (fun () -> dispatch PollTick) 2_000
    // The Elmish runtime owns teardown via its EffectHandle lifecycle when
    // running inside the SDK shell. For a minimal starter we let the
    // interval live for the page's lifetime — page navigation / refresh
    // clears it; production deployments using `EffectHandle.programLifetime`
    // get explicit dispose. See README "Walking tour" → "What the SDK
    // shell handles on your behalf".
    ignore id

[<ReactComponent>]
let private NameInput (currentName: string) (onChange: string -> unit) =
    let value, setValue = React.useState currentName

    Html.div [
        Html.label [ prop.text "Your name:"; prop.htmlFor "chat-name" ]
        Html.input [
            prop.id "chat-name"
            prop.value value
            prop.placeholder "Anonymous"
            prop.onChange (fun (v: string) ->
                setValue v
                onChange v)
        ]
    ]

[<ReactComponent>]
let private MessageInput (onSubmit: string -> unit) =
    let draft, setDraft = React.useState ""

    let submit () =
        let trimmed = draft.Trim()

        if trimmed <> "" then
            onSubmit trimmed
            setDraft ""

    Html.div [
        Html.input [
            prop.value draft
            prop.placeholder "Type a message, press Enter to send."
            prop.onChange (fun (v: string) -> setDraft v)
            prop.onKeyDown (fun e ->
                if e.key = "Enter" then
                    submit ())
        ]
        Html.button [ prop.text "Send"; prop.onClick (fun _ -> submit ()) ]
    ]

let private formatTime (t: DateTimeOffset) =
    let local = t.ToLocalTime()
    local.ToString("HH:mm:ss")

let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    // Boot-time effect: start the polling ticker once, on first render.
    // `React.useEffectOnce` is the canonical "mount-only" hook.
    React.useEffectOnce (fun () -> pollingTicker dispatch)

    let errorBanner =
        match model.LastError with
        | None -> Html.none
        | Some banner ->
            let reasonText =
                match banner.Reason with
                | EmptyBody -> "Message body was empty."
                | EmptyName -> "You haven't set a display name."
                | BodyTooLong max -> sprintf "Message exceeded the %d-character limit." max

            Html.div [
                prop.children [
                    Html.strong [ prop.text "Send failed: " ]
                    Html.span [ prop.text reasonText ]
                    Html.br []
                    Html.small [
                        prop.text (sprintf "Detail: %s · Correlation: %O" banner.Detail banner.Correlation)
                    ]
                    Html.button [ prop.text "Dismiss"; prop.onClick (fun _ -> dispatch DismissError) ]
                ]
            ]

    let renderMessage (m: Message) =
        Html.div [
            prop.key (string m.Id)
            prop.children [
                Html.strong [ prop.text m.SenderName ]
                Html.span [ prop.text (sprintf "  [%s]  " (formatTime m.SentAt)) ]
                Html.span [ prop.text m.Body ]
            ]
        ]

    let renderOptimistic (o: OptimisticMessage) =
        Html.div [
            prop.key (string o.LocalId)
            prop.children [ Html.em [ prop.text (sprintf "(sending…) %s" o.Body) ] ]
        ]

    let left =
        Html.div [
            prop.children [
                Html.h2 [ prop.text "Tiny Chat" ]
                NameInput model.DisplayName (fun n -> dispatch (NameChanged n))
                MessageInput(fun draft ->
                    dispatch (DraftChanged draft)
                    dispatch SendRequested)
            ]
        ]

    let right =
        Html.div [
            prop.children [
                errorBanner
                Html.div [
                    prop.children [
                        yield! model.ServerMessages |> List.map renderMessage
                        yield! model.Optimistic |> List.map renderOptimistic
                    ]
                ]
            ]
        ]

    left, right

let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Tiny Chat"
        Icon = Html.span [ prop.text "💬" ]
    }
    |> ClientModule.withView view
    |> ClientModule.register