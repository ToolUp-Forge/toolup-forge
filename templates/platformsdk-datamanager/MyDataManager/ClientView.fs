module MyDataManager.ClientView

open Feliz
open ToolUp.Platform
open MyDataManager.ClientModel

let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let left =
        Html.div [
            prop.children [
                Html.h2 [ prop.text "MyDataManager" ]
                Html.input [
                    prop.type'.file
                    prop.onChange (fun (ev: Browser.Types.Event) ->
                        let target = ev.target :?> Browser.Types.HTMLInputElement

                        if target.files.length > 0 then
                            dispatch (FileSelected target.files.[0].name))
                ]
                Html.button [ prop.text "Ingest"; prop.onClick (fun _ -> dispatch Ingest) ]
            ]
        ]

    let right =
        Html.div [
            prop.children [
                match model.LastIngest with
                | Some result -> Html.p [ prop.text (sprintf "Dataset %s — %d rows" result.DatasetId result.RowCount) ]
                | None -> Html.p [ prop.text "No ingest yet — pick a file and click Ingest." ]
            ]
        ]

    left, right

/// Returns the erased module. To make it the deployment's data
/// manager, fill the data-manager shell slot with it in the app's
/// Client.fs composition root:
///
///     Slots.DataManager = SlotFill.External(MyDataManager.ClientView.register ())
///
/// The slot is a `ClientConfig` setting, not a per-module one — the
/// shell has one data manager, so the choice belongs to the
/// composition root rather than to this module. The module takes the
/// slot's position and becomes the shell's data source: once it has
/// mounted, data modules see only what it provides. This module
/// declares `ClientModule.withProcessedData` below, publishing every
/// entry `Model.Processed` holds — see docs/platform/modules.md,
/// "Replacing a built-in — shell slots", the data-manager slot's
/// contract, for what a rewritten replacement must keep meeting.
let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "MyDataManager"
        Icon = Html.span [ prop.text "D" ]
    }
    |> ClientModule.withView view
    |> ClientModule.withProcessedData _.Processed
    |> ClientModule.register