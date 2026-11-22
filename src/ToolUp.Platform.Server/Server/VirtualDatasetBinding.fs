// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Infra

open ToolUp.Platform

open System
open System.Globalization
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// --- VirtualDatasetReader -----------------------------------------------
//
// The in-situ deployment model keeps the data in the deployment's own
// stores -- no copies, no transfers. A `Virtual` dataset version is defined
// as `(IDataSource ref, query, watermark)` and read through at page time;
// its "vintage" is a source watermark, not copied bytes.
//
// The query is either a string the connector interprets (`VirtualBinding`)
// or a bound statement (`BoundVirtualBinding`), whose text is the same for
// every version and whose watermark is a parameter. Whether a definition can
// be served by its source is decided before anything is sent to it
// (`Define`): a bound binding needs a connector that binds, and a source
// whose statement policy is `bound-only` admits no string binding.
//
// `VirtualDatasetReader` pages typed rows straight from an `IDataSource`,
// checking schema conformance at bind time and on every read (source drift
// is a typed error naming the changed column). Compute handoff for a virtual
// version is **ephemeral materialisation** with declared retention (spill ->
// hand off -> delete, every step audited), never a silent durable copy.

// --- The data-source query audit event ---------------------------------
//
// The platform's two call sites that run a statement against a data source,
// this reader and the data ingestor, each record one `DataSourceQueried` row
// per statement, failures and refusals included. A connector is never asked
// to emit it, so a third-party connector is covered without changing. The
// family's codec lives here, beside the first of the two sites, because the
// payload serialisation every audit kind shares (`PlatformJson.options`) is
// server-side; the payload and its kinds are Core's.

/// The data-source query audit family and the call sites' shared emission.
[<RequireQualifiedAccess>]
module DataSourceQueryAudit =

    let private write (payload: DataSourceQueriedPayload) : string =
        JsonSerializer.Serialize(payload, PlatformJson.options)

    let private read (json: string) : DataSourceQueriedPayload =
        JsonSerializer.Deserialize<DataSourceQueriedPayload>(json, PlatformJson.options)

    /// The family the platform registers in the composed audit-event
    /// registry beside its own kinds.
    let family: AuditEventFamily<DataSourceAuditEvent> = {
        Registrant = AuditEventRegistry.PlatformRegistrant
        Kinds = DataSourceAuditEvent.kinds
        Encode =
            function
            | DataSourceQueried p -> DataSourceAuditEvent.DataSourceQueriedKind, write p
        Decode =
            fun kind payload ->
                match kind with
                | DataSourceAuditEvent.DataSourceQueriedKind -> DataSourceQueried(read payload)
                | other -> failwithf "not a data-source audit kind: %s" other
    }

    /// The `AuditEvent` a query payload is recorded as.
    let event (payload: DataSourceQueriedPayload) : AuditEvent =
        AuditEventFamily.event family (DataSourceQueried payload)

    /// The query payload an `AuditEvent` carries, if it carries one.
    let tryRead (audit: AuditEvent) : DataSourceQueriedPayload option =
        AuditEventFamily.tryRead family audit
        |> Option.map (function
            | DataSourceQueried p -> p)

    /// Lowercase-hex SHA-256 over the UTF-8 bytes of a statement's text.
    let statementHash (text: string) : string = Sha256.hexOfUtf8 text

    /// What a call site knows of the statement before it runs it.
    type Statement = {
        Form: DataSourceStatementForm
        Text: string
        Parameters: DataSourceQueryParameter list
    }

    /// A string statement, as the connector's `Query` receives it.
    let ofText (text: string) : Statement = {
        Form = DataSourceStatementForm.Text
        Text = text
        Parameters = []
    }

    /// A bound statement: identified by its text alone, its parameters named
    /// with their kinds.
    let ofBound (statement: BoundStatement) : Statement = {
        Form = DataSourceStatementForm.Bound
        Text = statement.Text
        Parameters = DataSourceAuditEvent.parameters statement
    }

    /// The payload of one query against the source `config` names, through
    /// the connector of kind `connectorKind`. `recordText` is the source's
    /// `StatementTextAudit` opt-in; `answer` is the source's bytes when it
    /// answered; `rows` is how many rows the platform decoded from them.
    let payload
        (config: DataSourceConfig)
        (connectorKind: string)
        (recordText: bool)
        (statement: Statement)
        (answer: byte[] option)
        (rows: int64 option)
        (outcome: DataSourceQueryOutcome)
        : DataSourceQueriedPayload =
        {
            SourceId = config.Id
            ConnectorKind = connectorKind
            Form = statement.Form
            StatementHash = statementHash statement.Text
            Parameters = statement.Parameters
            StatementText = if recordText then Some statement.Text else None
            RowCount = rows
            ByteCount = answer |> Option.map (fun bytes -> int64 bytes.Length)
            ContentHash = answer |> Option.map Sha256.hex
            Outcome = outcome
        }

    /// The refusal class of a source whose statement-text audit opt-in is
    /// refused, at either call site.
    [<Literal>]
    let StatementTextAuditRefused = "StatementTextAuditRefused"

    /// The class an ingestion error records: its case name, never its
    /// message, which can echo the statement.
    let failureOf (error: IngestionError) : string =
        match error with
        | CredentialMissing _ -> "CredentialMissing"
        | SourceUnreachable _ -> "SourceUnreachable"
        | IngestionError.SchemaMismatch _ -> "SchemaMismatch"
        | IngestionError.StorageFailure _ -> "StorageFailure"
        | UnexpectedFailure _ -> "UnexpectedFailure"

    /// Record one query row under `scopeId`, attributed to `actor`. The
    /// payload is built only when a log is composed: under `NoOpAuditLog` no
    /// hash is taken and nothing is serialised, so a deployment with audit
    /// off pays nothing (GP 13). Answers the seam's decision.
    let record
        (log: IAuditLog)
        (scopeId: string)
        (actor: AuditSubject)
        (build: unit -> DataSourceQueriedPayload)
        : Async<Result<unit, AuditWriteError>> =
        match log with
        | :? NoOpAuditLog -> async { return Ok() }
        | _ -> log.Record(AuditScope.shard scopeId, actor, event (build ()))

