// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.TreeStylesheetTests

// ─── A tree surface's classes all have a rule in the reference stylesheet ──
//
// A tree-lowered surface is styled by `fuaran-reference.css`, which ships in
// the Fuaran.UI.Renderer package's `content/` and which the client CSS entry
// imports. The renderer emits `fuaran-*` class hooks and the stylesheet hangs
// its rules on them, so a renderer version that renames or adds a class the
// stylesheet does not know draws that node unstyled — visible only in a
// browser, since jsdom applies no layout. This pack renders a surface built
// from the authoring helpers the SDK's tree surfaces use, collects every
// `fuaran-*` class in its markup, and requires a selector for each in the
// stylesheet of the renderer this project restored (read through
// `obj/project.assets.json`, the record the client CSS entry's Vite plugin
// resolves the same file from). So a bump of `FuaranUIVersion` that breaks
// the class contract goes red here.

open Fable.Core
open Fable.Core.JsInterop
open Fuaran.UI
open Fuaran.UI.Types
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

[<Import("readFileSync", from = "node:fs")>]
let private readFileSync (path: obj) (encoding: string) : string = jsNative

[<Import("existsSync", from = "node:fs")>]
let private existsSync (path: string) : bool = jsNative

[<Import("join", from = "node:path")>]
let private pathJoin (a: string) (b: string) : string = jsNative

[<Import("renderToString", from = "react-dom/server")>]
let private renderToString (element: Feliz.ReactElement) : string = jsNative

/// Resolved against this module (`output/…`), not the process cwd.
[<Emit("new URL($0, import.meta.url)")>]
let private beside (relative: string) : obj = jsNative

[<Emit("Object.keys($0)")>]
let private keys (o: obj) : string array = jsNative

[<Emit("Array.from($0.matchAll(new RegExp($1, 'g')), m => m[1])")>]
let private captures (text: string) (pattern: string) : string array = jsNative

[<Emit("new RegExp($0).test($1)")>]
let private matches (pattern: string) (text: string) : bool = jsNative

[<Literal>]
let private RendererPackage = "Fuaran.UI.Renderer/"

/// The restored renderer package's `content/fuaran-reference.css`, and the
/// version restore chose.
let private referenceStylesheet () : string * string =
    let assets =
        JS.JSON.parse (readFileSync (beside "../obj/project.assets.json") "utf8")

    let library =
        keys assets?libraries |> Array.find (fun key -> key.StartsWith RendererPackage)

    let relative: string = assets?libraries?(library)?path

    let file =
        keys assets?packageFolders
        |> Array.map (fun folder -> pathJoin (pathJoin folder relative) "content/fuaran-reference.css")
        |> Array.find existsSync

    library.Substring RendererPackage.Length, readFileSync file "utf8"

type private Msg = | Go

let private literal = TreeLowering.literal

/// A surface over the vocabulary the SDK's tree surfaces are authored in: the
/// `TreeLowering` helpers (card, row, column, heading, text, button, banner)
/// and the nodes the tree pages build beside them (callouts in every tone,
/// badges, a plain table and a table with toned pill cells, a select, facts).
let private surface: Node<Msg> =
    let tones = [
        ToneVariant.Default
        ToneVariant.Subdued
        ToneVariant.Brand
        ToneVariant.Success
        ToneVariant.Warning
        ToneVariant.Critical
        ToneVariant.Info
    ]

    TreeLowering.column "surface" [
        TreeLowering.card "settings" "Settings" [
            TreeLowering.heading "settings-heading" 2 "Members"
            TreeLowering.text "settings-note" "Running text with *no* markup."
            TreeLowering.row "actions" [
                TreeLowering.button "primary" ButtonVariant.Primary "Save" false Go
                TreeLowering.button "secondary" ButtonVariant.Secondary "Refresh" false Go
                TreeLowering.button "tertiary" ButtonVariant.Tertiary "Show all" false Go
                TreeLowering.button "destructive" ButtonVariant.Destructive "Remove" false Go
                TreeLowering.button "disabled" ButtonVariant.Primary "Held" true Go
            ]
            TreeLowering.banner "banner" ToneVariant.Success "Saved." "Dismiss" Go
        ]
        yield!
            tones
            |> List.mapi (fun i tone ->
                Fuaran.callout $"callout-{i}" {
                    Defaults.callout with
                        Tone = tone
                        Heading = Some(literal "Heading")
                        Body = literal "Body"
                })
        TreeLowering.row "badges" [
            for i, variant in
                List.indexed [
                    BadgeVariant.Neutral
                    BadgeVariant.Brand
                    BadgeVariant.Success
                    BadgeVariant.Warning
                    BadgeVariant.Critical
                    BadgeVariant.Info
                ] do
                Fuaran.badge $"badge-{i}" {
                    Label = literal "Badge"
                    Variant = variant
                }
        ]
        Fuaran.table "plain-table" {
            Defaults.table with
                Headers = [ literal "Metric"; literal "Shape" ]
                Rows = [ [ literal "revenue"; literal "scalar" ] ]
        }
        Fuaran.tableWithColumns
            "toned-table"
            [
                TableColumn.text "Table"
                TableColumn.tonedPill "Freshness" (Map [ "fresh", ToneVariant.Success ]) ToneVariant.Default
            ]
            {
                Defaults.table with
                    Headers = [ literal "Table"; literal "Freshness" ]
                    Rows = [ [ literal "sales"; literal "fresh" ]; [ literal "stock"; literal "stale" ] ]
            }
        Fuaran.select "choice" {
            Defaults.select with
                Label = literal "Table"
                Source = Binding.Static(Some [ { Label = "sales"; Value = "sales" } ])
                Value = Some(Binding.Static(Some "sales"))
                Placeholder = Some(literal "Choose a table")
        }
        TreeLowering.row "facts" [ Fuaran.fact "subjects" "Subjects" "12"; Fuaran.fact "facts" "Facts" "340" ]
    ]

