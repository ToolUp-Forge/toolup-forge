// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open DataManagementTypes
open ColumnMappingTypes

/// Phase 218 — server-side seam for the mapping-aware Data Manager's
/// dry-run validation. Given a target type's `DataTypeSchema` and an
/// already-mapped (canonical-header) CSV, returns the per-row /
/// per-cell `DryRunReport` as data — never throwing on a bad row
/// (GP 12.3). No write, no `DataType.Process`: validation is a pure
/// inspection of the mapped shape.
///
/// The default implementation (`MappingDryRunValidator.create`) does
/// coarse type + required-cell checks over the platform's coarse
/// `DataTypeSchema` using BCL only. A richer validator (e.g. one backed
/// by `ToolUp.Tabular`'s constraint/pattern engine) can be composed in
/// to override it — keeping that companion's vendor dependency
/// (`DocumentFormat.OpenXml`) out of `ToolUp.Platform.*` per GP 1.
///
/// `CommitBlocked` on the returned report is left `false` here — the
/// `IConversionApi` handler stamps the policy verdict from
/// `ServerConfig.MappingDryRun`.
///
/// Portability (GP 12): identity by value (immutable records + strings),
/// no live handles, stateless between calls.
type IMappingDryRunValidator =
    abstract Validate: schema: DataTypeSchema * mappedCsv: string -> DryRunReport