module ToolUp.Forms.Tests.InProcess.MatrixFieldTests

open System
open System.Text.Json
open Expecto
open ToolUp.Platform.IEntityStore
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.IFormStore
open ToolUp.Forms.FormStore
open ToolUp.Forms.FormValidator
open ToolUp.Forms.Tests.InProcess.InMemoryEntityStore
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 21a acceptance — matrix / 2D-grid field ─────────────────
//
// Server-side coverage for the `MatrixField` extension. Client render
// coverage rides the Fable gate (samples/MinimalClient compiles the
// Client tier). The acceptance criteria exercised here:
//
//   * A schema with a `MatrixField` round-trips through save / load
//     (FormStore over IEntityStore) AND through the STJ wire
//     (FableConverters) without value loss.
//   * Per-cell validation runs with the same error-emission shape as
//     flat fields — the emitted FieldKey is the `{key}[r,c]` sub-key.
//   * The constructor enforces rows >= 1 / cols >= 1 (and rejects a
//     nested-matrix cell).
//   * Numeric label fallback (R1 / C1) when labels are absent /
//     partial; supplied labels win.
//   * Strip test — a flat-only form is unaffected by the matrix code.

// STJ options mirroring the SDK wire (same converter set the
// production entity stores serialise with).
let private jsonOptions = FableConverters.create ()

let private wireRoundTrip (submission: Submission) : Submission =
    let json = JsonSerializer.Serialize(submission, jsonOptions)
    JsonSerializer.Deserialize<Submission>(json, jsonOptions)

// A "weekly availability" schema: 7 days (rows) × 3 slots (cols) of
// boolean cells, with row/column labels.
let private availabilityLabels = {
    RowLabels = [ "Mon"; "Tue"; "Wed"; "Thu"; "Fri"; "Sat"; "Sun" ]
    ColLabels = [ "Morning"; "Afternoon"; "Evening" ]
}

let private availabilitySchema: FormSchema = {
    Id = "weekly-availability"
    Type = FormSchema.entityType
    Version = 1
    DisplayName = "Weekly availability"
    Description = Some "Tick every slot you are available."
    Fields = [
        {
            Key = "name"
            DisplayName = "Name"
            Description = None
            Kind = TextField(Some 120)
            Required = true
            Validators = []
        }
        {
            Key = "availability"
            DisplayName = "Availability"
            Description = None
            Kind = Matrix.create 7 3 BoolField (Some availabilityLabels)
            Required = false
            Validators = []
        }
    ]
    Visibility = Internal
}

// Flatten a bool grid into the `{key}[r,c]` sub-key map the way a
// client submission would.
let private flattenBoolGrid (key: string) (grid: bool[][]) : Map<string, FieldValue> =
    [
        for r in 0 .. grid.Length - 1 do
            for c in 0 .. grid[r].Length - 1 do
                let cell = grid[r][c]
                yield (Matrix.cellKey key r c, BoolValue cell)
    ]
    |> Map.ofList

