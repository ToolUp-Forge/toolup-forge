// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 206 — the round-trip fidelity regression corpus.
///
/// `Package.openRead` → `Import` → `DocModel` → `Emit` is lossy in ways
/// that do not announce themselves: a run property bag that stops being
/// re-attached, a numbering reference that survives with the wrong
/// instance, a section's properties landing on the wrong section, a
/// revision losing its author — each of these leaves a document that
/// still opens, still reads plausibly, and is quietly wrong. Nothing in
/// the suite before this pack would have failed on any of them.
///
/// So each corpus fixture is round-tripped and pinned by three
/// committed textual goldens: the re-imported `DocModel`, the emitted
/// package's OpenXml elements, and the import's `ResidueReport`. The
/// residue baseline is the one that makes losses honest — a newly
/// dropped element either appears there (and the build fails) or is
/// absorbed silently, and there is no third option once the baseline is
/// committed.
///
/// **What this pack does NOT claim.** It pins TODAY's fidelity, not
/// ideal fidelity. Where the layer is lossy the golden records the loss
/// rather than asserting it away, so the corpus fails on DRIFT — which
/// is the regression it exists to catch — while the known losses stay
/// visible in files a reader can review. Fixing one is a change to
/// `Import` / `Emit` plus a reviewed golden regeneration, which is
/// exactly the shape it should have.
module ToolUp.OpenXml.Tests.RoundTripFidelityTests

open System
open System.IO
open Expecto
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open ToolUp.OpenXml
open ToolUp.OpenXml.Tests
open ToolUp.OpenXml.Tests.Corpus

/// One fixture, carried through the whole round trip once so every
/// case in its list asserts against the same values.
type private RoundTrip = {
    Name: string
    /// The first import — the residue baseline's subject.
    First: ImportedDocument
    /// `Emit.toBytes` of the first import's model.
    Emitted: byte[]
    /// The re-import of those bytes — the model golden's subject.
    Second: ImportedDocument
}

let private roundTrip (fixture: CorpusFixtures.Fixture) : RoundTrip =
    let first = Import.fromBytes (fixture.Build())
    let emitted = Emit.toBytes first.Model

    {
        Name = fixture.Name
        First = first
        Emitted = emitted
        Second = Import.fromBytes emitted
    }

/// Turn a golden verdict into an Expecto outcome. The message the gate
/// produced is already the whole story — a wrapper that restated it
/// would only bury the line number the diff names.
let private expectGolden (verdict: Result<unit, string>) =
    match verdict with
    | Ok() -> ()
    | Error message -> failtest message

/// OOXML schema validation, rendered while the package is still OPEN.
/// `ValidationErrorInfo.Path` reaches back through the element to its
/// part, so reading it after disposal throws `Cannot access part
/// because parent package was closed` — the exception replaces the
/// validation failure it was meant to describe. Rendering inside the
/// `use` keeps the XPath, which is what makes a schema error
/// actionable.
let private validationErrors (bytes: byte[]) =
    use stream = new MemoryStream(bytes)
    use doc = WordprocessingDocument.Open(stream, false)
    let validator = Validation.OpenXmlValidator()

    validator.Validate doc
    |> Seq.map (fun e ->
        let path =
            match e.Path with
            | null -> "(no path)"
            | p -> p.XPath

        sprintf "%s at %s: %s" e.Id path e.Description)
    // Sorted: the validator's own emission order is not part of any
    // contract, and a golden that pinned it would churn on a vendor
    // bump for a difference that says nothing about fidelity.
    |> Seq.sort
    |> List.ofSeq

let private withDocument (bytes: byte[]) (assertions: WordprocessingDocument -> unit) =
    use stream = new MemoryStream(bytes)
    use doc = WordprocessingDocument.Open(stream, false)
    assertions doc

// ─── Per-fixture legs ────────────────────────────────────────────

/// The two cases a CLEAN fixture carries: the round trip is a fixpoint,
/// and the package it produces is schema-valid.
let private cleanRoundTripCases (trip: Lazy<RoundTrip>) = [
    testCase "the round trip is stable — a second pass changes nothing"
    <| fun () ->
        // The golden pins the round trip against FUTURE drift; this
        // pins it against itself. A layer that lost a payload on every
        // pass would satisfy a golden regenerated after the loss, and
        // fail here.
        Expect.equal
            (Goldens.renderModel trip.Value.Second.Model)
            (Goldens.renderModel trip.Value.First.Model)
            "import → emit → import is a fixpoint at the model altitude"

    testCase "emitted package validates against the OOXML schema"
    <| fun () ->
        let errors = validationErrors trip.Value.Emitted

        Expect.isEmpty errors (sprintf "no OOXML validation errors, got:\n%s" (String.concat "\n" errors))
]

