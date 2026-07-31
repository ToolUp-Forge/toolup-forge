module ToolUp.DeadCode.Program

open System
open System.IO
open System.Text.RegularExpressions

// ─── What this tool is, and the one idea it rests on ─────────────
//
// Phase 626. Nothing in this repo detects a definition with no call
// sites. The compiler does not: `--warnon:1182` fires only for unused
// LOCAL bindings. A module-level `let private` with zero callers, a
// module-level `let internal` with zero callers, and an unused private
// type are all silent — measured, not assumed (see README.md, "What the
// compiler already tells us").
//
// The hard part of unreferenced-code analysis for THIS repo is not
// finding definitions; it is the false positives. This SDK ships `.fs`
// source under `fable/` in its nupkgs, so a helper with no in-repo
// caller may be a deliberate public affordance whose callers live in a
// consumer's tree. An analysis that flags every Fable-packed helper is
// worse than none — it trains its reader to ignore it.
//
// So this tool does not try to be clever about reachability. It
// restricts itself to definitions whose ENTIRE LEGAL CALLER SET IS
// INSIDE THIS REPO BY LANGUAGE RULE, and reports nothing else:
//
//   Tier P — module-level `let private`. F# confines the callers to the
//            declaring module, and a module is declared in exactly one
//            file. The corpus is therefore that ONE FILE, complete. A
//            consumer who extracts the Fable-packed source still cannot
//            call it: their code is in different modules. The dominant
//            false-positive class is dissolved by construction rather
//            than filtered by heuristic.
//
//   Tier I — module-level `let internal`. Callers are confined to the
//            declaring assembly, so the corpus is the repo (a safe
//            superset of the assembly — see README "Corpus widening").
//            EXCLUDED when the owning project packs its source under
//            `fable/`: the consumer compiles that source into THEIR
//            assembly, where `internal` is reachable from their code.
//
// Public bindings are deliberately out of scope. Their caller set is
// unbounded — every consumer of a published nupkg — so "no in-repo
// caller" carries no information. Narrowing the public surface is a
// different question with a different instrument: the api-baselines
// triage of Phase 256, which drives from `api-baselines/*.approved.txt`
// and applies `internal`/`private`. 256 shrinks what is EXPOSED; this
// finds what is UNREACHABLE. Neither subsumes the other — the motivating
// instance here, `extractEventScopeId`, was `private`, so it never
// appeared in a public baseline at all.
//
// REPORT, NEVER DELETE. Unreachable-today is not always unwanted; a seam
// awaiting its first implementor is the obvious counter-example and this
// SDK is full of them. Exit code is 0 by default. `--fail-on-dead` makes
// it a gate for callers who want one.
//
// Every documented limit is in README.md next to this file. Read it
// before trusting a number from here.

// ─── Arguments ───────────────────────────────────────────────────

type Args = {
    RepoRoot: string
    Json: bool
    FailOnDead: bool
    Verbose: bool
}

let private defaultRepoRoot () =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private defaultArgs () = {
    RepoRoot = defaultRepoRoot ()
    Json = false
    FailOnDead = false
    Verbose = false
}

let private usage =
    """ToolUp.DeadCode — unreferenced-definition report (Phase 626)

Usage:
  dotnet run --project tools/ToolUp.DeadCode -- [options]

Options:
  --repo-root <path>   Repository root to scan (default: inferred from source location)
  --json               Emit machine-readable JSON instead of the text report
  --fail-on-dead       Exit 1 when any high-confidence finding exists (default: always exit 0)
  --verbose            List every finding rather than capping the per-tier listing
  --help               Show this message

Reports only definitions whose entire legal caller set is inside this repo:
module-level `let private` (corpus = the declaring file) and module-level
`let internal` in non-Fable-packed projects (corpus = the repo). Public
bindings are out of scope — see tools/ToolUp.DeadCode/README.md."""

let private parseArgs (argv: string array) : Result<Args, string> =
    let rec loop (acc: Args) (xs: string list) =
        match xs with
        | [] -> Ok acc
        | "--repo-root" :: v :: rest ->
            loop
                {
                    acc with
                        RepoRoot = Path.GetFullPath v
                }
                rest
        | "--repo-root" :: [] -> Error "--repo-root expects a path"
        | "--json" :: rest -> loop { acc with Json = true } rest
        | "--fail-on-dead" :: rest -> loop { acc with FailOnDead = true } rest
        | "--verbose" :: rest -> loop { acc with Verbose = true } rest
        | flag :: _ -> Error $"Unrecognised argument '{flag}'"

    loop (defaultArgs ()) (List.ofArray argv)

