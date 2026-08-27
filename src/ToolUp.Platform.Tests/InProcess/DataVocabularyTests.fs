module ToolUp.Platform.Tests.InProcess.DataVocabularyTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── Phase 594 — pinned data-vocabulary packs ─────────────────────────
//
// Covers the acceptance shape: a conforming data type passes; a squatting
// name (governed by a pinned pack's namespace with no matching entry) and a
// drifted declared schema each fail with the pack entry named; the canonical
// hash / loader round-trip; version-bump semantics (any change is a new
// version, detectable by hash); and the no-pin path is byte-for-byte
// unchanged (GP 11 / GP 13). The pure rule core (`checkWith`) is exercised
// over crafted manifests, plus one end-to-end pass through a composed
// `ServerApp`'s manifest.

// ─── A generic reference pack (no domain vocabulary) ──────────────────

/// The reference pack, authored as a value. Namespace `"reference"` with
/// two governed types. Generic by construction — zero private / domain
/// vocabulary.
let private referencePack: DataVocabularyPack = {
    Id = "reference-core"
    Namespace = "reference"
    Version = { Major = 1; Minor = 0 }
    Entries = [
        {
            TypeName = "reference.Widget"
            Description = "a widget with a weight and an active flag"
            Fields = [
                {
                    Name = "id"
                    Type = VocabId
                    Unit = None
                    Description = "the widget id"
                }
                {
                    Name = "weight"
                    Type = VocabNumber
                    Unit = Some "kg"
                    Description = "the widget weight"
                }
                {
                    Name = "active"
                    Type = VocabBoolean
                    Unit = None
                    Description = "whether the widget is active"
                }
            ]
        }
        {
            TypeName = "reference.Gadget"
            Description = "a gadget with a price"
            Fields = [
                {
                    Name = "id"
                    Type = VocabId
                    Unit = None
                    Description = "the gadget id"
                }
                {
                    Name = "price"
                    Type = VocabNumber
                    Unit = Some "USD"
                    Description = "the gadget price"
                }
            ]
        }
    ]
}

/// A conforming declared schema for `reference.Widget` — identical fields
/// to the pack entry (field order deliberately differs, to prove the drift
/// check is order-independent).
let private conformingWidgetSchema: VocabularyEntry = {
    TypeName = "reference.Widget"
    Description = "the deployment's own widget declaration"
    Fields = [
        {
            Name = "active"
            Type = VocabBoolean
            Unit = None
            Description = "active flag"
        }
        {
            Name = "weight"
            Type = VocabNumber
            Unit = Some "kg"
            Description = "weight"
        }
        {
            Name = "id"
            Type = VocabId
            Unit = None
            Description = "id"
        }
    ]
}

let private hasRule (code: string) (defects: CompositionDefect list) : bool =
    defects |> List.exists (fun d -> d.RuleCode = code)

let private ruleMessage (code: string) (defects: CompositionDefect list) : string =
    defects |> List.find (fun d -> d.RuleCode = code) |> _.Message

let private stubDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    SchemaVersion = DataTypes.initialSchemaVersion
    Migrations = []
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub Process never called" }
}

