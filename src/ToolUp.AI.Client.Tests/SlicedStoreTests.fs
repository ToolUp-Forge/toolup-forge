// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SlicedStoreTests

// ─── Phase 852 — the sliced model store, pinned on the transpiled code ──
//
// Four things, each where it runs (Fable output, a real React 19 tree in
// jsdom, React's own scheduler):
//
//   852.A  `Program.withReactStore` renders what `withReactSynchronous`
//          renders, from the store, once per task; `useSelector`
//          re-renders a reader only when its slice changes.
//   852.B  the SDK shell under the store re-renders the active module and
//          NO chrome on a module message, and the chrome but NOT the module
//          on a chrome message — while the whole-tree path (every
//          `Client.program` composer) re-renders both, as before.
//   852.C  the AG Grid wrapper's props compare decides as the old
//          serialisation did, without serialising.

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Elmish
open ToolUp.Elmish.React
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI.Client.Tests.RenderScope

[<Import("createRoot", from = "react-dom/client")>]
let private createRoot (container: obj) : obj = jsNative

[<Import("createElement", from = "react")>]
let private createElement (elementType: obj, props: obj) : ReactElement = jsNative

[<Emit("(function () { var a = {}; a.self = a; return a; })()")>]
let private cyclic () : obj = jsNative

[<Emit("new Date($0)")>]
let private date (ms: float) : obj = jsNative

// ─── 852.A — the store binding ─────────────────────────────────────────

type private CounterMsg = | Bump