/// Why a virtual binding cannot be defined over its source. Every case is
/// decided before anything is sent to the source.
[<RequireQualifiedAccess>]
type VirtualBindingRefusal =
    /// A bound binding over a connector that does not declare
    /// `ConnectorCapabilities.Binds`.
    | NotDeclared of CapabilityNotDeclared
    /// The bound statement at its watermark is refused by
    /// `BoundStatement.check` against the connector's bindable kinds.
    | Refused of BindingRefusal
    /// A shared parameter of a bound binding carries the reserved name
    /// `BoundVirtualBinding.WatermarkParameter`.
    | ReservedParameter
    /// A bound binding's watermark is a typed null: a vintage has a value.
    | NullWatermark
    /// A string binding over a source whose statement policy is
    /// `bound-only`, named by the source's id.
    | StatementTextRefused of sourceId: string
    /// The source's statement policy cannot be read.
    | PolicyUnreadable of reason: string
    /// The source's `StatementTextAudit` opt-in is refused: it is set under a
    /// statement policy that admits raw text, or it cannot be read.
    | StatementTextAuditRefused of reason: string

/// Rendering a `VirtualBindingRefusal`.
[<RequireQualifiedAccess>]
module VirtualBindingRefusal =

    /// A sentence naming what stops the definition.
    let describe (refusal: VirtualBindingRefusal) : string =
        match refusal with
        | VirtualBindingRefusal.NotDeclared declared ->
            $"a bound virtual binding needs a connector that binds statements: {CapabilityNotDeclared.describe declared}"
        | VirtualBindingRefusal.Refused refusal -> BindingRefusal.describe refusal
        | VirtualBindingRefusal.ReservedParameter ->
            $"parameter '{BoundVirtualBinding.WatermarkParameter}' is reserved for the binding's watermark"
        | VirtualBindingRefusal.NullWatermark -> "a bound virtual binding's watermark is a typed null"
        | VirtualBindingRefusal.StatementTextRefused sourceId ->
            $"source '{sourceId}' declares {StatementPolicy.Key}={StatementPolicy.token StatementPolicy.BoundOnly}, so a virtual binding over it carries a bound statement, never statement text. Nothing was sent to the source"
        | VirtualBindingRefusal.PolicyUnreadable reason -> $"the source's statement policy cannot be read: {reason}"
        | VirtualBindingRefusal.StatementTextAuditRefused reason ->
            $"the source's statement-text audit opt-in is refused: {reason}"

