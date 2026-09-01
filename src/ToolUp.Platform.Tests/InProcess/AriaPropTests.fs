module ToolUp.Platform.Tests.InProcess.AriaPropTests

open System.IO
open System.Text.RegularExpressions
open Expecto

// ─── AriaProp helper-module sanity contract ──────────────────────
//
// `Client/UI/AriaProp.fs` wraps `prop.custom` with `aria-*` and
// `role` attribute names. The helpers' correctness rests on two
// invariants:
//
//   1. Each declared helper produces a Feliz `IReactProperty` via
//      `prop.custom ("aria-…"|"role", ...)`.
//   2. No helper is wired to a non-`aria-*` / non-`role` attribute
//      name — that would defeat the audit ratchet
//      (`DomAttrCustomAuditTests.fs`) which excludes this module
//      from the no-`prop.custom("aria-…"|"role")` sweep on the basis
//      that this is the blessed source of those calls.
//
// `AriaProp.fs` lives in the Fable-only client tier and cannot be
// compiled by the .NET Expecto runner, so the checks here are
// textual (same approach as `SvgPropTests` / `DataPropTests`).

let private ariaPropSource () =
    let testFile = System.Reflection.Assembly.GetExecutingAssembly().Location
    let assemblyDir = Path.GetDirectoryName testFile

    let repoRoot =
        Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    Path.Combine(repoRoot, "src", "ToolUp.Platform.UI", "AriaProp.fs")

/// Each helper the module must expose, paired with the attribute
/// name it must wrap. Drift between the helper name and the
/// attribute name is the failure mode this list pins.
let private expectedHelpers = [
    "label", "aria-label"
    "labelledBy", "aria-labelledby"
    "describedBy", "aria-describedby"
    "hidden", "aria-hidden"
    "expanded", "aria-expanded"
    "selected", "aria-selected"
    "checked'", "aria-checked"
    "disabled", "aria-disabled"
    "busy", "aria-busy"
    "live", "aria-live"
    "atomic", "aria-atomic"
    "controls", "aria-controls"
    "current", "aria-current"
    "pressed", "aria-pressed"
    "hasPopup", "aria-haspopup"
    "modal", "aria-modal"
    "role", "role"
]

let private helperTest (helperName: string, attrName: string) =
    let testName = sprintf "ariaProp.%s wraps prop.custom %s" helperName attrName

    testCase testName
    <| fun () ->
        let contents = File.ReadAllText(ariaPropSource ())
        let declaration = sprintf "let %s " helperName

        Expect.stringContains contents declaration (sprintf "AriaProp.fs must declare a `let %s` helper" helperName)

        let body = sprintf "prop.custom (\"%s\"," attrName

        Expect.stringContains
            contents
            body
            (sprintf "ariaProp.%s must wrap prop.custom with the attribute name %s" helperName attrName)

let private fileExistsTest = test "AriaProp.fs exists at the canonical client-UI path" {
    let path = ariaPropSource ()

    Expect.isTrue (File.Exists path) (sprintf "expected AriaProp.fs at %s" path)
}

let private customEscapeHatchTest = test "ariaProp.custom escape hatch is exposed" {
    let contents = File.ReadAllText(ariaPropSource ())

    Expect.stringContains
        contents
        "let custom "
        "AriaProp.fs must declare a `let custom` generic escape hatch for one-off aria-* attributes"
}

let private onlyAriaOrRoleTest = test "every helper wraps an aria-* or role attribute" {
    let contents = File.ReadAllText(ariaPropSource ())

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
    // name starts with `aria-` or is exactly `role`. The generic
    // `custom` helper takes `name` as a parameter (no literal).
    let pattern = Regex("prop\\.custom\\s*\\(\\s*\"([^\"]+)\"")
    let matches = pattern.Matches(codeOnly)

    let offenders = [
        for m in matches do
            let name = m.Groups[1].Value

            if not (name.StartsWith "aria-" || name = "role") then
                yield name
    ]

    Expect.equal
        offenders
        []
        "AriaProp.fs helpers must only wrap aria-* or role attribute names; non-matching names belong in DataProp.fs or SvgProp.fs"
}

[<Tests>]
let tests =
    testList "AriaProp helper module" [
        fileExistsTest
        testList "each declared helper wraps the declared attribute" (expectedHelpers |> List.map helperTest)
        customEscapeHatchTest
        onlyAriaOrRoleTest
    ]