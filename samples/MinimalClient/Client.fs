module MinimalClient.Client

open Fable.Core.JsInterop
open ToolUp.Elmish
open ToolUp.Elmish.React
open Feliz

importSideEffects "./index.css"

type Model = { Count: int }

type Msg =
    | Increment
    | Decrement

let init () : Model * Cmd<Msg> = { Count = 0 }, Cmd.none

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Increment -> { model with Count = model.Count + 1 }, Cmd.none
    | Decrement -> { model with Count = model.Count - 1 }, Cmd.none

let view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.children [
            Html.h1 [ prop.text "MinimalClient — Fable smoke test" ]
            Html.p [ prop.text (sprintf "Count: %d" model.Count) ]
            Html.button [ prop.text "Increment"; prop.onClick (fun _ -> dispatch Increment) ]
            Html.button [ prop.text "Decrement"; prop.onClick (fun _ -> dispatch Decrement) ]
        ]
    ]

Program.mkProgram init update view
|> Program.withReactSynchronous "elmish-app"
|> Program.run