// ─── Blanking pass: prose must not masquerade as a reference ─────
//
// Comments and plain string literals are overwritten with spaces, so
// line and column geometry survives exactly while their contents can no
// longer count as a call site. INTERPOLATED strings are deliberately
// left intact: `$"{formatThing x}"` contains a real reference, and
// blanking it would turn a live helper into a false positive — the one
// direction this tool must not err in. The cost is that literal prose
// inside an interpolated string can mask a genuinely dead binding, which
// errs toward under-reporting. That is the safe side.

let private blankNonCode (src: string) : string =
    let out = src.ToCharArray()
    let n = src.Length

    let blankRange (a: int) (b: int) =
        for k in a .. (min b n) - 1 do
            if out[k] <> '\n' && out[k] <> '\r' then
                out[k] <- ' '

    let mutable i = 0

    while i < n do
        let c = src[i]

        // Line comment — `//` and `///` alike.
        if c = '/' && i + 1 < n && src[i + 1] = '/' then
            let start = i

            while i < n && src[i] <> '\n' do
                i <- i + 1

            blankRange start i

        // Block comment — nestable, per the F# spec.
        elif c = '(' && i + 1 < n && src[i + 1] = '*' && not (i + 2 < n && src[i + 2] = ')') then
            let start = i
            let mutable depth = 0
            let mutable finished = false

            while not finished && i < n do
                if i + 1 < n && src[i] = '(' && src[i + 1] = '*' then
                    depth <- depth + 1
                    i <- i + 2
                elif i + 1 < n && src[i] = '*' && src[i + 1] = ')' then
                    depth <- depth - 1
                    i <- i + 2

                    if depth <= 0 then
                        finished <- true
                else
                    i <- i + 1

            blankRange start i

        elif c = '"' then
            // Look behind for the `@` (verbatim) and `$` (interpolated)
            // prefixes; either order, and both may be present.
            let mutable isInterp = false
            let mutable start = i
            let mutable j = i - 1
            let mutable scanning = true

            while scanning && j >= 0 do
                match src[j] with
                | '@' ->
                    start <- j
                    j <- j - 1
                | '$' ->
                    isInterp <- true
                    start <- j
                    j <- j - 1
                | _ -> scanning <- false

            let isVerbatim = start < i && src[start .. i - 1].Contains '@'
            let isTriple = i + 2 < n && src[i + 1] = '"' && src[i + 2] = '"'

            if isTriple then
                i <- i + 3
                let mutable finished = false

                while not finished && i < n do
                    if i + 2 < n && src[i] = '"' && src[i + 1] = '"' && src[i + 2] = '"' then
                        i <- i + 3
                        finished <- true
                    else
                        i <- i + 1
            elif isVerbatim then
                i <- i + 1
                let mutable finished = false

                while not finished && i < n do
                    if src[i] = '"' then
                        if i + 1 < n && src[i + 1] = '"' then
                            i <- i + 2
                        else
                            i <- i + 1
                            finished <- true
                    else
                        i <- i + 1
            else
                i <- i + 1
                let mutable finished = false

                while not finished && i < n do
                    if src[i] = '\\' then
                        i <- i + 2
                    elif src[i] = '"' then
                        i <- i + 1
                        finished <- true
                    else
                        i <- i + 1

            if not isInterp then
                blankRange start i

        elif c = '\'' then
            // `'` is an identifier character (`x'`) and a generic-parameter
            // sigil (`'T`) as well as a char delimiter, so only blank when
            // the text genuinely lexes as a char literal. Getting this
            // wrong on `'"'` would desynchronise the whole string scanner.
            let isCharLit =
                if i + 7 < n && src[i + 1] = '\\' && src[i + 2] = 'u' && src[i + 7] = '\'' then
                    true
                elif i + 3 < n && src[i + 1] = '\\' && src[i + 3] = '\'' then
                    true
                elif i + 2 < n && src[i + 1] <> '\\' && src[i + 1] <> '\'' && src[i + 2] = '\'' then
                    true
                else
                    false

            if isCharLit then
                let start = i
                i <- i + 1

                while i < n && src[i] <> '\'' do
                    i <- i + (if src[i] = '\\' then 2 else 1)

                i <- min n (i + 1)
                blankRange start i
            else
                i <- i + 1
        else
            i <- i + 1

    String(out)

// ─── Candidate extraction ────────────────────────────────────────

type Accessibility =
    | Priv
    | Internal

