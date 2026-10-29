module DailyTrade.Server

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Facts
open ProcessedDataTypes
open DailyTrade.SharedTypes

// ─── What the module computes ─────────────────────────────────────
//
// Two fact tables, one row per subject per day. Every column is a
// registered metric and every hierarchy a registered subject, which the
// platform checks at compose. The facts are `Surfaceable`: a grounded
// narrative may publish them. Make a column `Restricted "<policy>"` to hold
// it back behind a disclosure policy instead.

[<Literal>]
let ModuleName = "DailyTrade"

/// The operation every fact this module writes names as its producer.
[<Literal>]
let IngestOperation = "dailyTrade.ingest"

let private metric id name unit dimensionality format rollUp : Grounding.MetricDefinition = {
    Id = id
    Name = name
    Unit = unit
    Dimensionality = dimensionality
    Direction = HigherIsBetter
    DisplayFormat = format
    Staleness = UntilSuperseded
    ProducingOperation = Some IngestOperation
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = Some rollUp
    Context = None
}

let metrics: Grounding.MetricDefinition list = [
    metric "orders" "Orders" "count" "count" "N0" (Additive 0m)
    metric "shoppers" "Shoppers" "count" "count" "N0" NonAdditive
    metric "revenue" "Revenue" "GBP" "currency" "C0" (Additive 0.01m)
    metric "units" "Units" "count" "count" "N0" (Additive 0m)
    metric "aov" "Average order value" "GBP" "currency" "C2" NonAdditive
]

let subjects: SubjectDefinition list = [
    {
        Id = "site"
        Name = "Site"
        Levels = [ "site" ]
        Calendar = Some "fiscal"
    }
    {
        Id = "channel"
        Name = "Site and channel"
        Levels = [ "site"; "channel" ]
        Calendar = Some "fiscal"
    }
]

let private table id hierarchy level (columns: string list) : FactTableDefinition = {
    Id = id
    SchemaVersion = 1
    Hierarchy = hierarchy
    Level = level
    Columns =
        columns
        |> List.map (fun m -> FactTableDefinition.column m FactTableValueShape.Scalar)
    PeriodGrain = FactTablePeriodGrain.Day
    ProducingOperation = IngestOperation
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.AppendByRun
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Required
}

[<Literal>]
let SiteTable = "trade.site"

[<Literal>]
let ChannelTable = "trade.channel"

let factTables: FactTableDefinition list = [
    table SiteTable "site" "site" [ "orders"; "revenue"; "units"; "aov" ]
    table ChannelTable "channel" "channel" [ "orders"; "shoppers"; "revenue" ]
]

// ─── The reader: validate the whole delivery, or reject it ────────

/// The columns a delivery must carry, in this order.
let header = [ "Date"; "Site"; "Channel"; "Orders"; "Shoppers"; "Revenue"; "Units" ]

/// One validated line of a delivery.
type TradeLine = {
    Date: DateOnly
    Site: string
    Channel: string
    Orders: decimal
    Shoppers: decimal
    Revenue: decimal
    Units: decimal
}

let private isMemberId (value: string) =
    value <> ""
    && value
       |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-')

