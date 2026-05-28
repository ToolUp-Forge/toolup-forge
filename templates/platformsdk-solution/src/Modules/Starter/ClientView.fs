module Starter.ClientView

open Feliz
open ToolUp.Platform
open Starter.ClientModel

[<ReactComponent>]
let private EchoInput (onSubmit: string -> unit) =
    let inputText, setInputText = React.useState ""

    let submit () =
        if inputText <> "" then
            onSubmit inputText
            setInputText ""

    Html.div [
        prop.children [
            Html.input [
                prop.value inputText
                prop.onChange (fun (v: string) -> setInputText v)
                prop.onKeyDown (fun e ->
                    if e.key = "Enter" then
                        submit ())
                prop.placeholder "Type something to echo, then press Enter"
            ]
            Html.button [ prop.text "Echo"; prop.onClick (fun _ -> submit ()) ]
        ]
    ]

let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let left =
        Html.div [
            prop.children [
                Html.h2 [ prop.text "Starter" ]
                EchoInput(fun text -> dispatch (SubmitEcho text))
            ]
        ]

    let right =
        Html.div [
            prop.children [
                match model.LastResponse with
                | Some text -> Html.p [ prop.text text ]
                | None -> Html.p [ prop.text "No echo yet — type in the input on the left." ]
            ]
        ]

    left, right

let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Starter"
        Icon = Html.span [ prop.text "S" ]
    }
    |> ClientModule.withView view
    |> ClientModule.register