namespace ToolUp.Platform

open System

// ─── IDataIngestor ────────────────────────────────────────────────
//
// Server-side orchestrator interface for the data-ingestion substrate
// (Phase 10). One implementation per process — the SDK ships a
// default `DataIngestor` that:
//
//   1. Reads `DataSourceConfig` by id from `IDataSourceConfigStore`.
//   2. Resolves the matching `IDataSource` by `Kind`.
//   3. Resolves credentials via `ISecretStore.GetSecret(scopeId,
//      config.CredentialKey)`.
//   4. Calls `IDataSource.Connect` then `Query`.
//   5. Writes the returned bytes through `IDataObjectStore.Save`
//      with `Versioned` policy — each refresh creates a new version,
//      preserving history (Phase 7's immutability guarantee).
//   6. Records an `IngestionRun` for the attempt.
//   7. Emits `IngestionRunCompleted` / `IngestionRunFailed` events
//      to `IEventStore` under `SourceModule = "_platform.dataingestion"`
//      so audit + webhook subscribers pick them up.

type IDataIngestor =
    /// Execute a single ingestion attempt. Resolves the config, the
    /// connector, and credentials internally; the caller supplies
    /// only the scope, source id, and table.
    ///
    /// Returns `Ok IngestionRun` describing the completed attempt
    /// (whether the underlying source call succeeded or failed —
    /// the run is always recorded, the `Status` and `Error` fields
    /// distinguish outcomes). Returns `Error IngestionError` only
    /// for setup failures that prevent the attempt from running at
    /// all (config missing, connector unregistered).
    abstract RunIngestion:
        scopeId: string * sourceId: DataSourceId * table: string -> Async<Result<IngestionRun, IngestionError>>

    /// Read the most recent N ingestion-run rows for a source.
    /// Newest first. Backed by the same blob layout `RunIngestion`
    /// writes through. Empty list when no runs exist or when the
    /// source is unknown.
    abstract GetRecentRuns: scopeId: string * sourceId: DataSourceId * count: int -> Async<IngestionRun list>
// ─── The caller label (Phase 1041) ────────────────────────────────
//
// `RunIngestion(scopeId, sourceId, table)` names everything it writes by
// `table`: the payload object (`_dataingestion__{sourceId}__{table}`),
// the schema object, and the run record's `Table`. For a connector whose
// "table" is a SQL statement that name is the statement itself, and a
// statement longer than a path component cannot be saved. A caller label
// separates the two: the label names what the ingestion WRITES, and the
// statement/table — what the connector RUNS — reaches `GetSchema` and
// `Query` untouched.

/// Why a string was refused as an `IngestionLabel`.
type IngestionLabelError =
    /// The label is empty or whitespace.
    | EmptyLabel
    /// The label is longer than `IngestionLabel.MaxLength`.
    | LabelTooLong of maximum: int * actual: int
    /// The label holds a character outside `A-Z a-z 0-9 - _ .`.
    | LabelHasInvalidCharacter of character: char * position: int
    /// The label is `.` or `..`, which a path reads as a directory step.
    | LabelIsDotSegment of label: string

/// A validated caller label: a safe path component that names an
/// ingestion's result object, schema object and run record. Construct one
/// with `IngestionLabel.tryCreate`.
type IngestionLabel =
    private
    | IngestionLabel of string

    /// The label text.
    member this.Value =
        let (IngestionLabel value) = this
        value

module IngestionLabel =

    /// The longest label accepted. Well inside a 255-character path
    /// component once the ingestor's object-id prefix and the source id
    /// are added.
    [<Literal>]
    let MaxLength = 128

    let private allowed (c: char) =
        (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c = '-'
        || c = '_'
        || c = '.'

    /// Validate `label` as a safe path component, or say why it is not one.
    let tryCreate (label: string) : Result<IngestionLabel, IngestionLabelError> =
        if String.IsNullOrWhiteSpace label then
            Error EmptyLabel
        elif label.Length > MaxLength then
            Error(LabelTooLong(MaxLength, label.Length))
        elif label = "." || label = ".." then
            Error(LabelIsDotSegment label)
        else
            match Seq.tryFindIndex (allowed >> not) label with
            | Some position -> Error(LabelHasInvalidCharacter(label[position], position))
            | None -> Ok(IngestionLabel label)

    /// The label text.
    let value (label: IngestionLabel) = label.Value

    /// A one-line description of a refusal, naming what to change.
    let describeError (error: IngestionLabelError) =
        match error with
        | EmptyLabel -> "an ingestion caller label must not be empty"
        | LabelTooLong(maximum, actual) ->
            $"an ingestion caller label must be at most %d{maximum} characters; got %d{actual}"
        | LabelHasInvalidCharacter(character, position) ->
            $"an ingestion caller label may hold only A-Z, a-z, 0-9, '-', '_' and '.'; got '%c{character}' at position %d{position}"
        | LabelIsDotSegment label -> $"an ingestion caller label must not be the path step '%s{label}'"

/// Phase 1041 — an `IDataIngestor` that can name an ingestion's result by
/// a caller label. The SDK's `DataIngestor` implements it; call it through
/// the `RunIngestion(scopeId, sourceId, table, label)` overload below.
type ICallerLabelledIngestor =
    /// `IDataIngestor.RunIngestion`, naming the payload object, the schema
    /// object and the run record's `Table` by `label` when one is given.
    /// `table` (a table name or a statement) reaches the connector's
    /// `GetSchema` and `Query` untouched. With `label = None` this is
    /// exactly `RunIngestion(scopeId, sourceId, table)`.
    abstract RunLabelledIngestion:
        scopeId: string * sourceId: DataSourceId * table: string * label: IngestionLabel option ->
            Async<Result<IngestionRun, IngestionError>>

[<AutoOpen>]
module IDataIngestorExtensions =

    type IDataIngestor with

        /// Phase 1041 — run an ingestion whose result is named by `label`
        /// rather than by `table`, so a statement longer than a path
        /// component can be ingested with its run record, events and schema
        /// drift detection. An ingestor that does not implement
        /// `ICallerLabelledIngestor` refuses without running anything.
        member ingestor.RunIngestion
            (scopeId: string, sourceId: DataSourceId, table: string, label: IngestionLabel)
            : Async<Result<IngestionRun, IngestionError>> =
            match box ingestor with
            | :? ICallerLabelledIngestor as labelled ->
                labelled.RunLabelledIngestion(scopeId, sourceId, table, Some label)
            | _ -> async {
                return
                    Error(
                        UnexpectedFailure
                            $"the composed IDataIngestor ({ingestor.GetType().Name}) cannot name a result by a caller label; nothing was run"
                    )
              }