/// Parse and validate a delivery. Every problem is reported, with its line
/// number, and any problem rejects the whole delivery: a partial day is
/// worse than no day, because a narrative would read it as complete.
/// `fiscalWeek` maps a business date to its fiscal label; a date the
/// calendar does not hold is a problem.
let read (fiscalWeek: DateOnly -> string option) (csv: string) : Result<TradeLine list, string list> =
    let lines =
        csv.Replace("\r\n", "\n").Split('\n')
        |> Array.mapi (fun i l -> i + 1, l.Trim())
        // Blank lines and `#` comment lines (a file's self-description) are skipped.
        |> Array.filter (fun (_, l) -> l <> "" && not (l.StartsWith "#"))
        |> List.ofArray

    match lines with
    | [] -> Error [ "the delivery is empty" ]
    | (_, first) :: rows ->
        let columns = first.Split(',') |> Array.map _.Trim() |> List.ofArray

        if columns <> header then
            Error [
                sprintf "line 1: expected the header %s, found %s" (String.concat "," header) first
            ]
        else
            let parseLine (number: int, text: string) : Result<TradeLine, string list> =
                let fields = text.Split(',') |> Array.map _.Trim()
                let problem what = sprintf "line %d: %s" number what

                if text.Contains "\"" then
                    Error [ problem "quoted fields are not supported by this reader" ]
                elif fields.Length <> header.Length then
                    Error [ problem (sprintf "expected %d fields, found %d" header.Length fields.Length) ]
                else
                    let amount (name: string) (value: string) =
                        match Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture) with
                        | true, v when v >= 0m -> Ok v
                        | _ -> Error(problem (sprintf "%s '%s' is not a non-negative number" name value))

                    let date =
                        match
                            DateOnly.TryParseExact(
                                fields[0],
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None
                            )
                        with
                        | true, d when (fiscalWeek d).IsSome -> Ok d
                        | true, d -> Error(problem (sprintf "%O is not in the fiscal calendar" d))
                        | _ -> Error(problem (sprintf "date '%s' is not yyyy-MM-dd" fields[0]))

                    let memberId (name: string) (value: string) =
                        if isMemberId value then
                            Ok value
                        else
                            Error(problem (sprintf "%s '%s' must be lowercase letters, digits and '-'" name value))

                    let results = [
                        date |> Result.map ignore
                        memberId "site" fields[1] |> Result.map ignore
                        memberId "channel" fields[2] |> Result.map ignore
                        amount "orders" fields[3] |> Result.map ignore
                        amount "shoppers" fields[4] |> Result.map ignore
                        amount "revenue" fields[5] |> Result.map ignore
                        amount "units" fields[6] |> Result.map ignore
                    ]

                    match
                        results
                        |> List.choose (function
                            | Error e -> Some e
                            | Ok() -> None)
                    with
                    | [] ->
                        let ok r =
                            match r with
                            | Ok v -> v
                            | Error e -> failwith e

                        Ok {
                            Date = ok date
                            Site = fields[1]
                            Channel = fields[2]
                            Orders = ok (amount "orders" fields[3])
                            Shoppers = ok (amount "shoppers" fields[4])
                            Revenue = ok (amount "revenue" fields[5])
                            Units = ok (amount "units" fields[6])
                        }
                    | problems -> Error problems

            let parsed = rows |> List.map parseLine

            let duplicates =
                parsed
                |> List.choose (function
                    | Ok l -> Some(l.Date, l.Site, l.Channel)
                    | Error _ -> None)
                |> List.countBy id
                |> List.filter (fun (_, n) -> n > 1)
                |> List.map (fun ((d, s, c), _) -> sprintf "%O %s/%s appears more than once" d s c)

            match
                (parsed
                 |> List.collect (function
                     | Error e -> e
                     | Ok _ -> []))
                @ duplicates
            with
            | [] when List.isEmpty rows -> Error [ "the delivery has a header and no rows" ]
            | [] ->
                Ok(
                    parsed
                    |> List.choose (function
                        | Ok l -> Some l
                        | Error _ -> None)
                )
            | problems -> Error problems

// ─── Rows, per table ──────────────────────────────────────────────

let private dayOf (fiscalWeek: DateOnly -> string option) (date: DateOnly) : TemporalExtent =
    let from = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)

    {
        From = from
        To = from.AddDays 1.0
        Label = fiscalWeek date
    }

/// The site table's rows: channels summed per site and day, with the
/// average order value computed from the sums.
let siteRows (fiscalWeek: DateOnly -> string option) (lines: TradeLine list) : FactTableRow list =
    lines
    |> List.groupBy (fun l -> l.Date, l.Site)
    |> List.map (fun ((date, site), group) ->
        let orders = group |> List.sumBy _.Orders
        let revenue = group |> List.sumBy _.Revenue

        {
            Subject = [ site ]
            Period = dayOf fiscalWeek date
            Values =
                Map [
                    "orders", Scalar orders
                    "revenue", Scalar revenue
                    "units", Scalar(group |> List.sumBy _.Units)
                    "aov",
                    (if orders = 0m then
                         Absent "no orders"
                     else
                         Scalar(Math.Round(revenue / orders, 2)))
                ]
        })

