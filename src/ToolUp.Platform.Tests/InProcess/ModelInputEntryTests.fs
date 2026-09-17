// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelInputEntryTests

open System.IO
open Expecto
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.Platform.Tests.Contracts

// ─── Phase 791.C — every provider entry takes the value ──────────────
//
// The acceptance clause "no provider entry in `src/` calls SendMessage
// with hand-assembled strings" is a claim about the source tree, so it is
// checked against the source tree. Same text-scan philosophy as
// `ArchitectureFitness` — no compiler hook, just the file text and a
// paren walk.
//
// The discriminator is ARITY, not a keyword. The string-shaped members
// take five arguments (the third being the assembled system prompt); the
// typed overloads take four. Counting arguments is exact where a keyword
// scan is not: the agent loop legitimately passes `Some streamCb` as its
// stream callback, so any rule keyed on `Some` would report the one entry
// that is hardest to migrate as the one that had not been.
//
// Both halves are asserted, because only the pair is meaningful: that
// each declared entry HAS a provider call (a path typo must fail, not
// pass vacuously), and that every such call is the typed form.

/// A provider call that still hands over a hand-assembled system prompt.
type EntryFinding = {
    /// Repo-relative path of the offending file.
    File: string
    /// The member called and the argument count that gave it away.
    Detail: string
}

/// Argument count of a call whose opening paren is at `openIndex`, or
/// `None` when the parens do not balance before end of source.
let private arity (source: string) (openIndex: int) : int option =
    let mutable depth = 0
    let mutable i = openIndex
    let mutable args = 1
    let mutable finished = false
    let mutable result = None

    while not finished && i < source.Length do
        match source[i] with
        | '('
        | '['
        | '{' -> depth <- depth + 1
        | ')'
        | ']'
        | '}' ->
            depth <- depth - 1

            if depth = 0 then
                result <- Some args
                finished <- true
        | ',' when depth = 1 -> args <- args + 1
        | _ -> ()

        i <- i + 1

    result

/// Every provider call in `source`, as (member name, argument count).
///
/// Pure over its inputs so the companion case below can feed it a
/// planted violation and prove the detector fails closed — a scan that
/// has never been shown to fire is indistinguishable from one that
/// cannot.
let providerCalls (source: string) : (string * int) list = [
    for name in [ "SendStructuredMessage"; "SendMessage" ] do
        let needle = "." + name + "("
        let mutable from = 0
        let mutable go = true

        while go do
            match source.IndexOf(needle, from) with
            | -1 -> go <- false
            | at ->
                // `member _.SendMessage(` is a definition, not a call.
                let isDefinition = source.LastIndexOf("member", at) > source.LastIndexOf('\n', at)

                if not isDefinition then
                    match arity source (at + needle.Length - 1) with
                    | Some n -> yield (name, n)
                    | None -> ()

                from <- at + needle.Length
]

/// The typed overloads' arity. `SendMessage(input, tools, onStream,
/// retryPolicy)` and `SendStructuredMessage(input, tools, schema,
/// retryPolicy)` both take four.
let private typedArity = 4

/// Findings for one entry file.
let scanEntry (filename: string) (source: string) : EntryFinding list = [
    for name, count in providerCalls source do
        if count <> typedArity then
            yield {
                File = filename
                Detail =
                    $"{name} called with {count} arguments — the string-shaped member. Construct a ModelInput and call the {typedArity}-argument overload."
            }
]

/// The provider entries Phase 791 migrated, repo-relative.
let entryFiles = [
    "src/ToolUp.AI.Server/Server/AIAgentEngine.fs"
    "src/ToolUp.AI.Server/Server/FastPathTriageResolver.fs"
    "src/ToolUp.AI.Server/Server/ConversationReplay.fs"
    "src/ToolUp.AI.Server/Server/AIProviderEntryProbe.fs"
    "src/ToolUp.AI.Server/Server/AISettingsHandler.fs"
    "src/ToolUp.RAG.Server/Server/ProviderQueryRewriter.fs"
    "src/ToolUp.Facts.Server/Server/AnswerPlanner.fs"
]

