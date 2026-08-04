module ToolUpApp.Build

open System.IO
open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open ToolUp.Platform
open ToolUp.Platform.Build

let config = {
    BuildConfig.defaults with
        Port = 5000
        // The 4 in-tree Expecto runners — `dotnet run -- VerifyAll`
        // iterates these sequentially. AIProviders.Tests is env-gated;
        // arms with no API-key env var report Pending (not Failed), so
        // a fresh checkout is green without per-provider credentials.
        TestPacks = [
            TestPack.create "Platform" "src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj"
            TestPack.create "Forms" "src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj"
            TestPack.create "Scheduling" "src/ToolUp.Scheduling.Tests/ToolUp.Scheduling.Tests.fsproj"
            TestPack.create "AIProviders" "src/ToolUp.AIProviders.Tests/ToolUp.AIProviders.Tests.fsproj"
            TestPack.create "Stripe" "src/ToolUp.Stripe.Tests/ToolUp.Stripe.Tests.fsproj"
            // Phase 182 — release SBOM gate + CycloneDX emission contract.
            TestPack.create "Build" "src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj"
            // Phase 195 — compile-time auth/audit analyzer AST-path coverage
            // (offline FCS parse fixtures → Analyzer.analyzeParseTree). The
            // recognition-vs-runtime parity lives in the Platform pack.
            TestPack.create
                "RemotingAnalyzers"
                "src/ToolUp.Remoting.Analyzers.Tests/ToolUp.Remoting.Analyzers.Tests.fsproj"
            // Phase 167 — `toolup` CLI substrate (dispatch + docker-emit
            // token substitution). Pure-BCL host; no env gating.
            TestPack.create "Cli" "src/ToolUp.Cli.Tests/ToolUp.Cli.Tests.fsproj"
            // Phase 518 — ToolUp.Voice: Transcript model + error taxonomy
            // + the Whisper / AzureSpeech pure Wire surfaces. No env gating.
            TestPack.create "Voice" "src/ToolUp.Voice.Tests/ToolUp.Voice.Tests.fsproj"
            // Phase 12e — AICookbooks licensing-boundary: the Community AG
            // Chart prompt builder leaks zero Enterprise feature names + the
            // ~600-token bound + graceful no-op on a missing cookbook.
            TestPack.create "AICookbooks" "src/ToolUp.AICookbooks.Tests/ToolUp.AICookbooks.Tests.fsproj"
            // Phase 11.E.2 — ToolUp.Algorithms: the six-portability-rule
            // contract pack, the registry's duplicate-registration
            // failure path, the dispatcher's typed error surface, and
            // the AI tool edge. No env gating; no vendor dependency.
            TestPack.create "Algorithms" "src/ToolUp.Algorithms.Tests/ToolUp.Algorithms.Tests.fsproj"
            // Phase 11.E.3 — the Math.NET algorithm provider: the shared
            // contract packs bound against a real vendor implementation,
            // plus known-answer numerical fixtures for the hand-written
            // estimators (R-7 quantiles, OLS + categorical encoding,
            // each distribution's parameterisation, centred-vs-trailing
            // alignment). No env gating — the numerics run in-process.
            TestPack.create
                "AlgorithmProviders"
                "src/ToolUp.AlgorithmProviders.Tests/ToolUp.AlgorithmProviders.Tests.fsproj"
            // Phase 193 — emulator-backed multi-cloud parity matrix. Every
            // emulator leg is env-gated (clean skip on a fresh checkout); the
            // divergence fixture + seam ratchets always run.
            TestPack.create "CloudParity" "src/ToolUp.Cloud.Parity.Tests/ToolUp.Cloud.Parity.Tests.fsproj"
        ]
}

