// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Compose

open System
open System.IO
open System.Security.Cryptography
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions

// ─── Declared reference data ────────────────────────────
//
// A dataset an application ships beside itself and declares by content hash.
// Its bytes must hash to the declared hash, its first row must be the declared
// column names in order, and every cell must read as its column's type, or the
// application does not start. Replacing a dataset is a data change plus a hash
// in the declaration, never a code change.
//
// The CSV dialect: UTF-8, one record per line (LF or CRLF), fields separated
// by ',', no quoting, so no field carries a comma, a quote or a line break.
// Lines before the header beginning with '#' are comments: hashed, and
// otherwise ignored.

/// A reference column's type.
[<RequireQualifiedAccess>]
type ReferenceColumnType =
    /// `YYYY-MM-DD`.
    | Date
    /// An optional `-` then ASCII digits, within the signed 64-bit range.
    | Integer
    /// Any text.
    | Text

/// One column of a reference dataset.
type ReferenceColumn = {
    Name: string
    Type: ReferenceColumnType
}

/// A reference dataset as the application declares it.
type ReferenceDataset = {
    /// The id it is read back by.
    Id: string
    /// The file, relative to the application's base directory or absolute.
    Source: string
    /// `sha256:` and the lower-case hex SHA-256 of the file's bytes.
    ContentHash: string
    Columns: ReferenceColumn list
}

/// One typed cell.
[<RequireQualifiedAccess>]
type ReferenceCell =
    | Date of DateOnly
    | Integer of int64
    | Text of string

/// A verified dataset: its declaration and its rows, each row's cells in column
/// order.
type VerifiedReferenceDataset = {
    Declaration: ReferenceDataset
    Rows: ReferenceCell list list
}

/// Raised by `ReferenceData.verifyAll` when a dataset does not verify. It names
/// every failure, so one start reports them all.
type ReferenceDataRefusedException(refusals: string list) =
    inherit InvalidOperationException("reference data refused: " + String.concat "; " refusals)

    /// Each dataset's refusal.
    member _.Refusals = refusals

/// The verified datasets of a deployment, by id.
type IReferenceData =
    /// The verified dataset with this id, or `None` for an id nothing declared.
    abstract Dataset: id: string -> VerifiedReferenceDataset option

