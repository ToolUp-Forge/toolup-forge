// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ModelViewerDemo.Client

open Fuaran.MVU
open Fuaran.MVU.React
open ToolUp.Platform
open Feliz
open ToolUp.Client.ModelViewer

// Worked example (Phase 125): a minimal module-style view rendering
// a .glb inline via the typed <model-viewer> binding — camera
// controls, poster / loading lifecycle, AR passthrough, and typed
// load / error / progress handlers driving Elmish state. Drop any
// glTF-binary file at public/assets/demo.glb (none is vendored —
// no binaries in the repo) and `npm run dev` to see it; the sample's
// primary job is the Fable acceptance gate either way.

type LoadState =
    | Loading of progress: float
    | Ready
    | Failed of reason: string

type Model = { State: LoadState }

type Msg =
    | Progressed of float
    | Loaded
    | Errored of ModelViewerError

let init () : Model * Step<Msg, RemoteEffect<Msg>> list = { State = Loading 0.0 }, []

let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | Progressed fraction -> { model with State = Loading fraction }, []
    | Loaded -> { model with State = Ready }, []
    | Errored error ->
        {
            model with
                State = Failed error.ErrorType
        },
        []

let view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.children [
            Html.h1 [ prop.text "ModelViewerDemo — inline 3D display" ]

            ModelViewer.viewer "/assets/demo.glb" "A demonstration 3D model you can orbit and zoom" [
                modelViewer.cameraControls true
                modelViewer.autoRotate true
                modelViewer.cameraOrbit "45deg 55deg 2.5m"
                modelViewer.exposure 1.0
                modelViewer.environmentImage "neutral"
                modelViewer.loading Loading.Eager
                modelViewer.reveal Reveal.Auto
                modelViewer.ar true
                modelViewer.arModes [ ArMode.WebXr; ArMode.SceneViewer; ArMode.QuickLook ]
                modelViewer.style [ style.width (length.px 480); style.height (length.px 360) ]
                modelViewer.onProgress (Progressed >> dispatch)
                modelViewer.onLoad (fun _ -> dispatch Loaded)
                modelViewer.onError (Errored >> dispatch)
            ]

            Html.p [
                prop.text (
                    match model.State with
                    | Loading fraction -> sprintf "Loading… %.0f%%" (fraction * 100.0)
                    | Ready -> "Model loaded."
                    | Failed reason -> sprintf "Model failed to load (%s)." reason
                )
            ]
        ]
    ]

Live.elements
    "elmish-app"
    Fresh
    (Host.create (fun () -> System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) 0UL ignore)
    (Component.plain init update (fun _ -> Map.empty) (ElementView.ofView view))
    RemoteEffect.interpreter
    ()
|> ignore