// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 788 — the proved Elmish runtime as oracle, under Fable.
///
/// The .NET pack (`ToolUp.Platform.Tests/InProcess/ElmishProofOracleTests.fs`)
/// runs the extracted `proofs/ElmishRing.fst` / `ElmishSub.fst` models
/// live beside the production `RingBuffer` and `Sub.Internal.diff`, and
/// writes the model's verdicts for its generated campaign to
/// `tests/elmish-proof-corpus/` — every case carrying its inputs and the
/// outputs the proved model produced. This pack replays that corpus
/// against the TRANSPILED `Ring.fs` / `Sub.fs`, the copy every browser
/// client actually executes, through the same host-neutral drivers
/// (`ElmishProofDifferential.fs`, compiled into both packs).
///
/// ─── Why a corpus and not the extraction ─────────────────────────────
///
/// The extraction compiles on .NET only: the F* extractor emits
/// pre-F#-8 layout that needs `--strict-indentation-`, and Fable reads no
/// `OtherFlags` from an fsproj, so compiling the oracle here would mean
/// pinning this pack's `LangVersion` back to 7 — forbidden by the
/// workspace baseline. The corpus carries the model's ANSWER instead of
/// the model, held to the live model on every .NET run; what this pack
/// measures is unchanged — the transpiled runtime against the proved
/// model's verdict, on the same sequences by seed.
///
/// ─── Why this pack and not a browser-smoke scenario ──────────────────
///
/// The differential is pure: no DOM, no React, no timers. `node:test`
/// over the transpiled output is the cheapest host that runs the
/// transpiled runtime, and this project already pulls `Platform.Client`
/// through Fable, so the ring and the diff are transpiled here at no new
/// cost.
///
/// ─── What it does NOT claim ──────────────────────────────────────────
///
/// Nothing beyond the .NET pack's claims, restated for the other
/// runtime. The go-red cases are asserted caught here too, for the same
/// reason: a comparison that has never been shown to fail agrees with
/// whatever it is shown, on either host.
module ToolUp.AI.Client.Tests.ElmishProofOracleTests

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform.Tests.Client.ElmishProofDifferential

[<Import("readFileSync", from = "node:fs")>]
let private readFileSync (path: obj, encoding: string) : string = jsNative

[<Import("existsSync", from = "node:fs")>]
let private existsSync (path: obj) : bool = jsNative

/// Resolve a path relative to THIS MODULE rather than to the process cwd
/// — the `RemotingCorpusParityTests` precedent. The transpiled module
/// sits in `output/`, so the repository root is three levels up.
[<Emit("new URL($0, import.meta.url)")>]
let private beside (relative: string) : obj = jsNative

let private corpusPath (file: string) =
    beside ("../../../" + CorpusDir + "/" + file)

let private readCorpus (file: string) : string list =
    corpusLines (readFileSync (corpusPath file, "utf8"))

let private ringCases () =
    readCorpus RingCorpusFile |> List.map parseRingCase

let private diffCases () =
    readCorpus DiffCorpusFile |> List.map parseDiffCase

let tests =
    testList "Phase 788 - the proved Elmish runtime as oracle (Fable)" [

        testCase "the corpus resolved and is not close to empty" (fun () ->
            // The non-vacuity floor: every case below would pass by doing
            // nothing if the path were wrong or the files empty.
            Expect.isTrue (existsSync (corpusPath RingCorpusFile)) $"no ring corpus at {CorpusDir}/{RingCorpusFile}"
            Expect.isTrue (existsSync (corpusPath DiffCorpusFile)) $"no diff corpus at {CorpusDir}/{DiffCorpusFile}"
            Expect.isTrue (List.length (ringCases ()) >= 150) $"only {List.length (ringCases ())} ring case(s)"
            Expect.isTrue (List.length (diffCases ()) >= 300) $"only {List.length (diffCases ())} diff case(s)")

        testCase "the transpiled ring agrees with the proved model over the corpus sequences" (fun () ->
            let mismatches =
                ringCases ()
                |> List.choose (fun c ->
                    describeRingMismatch c.Capacity c.Ops (productionRing c.Capacity c.Ops) c.Expected)

            Expect.isEmpty
                mismatches
                ($"{List.length mismatches} sequence(s) on which the transpiled ring and the proved model popped differently: "
                 + String.concat " | " (List.truncate 5 mismatches)))

        testCase "the transpiled diff and change agree with the proved model over the corpus inputs" (fun () ->
            let mismatches =
                diffCases ()
                |> List.collect (fun c -> describeDiffMismatch c.Input (productionDiffShape c.Input) c.Expected)

            Expect.isEmpty
                mismatches
                ($"{List.length mismatches} input(s) on which the transpiled diff and the proved model differed: "
                 + String.concat " | " (List.truncate 5 mismatches)))

        testCase "the corpus exercised the shortcut and the duplicate path" (fun () ->
            let classified = diffCases () |> List.map (fun c -> classifyDiffInput c.Input)
            let fastPath = classified |> List.filter fst |> List.length
            let withDupes = classified |> List.filter snd |> List.length
            Expect.isTrue (fastPath > 30) $"only {fastPath} inputs hit the shortcut"
            Expect.isTrue (withDupes > 30) $"only {withDupes} inputs carried a duplicate key")

        testCase "a ring that skips the wrap check is caught - go-red" (fun () ->
            let caught =
                ringCases ()
                |> List.filter (fun c -> brokenRing c.Capacity c.Ops <> c.Expected)
                |> List.length

            Expect.isTrue (caught > 0) "the broken ring was never caught")

        testCase "a diff that starts a key it also keeps is caught - go-red" (fun () ->
            let caught =
                diffCases ()
                |> List.filter (fun c -> brokenDiffShape c.Input <> c.Expected)
                |> List.length

            Expect.isTrue (caught > 0) "the broken diff was never caught")
    ]