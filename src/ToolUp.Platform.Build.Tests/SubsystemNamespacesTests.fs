// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.SubsystemNamespacesTests

open System
open System.IO
open Expecto
open ToolUp.Platform

// ─── Phase 1198 — a namespace names the part ──────────────────────────
//
// Three arms. The PLACEMENT arm pins the rule a file's header follows
// once it is placed in a part: the platform family moves under the
// part's root, a sub-namespace keeps its tail, the remoting library's
// own namespaces stay, and the rule is idempotent. The REWRITE arm runs
// the consumer pass over small texts with a hand-built table: an open
// of a moved path is rewritten, a qualified path too (never inside a
// string), the opens a file needs are derived from the names it
// mentions and placed where the old resolution is kept, a tier file
// never opens a later part, and a second run changes nothing. The
// TABLE arm holds the shipped name table to a fresh scan of the tree,
// so the resource the Codemod reads cannot drift from the sources.

let private ns =
    SubsystemNamespaces.parts |> List.map (fun p -> p.Project, p.Root) |> Map.ofList

let private table =
    SubsystemNamespaces.index [
        {
            Kind = SubsystemNamespaces.EntryKind.Type
            OldNamespace = "ToolUp.Platform"
            Name = "TeamManagement"
            NewNamespace = "ToolUp.Platform.Config"
        }
        {
            Kind = SubsystemNamespaces.EntryKind.Type
            OldNamespace = "ToolUp.Platform"
            Name = "IJobScheduler"
            NewNamespace = "ToolUp.Platform.Jobs"
        }
        {
            Kind = SubsystemNamespaces.EntryKind.Type
            OldNamespace = "ToolUp.Platform"
            Name = "ServerApp"
            NewNamespace = "ToolUp.Platform.Compose"
        }
        {
            Kind = SubsystemNamespaces.EntryKind.Case
            OldNamespace = "ToolUp.Platform"
            Name = "Coalesced"
            NewNamespace = "ToolUp.Platform.Jobs"
        }
        {
            Kind = SubsystemNamespaces.EntryKind.Namespace
            OldNamespace = "ToolUp.Platform.Teams"
            Name = "*"
            NewNamespace = "ToolUp.Platform.Scope.Teams"
        }
        {
            Kind = SubsystemNamespaces.EntryKind.Type
            OldNamespace = "ToolUp.Platform.Teams"
            Name = "TeamInvitationHandler"
            NewNamespace = "ToolUp.Platform.Scope.Teams"
        }
        {
            Kind = SubsystemNamespaces.EntryKind.Split
            OldNamespace = "ToolUp.Platform.Metrics"
            Name = "*"
            NewNamespace = "ToolUp.Platform.Infra.Metrics"
        }
    ]

let private rewrite (text: string) =
    SubsystemNamespaces.rewriteText table (text.Replace("\r\n", "\n"))

let private lines (text: string) = text.Split('\n') |> List.ofArray

