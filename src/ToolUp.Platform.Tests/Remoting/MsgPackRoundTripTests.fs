// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 784.B / 784.C / 784.E / 784.F — the MsgPack half of the remoting
/// wire differential corpus.
///
/// ─── Read this first: the suite MEASURES before it asserts ───────────
///
/// On its first run this corpus found that the shipped MsgPack WRITER was
/// corrupt under the build `verify.ps1` produces. `Write.fs` took its
/// scratch buffers from a `Span` over `NativePtr.stackalloc` returned by
/// an `inline` helper; with the F# optimiser off the `inline` is not
/// honoured, the allocation lands in the helper's own frame, and the
/// caller reads it back after that frame has been popped and reused. The
/// writer then stopped being a function of its input — two runs of
/// `writeString "hello"` emitted two different five-byte payloads under a
/// correct length header, and `1234.56m` decoded as `123456M` because the
/// scale word was lost. Measured `-c Debug` corrupt, `-c Release`
/// correct, `-c Debug -p:Optimize=true` CORRECT, so the discriminator was
/// the optimiser: production shipped Release and was unaffected, while
/// every Debug test run in this repository was exchanging corrupt
/// payloads with nothing looking.
///
/// **It is FIXED** — all three call sites now allocate their own buffer —
/// and the machinery below stays as the regression guard for the class.
/// The unsound helper is still public surface, so a new call site would
/// reintroduce it silently, in Debug only, under a correct header; this
/// probe is the only thing in the repository that would notice.
///
/// A corpus cannot pin bytes a writer does not emit deterministically,
/// and a suite that quietly skipped its main arm would be the
/// vacuous-green shape this estate keeps being bitten by. So:
///
///   * `WireCorpus.writerRegime` probes five known values and classifies
///     the build into `Sound`, the measured `UnoptimisedStackalloc`
///     signature, or `UnknownRegime`.
///   * Under `Sound` every arm runs: round trip, generated shapes, and
///     the committed byte pin.
///   * `UnoptimisedStackalloc` means the defect has RETURNED. The suite
///     asserts its exact signature IN BOTH DIRECTIONS (short string and
///     decimal corrupt; long string, Guid and integer correct), stands
///     the write-side arms down rather than pinning noise, and reports it
///     on every run. This is the path the corpus ran on before the fix,
///     kept because it is what makes the guard self-retiring in both
///     directions rather than a comment nobody reads.
///   * `UnknownRegime` is a red run, because a defect that has changed
///     shape is not a defect anybody has measured.
///   * The READER is unaffected in both regimes, so the arm that decodes
///     the committed fixtures and compares them to the declared values
///     runs unconditionally. That arm is the corpus's real content here,
///     and it is also what the Fable parity leg asserts, so the two hosts
///     are still held to one contract.
///
/// ─── What is asserted, when the regime allows it ─────────────────────
///
///   1. **Round trip.** `write >> read = id` over every pinned and every
///      generated case, compared AT THE STATIC TYPE (see `WireCorpus`).
///   2. **The falsifier, in both directions.** An `int64` that does not
///      fit in an `int32` is read back at `int32` and the comparison must
///      REPORT it — paired with the control that the same bytes read at
///      `int64` fall silent. A one-directional probe can report the
///      opposite of the truth. The reader narrows with an UNCHECKED
///      conversion, so the misread produces `-2147483648` rather than an
///      error; this is exactly why the comparison is type-exact.
///   3. **The pin.** Each pinned case's bytes are committed under
///      `tests/remoting-corpus/`. A writer change re-pins DELIBERATELY,
///      through `TOOLUP_REMOTING_CORPUS_REFRESH=1`, and the diff is the
///      review.
///   4. **Adequacy.** The run reports draws per class and fails on any
///      class that drew zero. A green run over an empty class measures
///      nothing.
module ToolUp.Platform.Tests.Remoting.MsgPackRoundTripTests

open System
open System.IO
open Expecto
open ToolUp.Remoting.MsgPack
open ToolUp.Platform.Tests.Remoting.WireCorpus

/// Is the writer trustworthy in this process? Consulted by every arm
/// that writes; never by an arm that only reads.
let private writerIsSound () = writerRegime.Value = Sound

/// The one line a quarantined arm prints. Never bare "skipped": a reader
/// who meets this in a log has to be able to tell a measured quarantine
/// from a test that does nothing — the `writer regime` case above prints
/// the full account once, and this points at it.
let private blocked (arm: string) =
    sprintf
        "%s NOT RUN — the MsgPack writer is not a function of its input under this build; see the `writer regime` case in this suite for the measurement."
        arm

