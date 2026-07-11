// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ModelProviders.Reference

open System
open System.Text
open System.Security.Cryptography
open ToolUp.Platform

// ─── Phase 454 — reference IModelScoreProvider ──────────────────────────
//
// The scoring counterpart to `ReferenceModelFitProvider` — the same
// reference provider family (`Kind = "reference"`, same `Version`) now also
// scores, so the artifact a reference fit produced is scored by the
// reference scorer (provider-symmetric, plan Stage 5). It is **explicitly
// not statistics**: for each input row it emits one deterministic
// `prediction` value, a pure seeded function of the fitted artifact's
// content hash + the row's ordinal. There is no estimator and no use of the
// row's feature values — the point is to bind the score contract pack and
// prove the seam, not to model anything.
//
// **Output shape (joinable by construction).** The output frame carries the
// input's panel-key columns (`PanelUnit` / `PanelPeriod`) forward verbatim,
// then appends a single `prediction : Float` target column. So the scored
// vintage joins straight back to the input on its panel keys with zero new
// machinery — the whole motivation of Phase 454.
//
// **Determinism (plan D4).** The predictions are a pure function of
// `(artifact.ContentHash, rowIndex)` — no wall-clock, no RNG — so an
// identical `(artifact, input)` scores byte-identically across re-runs.

module ReferenceModelScoreProvider =

    /// Same kind as the reference fit provider — an artifact fitted by
    /// `reference` is scored by `reference` (the `ProviderId` on the
    /// artifact's composite key resolves this scorer).
    [<Literal>]
    let Kind = "reference"

    [<Literal>]
    let Version = "1.0.0"

    /// The output column name the reference scorer appends.
    [<Literal>]
    let PredictionColumn = "prediction"

    /// A stable unit float in `[0, 1)` derived from a string's SHA-256. The
    /// determinism source — pure, no RNG (mirrors the fit provider).
    let private deterministicUnit (s: string) : float =
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes s)
        let u = BitConverter.ToUInt64(hash, 0)
        float u / float UInt64.MaxValue

    /// The panel-key columns (unit + period) of an input schema, preserving
    /// order — carried forward verbatim into the output frame so the
    /// predictions join back to the input.
    let private keyColumns (schema: DatasetSchema) : DatasetColumn list =
        schema.Columns
        |> List.filter (fun c -> c.Role = DatasetColumnRole.PanelUnit || c.Role = DatasetColumnRole.PanelPeriod)

    let private predict
        (requiredColumns: string list)
        (artifact: ArtifactRef)
        (schema: DatasetSchema)
        (rows: DatasetRow list)
        : Async<Result<ScorePrediction, ScoreError>> =
        async {
            // The envelope already enforced RequiredInputColumns, but a
            // provider defends its own contract too (rule 4 — a distributed
            // worker can be handed anything). A missing declared column is a
            // typed refusal, never a throw.
            let have = schema.Columns |> List.map (fun c -> c.Name) |> Set.ofList
            let missing = requiredColumns |> List.filter (fun c -> not (Set.contains c have))

            if not (List.isEmpty missing) then
                return
                    Error(
                        ScoreError.InputSchemaMismatch(
                            sprintf "reference scorer requires column(s): %s" (String.concat ", " missing)
                        )
                    )
            else
                let keyCols = keyColumns schema

                let keyIdx =
                    keyCols
                    |> List.map (fun c -> schema.Columns |> List.findIndex (fun x -> x.Name = c.Name))

                let predictionColumn: DatasetColumn = {
                    Name = PredictionColumn
                    DType = DatasetDType.Float
                    Nullable = false
                    Role = DatasetColumnRole.Target
                }

                let outputSchema: DatasetSchema = {
                    Columns = keyCols @ [ predictionColumn ]
                }

                let outputRows =
                    rows
                    |> List.mapi (fun i row ->
                        let cells = row.Cells |> List.toArray
                        let keyCells = keyIdx |> List.map (fun idx -> cells.[idx])

                        // Deterministic prediction — NOT a real fit. Seeded by
                        // the fitted artifact's content hash + the row ordinal.
                        let value = deterministicUnit (sprintf "%s|%d" artifact.ContentHash i)

                        {
                            Cells = keyCells @ [ DatasetValue.Float value ]
                        })

                return
                    Ok {
                        Schema = outputSchema
                        Rows = outputRows
                    }
        }

    /// A reference scorer that declares `requiredColumns` as its input
    /// contract. Used by the contract pack to exercise the envelope's
    /// schema-mismatch refusal; the common `create ()` requires nothing.
    let createRequiring (requiredColumns: string list) : IModelScoreProvider =
        { new IModelScoreProvider with
            member _.Kind = Kind
            member _.ProviderVersion = Version
            member _.RequiredInputColumns() = requiredColumns

            member _.Predict(artifact, schema, rows) =
                predict requiredColumns artifact schema rows
        }

    /// The reference scorer as an `IModelScoreProvider` with no structural
    /// input requirement. Takes no substrate dependencies — deliberately
    /// trivial (a production scorer receives an `IDatasetStore` / model
    /// runtime here and reads the fitted parameters).
    let create () : IModelScoreProvider = createRequiring []