type Candidate = {
    File: string
    Line: int
    Column: int
    Name: string
    Access: Accessibility
    /// Last line (0-based, inclusive) of the binding's own body, used to
    /// separate "no references at all" from "referenced only by itself".
    BodyEnd: int
}

// The trailing lookahead is deliberate and load-bearing: a plain `\b`
// here backtracks off F#'s apostrophe-suffixed identifiers, because no
// word boundary exists after the `'`. That silently reported `member'`
// as `member` — and then counted references to the WRONG name. Caught
// only by reading the findings rather than the exit code.
let private bindingRegex =
    Regex(
        @"^(?<indent>[ ]*)let[ ]+(?:rec[ ]+)?(?<acc>private|internal)[ ]+(?:rec[ ]+)?(?<name>[A-Za-z_][A-Za-z0-9_']*)(?![A-Za-z0-9_'])",
        RegexOptions.Compiled
    )

let private moduleOpenerRegex =
    Regex(@"^(?<indent>[ ]*)(module|namespace)\b", RegexOptions.Compiled)

let private indentOf (line: string) =
    let mutable k = 0

    while k < line.Length && line[k] = ' ' do
        k <- k + 1

    k

let private isBlank (line: string) = String.IsNullOrWhiteSpace line

/// A binding is module-level when every enclosing block that is still
/// open at its indentation was opened by a `module` / `namespace` line.
/// A `let private` nested inside another `let` is a LOCAL, and the
/// compiler already warns on those (FS1182) — reporting them here would
/// duplicate a signal that exists and dilute one that does not.
let private extractCandidates (path: string) (blanked: string) : Candidate list =
    let lines = blanked.Replace("\r\n", "\n").Split('\n')

    // Stack of (indent, openedByModule).
    let mutable stack: (int * bool) list = []
    let found = ResizeArray<Candidate>()

    let bodyEndFrom (declLine: int) (declIndent: int) =
        let mutable e = declLine

        for k in declLine + 1 .. lines.Length - 1 do
            if e = k - 1 then
                let l = lines[k]

                if isBlank l || indentOf l > declIndent then
                    e <- k

        e

    for idx in 0 .. lines.Length - 1 do
        let line = lines[idx]

        if not (isBlank line) then
            let ind = indentOf line
            stack <- stack |> List.filter (fun (i, _) -> i < ind)

            let m = bindingRegex.Match line

            if m.Success then
                let allModule = stack |> List.forall snd

                if allModule then
                    let nameGroup = m.Groups.["name"]

                    found.Add {
                        File = path
                        Line = idx
                        Column = nameGroup.Index
                        Name = nameGroup.Value
                        Access =
                            if m.Groups.["acc"].Value = "private" then
                                Priv
                            else
                                Internal
                        BodyEnd = bodyEndFrom idx ind
                    }

            stack <- (ind, moduleOpenerRegex.IsMatch line) :: stack

    List.ofSeq found

// ─── Reference counting ──────────────────────────────────────────

type Verdict =
    /// Not one reference anywhere in its legal caller corpus.
    | Unreferenced
    /// Referenced only from inside its own body — a recursive helper
    /// nothing else calls. Almost certainly dead, but stated separately
    /// so the claim stays exactly as strong as the evidence.
    | SelfReferenceOnly
    | Live
    /// The name is declared more than once in its corpus, so occurrences
    /// cannot be attributed. Never reported as a finding.
    | Ambiguous

let private nameRegex (name: string) =
    Regex($@"(?<![A-Za-z0-9_'])%s{Regex.Escape name}(?![A-Za-z0-9_'])", RegexOptions.Compiled)

/// Offsets of every occurrence of `name` in `text`, cheap-filtered so the
/// repo-wide `internal` sweep does not run a regex over files that cannot
/// possibly match.
let private occurrences (rx: Regex) (name: string) (text: string) =
    if not (text.Contains(name, StringComparison.Ordinal)) then
        []
    else
        [ for m in rx.Matches text -> m.Index ]

// ─── Project model (only needed for the `internal` tier) ─────────

let private isUnderExcludedDir (path: string) =
    let p = path.Replace('\\', '/')

    [ "/bin/"; "/obj/"; "/node_modules/"; "/output/"; "/.git/" ]
    |> List.exists p.Contains

let private enumerateSources (root: string) (subdirs: string list) =
    subdirs
    |> List.collect (fun sub ->
        let dir = Path.Combine(root, sub)

        if Directory.Exists dir then
            Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories)
            |> Seq.filter (fun p -> not (isUnderExcludedDir p))
            |> List.ofSeq
        else
            [])

