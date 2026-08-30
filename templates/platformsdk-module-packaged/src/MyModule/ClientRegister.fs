module MODULE_NAMESPACE_ROOT.ClientRegister

open Feliz
open ToolUp.Platform
open ToolUp.Platform.DataProp
open MODULE_NAMESPACE_ROOT.ClientModel
open MODULE_NAMESPACE_ROOT.SharedTypes

// ─── View ────────────────────────────────────────────────────────
//
// Single-page module: the view returns `(left, right)` and the shell
// wraps it as a split panel. Multi-page modules chain `withPages`
// instead — see the SDK's module documentation for the full spec
// surface.

let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let left =
        Html.div [
            dataProp.testid "MODULE_ID_TOKEN-module"
            prop.children [
                Html.h2 [ prop.text "MODULE_DISPLAY_NAME" ]
                Html.input [
                    prop.value model.Input
                    prop.placeholder "Type something to echo"
                    prop.onChange (InputChanged >> dispatch)
                ]
                Html.button [ prop.text "Echo"; prop.onClick (fun _ -> dispatch SubmitEcho) ]
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

// ─── Module registration (client half) ───────────────────────────

/// The registration chain, with the icon supplied by the caller.
///
/// The split exists so this repo's own conformance test can bind the
/// REAL chain rather than restate it: the icon is the one field that
/// cannot be built outside a browser (`importDefault` is Fable-only),
/// and none of the five module laws read it. The test passes a null
/// icon; `register` below passes the real one.
let registerWith (icon: ReactElement) : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "MODULE_DISPLAY_NAME"
        Icon = icon
    }
    // `Name` is the DISPLAY name; the id is derived from it unless
    // pinned. Pin it to the shared literal so the server's
    // `ServerModule.create ModuleId` and this registration resolve to
    // the same ComponentId — the id-parity law, satisfied by
    // construction.
    |> ClientModule.withId ModuleId
    |> ClientModule.withView view
    // Declares the gate AND its key set in one call, so the predicate
    // and the descriptor cannot drift apart.
    |> ClientModule.withRequiredDataTypes [ DataTypeId ]
    |> ClientModule.withGroup "MODULE_DISPLAY_NAME"
    |> ClientModule.register

/// The client-tier registration a consumer's client entry point calls.
let register () : ErasedModule = registerWith (Icons.moduleIcon ())