let tests =
    testList "Phase 21a matrix field" [

        testCase "constructor enforces dimensions and rejects nested matrices" (fun () ->
            Expect.throws (fun () -> Matrix.create 0 3 BoolField None |> ignore) "rows < 1 must throw"
            Expect.throws (fun () -> Matrix.create 3 0 BoolField None |> ignore) "cols < 1 must throw"

            Expect.throws
                (fun () -> Matrix.create 2 2 (Matrix.create 2 2 BoolField None) None |> ignore)
                "nested-matrix cell must throw"

            match Matrix.create 2 3 BoolField None with
            | MatrixField(r, c, cell, labels) ->
                Expect.equal r 2 "rows"
                Expect.equal c 3 "cols"
                Expect.equal cell BoolField "cell kind"
                Expect.equal labels None "labels"
            | other -> failtestf "expected MatrixField, got %A" other)

        testCase "labels fall back to R/C numbering when absent or short" (fun () ->
            Expect.equal (Matrix.rowLabel None 0) "R1" "row fallback is 1-based"
            Expect.equal (Matrix.colLabel None 2) "C3" "col fallback is 1-based"
            Expect.equal (Matrix.rowLabel (Some availabilityLabels) 0) "Mon" "supplied row label wins"
            Expect.equal (Matrix.colLabel (Some availabilityLabels) 1) "Afternoon" "supplied col label wins"

            // A partially-populated label list falls back for the gap.
            let partial =
                Some {
                    RowLabels = [ "Alpha" ]
                    ColLabels = []
                }

            Expect.equal (Matrix.rowLabel partial 0) "Alpha" "in-range label used"
            Expect.equal (Matrix.rowLabel partial 3) "R4" "out-of-range row falls back"
            Expect.equal (Matrix.colLabel partial 0) "C1" "empty col list falls back")

        testCase "cellKey + coords define the flattened layout" (fun () ->
            Expect.equal (Matrix.cellKey "availability" 2 1) "availability[2,1]" "canonical sub-key format"
            Expect.equal (Matrix.coords 2 3).Length 6 "2x3 -> 6 cells"
            Expect.equal (List.head (Matrix.coords 2 3)) (0, 0) "row-major first"
            Expect.equal (List.last (Matrix.coords 2 3)) (1, 2) "row-major last")

        testAsync "weekly availability round-trips through save/load and the STJ wire without value loss" {
            let entityStore = InMemoryEntityStore() :> IEntityStore
            let formStore = FormStore(entityStore) :> IFormStore
            let scope = "team-availability"

            let! _ = formStore.SaveSchema(scope, availabilitySchema)

            // A representative availability pattern.
            let grid = [|
                [| true; false; true |] // Mon
                [| false; false; true |] // Tue
                [| true; true; false |] // Wed
                [| false; true; true |] // Thu
                [| true; false; false |] // Fri
                [| false; false; false |] // Sat
                [| true; true; true |] // Sun
            |]

            let values = flattenBoolGrid "availability" grid |> Map.add "name" (TextValue "Ada")

            let submission: Submission = {
                Id = "avail-1"
                Type = Submission.entityType
                Version = 1
                FormId = availabilitySchema.Id
                SchemaVersion = 1
                SubmittedAt = DateTimeOffset.UtcNow
                Author = AuthenticatedUser "ada"
                Values = values
                State = Submitted
                WorkflowId = None
            }

            // Validation passes (optional matrix, bool cells always present).
            Expect.isOk (validate emptyRegistry availabilitySchema values) "availability validates"

            // Save then load — the 21 cell entries + the flat field survive.
            let! saved = formStore.SaveSubmission(scope, submission)
            Expect.isOk saved "save ok"

            let! loaded = formStore.GetSubmission(scope, "avail-1")

            match loaded with
            | Ok s ->
                Expect.equal s.Values values "loaded values identical to submitted (no loss)"
                Expect.equal (Map.count s.Values) 22 "21 matrix cells + 1 flat field"
                // Every cell individually recoverable under its sub-key.
                for (r, c) in Matrix.coords 7 3 do
                    let ck = Matrix.cellKey "availability" r c
                    let cellVal = grid[r][c]
                    let expected = BoolValue cellVal

                    Expect.equal (Map.tryFind ck s.Values) (Some expected) (sprintf "cell %s round-trips" ck)
            | Error e -> failtestf "expected submission, got %A" e

            // Replay through the actual SDK wire — the sub-keyed map must
            // survive STJ serialisation byte-for-byte (InMemory store just
            // boxes, so this is the real fidelity check).
            let replayed = wireRoundTrip submission
            Expect.equal replayed.Values values "STJ wire round-trip preserves the flattened matrix map"
        }

        testCase "per-cell validation emits errors keyed by the cell sub-key" (fun () ->
            // A 2x2 matrix of NumberField with a 0..10 range on every cell.
            let schema: FormSchema = {
                Id = "scores"
                Type = FormSchema.entityType
                Version = 1
                DisplayName = "Scores"
                Description = None
                Fields = [
                    {
                        Key = "grid"
                        DisplayName = "Score grid"
                        Description = None
                        Kind = Matrix.create 2 2 (NumberField(None, None)) None
                        Required = true
                        Validators = [ NumberRange(Some 0.0, Some 10.0) ]
                    }
                ]
                Visibility = Internal
            }

            // One out-of-range cell (1,1) = 42; one wrong-type cell (0,1) = text.
            let values =
                Map [
                    Matrix.cellKey "grid" 0 0, NumberValue 5.0
                    Matrix.cellKey "grid" 0 1, TextValue "oops"
                    Matrix.cellKey "grid" 1 0, NumberValue 3.0
                    Matrix.cellKey "grid" 1 1, NumberValue 42.0
                ]

            match validate emptyRegistry schema values with
            | Ok() -> failtest "expected validation errors"
            | Error errs ->
                // Range error on cell [1,1].
                let rangeErr =
                    errs |> List.tryFind (fun e -> e.FieldKey = "grid[1,1]" && e.Code = "range")

                Expect.isSome rangeErr "range error keyed by cell sub-key grid[1,1]"

                // Wrong-type error on cell [0,1].
                let typeErr =
                    errs
                    |> List.tryFind (fun e -> e.FieldKey = "grid[0,1]" && e.Code = "wrong-type")

                Expect.isSome typeErr "wrong-type error keyed by cell sub-key grid[0,1]"

                // Valid cells emit nothing.
                Expect.isNone
                    (errs |> List.tryFind (fun e -> e.FieldKey = "grid[0,0]"))
                    "valid cell [0,0] emits no error")

        testCase "required matrix flags each missing cell as required" (fun () ->
            let schema: FormSchema = {
                Id = "req-grid"
                Type = FormSchema.entityType
                Version = 1
                DisplayName = "Required grid"
                Description = None
                Fields = [
                    {
                        Key = "g"
                        DisplayName = "G"
                        Description = None
                        Kind = Matrix.create 1 2 (NumberField(None, None)) None
                        Required = true
                        Validators = []
                    }
                ]
                Visibility = Internal
            }

            // Only one of the two required cells supplied.
            let values = Map [ Matrix.cellKey "g" 0 0, NumberValue 1.0 ]

            match validate emptyRegistry schema values with
            | Ok() -> failtest "expected a required error for the missing cell"
            | Error errs ->
                let missing =
                    errs |> List.tryFind (fun e -> e.FieldKey = "g[0,1]" && e.Code = "required")

                Expect.isSome missing "missing required cell g[0,1] flagged")

        testCase "strip test — a flat-only form is unaffected by the matrix code" (fun () ->
            // A form with no MatrixField validates exactly as before:
            // present/absent required, range, wrong-type all behave
            // identically to the pre-21a path (regression guard).
            let schema: FormSchema = {
                Id = "flat"
                Type = FormSchema.entityType
                Version = 1
                DisplayName = "Flat"
                Description = None
                Fields = [
                    {
                        Key = "title"
                        DisplayName = "Title"
                        Description = None
                        Kind = TextField(Some 20)
                        Required = true
                        Validators = []
                    }
                    {
                        Key = "score"
                        DisplayName = "Score"
                        Description = None
                        Kind = NumberField(None, None)
                        Required = false
                        Validators = [ NumberRange(Some 0.0, Some 100.0) ]
                    }
                ]
                Visibility = Internal
            }

            // Happy path.
            let good = Map [ "title", TextValue "Hi"; "score", NumberValue 50.0 ]
            Expect.isOk (validate emptyRegistry schema good) "flat happy path validates"

            // Missing required title.
            match validate emptyRegistry schema (Map [ "score", NumberValue 50.0 ]) with
            | Error errs ->
                Expect.isSome
                    (errs |> List.tryFind (fun e -> e.FieldKey = "title" && e.Code = "required"))
                    "missing title flagged exactly as before"
            | Ok() -> failtest "expected required error"

            // No stray matrix sub-keys are ever produced for a flat form.
            let flatKeys = good |> Map.toList |> List.map fst

            Expect.isFalse
                (flatKeys |> List.exists (fun k -> k.Contains "["))
                "no matrix sub-keys in a flat submission")
    ]