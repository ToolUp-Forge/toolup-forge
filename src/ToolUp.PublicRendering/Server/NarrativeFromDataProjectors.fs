// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 85 — registry builders for the `ProcessedData` → Narrative
/// projector map consumed by [`NarrativeFromData.fromProcessed`](NarrativeFromData.fs).
///
/// A deployment registers one projector per data `TypeName`. The common
/// case — "deserialise the JSON payload into my module's result type,
/// then project it" — is captured by `registerTyped`, which folds the
/// `System.Text.Json` decode (using the SDK's canonical F#-aware
/// converter set) into the registration so the projector body sees a
/// typed value rather than a JSON string.
///
/// ```fsharp
/// open ToolUp.PublicRendering
///
/// let registry =
///     NarrativeFromDataProjectors.empty
///     |> NarrativeFromDataProjectors.registerTyped<SalesSummary> "SalesData" (fun s ->
///         [ NarrativeFromData.metricGrid
///             [ "Revenue", s.RevenueDisplay, Some s.RevenueDelta ]
///           NarrativeFromData.table
///             [ "Region", TableAlignment.Left; "Spend", TableAlignment.Right ]
///             (s.Regions |> List.map (fun r -> [ CellText r.Name; CellMoney(r.Spend, "£") ])) ])
///
/// let opts = NarrativeFromDataProjectors.options registry
/// // hand `opts` to NarrativeFromData.fromProcessed in a content source
/// ```
module ToolUp.PublicRendering.NarrativeFromDataProjectors

open System.Text.Json
open ToolUp.Platform.Narrative
open ToolUp.Remoting.Json.SystemTextJson
open ProcessedDataTypes
open ToolUp.PublicRendering

/// The SDK's canonical STJ options with the full F# converter set
/// (Option / DU / record / tuple / list / Map / …) registered — the same
/// wire shape every other non-Remoting JSON seam in the SDK uses, so a
/// payload produced by a module's `DataType.Process` round-trips
/// faithfully here.
let private jsonOptions = FableConverters.create ()

/// The empty projector registry — no data types projected. The starting
/// point for `register` / `registerTyped` chains.
let empty: Map<string, ProcessedProjector> = Map.empty

/// Register a raw projector (one that decodes `ProcessedData.Payload`
/// itself) under a `TypeName`. Last registration wins on a duplicate key.
let register
    (typeName: string)
    (projector: ProcessedProjector)
    (registry: Map<string, ProcessedProjector>)
    : Map<string, ProcessedProjector> =
    Map.add typeName projector registry

/// Register a typed projector: the `Payload` is deserialised into `'T`
/// (with the SDK converter set) before `project` runs, so the projector
/// body works with a typed value. A deserialisation failure surfaces
/// through `NarrativeFromData.fromProcessed`'s projector-exception guard
/// (degrading to a `Critical` callout), so a malformed payload can't 500
/// the page.
let registerTyped<'T>
    (typeName: string)
    (project: 'T -> NarrativeElement list)
    (registry: Map<string, ProcessedProjector>)
    : Map<string, ProcessedProjector> =
    let projector (data: ProcessedData) =
        let value = JsonSerializer.Deserialize<'T>(data.Payload, jsonOptions)
        project value

    Map.add typeName projector registry

/// Decode a `ProcessedData.Payload` into `'T` using the SDK converter
/// set. The escape hatch for a raw projector registered via `register`
/// that wants the typed value without `registerTyped`'s wrapping.
let decode<'T> (data: ProcessedData) : 'T =
    JsonSerializer.Deserialize<'T>(data.Payload, jsonOptions)

/// Lift a projector registry into `NarrativeFromDataOptions` carrying the
/// default `FormatOptions` and `UnknownTypeSeverity`. Use `optionsWith`
/// to override either.
let options (registry: Map<string, ProcessedProjector>) : NarrativeFromDataOptions = {
    NarrativeFromDataOptions.Default with
        Projectors = registry
}

/// `options` with an explicit `FormatOptions` and unknown-type
/// `Severity`.
let optionsWith
    (format: FormatOptions)
    (unknownTypeSeverity: Severity)
    (registry: Map<string, ProcessedProjector>)
    : NarrativeFromDataOptions =
    {
        Projectors = registry
        Format = format
        UnknownTypeSeverity = unknownTypeSeverity
    }