let tests =
    testList "Phase 791 — every provider entry takes a ModelInput" [

        testCase "each declared entry exists and reaches the provider"
        <| fun () ->
            // The anti-vacuity half. A renamed or mistyped path would
            // otherwise make the scan below pass by finding nothing.
            let root = ArchitectureFitness.repoRoot ()

            for file in entryFiles do
                let path = Path.Combine(root, file)
                Expect.isTrue (File.Exists path) $"declared provider entry {file} exists"

                let calls = providerCalls (File.ReadAllText path)

                Expect.isNonEmpty
                    calls
                    $"{file} reaches the provider — otherwise it is not an entry and should not be listed"

        testCase "and none of them hands the provider a hand-assembled string"
        <| fun () ->
            let root = ArchitectureFitness.repoRoot ()

            let findings =
                entryFiles
                |> List.collect (fun file -> scanEntry file (File.ReadAllText(Path.Combine(root, file))))

            Expect.isEmpty findings (findings |> List.map (fun f -> $"{f.File}: {f.Detail}") |> String.concat "\n")

        testCase "the scan fails closed on a planted string-shaped call"
        <| fun () ->
            // Proof the detector can fire. Five arguments, the third a
            // hand-assembled prompt — exactly the shape this phase
            // removed.
            let planted =
                """
                let! result =
                    provider.SendMessage([ userMessage ], [], Some (sprintf "%s" prefix), None, policy)
                """

            let findings = scanEntry "planted.fs" planted

            Expect.equal (List.length findings) 1 "the planted five-argument call is reported"

            Expect.stringContains findings.Head.Detail "5 arguments" "and the finding names what gave it away"

        testCase "and stays quiet on the typed form"
        <| fun () ->
            let migrated =
                """
                let input = ModelInput.ofSystemPrompt "X" (Some prefix) [ userMessage ]
                let! result = provider.SendMessage(input, [], None, policy)
                """

            Expect.isEmpty (scanEntry "migrated.fs" migrated) "a migrated call is not reported"

        testCase "a provider implementation is not read as a call"
        <| fun () ->
            // `member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)`
            // is five-argument by definition and must stay so — the
            // interface is unchanged (GP 11). Reading it as a call would
            // make every connector and every test double a violation.
            let implementation =
                """
                interface IAIProvider with
                    member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
                        return Ok response
                    }
                """

            Expect.isEmpty (scanEntry "provider.fs" implementation) "an implementation is a definition, not an entry"
    ]

// ─── Phase 791.C — rendering is byte-identical to the pre-phase strings ─

let renderTests =
    testList "Phase 791 — render reproduces the pre-phase prompt exactly" [

        testCase "a lifted system prompt renders back unchanged"
        <| fun () ->
            // GP 11 stated as an equation. Every migrated entry lifts its
            // existing `string option` through `ofSystemPrompt`, so this
            // is the property that keeps all seven byte-identical.
            let cases = [
                None
                Some ""
                Some "Reply with a single word."
                Some "line one\n\nline two\nline three"
            ]

            for systemPrompt in cases do
                let rendered =
                    ModelInput.render (ModelInput.ofSystemPrompt "builder" systemPrompt [])

                Expect.equal rendered.SystemPrompt systemPrompt $"%A{systemPrompt} survives the round trip"

        testCase "messages pass through untouched"
        <| fun () ->
            let messages = [
                AIProviderMessage.text "user" "first"
                AIProviderMessage.text "assistant" "second"
            ]

            let rendered = ModelInput.render (ModelInput.ofSystemPrompt "builder" None messages)

            Expect.equal rendered.Messages messages "the message list is carried, not rebuilt"

        testCase "multiple blocks join on the separator compose uses"
        <| fun () ->
            // `SystemPromptBuilder.compose` joins non-empty contributions
            // with a blank line. A value carrying the contributions
            // separately must render to the same bytes, or a deployment
            // that moves from one composed string to typed blocks would
            // silently change the prompt.
            let parts = [ "platform layer"; "team layer"; "module layer" ]

            let input =
                parts
                |> List.fold
                    (fun acc text ->
                        ModelInput.addBlock
                            {
                                BuilderId = "SystemPromptBuilder"
                                Text = text
                            }
                            acc)
                    ModelInput.empty

            Expect.equal
                (ModelInput.render input).SystemPrompt
                (Some(parts |> String.concat "\n\n"))
                "blocks join exactly as compose joins its parts"

        testCase "render introduces nothing that is not in the value"
        <| fun () ->
            // The faithfulness claim, asserted rather than asserted-about:
            // every byte of the rendering is accounted for by a field.
            let input =
                ModelInput.ofSystemPrompt "builder" (Some "the prompt") [ AIProviderMessage.text "user" "the question" ]

            let rendered = ModelInput.renderedText input

            Expect.equal
                rendered
                "the prompt\nthe question"
                "the rendering is the prompt and the turns, and nothing else"
    ]