/// Round-trip one case, returning the comparison verdict.
let private roundTrip (c: WireCase) =
    let bytes = c.WriteMsgPack()

    let decoded =
        try
            Ok(readMsgPack c bytes)
        with ex ->
            Error(sprintf "the reader threw: %s | bytes: %s" ex.Message (describeBytes bytes))

    match decoded with
    | Error problem -> Error problem
    | Ok value ->
        match c.Compare value with
        | Ok() -> Ok()
        | Error problem -> Error(sprintf "%s | bytes: %s" problem (describeBytes bytes))

// ─── The regime probe — always runs, and is the phase's finding ──────

let private regime =
    testList "writer regime" [
        testCase "the writer is in a regime this corpus has measured"
        <| fun () ->
            let probe = probeWriter ()
            printfn "%s" (regimeReport (regimeOf probe))

            match regimeOf probe with
            | Sound -> ()
            | UnoptimisedStackalloc ->
                // The quarantine is only honest if its shape is asserted
                // rather than assumed. These five are the measurement,
                // in both directions: two things that must be broken and
                // three that must not. A fix flips the first two and the
                // regime becomes `Sound` on the next run.
                Expect.isFalse
                    probe.ShortStringExact
                    "a short string is now written correctly but the decimal is not — the regime has changed shape."

                Expect.isFalse
                    probe.DecimalExact
                    "a decimal is now written correctly but the short string is not — the regime has changed shape."

                Expect.isTrue
                    probe.LongStringExact
                    "a 600-character string is ALSO corrupt. That string takes `writeString`'s ArrayPool branch rather than the stack one, so this is a different defect from the one recorded here."

                Expect.isTrue
                    probe.GuidExact
                    "a Guid is ALSO corrupt. `writeGuid` reads its stack buffer with no intervening call, which is why it survived the recorded defect; if it is failing now, the fault has moved."

                Expect.isTrue
                    probe.IntegerExact
                    "an integer is corrupt. Integers touch no scratch buffer at all, so this is not the recorded defect."
            | UnknownRegime probe -> failtest (regimeReport (UnknownRegime probe))

        testCase "the regime classifier itself discriminates"
        <| fun () ->
            // The classifier is what every other arm keys off, so it gets
            // its own falsifier: a hand-built probe of each shape must
            // land in the class it names, and an all-true probe must NOT
            // read as the defect.
            let allTrue = {
                ShortStringExact = true
                LongStringExact = true
                DecimalExact = true
                GuidExact = true
                IntegerExact = true
            }

            let defect = {
                allTrue with
                    ShortStringExact = false
                    DecimalExact = false
            }

            Expect.equal (regimeOf allTrue) Sound "a fully-correct writer did not classify as sound."

            Expect.equal
                (regimeOf defect)
                UnoptimisedStackalloc
                "the recorded defect's own signature did not classify as the recorded defect."

            match regimeOf { allTrue with GuidExact = false } with
            | UnknownRegime _ -> ()
            | other ->
                failtestf
                    "a writer broken in a way the corpus has NOT measured classified as `%A` instead of unknown; the quarantine would then cover a defect nobody has looked at."
                    other
    ]

// ─── Round trip (784.B) ──────────────────────────────────────────────

let private pinnedRoundTrip =
    testList "pinned" [
        for c in pinnedCases ->
            testCase c.Name
            <| fun () ->
                if not (writerIsSound ()) then
                    printfn "%s" (blocked ("round trip for `" + c.Name + "`"))
                else
                    match roundTrip c with
                    | Ok() -> ()
                    | Error problem -> failtestf "MsgPack round trip failed for `%s` (%A): %s" c.Name c.Class problem
    ]

/// Generated cases are grouped BY SHAPE rather than flattened, so a
/// failure can report the smallest size at which the shape still fails —
/// the shrink this corpus offers (see `WireCorpus.sizes`).
let private generatedRoundTrip =
    testList "generated" [
        for shape in shapeGenerators ->
            testCase shape.ShapeName
            <| fun () ->
                if not (writerIsSound ()) then
                    printfn "%s" (blocked ("generated shape `" + shape.ShapeName + "`"))
                else

                    let failing =
                        sizes
                        |> List.tryPick (fun size ->
                            let drawn = shape.Draw (seed ()) size

                            match roundTrip drawn with
                            | Ok() -> None
                            | Error problem -> Some(size, drawn, problem))

                    match failing with
                    | None -> ()
                    | Some(size, drawn, problem) ->
                        let smallest =
                            shrinkSize shape (fun c ->
                                match roundTrip c with
                                | Ok() -> false
                                | Error _ -> true)

                        failtestf
                            "MsgPack round trip failed for generated shape `%s` at size %d (case `%s`): %s\nSmallest failing size: %s. Reproduce with `$env:%s = '%d'` — the draw is a pure function of (seed, size)."
                            shape.ShapeName
                            size
                            drawn.Name
                            problem
                            (match smallest with
                             | Some s -> string s
                             | None ->
                                 "none — the failure did not reproduce, which means the draw is not deterministic and THAT is the defect")
                            SeedVariable
                            (seed ())
    ]

