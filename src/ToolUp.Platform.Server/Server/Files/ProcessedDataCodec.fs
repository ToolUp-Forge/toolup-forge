// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text.Json
open ProcessedDataTypes
open ToolUp.Remoting.Json.SystemTextJson

/// Phase 817 — the server-side codec for the `ProcessedData` envelope a
/// module puts on `ProcessedFileEntry.Summary`.
///
/// The shared tier carries no JSON stack (the `ModuleQueryCodec`
/// precedent), so each tier that has one supplies its half: this module
/// encodes and decodes through System.Text.Json + `FableConverters`, the
/// same converter set every other SDK boundary uses, whose wire shape
/// `Fable.SimpleJson` reads on the client — which is what lets
/// `DataTypeDisplay.typed` decode on the browser what `encode` produced
/// here.
///
/// `TypeName` is stamped with the summary type's CLR full name and is a
/// ROUTING TAG, not a check: `tryDecode` decodes by shape, because the
/// two hosts spell a nested type's name differently (`Module+Type` on
/// .NET, `Module.Type` under Fable) and a decode that compared them
/// would refuse its own producer's output across the wire.
[<RequireQualifiedAccess>]
module ProcessedDataCodec =

    let private options = FableConverters.create ()

    /// The envelope for a summary value.
    let encode<'T> (summary: 'T) : ProcessedData = {
        TypeName = typeof<'T>.FullName
        Payload = JsonSerializer.Serialize<'T>(summary, options)
    }

    /// The summary back, or the reason it did not read as `'T`. Never
    /// throws: a payload that does not match is an expected failure of a
    /// wire contract, not a fault.
    let tryDecode<'T> (envelope: ProcessedData) : Result<'T, string> =
        try
            let value = JsonSerializer.Deserialize<'T>(envelope.Payload, options)

            if isNull (box value) then
                Error(sprintf "the %s payload read as null" envelope.TypeName)
            else
                Ok value
        with ex ->
            Error(sprintf "the %s payload does not read as %s: %s" envelope.TypeName typeof<'T>.Name ex.Message)