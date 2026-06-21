module ToolUp.Platform.Tests.InProcess.ClientToolkitThemingTests

open System.IO
open System.Text.RegularExpressions
open Expecto

// ─── Client-toolkit theming contract (Phase 221/222/224) ─────────
//
// The Fable/Elmish client toolkit (`Toolup.UIToolkit.Tokens` + Kpi /
// Data / StateViews / Forms / Typography / Layout) and the shell read
// CSS-custom-property theming tokens instead of hardcoding shape /
// neutral / status styling, so a consumer (or per-team) `:root` override
// re-skins the whole client surface (see docs/client-toolkit-tokens.md).
//
// These files live in the Fable-only client tier and cannot be compiled
// or rendered by the .NET Expecto runner, so the contract is asserted
// TEXTUALLY (same approach as AriaPropTests / DataPropTests). This pins:
//   1. no hardcoded neutral/status/shape literal leaked back into a
//      tokenised toolkit file (the "zero-opinionated-styling" no-leak
//      contract — a regression here is otherwise invisible);
//   2. each theming token is actually referenced somewhere (emission);
//   3. the brand hex (#59229D) does not reappear outside AgChart's
//      sanctioned ChartPalette fallback (Phase 222 drift guard).
//
// The render-isolation assertion (render under two token sets, assert the
// only delta is the token values) needs a DOM / jsdom and so is deferred
// to a Fable-side harness; the no-leak contract below is its .NET-tier
// stand-in.

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private clientUi rel =
    Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "UI", rel)

/// The toolkit files converted to the token contract in Phase 221.
let private tokenisedFiles = [
    "Toolkit/Tokens.fs"
    "Toolkit/Kpi.fs"
    "Toolkit/Data.fs"
    "Toolkit/StateViews.fs"
    "Toolkit/Forms.fs"
    "Toolkit/Typography.fs"
    "Toolkit/Layout.fs"
    "Sidebar.fs"
]

/// Strip line comments so documentation examples don't trip the scan.
let private codeOnly (contents: string) =
    contents.Split('\n')
    |> Array.map (fun line ->
        let t = line.TrimStart()
        if t.StartsWith "//" then "" else line)
    |> String.concat "\n"

let private readCode rel =
    clientUi rel |> File.ReadAllText |> codeOnly

/// Hardcoded utilities that must NOT survive in a tokenised file — each
/// should now be a `text-[var(--…)]` / `rounded-[var(--radius)]` /
/// `bg-[var(--surface)]` reference instead.
let private bannedLiterals = [
    "text-gray-"
    "rounded-lg"
    "text-green-600"
    "text-green-500"
    "text-red-600"
    "text-red-700"
    "text-red-400"
]

let private noLeakTests =
    tokenisedFiles
    |> List.collect (fun rel ->
        bannedLiterals
        |> List.map (fun banned ->
            testCase $"{rel} contains no hardcoded `{banned}`"
            <| fun () ->
                let code = readCode rel

                Expect.isFalse
                    (code.Contains banned)
                    $"{rel} must read a theming token, not the hardcoded `{banned}` (Phase 221 no-leak contract)"))

/// Every theming token must be referenced by at least one toolkit file —
/// a token documented but never emitted is dead (and vice versa).
let private tokenEmissionTest = test "every client-toolkit theming token is referenced" {
    let allCode = tokenisedFiles |> List.map readCode |> String.concat "\n"

    for token in
        [
            "--surface"
            "--text-strong"
            "--text"
            "--muted"
            "--pos"
            "--neg"
            "--radius"
        ] do
        Expect.isTrue
            (allCode.Contains $"var({token})")
            $"no toolkit file references var({token}) — token is dead or a component regressed off it"
}

/// Phase 222 drift guard: the brand hex must not be frozen anywhere under
/// Client/UI except AgChart.fs, where it is the sanctioned ChartPalette
/// fallback (equal to the brand default) behind `refreshFromTheme`.
let private brandHexGuardTest = test "brand hex #59229D appears only in AgChart's ChartPalette fallback" {
    let uiDir =
        Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "UI")

    let offenders = [
        for f in Directory.EnumerateFiles(uiDir, "*.fs", SearchOption.AllDirectories) do
            let name = Path.GetFileName f

            if name <> "AgChart.fs" then
                let code = File.ReadAllText f |> codeOnly

                if Regex.IsMatch(code, "#59229[Dd]") then
                    yield name
    ]

    Expect.equal
        offenders
        []
        "brand colour must be themed via --color-brand, not a frozen #59229D literal (Phase 222 drift guard)"
}

let private filesExistTest = test "tokenised toolkit files exist at the canonical client-UI path" {
    for rel in tokenisedFiles do
        Expect.isTrue (File.Exists(clientUi rel)) $"expected {rel} under Client/UI"
}

[<Tests>]
let tests =
    testList "Client-toolkit theming contract" [
        filesExistTest
        testList "no hardcoded styling leaked back into a tokenised file" noLeakTests
        tokenEmissionTest
        brandHexGuardTest
    ]