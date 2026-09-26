// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Native payload formats — the reader seam (Phase 837) ─────────
//
// CSV is the family default and stays the fallback, but it is not a
// requirement: a connector whose source format is strictly richer
// (Parquet carries nulls in definition levels and types in its footer)
// may hand the ingestor its NATIVE bytes instead of flattening them to
// CSV at the source. Two seams make that possible without dragging a
// vendor SDK into `ToolUp.Platform.*` (GP 1):
//
//   * `IEmitsNativePayload` — implemented by a connector, alongside
//     `IDataSource`, that can ALSO emit a native format. Its `Query`
//     keeps emitting what `IDeclaresPayloadFormat` declares (CSV), so a
//     deployment that composes no reader for the native format is
//     byte-for-byte unchanged.
//   * `IPayloadReader` — reads stored payload bytes of one declared
//     format into typed rows. The implementation lives in the companion
//     that already carries the format's dependency; a deployment opts
//     into a native format by composing its reader (GP 13).
//
// The ingestor takes the native path only when BOTH are present, and
// still never parses: it stores the bytes unchanged and records their
// format as `IngestedPayload.ContentFormatKey`. A module reading the
// payload asks `PayloadReader.read`, never the format — and a payload
// whose declared format has no composed reader is a named refusal
// (`PayloadReadError.NoReaderComposed`), never a silent misparse.
//
// A native format is named by the companion as `PayloadFormat.Other`
// (the Parquet companion's is `"parquet"`): the platform does not name
// formats it cannot read, which is exactly the `Other` case's meaning.

/// Phase 837 — reads stored payload bytes of one declared format into the
/// platform's typed rows. The schema and every cell's type and nullness
/// come from the payload itself — a reader never re-infers what the
/// format already states. Stateless; no vendor type crosses the seam.
type IPayloadReader =
    /// The payload format this reader reads, matched against a payload's
    /// declared `content-format` token (`PayloadFormat.token`).
    abstract Format: PayloadFormat

    /// Decode `payload` into its declared schema and rows. `Error reason`
    /// for bytes that are not a readable payload of `Format` — a reader
    /// returns `Error` rather than throwing.
    abstract Read: payload: byte[] -> Result<DatasetSchema * DatasetRow list, string>

/// Phase 837 — implemented alongside `IDataSource` by a connector that can
/// also emit its source's native format. Optional: the ingestor calls
/// `QueryNative` only when a reader for `NativeFormat` is composed, and
/// falls back to `IDataSource.Query` (the connector's declared format)
/// otherwise.
type IEmitsNativePayload =
    /// The native format `QueryNative` emits. A constant of the connector.
    abstract NativeFormat: PayloadFormat

    /// Run a query and return the source's bytes in `NativeFormat`,
    /// unconverted. Same dialect and error contract as `IDataSource.Query`.
    abstract QueryNative: ctx: DataSourceCallContext * sql: string -> Async<Result<byte[], IngestionError>>

/// Phase 837 — why a stored payload could not be read through the seam.
[<RequireQualifiedAccess>]
type PayloadReadError =
    /// The payload declares a format (its token) for which the deployment
    /// composed no `IPayloadReader`. Named, so the operator knows which
    /// reader to compose.
    | NoReaderComposed of format: string
    /// A reader for the format is composed but refused these bytes.
    | Unreadable of format: string * reason: string

/// Phase 837 — asking the composed readers for a stored payload.
module PayloadReader =

    /// The composed reader for `format`, if any. Matched by token, so two
    /// spellings of one `Other` name cannot diverge.
    let forFormat (readers: IPayloadReader list) (format: PayloadFormat) : IPayloadReader option =
        let wanted = PayloadFormat.token format

        readers
        |> List.tryFind (fun reader -> PayloadFormat.token reader.Format = wanted)

    /// Whether a reader for `format` is composed.
    let handles (readers: IPayloadReader list) (format: PayloadFormat) : bool = (forFormat readers format).IsSome

    /// Read a stored payload's bytes through the reader for the format the
    /// payload DECLARES. A payload that declares none (written before
    /// Phase 834) is CSV by convention. A reader that throws is reported
    /// as `Unreadable`, not propagated.
    let read
        (readers: IPayloadReader list)
        (payload: DataObject)
        (bytes: byte[])
        : Result<DatasetSchema * DatasetRow list, PayloadReadError> =
        let format =
            IngestedPayload.contentFormat payload |> Option.defaultValue PayloadFormat.Csv

        let token = PayloadFormat.token format

        match forFormat readers format with
        | None -> Error(PayloadReadError.NoReaderComposed token)
        | Some reader ->
            try
                reader.Read bytes
                |> Result.mapError (fun reason -> PayloadReadError.Unreadable(token, reason))
            with ex ->
                Error(PayloadReadError.Unreadable(token, ex.Message))

    /// Operator-facing rendering of a `PayloadReadError`.
    let describe (error: PayloadReadError) : string =
        match error with
        | PayloadReadError.NoReaderComposed format ->
            $"the payload declares format '{format}', and this deployment composes no IPayloadReader for it"
        | PayloadReadError.Unreadable(format, reason) -> $"the '{format}' reader refused the payload: {reason}"