/// Directories of projects that pack their `.fs` under `fable/` in the
/// nupkg. A consumer extracts and compiles that source into THEIR
/// assembly, which makes `internal` reachable from consumer code — so
/// "no in-repo caller" stops being evidence of anything.
let private fablePackedProjectDirs (root: string) =
    let src = Path.Combine(root, "src")

    if not (Directory.Exists src) then
        Set.empty
    else
        Directory.EnumerateFiles(src, "*.fsproj", SearchOption.AllDirectories)
        |> Seq.filter (fun p -> not (isUnderExcludedDir p))
        |> Seq.filter (fun p ->
            let text = File.ReadAllText p
            text.Contains "PackagePath=\"fable" || text.Contains "PackagePath='fable")
        |> Seq.map (fun p -> Path.GetDirectoryName(p: string) |> Path.GetFullPath)
        |> Set.ofSeq

/// Projects granting `InternalsVisibleTo`. Their internals are legally
/// reachable from another assembly's source, so the same reasoning that
/// excludes Fable-packed projects applies.
let private ivtProjectDirs (root: string) =
    let src = Path.Combine(root, "src")

    if not (Directory.Exists src) then
        Set.empty
    else
        Directory.EnumerateFiles(src, "*.fsproj", SearchOption.AllDirectories)
        |> Seq.filter (fun p -> not (isUnderExcludedDir p))
        |> Seq.filter (fun p -> (File.ReadAllText p).Contains "InternalsVisibleTo")
        |> Seq.map (fun p -> Path.GetDirectoryName(p: string) |> Path.GetFullPath)
        |> Set.ofSeq

let private isUnderAny (dirs: Set<string>) (file: string) =
    let full = Path.GetFullPath file

    dirs
    |> Set.exists (fun d -> full.StartsWith(d + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))

// ─── Report ──────────────────────────────────────────────────────

type Finding = {
    Candidate: Candidate
    Verdict: Verdict
    RelPath: string
}

let private jsonEscape (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"")

