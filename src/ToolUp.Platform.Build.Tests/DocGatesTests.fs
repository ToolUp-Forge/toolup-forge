// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The three documentation gates, each arm held in both directions over
/// fixture trees and strings. `DocGates.fs` is source-linked from the
/// repository root, so the targets and these proofs run one implementation.
/// Nothing here compiles a snippet: the compile step is a parameter, and the
/// fixtures hand it a result.
module ToolUp.Platform.Build.Tests.DocGatesTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts
open ToolUp.Forge
open ToolUp.Forge.DocGates

/// A sink that keeps what a gate printed.
let private collecting () =
    let lines = ResizeArray<string>()

    {
        Trace = lines.Add
        Error = fun l -> lines.Add("ERR " + l)
    },
    lines

let private noFlags: Flags = {
    Target = "VerifyDocSnippets"
    Positionals = []
    Switches = Set.empty
    Values = Map.empty
}

let private baselineText =
    String.concat "\n" [
        "# Public API baseline — Demo"
        "Demo.ServerConfig (class)"
        "Demo.ServerConfig.Host : System.String { get }"
        "Demo.ServerConfig.Name : System.String { get }"
        "Demo.ServerConfig.Port : System.Int32 { get }"
        "Demo.ServerConfig.withPort(System.Int32) : Demo.ServerConfig"
        "Demo.ServerConfig.withPort(System.Int32) : Demo.ServerConfig  (obsolete)"
        "Demo.Shape (class)"
        "Demo.Shape+Circle (class)"
        "Demo.Shape+Square (class)"
        "Demo.Shape.IsCircle : System.Boolean { get }"
        "Demo.Shape.IsSquare : System.Boolean { get }"
        "Demo.IStore (interface)"
        "Demo.IStore.Save(System.String) : Demo.ServerConfig"
        "Demo.Unread  # <members unavailable: FileNotFoundException>"
        ""
    ]

let private universe = Universe.ofSurfaces [ SurfaceDiff.parse baselineText ]

/// A scratch tree, deleted after `body` runs.
let private withTree (files: (string * string) list) (body: string -> unit) =
    let root =
        Path.Combine(Path.GetTempPath(), "docgates-" + Guid.NewGuid().ToString("N"))

    try
        for rel, text in files do
            let path = Path.Combine(root, rel)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, text)

        body root
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

let private compiled (exit: int) (stdout: string) =
    fun (_: string) (_: string list) (_: string) -> {
        Command = "dotnet build"
        ExitCode = exit
        StdOut = stdout
        StdErr = ""
        Elapsed = TimeSpan.Zero
    }

let private page = "# Page\n\n```fsharp\nlet x = ServerConfig.withPort 1\n```\n"