/// The channel table's rows, one per line of the delivery.
let channelRows (fiscalWeek: DateOnly -> string option) (lines: TradeLine list) : FactTableRow list =
    lines
    |> List.map (fun l -> {
        Subject = [ l.Site; l.Channel ]
        Period = dayOf fiscalWeek l.Date
        Values =
            Map [
                "orders", Scalar l.Orders
                "shoppers", Scalar l.Shoppers
                "revenue", Scalar l.Revenue
            ]
    })

// ─── The data source ──────────────────────────────────────────────
//
// Deliveries arrive by upload: the module stores the file in the caller's
// blob container and reads it back through this `IDataSource`, the same
// seam a connector to a real system implements. To read from a database,
// a share or an API instead, replace this source with that connector and
// keep everything below it.

[<Literal>]
let SourceKind = "dailytrade-upload"

type UploadedDeliverySource(storage: IBlobStorage) =
    interface IDataSource with
        member _.Kind = SourceKind
        member _.Capabilities = ConnectorCapabilities.none
        member _.Connect(_ctx) = async { return Ok() }

        member _.ListTables(ctx) = async {
            let! names = storage.List(ctx.Config.ConnectionScope["container"], "dailytrade/")
            return Ok names
        }

        member _.GetSchema(_ctx, table) = async { return Ok { TableName = table; Columns = [] } }

        /// `sql` names the stored delivery to read.
        member _.Query(ctx, sql) = async {
            match! storage.Download(ctx.Config.ConnectionScope["container"], sql) with
            | Ok bytes -> return Ok bytes
            | Error e -> return Error(SourceUnreachable e)
        }

let private sha256 (bytes: byte[]) =
    "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private service<'T> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService typeof<'T> with
    | :? 'T as s -> Some s
    | _ -> None

/// Write one table's rows as one run over the delivery's period slice, so
/// days outside the delivery keep the facts earlier deliveries wrote.
let private writeTable
    (writer: IFactTableWriter)
    (scopeId: string)
    (options: FactTableRunOptions)
    (tableId: string)
    (rows: FactTableRow list)
    : Async<Result<TableWritten, string>> =
    async {
        match! writer.OpenRun(scopeId, tableId, options = options) with
        | Error e -> return Error(FactTableWriteError.describe e)
        | Ok run ->
            match! writer.WriteRows(scopeId, run.RunId, rows) with
            | Error e ->
                let! _ = writer.Abandon(scopeId, run.RunId, FactTableWriteError.describe e)
                return Error(FactTableWriteError.describe e)
            | Ok _ ->
                match! writer.Commit(scopeId, run.RunId) with
                | Ok commit ->
                    return
                        Ok {
                            TableId = tableId
                            Rows = commit.RowCount
                        }
                | Error e -> return Error(FactTableWriteError.describe e)
    }

let private rejected problems = {
    Result = {
        Accepted = false
        Problems = problems
        Tables = []
        Narratives = []
    }
    Processed = None
}