/// The two cases a fixture with a DECLARED defect carries instead.
/// Both assert the defect is still exactly what the declaration says,
/// so the corpus reddens in both directions: if the loss widens, and
/// if it is fixed without the declaration and goldens being updated
/// alongside. A pinned defect that could be silently repaired would be
/// a comment, not a test.
let private pinnedDefectCases (trip: Lazy<RoundTrip>) (defect: string) = [
    testCase "PINNED DEFECT — the round trip is NOT a fixpoint"
    <| fun () ->
        Expect.notEqual
            (Goldens.renderModel trip.Value.Second.Model)
            (Goldens.renderModel trip.Value.First.Model)
            (sprintf
                "This fixture pins a KNOWN defect, and the round trip just came out stable — so either the defect was fixed or the fixture stopped reaching it. If it was FIXED: delete this fixture's `KnownDefect` declaration in Corpus/CorpusFixtures.fs (which restores the ordinary clean-round-trip cases) and regenerate its goldens in the same change.\n\nThe pinned defect:\n%s"
                defect)

    testCase "PINNED DEFECT — the emitted package fails OOXML validation, exactly as recorded"
    <| fun () ->
        // The validation errors are themselves a golden: the SET of
        // schema violations is the shape of the loss, so a new
        // violation appearing beside the known one is a regression the
        // corpus must catch rather than absorb into "still invalid".
        let errors = validationErrors trip.Value.Emitted

        Expect.isNonEmpty
            errors
            (sprintf
                "This fixture pins a KNOWN schema-invalid emission and the package just validated cleanly — see the note on the failing fixpoint case.\n\nThe pinned defect:\n%s"
                defect)

        expectGolden (Goldens.check (trip.Value.Name + ".validation.txt") (String.concat "\n" errors + "\n"))
]

let private fixtureTests (fixture: CorpusFixtures.Fixture) =
    let name = fixture.Name
    // Built once per list rather than per case: the round trip is the
    // subject of every case below, and re-running it would only invite
    // two cases to disagree about what they measured.
    let trip = lazy (roundTrip fixture)

    testList name [
        testCase "the fixture SOURCE is valid OOXML"
        <| fun () ->
            // The corpus measures fidelity, so its inputs must be
            // well-formed: a malformed fixture would pin the layer's
            // behaviour against markup Word could never produce, and
            // the goldens would look entirely plausible. This case
            // caught a `w:r` nested inside a `w:r` in the residue
            // fixture on the pass that added it.
            let errors = validationErrors (fixture.Build())

            Expect.isEmpty
                errors
                (sprintf
                    "the fixture built by Corpus/CorpusFixtures.fs is not schema-valid, so nothing downstream of it means what it appears to:\n%s"
                    (String.concat "\n" errors))

        testCase "re-imported DocModel matches its committed golden"
        <| fun () -> expectGolden (Goldens.check (name + ".model.txt") (Goldens.renderModel trip.Value.Second.Model))

        testCase "emitted package elements match their committed golden"
        <| fun () -> expectGolden (Goldens.check (name + ".package.txt") (Goldens.renderPackage trip.Value.Emitted))

        testCase "import residue matches its expected baseline"
        <| fun () ->
            expectGolden (Goldens.check (name + ".residue.txt") (Goldens.renderResidue trip.Value.First.Residue))

        yield!
            match fixture.KnownDefect with
            | None -> cleanRoundTripCases trip
            | Some defect -> pinnedDefectCases trip defect
    ]

// ─── The tracked-changes leg ─────────────────────────────────────

let private reviewer = {
    Name = "Reviewer C"
    Initials = Some "RC"
}

let private editTimestamp = DateTimeOffset(2026, 6, 4, 11, 45, 0, TimeSpan.Zero)

