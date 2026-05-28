// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ProcessedDataContext

open Feliz
open DataManagementTypes
open ProcessedDataTypes

/// React context carrying the platform-aggregated `ProcessedFileEntry`
/// list. The shell computes this after every Elmish update cycle from
/// each module's `ProvidesProcessedData` hook and publishes it through
/// the Provider wrapping the view tree.
let Context = React.createContext<ProcessedFileEntry list> (defaultValue = [])

module ProcessedData =
    /// Hook — must be invoked from a function that is itself rendered as
    /// a React component (typically a `[<ReactComponent>]`-attributed
    /// Feliz component). Returns the processed-file entries matching
    /// `dataTypeId` with no error.
    let forType (dataTypeId: DataTypeId) =
        let entries = React.useContext Context

        entries |> List.filter (fun e -> e.DataType = dataTypeId && e.Error.IsNone)