let private storeBindingTests =
    testList "852.A - the store binding" [

        testCaseDeferred "a store-bound program renders the view from the store, once per task" 300
        <| fun () ->
            let document = installDom "store-a"
            let store = ModelStore.create<int, CounterMsg> ()
            let mutable views = 0
            let mutable dispatch: (CounterMsg -> unit) option = None
            let mutable afterMount = ("", 0)

            Program.mkProgram (fun () -> 0, Cmd.none) (fun Bump model -> model + 1, Cmd.none) (fun model d ->
                views <- views + 1
                dispatch <- Some d
                Html.p [ prop.id "store-a-out"; prop.text (sprintf "count %d" model) ])
            |> Program.withReactStore store "store-a"
            |> Program.run

            JS.setTimeout
                (fun () ->
                    afterMount <- ((document?getElementById "store-a-out")?textContent, views)
                    // Two dispatches in one task: two drains, one publish.
                    dispatch.Value Bump
                    dispatch.Value Bump)
                80
            |> ignore

            fun () ->
                let text, viewsAtMount = afterMount
                Expect.equal text "count 0" "the root read the first published model"
                Expect.equal viewsAtMount 1 "the view ran once to mount"

                Expect.equal
                    ((document?getElementById "store-a-out")?textContent: string)
                    "count 2"
                    "both messages reached the screen"

                Expect.equal views 2 "two drains in one task published once: one more view"
                Expect.isTrue store.Snapshot.IsSome "the store holds the last model"
                Expect.equal store.Snapshot.Value.Model 2 "and it is the model the loop ended on"

        testCaseDeferred
            "a view that throws keeps the last good tree and reports the error, as the push binding did"
            300
        <| fun () ->
            let document = installDom "store-t"
            let reported = ResizeArray<string>()
            // Capture what the binding reports as uncaught.
            document?defaultView?reportError <- (fun (e: exn) -> reported.Add e.Message)
            let store = ModelStore.create<int, CounterMsg> ()
            let mutable dispatch: (CounterMsg -> unit) option = None

            Program.mkProgram (fun () -> 0, Cmd.none) (fun Bump model -> model + 1, Cmd.none) (fun model d ->
                dispatch <- Some d

                if model = 1 then
                    failwith "view broke at 1"

                Html.p [ prop.id "store-t-out"; prop.text (sprintf "count %d" model) ])
            |> Program.withReactStore store "store-t"
            |> Program.run

            JS.setTimeout (fun () -> dispatch.Value Bump) 80 |> ignore

            fun () ->
                Expect.equal
                    ((document?getElementById "store-t-out")?textContent: string)
                    "count 0"
                    "the last good tree is still mounted"

                Expect.equal (List.ofSeq reported) [ "view broke at 1" ] "the error was reported once, as uncaught"

        testCaseDeferred "withReactStoreHydrate adopts the server-rendered DOM and then renders from the store" 300
        <| fun () ->
            let document = installDom "store-h"
            let host = document?getElementById "store-h"
            // What a server would have rendered for `init`'s model.
            host?innerHTML <- "<p id=\"store-h-out\">count 0</p>"
            let serverNode = document?getElementById "store-h-out"
            let store = ModelStore.create<int, CounterMsg> ()
            let mutable dispatch: (CounterMsg -> unit) option = None
            let mutable adopted = false

            Program.mkProgram (fun () -> 0, Cmd.none) (fun Bump model -> model + 1, Cmd.none) (fun model d ->
                dispatch <- Some d
                Html.p [ prop.id "store-h-out"; prop.text (sprintf "count %d" model) ])
            |> Program.withReactStoreHydrate store "store-h"
            |> Program.run

            JS.setTimeout
                (fun () ->
                    adopted <- obj.ReferenceEquals(document?getElementById "store-h-out", serverNode)
                    dispatch.Value Bump)
                80
            |> ignore

            fun () ->
                Expect.isTrue adopted "hydration kept the server's node (a mismatch would have replaced it)"
                let now = document?getElementById "store-h-out"
                Expect.isTrue (obj.ReferenceEquals(now, serverNode)) "and the update patched that same node"
                Expect.equal (now?textContent: string) "count 1" "with the new model"

        testCaseDeferred "useSelector re-renders a reader only when its slice changes" 300
        <| fun () ->
            let document = installDom "store-b"
            let store = ModelStore.create<int * int, unit> ()
            let mutable renders = 0
            let mutable optionRenders = 0

            // Reads the FIRST component only.
            let reader (_: obj) : ReactElement =
                let first = ModelStore.useSelector store fst ModelStore.refEquals
                renders <- renders + 1
                Html.span (string first)

            // A selector that ALLOCATES (a fresh option per call), compared
            // by value: must still read as unchanged, and must not loop.
            let optionReader (_: obj) : ReactElement =
                let first = ModelStore.useSelector store (fun (a, _) -> Some a) (fun x y -> x = y)

                optionRenders <- optionRenders + 1
                Html.span (sprintf "%A" first)

            store.Publish((1, 1), ignore)
            let root = createRoot (document?getElementById "store-b")

            root?render (Html.div [ createElement (box reader, null); createElement (box optionReader, null) ])
            |> ignore

            let marks = ResizeArray<int * int>()

            JS.setTimeout
                (fun () ->
                    marks.Add(renders, optionRenders)
                    store.Publish((1, 2), ignore) // second component only

                    JS.setTimeout
                        (fun () ->
                            marks.Add(renders, optionRenders)
                            store.Publish((2, 2), ignore)) // first component
                        60
                    |> ignore)
                60
            |> ignore

            fun () ->
                Expect.equal marks[0] (1, 1) "each reader rendered once to mount"
                Expect.equal marks[1] (1, 1) "a publish that left the slice alone re-rendered nobody"
                Expect.equal (renders, optionRenders) (2, 2) "a publish that moved the slice re-rendered both readers"
    ]

// ─── 852.B — the sliced shell ──────────────────────────────────────────

