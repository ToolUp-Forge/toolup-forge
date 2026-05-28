module ToolUp.Platform.Tests.InProcess.SvgPropTests

open System.IO
open System.Text.RegularExpressions
open Expecto

// ─── SvgProp helper-module sanity contract ───────────────────────
//
// `Client/UI/SvgProp.fs` wraps `prop.custom` with React-compatible
// camelCase SVG attribute names. The helpers' correctness rests on
// two invariants:
//
//   1. Each declared helper produces a Feliz `IReactProperty` via
//      `prop.custom (camelCaseName, ...)`.
//   2. No helper is wired to a kebab-case attribute name — that
//      would defeat the module's reason to exist (React silently
//      drops kebab-case SVG attributes; the helpers exist precisely
//      to neutralise that footgun).
//
// `SvgProp.fs` lives in the Fable-only client tier and cannot be
// compiled by the .NET Expecto runner, so the checks here are
// textual (same approach as `WithRequestHeadersPassthroughTests`).
// If Fable-runnable test infrastructure lands in the future, these
// assertions can be promoted to behavioural checks that invoke each
// helper and inspect the resulting `IReactProperty`.

let private svgPropSource () =
    let testFile = System.Reflection.Assembly.GetExecutingAssembly().Location
    let assemblyDir = Path.GetDirectoryName testFile

    let repoRoot =
        Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    Path.Combine(repoRoot, "src", "ToolUp.Platform.Client", "Client", "UI", "SvgProp.fs")

/// Each helper the module must expose, paired with the React-compatible
/// camelCase attribute name it must wrap. Drift between the helper name
/// and the attribute name is the failure mode this list pins.
let private expectedHelpers = [
    "strokeWidth", "strokeWidth"
    "strokeDasharray", "strokeDasharray"
    "strokeOpacity", "strokeOpacity"
    "strokeLinecap", "strokeLinecap"
    "strokeLinejoin", "strokeLinejoin"
    "fillOpacity", "fillOpacity"
    "textAnchor", "textAnchor"
    "dominantBaseline", "dominantBaseline"
    "alignmentBaseline", "alignmentBaseline"
    "fontSize", "fontSize"
    "fontWeight", "fontWeight"
    "fontFamily", "fontFamily"
    "pointerEvents", "pointerEvents"
    "clipPath", "clipPath"
    "className", "className"
    "htmlFor", "htmlFor"
]

let private helperTest (helperName: string, attrName: string) =
    let testName = sprintf "svgProp.%s wraps prop.custom %s" helperName attrName

    testCase testName
    <| fun () ->
        let contents = File.ReadAllText(svgPropSource ())
        let declaration = sprintf "let %s " helperName

        Expect.stringContains contents declaration (sprintf "SvgProp.fs must declare a `let %s` helper" helperName)

        let body = sprintf "prop.custom (\"%s\"," attrName

        Expect.stringContains
            contents
            body
            (sprintf "svgProp.%s must wrap prop.custom with the React-compatible camelCase name %s" helperName attrName)

let private fileExistsTest = test "SvgProp.fs exists at the canonical client-UI path" {
    let path = svgPropSource ()

    Expect.isTrue (File.Exists path) (sprintf "expected SvgProp.fs at %s" path)
}

let private noKebabTest = test "no helper wraps a kebab-case attribute name" {
    let contents = File.ReadAllText(svgPropSource ())

    // Strip line-comments before scanning so the module-header
    // examples (e.g. stroke-width, fill-opacity) cited for
    // documentation purposes do not trip the check.
    let codeOnly =
        contents.Split('\n')
        |> Array.map (fun line ->
            let trimmed = line.TrimStart()

            if trimmed.StartsWith "//" || trimmed.StartsWith "///" then
                ""
            else
                line)
        |> String.concat "\n"

    // Match any `prop.custom ("foo-bar", ...)` shape — the exact
    // footgun the helpers exist to neutralise.
    let kebabPattern = Regex("prop\\.custom\\s*\\(\\s*\"[a-z]+-[a-z]+\"")
    let kebabMatches = kebabPattern.Matches(codeOnly)

    Expect.equal kebabMatches.Count 0 "SvgProp.fs must not wrap any kebab-case attribute name in prop.custom"
}

[<Tests>]
let tests =
    testList "SvgProp helper module" [
        fileExistsTest
        testList
            "each declared helper wraps the React-compatible camelCase attribute"
            (expectedHelpers |> List.map helperTest)
        noKebabTest
    ]