let private run (args: Args) =
    let root = args.RepoRoot

    if not (Directory.Exists root) then
        eprintfn $"ToolUp.DeadCode: repo root not found: {root}"
        2
    else

        let roots = [ "src"; "samples"; "dev"; "probes"; "tools" ]
        let files = enumerateSources root roots

        if List.isEmpty files then
            eprintfn $"ToolUp.DeadCode: no .fs sources found under {root}"
            2
        else

            let blanked =
                files |> List.map (fun f -> f, blankNonCode (File.ReadAllText f)) |> dict

            let candidates = files |> List.collect (fun f -> extractCandidates f blanked[f])

            let fableDirs = fablePackedProjectDirs root
            let ivtDirs = ivtProjectDirs root

            // Tier I is excluded where `internal` escapes the repo by language
            // rule; those candidates are counted as skipped rather than silently
            // dropped, so the report's own coverage is legible.
            let analysable, skippedInternal =
                candidates
                |> List.partition (fun c ->
                    match c.Access with
                    | Priv -> true
                    | Internal -> not (isUnderAny fableDirs c.File) && not (isUnderAny ivtDirs c.File))

            let allBlanked = blanked.Values |> List.ofSeq

            let classify (c: Candidate) =
                let rx = nameRegex c.Name

                // Declaration offsets in the declaring file, used both to detect
                // shadowing and to exclude the declaration itself from the count.
                let declText = blanked[c.File]
                let declLines = declText.Replace("\r\n", "\n").Split('\n')

                let declCount =
                    declLines
                    |> Array.mapi (fun i l -> i, l)
                    |> Array.sumBy (fun (i, l) ->
                        let m = bindingRegex.Match l

                        if m.Success && m.Groups.["name"].Value = c.Name && i <> c.Line then
                            1
                        else
                            0)

                if declCount > 0 then
                    Ambiguous
                else

                    // Offset of the declaration's own name token, so it is not counted
                    // as a reference to itself.
                    let lineStarts =
                        let mutable acc = 0

                        [|
                            for l in declLines do
                                yield acc
                                acc <- acc + l.Length + 1
                        |]

                    let declOffset = lineStarts[c.Line] + c.Column
                    let bodyStart = lineStarts[c.Line]

                    let bodyEndOffset =
                        if c.BodyEnd + 1 < lineStarts.Length then
                            lineStarts[c.BodyEnd + 1]
                        else
                            declText.Length

                    let inOwnFile =
                        occurrences rx c.Name declText |> List.filter (fun o -> o <> declOffset)

                    match c.Access with
                    | Priv ->
                        if List.isEmpty inOwnFile then
                            Unreferenced
                        elif inOwnFile |> List.forall (fun o -> o >= bodyStart && o < bodyEndOffset) then
                            SelfReferenceOnly
                        else
                            Live
                    | Internal ->
                        let elsewhere =
                            allBlanked
                            |> List.sumBy (fun t ->
                                if Object.ReferenceEquals(t, declText) then
                                    0
                                else
                                    occurrences rx c.Name t |> List.length)

                        if elsewhere > 0 then
                            Live
                        elif List.isEmpty inOwnFile then
                            Unreferenced
                        elif inOwnFile |> List.forall (fun o -> o >= bodyStart && o < bodyEndOffset) then
                            SelfReferenceOnly
                        else
                            Live

            let rel (p: string) =
                Path.GetRelativePath(root, p).Replace('\\', '/')

            let findings =
                analysable
                |> List.map (fun c -> {
                    Candidate = c
                    Verdict = classify c
                    RelPath = rel c.File
                })

            let dead =
                findings
                |> List.filter (fun f -> f.Verdict = Unreferenced)
                |> List.sortBy (fun f -> f.RelPath, f.Candidate.Line)

            let selfOnly =
                findings
                |> List.filter (fun f -> f.Verdict = SelfReferenceOnly)
                |> List.sortBy (fun f -> f.RelPath, f.Candidate.Line)

            let ambiguous = findings |> List.filter (fun f -> f.Verdict = Ambiguous)

            let privTotal = analysable |> List.filter (fun c -> c.Access = Priv) |> List.length

            let intTotal =
                analysable |> List.filter (fun c -> c.Access = Internal) |> List.length

            if args.Json then
                let one (f: Finding) =
                    let acc =
                        match f.Candidate.Access with
                        | Priv -> "private"
                        | Internal -> "internal"

                    $"""{{"file":"{jsonEscape f.RelPath}","line":{f.Candidate.Line + 1},"name":"{jsonEscape f.Candidate.Name}","access":"{acc}"}}"""

                let join xs = String.Join(",", (xs |> List.map one))

                printfn
                    $"""{{"scanned":{List.length files},"candidates":{privTotal + intTotal},"privateCandidates":{privTotal},"internalCandidates":{intTotal},"skippedInternal":{List.length skippedInternal},"ambiguous":{List.length ambiguous},"unreferenced":{List.length dead},"selfReferenceOnly":{List.length selfOnly},"unreferencedItems":[{join dead}],"selfReferenceOnlyItems":[{join selfOnly}]}}"""
            else
                printfn ""
                printfn "ToolUp.DeadCode — unreferenced-definition report (Phase 626)"
                printfn "============================================================"
                printfn ""
                printfn $"  Source files scanned          %d{List.length files}"
                printfn $"  Module-level `let private`    %d{privTotal}"
                printfn $"  Module-level `let internal`   %d{intTotal}"
                printfn $"  `internal` skipped (escapes)  %d{List.length skippedInternal}"
                printfn $"  Ambiguous (shadowed name)     %d{List.length ambiguous}"
                printfn ""
                printfn $"  UNREFERENCED                  %d{List.length dead}"
                printfn $"  Self-reference only           %d{List.length selfOnly}"
                printfn ""

                let listing title (xs: Finding list) =
                    if not (List.isEmpty xs) then
                        printfn $"── {title} ──"

                        let shown = if args.Verbose then xs else xs |> List.truncate 40

                        for f in shown do
                            let acc =
                                match f.Candidate.Access with
                                | Priv -> "private"
                                | Internal -> "internal"

                            printfn $"  {f.RelPath}:{f.Candidate.Line + 1}  {acc} {f.Candidate.Name}"

                        if List.length shown < List.length xs then
                            printfn $"  … and %d{List.length xs - List.length shown} more (use --verbose)"

                        printfn ""

                listing "Unreferenced" dead
                listing "Self-reference only (recursive, nothing else calls it)" selfOnly

                printfn "Report only — deletion is a human decision. Unreachable-today is not"
                printfn "always unwanted. Read tools/ToolUp.DeadCode/README.md for the limits"
                printfn "of this analysis before acting on any line above."
                printfn ""

            if args.FailOnDead && not (List.isEmpty dead) then 1 else 0

[<EntryPoint>]
let main argv =
    if argv |> Array.exists (fun a -> a = "--help" || a = "-h") then
        printfn $"{usage}"
        0
    else
        match parseArgs argv with
        | Error msg ->
            eprintfn $"ToolUp.DeadCode: {msg}"
            eprintfn ""
            eprintfn $"{usage}"
            2
        | Ok args -> run args