[<RequireQualifiedAccess>]
module ReferenceData =

    /// The one spelling of a content hash: `sha256:` and the lower-case hex
    /// SHA-256 of the bytes.
    let hashOf (bytes: byte[]) : string =
        "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let private cellOf (column: ReferenceColumn) (raw: string) : Result<ReferenceCell, string> =
        let invariant = Globalization.CultureInfo.InvariantCulture

        match column.Type with
        | ReferenceColumnType.Date ->
            match DateOnly.TryParseExact(raw, "yyyy-MM-dd", invariant, Globalization.DateTimeStyles.None) with
            | true, date -> Ok(ReferenceCell.Date date)
            | _ -> Error(sprintf "column '%s' is a date (YYYY-MM-DD), and '%s' is not one" column.Name raw)
        | ReferenceColumnType.Integer ->
            // No '+', no white space, no separator.
            let digits = if raw.StartsWith '-' then raw.Substring 1 else raw

            match Int64.TryParse(raw, Globalization.NumberStyles.AllowLeadingSign, invariant) with
            | true, number when digits <> "" && Seq.forall Char.IsAsciiDigit digits -> Ok(ReferenceCell.Integer number)
            | _ -> Error(sprintf "column '%s' is an integer, and '%s' is not one" column.Name raw)
        | ReferenceColumnType.Text -> Ok(ReferenceCell.Text raw)

    let private rowOf (dataset: ReferenceDataset) (line: int, fields: string[]) =
        let named message =
            sprintf "reference data '%s' (%s) line %d: %s" dataset.Id dataset.Source line message

        if fields.Length <> dataset.Columns.Length then
            Error(named (sprintf "%d fields, and the header declares %d" fields.Length dataset.Columns.Length))
        else
            List.zip dataset.Columns (List.ofArray fields)
            |> List.fold
                (fun cells (column, raw) ->
                    cells
                    |> Result.bind (fun read -> cellOf column raw |> Result.map (fun cell -> cell :: read)))
                (Ok [])
            |> Result.map List.rev
            |> Result.mapError named

    /// The numbered lines of the text: line separators removed, and the empty
    /// line a final separator leaves dropped, for it is no record.
    let private linesOf (text: string) : (int * string) list =
        let lines =
            text.Split('\n')
            |> Array.mapi (fun index line -> index + 1, line.TrimEnd('\r'))
            |> List.ofArray

        match List.rev lines with
        | (_, "") :: rest -> List.rev rest
        | _ -> lines

    /// Read one dataset from its bytes, verifying the hash first, then the
    /// header, then every cell. Pure.
    let read (dataset: ReferenceDataset) (bytes: byte[]) : Result<VerifiedReferenceDataset, string> =
        let named message =
            sprintf "reference data '%s' (%s): %s" dataset.Id dataset.Source message

        let actual = hashOf bytes

        if actual <> dataset.ContentHash then
            Error(named (sprintf "the declaration names %s, and the file hashes to %s" dataset.ContentHash actual))
        else
            try
                let lines =
                    linesOf (Text.UTF8Encoding(false, true).GetString bytes)
                    |> List.skipWhile (fun (_, line) -> line.StartsWith '#')

                let expected = dataset.Columns |> List.map _.Name |> String.concat ","

                match lines with
                | [] -> Error(named "the file has no header")
                | (_, header) :: _ when header <> expected ->
                    Error(named (sprintf "the header is '%s', and the declaration names '%s'" header expected))
                | _ :: rows ->
                    rows
                    |> List.fold
                        (fun read (line, row: string) ->
                            read
                            |> Result.bind (fun parsed ->
                                rowOf dataset (line, row.Split ',') |> Result.map (fun cells -> cells :: parsed)))
                        (Ok [])
                    |> Result.map (fun rows -> {
                        Declaration = dataset
                        Rows = List.rev rows
                    })
            with :? Text.DecoderFallbackException ->
                Error(named "the file is not UTF-8")

    /// Read and verify every dataset against files under `baseDirectory`, and
    /// raise `ReferenceDataRefusedException` naming every failure: a repeated
    /// id, a missing file, and each `read` refusal.
    let verifyAll (baseDirectory: string) (datasets: ReferenceDataset list) : VerifiedReferenceDataset list =
        let repeated =
            datasets
            |> List.countBy _.Id
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (id, _) -> sprintf "reference data '%s' is declared more than once" id)

        let outcomes =
            datasets
            |> List.map (fun dataset ->
                let path = Path.Combine(baseDirectory, dataset.Source)

                if File.Exists path then
                    read dataset (File.ReadAllBytes path)
                else
                    Error(sprintf "reference data '%s': %s is not beside the application" dataset.Id dataset.Source))

        let failures =
            outcomes
            |> List.choose (function
                | Error refusal -> Some refusal
                | Ok _ -> None)

        match repeated @ failures with
        | [] ->
            outcomes
            |> List.choose (function
                | Ok verified -> Some verified
                | Error _ -> None)
        | refusals -> raise (ReferenceDataRefusedException refusals)

    /// One `ServerApp.withReferenceData` call's verified datasets, as a service
    /// the registry gathers.
    type private VerifiedSet = {
        Datasets: VerifiedReferenceDataset list
    }

    /// The registry over verified datasets, whose ids are distinct.
    let ofDatasets (datasets: VerifiedReferenceDataset list) : IReferenceData =
        let byId =
            datasets
            |> List.map (fun dataset -> dataset.Declaration.Id, dataset)
            |> Map.ofList

        { new IReferenceData with
            member _.Dataset id = Map.tryFind id byId
        }

    /// Verify `datasets` now, against files beside the application, and return
    /// the registration that makes them readable through `IReferenceData`. Nothing
    /// serves over a dataset that does not verify, because this raises before the
    /// application is composed further. Several calls gather into one registry; an
    /// id two of them declare is refused when the registry is built.
    let register (datasets: ReferenceDataset list) : IServiceCollection -> IServiceCollection =
        let verified = verifyAll AppContext.BaseDirectory datasets

        fun services ->
            services.AddSingleton<VerifiedSet>({ Datasets = verified }) |> ignore

            services.TryAddSingleton<IReferenceData>(
                Func<IServiceProvider, IReferenceData>(fun provider ->
                    let all =
                        provider.GetServices<VerifiedSet>() |> Seq.collect _.Datasets |> List.ofSeq

                    match
                        all
                        |> List.countBy _.Declaration.Id
                        |> List.filter (fun (_, count) -> count > 1)
                    with
                    | [] -> ofDatasets all
                    | repeats ->
                        repeats
                        |> List.map (fun (id, _) -> sprintf "reference data '%s' is declared more than once" id)
                        |> ReferenceDataRefusedException
                        |> raise)
            )

            services