/// The tree drawn through the renderer chunk's component, as static markup:
/// the class hooks are in the markup whether or not a stylesheet applies.
let private markup () : string =
    let props: TreeLoweringRuntime.TreeProps = {|
        Host = HostBindingSources.empty
        Dispatch = ignore
        Node = unbox (box surface)
        IsIsland = fun _ -> false
        RenderIsland = fun _ -> Feliz.Html.none
    |}

    renderToString (TreeLoweringRuntime.Tree props)

/// Every `fuaran-*` class in `html`, once each, sorted.
let private fuaranClasses (html: string) : string list =
    captures html "class=\"([^\"]*)\""
    |> Array.collect (fun value -> value.Split ' ')
    |> Array.filter (fun c -> c.StartsWith "fuaran-")
    |> Array.distinct
    |> Array.sort
    |> List.ofArray

/// Whether `css` has a selector naming `.className` (not as the prefix of a
/// longer class name). A class hook is letters, digits, `-` and `_`, so it
/// needs no escaping inside the pattern; anything else is reported unstyled.
let private hasRule (css: string) (className: string) : bool =
    matches "^[A-Za-z0-9_-]+$" className
    && matches ("\\." + className + "(?![A-Za-z0-9_-])") css

/// The node-kind marker every rendered element carries beside its component
/// class (`fuaran-kind-button` beside `fuaran-button`): a hook for a host to
/// target, which the reference stylesheet styles through the component class.
[<Literal>]
let private KindMarker = "fuaran-kind-"

/// The classes the renderer emits that the reference stylesheet deliberately
/// carries no rule for, each named. The default value of each semantic-style
/// axis is the base rule's look, so only the other values have rules; the two
/// body wrappers are structure the card and callout rules lay out from their
/// parent. A name here that the stylesheet gains a rule for, or that the
/// surface stops emitting, fails the second case, so the list cannot go stale.
let private hookOnly = [
    "fuaran-tone-default"
    "fuaran-weight-standard"
    "fuaran-emphasis-normal"
    "fuaran-callout-body"
    "fuaran-card-body"
]

let tests =
    testList "the tree stylesheet covers a tree surface's classes" [

        testCase "every fuaran-* class a tree surface emits has a rule in the restored reference stylesheet" (fun () ->
            let version, css = referenceStylesheet ()
            let classes = fuaranClasses (markup ())

            Expect.isTrue (List.length classes >= 20) $"the surface emits the renderer's class hooks: {classes}"

            let unstyled =
                classes
                |> List.filter (fun c -> not (c.StartsWith KindMarker) && not (List.contains c hookOnly))
                |> List.filter (hasRule css >> not)

            Expect.isEmpty
                unstyled
                $"Fuaran.UI.Renderer {version}'s fuaran-reference.css has no rule for these classes the renderer emits: {unstyled}")

        testCase "each hook-only class is emitted and still has no rule" (fun () ->
            let version, css = referenceStylesheet ()
            let classes = fuaranClasses (markup ())

            for c in hookOnly do
                Expect.isTrue (List.contains c classes) $"{c} is still emitted by the surface"
                Expect.isFalse (hasRule css c) $"Fuaran.UI.Renderer {version} now styles {c}: drop it from hookOnly"

            Expect.isTrue
                (classes |> List.exists (fun c -> c.StartsWith KindMarker))
                "the renderer still marks each node with its kind")

        testCase "the check reads a class the stylesheet does not carry as unstyled" (fun () ->
            let _, css = referenceStylesheet ()
            Expect.isTrue (hasRule css "fuaran-button") "a real hook is found"
            Expect.isFalse (hasRule css "fuaran-butto") "the prefix of a hook is not read as the hook"
            Expect.isFalse (hasRule css "fuaran-no-such-hook") "an unknown hook is unstyled")
    ]