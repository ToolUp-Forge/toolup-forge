module DailyTrade.ClientView

open Feliz
open ToolUp.Platform
open DailyTrade.SharedTypes
open DailyTrade.ClientModel

let private resultView (result: UploadResult) =
    Html.div [
        if result.Accepted then
            Html.p [ prop.text "Delivery accepted." ]

            Html.ul [
                for t in result.Tables do
                    Html.li [ prop.text (sprintf "%s: %d rows" t.TableId t.Rows) ]
            ]

            for n in result.Narratives do
                Html.p [ prop.text n ]
        else
            Html.p [ prop.text "Delivery rejected; nothing was written." ]

            Html.ul [
                for p in result.Problems do
                    Html.li [ prop.text p ]
            ]
    ]

let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let left =
        Html.div [
            prop.children [
                Html.h2 [ prop.text "Daily trading" ]
                Html.p [
                    prop.text
                        "Upload the day's trading extract (CSV: Date, Site, Channel, Orders, Shoppers, Revenue, Units)."
                ]
                Html.input [
                    prop.type'.file
                    prop.accept ".csv"
                    prop.onChange (fun (file: Browser.Types.File) ->
                        let reader = Browser.Dom.FileReader.Create()

                        reader.onload <- fun _ -> dispatch (FileRead(file.name, string reader.result))

                        reader.readAsText file)
                ]
                Html.button [
                    prop.text (if model.Uploading then "Uploading…" else "Upload")
                    prop.disabled (model.Uploading || model.Pending.IsNone)
                    prop.onClick (fun _ -> dispatch Upload)
                ]
            ]
        ]

    let right =
        Html.div [
            prop.children [
                match model.Error, model.LastResult with
                | Some error, _ -> Html.p [ prop.text error ]
                | None, Some result -> resultView result
                | None, None ->
                    Html.p [
                        prop.text
                            "No delivery yet. Each accepted delivery refreshes the trading fact tables and writes the day's grounded report."
                    ]
            ]
        ]

    left, right

/// The erased module. The composition root (Client.fs) fills the shell's
/// data-manager slot with it, so it is the deployment's data manager:
///
///     Slots.DataManager = SlotFill.External(DailyTrade.ClientView.register ())
///
/// It publishes every delivery it ingested through
/// `ClientModule.withProcessedData` — see docs/platform/modules.md, the
/// data-manager slot's contract.
let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Daily trading"
        Icon = Html.span [ prop.text "T" ]
    }
    |> ClientModule.withView view
    |> ClientModule.withProcessedData _.Processed
    |> ClientModule.register