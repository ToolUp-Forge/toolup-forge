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

/// A `src/`-relative path, written with `/` separators.
let private srcFile (rel: string) =
    Path.Combine(repoRoot (), "src", rel.Replace('/', Path.DirectorySeparatorChar))

/// The toolkit files converted to the token contract in Phase 221.
///
/// Phase 307 promoted the seven `Toolup.UIToolkit` component modules into the
/// standalone `ToolUp.Platform.UI` package; `Layout.fs` (shell composition)
/// and `Sidebar.fs` (shell chrome) stayed in the client tier. The contract is
/// about these FILES, not their address, so each entry now carries its own
/// `src/`-relative path and the guard follows the code — the same correction
/// Phase 344 forced on the brand-hex guard below.
let private tokenisedFiles = [
    "ToolUp.Platform.UI/Toolkit/Tokens.fs"
    "ToolUp.Platform.UI/Toolkit/Kpi.fs"
    "ToolUp.Platform.UI/Toolkit/Data.fs"
    "ToolUp.Platform.UI/Toolkit/StateViews.fs"
    "ToolUp.Platform.UI/Toolkit/Forms.fs"
    "ToolUp.Platform.UI/Toolkit/Typography.fs"
    "ToolUp.Platform.Client/Client/UI/Toolkit/Layout.fs"
    "ToolUp.Platform.Client/Client/UI/Sidebar.fs"
]

/// Strip line comments so documentation examples don't trip the scan.
let private codeOnly (contents: string) =
    contents.Split('\n')
    |> Array.map (fun line ->
        let t = line.TrimStart()
        if t.StartsWith "//" then "" else line)
    |> String.concat "\n"

let private readCode rel =
    srcFile rel |> File.ReadAllText |> codeOnly

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
///
/// Phase 344 moved the AG Grid / AG Charts bindings out of `Client/UI` into
/// the standalone `Feliz.AgGrid` / `Feliz.AgCharts` packages — taking the
/// only sanctioned occurrence of the hex with them. Scanning `Client/UI`
/// alone would still be GREEN and would be measuring nothing: the guard
/// therefore follows the code, and the two binding directories are scanned
/// alongside it. The exemption is still `AgChart.fs` by NAME, so the
/// sanctioned fallback is exempt wherever the file lives and every other
/// binding file is covered.
///
/// Phase 307 did the same thing again for the toolkit itself, so
/// `ToolUp.Platform.UI` joins the list for the same reason: the components
/// most likely to freeze a brand hex now live there.
let private brandHexGuardTest = test "brand hex #59229D appears only in AgChart's ChartPalette fallback" {
    let scanned = [
        Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "UI")
        Path.Combine(repoRoot (), "src", "ToolUp.Platform.UI")
        Path.Combine(repoRoot (), "src", "Feliz.AgGrid")
        Path.Combine(repoRoot (), "src", "Feliz.AgCharts")
    ]

    let offenders = [
        for dir in scanned do
            for f in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
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

let private filesExistTest = test "tokenised toolkit files exist at their declared paths" {
    for rel in tokenisedFiles do
        Expect.isTrue (File.Exists(srcFile rel)) $"expected {rel} under src/"
}

/// Phase 1069 — every number the toolkit formats goes through the ONE
/// named locale, `OutputFormatting.fallbackLocale`. A `toLocaleString`
/// with no locale argument renders in the BROWSER's locale (a non-`en-GB`
/// user sees a different form from everyone else), and a spelled `"en-GB"`
/// literal is a second copy of the fallback that Phase 1075 would have to
/// find. So: each call names `fallbackLocale`, and the literal appears
/// exactly once — in its definition.
let private fallbackLocaleTest = test "the toolkit formats under the one named fallback locale (Phase 1069)" {
    let toolkitDir = Path.Combine(repoRoot (), "src", "ToolUp.Platform.UI")
    let objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"

    let files =
        Directory.EnumerateFiles(toolkitDir, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun f -> not (f.Contains objSegment))
        |> Seq.map (fun f -> Path.GetFileName f, File.ReadAllText f |> codeOnly)
        |> List.ofSeq

    // The call and the line after it — the locale argument of a
    // multi-line `toLocaleString (` sits on the next line.
    let calls = [
        for name, code in files do
            for m in Regex.Matches(code, @"toLocaleString\b[^\n]*(?:\n[^\n]*)?") do
                yield name, m.Value
    ]

    Expect.isGreaterThanOrEqual (List.length calls) 5 "the probe sees the toolkit's formatting calls"

    let unnamed =
        calls
        |> List.filter (fun (_, call) ->
            not (Regex.IsMatch(call, @"toLocaleString\s*\(?\s*(?:OutputFormatting\.)?fallbackLocale")))

    Expect.isEmpty unnamed $"a toolkit formatting call does not name `OutputFormatting.fallbackLocale`: %A{unnamed}"

    let literals = [
        for name, code in files do
            for _ in Regex.Matches(code, "\"en-GB\"") do
                yield name
    ]

    Expect.equal literals [ "OutputFormatting.fs" ] "the \"en-GB\" literal is spelled once, as `fallbackLocale`"
}

/// Phase 1069 — the shell bundle references no vendor UI package. Clerk's
/// `UserButton` is rendered by the ClerkUI companion, which registers it
/// as the page header's identity action; `Layout.userButton` renders the
/// registered action or nothing.
let private noVendorImportTest = test "the Platform.Client bundle imports no @clerk package (Phase 1069)" {
    let clientDir = Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client")

    let offenders = [
        for f in Directory.EnumerateFiles(clientDir, "*.fs", SearchOption.AllDirectories) do
            if File.ReadAllText(f).Contains "@clerk/" then
                yield Path.GetRelativePath(clientDir, f)
    ]

    Expect.equal
        offenders
        []
        "a Platform.Client source names a @clerk package — the vendor import belongs in the ClerkUI companion"

    let companion =
        File.ReadAllText(Path.Combine(repoRoot (), "src", "AuthProviders", "ClerkUI", "ClerkAuthUI.fs"))

    Expect.isTrue
        (companion.Contains "Layout.registerHeaderIdentityAction userButton")
        "the ClerkUI companion registers its UserButton as the header identity action"
}

[<Tests>]
let tests =
    testList "Client-toolkit theming contract" [
        filesExistTest
        testList "no hardcoded styling leaked back into a tokenised file" noLeakTests
        tokenEmissionTest
        brandHexGuardTest
        fallbackLocaleTest
        noVendorImportTest
    ]