/// Edits applied on top of a document that ALREADY carries tracked
/// changes — the case that distinguishes "revisions survive" from
/// "revisions are produced". Addresses are into the tracked-changes
/// fixture's imported model: block 1 is the heading, block 2 the
/// paragraph carrying a pre-existing insertion.
let private editSet = {
    Author = reviewer
    Timestamp = editTimestamp
    Edits = [
        ReplaceText({ Section = 0; Block = 1 }, "original", "revised")
        AddComment({ Section = 0; Block = 1 }, "checked against the source")
        InsertParagraphAfter(
            { Section = 0; Block = 1 },
            ParagraphModel.create [ Run.plain "Added by a third reviewer." ]
        )
    ]
}

let private trackedFixture =
    CorpusFixtures.all |> List.find (fun f -> f.Name = "tracked-changes")

let private redlined () =
    let imported = Import.fromBytes (CorpusFixtures.trackedChanges ())

    match Revisions.applyTracked editSet imported.Model with
    | Ok model -> model
    | Error e -> failtestf "expected the edit set to apply, got %A" e

/// Every revision attribution in an emitted package, as
/// `kind, author, date, id` — the four facts a tracked change is
/// worthless without.
let private revisionAttribution (doc: WordprocessingDocument) =
    let body = doc.MainDocumentPart.Document.Body

    let optionalDate (value: DateTimeValue) =
        if isNull (box value) || not value.HasValue then
            None
        else
            Some value.Value

    [
        for ins in body.Descendants<Wordprocessing.InsertedRun>() do
            "ins", ins.Author.Value, optionalDate ins.Date, ins.Id.Value
        for del in body.Descendants<Wordprocessing.DeletedRun>() do
            "del", del.Author.Value, optionalDate del.Date, del.Id.Value
        for ins in body.Descendants<Wordprocessing.Inserted>() do
            "markIns", ins.Author.Value, optionalDate ins.Date, ins.Id.Value
        for del in body.Descendants<Wordprocessing.Deleted>() do
            "markDel", del.Author.Value, optionalDate del.Date, del.Id.Value
    ]

let private trackedChangesTests =
    testList "tracked-changes fidelity" [
        testCase "pre-existing revisions survive the round trip with author and date"
        <| fun () ->
            let trip = roundTrip trackedFixture

            let attribution (model: DocModel) = [
                for section in model.Sections do
                    for block in section.Blocks do
                        match block with
                        | Heading(_, p)
                        | Paragraph p
                        | ListItem(_, p) ->
                            match p.MarkRevision with
                            | Some(Inserted info) -> yield "markIns", info.Author, info.Date
                            | Some(Deleted info) -> yield "markDel", info.Author, info.Date
                            | None -> ()

                            for run in p.Runs do
                                match run.Revision with
                                | Some(Inserted info) -> yield "ins", info.Author, info.Date
                                | Some(Deleted info) -> yield "del", info.Author, info.Date
                                | None -> ()
                        | _ -> ()
            ]

            let expected = [
                "ins", "Reviewer A", Some(DateTimeOffset CorpusFixtures.firstRevisionDate)
                "del", "Reviewer B", Some(DateTimeOffset CorpusFixtures.secondRevisionDate)
                "markIns", "Reviewer A", Some(DateTimeOffset CorpusFixtures.firstRevisionDate)
                "ins", "Reviewer A", Some(DateTimeOffset CorpusFixtures.firstRevisionDate)
                "markDel", "Reviewer B", Some(DateTimeOffset CorpusFixtures.secondRevisionDate)
                "del", "Reviewer B", Some(DateTimeOffset CorpusFixtures.secondRevisionDate)
            ]

            Expect.equal (attribution trip.First.Model) expected "attribution on first import"

            Expect.equal (attribution trip.Second.Model) expected "attribution unchanged after import → emit → import"

        testCase "emitted revision ids are unique document-wide"
        <| fun () ->
            // The fixture's own source ids deliberately collide across
            // paragraphs (101 appears twice), so an emission that copied
            // them through instead of renumbering fails here.
            let trip = roundTrip trackedFixture

            withDocument trip.Emitted (fun doc ->
                let ids = revisionAttribution doc |> List.map (fun (_, _, _, id) -> id)

                Expect.equal
                    (List.distinct ids).Length
                    ids.Length
                    (sprintf "every w:ins / w:del carries a distinct w:id, got %A" ids))

        testCase "Revisions.applyTracked layers new marks over the pre-existing ones"
        <| fun () ->
            withDocument (Emit.toBytes (redlined ())) (fun doc ->
                let attribution = revisionAttribution doc

                let authors =
                    attribution
                    |> List.map (fun (_, author, _, _) -> author)
                    |> List.distinct
                    |> List.sort

                Expect.equal
                    authors
                    [ "Reviewer A"; "Reviewer B"; "Reviewer C" ]
                    "all three reviewers' attributions coexist in one redline"

                Expect.isTrue
                    (attribution |> List.forall (fun (_, _, date, _) -> date.IsSome))
                    (sprintf "every revision carries a timestamp, got %A" attribution)

                let ids = attribution |> List.map (fun (_, _, _, id) -> id)

                Expect.equal
                    (List.distinct ids).Length
                    ids.Length
                    (sprintf "ids stay unique once new marks are layered in, got %A" ids)

                let newEdits =
                    attribution
                    |> List.filter (fun (_, author, _, _) -> author = "Reviewer C")
                    |> List.map (fun (kind, _, date, _) -> kind, date)

                Expect.equal
                    (newEdits |> List.map fst |> List.sort)
                    [ "del"; "ins"; "ins"; "markIns" ]
                    "the replace lowers to one del + one ins; the inserted paragraph to a run ins + a mark ins"

                Expect.isTrue
                    (newEdits |> List.forall (fun (_, date) -> date = Some editTimestamp.UtcDateTime))
                    (sprintf "the new edits carry the edit set's timestamp, got %A" newEdits))

        testCase "the redlined package matches its committed golden"
        <| fun () ->
            expectGolden (
                Goldens.check "tracked-changes-redline.package.txt" (Goldens.renderPackage (Emit.toBytes (redlined ())))
            )

        testCase "PINNED DEFECT — the redline inherits the duplicated paragraph marks"
        <| fun () ->
            // `applyTracked` is not implicated: the redline is emitted
            // from a model imported through the same lossy path, so it
            // carries the fixture's own pinned defect forward. The
            // violations are pinned as a golden for the reason the
            // per-fixture case gives — a NEW violation appearing beside
            // the known ones must not read as "still invalid".
            let errors = validationErrors (Emit.toBytes (redlined ()))

            Expect.isNonEmpty
                errors
                (sprintf
                    "The redline validated cleanly, so the paragraph-mark duplication was fixed. Update this case and the tracked-changes fixture's `KnownDefect` declaration together.\n\nThe pinned defect:\n%s"
                    (trackedFixture.KnownDefect |> Option.defaultValue "(none declared)"))

            expectGolden (Goldens.check "tracked-changes-redline.validation.txt" (String.concat "\n" errors + "\n"))
    ]