// ─── The falsifier and its control ───────────────────────────────────

/// One past `Int32.MaxValue`: the smallest `int64` whose `int32`
/// truncation is a different number rather than the same one.
///
/// Until Phase 802 `writeInt64` compacted it into a `Uint32` — bytes
/// indistinguishable from a well-formed `int32 -2147483648` — so no
/// reader could refuse it, and the refusal arm had to use the value
/// below. Since 802 the writer keeps a positive value's top bit clear,
/// so this one leaves as a `Uint64` and is refused like any other.
let private beyondInt32 = 2147483648L

/// Past 32 bits in the VALUE's magnitude as well: 2^32 has a non-zero
/// byte above the low four, so it was a `Uint64` before Phase 802 too.
/// Kept as the arm that was refusable under both writers.
let private unambiguouslyWide = 4294967296L

let private narrowingFalsifier =
    testList "narrowing falsifier" [
        testCase "an int64 payload WIDER than 32 bits read at int32 is REFUSED"
        <| fun () ->
            // Integers do not touch the corrupt scratch-buffer path, so
            // this arm is live in BOTH regimes — which matters, because
            // it is the acceptance criterion's own go-red case.
            //
            // This arm used to assert the opposite, and said so: its
            // precondition read "the reader narrows with an UNCHECKED
            // conversion … if this line fails the reader has started
            // refusing". It has — Phase 786 made the narrowing a named
            // refusal — so the arm now pins the refusal. It also had to
            // change its VALUE, and the reason is the arm below.
            let declared = both WireClass.NumericWidth "falsifier-int64-wide" unambiguouslyWide
            let bytes = declared.WriteMsgPack()

            match Read.Reader(bytes).TryRead typeof<int32> with
            | Ok value ->
                failtestf
                    "the reader narrowed an int64 payload into an int32 and produced %A. That is the silent wrong ANSWER this row exists to catch."
                    value
            | Error error ->
                Expect.equal error.Expected "Int32" "the refusal names the target width that could not hold the value"

        testCase "and the value one past Int32.MaxValue is refused too — the emitter no longer flattens it"
        <| fun () ->
            // Phase 802's go-red case. Before it, this arm asserted the
            // opposite: `writeInt64` compacted 2147483648L into
            // `Uint32 80 00 00 00`, byte for byte a legitimate
            // `int32 -2147483648`, so the narrowing was real and
            // unrefusable at any reader. The emitter now keeps a positive
            // value's top bit clear, which moves this value into a 64-bit
            // format the width rule can measure.
            let declared = both WireClass.NumericWidth "falsifier-int64" beyondInt32
            let bytes = declared.WriteMsgPack()

            Expect.equal
                bytes
                [| 0xcfuy; 0uy; 0uy; 0uy; 0uy; 0x80uy; 0uy; 0uy; 0uy |]
                "2147483648L must leave as a Uint64; a 32-bit form is the ambiguity Phase 802 closed"

            match Read.Reader(bytes).TryRead typeof<int32> with
            | Ok value ->
                failtestf
                    "the reader produced %A from 2147483648L read at int32. The emitter is flattening across the sign boundary again."
                    value
            | Error error -> Expect.equal error.Expected "Int32" "the refusal names the target width"

        testCase "while the PRE-802 bytes for that value still decode — the reader did not change"
        <| fun () ->
            // The other half of the compatibility claim. An older writer
            // still emits `Uint32 80 00 00 00`, and the reader still
            // admits a same-width source, so a peer on the older writer
            // reads exactly what it read before. Refusing these bytes
            // would be the wire break; Phase 802 deliberately does not.
            let legacy = [| 0xceuy; 0x80uy; 0uy; 0uy; 0uy |]

            Expect.equal
                (Read.Reader(legacy).Read typeof<int32> :?> int32)
                Int32.MinValue
                "the pre-802 form is a legitimate int32 -2147483648 at an int32 target"

        testCase "a narrowed value is REPORTED by the comparison, not accepted"
        <| fun () ->
            // The claim this list was built for, now that the reader
            // cannot be used to manufacture the wrong-typed value: the
            // corpus comparison is TYPE-EXACT, so an int32 offered against
            // an int64 declaration is a mismatch even when the numbers
            // would compare equal after boxing or rendering.
            let declared = both WireClass.NumericWidth "falsifier-int64" beyondInt32

            match declared.Compare(box Int32.MinValue) with
            | Error _ -> ()
            | Ok() ->
                failtest
                    "the corpus comparison ACCEPTED an int32 against an int64 declaration. This is the defect the type-exact comparison exists to catch; a boxed or rendered comparison would land here."

        testCase "the control: the same bytes read at int64 fall silent"
        <| fun () ->
            let declared = both WireClass.NumericWidth "falsifier-int64" beyondInt32
            let bytes = declared.WriteMsgPack()
            let correct = Read.Reader(bytes).Read typeof<int64>

            match declared.Compare correct with
            | Ok() -> ()
            | Error problem ->
                failtestf
                    "the control arm failed: a correctly-read value was reported as a mismatch (%s). A falsifier whose control also fails proves nothing about the falsifier."
                    problem

        testCase "a wrong VALUE at the right type is also reported"
        <| fun () ->
            // The second direction the comparison has to get right: same
            // type, different value. Without this, a comparison that only
            // ever checked types would pass the arm above.
            let declared = both WireClass.NumericWidth "falsifier-value" 7L
            let other = (both WireClass.NumericWidth "other" 8L).WriteMsgPack()
            let decoded = Read.Reader(other).Read typeof<int64>

            match declared.Compare decoded with
            | Error _ -> ()
            | Ok() -> failtest "the corpus comparison accepted 8L where 7L was declared."
    ]