let tests =
    testList "DataVocabulary" [

        // ── the loader round-trips the canonical JSON ────────────────
        testCase "load(canonicalJson pack) reconstructs the pack"
        <| fun _ ->
            let json = DataVocabulary.canonicalJson referencePack

            match DataVocabulary.load json with
            | Ok loaded ->
                Expect.equal loaded.Id referencePack.Id "id round-trips"
                Expect.equal loaded.Namespace referencePack.Namespace "namespace round-trips"
                Expect.equal loaded.Version referencePack.Version "version round-trips"
                // Entries + fields are canonical-sorted; compare as sets of
                // (typeName, sorted field names + types).
                let normalise (p: DataVocabularyPack) =
                    p.Entries
                    |> List.map (fun e ->
                        e.TypeName,
                        (e.Fields
                         |> List.map (fun f -> f.Name, DataVocabulary.fieldTypeToWire f.Type, f.Unit)
                         |> List.sort))
                    |> List.sortBy fst

                Expect.equal (normalise loaded) (normalise referencePack) "entries + fields round-trip"
            | Error e -> failtestf "expected Ok, got Error %s" e

        // ── canonical JSON is order-independent (stable hash) ────────
        testCase "the canonical hash is independent of authoring order"
        <| fun _ ->
            let reordered = {
                referencePack with
                    Entries = List.rev referencePack.Entries
            }

            Expect.equal
                (DataVocabulary.hash reordered)
                (DataVocabulary.hash referencePack)
                "reordering entries does not change the canonical hash"

        // ── version-bump semantics: any change is a new version ──────
        testCase "a schema change produces a different hash (a new version)"
        <| fun _ ->
            let widened = {
                referencePack with
                    Version = { Major = 1; Minor = 1 }
                    Entries =
                        referencePack.Entries
                        |> List.map (fun e ->
                            if e.TypeName = "reference.Gadget" then
                                {
                                    e with
                                        Fields =
                                            e.Fields
                                            @ [
                                                {
                                                    Name = "sku"
                                                    Type = VocabString
                                                    Unit = None
                                                    Description = "stock-keeping unit"
                                                }
                                            ]
                                }
                            else
                                e)
            }

            Expect.notEqual
                (DataVocabulary.hash widened)
                (DataVocabulary.hash referencePack)
                "adding a field yields a distinct canonical hash — a genuinely new version"

            Expect.notEqual widened.Version referencePack.Version "the version is bumped alongside the content change"

        // ── the loader rejects an unknown field value-type ───────────
        testCase "load fails on an unknown field value-type, naming the field"
        <| fun _ ->
            let bad =
                """{"formatVersion":1,"id":"x","namespace":"x","version":{"major":1,"minor":0},"entries":[{"typeName":"x.Y","description":"","fields":[{"name":"f","type":"complex","unit":null,"description":""}]}]}"""

            match DataVocabulary.load bad with
            | Error msg ->
                Expect.stringContains msg "f" "names the offending field"
                Expect.stringContains msg "complex" "names the unknown value-type"
            | Ok _ -> failtest "expected the loader to reject an unknown value-type"

        // ── a conforming data type + declared schema pass silently ───
        testCase "a conforming data type passes with the pack pinned"
        <| fun _ ->
            let manifest =
                CompositionManifest.build [] [] [ CompositionManifest.dataTypeEntry "reference.Widget" ] [] []

            let refs = {
                CompositionReferences.empty with
                    PinnedVocabularyPacks = [ referencePack ]
                    DataSchemas = [ conformingWidgetSchema ]
            }

            Expect.isEmpty
                (CompositionValidator.checkWith refs manifest)
                "a governed name with a matching entry + conforming schema has no defects"

        // ── a squatting name fails, naming the pack ──────────────────
        testCase "a name governed by the pack with no entry squats and fails"
        <| fun _ ->
            let manifest =
                CompositionManifest.build [] [] [ CompositionManifest.dataTypeEntry "reference.Sprocket" ] [] []

            let refs = {
                CompositionReferences.empty with
                    PinnedVocabularyPacks = [ referencePack ]
            }

            let defects = CompositionValidator.checkWith refs manifest

            Expect.isTrue (hasRule "vocabulary-typename-unknown" defects) "the squatting rule fired"

            let message = ruleMessage "vocabulary-typename-unknown" defects
            Expect.stringContains message "reference.Sprocket" "names the squatting data type"
            Expect.stringContains message "reference-core" "names the pinned pack"
            Expect.stringContains message "reference.Widget" "enumerates a sanctioned pack entry"

        // ── a drifted declared schema fails, naming the entry ────────
        testCase "a drifted declared schema fails naming the pack entry"
        <| fun _ ->
            let drifted = {
                conformingWidgetSchema with
                    Fields =
                        conformingWidgetSchema.Fields
                        |> List.map (fun f ->
                            if f.Name = "weight" then
                                { f with Type = VocabString } // number → string drift
                            else
                                f)
            }

            let manifest =
                CompositionManifest.build [] [] [ CompositionManifest.dataTypeEntry "reference.Widget" ] [] []

            let refs = {
                CompositionReferences.empty with
                    PinnedVocabularyPacks = [ referencePack ]
                    DataSchemas = [ drifted ]
            }

            let defects = CompositionValidator.checkWith refs manifest

            Expect.isTrue (hasRule "vocabulary-schema-mismatch" defects) "the schema-drift rule fired"

            let message = ruleMessage "vocabulary-schema-mismatch" defects
            Expect.stringContains message "reference.Widget" "names the drifting entry"
            Expect.stringContains message "reference-core" "names the pinned pack"
            Expect.stringContains message "weight" "names the drifting field"

        // ── an ungoverned name is untouched ──────────────────────────
        testCase "a name outside every pinned namespace is unaffected"
        <| fun _ ->
            let manifest =
                CompositionManifest.build [] [] [ CompositionManifest.dataTypeEntry "other.Thing" ] [] []

            let refs = {
                CompositionReferences.empty with
                    PinnedVocabularyPacks = [ referencePack ]
            }

            Expect.isEmpty
                (CompositionValidator.checkWith refs manifest)
                "the pack has no authority over a name outside its namespace"

        // ── the no-pin path is byte-for-byte unchanged (GP 11/13) ────
        testCase "with no pack pinned, a governed-looking name yields no vocab defect"
        <| fun _ ->
            let manifest =
                CompositionManifest.build [] [] [ CompositionManifest.dataTypeEntry "reference.Sprocket" ] [] []

            // Empty pins — the default. Even a name that WOULD squat under a
            // pinned pack is fine when nothing is pinned.
            Expect.isEmpty
                (CompositionValidator.checkWith CompositionReferences.empty manifest)
                "an unpinned deployment pays nothing for the vocabulary rules"

        // ── end-to-end through a composed ServerApp manifest ─────────
        testCase "a squatting registered DataType fails preflight end-to-end"
        <| fun _ ->
            let config = {
                ServerConfig.defaults with
                    PinnedVocabularyPacks = [ referencePack ]
            }

            let widgets =
                ServerModule.create "Widgets"
                |> ServerModule.withDataTypes [ stubDataType "reference.Sprocket" ]

            let app =
                ServerApp.empty
                |> ServerApp.withConfig config
                |> ServerApp.addModules [ widgets ]

            let manifest = ServerApp.compositionManifest app

            let refs = {
                CompositionReferences.empty with
                    PinnedVocabularyPacks = app.Config.PinnedVocabularyPacks
                    DataSchemas = app.Config.DeclaredDataSchemas
            }

            let defects = CompositionValidator.checkWith refs manifest
            Expect.isTrue (hasRule "vocabulary-typename-unknown" defects) "the registered squatting DataType is caught"

            // The squatting defect is error-severity, so preflight aborts
            // (the ValidationResult mapping itself is covered in
            // CompositionValidatorTests — asserting severity here avoids the
            // ValidationResult.Ok/Error shadowing of the Result cases used
            // above).
            let squat =
                defects |> List.find (fun d -> d.RuleCode = "vocabulary-typename-unknown")

            Expect.equal squat.Severity DefectError "a squatting name aborts preflight (error severity)"
    ]