let tests =
    testList "Phase 1198 — a namespace names the part" [
        testList "placement" [
            testCase "the platform family moves under the part's root and keeps a sub-namespace's tail"
            <| fun () ->
                let infra = ns["ToolUp.Platform.Server.Infra"]
                Expect.equal (SubsystemNamespaces.placed infra "ToolUp.Platform") "ToolUp.Platform.Infra" "namespace"

                Expect.equal
                    (SubsystemNamespaces.placed infra "ToolUp.Platform.Metrics")
                    "ToolUp.Platform.Infra.Metrics"
                    "sub-namespace"

                Expect.equal
                    (SubsystemNamespaces.placed infra "ToolUp.Platform.EntityStore")
                    "ToolUp.Platform.Infra.EntityStore"
                    "top-level module"

                Expect.equal
                    (SubsystemNamespaces.placed infra "LocalFileStorage")
                    "ToolUp.Platform.Infra.LocalFileStorage"
                    "global module"

            testCase "the remoting library's namespaces stay; its platform-named module takes the Http sub-namespace"
            <| fun () ->
                let remoting = ns["ToolUp.Platform.Server.Remoting"]

                Expect.equal
                    (SubsystemNamespaces.placed remoting "ToolUp.Remoting.Server")
                    "ToolUp.Remoting.Server"
                    "server"

                Expect.equal (SubsystemNamespaces.placed remoting "ToolUp.Remoting.Json") "ToolUp.Remoting.Json" "json"
                Expect.equal (SubsystemNamespaces.placed remoting "TypeShape") "TypeShape" "vendored internal"

                Expect.equal
                    (SubsystemNamespaces.placed remoting "ToolUp.Platform.HttpBodyBinding")
                    "ToolUp.Remoting.Server.Http.HttpBodyBinding"
                    "platform-named module"

            testCase "placement is idempotent and the umbrella's file sources take Files"
            <| fun () ->
                let compose = ns["ToolUp.Platform.Server"]
                let once = SubsystemNamespaces.placed compose "ToolUp.Platform.ServerApp"
                Expect.equal (SubsystemNamespaces.placed compose once) once "second placement is the first"

                let umbrella =
                    SubsystemNamespaces.parts
                    |> List.find (fun p -> p.Project = "ToolUp.Platform.Server")

                Expect.equal
                    (SubsystemNamespaces.rootFor umbrella "Server/Files/FileManagement.fs")
                    SubsystemNamespaces.filesRoot
                    "a file source"

                Expect.equal (SubsystemNamespaces.rootFor umbrella "Server/ServerApp.fs") compose "a composition source"

            testCase "the header and the names a file declares are read from its text"
            <| fun () ->
                let text = [|
                    "namespace ToolUp.Platform"
                    ""
                    "open System"
                    ""
                    "type Widget = { Id: string }"
                    ""
                    "type Verdict ="
                    "    | Accepted"
                    "    | Refused of string"
                    ""
                    "[<AutoOpen>]"
                    "module Helpers ="
                    "    let widgetOf id = { Id = id }"
                    ""
                    "module private Hidden ="
                    "    let x = 1"
                |]

                let h = SubsystemNamespaces.header text
                Expect.equal (h |> Option.map _.Path) (Some "ToolUp.Platform") "header path"

                let declared = SubsystemNamespaces.declarations text h |> Set.ofList

                Expect.equal
                    declared
                    (set [
                        SubsystemNamespaces.EntryKind.Type, "Widget"
                        SubsystemNamespaces.EntryKind.Type, "Verdict"
                        SubsystemNamespaces.EntryKind.Case, "Accepted"
                        SubsystemNamespaces.EntryKind.Case, "Refused"
                        SubsystemNamespaces.EntryKind.Type, "Helpers"
                        SubsystemNamespaces.EntryKind.Member, "widgetOf"
                    ])
                    "types, cases, the AutoOpen module and its members; nothing private, no record field"
        ]

        testList "rewrite" [
            testCase "an open of a moved module or namespace is rewritten; a split namespace's open stays"
            <| fun () ->
                let before =
                    "module Consumer.Server\n\nopen ToolUp.Platform\nopen ToolUp.Platform.TeamManagement\nopen ToolUp.Platform.Teams.TeamInvitationHandler\nopen ToolUp.Platform.Metrics\n\nlet x = 1\n"

                let after, rewrites = rewrite before

                Expect.equal
                    (lines after)
                    [
                        "module Consumer.Server"
                        ""
                        "open ToolUp.Platform"
                        "open ToolUp.Platform.Config.TeamManagement"
                        "open ToolUp.Platform.Scope.Teams.TeamInvitationHandler"
                        "open ToolUp.Platform.Metrics"
                        ""
                        "let x = 1"
                        ""
                    ]
                    "the two moved opens follow their paths"

                Expect.equal
                    (rewrites |> List.map (fun (rule, _, _, _) -> rule) |> List.distinct)
                    [ "1198-open" ]
                    "rule"

            testCase "a qualified path is rewritten outside strings and comments, and a case moves with its union"
            <| fun () ->
                let before =
                    "module Consumer.Server\n\nopen ToolUp.Platform\n\nlet a = ToolUp.Platform.TeamManagement.create ()\nlet b = \"ToolUp.Platform.TeamManagement stays in a string\"\n// ToolUp.Platform.TeamManagement stays in a comment\nlet c = ToolUp.Platform.Coalesced\nlet d = \"ToolUp.Platform.Server.Jobs\"\n"

                let after, _ = rewrite before

                Expect.equal
                    (lines after)
                    [
                        "module Consumer.Server"
                        ""
                        "open ToolUp.Platform"
                        ""
                        "let a = ToolUp.Platform.Config.TeamManagement.create ()"
                        "let b = \"ToolUp.Platform.TeamManagement stays in a string\""
                        "// ToolUp.Platform.TeamManagement stays in a comment"
                        "let c = ToolUp.Platform.Jobs.Coalesced"
                        "let d = \"ToolUp.Platform.Server.Jobs\""
                        ""
                    ]
                    "only the code paths move"

            testCase
                "a consumer's opens are derived from the names it mentions, after its last open, and the run is idempotent"
            <| fun () ->
                let before =
                    "module Consumer.Server\n\nopen System\nopen ToolUp.Platform\n\nlet app = ServerApp.create ()\nlet schedule (s: IJobScheduler) = s\nlet unrelated = Widget\n"

                let after, rewrites = rewrite before

                Expect.equal
                    (lines after)
                    [
                        "module Consumer.Server"
                        ""
                        "open System"
                        "open ToolUp.Platform"
                        "open ToolUp.Platform.Jobs"
                        "open ToolUp.Platform.Compose"
                        ""
                        "let app = ServerApp.create ()"
                        "let schedule (s: IJobScheduler) = s"
                        "let unrelated = Widget"
                        ""
                    ]
                    "Jobs and Compose, in part order, and nothing for a name the table does not hold"

                Expect.equal
                    (rewrites |> List.map (fun (rule, _, _, _) -> rule))
                    [ "1198-derived-open" ]
                    "one insertion"

                let again, moreRewrites = rewrite after
                Expect.equal again after "a second run changes nothing"
                Expect.isEmpty moreRewrites "and reports nothing"

            testCase "a tier file opens ToolUp.Platform first, the earlier parts after it, and never a later part"
            <| fun () ->
                let before =
                    "namespace ToolUp.Platform.Jobs\n\nopen System\n\ntype Runner(scheduler: IJobScheduler, teams: TeamManagement.Team) =\n    member _.App = ServerApp.create ()\n"

                let after, _ = rewrite before

                Expect.equal
                    (lines after)
                    [
                        "namespace ToolUp.Platform.Jobs"
                        ""
                        "open ToolUp.Platform"
                        "open ToolUp.Platform.Config"
                        ""
                        "open System"
                        ""
                        "type Runner(scheduler: IJobScheduler, teams: TeamManagement.Team) ="
                        "    member _.App = ServerApp.create ()"
                        ""
                    ]
                    "Core, then Config (earlier); its own Jobs is implicit; Compose (later) is never opened"

            testCase "a tier file that left a namespace Core still declares opens the Core half"
            <| fun () ->
                let before =
                    "namespace ToolUp.Platform.Infra.Metrics\n\nopen System\n\ntype Sink() = class end\n"

                let after, _ = rewrite before

                Expect.equal
                    (lines after)
                    [
                        "namespace ToolUp.Platform.Infra.Metrics"
                        ""
                        "open ToolUp.Platform"
                        "open ToolUp.Platform.Metrics"
                        ""
                        "open System"
                        ""
                        "type Sink() = class end"
                        ""
                    ]
                    "the split namespace is opened beside Core"

            testCase "a file whose projects do not reach the server tier is left alone"
            <| fun () ->
                let before =
                    "module Client.View\n\nopen ToolUp.Platform\n\nlet view (s: IJobScheduler) = ServerApp.create ()\n"

                let segments, rewrites =
                    SubsystemNamespaces.rewrite
                        table
                        {
                            ReachesServer = false
                            LevelCap = None
                            Allowed = None
                        }
                        (before.Split([| "\n" |], StringSplitOptions.None)
                         |> Array.collect (fun l -> [| l; "\n" |])
                         |> fun a -> Array.sub a 0 (a.Length - 1))

                Expect.equal (String.concat "" segments) before "untouched"
                Expect.isEmpty rewrites "nothing reported"
        ]

        testList "the shipped table" [
            testCase "matches a fresh scan of the tier's sources"
            <| fun () ->
                let root = GateReconcileTests.repoRoot
                let _, entries = SubsystemNamespaces.scan root
                let rendered = SubsystemNamespaces.render entries

                let committed =
                    File.ReadAllText(
                        Path.Combine(root, "src", "ToolUp.Platform.Build", "Build", SubsystemNamespaces.resourceName)
                    )

                Expect.equal
                    (committed.Replace("\r\n", "\n"))
                    rendered
                    "regenerate with `dotnet run --project Build.fsproj -- SubsystemNamespaces --write-table`"

            testCase "round-trips through its text form and is the table the Codemod reads"
            <| fun () ->
                let embedded = SubsystemNamespaces.embedded.Value
                Expect.isNonEmpty embedded "the resource is present"

                Expect.equal
                    (SubsystemNamespaces.parse (SubsystemNamespaces.render embedded))
                    (SubsystemNamespaces.parse (SubsystemNamespaces.render embedded) |> List.distinct)
                    "no duplicate entry"

                Expect.equal
                    (SubsystemNamespaces.render (SubsystemNamespaces.parse (SubsystemNamespaces.render embedded)))
                    (SubsystemNamespaces.render embedded)
                    "render ∘ parse is the identity"

            testCase "no tier file declares the bare platform namespace, and no namespace spans two parts"
            <| fun () ->
                let root = GateReconcileTests.repoRoot
                let moves, _ = SubsystemNamespaces.scan root

                let bare =
                    moves
                    |> List.filter (fun m -> m.NewHeader = "namespace ToolUp.Platform")
                    |> List.map _.Path

                Expect.isEmpty bare "every server file's header names its part"

                let byNamespace =
                    moves
                    |> List.choose (fun m ->
                        match m.NewHeader.Split(' ') with
                        | [| "namespace"; path |] -> Some(path, m.Project)
                        | _ -> None)
                    |> List.groupBy fst
                    |> List.map (fun (path, xs) -> path, xs |> List.map snd |> List.distinct)
                    |> List.filter (fun (_, projects) -> projects.Length > 1)

                Expect.isEmpty byNamespace "a namespace is declared by files of one part"
        ]
    ]