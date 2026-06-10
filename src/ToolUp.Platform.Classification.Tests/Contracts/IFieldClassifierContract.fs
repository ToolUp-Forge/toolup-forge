// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Classification.Tests.Contracts.IFieldClassifierContract

open Expecto
open ToolUp.Platform

/// Phase 41 — contract assertions every `IFieldClassifier` implementation
/// must satisfy. The factory returns a classifier seeded with a known
/// registry, plus the registry itself so the harness knows the expected
/// answers.
type FieldClassifierFixture = {
    Classifier: IFieldClassifier
    Classifications: FieldClassification list
}

let tests (name: string) (factory: unit -> FieldClassifierFixture) =
    testList $"{name} — IFieldClassifier contract" [
        testCaseAsync "Classify returns every classification declared for an entity"
        <| async {
            let fx = factory ()
            let! result = fx.Classifier.Classify "Customer"

            let expected =
                fx.Classifications |> List.filter (fun c -> c.EntityName = "Customer")

            Expect.equal (List.length result) (List.length expected) "all Customer classifications returned"

            Expect.containsAll (result |> List.map _.FieldPath) (expected |> List.map _.FieldPath) "field paths match"
        }

        testCaseAsync "Classify returns [] for an unknown entity"
        <| async {
            let fx = factory ()
            let! result = fx.Classifier.Classify "NoSuchEntity"
            Expect.isEmpty result "unknown entity classifies nothing"
        }

        testCaseAsync "IsClassified is true for a classified field"
        <| async {
            let fx = factory ()
            let! result = fx.Classifier.IsClassified("Customer", "Email")
            Expect.isTrue result "Email is classified"
        }

        testCaseAsync "IsClassified is false for an unclassified field"
        <| async {
            let fx = factory ()
            let! unknownField = fx.Classifier.IsClassified("Customer", "NotAField")
            let! unknownEntity = fx.Classifier.IsClassified("Other", "Email")
            Expect.isFalse unknownField "unknown field is unclassified"
            Expect.isFalse unknownEntity "field on unknown entity is unclassified"
        }

        testCaseAsync "LookupBySubject returns the personal-data (Pii/Spi) fields as hits"
        <| async {
            let fx = factory ()
            let! hits = fx.Classifier.LookupBySubject "subject-42"

            let expectedPersonal =
                fx.Classifications
                |> List.filter (fun c -> ClassificationLevel.isPersonalData c.Level)

            Expect.equal (List.length hits) (List.length expectedPersonal) "one hit per personal-data field"
            Expect.all hits (fun h -> h.SubjectId = "subject-42") "every hit carries the subject id"

            Expect.all hits (fun h -> ClassificationLevel.isPersonalData h.Level) "every hit is Pii/Spi"
        }

        testCaseAsync "LookupBySubject excludes non-personal-data classifications"
        <| async {
            let fx = factory ()
            let! hits = fx.Classifier.LookupBySubject "subject-42"
            let paths = hits |> List.map _.FieldPath
            Expect.isFalse (List.contains "RevenueUsd" paths) "Financial field is not a DSAR personal-data hit"
            Expect.isFalse (List.contains "DisplayHandle" paths) "Public field is not a DSAR personal-data hit"
        }

        testCaseAsync "encryptionKeyFor returns the per-classification key when configured"
        <| async {
            let fx = factory ()
            let! piiKey = DefaultFieldClassifier.encryptionKeyFor fx.Classifier "Customer" "Email"
            let! finKey = DefaultFieldClassifier.encryptionKeyFor fx.Classifier "Customer" "RevenueUsd"
            Expect.equal piiKey (Some "pii-kek") "Email routes through pii-kek"
            Expect.equal finKey (Some "financial-kek") "RevenueUsd routes through financial-kek"
        }

        testCaseAsync "encryptionKeyFor returns None for a field with no per-classification key"
        <| async {
            let fx = factory ()
            let! nameKey = DefaultFieldClassifier.encryptionKeyFor fx.Classifier "Customer" "Name"
            let! unknownKey = DefaultFieldClassifier.encryptionKeyFor fx.Classifier "Customer" "NotAField"
            Expect.equal nameKey None "unkeyed classified field uses the master key (None)"
            Expect.equal unknownKey None "unclassified field has no per-classification key"
        }
    ]