let private shellScopeTests =
    let mutable whole: ScopeRun option = None
    let mutable sliced: ScopeRun option = None

    testList "852.B - a module message re-renders the module and no chrome" [

        testCaseDeferred "the whole-tree path (every Client.program composer) re-renders both, as before" (RunMs + 100)
        <| fun () ->
            run false (fun r -> whole <- Some r)

            fun () ->
                let r = whole.Value

                printfn
                    "[852] %s: mount %A -> module msg %A -> chrome msg %A"
                    r.Path
                    r.AfterMount
                    r.AfterModuleMsg
                    r.AfterChromeMsg

                Expect.equal r.ModuleText "count 1" "the module message reached the screen"

                Expect.equal
                    (r.AfterModuleMsg.Chrome - r.AfterMount.Chrome)
                    1
                    "the chrome re-rendered for a module message"

                Expect.equal (r.AfterModuleMsg.Module - r.AfterMount.Module) 1 "and so did the module"

                Expect.equal
                    (r.AfterChromeMsg.Chrome - r.AfterModuleMsg.Chrome)
                    1
                    "the chrome re-rendered for a chrome message"

                Expect.equal
                    (r.AfterChromeMsg.Module - r.AfterModuleMsg.Module)
                    1
                    "and so did the module, whose state had not moved"

        testCaseDeferred "the sliced path (Client.run) re-renders only the slice that moved" (RunMs + 100)
        <| fun () ->
            run true (fun r -> sliced <- Some r)

            fun () ->
                let r = sliced.Value

                printfn
                    "[852] %s: mount %A -> module msg %A -> chrome msg %A"
                    r.Path
                    r.AfterMount
                    r.AfterModuleMsg
                    r.AfterChromeMsg

                Expect.equal r.AfterMount { Chrome = 1; Module = 1 } "one chrome render and one module render to mount"
                Expect.equal r.ModuleText "count 1" "the module message reached the screen"
                Expect.equal (r.AfterModuleMsg.Chrome - r.AfterMount.Chrome) 0 "NO chrome render for a module message"
                Expect.equal (r.AfterModuleMsg.Module - r.AfterMount.Module) 1 "one module render for it"

                Expect.equal
                    (r.AfterChromeMsg.Chrome - r.AfterModuleMsg.Chrome)
                    1
                    "one chrome render for a chrome message"

                Expect.equal
                    (r.AfterChromeMsg.Module - r.AfterModuleMsg.Module)
                    0
                    "NO module render: its state did not move"

        testCase "the chrome's comparison masks ModuleStates and nothing else" (fun () ->
            let m: Client.Model = {
                ActiveModuleId = "m"
                ActivePageRoute = None
                ModuleStates = Map.ofList [ "m", box 1 ]
                AccessibleModules = None
                ShowAllModules = false
                ModuleConfigs = Map.empty
                PlatformConfig = Map.empty
                ResolvedFlags = Map.empty
                SidebarPrefs = SidebarPreferences.UserSidebarPreferences.empty
                ProcessedData = []
                PrefetchedProcessedData = []
                MyTeams = []
                ActiveTeamId = None
                ActiveTeamLoadCompleted = false
                PlatformRole = None
                ActiveTeamRole = None
                CurrentArea = ModuleArea.Product
                ConfigsPrefetch = Prefetch.none
                FlagsPrefetch = Prefetch.none
                ResetCounters = Map.empty
                InitPhase = Client.Ready
                Degradations = []
                CommandPalette = CommandPaletteNav.closed
                LocaleOverride = None
            }

            let moduleOnly = {
                m with
                    ModuleStates = m.ModuleStates |> Map.add "m" (box 2)
            }

            let chromeToo = {
                moduleOnly with
                    ShowAllModules = true
            }

            Expect.isTrue (Client.chromeSliceEqual m moduleOnly) "a ModuleStates-only change is not a chrome change"
            Expect.isFalse (Client.chromeSliceEqual m chromeToo) "any other field is"

            Expect.isFalse
                (Client.chromeSliceEqual m { m with ActivePageRoute = Some "/x" })
                "including the active page route")
    ]

// ─── 852.C — the grid's props compare ──────────────────────────────────

type private Row = { Name: string; Value: int }