// ─── The regeneration switch ─────────────────────────────────────

let private regenerationSwitchTests =
    testList "golden regeneration" [
        testCase "the regeneration switch is not armed"
        <| fun () ->
            // Acceptance criterion: the switch is documented and NOT
            // invoked silently. Every golden case already fails under
            // it — this case says WHY in one line, so a run that
            // inherited the variable from a shell reports a cause
            // rather than a wall of rewrites.
            Expect.isFalse
                (Goldens.approveModeOn ())
                (sprintf
                    "%s is set, so this run REWRITES the fidelity goldens instead of checking them. Unset it and re-run; regeneration is only ever a deliberate act for a reviewed format change (see docs/migrations/206-openxml-round-trip-fidelity-corpus.md)."
                    Goldens.ApproveVariable)

        testCase "every corpus fixture is pinned by all three goldens"
        <| fun () ->
            // A fixture added without goldens would otherwise surface
            // as three unrelated-looking failures; this names the gap
            // once, and proves the corpus directory resolved at all.
            let dir = Goldens.corpusDir ()

            Expect.isTrue (Directory.Exists dir) (sprintf "corpus directory resolves, looked in %s" dir)

            let missing = [
                for fixture in CorpusFixtures.all do
                    let suffixes = [
                        ".model.txt"
                        ".package.txt"
                        ".residue.txt"
                        // A declared defect carries a fourth golden:
                        // the schema violations its emission produces.
                        if fixture.KnownDefect.IsSome then
                            ".validation.txt"
                    ]

                    for suffix in suffixes do
                        let file = fixture.Name + suffix

                        if not (File.Exists(Path.Combine(dir, file))) then
                            file
            ]

            Expect.isEmpty missing (sprintf "every fixture has its committed goldens, missing: %A" missing)
    ]

let tests =
    testList "RoundTripFidelity" [
        yield! CorpusFixtures.all |> List.map fixtureTests
        trackedChangesTests
        regenerationSwitchTests
    ]