[<EntryPoint>]
let main args =
    init args
    registerTargets config

    // Phase 614 — the Fable-tier test gate as ONE invocation.
    //
    // `VerifyAll` (in ToolUp.Platform.Build) covers the twelve .NET
    // Expecto packs. The client tier's harness is a different shape —
    // Fable-transpiled F# run under Node's built-in `node:test` — and
    // was until now a four-step recipe every caller reproduced by hand
    // from `CLAUDE.md`. Two transcriptions of one recipe drift; a single
    // target cannot, so CI and a developer necessarily run the same
    // thing.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyFable`
    //
    // The last step deliberately reads the TAP COUNTS, not just the exit
    // code. `node --test` exits 0 when it matched no test file at all,
    // so a harness that silently stopped emitting cases — a moved output
    // path, a Fable compile that wrote elsewhere, an entry point that
    // registered nothing — is indistinguishable from a green run by exit
    // status alone. That is the exact shape of the local `--filter`
    // incident that silently matched 0 Expecto tests. `fableCaseFloor` is
    // a LOWER BOUND, not the exact count: adding a case never needs a
    // companion edit here, and it fires only when the harness has
    // collapsed rather than shrunk.
    let fableCaseFloor = 100

    Target.create "VerifyFable" (fun _ ->
        let testDir = Path.getFullName "src/ToolUp.AI.Client.Tests"

        let onPath name =
            match ProcessUtils.tryFindFileOnPath name with
            | Some path -> path
            | None ->
                failwithf
                    "VerifyFable: `%s` was not found on PATH. The Fable-tier harness needs the .NET SDK and Node.js (>= 20)."
                    name

        let proc exe args =
            CreateProcess.fromRawCommand exe args
            |> CreateProcess.withWorkingDirectory testDir

        let runChecked exe args =
            proc exe args |> CreateProcess.ensureExitCode |> Proc.run |> ignore

        let npm = onPath "npm"
        let node = onPath "node"

        Trace.tracefn "▶ VerifyFable (1/4): dotnet tool restore"
        runChecked "dotnet" [ "tool"; "restore" ]

        // `ci`, not `install` — the lockfile is committed, so this is the
        // reproducible form and it is the same one CI runs.
        Trace.tracefn "▶ VerifyFable (2/4): npm ci"
        runChecked npm [ "ci"; "--no-fund"; "--no-audit" ]

        Trace.tracefn "▶ VerifyFable (3/4): dotnet fable -o output --noCache"
        runChecked "dotnet" [ "fable"; "-o"; "output"; "--noCache" ]

        Trace.tracefn "▶ VerifyFable (4/4): node --test output/Program.js"

        // `--test-reporter=tap` is pinned rather than inherited: node
        // picks `spec` on a TTY and `tap` otherwise, and the summary
        // counts parsed below only exist in the TAP form.
        let result =
            proc node [
                "--import"
                "./register-loader.mjs"
                "--test"
                "--test-reporter=tap"
                "output/Program.js"
            ]
            |> CreateProcess.redirectOutput
            |> Proc.run

        let output = result.Result.Output + result.Result.Error
        printfn "%s" output

        let tapCount (label: string) =
            let prefix = sprintf "# %s " label

            output.Split('\n')
            |> Array.map _.Trim()
            |> Array.tryPick (fun line ->
                if line.StartsWith prefix then
                    match System.Int32.TryParse(line.Substring prefix.Length) with
                    | true, n -> Some n
                    | _ -> None
                else
                    None)

        match tapCount "pass", tapCount "fail" with
        | Some passed, Some failed ->
            Trace.tracefn ""
            Trace.tracefn "VerifyFable summary: %d passed, %d failed (floor %d)." passed failed fableCaseFloor

            if failed > 0 then
                failwithf "VerifyFable: %d node:test case(s) failed." failed

            if passed < fableCaseFloor then
                failwithf
                    "VerifyFable: only %d case(s) ran, below the floor of %d. A run this small means the harness matched almost nothing — check the Fable output path and the entry point before lowering the floor."
                    passed
                    fableCaseFloor
        | _ ->
            failwith
                "VerifyFable: node:test printed no TAP `# pass` / `# fail` summary — the harness did not run. `node --test` exits 0 in that case, which is why the counts are checked and not just the exit status."

        if result.ExitCode <> 0 then
            failwithf "VerifyFable: node exited %d." result.ExitCode)

    // Phase 620 — compile-checked documentation snippets.
    //
    // Nothing detected a doc snippet naming an API the SDK no longer
    // has. The docs teach `fsharp` fenced blocks a reader copies
    // verbatim; when a phase renames or removes the thing a snippet
    // calls, the snippet silently becomes a lie that compiles nowhere
    // and fails at the reader's first build. Two live instances were
    // found minutes apart by a human noticing, which is not a process.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyDocSnippets`
    //        `… -- VerifyDocSnippets --update-baseline`  (see below)
    //
    // ── What is in scope (620.A) ────────────────────────────────────
    //
    // IN SCOPE BY DEFAULT: every ```fsharp block under `docs/**` and
    // `src/ToolUp.Platform/technical-guide/**`. The escape is a marker
    // in the fence INFO STRING — ```fsharp skip=<reason> — drawn from a
    // CLOSED set (`docSnippetSkipReasons`). An unknown reason, a bare
    // `skip`, or any other unrecognised attribute FAILS the target, so
    // the escape cannot be widened by typo or by invention; widening it
    // is a visible diff to this file.
    //
    // The info string is metadata, not content: every Markdown renderer
    // takes the FIRST word as the language, so `fsharp skip=fragment`
    // still highlights as F# and the marker is invisible to a reader.
    // That is what makes an in-band marker acceptable here — it adds no
    // ceremony to the code a reader copies.
    //
    // ONE tree-level exclusion, `docs/migrations/**`: a migration doc's
    // job is to show the RETIRED shape beside its replacement, usually
    // in the same block. Compiling it is category-incorrect — the old
    // shape must not compile, that is the point of the page. This is a
    // tree exclusion rather than 436 per-block markers precisely
    // because a marker on every migration block would be the easy
    // opt-out that gets reached for.
    //
    // ── How a fragment declares its context (620.B) ─────────────────
    //
    // Snippets are excerpts, not programs, and the docs must not grow
    // `open`-ceremony a reader then copies. So context is supplied
    // OUT-OF-BAND, in three layers, none of which touch the markdown:
    //
    //   1. `docSnippetPreamble` — the ambient opens any ToolUp source
    //      file has.
    //   2. `docSnippetTreePreamble` — per doc tree; a page under
    //      `docs/rag/` is read in the context of the RAG package.
    //   3. Page accumulation — `open` lines declared in an EARLIER
    //      block of the same page apply to later ones, which is how a
    //      reader reads a page top to bottom.
    //
    // Each block then compiles as its own module in its own generated
    // file. One file per BLOCK, not per page, is load-bearing: F#
    // abandons a file at its first parse error, so a single malformed
    // block in a shared file would mask every later block on the page.
    //
    // What this therefore gates is "every SDK name a snippet uses still
    // exists, with the shape shown" — not "this block is a runnable
    // program". That is the drift class, and it is the one that bites
    // readers.
    //
    // ── Where the failure points (620.E) ────────────────────────────
    //
    // Each generated file carries an F# `# <line> "<abs path to the
    // .md>"` directive, so the COMPILER ITSELF reports at the markdown
    // file and line — `docs/platform/auth.md(88,13): error FS1129: …`.
    // No error-message rewriting, and the location survives every
    // downstream tool that understands compiler output. The summary
    // below additionally groups by file and block ordinal.
    //
    // ── The baseline, and why it is not a way of going green ────────
    //
    // The corpus was measured before the gate was designed: of 655
    // in-scope blocks, 231 already named at least one API the SDK does
    // not have. Fixing all of them is a docs project, not this phase,
    // and marking them `skip=` would be a lie — `skip` means "not
    // checkable", not "checkable and currently wrong". They are instead
    // recorded in `docs-snippets/known-drift.txt`, keyed by a hash of
    // the block's own text.
    //
    // The baseline is a RATCHET, not a mute button, and it fails in
    // BOTH directions:
    //   * any failing block absent from the baseline fails the target
    //     (new drift cannot land);
    //   * any baseline entry that now compiles ALSO fails the target,
    //     demanding its removal (the baseline can only shrink).
    // Because the key is the block's content hash, editing a baselined
    // block invalidates its entry — so a broken snippet cannot be
    // quietly rewritten into a differently-broken one.
    //
    // Same posture as the Phase 175 public-API approval baseline, and
    // the same posture Phase 614 took when `verify-all` was not green
    // on Linux: record the evidence, gate what is true today, and let
    // the number only fall.
    //
    // `--update-baseline` REWRITES the file from the current run. It is
    // for the first seeding and for wholesale re-measurement after a
    // deliberate corpus change; it is never part of making CI pass.
    let docSnippetRoots = [ "docs"; "src/ToolUp.Platform/technical-guide" ]
    let docSnippetExcludedTrees = [ "docs/migrations" ]

    // The closed escape set. Each reason is a claim about the block's
    // SHAPE that a reviewer can check by reading it:
    //   signature   — an `.fsi`-shaped API listing (`module M = val f: …`).
    //                 Not implementation F#; cannot be compiled as such.
    //   fragment    — an excerpt: an elided body (`|> ...`), or it reads
    //                 locals belonging to a surrounding program the page
    //                 does not show.
    //   anti-pattern— deliberately wrong, shown as a "don't".
    // "It failed to compile" is NOT a reason — that is what the
    // baseline is for.
    let docSnippetSkipReasons = set [ "signature"; "fragment"; "anti-pattern" ]

    // Floor guard: this target legitimately exits 0 with an empty
    // corpus (a bad glob, a moved docs folder, an extractor that
    // matched no fences), which is indistinguishable from a pass unless
    // the count is asserted. A LOWER bound, like `fableCaseFloor` — it
    // fires when the harness has collapsed, not when the corpus shrank
    // a little.
    let docSnippetFloor = 300

    Target.create "VerifyDocSnippets" (fun _ ->
        // Read from the process argv rather than `p.Context.Arguments`:
        // FAKE's own CLI parser consumes trailing options before the
        // target sees them, so the flag never arrives there.
        let updateBaseline = args |> Array.contains "--update-baseline"
        let repoRoot = __SOURCE_DIRECTORY__
        let projDir = Path.Combine(repoRoot, "docs-snippets")
        let outDir = Path.Combine(projDir, "generated")
        let baselinePath = Path.Combine(projDir, "known-drift.txt")

        let toSlash (s: string) = s.Replace('\\', '/')

        let docFiles =
            docSnippetRoots
            |> List.collect (fun root ->
                let full = Path.Combine(repoRoot, root)

                if Directory.Exists full then
                    Directory.EnumerateFiles(full, "*.md", SearchOption.AllDirectories)
                    |> List.ofSeq
                else
                    [])
            |> List.map (fun f -> f, toSlash (Path.GetRelativePath(repoRoot, f)))
            |> List.filter (fun (_, rel) ->
                docSnippetExcludedTrees |> List.forall (fun t -> not (rel.StartsWith(t + "/"))))
            |> List.sortBy snd

        // A fence opens with >= 3 backticks plus an info string, and
        // closes on the first >= as many backticks with no info string.
        let fenceOf (line: string) =
            let t = line.TrimStart(' ')
            let n = t.Length - t.TrimStart('`').Length
            if n >= 3 then Some(n, t.Substring(n).Trim()) else None

        let blocksIn (abs: string, rel: string) =
            let lines = File.ReadAllLines abs
            let acc = ResizeArray<string * int * int * int * string * string list>()
            let mutable i = 0
            let mutable ordinal = 0

            while i < lines.Length do
                match fenceOf lines[i] with
                | Some(n, info) when info <> "" ->
                    let mutable j = i + 1

                    let closes k =
                        match fenceOf lines[k] with
                        | Some(m, "") when m >= n -> true
                        | _ -> false

                    while j < lines.Length && not (closes j) do
                        j <- j + 1

                    ordinal <- ordinal + 1
                    let body = [ for k in i + 1 .. j - 1 -> lines[k] ]
                    // rel, ordinal, first content line, closing-fence line, info, body
                    acc.Add(rel, ordinal, i + 2, j, info, body)
                    i <- j + 1
                | _ -> i <- i + 1

            List.ofSeq acc

        let allBlocks = docFiles |> List.collect blocksIn

        let fsharpBlocks =
            allBlocks
            |> List.filter (fun (_, _, _, _, info, _) -> info.Split(' ')[0] = "fsharp")

        // Validate every attribute BEFORE deciding anything, so a typo'd
        // marker fails loudly rather than silently leaving a block in or
        // out of scope.
        let markerErrors =
            fsharpBlocks
            |> List.collect (fun (rel, ord, start, _, info, _) ->
                info.Split(' ')
                |> Array.toList
                |> List.tail
                |> List.filter (fun a -> a <> "")
                |> List.choose (fun attr ->
                    if attr.StartsWith "skip=" then
                        let reason = attr.Substring 5

                        if docSnippetSkipReasons.Contains reason then
                            None
                        else
                            Some(
                                sprintf
                                    "%s:%d (block %d): unknown skip reason '%s'. Allowed: %s"
                                    rel
                                    (start - 1)
                                    ord
                                    reason
                                    (System.String.Join(", ", docSnippetSkipReasons))
                            )
                    else
                        Some(
                            sprintf
                                "%s:%d (block %d): unrecognised fence attribute '%s'. The only attribute is skip=<reason>."
                                rel
                                (start - 1)
                                ord
                                attr
                        )))

        if not markerErrors.IsEmpty then
            for e in markerErrors do
                Trace.traceError e

            failwithf "VerifyDocSnippets: %d invalid fence marker(s) — see above." markerErrors.Length

        let isSkipped (info: string) = info.Contains "skip="

        let inScope =
            fsharpBlocks |> List.filter (fun (_, _, _, _, info, _) -> not (isSkipped info))

        let skippedByReason =
            fsharpBlocks
            |> List.choose (fun (_, _, _, _, info, _) ->
                info.Split(' ')
                |> Array.tryPick (fun a -> if a.StartsWith "skip=" then Some(a.Substring 5) else None))
            |> List.countBy id
            |> List.sortBy fst

        // ---- ambient context, declared here and never in the docs ----
        let docSnippetPreamble = [
            "open System"
            "open System.Threading.Tasks"
            "open ToolUp.Platform"
            "open ToolUp.Platform.Auth"
            "open ToolUp.Platform.VectorKnowledgeTypes"
            "open DataManagementTypes"
        ]

        let docSnippetTreePreamble = [
            "docs/ai/",
            [
                "open ToolUp.AI"
                "open ToolUp.AI.AICompose"
                "open ToolUp.Platform.AI"
                "open ToolUp.AI.Wire"
            ]
            "docs/rag/", [ "open ToolUp.RAG"; "open ToolUp.RAG.RAGCompose" ]
            "docs/knowledge-base/", [ "open ToolUp.KnowledgeBase"; "open SharedTypes" ]
            "docs/forms/",
            [
                "open ToolUp.Forms"
                "open ToolUp.Forms.FormSchema"
                "open ToolUp.Forms.FormSubmission"
            ]
            "docs/scheduling/",
            [
                "open ToolUp.Scheduling"
                "open ToolUp.Scheduling.SchedulingTypes"
                "open ToolUp.Scheduling.SchedulingCompose"
            ]
        ]

        let preambleFor (rel: string) =
            let tree =
                docSnippetTreePreamble
                |> List.tryPick (fun (prefix, opens) -> if rel.StartsWith prefix then Some opens else None)
                |> Option.defaultValue []

            docSnippetPreamble @ tree

        let moduleNameOf (rel: string) =
            let stripped = rel.Replace(".md", "")

            let cleaned =
                System.String([| for c in stripped -> if System.Char.IsLetterOrDigit c then c else '_' |])

            if System.Char.IsDigit cleaned[0] then
                "d" + cleaned
            else
                cleaned

        let hashOf (body: string list) =
            use sha = System.Security.Cryptography.SHA256.Create()

            let bytes =
                System.Text.Encoding.UTF8.GetBytes(System.String.Join("\n", body |> List.map _.TrimEnd()))

            (sha.ComputeHash bytes)[..3] |> Array.map (sprintf "%02x") |> String.concat ""

        if Directory.Exists outDir then
            Directory.Delete(outDir, true)

        Directory.CreateDirectory outDir |> ignore

        // The blocks a compiler error may be attributed to. SKIPPED blocks
        // are present too, with inScope=false: a page's `open` lines are
        // carried forward to later blocks (see below), so an unresolvable
        // `open` declared in a skipped block would otherwise surface as an
        // unattributable error — and be reported as a harness fault — in
        // every later block on the page. Attributed to its declaring block
        // it is correctly ignored, because that block was already declared
        // uncheckable.
        // rel, ordinal, startLine, endLine, hash, inScope
        let blockTable = ResizeArray<string * int * int * int * string * bool>()

        for (rel, ord, start, endLine, info, body) in fsharpBlocks do
            if isSkipped info then
                blockTable.Add(rel, ord, start, endLine, hashOf body, false)

        for rel, blocks in fsharpBlocks |> List.groupBy (fun (rel, _, _, _, _, _) -> rel) do
            let m = moduleNameOf rel
            let absPath = docFiles |> List.find (fun (_, r) -> r = rel) |> fst

            let lineDirective line =
                sprintf "# %d \"%s\"" line (absPath.Replace("\\", "\\\\"))

            // (open text, the doc line it was declared on)
            let carried = ResizeArray<string * int>()

            for (_, ord, start, endLine, info, body) in blocks |> List.sortBy (fun (_, o, _, _, _, _) -> o) do
                if not (isSkipped info) then
                    let sb = System.Text.StringBuilder()

                    sb.AppendLine(sprintf "module ToolUp.DocSnippets.Generated.%s_B%02d" m ord)
                    |> ignore

                    for o in preambleFor rel do
                        sb.AppendLine o |> ignore

                    // Each carried `open` is stamped with the line it was
                    // written on, so an unresolvable one is reported against
                    // the block that DECLARED it rather than every block that
                    // inherits it.
                    for o, srcLine in Seq.distinctBy fst carried do
                        if not (body |> List.exists (fun l -> l.Trim() = o)) then
                            sb.AppendLine(lineDirective srcLine) |> ignore
                            sb.AppendLine o |> ignore

                    // Everything after this directive is attributed to the
                    // markdown file — this is what makes the compiler name
                    // the doc rather than a generated artefact.
                    sb.AppendLine(lineDirective start) |> ignore

                    for l in body do
                        sb.AppendLine l |> ignore

                    File.WriteAllText(Path.Combine(outDir, sprintf "%s_B%02d.fs" m ord), sb.ToString())
                    blockTable.Add(rel, ord, start, endLine, hashOf body, true)

                body
                |> List.iteri (fun i l ->
                    let t = l.Trim()

                    if t.StartsWith "open " && not (t.Contains "//") then
                        carried.Add(t, start + i))

        let checkedCount =
            blockTable |> Seq.filter (fun (_, _, _, _, _, ok) -> ok) |> Seq.length

        Trace.tracefn
            "▶ VerifyDocSnippets (1/2): extracted %d compiled block(s) (+%d skipped) from %d file(s)"
            checkedCount
            (blockTable.Count - checkedCount)
            docFiles.Length

        if checkedCount < docSnippetFloor then
            failwithf
                "VerifyDocSnippets: only %d block(s) extracted, below the floor of %d. A corpus this small means the extractor matched almost nothing — check the scope roots and the fence parser before lowering the floor."
                checkedCount
                docSnippetFloor

        Trace.tracefn "▶ VerifyDocSnippets (2/2): compiling against the real SDK"

        let result =
            CreateProcess.fromRawCommand "dotnet" [
                "build"
                "docs-snippets/ToolUp.DocSnippets.fsproj"
                "--nologo"
                "-v"
                "quiet"
            ]
            |> CreateProcess.withWorkingDirectory repoRoot
            |> CreateProcess.redirectOutput
            |> Proc.run

        let output = result.Result.Output + result.Result.Error

        // Attribute each reported error to the block whose line range
        // covers it. `+ 1` because an unterminated construct is reported
        // at the line PAST the block's last one.
        let errorLines =
            output.Split('\n')
            |> Array.map _.Trim()
            |> Array.filter (fun l -> l.Contains ": error ")
            |> Array.distinct

        let attribute (line: string) =
            let i = line.LastIndexOf ".md("

            if i < 0 then
                None
            else
                let rest = line.Substring(i + 4)

                match System.Int32.TryParse(rest.Substring(0, max 0 (rest.IndexOf ','))) with
                | false, _ -> None
                | true, lineNo ->
                    let rel = toSlash (line.Substring(0, i + 3))

                    blockTable
                    |> Seq.tryFind (fun (r, _, s, e, _, _) -> rel.EndsWith r && lineNo >= s && lineNo <= e + 1)
                    |> Option.map (fun (r, ord, s, _, h, ok) -> ((r, ord, s, h), line, ok))

        // Errors landing in a SKIPPED block are dropped: that block was
        // declared uncheckable, and the only way one of its lines reaches
        // the compiler is as an `open` carried onto a later block.
        let attributed =
            errorLines
            |> Array.choose attribute
            |> Array.filter (fun (_, _, ok) -> ok)
            |> Array.map (fun (k, l, _) -> k, l)

        let unattributed = errorLines |> Array.filter (attribute >> Option.isNone)

        // An error the harness cannot pin to a block is a harness fault
        // (a broken preamble, a missing reference) and must never be
        // absorbed as doc drift.
        if unattributed.Length > 0 then
            for l in unattributed |> Array.truncate 20 do
                Trace.traceError l

            failwithf
                "VerifyDocSnippets: %d compiler error(s) could not be attributed to a documentation block. That is a fault in the harness (preamble, project references, or the generated project), not drift in the docs."
                unattributed.Length

        let failing =
            attributed
            |> Array.groupBy fst
            |> Array.map (fun (k, es) -> k, es |> Array.map snd)
            |> Array.sortBy (fun ((r, o, _, _), _) -> r, o)

        let failingKeys =
            failing |> Array.map (fun ((r, _, _, h), _) -> r + " " + h) |> Set.ofArray

        let writeBaseline () =
            let lines = [
                "# Phase 620 — documentation snippets that name an API the SDK does not have."
                "# GENERATED by `dotnet run --project Build.fsproj -- VerifyDocSnippets --update-baseline`."
                "#"
                "# Each line: <content-hash> <doc path>#<block> — <first compiler error>"
                "# The hash is of the block's own text, so editing a block RETIRES its"
                "# entry and the block must then compile. This list may only shrink."
                ""
                for (rel, ord, start, h), errs in failing do
                    let first = if errs.Length > 0 then errs[0] else ""

                    // Keep the message, drop MSBuild's trailing
                    // ` [<absolute path>.fsproj]` — a machine-local path in
                    // a tracked file is churn on every other clone — and
                    // flatten control characters to spaces. FSC embeds raw
                    // 0x1D separators in its "Maybe you want one of the
                    // following" hints; a tracked file carrying those reads
                    // as BINARY to git's diff heuristics and to `file`,
                    // which would quietly opt this file out of the repo's
                    // LF normalisation.
                    let msg =
                        let i = first.IndexOf ": error "
                        let m = if i >= 0 then first.Substring(i + 2) else first
                        let j = m.LastIndexOf " ["
                        let trimmed = if j >= 0 && m.EndsWith "]" then m.Substring(0, j) else m

                        let flattened =
                            System.String([| for c in trimmed -> if System.Char.IsControl c then ' ' else c |])

                        System.Text.RegularExpressions.Regex.Replace(flattened, @"\s+", " ").Trim()

                    sprintf "%s %s#%d (line %d) — %s" h rel ord start msg
            ]

            // LF explicitly, not `WriteAllLines`: this file is tracked and
            // `.gitattributes` pins the repo to LF, so writing the platform
            // newline would make every regeneration on Windows a whole-file
            // diff on every other clone.
            File.WriteAllText(baselinePath, System.String.Join("\n", lines) + "\n")
            Trace.tracefn "VerifyDocSnippets: wrote %d baseline entries to %s" failing.Length baselinePath

        let runChecks () =
            let baseline =
                if File.Exists baselinePath then
                    File.ReadAllLines baselinePath
                    |> Array.filter (fun l -> l.Trim() <> "" && not (l.StartsWith "#"))
                    |> Array.choose (fun l ->
                        let parts = l.Split(' ')

                        if parts.Length >= 2 then
                            Some(parts[1].Split('#')[0] + " " + parts[0], l)
                        else
                            None)
                    |> Map.ofArray
                else
                    Map.empty

            let baselineKeys = baseline |> Map.keys |> Set.ofSeq

            let newFailures =
                failing
                |> Array.filter (fun ((r, _, _, h), _) -> not (baselineKeys.Contains(r + " " + h)))

            let fixedButListed = baselineKeys - failingKeys
            let passing = checkedCount - failing.Length

            Trace.tracefn ""
            Trace.tracefn "VerifyDocSnippets summary:"
            Trace.tracefn "  blocks compiled : %d" checkedCount
            Trace.tracefn "  passing         : %d" passing
            Trace.tracefn "  known drift     : %d (docs-snippets/known-drift.txt)" baselineKeys.Count
            Trace.tracefn "  new failures    : %d" newFailures.Length
            Trace.tracefn "  fixed-but-listed: %d" fixedButListed.Count

            for reason, n in skippedByReason do
                Trace.tracefn "  skip=%-12s %d" reason n

            if newFailures.Length > 0 then
                Trace.tracefn ""

                for (rel, ord, start, _), errs in newFailures do
                    Trace.traceError (sprintf "%s — block %d (opens at line %d):" rel ord (start - 1))

                    for e in errs |> Array.truncate 8 do
                        Trace.traceError ("    " + e)

                failwithf
                    "VerifyDocSnippets: %d documentation block(s) name an API the SDK does not have. Fix the snippet against the current surface; do NOT add it to known-drift.txt — that list may only shrink. If the block genuinely cannot be compiled (an .fsi-shaped listing, an elided excerpt, a deliberate anti-pattern), mark its fence with a skip reason instead."
                    newFailures.Length

            if fixedButListed.Count > 0 then
                Trace.tracefn ""

                for k in fixedButListed do
                    Trace.traceError (sprintf "    %s" (baseline |> Map.find k))

                failwithf
                    "VerifyDocSnippets: %d baseline entr(ies) in docs-snippets/known-drift.txt now compile. Delete those lines — the ratchet only holds if a fixed snippet is removed from the list."
                    fixedButListed.Count

            if result.ExitCode <> 0 && failing.Length = 0 then
                printfn "%s" output

                failwithf
                    "VerifyDocSnippets: the snippet project failed to build for a reason not attributable to any block (exit %d)."
                    result.ExitCode

            Trace.tracefn ""

            Trace.tracefn
                "VerifyDocSnippets: OK — %d block(s) compile, %d known-drift held at the ratchet."
                passing
                baselineKeys.Count

        if updateBaseline then writeBaseline () else runChecks ())

    // App-specific target: Azure deployment
    Target.create "Deploy-CD" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        dotnet [ "run"; "--project"; "deploy/azure/Deploy.fsproj"; "--"; "cd" ] "."
        |> Proc.run
        |> ignore)

    // Phase 72 — template-pack packaging.
    //
    // The standard Pack target (in ToolUp.Platform.Build) walks
    // `src/**/*.fsproj` and packs each SDK fsproj as a NuGet assembly
    // package. Templates live under `templates/` and need their own
    // packaging shape (`<PackageType>Template</PackageType>` + content-
    // only packing), so they don't fit the standard Pack glob.
    //
    // `PackTemplates` packs each template-pack csproj into
    // ../local-nuget-feed for consumers to `dotnet new install
    // <PackageId>` from the same feed they consume the SDK from.
    //
    // Single-template-per-package convention: each template ships as
    // its own NuGet (e.g. ToolUp.Templates.SAFER ships only
    // `toolup-safer`). Add new template-packs by extending the
    // `templatePackProjects` glob.
    Target.create "PackTemplates" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        let outputDir = Path.getFullName "../../local-nuget-feed"
        Directory.ensure outputDir

        let templatePackProjects = !!"templates/**/ToolUp.Templates.*.fsproj"

        for proj in templatePackProjects do
            Trace.tracefn "Packing template-pack %s..." proj

            dotnet [ "pack"; proj; "-c"; "Release"; "-o"; outputDir; "--nologo" ] "."
            |> Proc.run
            |> ignore)

    // Phase 4b dev convenience — wipe the local Platform Admin list
    // so the next `dotnet run` re-bootstraps from
    // `TOOLUP_INITIAL_PLATFORM_ADMIN` or `ServerConfig.AutoBootstrapDevAdmin`.
    // Saves the manual file-delete step when iterating on bootstrap.
    // Does NOT clear the rest of `data/` — KB content, files, teams,
    // etc. are preserved.
    //
    // Usage: `dotnet run -- ResetPlatformAdmins`
    //
    // The reset only applies to the reference deployment's
    // `LocalFileStorage` data path under `src/ToolUpApp-Server/data/`.
    // Cloud-storage deployments (Azure Blob, S3, GCS) are unaffected
    // — operators clear those via the cloud provider's tooling.
    Target.create "ResetPlatformAdmins" (fun _ ->
        let adminListPath =
            Path.Combine(__SOURCE_DIRECTORY__, "src", "ToolUpApp-Server", "data", "_platform", "platform-admins.json")

        if File.Exists adminListPath then
            File.Delete adminListPath
            Trace.tracefn "Removed %s" adminListPath
            Trace.tracefn "Next `dotnet run` will re-bootstrap the Platform Admin list."
        else
            Trace.tracefn "No admin list to remove (already empty / no LocalFileStorage data)."
            Trace.tracefn "Path checked: %s" adminListPath)

    // Phase 167 — pack the `toolup` CLI as a tool-manifest-installable
    // `dotnet tool` into the shared local feed. The standard `Pack`
    // target (in ToolUp.Platform.Build) already walks `src/**/*.fsproj`
    // and picks up ToolUp.Cli too — `PackCli` is the fast single-package
    // path for iterating on the CLI and then `dotnet tool install
    // --add-source ../../local-nuget-feed ToolUp.Cli`.
    //
    // Usage: `dotnet run -- PackCli`
    Target.create "PackCli" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        let outputDir = Path.getFullName "../../local-nuget-feed"
        Directory.ensure outputDir

        dotnet
            [
                "pack"
                "src/ToolUp.Cli/ToolUp.Cli.fsproj"
                "-c"
                "Release"
                "-o"
                outputDir
                "--nologo"
            ]
            "."
        |> Proc.run
        |> ignore)

    // Phase 626 — unreferenced-definition report.
    //
    // Nothing in this repo detected a definition with no call sites, and
    // the compiler does not fill the gap: `--warnon:1182` fires only for
    // unused LOCAL bindings, so a module-level `let private` with zero
    // callers is silent. That is how `DatadogLogsAuditSink`'s
    // `extractEventScopeId` drifted 51 of 132 match arms behind without
    // anyone noticing — nothing called it, so nothing failed.
    //
    // A REPORT, not a gate, and that is a deliberate choice rather than
    // a staging post: the tool cannot distinguish "dead" from "not yet
    // used", and in this SDK both are legitimate — a seam shipped ahead
    // of its first implementor is exactly the shape it would flag. A
    // gate makes keeping such a thing cost a suppression mechanism, and
    // the pressure then runs toward deleting seams to get green. Pass
    // `--fail-on-dead` to gate anyway; see tools/ToolUp.DeadCode/README.md
    // for the promotion criteria and the analysis's documented limits.
    //
    // Usage: `dotnet run --project Build.fsproj -- DeadCodeReport`
    Target.create "DeadCodeReport" (fun _ ->
        // Read from the process argv rather than the target context, for
        // the same reason VerifyDocSnippets does: FAKE's own CLI parser
        // consumes trailing options before the target sees them.
        let passthrough =
            [ "--fail-on-dead"; "--verbose"; "--json" ]
            |> List.filter (fun flag -> args |> Array.contains flag)

        let toolArgs =
            [ "run"; "--project"; "tools/ToolUp.DeadCode"; "--" ]
            @ [ "--repo-root"; __SOURCE_DIRECTORY__ ]
            @ passthrough

        CreateProcess.fromRawCommand "dotnet" toolArgs
        |> CreateProcess.withWorkingDirectory __SOURCE_DIRECTORY__
        |> CreateProcess.ensureExitCode
        |> Proc.run
        |> ignore)

    execute args