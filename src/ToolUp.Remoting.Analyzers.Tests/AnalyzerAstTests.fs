module ToolUp.Remoting.Analyzers.Tests.AnalyzerAstTests

open Expecto
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Analyzers.SDK
open ToolUp.Remoting.Analyzers

// ─── Phase 195 — analyzer AST-path coverage ──────────────────────────
//
// The recognition-vs-runtime parity (the load-bearing acceptance criterion)
// is pinned in ToolUp.Platform.Tests via the source-linked Recognition.fs.
// THIS pack covers the other half — the syntax-tree extraction (Extraction.fs)
// + the full analyzer assembly — by parsing fixture sources offline (FCS
// parse-only; no project options / restore) and driving
// `Analyzer.analyzeParseTree` directly. A bug in field/function/attribute
// extraction (the analyzer-only half not reachable from the lean pack) fails
// here.

let private checker = FSharpChecker.Create()

let private parse (source: string) =
    let fileName = "fixture.fs"

    let parsingOptions = {
        FSharpParsingOptions.Default with
            SourceFiles = [| fileName |]
    }

    let result =
        checker.ParseFile(fileName, SourceText.ofString source, parsingOptions)
        |> Async.RunSynchronously

    result.ParseTree

let private analyze auditOptIn source =
    parse source |> Analyzer.analyzeParseTree auditOptIn

let private codes (messages: Message list) =
    messages |> List.map _.Code |> List.sort

// ── Fixtures ─────────────────────────────────────────────────────────

let private unclassifiedApi =
    """module Demo
type MyApi = {
    GetThings: unit -> Async<int>
    SaveThing: string -> Async<unit>
}
"""

let private fullyClassifiedApi =
    """module Demo
open ToolUp.Platform
type MyApi = {
    [<RequiresRole "Admin">]
    PromoteUser: string -> Async<unit>
    [<AllowAnonymous>]
    GetVersion: unit -> Async<string>
}
"""

let private mixedApi =
    """module Demo
type MyApi = {
    [<TenantScoped>]
    Guarded: unit -> Async<int>
    Naked: string -> Async<int>
}
"""

// A plain data record — not an API contract (fields are not functions) — must
// be ignored entirely.
let private dataRecord =
    """module Demo
type Thing = {
    Id: int
    Name: string
}
"""

// PII flows through an audited-gap method (audit opt-in only).
let private piiNoAudit =
    """module Demo
type GrantInput = {
    [<PiiSafe>]
    SubjectUserId: string
    Justification: string
}
type MyApi = {
    [<RequiresRole "Admin">]
    Grant: GrantInput -> Async<unit>
}
"""

let private piiWithAudit =
    """module Demo
type GrantInput = {
    [<PiiSafe>]
    SubjectUserId: string
}
type MyApi = {
    [<RequiresRole "Admin">]
    [<Audit "PermissionGranted">]
    Grant: GrantInput -> Async<unit>
}
"""

// Phase 964 (h) — an all-function record marked `NotRemotingApi` is not an
// API contract, whatever its shape: the generator's census skips it by
// attribute NAME in any namespace (`Plan.isExcludedFromApiCensus`), and the
// analyzer must agree. Both the source form and the CLR form, bare and
// qualified, are recognised.
let private markedCallbacks =
    """module Demo
type NotRemotingApiAttribute() =
    inherit System.Attribute()

[<NotRemotingApi>]
type Callbacks = {
    OnSaved: string -> unit
    OnClosed: unit -> Async<unit>
}
"""

let private markedCallbacksQualified =
    """module Demo
[<Contracts.Markers.NotRemotingApiAttribute>]
type Callbacks = {
    OnSaved: string -> unit
}
"""

// A different attribute on an all-function record does not exclude it.
let private otherAttributeApi =
    """module Demo
[<RequireQualifiedAccess>]
type MyApi = {
    GetThings: unit -> Async<int>
}
"""

// ── Tests ────────────────────────────────────────────────────────────

let private astTests =
    testList "AST extraction → TUR0001" [
        test "an unclassified API record raises TUR0001 for every method" {
            let msgs = analyze false unclassifiedApi
            Expect.equal (codes msgs) [ "TUR0001"; "TUR0001" ] "both naked methods flagged"

            msgs
            |> List.iter (fun m ->
                Expect.equal m.Severity Severity.Error "TUR0001 is an Error (mirrors the runtime refuse-to-start)"
                Expect.isNonEmpty m.Fixes "carries a placeholder codefix"

                Expect.stringContains
                    (m.Fixes |> List.head |> _.ToText)
                    "AllowAnonymous"
                    "codefix inserts the fail-closed placeholder")
        }

        test "a fully-classified API record is clean" {
            Expect.isEmpty (analyze false fullyClassifiedApi) "every method classified ⇒ no diagnostics"
        }

        test "a mixed record flags only the naked method" {
            let msgs = analyze false mixedApi
            Expect.equal (codes msgs) [ "TUR0001" ] "exactly one finding"
        }

        test "a plain data record is ignored (not an API contract)" {
            Expect.isEmpty (analyze false dataRecord) "non-function-record is never flagged"
        }

        test "an all-function record marked NotRemotingApi is not an API contract (Phase 964)" {
            Expect.isEmpty
                (analyze false markedCallbacks)
                "marked record is skipped, as the generator's census skips it"

            Expect.isEmpty
                (analyze false markedCallbacksQualified)
                "matched by name in any namespace, CLR form included"
        }

        test "another attribute on an all-function record does not exclude it (Phase 964)" {
            Expect.equal (codes (analyze false otherAttributeApi)) [ "TUR0001" ] "only NotRemotingApi excludes"
        }
    ]

let private auditTests =
    testList "AST extraction → TUR0002 (opt-in)" [
        test "PII input + no audit is silent when the heuristic is off" {
            // The Grant method IS auth-classified, so no TUR0001; with the
            // audit heuristic off, no TUR0002 either.
            Expect.isEmpty (analyze false piiNoAudit) "TUR0002 off by default"
        }

        test "PII input + no audit raises TUR0002 when opted in" {
            let msgs = analyze true piiNoAudit
            Expect.equal (codes msgs) [ "TUR0002" ] "audited-gap flagged on opt-in"

            Expect.stringContains
                (msgs |> List.head |> (fun m -> m.Fixes |> List.head |> _.ToText))
                "Audit"
                "codefix inserts the audit placeholder"
        }

        test "PII input WITH audit is clean even when opted in" {
            Expect.isEmpty (analyze true piiWithAudit) "audited ⇒ no TUR0002"
        }
    ]

[<Tests>]
let tests = testList "ToolUp.Remoting.Analyzers" [ astTests; auditTests ]