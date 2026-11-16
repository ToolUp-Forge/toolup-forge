// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Fixture builders over the published Fact kernel (`Fuaran.Facts`), so a
/// test states a metric in the free-text unit and format forms a module
/// writes (`"GBP"`, `"C0"`) and a value as a .NET decimal, and the kernel's
/// own conversions make the typed forms.
module ToolUp.Platform.Tests.KernelFixtures

open System
open Fuaran.Facts

let private ok (what: string) (r: Result<'T, Refusal>) : 'T =
    match r with
    | Ok v -> v
    | Error refusal -> failwithf "%s: %s" what (Refusal.describe refusal)

/// The unit a free-text unit names (`Legacy.unitOf`).
let unitOf (legacy: string) : Fuaran.Core.UnitOfMeasure = Legacy.unitOf legacy |> ok legacy

/// The display format a .NET numeric format string names (`Legacy.displayFormatOf`).
let formatOf (legacy: string) : DisplayFormat =
    Legacy.displayFormatOf legacy |> ok legacy

/// A metric definition with every optional member undeclared: no direction
/// of better, fresh until superseded.
let metric (id: string) (name: string) (unit: string) (dimensionality: string) (format: string) : MetricDefinition = {
    Id = id
    Name = name
    Unit = unitOf unit
    Dimensionality = dimensionality
    Direction = Neutral
    DisplayFormat = formatOf format
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RollUp = None
    Context = None
    RecomputePolicy = None
}

/// A scalar value from a .NET decimal, its scale kept.
let scalar (d: decimal) : FactValue = Scalar(DecimalText.ofDecimal d)

/// The instant a .NET `DateTime` names (an `Unspecified` value read as UTC).
let instant (d: DateTime) : Instant = Instant.ofDateTime d

/// A valid-time extent from two .NET `DateTime`s and an optional label.
let period (from: DateTime) (``to``: DateTime) (label: string option) : TemporalExtent = {
    From = Instant.ofDateTime from
    To = Instant.ofDateTime ``to``
    Label = label
}