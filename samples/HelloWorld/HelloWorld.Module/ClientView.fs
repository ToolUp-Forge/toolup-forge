module HelloWorld.Module.ClientView

open Feliz
open ToolUp.Platform
open ToolUp.Platform.SvgProp
open ToolUp.Platform.DataProp
open HelloWorld.Module.ClientModel

// ─── View ────────────────────────────────────────────────────────
//
// Single-page module: the view returns a tuple `(left, right)` and
// the shell wraps it as `SplitPanel(left, right)`. Multi-page
// modules call `withPages` instead — see `docs/platform/modules.md`
// for the multi-page pattern and the full module-spec surface.

let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
#if DEBUG
    // Phase 12c verification — throwing here happens DURING render, which
    // is exactly the lifecycle React error boundaries catch. Throwing in
    // an `onClick` handler instead would NOT trigger the boundary (event
    // handlers are outside the render lifecycle); the model-flag pattern
    // routes the user click through dispatch -> update -> re-render so
    // the throw lands where the boundary can catch it.
    if model.ShouldThrow then
        failwith "Phase 12c debug throw — boundary should catch this"
#endif

    let left =
        Html.div [
            // Inline `dataProp.testid` anchors the `dataProp.*` helper
            // convention (see docs/platform/dom-props.md). React preserves
            // `data-*` verbatim today via a hardcoded exception; using
            // the helper insulates against a future React tightening and
            // documents the intent at the call site.
            dataProp.testid "hello-world-module"
            prop.children [
                // Inline divider SVG anchoring the `svgProp.*` helper convention
                // (see docs/platform/dom-props.md). `strokeWidth` and
                // `strokeLinecap` are kebab-case in the SVG spec; React
                // requires their camelCase forms — the helpers wrap the
                // correct names so consumers can't trip the silent-drop
                // footgun from `prop.custom`.
                Html.svg [
                    prop.custom ("width", 80)
                    prop.custom ("height", 8)
                    prop.custom ("viewBox", "0 0 80 8")
                    prop.children [
                        Html.line [
                            prop.custom ("x1", 0)
                            prop.custom ("y1", 4)
                            prop.custom ("x2", 80)
                            prop.custom ("y2", 4)
                            prop.stroke "currentColor"
                            svgProp.strokeWidth 2.0
                            svgProp.strokeLinecap "round"
                        ]
                    ]
                ]
                Html.h2 [ prop.text "Hello World" ]
                Html.input [
                    prop.value model.Input
                    prop.onChange (fun (v: string) -> dispatch (SubmitEcho v))
                    prop.placeholder "Type something to echo"
                ]
#if DEBUG
                Html.button [
                    prop.className "mt-3 px-3 py-1 rounded bg-red-600 text-white text-sm hover:bg-red-700"
                    prop.text "Throw (Phase 12c test)"
                    prop.onClick (fun _ -> dispatch TriggerThrow)
                ]
#endif
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

// ─── Module registration ─────────────────────────────────────────
//
// The minimum viable module registration. `ClientModule.create` takes
// the four required spec fields (Init, Update, Name, Icon); `withView`
// supplies the single-page view function; `register` erases the
// typed module into an `ErasedModule` for the heterogeneous module
// list. Add `withDataTypes`, `withConfig`, `withNeedsData`, etc. for
// richer modules — see `docs/platform/modules.md` for the full
// module-spec surface.

let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Hello World"
        Icon = HelloWorld.Module.Icons.chart
    }
    |> ClientModule.withView view
    |> ClientModule.withAvailability DebugOnly
    |> ClientModule.withGroup "Debug"
    |> ClientModule.register