// ─── The pinned fixture set (784.C, .NET half) ───────────────────────

let private fixturePin =
    testList "pinned fixtures" [
        testCase "the corpus directory resolves inside the RUNNING checkout"
        <| fun () ->
            let dir = corpusDirectory ()

            Expect.isTrue
                (Directory.Exists dir)
                (sprintf
                    "the committed corpus directory is missing at `%s`. It is tracked in this repository, so this is a checkout or a path-arithmetic problem, not a missing environment variable."
                    dir)

            Expect.isFalse
                (resolvesInsideAForeignWorktree ())
                (sprintf
                    "the corpus resolved to `%s`, which is inside a DIFFERENT working tree of this repository. Phase 735 records why that is never acceptable: a sibling worktree's transient contents would be certified against instead of this checkout's."
                    dir)

        yield! [
            for c in pinnedCases ->
                testCase (c.Name + " — bytes are what the writer emits today")
                <| fun () ->
                    if not (writerIsSound ()) then
                        printfn "%s" (blocked ("byte pin for `" + c.Name + "`"))
                    else

                        let path = msgPackFixturePath c
                        let emitted = c.WriteMsgPack()

                        if refreshRequested () then
                            Directory.CreateDirectory(corpusDirectory ()) |> ignore
                            File.WriteAllBytes(path, emitted)
                        else
                            Expect.isTrue
                                (File.Exists path)
                                (sprintf
                                    "no committed fixture at `%s`. Re-pin with `$env:%s = '1'` and commit the result — a fixture that materialises itself on a green run pins nothing."
                                    path
                                    RefreshVariable)

                            let committed = File.ReadAllBytes path

                            if committed <> emitted then
                                failtestf
                                    "the MsgPack writer no longer emits the committed bytes for `%s`.\n  committed: %s\n  emitted:   %s\nIf the change is INTENDED, re-pin with `$env:%s = '1'` and review the diff; if it is not, the writer has regressed."
                                    c.Name
                                    (describeBytes committed)
                                    (describeBytes emitted)
                                    RefreshVariable
        ]

        // The READER arm. Unconditional: the reader is sound in both
        // regimes, so this is the claim that never degrades — and it is
        // the same claim the Fable leg makes over the same bytes, which
        // is what holds the two hosts to one contract.
        yield! [
            for c in pinnedCases ->
                testCase (c.Name + " — the committed bytes decode to the declared value")
                <| fun () ->
                    let path = msgPackFixturePath c

                    if File.Exists path then
                        let committed = File.ReadAllBytes path

                        let decoded =
                            try
                                Ok(readMsgPack c committed)
                            with ex ->
                                Error(sprintf "the reader threw: %s" ex.Message)

                        match decoded |> Result.bind c.Compare with
                        | Ok() -> ()
                        | Error problem ->
                            failtestf
                                "the committed fixture for `%s` does not decode to the declared value: %s. This is the half a writer-only pin cannot make — the bytes being stable says nothing about them being right."
                                c.Name
                                problem
                    elif refreshRequested () then
                        ()
                    else
                        failtestf
                            "no committed fixture at `%s`. Fixtures are pinned from a build where the writer is SOUND (`-c Release`); see the regime note in this file's header."
                            path
        ]

        testCase "no orphan fixtures"
        <| fun () ->
            let dir = corpusDirectory ()

            if Directory.Exists dir then
                let expected = pinnedCases |> List.map (fun c -> c.Name + ".msgpack") |> Set.ofList

                let orphans =
                    Directory.GetFiles(dir, "*.msgpack")
                    |> Array.map Path.GetFileName
                    |> Array.filter (fun f -> not (expected.Contains f))

                Expect.isEmpty
                    orphans
                    (sprintf
                        "fixture file(s) with no declared case: %s. A renamed or deleted case leaves its bytes behind, and an orphan fixture is one nothing verifies — delete it in the same commit as the rename."
                        (String.Join(", ", orphans)))

        testCase "every pinned case has a committed fixture"
        <| fun () ->
            // The other direction, and the one that stops the arm above
            // from passing over an empty directory: a case with no file
            // is a case the reader arm cannot check.
            let missing =
                pinnedCases
                |> List.filter (fun c -> not (File.Exists(msgPackFixturePath c)))
                |> List.map (fun c -> c.Name)

            Expect.isEmpty
                missing
                (sprintf
                    "pinned case(s) with no committed MsgPack fixture: %s. Re-pin from a build where the writer is sound and commit the files."
                    (String.Join(", ", missing)))
    ]