let private refusal (result: Result<'c, Refusal>) =
    match result with
    | Error r -> r.Message
    | Ok _ -> failtest "expected the gate to refuse"

let private parseTests =
    testList "SurfaceDiff.parse" [
        test "headers, members by prefix, the marker and the unreadable" {
            let s = SurfaceDiff.parse baselineText

            Expect.equal
                (s.Types |> List.map _.Full)
                [
                    "Demo.ServerConfig"
                    "Demo.Shape"
                    "Demo.Shape+Circle"
                    "Demo.Shape+Square"
                    "Demo.IStore"
                ]
                "types in file order"

            let config = s.Types |> List.find (fun t -> t.Full = "Demo.ServerConfig")

            Expect.equal
                (config.Members |> List.map _.Name)
                [ "Host"; "Name"; "Port"; "withPort"; "withPort" ]
                "members in file order"

            Expect.equal (config.Members |> List.filter _.Obsolete |> List.length) 1 "one marker"
            Expect.equal s.Unreadable (set [ "Demo.Unread" ]) "the unreadable type"
            Expect.isFalse (s.Lines |> Array.exists (fun l -> l.StartsWith "#")) "comments are not tokens"
        }

        test "a nested type's members are not its parent's" {
            let s = SurfaceDiff.parse baselineText
            let shape = s.Types |> List.find (fun t -> t.Full = "Demo.Shape")
            Expect.equal (shape.Members |> List.map _.Name) [ "IsCircle"; "IsSquare" ] "only the dotted lines"
        }

        test "the comparer reads the same tokens" {
            let drift =
                SurfaceDiff.compareSurface
                    baselineText
                    (baselineText.Replace("Demo.ServerConfig.Host", "Demo.ServerConfig.Hostname"))

            Expect.equal drift.Removed [ "Demo.ServerConfig.Host : System.String { get }" ] "removed"
            Expect.equal drift.Added [ "Demo.ServerConfig.Hostname : System.String { get }" ] "added"
        }
    ]

let private fenceTests =
    testList "fences and the skip= closed set" [
        test "a block's lines, ordinal and common indent" {
            let blocks =
                blocksIn "docs/p.md" [|
                    "x"
                    "  ```fsharp"
                    "  let a = 1"
                    "    a"
                    "  ```"
                    "```json"
                    "{}"
                    "```"
                |]

            Expect.equal blocks.Length 2 "two blocks"
            let b = blocks.Head
            Expect.equal (b.Ordinal, b.Start, b.Close) (1, 3, 4) "ordinal, first content line, closing fence"
            Expect.equal b.Body [ "let a = 1"; "  a" ] "the common indent is stripped, the internal one kept"
            Expect.equal blocks[1].Ordinal 2 "second"
        }

        test "every skip reason in the closed set is admitted" {
            for reason in docSnippetSkipReasons do
                let blocks = blocksIn "p.md" [| sprintf "```fsharp skip=%s" reason; "x"; "```" |]
                Expect.isEmpty (fenceErrors blocks) reason
        }

        test "an unknown reason, a bare skip and any other attribute are refused" {
            let errorsOf info =
                fenceErrors (blocksIn "p.md" [| "```" + info; "x"; "```" |])

            Expect.stringContains (errorsOf "fsharp skip=wip").Head "unknown skip reason 'wip'" "unknown reason"
            Expect.stringContains (errorsOf "fsharp skip").Head "unrecognised fence attribute 'skip'" "bare skip"
            Expect.stringContains (errorsOf "fsharp lang=x").Head "unrecognised fence attribute" "other attribute"
        }
    ]

let private markTests =
    testList "the high-water mark" [
        test "growth raises, a drop is refused, equality holds" {
            Expect.equal (markMove 578 577) MarkMove.Raise "growth"
            Expect.equal (markMove 576 577) MarkMove.Below "drop"
            Expect.equal (markMove 577 577) MarkMove.Hold "equal"
        }

        test "a hand-lowered mark admits the smaller corpus" {
            let lowered =
                rewriteMark [ "# argued: the page moved to docs/design"; ""; "577" ] 570

            let mark = markOf (lowered.Split('\n') |> List.ofArray)
            Expect.equal mark 570 "the hand edit is the mark"
            Expect.equal (markMove 570 mark) MarkMove.Hold "and the gate holds"
        }

        test "the auto-raise rewrites the number and keeps every comment" {
            let text = rewriteMark [ "# why"; ""; "574" ] 577
            Expect.equal text "# why\n\n577\n" "number replaced in place, LF"
        }

        test "an absent file seeds the mark" {
            Expect.equal (markOf []) docSnippetFloorSeed "seed"
            Expect.stringContains (rewriteMark [] 12) "HIGH-WATER MARK" "a fresh file gets its header"
        }
    ]

let private snippetGateTests =
    testList "VerifyDocSnippets over a fixture tree" [
        test "a clean tree passes and raises the mark" {
            withTree [ "docs/page.md", page; "docs-snippets/corpus-floor.txt", "0\n" ] (fun root ->
                let sink, _ = collecting ()

                match docSnippets sink root noFlags (compiled 0 "") with
                | Ok c ->
                    Expect.equal (c.Compiled, c.Passing, c.KnownDrift) (1, 1, 0) "one block, passing"

                    Expect.equal
                        (File.ReadAllText(Path.Combine(root, "docs-snippets", "corpus-floor.txt")))
                        "1\n"
                        "raised"
                | Error r -> failtestf "refused: %s" r.Message)
        }

        test "fewer blocks than the mark is refused" {
            withTree [ "docs/page.md", page; "docs-snippets/corpus-floor.txt", "5\n" ] (fun root ->
                let sink, _ = collecting ()

                Expect.stringContains
                    (refusal (docSnippets sink root noFlags (compiled 0 "")))
                    "below the recorded high-water mark"
                    "below")
        }

        test "a block that does not compile is a new failure" {
            withTree [ "docs/page.md", page; "docs-snippets/corpus-floor.txt", "1\n" ] (fun root ->
                let sink, _ = collecting ()

                let error =
                    Path.Combine(root, "docs", "page.md")
                    + "(4,9): error FS0039: The value 'withPort' is not defined"

                Expect.stringContains
                    (refusal (docSnippets sink root noFlags (compiled 1 error)))
                    "name an API the SDK does not have"
                    "new failure")
        }

        test "an error no block owns is a harness fault" {
            withTree [ "docs/page.md", page; "docs-snippets/corpus-floor.txt", "1\n" ] (fun root ->
                let sink, _ = collecting ()

                Expect.stringContains
                    (refusal (docSnippets sink root noFlags (compiled 1 "Preamble.fs(1,1): error FS0039: x")))
                    "could not be attributed"
                    "harness")
        }

        test "a listed entry that now compiles must be deleted" {
            withTree
                [
                    "docs/page.md", page
                    "docs-snippets/corpus-floor.txt", "1\n"
                    "docs-snippets/known-drift.txt", "deadbeef docs/page.md#1 (line 4) — gone\n"
                ]
                (fun root ->
                    let sink, _ = collecting ()

                    Expect.stringContains
                        (refusal (docSnippets sink root noFlags (compiled 0 "")))
                        "now compile"
                        "fixed-but-listed")
        }

        test "the baseline is held at zero even when its entry still fails" {
            let block = (blocksIn "docs/page.md" (page.Split('\n'))).Head
            let entry = sprintf "%s docs/page.md#1 (line 4) — still broken\n" (Block.hash block)

            withTree
                [
                    "docs/page.md", page
                    "docs-snippets/corpus-floor.txt", "1\n"
                    "docs-snippets/known-drift.txt", entry
                ]
                (fun root ->
                    let sink, _ = collecting ()
                    let error = Path.Combine(root, "docs", "page.md") + "(4,9): error FS0039: x"

                    Expect.stringContains
                        (refusal (docSnippets sink root noFlags (compiled 1 error)))
                        "empty is the enforced state"
                        "zero-baseline")
        }

        test "an invalid fence marker refuses before anything compiles" {
            withTree [ "docs/page.md", "```fsharp skip=later\nx\n```\n" ] (fun root ->
                let sink, _ = collecting ()

                Expect.stringContains
                    (refusal (docSnippets sink root noFlags (compiled 0 "")))
                    "invalid fence marker"
                    "marker")
        }

        test "the summary renders the census byte for byte" {
            let census = {
                Files = 336
                Compiled = 577
                Skipped = 566
                HighWater = 577
                Passing = 577
                KnownDrift = 0
                NewFailures = 0
                FixedButListed = 0
                SkipsByReason = [ "fragment", 500; "signature", 66 ]
                AmbientPages = 63
                Redeclarations = {
                    Types = 339
                    Blocks = 194
                    NotComparable = 12
                    Ambiguous = 20
                }
                Fragments = {
                    Walked = 500
                    Checked = 2198
                    Resolved = 1401
                    Local = 23
                    Outside = 616
                    Ambiguous = 158
                    RecordRegions = 259
                }
                UnresolvedOpens = [ "docs/companions/ai-providers.md", 5 ]
            }

            Expect.equal
                (summaryLines census)
                [
                    ""
                    "VerifyDocSnippets summary:"
                    "  blocks compiled : 577 (high-water mark 577)"
                    "  passing         : 577"
                    "  known drift     : 0 (docs-snippets/known-drift.txt)"
                    "  new failures    : 0"
                    "  fixed-but-listed: 0"
                    "  skip=fragment     500"
                    "  skip=signature    66"
                    "  ambient pages   : 63 (docs-snippets/ambient/)"
                    "  redeclared types: 339 in 194 block(s) — 327 compared, 12 not comparable, 20 ambiguous"
                    "  fragment symbols: 500 fragment(s) walked — 2198 identifier(s) checked, 1401 resolved, 23 local, 616 outside, 158 ambiguous; 259 record region(s)"
                    "  unresolved opens: 1 skipped block(s) — illustrative, or moved?"
                    "                    docs/companions/ai-providers.md#5"
                ]
                "the lines the gate printed before the move"
        }
    ]

let private declOf (body: string list) =
    declsIn {
        Rel = "docs/p.md"
        Ordinal = 1
        Start = 10
        Close = 20
        Info = "fsharp"
        Body = body
    }

let private parityTests =
    testList "the 668 parity arm" [
        test "a redeclared record with a member the surface has passes" {
            let census, findings =
                parityArm universe (declOf [ "type ServerConfig = {"; "    Port: int"; "}" ])

            Expect.isEmpty findings "subset of the surface"
            Expect.equal census.Types 1 "counted"
        }

        test "a redeclared record with a member the surface lacks is a finding" {
            let _, findings =
                parityArm universe (declOf [ "type ServerConfig = {"; "    Port: int"; "    Missing: string"; "}" ])

            Expect.equal findings.Length 1 "one finding"
            Expect.stringContains findings.Head "ServerConfig.Missing" "names the member"
        }

        test "a union case the surface does not have is a finding" {
            let _, findings =
                parityArm universe (declOf [ "type Shape ="; "    | Circle"; "    | Triangle" ])

            Expect.equal findings.Length 1 "Triangle only"
            Expect.stringContains findings.Head "Shape.Triangle" "names the case"
        }

        test "a declaration with nothing to compare is counted, not judged" {
            let census, findings = parityArm universe (declOf [ "type IStore = Other" ])
            Expect.isEmpty findings "nothing to compare"
            Expect.equal census.NotComparable 1 "not comparable"
        }
    ]

let private fragment (body: string list) = {
    Rel = "docs/p.md"
    Ordinal = 1
    Start = 10
    Close = 20
    Info = "fsharp skip=fragment"
    Body = body
}

let private fragmentTests =
    testList "the 672 fragment arm and the brace walker" [
        test "a member the container has resolves; one it lacks is a finding" {
            let census, findings =
                fragmentArm universe Map.empty [
                    fragment [ "let a = ServerConfig.withPort 1"; "let b = ServerConfig.withPrt 2" ]
                ]

            Expect.equal (census.Checked, census.Resolved) (2, 1) "two read, one resolved"
            Expect.equal findings.Length 1 "one finding"
            Expect.stringContains findings.Head "has no member `withPrt`" "names it"
            Expect.stringContains findings.Head "nearest: withPort" "suggests the fix"
        }

        test "a container the page declares is local" {
            let census, findings =
                fragmentArm universe (Map.ofList [ "docs/p.md", set [ "ServerConfig" ] ]) [
                    fragment [ "ServerConfig.anything" ]
                ]

            Expect.isEmpty findings "local"
            Expect.equal census.Local 1 "counted local"
        }

        test "a record construction with a field the matched record lacks is a finding" {
            let _, findings =
                fragmentArm universe Map.empty [
                    fragment [ "let c = { Port = 1; Host = \"h\"; Name = \"n\"; Nme = 2 }" ]
                ]

            Expect.equal findings.Length 1 "one"
            Expect.stringContains findings.Head "has no field `Nme`" "names the field"
        }

        test "the brace walker: one region per construction, anonymous ones discarded, unclosed ones kept" {
            Expect.equal (recordRegionsIn [ "{ A = 1; B = 2 }" ] 7) [ 7, set [ "A"; "B" ] ] "one region on its line"
            Expect.isEmpty (recordRegionsIn [ "{| A = 1 |}" ] 1) "anonymous"

            Expect.equal
                (recordRegionsIn [ "{ A = 1"; "  B = { C = 3 }" ] 1)
                [ 2, set [ "C" ]; 1, set [ "A"; "B" ] ]
                "nested, inner first"

            Expect.equal (recordRegionsIn [ "{ A = 1;" ] 3) [ 3, set [ "A" ] ] "unclosed"
            Expect.isEmpty (recordRegionsIn [ "let s = \"{ A = 1 }\"" ] 1) "a string is not a region"
        }
    ]

let private citationTests =
    testList "VerifySourceCitations" [
        test "the comment lexer over the CONTRIBUTING corpus" {
            let lines = [|
                "let url = \"http://example\" // a real comment"
                "let s = \"(* not a comment *)\""
                "let f = (*) 2 3"
                "(* opens"
                "   closes *) let x = 1"
                "let t = \"\"\"// in a triple\"\"\""
            |]

            Expect.equal (commentLinesOf lines) [ 1, " a real comment"; 4, " opens"; 5, "   closes " ] "comments only"
        }

        test "a resolving citation, a file name and a declared helper pass; a stale one is a finding" {
            let sources = [
                "src/A/Store.fs",
                [|
                    "module ServerConfig ="
                    "    let private helper = 1"
                    "// `ServerConfig.withPort`, `Directory.Build.props`, `ServerConfig.helper`"
                    "// `ServerConfig.withPrt`"
                |]
            ]

            let census, findings = citationArm universe sources
            Expect.equal (census.CitationsResolved, census.CitationsOutside, census.CitationsLocal) (1, 1, 1) "classes"
            Expect.equal findings.Length 1 "one finding"
            Expect.stringContains findings.Head "has no member `withPrt`" "names it"
        }

        test "a line pointer past the end of its file, and one to a missing src/ file, are findings" {
            let sources = [
                "src/A/Store.fs", [| "// see Store.fs:99 and src/B/Gone.fs:3"; "let x = 1" |]
            ]

            let census, findings = citationArm universe sources
            Expect.equal census.PointersSeen 2 "two pointers"
            Expect.equal findings.Length 2 "both"
            Expect.stringContains findings[0] "that file has 2 line(s)" "too short"
            Expect.stringContains findings[1] "this repo has no such file" "missing"
        }

        test "the gate refuses on a stale citation and passes without one" {
            let baseline = "api-baselines/Demo.approved.txt", baselineText

            withTree [ baseline; "src/A/A.fs", "// `ServerConfig.withPrt`\n" ] (fun root ->
                let sink, _ = collecting ()
                Expect.stringContains (refusal (sourceCitations sink root)) "cite something that is not there" "red")

            withTree [ baseline; "src/A/A.fs", "// `ServerConfig.withPort`\n" ] (fun root ->
                let sink, _ = collecting ()
                Expect.isOk (sourceCitations sink root) "green")
        }
    ]

let private sitesOf (lines: string list) =
    uploadSites [ "src/S.fs", Array.ofList lines ]

let private uploadTests =
    testList "VerifyUploadResults" [
        test "each result-bearing write discarded on one line is a site" {
            for call in
                [
                    "Upload"
                    "UploadWithETag"
                    "Remove"
                    "SetSecret"
                    "SetActiveTeam"
                    "DeleteTeam"
                    "TriggerOnce"
                    "Delete"
                ] do
                let sites = sitesOf [ sprintf "        let! _ = store.%s(x)" call ]
                Expect.equal sites.Length 1 call
                Expect.isFalse sites.Head.Marked call
        }

        test "the marker makes a discard a declared one" {
            let sites =
                sitesOf [ "let! _ = s.Upload(x) // best-effort-write: the sweep retries it" ]

            Expect.isTrue sites.Head.Marked "marked"
        }

        test "the two-line form is a site at the binding" {
            let sites = sitesOf [ "    let! _ ="; "        s.Upload(c, n, b)" ]
            Expect.equal (sites |> List.map _.Line) [ 1 ] "reported at the binding"
        }

        test "a fan-out ended by Async.Ignore is a site, for a write or a Delete" {
            for call in [ "Upload"; "Delete" ] do
                let sites =
                    sitesOf [
                        "    do!"
                        "        names"
                        sprintf "        |> List.map (fun n -> s.%s(n))" call
                        "        |> Async.Parallel"
                        "        |> Async.Ignore"
                    ]

                Expect.equal sites.Length 1 call
        }

        test "a consumed result, a comment and a named binding are not sites" {
            Expect.isEmpty (sitesOf [ "let! r = s.Upload(x)" ]) "named"
            Expect.isEmpty (sitesOf [ "match! s.Upload(x) with" ]) "matched"
            Expect.isEmpty (sitesOf [ "// let! _ = s.Upload(x)" ]) "comment"
            Expect.isEmpty (sitesOf [ "let! r ="; "    s.Upload(x)" ]) "named two-line"
        }

        test "the gate refuses an unmarked site and passes a marked one" {
            let flagsAt (root: string) = {
                noFlags with
                    Values = Map.ofList [ "--root", root ]
            }

            withTree [ "src/A/A.fs", "let! _ = s.Upload(x)\n" ] (fun root ->
                let sink, _ = collecting ()
                Expect.stringContains (refusal (uploadResults sink "unused" (flagsAt root))) "discard the Result" "red")

            withTree
                [
                    "src/A/A.fs", "let! _ = s.Upload(x) // best-effort-write: why\n"
                    "src/A.Tests/T.fs", "let! _ = s.Upload(x)\n"
                ]
                (fun root ->
                    let sink, _ = collecting ()

                    match uploadResults sink "unused" (flagsAt root) with
                    | Ok c -> Expect.equal (c.Files, c.Marked, c.Unmarked) (1, 1, 0) "test projects excluded"
                    | Error r -> failtestf "refused: %s" r.Message)
        }
    ]

let tests =
    testList "DocGates" [
        parseTests
        fenceTests
        markTests
        snippetGateTests
        parityTests
        fragmentTests
        citationTests
        uploadTests
    ]