/// Reads a virtual dataset version through an `IDataSource`. Constructed with
/// the connector, the codec that decodes the connector's row bytes, the
/// version's **declared** schema (the conformance contract), and the
/// persisted `DataSourceConfig` used to build the per-call context and read
/// the source's statement policy.
///
/// Every statement the reader runs against the source, or refuses to run, is
/// recorded as one `DataSourceQueried` audit row attributed to `subject`: in
/// `audit` for `Bind` and `ReadPage`, and in the log a `MaterialiseForHandoff`
/// call is handed. The four-argument constructor composes `NoOpAuditLog`, so a
/// reader built without a log records nothing and pays nothing.
///
/// The connector returns opaque bytes; this reader decodes them through the
/// composed `IDatasetCodec` (the in-situ connector emits rows in the
/// deployment's dataset wire format). A string binding reads through
/// `IDataSource.Query`, a bound binding through `BoundQuery.run`. Pushdown of
/// the page window + filter predicates is the connector's where its query
/// supports it; the reader always applies the requested `Filter`s + paging
/// as a residual in memory, so correctness never depends on connector
/// pushdown.
type VirtualDatasetReader
    (
        source: IDataSource,
        codec: IDatasetCodec,
        declaredSchema: DatasetSchema,
        config: DataSourceConfig,
        audit: IAuditLog,
        subject: AuditSubject
    ) =

    [<Literal>]
    static let MaxPageLimit = 10000

    /// Reserved metadata keys stamped on an ephemeral spill blob.
    [<Literal>]
    static let SpillMarkerKey = "dataset.spill"

    [<Literal>]
    static let SpillExpiryKey = "dataset.spill.expiresAt"

    [<Literal>]
    static let SpillWatermarkKey = "dataset.spill.watermark"

    let callContext (scopeId: string) : DataSourceCallContext = {
        ScopeId = scopeId
        Config = config
        Credential = None
    }

    /// A bound binding's watermark as the vintage identity a spill and its
    /// audit row record, the text `VirtualBinding.Watermark` holds.
    let watermarkText (watermark: BoundValue) : string =
        match watermark with
        | BoundValue.Text text
        | BoundValue.Categorical text
        | BoundValue.Decimal text -> text
        | BoundValue.Int value -> value.ToString(CultureInfo.InvariantCulture)
        | BoundValue.Float value -> value.ToString("R", CultureInfo.InvariantCulture)
        | BoundValue.Bool value -> if value then "true" else "false"
        | BoundValue.Timestamp value -> value.ToString("o", CultureInfo.InvariantCulture)
        | BoundValue.Null _ -> ""

    /// The source's statement-text audit opt-in, refused where it is set
    /// under a policy that admits raw text.
    let recordsText () : Result<bool, VirtualBindingRefusal> =
        StatementTextAudit.read config.ConnectionScope
        |> Result.mapError VirtualBindingRefusal.StatementTextAuditRefused

    /// A string binding admitted by its source, answering the source's
    /// statement-text audit opt-in.
    let admitText (_: VirtualBinding) : Result<bool, VirtualBindingRefusal> =
        match StatementPolicy.read config.ConnectionScope with
        | Error reason -> Error(VirtualBindingRefusal.PolicyUnreadable reason)
        | Ok StatementPolicy.BoundOnly -> Error(VirtualBindingRefusal.StatementTextRefused config.Id)
        | Ok StatementPolicy.RawAllowed -> recordsText ()

    /// A bound binding admitted by its source, answering the source's
    /// statement-text audit opt-in.
    let admitBound (binding: BoundVirtualBinding) : Result<bool, VirtualBindingRefusal> =
        match ConnectorCapabilities.binds source with
        | Error declared -> Error(VirtualBindingRefusal.NotDeclared declared)
        | Ok binds ->
            if
                binding.Statement.Parameters
                |> List.exists (fun p -> p.Name = BoundVirtualBinding.WatermarkParameter)
            then
                Error VirtualBindingRefusal.ReservedParameter
            else
                match binding.Watermark with
                | BoundValue.Null _ -> Error VirtualBindingRefusal.NullWatermark
                | _ ->
                    BoundStatement.check binds.BindableKinds (BoundVirtualBinding.statement binding)
                    |> Result.mapError VirtualBindingRefusal.Refused
                    |> Result.bind recordsText

    let defineText (binding: VirtualBinding) : Result<unit, VirtualBindingRefusal> =
        admitText binding |> Result.map ignore

    let defineBound (binding: BoundVirtualBinding) : Result<unit, VirtualBindingRefusal> =
        admitBound binding |> Result.map ignore

    /// The class a refusal records in the query audit row: its case name, or
    /// a binding refusal's own case name.
    let refusalClass (refusal: VirtualBindingRefusal) : string =
        match refusal with
        | VirtualBindingRefusal.NotDeclared _ -> "NotDeclared"
        | VirtualBindingRefusal.Refused refusal ->
            match refusal with
            | BindingRefusal.InvalidName _ -> "InvalidName"
            | BindingRefusal.DuplicateName _ -> "DuplicateName"
            | BindingRefusal.UnusedParameter _ -> "UnusedParameter"
            | BindingRefusal.MissingParameter _ -> "MissingParameter"
            | BindingRefusal.UnbindableKind _ -> "UnbindableKind"
            | BindingRefusal.NonCanonicalDecimal _ -> "NonCanonicalDecimal"
        | VirtualBindingRefusal.ReservedParameter -> "ReservedParameter"
        | VirtualBindingRefusal.NullWatermark -> "NullWatermark"
        | VirtualBindingRefusal.StatementTextRefused _ -> "StatementTextRefused"
        | VirtualBindingRefusal.PolicyUnreadable _ -> "PolicyUnreadable"
        | VirtualBindingRefusal.StatementTextAuditRefused _ -> DataSourceQueryAudit.StatementTextAuditRefused

    /// Record one query row in `log`; the payload is built only when a log
    /// is composed.
    let recordQuery
        (log: IAuditLog)
        (scopeId: string)
        (statement: unit -> DataSourceQueryAudit.Statement)
        (recordText: bool)
        (answer: byte[] option)
        (rows: int64 option)
        (outcome: DataSourceQueryOutcome)
        =
        DataSourceQueryAudit.record log scopeId subject (fun () ->
            DataSourceQueryAudit.payload config source.Kind recordText (statement ()) answer rows outcome)

    let refused (refusal: VirtualBindingRefusal) =
        DatasetError.SchemaMismatch(VirtualBindingRefusal.describe refusal)

    let sourceFailed (failure: IngestionError) =
        DatasetError.StorageFailure(sprintf "virtual source read failed: %A" failure)

    /// Read-time conformance: every declared column must still be present in
    /// the source's returned schema with the same dtype. A missing or
    /// dtype-changed column is a typed error naming it (source drift).
    let checkConformance (actual: DatasetSchema) : Result<unit, string> =
        let actualByName = actual.Columns |> List.map (fun c -> c.Name, c) |> Map.ofList

        declaredSchema.Columns
        |> List.tryPick (fun d ->
            match Map.tryFind d.Name actualByName with
            | None -> Some(sprintf "source no longer provides column '%s'" d.Name)
            | Some a when a.DType <> d.DType ->
                Some(
                    sprintf
                        "source column '%s' changed dtype from %s to %s"
                        d.Name
                        (DatasetDType.name d.DType)
                        (DatasetDType.name a.DType)
                )
            | Some _ -> None)
        |> function
            | Some msg -> Error msg
            | None -> Ok()

    let opHolds (op: DatasetFilterOp) (sign: int) =
        match op with
        | DatasetFilterOp.Eq -> sign = 0
        | DatasetFilterOp.Ne -> sign <> 0
        | DatasetFilterOp.Lt -> sign < 0
        | DatasetFilterOp.Lte -> sign <= 0
        | DatasetFilterOp.Gt -> sign > 0
        | DatasetFilterOp.Gte -> sign >= 0

    /// Validate + apply the query's filters against the declared schema,
    /// preserving row order. Same semantics as the materialised store's
    /// `applyFilters` so a filtered read matches a materialised read.
    let applyFilters (filters: DatasetFilter list) (rows: DatasetRow list) : Result<DatasetRow list, string> =
        let resolved =
            filters
            |> List.map (fun f ->
                match DatasetSchema.indexOf f.Column declaredSchema with
                | None -> Error(sprintf "filter column '%s' is not in the schema" f.Column)
                | Some idx ->
                    let col = List.item idx declaredSchema.Columns

                    match f.Value with
                    | DatasetValue.Null -> Ok(idx, f.Op, f.Value)
                    | _ when DatasetValue.dtypeOf f.Value = Some col.DType -> Ok(idx, f.Op, f.Value)
                    | _ -> Error(sprintf "filter literal for column '%s' does not match its dtype" f.Column))

        match
            resolved
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some reason -> Error reason
        | None ->
            let preds =
                resolved
                |> List.choose (function
                    | Ok v -> Some v
                    | Error _ -> None)

            let rowPasses (row: DatasetRow) =
                preds
                |> List.forall (fun (idx, op, literal) ->
                    let cell = List.item idx row.Cells

                    match op, literal with
                    | DatasetFilterOp.Eq, DatasetValue.Null -> cell = DatasetValue.Null
                    | DatasetFilterOp.Ne, DatasetValue.Null -> cell <> DatasetValue.Null
                    | _ ->
                        match DatasetValue.compare cell literal with
                        | Some sign -> opHolds op sign
                        | None -> false)

            Ok(rows |> List.filter rowPasses)

    /// Decode the source's bytes and check conformance. Returns the decoded
    /// rows (unfiltered, unpaged).
    let decoded (bytes: byte[]) : Result<DatasetRow list, DatasetError> =
        match codec.Decode bytes with
        | Error reason -> Error(DatasetError.StorageFailure(sprintf "virtual source decode failed: %s" reason))
        | Ok(actualSchema, rows) ->
            match checkConformance actualSchema with
            | Error drift -> Error(DatasetError.SchemaMismatch drift)
            | Ok() -> Ok rows

    /// Refuse a statement before it is sent, recording the refusal.
    let refuse log scopeId statement (refusal: VirtualBindingRefusal) = async {
        do!
            recordQuery log scopeId statement false None None (DataSourceQueryOutcome.Refused(refusalClass refusal))
            |> AuditRow.bestEffort

        return Error(refused refusal)
    }

    /// A query the source failed, recorded by the error's class.
    let failed log scopeId statement recordText (failure: IngestionError) = async {
        do!
            recordQuery
                log
                scopeId
                statement
                recordText
                None
                None
                (DataSourceQueryOutcome.Failed(DataSourceQueryAudit.failureOf failure))
            |> AuditRow.bestEffort

        return Error(sourceFailed failure)
    }

    /// The source's answer, decoded and recorded. Rows the log refuses to
    /// record are withheld.
    let answered log scopeId statement recordText (bytes: byte[]) = async {
        let result = decoded bytes

        let rows, outcome =
            match result with
            | Ok rows -> Some(int64 (List.length rows)), DataSourceQueryOutcome.Succeeded
            | Error(DatasetError.SchemaMismatch _) -> None, DataSourceQueryOutcome.Failed "SchemaDrift"
            | Error _ -> None, DataSourceQueryOutcome.Failed "Undecodable"

        match! recordQuery log scopeId statement recordText (Some bytes) rows outcome with
        | Error e when Result.isOk result -> return Error(DatasetError.StorageFailure(AuditWriteError.describe e))
        | _ -> return result
    }

    /// Read a string binding through: refuse it first if its source admits
    /// no statement text, then query the source at the binding's watermark.
    /// One query row is recorded in `log`.
    let readText
        (log: IAuditLog)
        (scopeId: string)
        (binding: VirtualBinding)
        : Async<Result<DatasetRow list, DatasetError>> =
        async {
            let statement () =
                DataSourceQueryAudit.ofText binding.QuerySpec

            match admitText binding with
            | Error refusal -> return! refuse log scopeId statement refusal
            | Ok recordText ->
                match! source.Query(callContext scopeId, binding.QuerySpec) with
                | Error e -> return! failed log scopeId statement recordText e
                | Ok bytes -> return! answered log scopeId statement recordText bytes
        }

    /// Read a bound binding through the connector's binding capability, the
    /// watermark bound as the reserved parameter. One query row is recorded
    /// in `log`, identified by the statement's text alone.
    let readBound
        (log: IAuditLog)
        (scopeId: string)
        (binding: BoundVirtualBinding)
        : Async<Result<DatasetRow list, DatasetError>> =
        async {
            let bound = BoundVirtualBinding.statement binding
            let statement () = DataSourceQueryAudit.ofBound bound

            match admitBound binding with
            | Error refusal -> return! refuse log scopeId statement refusal
            | Ok recordText ->
                match! BoundQuery.run source (callContext scopeId) bound with
                | Error(BoundStatementError.Failed e) -> return! failed log scopeId statement recordText e
                | Error(BoundStatementError.NotDeclared declared) ->
                    return! refuse log scopeId statement (VirtualBindingRefusal.NotDeclared declared)
                | Error(BoundStatementError.Refused refusal) ->
                    return! refuse log scopeId statement (VirtualBindingRefusal.Refused refusal)
                | Ok bytes -> return! answered log scopeId statement recordText bytes
        }

    let bind
        (scopeId: string)
        (define: unit -> Result<unit, VirtualBindingRefusal>)
        (statement: unit -> DataSourceQueryAudit.Statement)
        (read: unit -> Async<Result<DatasetRow list, DatasetError>>)
        : Async<Result<unit, DatasetError>> =
        async {
            match define () with
            | Error refusal ->
                let! refusal = refuse audit scopeId statement refusal
                return refusal |> Result.map ignore
            | Ok() ->
                match! source.Connect(callContext scopeId) with
                | Error e -> return Error(DatasetError.StorageFailure(sprintf "virtual source unreachable: %A" e))
                | Ok() ->
                    match! read () with
                    | Error e -> return Error e
                    | Ok _ -> return Ok()
        }

    let readPage
        (query: DatasetPageQuery)
        (read: unit -> Async<Result<DatasetRow list, DatasetError>>)
        : Async<Result<DatasetPage, DatasetError>> =
        async {
            if query.Offset < 0L then
                return Error(DatasetError.InvalidPage "offset must be non-negative")
            elif query.Limit <= 0 then
                return Error(DatasetError.InvalidPage "limit must be positive")
            else
                match! read () with
                | Error e -> return Error e
                | Ok rows ->
                    match applyFilters query.Filters rows with
                    | Error reason -> return Error(DatasetError.SchemaMismatch reason)
                    | Ok filtered ->
                        let total = int64 (List.length filtered)
                        let count = List.length filtered

                        let skipN =
                            if query.Offset >= int64 count then
                                count
                            else
                                int query.Offset

                        let pageRows =
                            filtered |> List.skip skipN |> List.truncate (min query.Limit MaxPageLimit)

                        return
                            Ok {
                                Schema = declaredSchema
                                Rows = pageRows
                                Offset = query.Offset
                                TotalRows = total
                            }
        }

    let materialise
        (scopeId: string)
        (watermark: string)
        (read: Async<Result<DatasetRow list, DatasetError>>)
        (dataObjects: IDataObjectStore)
        (spillDatasetId: string)
        (ttl: TimeSpan)
        (labels: DataProvenanceLabel list)
        (audit: IAuditLog)
        (actor: string)
        : Async<Result<DatasetContentRef, DatasetError>> =
        async {
            match! read with
            | Error e -> return Error e
            | Ok rows ->
                let bytes = codec.Encode(declaredSchema, rows)
                let rowCount = int64 (List.length rows)
                let expiresAt = DateTime.UtcNow.Add ttl

                let metadata =
                    Map [
                        SpillMarkerKey, "true"
                        SpillExpiryKey, expiresAt.ToString("o")
                        SpillWatermarkKey, watermark
                    ]
                    |> DataProvenanceLabels.writeInto labels

                match!
                    dataObjects.Save(scopeId, spillDatasetId, bytes, "toolup.dataset.spill", actor, metadata, Versioned)
                with
                | Error e -> return Error(DatasetError.StorageFailure(sprintf "%A" e))
                | Ok dobj ->
                    do!
                        audit.Record(
                            AuditScope.shard scopeId,
                            AuditSubject.system "VirtualDataset",
                            DatasetSpillCreated {
                                Actor = actor
                                ScopeId = scopeId
                                SpillDatasetId = spillDatasetId
                                Watermark = watermark
                                ExpiresAt = expiresAt
                                RowCount = rowCount
                            }
                        )
                        |> AuditRow.orRefuse

                    return
                        Ok {
                            ScopeId = scopeId
                            DatasetId = spillDatasetId
                            Version = dobj.Version
                            ContentHash = dobj.ContentHash
                            Format = codec.Format
                            RowCount = rowCount
                        }
        }

    /// A reader that records no query audit rows of its own: its log is
    /// `NoOpAuditLog`. `MaterialiseForHandoff` still records its query in the
    /// log it is handed.
    new(source: IDataSource, codec: IDatasetCodec, declaredSchema: DatasetSchema, config: DataSourceConfig) =
        VirtualDatasetReader(
            source,
            codec,
            declaredSchema,
            config,
            NoOpAuditLog(),
            AuditSubject.system "VirtualDataset"
        )

    /// The declared schema of the virtual version (the conformance contract).
    member _.Schema: DatasetSchema = declaredSchema

    /// Definition-time check of a string binding, with nothing sent to the
    /// source: refused when the source's statement policy is `bound-only`.
    member _.Define(binding: VirtualBinding) : Result<unit, VirtualBindingRefusal> = defineText binding

    /// Definition-time check of a bound binding, with nothing sent to the
    /// source: the connector declares `Binds`, no shared parameter takes the
    /// reserved name, the watermark has a value, and the statement at the
    /// watermark passes `BoundStatement.check` against the connector's
    /// bindable kinds.
    member _.Define(binding: BoundVirtualBinding) : Result<unit, VirtualBindingRefusal> = defineBound binding

    /// Bind-time definition, conformance + reachability probe: refuse a
    /// definition the source cannot serve before anything is sent to it
    /// (`Define`, as `SchemaMismatch` naming the refusal), then confirm the
    /// source is reachable and its current schema conforms to the declared
    /// schema. `Ok ()` when a virtual version can be safely served from this
    /// binding.
    member _.Bind(scopeId: string, binding: VirtualBinding) : Async<Result<unit, DatasetError>> =
        bind scopeId (fun () -> defineText binding) (fun () -> DataSourceQueryAudit.ofText binding.QuerySpec) (fun () ->
            readText audit scopeId binding)

    /// `Bind` for a bound binding.
    member _.Bind(scopeId: string, binding: BoundVirtualBinding) : Async<Result<unit, DatasetError>> =
        bind
            scopeId
            (fun () -> defineBound binding)
            (fun () -> DataSourceQueryAudit.ofBound (BoundVirtualBinding.statement binding))
            (fun () -> readBound audit scopeId binding)

    /// Page typed rows straight from the source with no durable copy. Filters
    /// apply first (AND-combined) then `Offset` / `Limit` page over the
    /// matching rows. Schema conformance is re-checked on every read (source
    /// drift -> `SchemaMismatch` naming the changed column).
    member _.ReadPage
        (scopeId: string, binding: VirtualBinding, query: DatasetPageQuery)
        : Async<Result<DatasetPage, DatasetError>> =
        readPage query (fun () -> readText audit scopeId binding)

    /// `ReadPage` for a bound binding.
    member _.ReadPage
        (scopeId: string, binding: BoundVirtualBinding, query: DatasetPageQuery)
        : Async<Result<DatasetPage, DatasetError>> =
        readPage query (fun () -> readBound audit scopeId binding)

    /// Ephemeral materialisation for compute handoff -- the **declared,
    /// observable exception to zero-copy**. Reads the virtual version
    /// through, re-encodes it, and spills to a retention-bounded scratch blob
    /// under `spillDatasetId`, carrying the propagated `labels` and a
    /// declared `ttl` after which the spill is eligible for deletion. Emits a
    /// `DatasetSpillCreated` audit row so the copy is never silent. Returns
    /// the `DatasetContentRef` a worker fetches the raw bytes with.
    member _.MaterialiseForHandoff
        (
            scopeId: string,
            binding: VirtualBinding,
            dataObjects: IDataObjectStore,
            spillDatasetId: string,
            ttl: TimeSpan,
            labels: DataProvenanceLabel list,
            audit: IAuditLog,
            actor: string
        ) : Async<Result<DatasetContentRef, DatasetError>> =
        materialise
            scopeId
            binding.Watermark
            (readText audit scopeId binding)
            dataObjects
            spillDatasetId
            ttl
            labels
            audit
            actor

    /// `MaterialiseForHandoff` for a bound binding. The spill and its audit
    /// row record the watermark as its text and carry no other parameter
    /// value.
    member _.MaterialiseForHandoff
        (
            scopeId: string,
            binding: BoundVirtualBinding,
            dataObjects: IDataObjectStore,
            spillDatasetId: string,
            ttl: TimeSpan,
            labels: DataProvenanceLabel list,
            audit: IAuditLog,
            actor: string
        ) : Async<Result<DatasetContentRef, DatasetError>> =
        materialise
            scopeId
            (watermarkText binding.Watermark)
            (readBound audit scopeId binding)
            dataObjects
            spillDatasetId
            ttl
            labels
            audit
            actor

    /// Delete a spill blob and audit the deletion, closing the spill
    /// lifecycle. `reason` is `"ttl-expired"` for a retention sweep or
    /// `"explicit"` for an eager cleanup after handoff.
    member _.DeleteSpill
        (
            scopeId: string,
            spillDatasetId: string,
            dataObjects: IDataObjectStore,
            audit: IAuditLog,
            actor: string,
            reason: string
        ) : Async<Result<unit, DatasetError>> =
        async {
            match! dataObjects.Delete(scopeId, spillDatasetId) with
            | Error e -> return Error(DatasetError.StorageFailure(sprintf "%A" e))
            | Ok() ->
                do!
                    audit.Record(
                        AuditScope.shard scopeId,
                        AuditSubject.system "VirtualDataset",
                        DatasetSpillDeleted {
                            Actor = actor
                            ScopeId = scopeId
                            SpillDatasetId = spillDatasetId
                            Reason = reason
                        }
                    )
                    |> AuditRow.orRefuse

                return Ok()
        }

module VirtualDatasetReader =
    /// Construct a reader over the default JSON-frame codec (the in-situ
    /// connector emits rows in `toolup-frame-v1`). A deployment whose
    /// connector emits a different wire format constructs the reader with the
    /// matching codec.
    let create
        (source: IDataSource)
        (codec: IDatasetCodec)
        (declaredSchema: DatasetSchema)
        (config: DataSourceConfig)
        : VirtualDatasetReader =
        VirtualDatasetReader(source, codec, declaredSchema, config)