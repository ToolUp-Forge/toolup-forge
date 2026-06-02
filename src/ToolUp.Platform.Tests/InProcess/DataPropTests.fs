module ToolUp.Platform.Tests.InProcess.DataPropTests

open System.IO
open System.Text.RegularExpressions
open Expecto

// ─── DataProp helper-module sanity contract ──────────────────────
//
// `Client/UI/DataProp.fs` wraps `prop.custom` with `data-*` attribute
// names. The helpers' correctness rests on two invariants:
//
//   1. Each declared helper produces a Feliz `IReactProperty` via
//      `prop.custom ("data-…", ...)`.
//   2. No helper is wired to a non-`data-*` attribute name — that
//      would defeat the audit ratchet (`DomAttrCustomAuditTests.fs`)
//      which excludes this module from the no-`prop.custom("data-…")`
//      sweep on the basis that this is the blessed source of those
//      calls.
//
// `DataProp.fs` lives in the Fable-only client tier and cannot be
// compiled by the .NET Expecto runner, so the checks here are
// textual (same approach as `SvgPropTests`). If Fable-runnable test
// infrastructure lands in the future, these can be promoted to
// behavioural checks that invoke each helper and inspect the
// resulting `IReactProperty`.

let private dataPropSource () =
    let testFile = System.Reflection.Assembly.GetExecutingAssembly().Location
    let assemblyDir = Path.GetDirectoryName testFile

    let repoRoot =
        Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    Path.Combine(repoRoot, "src", "ToolUp.Platform.Client", "Client", "UI", "DataProp.fs")

/// Each helper the module must expose, paired with the `data-*`
/// attribute name it must wrap. Drift between the helper name and
/// the attribute name is the failure mode this list pins.
let private expectedHelpers = [
    "adClient", "data-ad-client"
    "adSlot", "data-ad-slot"
    "adFormat", "data-ad-format"
    "adLayoutKey", "data-ad-layout-key"
    "aiName", "data-ai-name"
    "area", "data-area"
    "citeIdx", "data-cite-idx"
    "testid", "data-testid"
]

let private helperTest (helperName: string, attrName: string) =
    let testName = sprintf "dataProp.%s wraps prop.custom %s" helperName attrName

    testCase testName
    <| fun () ->
        let contents = File.ReadAllText(dataPropSource ())
        let declaration = sprintf "let %s " helperName

        Expect.stringContains contents declaration (sprintf "DataProp.fs must declare a `let %s` helper" helperName)

        let body = sprintf "prop.custom (\"%s\"," attrName

        Expect.stringContains
            contents
            body
            (sprintf "dataProp.%s must wrap prop.custom with the data attribute name %s" helperName attrName)

let private fileExistsTest = test "DataProp.fs exists at the canonical client-UI path" {
    let path = dataPropSource ()

    Expect.isTrue (File.Exists path) (sprintf "expected DataProp.fs at %s" path)
}

let private customEscapeHatchTest = test "dataProp.custom escape hatch is exposed" {
    let contents = File.ReadAllText(dataPropSource ())

    Expect.stringContains
        contents
        "let custom "
        "DataProp.fs must declare a `let custom` generic escape hatch for one-off data-* attributes"
}

let private onlyDataAttributesTest = test "every helper wraps a data-* attribute" {
    let contents = File.ReadAllText(dataPropSource ())

    // Strip line-comments before scanning so the module-header
    // examples cited for documentation purposes do not trip the check.
    let codeOnly =
        contents.Split('\n')
        |> Array.map (fun line ->
            let trimmed = line.TrimStart()

            if trimmed.StartsWith "//" || trimmed.StartsWith "///" then
                ""
            else
                line)
        |> String.concat "\n"

    // Find every `prop.custom ("name", ...)` literal and assert the
    // name starts with `data-`. The generic `custom` helper takes
    // `name` as a parameter (no literal), so it doesn't appear here.
    let pattern = Regex("prop\\.custom\\s*\\(\\s*\"([^\"]+)\"")
    let matches = pattern.Matches(codeOnly)

    let offenders = [
        for m in matches do
            let name = m.Groups.[1].Value

            if not (name.StartsWith "data-") then
                yield name
    ]

    Expect.equal
        offenders
        []
        "DataProp.fs helpers must only wrap data-* attribute names; non-data-* names belong in AriaProp.fs or elsewhere"
}

[<Tests>]
let tests =
    testList "DataProp helper module" [
        fileExistsTest
        testList "each declared helper wraps the declared data-* attribute" (expectedHelpers |> List.map helperTest)
        customEscapeHatchTest
        onlyDataAttributesTest
    ]