/// One delivery, end to end: store it, read it back through the data
/// source, validate it, write both fact tables, then run each grounded
/// narrative in `narratives` under the scope the platform resolved for
/// THIS request, so the narrative reads exactly the facts just written.
let upload
    (fiscalWeek: DateOnly -> string option)
    (narratives: string list)
    (ctx: HttpContext)
    (request: UploadRequest)
    : Async<UploadResponse> =
    async {
        let scope = StorageScopeResolver.ScopeResolution.forRequest ctx

        match scope.Storage, service<IBlobStorage> ctx, service<IFactTableWriter> ctx with
        | None, _, _ -> return rejected [ "the request resolved no storage scope; sign in to a team first" ]
        | _, None, _ -> return rejected [ "no blob storage is composed" ]
        | _, _, None -> return rejected [ "no fact table writer is composed (the fact tier is off)" ]
        | Some storageScope, Some storage, Some writer ->
            let bytes = Encoding.UTF8.GetBytes request.Csv
            let hash = sha256 bytes
            let blobName = sprintf "dailytrade/%s.csv" (hash.Substring 7)

            let source = UploadedDeliverySource storage :> IDataSource

            let call: DataSourceCallContext = {
                ScopeId = scope.ScopeId
                Config = {
                    Id = "dailytrade-uploads"
                    Name = "Daily trade uploads"
                    Kind = SourceKind
                    ConnectionScope = Map [ "container", storageScope.Container ]
                    CredentialKey = ""
                    Tables = None
                    Tags = Map.empty
                }
                Credential = None
            }

            match! storage.Upload(storageScope.Container, blobName, bytes) with
            | Error e -> return rejected [ sprintf "could not store the delivery: %s" e ]
            | Ok _ ->
                match! source.Query(call, blobName) with
                | Error e -> return rejected [ sprintf "could not read the delivery back: %A" e ]
                | Ok stored ->
                    match read fiscalWeek (Encoding.UTF8.GetString stored) with
                    | Error problems -> return rejected problems
                    | Ok lines ->
                        let first = lines |> List.minBy _.Date
                        let last = lines |> List.maxBy _.Date

                        let slice: TemporalExtent = {
                            From = (dayOf fiscalWeek first.Date).From
                            To = (dayOf fiscalWeek last.Date).To
                            Label = None
                        }

                        let options =
                            FactTableRunOptions.none
                            |> FactTableRunOptions.withInputs [ hash ]
                            |> FactTableRunOptions.forSlice slice

                        let write = writeTable writer scope.ScopeId options
                        let! site = write SiteTable (siteRows fiscalWeek lines)
                        let! channel = write ChannelTable (channelRows fiscalWeek lines)

                        match site, channel with
                        | Ok s, Ok c ->
                            let! outcomes =
                                match service<IGroundedNarrativeRun> ctx with
                                | None -> async {
                                    return
                                        narratives
                                        |> List.map (sprintf "%s: no narrative run is composed")
                                        |> Array.ofList
                                  }
                                | Some run ->
                                    narratives
                                    |> List.map (fun key -> async {
                                        let! outcome =
                                            run.Run {
                                                RunKey = key
                                                Scope = scope
                                                Access = None
                                                Trigger = "dailytrade-upload"
                                            }

                                        return sprintf "%s: %s" key (GroundedNarrativeOutcome.describe outcome)
                                    })
                                    |> Async.Sequential

                            let result = {
                                Accepted = true
                                Problems = []
                                Tables = [ s; c ]
                                Narratives = List.ofArray outcomes
                            }

                            return {
                                Result = result
                                Processed =
                                    Some(
                                        ProcessedFileEntry.summarised
                                            request.FileName
                                            ModuleName
                                            DateTime.UtcNow
                                            (ProcessedDataCodec.encode result)
                                    )
                            }
                        | _ ->
                            return
                                rejected [
                                    for r in [ site; channel ] do
                                        match r with
                                        | Error e -> yield e
                                        | Ok _ -> ()
                                ]
    }

/// The module as the server composes it: its metrics, subjects and fact
/// tables, and its API. `narratives` are the grounded narrative run keys a
/// successful delivery triggers.
let serverModule (fiscalWeek: DateOnly -> string option) (narratives: string list) : ServerModule =
    ServerModule.create ModuleName
    |> ServerModule.declareMetrics metrics
    |> ServerModule.declareSubjects subjects
    |> ServerModule.declareFactTables factTables
    |> ServerModule.withGuardedApi (fun ctx -> {
        Upload = upload fiscalWeek narratives ctx
    })