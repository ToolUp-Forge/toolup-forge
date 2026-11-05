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
// of a moved path is rewritten, a qualified path too, the opens a file
// needs are derived from the names its code uses (never a name inside a
// string or a comment) and placed after its header whatever the header's
// spelling, where the old resolution is kept, inside a nested module that has
// opens of its own, never from a case name the tree or another open already
// answers, a tier file never opens a later part, a dead open is reported and
// not deleted, and a second run changes nothing. The
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

/// The hand-built table with what an open of `ToolUp.Platform` reaches.
let private tableWithPlatform = {
    table with
        Reaches = table.Reaches.Add("ToolUp.Platform", set [ "ServerConfig"; "Ok"; "String" ])
}

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
                "a consumer's opens are derived from the names it mentions, after its open of ToolUp.Platform, and the run is idempotent"
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

            testCase "a name that appears only in a string or a comment derives no open"
            <| fun () ->
                let cases = [
                    "a regular string", "let s = \"IJobScheduler and TeamManagement.create\""
                    "an escaped quote", "let s = \"\\\"IJobScheduler\\\"\""
                    "a verbatim string", "let s = @\"a \"\"quoted\"\" IJobScheduler\""
                    "a triple-quoted string on one line", "let s = \"\"\"a \"quoted\" IJobScheduler\"\"\""
                    "a triple-quoted string across lines",
                    "let css = \"\"\"\n.IJobScheduler { color: red; }\nServerApp\n\"\"\""
                    "a string across lines", "let s = \"first\nIJobScheduler\""
                    "an interpolated string's text", "let s = $\"{{IJobScheduler}} {1 + 1}\""
                    "a line comment", "let x = 1 // IJobScheduler"
                    "a block comment across lines", "let x = 1 (* first\nIJobScheduler (* nested *)\nServerApp *)"
                    "a string in a comment", "(* \"*)\" IJobScheduler *)\nlet x = 1"
                ]

                for name, body in cases do
                    let before = sprintf "module Consumer.Styles\n\nopen System\n\n%s\n" body
                    let after, rewrites = rewrite before
                    Expect.equal after before name
                    Expect.isEmpty rewrites name

            testCase "a name used in code still derives its open beside a literal that could confuse the lexer"
            <| fun () ->
                let cases = [
                    "after a quote character", "let q = '\"'\nlet s (x: IJobScheduler) = x"
                    "after an escaped quote character", "let q = '\\\"'\nlet s (x: IJobScheduler) = x"
                    "after a type variable", "let id' (x: 'a) = x\nlet s (x: IJobScheduler) = x"
                    "after a string", "let q = \"text\"\nlet s (x: IJobScheduler) = x"
                    "after the multiplication operator", "let m = (*) 2\nlet s (x: IJobScheduler) = x"
                    "in an interpolated string's hole", "let s (x: obj) = $\"{x :?> IJobScheduler}\""
                    "in a triple-quoted interpolated string's hole",
                    "let s (x: obj) = $\"\"\"a {x :?> IJobScheduler} b\"\"\""
                ]

                for name, body in cases do
                    let before = sprintf "module Consumer.Server\n\nopen System\n\n%s\n" body
                    let after, _ = rewrite before

                    Expect.equal
                        after
                        (sprintf "module Consumer.Server\n\nopen ToolUp.Platform.Jobs\nopen System\n\n%s\n" body)
                        name

            testCase "a qualified path or an open inside a string spanning lines is not rewritten"
            <| fun () ->
                let before =
                    "module Consumer.Emit\n\nlet template = \"\"\"\nopen ToolUp.Platform.TeamManagement\nlet a = ToolUp.Platform.TeamManagement.create ()\n\"\"\"\n"

                let after, rewrites = rewrite before
                Expect.equal after before "the literal is data"
                Expect.isEmpty rewrites "nothing reported"

            testCase "an open with a trailing comment is read and rewritten, and keeps the comment"
            <| fun () ->
                let after, _ =
                    rewrite "module Consumer.Server\n\nopen ToolUp.Platform.TeamManagement // teams\n\nlet x = 1\n"

                Expect.equal
                    (lines after)
                    [
                        "module Consumer.Server"
                        ""
                        "open ToolUp.Platform.Config.TeamManagement // teams"
                        ""
                        "let x = 1"
                        ""
                    ]
                    "the path moves and the comment stays"

            testCase "derived opens go after the top-level header whatever its spelling"
            <| fun () ->
                let headers = [
                    "a plain module", [ "module Consumer" ]
                    "a dotted module", [ "module Consumer.Server" ]
                    "a namespace", [ "namespace Consumer.Server" ]
                    "a recursive module", [ "module rec Consumer" ]
                    "a double-backticked module", [ "module ``Some name``" ]
                    "a dotted double-backticked module", [ "module Consumer.``Some name``" ]
                    "a module with a trailing comment", [ "module ``Some name`` // the header" ]
                    "a module under a comment and an attribute",
                    [ "// module Commented"; "[<AutoOpen>]"; "module ``Some name``" ]
                    "a module with its attribute on one line", [ "[<AutoOpen>] module ``Some name``" ]
                ]

                for name, header in headers do
                    let body = "let schedule (s: IJobScheduler) = s"
                    let before = String.Join("\n", header @ [ ""; body; "" ])
                    let after, _ = rewrite before

                    Expect.equal (lines after) (header @ [ ""; "open ToolUp.Platform.Jobs"; ""; body; "" ]) name

                    Expect.equal (fst (rewrite after)) after (name + ": a second run changes nothing")

            testCase "a header-shaped line in a comment is not the header"
            <| fun () ->
                let header =
                    SubsystemNamespaces.header [| "(*"; "module InAComment"; "*)"; "module ``Some name``" |]

                Expect.equal (header |> Option.map _.Path) (Some "``Some name``") "the code's header"
                Expect.equal (header |> Option.map _.Line) (Some 3) "and its line"

            testCase "a consumer's part opens go straight after its open of ToolUp.Platform, above its later opens"
            <| fun () ->
                let before =
                    "module Consumer.Server\n\nopen System\nopen ToolUp.Platform\nopen Consumer.Domain\n\nlet schedule (s: IJobScheduler) = s\n"

                let after, _ = rewrite before

                Expect.equal
                    (lines after)
                    [
                        "module Consumer.Server"
                        ""
                        "open System"
                        "open ToolUp.Platform"
                        "open ToolUp.Platform.Jobs"
                        "open Consumer.Domain"
                        ""
                        "let schedule (s: IJobScheduler) = s"
                        ""
                    ]
                    "the consumer's own module still shadows the part"

                Expect.equal (rewrite after |> fst) after "a second run changes nothing"

            testCase "a consumer with no open of ToolUp.Platform takes its part opens above its own"
            <| fun () ->
                let before =
                    "module Consumer.Server\n\nopen Consumer.Domain\n\nlet schedule (s: IJobScheduler) = s\n"

                let after, _ = rewrite before

                Expect.equal
                    (lines after)
                    [
                        "module Consumer.Server"
                        ""
                        "open ToolUp.Platform.Jobs"
                        "open Consumer.Domain"
                        ""
                        "let schedule (s: IJobScheduler) = s"
                        ""
                    ]
                    "above, not below"

                Expect.equal (rewrite after |> fst) after "a second run changes nothing"

            testCase "a nested module's dead open of ToolUp.Platform is replaced inside the module"
            <| fun () ->
                let before =
                    "namespace Consumer\n\nmodule Briefing =\n\n    open ToolUp.Platform\n\n    let schedule (s: IJobScheduler) = s\n"

                let after, _ = SubsystemNamespaces.rewriteText tableWithPlatform before

                Expect.equal
                    (lines after)
                    [
                        "namespace Consumer"
                        ""
                        "module Briefing ="
                        ""
                        "    open ToolUp.Platform.Jobs"
                        ""
                        "    let schedule (s: IJobScheduler) = s"
                        ""
                    ]
                    "in the module, in the open's place"

                Expect.equal
                    (SubsystemNamespaces.rewriteText tableWithPlatform after |> fst)
                    after
                    "a second run changes nothing"

            testCase "a nested module still reaching ToolUp.Platform keeps that open and takes the part beside it"
            <| fun () ->
                let before =
                    "namespace Consumer\n\nmodule Briefing =\n    open ToolUp.Platform\n\n    let schedule (s: IJobScheduler) (c: ServerConfig) = s\n"

                let after, _ = SubsystemNamespaces.rewriteText tableWithPlatform before

                Expect.equal
                    (lines after)
                    [
                        "namespace Consumer"
                        ""
                        "module Briefing ="
                        "    open ToolUp.Platform"
                        "    open ToolUp.Platform.Jobs"
                        ""
                        "    let schedule (s: IJobScheduler) (c: ServerConfig) = s"
                        ""
                    ]
                    "both, indented"

            testCase "a nested module's opens cover only its body, and a module with none takes the namespace's"
            <| fun () ->
                let before =
                    "namespace Consumer\n\nmodule Own =\n    open Consumer.Domain\n\n    let a (s: IJobScheduler) = s\n\nmodule Bare =\n    let b = ServerApp.create ()\n"

                let after, _ = rewrite before

                Expect.equal
                    (lines after)
                    [
                        "namespace Consumer"
                        ""
                        "open ToolUp.Platform.Compose"
                        ""
                        "module Own ="
                        "    open ToolUp.Platform.Jobs"
                        "    open Consumer.Domain"
                        ""
                        "    let a (s: IJobScheduler) = s"
                        ""
                        "module Bare ="
                        "    let b = ServerApp.create ()"
                        ""
                    ]
                    "each open where it is used"

                Expect.equal (rewrite after |> fst) after "a second run changes nothing"

            testCase "a case name the file declares for itself derives nothing, whatever the union's spelling"
            <| fun () ->
                for declaration in
                    [
                        "type Mode =\n    | Coalesced\n    | Other"
                        "type Mode = Coalesced | Other"
                        "type Mode = Other | Coalesced of int"
                        "type Coalesced = { A: int }"
                        "type Row = {\n    Coalesced: bool\n}"
                    ] do
                    let before =
                        sprintf "module Consumer.Server\n\n%s\n\nlet x = Coalesced\n" declaration

                    let after, rewrites = rewrite before
                    Expect.equal after before (sprintf "untouched under %s" declaration)
                    Expect.isEmpty rewrites "nothing derived"

            testCase "a case name the tree declares derives nothing; a type of the same table still does"
            <| fun () ->
                let before =
                    "module Consumer.Server\n\nlet x = Coalesced\nlet schedule (s: IJobScheduler) = s\n"

                let treeDeclares = {
                    SubsystemNamespaces.consumerReach with
                        TreeDeclared = set [ "Coalesced"; "IJobScheduler" ]
                }

                let segments = Text.RegularExpressions.Regex.Split(before, "(\n)")
                let after, _ = SubsystemNamespaces.rewrite table treeDeclares segments

                Expect.stringContains (String.concat "" after) "open ToolUp.Platform.Jobs" "the type row still derives"

                let caseOnly =
                    Text.RegularExpressions.Regex.Split("module Consumer.Server\n\nlet x = Coalesced\n", "(\n)")

                let untouched, rewrites = SubsystemNamespaces.rewrite table treeDeclares caseOnly
                Expect.equal untouched caseOnly "the case row does not"
                Expect.isEmpty rewrites "and nothing is reported"

            testCase "a match arm declares nothing, and a case it names still derives"
            <| fun () ->
                let before =
                    "module Consumer.Server\n\nopen ToolUp.Platform\n\nlet f o =\n    match o with\n    | Coalesced -> 1\n    | _ -> 2\n"

                let after, _ = rewrite before
                Expect.stringContains after "open ToolUp.Platform.Jobs" "the arm's case derives"

                Expect.isEmpty
                    (SubsystemNamespaces.declaredNames [|
                        "let f o ="
                        "    match o with"
                        "    | Required"
                        "    | Other -> 1"
                    |])
                    "an or-pattern under no type is not a declaration"

                Expect.equal
                    (SubsystemNamespaces.declaredNames [|
                        "type Need ="
                        "    private"
                        "    // a comment"
                        "    | Required of int"
                        "    | Optional"
                        "let g = 1"
                        "    | Stray"
                        "type Row = {"
                        "    Field: int"
                        "}"
                        "exception Refused of string"
                        "let (|Big|Small|) x = Big"
                    |])
                    (set [ "Need"; "Required"; "Optional"; "Row"; "Field"; "Refused"; "Big"; "Small" ])
                    "the type, its cases, a field, an exception and an active pattern"

            testCase "a case name an open outside the tier resolves derives nothing; ToolUp.Platform does not count"
            <| fun () ->
                let resolved = {
                    table with
                        Reaches =
                            table.Reaches
                                .Add("Consumer.Facts", set [ "Coalesced" ])
                                .Add("ToolUp.Platform", set [ "Coalesced" ])
                }

                let viaFacts =
                    "module Consumer.Server\n\nopen Consumer.Facts\n\nlet x = Coalesced\n"

                Expect.equal (SubsystemNamespaces.rewriteText resolved viaFacts |> fst) viaFacts "already resolved"

                let viaPlatform =
                    "module Consumer.Server\n\nopen ToolUp.Platform\n\nlet x = Coalesced\n"

                Expect.stringContains
                    (SubsystemNamespaces.rewriteText resolved viaPlatform |> fst)
                    "open ToolUp.Platform.Jobs"
                    "the server half still wins over Core's"

            testCase "an open the move left dead is reported and left in place; a live or unknown one is not"
            <| fun () ->
                let report (text: string) =
                    SubsystemNamespaces.likelyDeadOpens tableWithPlatform SubsystemNamespaces.consumerReach text

                Expect.equal
                    (report "module C\n\nopen ToolUp.Platform\n\nlet x = 1\n")
                    [ 3, "open ToolUp.Platform" ]
                    "reaches nothing here"

                Expect.equal
                    (report "module C\n\nopen ToolUp.Platform\n\nlet x = Ok (String.length \"\")\n")
                    [ 3, "open ToolUp.Platform" ]
                    "a name that resolves without the open keeps nothing alive"

                Expect.isEmpty (report "module C\n\nopen ToolUp.Platform\n\nlet x (c: ServerConfig) = c\n") "live"

                Expect.equal
                    (report "module C\n\nopen ToolUp.Platform\n\n// ServerConfig\nlet x = \"ServerConfig\"\n")
                    [ 3, "open ToolUp.Platform" ]
                    "a name in a comment or a string keeps nothing alive"

                Expect.isEmpty (report "module C\n\nopen Consumer.Domain\n\nlet x = 1\n") "not the move's to kill"

                Expect.equal
                    (report "module C\n\nopen ToolUp.Platform.Scope.Teams.TeamInvitationHandler\nlet x = 1\n")
                    []
                    "a path the tables hold no members for is never called dead"

                Expect.isEmpty
                    (SubsystemNamespaces.likelyDeadOpens
                        tableWithPlatform
                        {
                            SubsystemNamespaces.consumerReach with
                                ReachesServer = false
                        }
                        "module C\n\nopen ToolUp.Platform\n")
                    "a project out of scope reports nothing"

                let text = "module C\n\nopen ToolUp.Platform\n\nlet x = 1\n"
                let after, _ = SubsystemNamespaces.rewriteText tableWithPlatform text
                Expect.equal after text "and the rewrite deletes nothing"

            testCase "the Codemod reads the tree's declarations and reports dead opens as review findings"
            <| fun () ->
                let dir =
                    Path.Combine(Path.GetTempPath(), "codemod-1198-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore

                try
                    File.WriteAllText(
                        Path.Combine(dir, "App.fsproj"),
                        "<Project><ItemGroup><Compile Include=\"Modes.fs\" /><Compile Include=\"Use.fs\" /><PackageReference Include=\"ToolUp.Sdk\" /></ItemGroup></Project>"
                    )

                    File.WriteAllText(
                        Path.Combine(dir, "Modes.fs"),
                        "module App.Modes\n\ntype Need =\n    | Required\n    | Optional\n"
                    )

                    File.WriteAllText(
                        Path.Combine(dir, "Use.fs"),
                        "module App.Use\n\nopen ToolUp.Platform\nopen App.Modes\n\nlet x = Required\n"
                    )

                    let plan = Codemod.planFor [ CodemodMigration.SubsystemNamespaces ] dir
                    let file = plan.Files |> List.find (fun f -> f.RelativePath = "Use.fs")

                    Expect.equal file.Rewritten file.Original "Required is the tree's own, so nothing is derived"

                    Expect.equal
                        (file.Findings |> List.map (fun f -> f.RuleId, f.Line))
                        [ "1198-likely-dead-open", 3 ]
                        "the open that now reaches nothing is reported, and stays"
                finally
                    Directory.Delete(dir, true)

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
                            TreeDeclared = Set.empty
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

            testCase "the reach table answers for ToolUp.Platform, a namespace outside the tier and a moved module"
            <| fun () ->
                let reaches = SubsystemNamespaces.embeddedIndex.Value.Reaches

                let has path name =
                    reaches.TryFind path
                    |> Option.exists (fun (names: Set<string>) -> names.Contains name)

                Expect.isTrue (has "ToolUp.Platform" "ServerConfig") "Core's namespace"
                Expect.isTrue (has "ToolUp.Facts" "Scalar") "a union case outside the tier"

                Expect.isTrue
                    (has "ToolUp.Platform.Scope.StorageScopeResolver" "IStorageScopeResolver")
                    "a moved module's member"

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