let private gridCompareTests =
    let same (a: obj) (b: obj) = Feliz.AgGrid.sameGridProps a b

    testList "852.C - the AG Grid wrapper compares without serialising" [

        testCase "shared references are equal; a fresh array of the same rows is equal" (fun () ->
            let rows = [| { Name = "a"; Value = 1 }; { Name = "b"; Value = 2 } |]
            let props = createObj [ "rowData" ==> rows ]
            Expect.isTrue (same props props) "same object"
            Expect.isTrue (same props (createObj [ "rowData" ==> Array.copy rows ])) "fresh array, same row objects"

            Expect.isTrue
                (same props (createObj [ "rowData" ==> [| { Name = "a"; Value = 1 }; { Name = "b"; Value = 2 } |] ]))
                "fresh rows carrying the same data")

        testCase "a changed row, a changed option or a changed key count is a change" (fun () ->
            let rows = [| { Name = "a"; Value = 1 } |]
            let props = createObj [ "rowData" ==> rows; "pagination" ==> true ]

            Expect.isFalse
                (same props (createObj [ "rowData" ==> [| { Name = "a"; Value = 2 } |]; "pagination" ==> true ]))
                "row value"

            Expect.isFalse (same props (createObj [ "rowData" ==> rows; "pagination" ==> false ])) "option value"
            Expect.isFalse (same props (createObj [ "rowData" ==> rows ])) "a key dropped")

        testCase "functions and undefined keys are invisible, as they were to JSON" (fun () ->
            let a = createObj [ "rowData" ==> [||]; "onCellClicked" ==> (fun () -> 1) ]
            let b = createObj [ "rowData" ==> [||]; "onCellClicked" ==> (fun () -> 2) ]
            let c = createObj [ "rowData" ==> [||]; "extra" ==> None ]
            Expect.isTrue (same a b) "a fresh closure alone is not a change"
            Expect.isTrue (same a (createObj [ "rowData" ==> [||] ])) "a function-valued key is absent"
            Expect.isTrue (same c (createObj [ "rowData" ==> [||] ])) "an undefined-valued key is absent")

        testCase "column definitions rebuilt with the same content are equal" (fun () ->
            let colDefs () = [|
                createObj [
                    "field" ==> "Name"
                    "headerName" ==> "Name"
                    "valueFormatter" ==> (fun () -> "")
                ]
                createObj [ "field" ==> "Value"; "sortable" ==> true ]
            |]

            Expect.isTrue
                (same (createObj [ "columnDefs" ==> colDefs () ]) (createObj [ "columnDefs" ==> colDefs () ]))
                "rebuilt literals"

            Expect.isFalse
                (same
                    (createObj [ "columnDefs" ==> colDefs () ])
                    (createObj [ "columnDefs" ==> [| createObj [ "field" ==> "Name" ] |] ]))
                "a dropped column")

        testCase "F# lists, dates and NaN compare by content; a long list does not recurse down its spine" (fun () ->
            Expect.isTrue (same (box [ 1; 2; 3 ]) (box [ 1; 2; 3 ])) "fresh equal lists"
            Expect.isFalse (same (box [ 1; 2; 3 ]) (box [ 1; 2 ])) "a shorter list"
            Expect.isTrue (same (date 5.0) (date 5.0)) "equal dates"
            Expect.isFalse (same (date 5.0) (date 6.0)) "different dates"
            Expect.isTrue (same (box nan) (box nan)) "NaN, as JSON's null"
            let long = List.init 200_000 id
            Expect.isTrue (same (box long) (box (List.init 200_000 id))) "200k-element lists, compared iteratively")

        testCase "a cycle is a change, never a hang" (fun () ->
            Expect.isFalse
                (same (cyclic ()) (cyclic ()))
                "two distinct cycles: past the depth cap the answer is the safe one")
    ]

let tests =
    testList "Phase 852 - the sliced model store" [ storeBindingTests; shellScopeTests; gridCompareTests ]