// ─── Adequacy (784.F) ────────────────────────────────────────────────

let private adequacy =
    testList "adequacy" [
        testCase "every declared class drew at least one pinned case"
        <| fun () ->
            let report = adequacyReport "pinned" pinnedCases
            printfn "%s" report

            Expect.isEmpty
                (emptyClasses pinnedCases)
                (sprintf
                    "these classes drew ZERO pinned cases, so the suite's green says nothing about them: %A. %s"
                    (emptyClasses pinnedCases)
                    report)

        testCase "every declared class drew at least one generated case"
        <| fun () ->
            let generated = generatedCases ()
            let report = adequacyReport "generated" generated
            printfn "%s" report

            Expect.isEmpty
                (emptyClasses generated)
                (sprintf "these classes drew ZERO generated cases: %A. %s" (emptyClasses generated) report)

        testCase "the adequacy guard itself goes red on an empty class"
        <| fun () ->
            // The falsifier for the guard: a population that deliberately
            // omits a class must be REPORTED. Without this the guard
            // could be quantifying over an empty `allClasses` and still
            // report green — the vacuity the Phase 722 registration guard
            // had to add a floor for.
            let missingOne = pinnedCases |> List.filter (fun c -> c.Class <> WireClass.MapSet)

            Expect.equal
                (emptyClasses missingOne)
                [ WireClass.MapSet ]
                "the adequacy guard did not report a class removed from the population; it is not measuring what it claims to."

            Expect.isNonEmpty
                allClasses
                "the class vocabulary is EMPTY, so every adequacy check above is vacuously green."

        testCase "the generator is deterministic in (seed, size)"
        <| fun () ->
            // The reproduction story printed in every generated failure
            // message is only true if this holds. Asserted rather than
            // assumed: a generator that drew from ambient state would
            // print a seed nobody could use.
            //
            // Compared on the declared VALUES rather than on emitted
            // bytes, so the check is independent of the writer's regime.
            for shape in shapeGenerators do
                for size in sizes do
                    let first = shape.Draw DefaultSeed size
                    let second = shape.Draw DefaultSeed size

                    match first.Compare second.Value with
                    | Ok() -> ()
                    | Error problem ->
                        failtestf
                            "shape `%s` at size %d drew different values on two invocations with the same seed (%s). Every 'reproduce with this seed' message this corpus prints is then a lie."
                            shape.ShapeName
                            size
                            problem
    ]

/// The cross-host divergence register. Not a skip list: each entry is a
/// MEASURED difference between the two hosts' readers, and printing them
/// on every run is what keeps the list from quietly growing.
let private divergences =
    testCase "recorded cross-host divergences"
    <| fun () ->
        for name, reason in recordedDivergences do
            printfn "cross-host divergence — %s: %s" name reason

        printfn "cross-host fixtures: %d of %d pinned case(s)" (List.length crossHostCases) (List.length pinnedCases)

        Expect.isNonEmpty
            crossHostCases
            "no case is in the cross-host set, so the Fable parity leg would be vacuously green."

// ─── Refuse-path mutations (784.D) ───────────────────────────────────

