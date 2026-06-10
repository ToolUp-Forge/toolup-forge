// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Phase 41 — registry-backed default `IFieldClassifier`. Built from the
/// classifications a deployment declares at compose time (via
/// `FieldClassification.attach<'Entity>` lists, or hydrated from a
/// config store keyed `_platform/classification/{entityName}.json`). The
/// registry is an immutable snapshot — the classifier holds no mutable
/// state and resolves every lookup against it (GP 12 rule 4).
///
/// `LookupBySubject` returns one `FieldHit` per declared personal-data
/// (`Pii` / `Spi`) field. This is the *schema-level* answer — "these are
/// the fields that hold personal data for any subject" — which is
/// precisely what the Phase 9h export pipeline consumes to know which
/// fields to pull for a subject; the per-row matching against the entity
/// store happens in the exporter, not the classifier.
type DefaultFieldClassifier(classifications: FieldClassification list) =

    // Index by entity name once at construction for O(1) Classify.
    let byEntity = classifications |> List.groupBy _.EntityName |> Map.ofList

    interface IFieldClassifier with
        member _.Classify(entityName: string) : Async<FieldClassification list> = async {
            return byEntity |> Map.tryFind entityName |> Option.defaultValue []
        }

        member _.IsClassified(entityName: string, fieldPath: string) : Async<bool> = async {
            return
                byEntity
                |> Map.tryFind entityName
                |> Option.map (List.exists (fun c -> c.FieldPath = fieldPath))
                |> Option.defaultValue false
        }

        member _.LookupBySubject(subjectId: string) : Async<FieldHit list> = async {
            return
                classifications
                |> List.filter (fun c -> ClassificationLevel.isPersonalData c.Level)
                |> List.map (fun c -> {
                    EntityName = c.EntityName
                    FieldPath = c.FieldPath
                    Level = c.Level
                    SubjectId = subjectId
                })
        }

module DefaultFieldClassifier =
    /// Construct a classifier from a flat classification list (typically
    /// the concatenation of every module's `FieldClassification.attach`
    /// declarations).
    let create (classifications: FieldClassification list) : IFieldClassifier =
        DefaultFieldClassifier(classifications) :> IFieldClassifier

    /// Resolve the per-classification encryption key id for a field, if
    /// any (Phase 22 integration). The entity-store encryption decorator
    /// calls this to route encrypt / decrypt through a per-classification
    /// KEK; `None` (field unclassified or no key configured) means use
    /// the deployment master key.
    let encryptionKeyFor
        (classifier: IFieldClassifier)
        (entityName: string)
        (fieldPath: string)
        : Async<string option> =
        async {
            let! fields = classifier.Classify entityName

            return
                fields
                |> List.tryFind (fun c -> c.FieldPath = fieldPath)
                |> Option.bind _.EncryptionKeyId
        }