/// The corpus's other half: what each decoder does with a payload that
/// does NOT encode a value of the target type. Phase 783 gave the reader
/// `TryRead`, so there is now a stated right answer to hold it to.
///
/// Each mutation declares its measured outcome CLASS and this arm asserts
/// it. That is deliberately not the same as asserting every mutation is
/// refused: several are ACCEPTED today, and the most important row in the
/// list — an int64 payload read at int32 — is accepted with a silently
/// narrowed value rather than refused. Writing those down as `Accepted`
/// with their reason is what makes them findings instead of omissions,
/// and it is what makes this arm go red the day Phase 785's closed
/// algebra turns one of them into a refusal.
let private refusals =
    testList "refuse-path mutations" [
        yield! [
            for m in mutations () do
                match m.MsgPack with
                | None -> ()
                | Some payload ->
                    testCase (m.Name + " (" + string m.Kind + ")")
                    <| fun () ->
                        let actual, detail = classifyMsgPack m.Target payload
                        printfn "msgpack refusal — %s: %s | %s" m.Name (describeOutcome actual) detail

                        if not (sameOutcomeClass actual m.ExpectedMsgPack) then
                            failtestf
                                "the MsgPack decoder's behaviour on mutation `%s` has changed class.\n  declared: %s\n  measured: %s (%s)\nIf the decoder was IMPROVED, update the declaration in `WireCorpus.mutations` — that is the whole point of this list going red. If it was not, a refusal has been lost."
                                m.Name
                                (describeOutcome m.ExpectedMsgPack)
                                (describeOutcome actual)
                                detail
        ]

        testCase "every mutation kind is represented"
        <| fun () ->
            let drawn = mutations () |> List.map (fun m -> m.Kind) |> List.distinct

            let missing = allMutationKinds |> List.filter (fun k -> not (List.contains k drawn))

            Expect.isEmpty
                missing
                (sprintf "these mutation kinds drew nothing, so the refuse-path arm says nothing about them: %A" missing)

        testCase "exactly these MsgPack mutations are refused — the boundary, measured"
        <| fun () ->
            // The headline result of the refuse-path arm, asserted rather
            // than left to be inferred from eleven individual cases.
            //
            // This case asserted ABSENCE when it was written: Phase 783
            // had given the reader a named refusal and `TryRead` returned
            // `Result<obj, DecodeError>`, and over a real
            // malformed-payload population NOT ONE mutation produced a
            // `DecodeError`. It said, in its own failure message, that
            // the day one closed it should be replaced by the set. Phase
            // 786 closed two, so this is that replacement, and the set is
            // now the tripwire in BOTH directions: a refusal gained is
            // progress to record here, a refusal lost is a regression.
            //
            // What is still open is the shape 783 named: refusals are
            // raised where the decoder KNOWS it is refusing, and the
            // remaining paths do not know — they index, cast and look up
            // on the assumption that the payload is well formed.
            // `KeyNotFoundException` from `interpretStringAs` looking a
            // string up as a union case name, `IndexOutOfRangeException`
            // reading a record's third field off a two-element array,
            // `ArgumentException` asking `FSharpType.GetUnionCases` about
            // `int32`. Closing those is the content of Phase 785's closed
            // algebra.
            //
            // Phase 802 gained `wrong-width-int64-into-int32`. It was
            // unrefusable at the reader while `writeInt64` compacted the
            // value into an encoding a legitimate negative `int32` also
            // used; it needed the emitter, not the algebra, and the
            // emitter now keeps a positive value's top bit clear.
            let refused =
                mutations ()
                |> List.filter (fun m ->
                    match m.MsgPack, m.ExpectedMsgPack with
                    | Some _, Refused -> true
                    | _ -> false)
                |> List.map (fun m -> m.Name)
                |> List.sort

            let expected = [
                "truncated-record-body"
                "truncated-string-header"
                "wrong-width-int64-into-int32"
            ]

            Expect.equal
                refused
                expected
                "the set of MsgPack mutations declared REFUSED has changed. If a refusal was GAINED, record it here — that is this case doing its job. If one was LOST, a guard has regressed."

        testCase "and it is not vacuous: the population is non-empty and every kind is drawn"
        <| fun () ->
            // The case above asserts a SET drawn from the declarations,
            // which an empty mutation list would satisfy nearly as
            // trivially as the absence it used to assert. This is the
            // floor under it.
            let withPayload = mutations () |> List.filter (fun m -> m.MsgPack.IsSome)

            Expect.isTrue
                (List.length withPayload >= List.length allMutationKinds)
                (sprintf
                    "only %d MsgPack mutation(s) are declared against %d kinds; the absence asserted above would be close to vacuous."
                    (List.length withPayload)
                    (List.length allMutationKinds))
    ]

// ─── Generated mutation population (Phase 844 ↔ 913) ─────────────────

/// The per-shape GENERATED population (`WireCorpus.generatedMutations`),
/// measured here against the MsgPack reader — the msgpack twin of
/// `StjRoundTripTests.generatedShapeMutations`. Only rows carrying an
/// `MsgPack` payload are exercised: `generatedMutationsFor`'s own doc
/// comment records that `WrongWidth` / `MissingField` / `ExtraField` are
/// JSON-only BY DESIGN — MsgPack widths are typed bytes rather than text,
/// so "a non-integral token where an integer was declared" has no MsgPack
/// analogue, and a generic field splice would need a per-shape rewrite of
/// the array/map length header this generator does not attempt. The
/// hand-written rows in `refusals` above stay the authoritative MsgPack
/// coverage for those three kinds; this arm draws `WrongTag` and
/// `Truncated` only.
let private generatedRefusals =
    testList "generated refuse-path mutations" [
        yield! [
            for m in generatedMutations pinnedCases do
                match m.MsgPack with
                | None -> ()
                | Some payload ->
                    testCase (m.Name + " (" + string m.Kind + ")")
                    <| fun () ->
                        let actual, detail = classifyMsgPack m.Target payload
                        printfn "msgpack refusal (generated) — %s: %s | %s" m.Name (describeOutcome actual) detail

                        if not (sameOutcomeClass actual m.ExpectedMsgPack) then
                            failtestf
                                "the MsgPack decoder's behaviour on generated mutation `%s` has changed class.\n  declared: %s\n  measured: %s (%s)\nIf the decoder was IMPROVED, update the declaration in `WireCorpus.generatedMutationsFor`; if it was not, a refusal has been lost."
                                m.Name
                                (describeOutcome m.ExpectedMsgPack)
                                (describeOutcome actual)
                                detail
        ]

        testCase "the generated set is not vacuous, and covers every kind it can generate for MsgPack"
        <| fun () ->
            let withPayload =
                generatedMutations pinnedCases |> List.filter (fun m -> m.MsgPack.IsSome)

            Expect.isNonEmpty withPayload "no generated mutation carries an MsgPack payload"

            let kinds = withPayload |> List.map (fun m -> m.Kind) |> List.distinct

            // The generator's own boundary (see the doc comment above):
            // WrongWidth / MissingField / ExtraField have no MsgPack
            // analogue in the GENERATED population, so this checks the two
            // kinds it does draw rather than all five — the other three
            // are asserted against `mutations()`'s hand-written MsgPack
            // rows by "every mutation kind is represented" above.
            for kind in [ MutationKind.WrongTag; MutationKind.Truncated ] do
                Expect.contains kinds kind (sprintf "no generated MsgPack mutation of kind %A" kind)

        testCase "every mutation kind is represented across hand-written and generated MsgPack mutations"
        <| fun () ->
            // The combined floor: the hand-written arm alone already
            // covers all five kinds on MsgPack (`refusals`'s "every
            // mutation kind is represented" above), and this re-asserts
            // that fact jointly with the generated population so a future
            // change to either declaration cannot silently narrow the
            // union without a test noticing.
            let drawn =
                (mutations () |> List.filter (fun m -> m.MsgPack.IsSome))
                @ (generatedMutations pinnedCases |> List.filter (fun m -> m.MsgPack.IsSome))
                |> List.map (fun m -> m.Kind)
                |> List.distinct

            let missing = allMutationKinds |> List.filter (fun k -> not (List.contains k drawn))

            Expect.isEmpty
                missing
                (sprintf "these mutation kinds drew zero MsgPack-payload mutations across BOTH populations: %A" missing)
    ]

// ─── Emitter width discipline (Phase 802) ────────────────────────────

/// Every width a target can be declared at, as a reader target type.
let private integerTargets: Type list = [
    typeof<sbyte>
    typeof<byte>
    typeof<int16>
    typeof<uint16>
    typeof<int32>
    typeof<uint32>
    typeof<int64>
    typeof<uint64>
]

/// The boundary values of every width, either side of each sign and
/// magnitude edge — the values a compacting writer gets wrong.
let private signedProbes: int64 list = [
    0L
    1L
    127L
    128L
    255L
    256L
    32767L
    32768L
    65535L
    65536L
    2147483647L
    2147483648L
    4294967295L
    4294967296L
    Int64.MaxValue
    -1L
    -31L
    -32L
    -33L
    -128L
    -129L
    -32768L
    -32769L
    -2147483648L
    -2147483649L
    Int64.MinValue
]

let private unsignedProbes: uint64 list = [
    yield! signedProbes |> List.filter (fun v -> v >= 0L) |> List.map uint64
    9223372036854775808UL
    UInt64.MaxValue
]

/// Every (source, target) pair at which the writer's bytes decode to a
/// DIFFERENT number than the one written. Refusals are fine — that is the
/// point; only a silently different value is collected.
let private misreadings () =
    let probe (source: string) (value: decimal) (bytes: byte[]) =
        integerTargets
        |> List.choose (fun target ->
            match Read.Reader(bytes).TryRead target with
            | Ok decoded when Convert.ToDecimal decoded <> value ->
                Some(sprintf "%s %M at %s read as %O" source value target.Name decoded)
            | _ -> None)

    [
        for v in signedProbes do
            yield! probe "int64" (decimal v) ((both WireClass.NumericWidth "probe" v).WriteMsgPack())
        for v in unsignedProbes do
            yield! probe "uint64" (decimal v) ((both WireClass.NumericWidth "probe" v).WriteMsgPack())
        for v in [ SByte.MinValue; -1y; SByte.MaxValue ] do
            yield! probe "sbyte" (decimal v) ((both WireClass.NumericWidth "probe" v).WriteMsgPack())
        for v in [ 128uy; Byte.MaxValue ] do
            yield! probe "byte" (decimal v) ((both WireClass.NumericWidth "probe" v).WriteMsgPack())
        for v in [ Int16.MinValue; Int16.MaxValue ] do
            yield! probe "int16" (decimal v) ((both WireClass.NumericWidth "probe" v).WriteMsgPack())
        for v in [ UInt16.MaxValue ] do
            yield! probe "uint16" (decimal v) ((both WireClass.NumericWidth "probe" v).WriteMsgPack())
        for v in [ Int32.MinValue; Int32.MaxValue ] do
            yield! probe "int32" (decimal v) ((both WireClass.NumericWidth "probe" v).WriteMsgPack())
        for v in [ UInt32.MaxValue ] do
            yield! probe "uint32" (decimal v) ((both WireClass.NumericWidth "probe" v).WriteMsgPack())
    ]

let private emitterDiscipline =
    testList "emitter width discipline (Phase 802)" [
        testCase "no integer the writer emits reads as a different value at any width the reader accepts"
        <| fun () ->
            // The acceptance criterion, asserted over every width's
            // boundary values at every integer target. The ONE expected
            // misreading is the format's own limit: a uint64 above
            // Int64.MaxValue has no wider format, so at an int64 target it
            // is a same-width reinterpretation. It is asserted as a SET so
            // that a new misreading and a vanished residual both go red.
            let expectedResidual = [
                "uint64 9223372036854775808 at Int64 read as -9223372036854775808"
                "uint64 18446744073709551615 at Int64 read as -1"
            ]

            Expect.equal
                (misreadings ())
                expectedResidual
                "the writer emitted an integer that a reader accepts as a DIFFERENT value. Every format must keep its top bit clear unless no wider format exists - see Format.fs's Phase 802 header."

        testCase "and never emits Uint8 or Int16 for an integer"
        <| fun () ->
            // Two formats the rule leaves out on purpose (Format.fs's
            // Phase 802 header): `Uint8` because 128..255 sets an int8's
            // sign bit, `Int16` because a pre-802 Fable reader does not
            // sign-extend it. The .NET reader reads both correctly, so the
            // misreading probe above cannot see a regression to either;
            // this case can.
            let firstBytes =
                [
                    for v in signedProbes -> (both WireClass.NumericWidth "probe" v).WriteMsgPack()
                    for v in unsignedProbes -> (both WireClass.NumericWidth "probe" v).WriteMsgPack()
                    for v in [ Int16.MinValue; -200s; -129s ] -> (both WireClass.NumericWidth "probe" v).WriteMsgPack()
                    for v in [ 128uy; Byte.MaxValue ] -> (both WireClass.NumericWidth "probe" v).WriteMsgPack()
                ]
                |> List.map (fun bytes -> bytes[0])

            Expect.isFalse (List.contains Format.Uint8 firstBytes) "an integer left as Uint8"
            Expect.isFalse (List.contains Format.Int16 firstBytes) "an integer left as Int16"

        testCase "and the probe is not vacuous: a compacting writer's bytes ARE misread"
        <| fun () ->
            // Make the probe fail once. The pre-802 form of 2147483648L
            // decodes at int32 as a different number; if this ever stops
            // being true the reader changed, and the assertion above no
            // longer measures the writer.
            match Read.Reader([| 0xceuy; 0x80uy; 0uy; 0uy; 0uy |]).TryRead typeof<int32> with
            | Ok decoded -> Expect.notEqual (Convert.ToDecimal decoded) 2147483648M "the compacted form misreads"
            | Error e ->
                failtestf
                    "the reader refused the pre-802 compacted form (%s); the compatibility leg below needs it accepted"
                    e.Found

        yield! [
            for name, legacy in preEmitterDisciplinePayloads ->
                testCase ("pre-802 bytes still decode — " + name)
                <| fun () ->
                    // The compatibility claim, measured: a peer on the
                    // older writer sends these bytes, and the unchanged
                    // reader must still produce the declared value.
                    let c =
                        match pinnedCases |> List.tryFind (fun c -> c.Name = name) with
                        | Some c -> c
                        | None -> failtestf "preEmitterDisciplinePayloads names `%s`, which is no pinned case" name

                    Expect.notEqual
                        legacy
                        (c.WriteMsgPack())
                        "a pre-802 row whose bytes the current writer still emits pins nothing; drop it"

                    match c.Compare(Read.Reader(legacy).Read c.ClrType) with
                    | Ok() -> ()
                    | Error problem ->
                        failtestf
                            "the pre-802 bytes for `%s` no longer decode to the declared value: %s. That is a wire break for every peer still on the older writer."
                            name
                            problem
        ]

        testCase "the pre-802 population is not vacuous"
        <| fun () ->
            Expect.isGreaterThanOrEqual
                (List.length preEmitterDisciplinePayloads)
                10
                "every re-pinned fixture keeps its older bytes"
    ]

[<Tests>]
let tests =
    testList "Remoting MsgPack wire corpus" [
        regime
        pinnedRoundTrip
        generatedRoundTrip
        narrowingFalsifier
        fixturePin
        refusals
        emitterDiscipline
        generatedRefusals
        adequacy
        divergences
    ]