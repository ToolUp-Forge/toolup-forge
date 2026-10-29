// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Common

open System
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

// ─── Phase 1015 — a declared source's delivery, run as its principal ───
//
// A data source a deployment declares in its composition says how its data
// arrives (`DataSourceDelivery`): pulled on a cadence, or ingested as it
// lands at a watched location. Either runs as a principal the deployment
// named, never as a scope. At startup the deployment's own scope resolver
// resolves the principal (`DeclaredPrincipalResolver`, Phase 1005), the
// source's config is seeded at that scope through `IDataSourceConfigStore`,
// and its delivery's jobs are scheduled under the resolution through the
// typed `IJobScheduler.Schedule`, whose carrier seals the scope onto each
// job (Phase 935). No scope is ever built from a string (Phase 797).
//
// **Both deliveries commit through the ordinary ingestion path.** A pull
// job is the platform's own `DataIngestionJobHandler` job, one per table; a
// watch job lists the location through the connector's declared watch
// (`ConnectorCapabilities.Watchable`, Phase 1095) and calls `IDataIngestor.RunIngestion` for each object it has not
// ingested. Either way the run is recorded, `IngestionRunCompleted` is
// written under the principal's scope, and every downstream trigger sees a
// scoped commit.
//
// **Refused at startup, never at the first tick.** Every declaration is
// checked before anything is written — its id, its principal (declared,
// resolved, to a persistent scope), its connector, its cadence and tables,
// and for a watch, that the location reads. Any refusal fails the host's
// start, naming every failing source and what it lacks, and seeds nothing.
//
// **A plan, then one effectful step (Phase 1095).** `observe` reads what
// the world answers (resolutions, location reads, the ledger, live jobs,
// current configs); `plan` turns the declaration and that observation into
// a `DeliveryPlan` value with no effect, refusing before anything exists;
// `execute` performs the plan. `seed` is the three in order, so a refusal
// the plan can see — a kept job no scheduler can re-scope included — never
// leaves another source half-seeded.
//
// **Idempotent.** A source's config is keyed on its id at its principal's
// scope, and each of its jobs on (source, table or watch, cadence), so a
// re-deploy updates what it seeded. A ledger records where sources were
// seeded, so a source removed from the composition — or whose principal now
// resolves elsewhere — has its jobs cancelled and its config deleted there.
// A watched object is recorded once ingested, by its identity (blob name,
// last-modified instant, size), so it is ingested once however often the
// location is polled.

/// Why declared data sources refused startup (Phase 1015). Each case names
/// the source (or principal) and what it lacks.
type DeclaredDeliveryRefusal =
    /// The source id is empty or holds a character a storage path cannot.
    | InvalidSourceId of sourceId: DataSourceId * reason: string
    /// Two declarations share a source id.
    | DuplicateSourceId of sourceId: DataSourceId
    /// Two principals share a name.
    | DuplicatePrincipal of principal: string
    /// The delivery names a principal the deployment did not declare.
    | UndeclaredPrincipal of sourceId: DataSourceId * principal: string
    /// No composed `IDataSource` answers the source's `Kind`.
    | UnknownConnector of sourceId: DataSourceId * kind: string
    /// A cadence (the pull's, or the watch poll) is not a 5-field cron.
    | InvalidCadence of sourceId: DataSourceId * cadence: string * reason: string
    /// A scheduled pull declares no table to pull.
    | NoTablesToPull of sourceId: DataSourceId
    /// A watched source's connector does not declare a watch
    /// (`ConnectorCapabilities.Watchable`).
    | NotWatchable of sourceId: DataSourceId * kind: string
    /// A watched source's config sets the container or prefix itself; a
    /// watch reads its principal's scope container at its declared location.
    | LocationInConfig of sourceId: DataSourceId * key: string
    /// The watched location is not a prefix within the scope container.
    | InvalidLocation of sourceId: DataSourceId * location: string * reason: string
    /// The deployment's scope resolver did not resolve the principal;
    /// `missing` names the grant it lacks.
    | PrincipalUnresolved of sourceId: DataSourceId * principal: string * missing: string
    /// The principal resolved to a scope whose data does not persist, which
    /// a delivery that runs after a restart cannot commit to.
    | PrincipalScopeNotPersistent of sourceId: DataSourceId * principal: string * scopeId: string
    /// The watched location could not be read.
    | LocationUnreadable of sourceId: DataSourceId * location: string * reason: string
    /// The deployment composes no `DeclaredPrincipalResolver`.
    | NoPrincipalResolver
    /// A delivery was declared, but the deployment composes no part it
    /// needs (`missing`: data ingestion, or the job scheduler).
    | DeliveryUnavailable of missing: string
    /// The ledger could not be read or written.
    | LedgerUnavailable of reason: string
    /// Writing the config, scheduling a job, or retiring an old source
    /// failed.
    | SeedFailed of sourceId: DataSourceId * reason: string
    /// A live job the source's delivery keeps carries no scope, and the
    /// deployment's scheduler cannot re-issue one (`IJobScopeReissue`)
    /// (Phase 1095: refused by the plan, before anything is written).
    | UnscopedJob of sourceId: DataSourceId * jobKey: string
    /// Reading the live jobs or the seeded configs failed, before anything
    /// was written (Phase 1095).
    | ObservationFailed of reason: string

/// Rendering for `DeclaredDeliveryRefusal`.
[<RequireQualifiedAccess>]
module DeclaredDeliveryRefusal =

    /// A sentence naming the source and what it lacks.
    let describe (refusal: DeclaredDeliveryRefusal) : string =
        match refusal with
        | InvalidSourceId(sourceId, reason) -> $"declared data source '{sourceId}': {reason}"
        | DuplicateSourceId sourceId -> $"declared data source '{sourceId}' is declared more than once"
        | DuplicatePrincipal principal -> $"data-delivery principal '{principal}' is declared more than once"
        | UndeclaredPrincipal(sourceId, principal) ->
            $"declared data source '{sourceId}' is delivered as principal '{principal}', which the deployment does not declare"
        | UnknownConnector(sourceId, kind) ->
            $"declared data source '{sourceId}': no composed connector answers Kind '{kind}'"
        | InvalidCadence(sourceId, cadence, reason) ->
            $"declared data source '{sourceId}': cadence '{cadence}' is not a 5-field cron: {reason}"
        | NoTablesToPull sourceId ->
            $"declared data source '{sourceId}' is a scheduled pull with no tables — declare the tables it pulls in its config's Tables"
        | NotWatchable(sourceId, kind) ->
            $"declared data source '{sourceId}' is watched, but its connector '{kind}' is not a file connector a location can be watched through"
        | LocationInConfig(sourceId, key) ->
            $"declared data source '{sourceId}' is watched, and its config sets '{key}' itself — a watch reads its principal's scope container at its declared location"
        | InvalidLocation(sourceId, location, reason) ->
            $"declared data source '{sourceId}': watched location '{location}' {reason}"
        | PrincipalUnresolved(sourceId, principal, missing) ->
            $"declared data source '{sourceId}': principal '{principal}' does not resolve — it lacks {missing}"
        | PrincipalScopeNotPersistent(sourceId, principal, scopeId) ->
            $"declared data source '{sourceId}': principal '{principal}' resolves to scope '{scopeId}', whose data does not persist — it lacks a persistent scope"
        | LocationUnreadable(sourceId, location, reason) ->
            $"declared data source '{sourceId}': watched location '{location}' cannot be read: {reason}"
        | NoPrincipalResolver ->
            "data-source deliveries were declared, but this deployment composes no DeclaredPrincipalResolver — compose the server through the platform, whose scope-resolution registration provides it"
        | DeliveryUnavailable missing ->
            $"data-source deliveries were declared, but this deployment composes no {missing}"
        | LedgerUnavailable reason -> $"the declared data-source ledger is unavailable: {reason}"
        | SeedFailed(sourceId, reason) -> $"declared data source '{sourceId}' could not be seeded: {reason}"
        | UnscopedJob(sourceId, jobKey) ->
            $"declared data source '{sourceId}': its {jobKey} job carries no scope and this deployment's scheduler cannot re-issue one (IJobScopeReissue)"
        | ObservationFailed reason -> $"the declared data sources' live jobs and configs could not be read: {reason}"

/// The host's start was refused; carries every refusal at once (Phase 1015).
exception DeclaredDataSourcesRefused of refusals: DeclaredDeliveryRefusal list with
    override this.Message =
        let lines =
            this.refusals
            |> List.map (DeclaredDeliveryRefusal.describe >> sprintf "  - %s")
            |> String.concat "\n"

        $"ToolUp.DataSources: declared data sources refused startup:\n{lines}"

/// What seeding and running declared deliveries needs (Phase 1015).
type DeclaredDeliveryDeps = {
    /// The deployment's config store; a declared source is seeded here.
    Configs: IDataSourceConfigStore
    /// The deployment's scheduler, with the platform's ingestion handler
    /// registered (the platform's composition does so when it builds the
    /// ingestor).
    Scheduler: IJobScheduler
    /// The ingestion path every delivery commits through.
    Ingestor: IDataIngestor
    /// The composed connectors.
    Connectors: IDataSource list
    /// Where sources were seeded and which watched objects were ingested.
    Ledger: IDeclaredDeliveryLedger
    /// The 5-field cron a watched location is polled on.
    WatchPoll: string
}

/// What a seeding did (Phase 1015).
type DeclaredSeedReport = {
    /// Sources seeded (created or updated), as (scope id, source id).
    Seeded: (string * DataSourceId) list
    /// Sources retired, as (scope id, source id).
    Retired: (string * DataSourceId) list
}

/// What the declared-delivery startup service takes from the deployment's
/// composition (Phase 1015). `None` (or an empty list) for a part the
/// deployment does not compose.
type DeclaredDeliveryServices = {
    /// The deployment's config store (composed with data ingestion).
    Configs: IDataSourceConfigStore option
    /// The ingestion path (composed with data ingestion).
    Ingestor: IDataIngestor option
    /// The job scheduler.
    Scheduler: IJobScheduler option
    /// The deployment's blob storage, which holds the ledger.
    Storage: BlobStorage.IBlobStorage option
    /// The composed connectors.
    Connectors: IDataSource list
    /// The platform's declared-principal resolver.
    Resolver: DeclaredPrincipalResolver option
    /// Where a watch logs an object refused for its content.
    Logger: ILogger
}

/// What `DeclaredDelivery.plan` reads (Phase 1095): what the deployment
/// composes, and every answer the world gave, gathered by
/// `DeclaredDelivery.observe` before anything is written. Rows are keyed
/// (source id, scope id), the ledger's own order.
type DeliveryObservation = {
    /// The composed connectors' declared capabilities, by `Kind` (the first
    /// connector composed for a `Kind`, as the ingestor resolves it).
    Connectors: Map<string, ConnectorCapabilities>
    /// The 5-field cron a watched location is polled on.
    WatchPoll: string
    /// Whether the deployment composes a `DeclaredPrincipalResolver`.
    ResolverComposed: bool
    /// Each principal a delivery runs as, by name, with its resolution.
    Resolutions: Map<string, Result<ResolvedScope, ScopeResolutionError>>
    /// Each watched source's location read, by source id: `Error` carries
    /// the read failure. A watched source absent here was not read, because
    /// its declaration is refused before its location matters.
    Locations: Map<DataSourceId, Result<unit, string>>
    /// Where sources were seeded, as the ledger last recorded it, or why the
    /// ledger could not be read.
    Ledger: Result<Set<DataSourceId * string>, string>
    /// The live (not cancelled) declared jobs of every row the plan may touch.
    Live: Map<DataSourceId * string, JobDefinition list>
    /// The config each delivered source has at its principal's scope now.
    Configs: Map<DataSourceId * string, DataSourceConfig>
    /// Whether the scheduler re-issues a carried scope (`IJobScopeReissue`).
    Reissues: bool
}

/// A delivered source's config, written at its principal's scope (Phase 1095).
type DeliverySeed = {
    Scope: ResolvedScope
    Config: DataSourceConfig
}

/// A job a delivery schedules under its principal's resolution (Phase 1095).
type DeliveryRegistration = {
    SourceId: DataSourceId
    /// `pull:<table>` or `watch` — the job's `DeclaredDelivery.JobKeyTag`.
    JobKey: string
    Scope: ResolvedScope
    Registration: JobRegistration
}

/// A live job a delivery keeps, whose carried scope is re-issued under its
/// principal's resolution, so a token an earlier key ring no longer redeems
/// runs under the scope it was declared for (Phase 1095).
type DeliveryRestamp = {
    SourceId: DataSourceId
    Scope: ResolvedScope
    JobId: JobId
    /// The job already carries a scope token, so a scheduler that cannot
    /// re-issue one leaves it running under that token.
    CarriesToken: bool
}

/// A live declared job no declaration accounts for any more (Phase 1095).
type DeliveryCancel = {
    SourceId: DataSourceId
    ScopeId: string
    JobId: JobId
}

/// A seeded source no declaration accounts for where it was seeded: its
/// config is deleted there (its jobs are `Cancel` entries) (Phase 1095).
type DeliveryRetire = {
    SourceId: DataSourceId
    ScopeId: string
}

/// A declared data source set's delivery contract, as a value (Phase 1095):
/// everything seeding does, computed by `DeclaredDelivery.plan` with no
/// effect, and performed by `DeclaredDelivery.execute` in field order.
type DeliveryPlan = {
    /// The rows the declaration delivers, as (source id, scope id): every
    /// pull or watch source, at its principal's scope.
    Delivered: Set<DataSourceId * string>
    /// Configs to write — a delivered source whose config differs from what
    /// its scope holds.
    Seed: DeliverySeed list
    /// Jobs to schedule.
    Register: DeliveryRegistration list
    /// Kept jobs whose carried scope is re-issued.
    Restamp: DeliveryRestamp list
    /// Jobs to cancel.
    Cancel: DeliveryCancel list
    /// Seeded configs to delete.
    Retire: DeliveryRetire list
    /// The ledger as the plan found it.
    LedgerBefore: Set<DataSourceId * string>
    /// The ledger once the plan is performed.
    LedgerAfter: Set<DataSourceId * string>
}

/// A declaration that passed every check, ready to plan.
type private CheckedSource = {
    Declared: DeclaredDataSource
    Principal: DeclaredPrincipal
    Scope: ResolvedScope
    /// The config as seeded — for a watch, located in the scope.
    Config: DataSourceConfig
}

/// Declared data-source deliveries (Phase 1015).
[<RequireQualifiedAccess>]
module DeclaredDelivery =

    /// The job handler a watched location's poll runs under.
    [<Literal>]
    let WatchJobHandlerName = "_platform.dataingestion.watch"

    /// The default watch poll: every minute.
    [<Literal>]
    let DefaultWatchPoll = "* * * * *"

    /// The tag a declared delivery's jobs carry, naming their source.
    [<Literal>]
    let SourceTag = "declared-source"

    /// The tag naming which of a source's jobs a job is: `pull:<table>` or
    /// `watch`.
    [<Literal>]
    let JobKeyTag = "declared-job"

    // ── Payloads ─────────────────────────────────────────────────────

    let private watchPayload (sourceId: DataSourceId) : string =
        JsonSerializer.Serialize(dict [ "sourceId", sourceId ])

    let private tryWatchSource (payload: string) : DataSourceId option =
        try
            use document = JsonDocument.Parse payload
            let mutable value = Unchecked.defaultof<JsonElement>

            if document.RootElement.TryGetProperty("sourceId", &value) then
                match value.GetString() with
                | null
                | "" -> None
                | sourceId -> Some sourceId
            else
                None
        with _ ->
            None

    // ── Shape ────────────────────────────────────────────────────────

    let private validSourceId (sourceId: DataSourceId) : Result<unit, string> =
        if String.IsNullOrWhiteSpace sourceId then
            Error "the source id is empty"
        elif
            sourceId
            |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '-' || c = '_')
            |> not
        then
            Error "the source id may hold only letters, digits, '.', '-' and '_'"
        elif sourceId.Contains ".." then
            Error "the source id may not hold '..'"
        else
            Ok()

    /// The prefix a watched location names, normalised as a file
    /// connector normalises its own (`Files.normalisePrefix`).
    let private watchedPrefix (location: string) : Result<string, string> =
        let prefix = Files.normalisePrefix location

        if prefix.Split('/') |> Array.exists (fun segment -> segment = "..") then
            Error "may not climb out of the scope container ('..')"
        else
            Ok prefix

    let private connectorOf (deps: DeclaredDeliveryDeps) (kind: string) =
        deps.Connectors |> List.tryFind (fun c -> c.Kind = kind)

    /// The composed connectors' declared capabilities, by `Kind` — the first
    /// connector for a `Kind`, as `connectorOf` resolves it.
    let private capabilitiesOf (connectors: IDataSource list) : Map<string, ConnectorCapabilities> =
        connectors
        |> List.distinctBy _.Kind
        |> List.map (fun c -> c.Kind, c.Capabilities)
        |> Map.ofList

    /// Whether the scheduler re-issues a carried scope. A scheduler's
    /// abilities are not a connector's: this reads `IJobScopeReissue` the
    /// one way the scheduler contract offers it, as `JobScopeReissue.reissue`
    /// does when it is called.
    let private reissues (scheduler: IJobScheduler) : bool =
        match box scheduler with
        | :? IJobScopeReissue -> true
        | _ -> false

    let private cadenceRefusal (sourceId: DataSourceId) (cadence: string) =
        match CronExpression.tryParse cadence with
        | Ok _ -> None
        | Error reason -> Some(InvalidCadence(sourceId, cadence, reason))

    /// The refusals a declaration set earns before anything is resolved.
    let private shapeRefusals
        (connectors: Map<string, ConnectorCapabilities>)
        (watchPoll: string)
        (declared: DeclaredDataSources)
        =
        let duplicates (keys: string list) =
            keys |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

        let principals = declared.Principals |> List.map _.Name |> Set.ofList

        [
            for d in declared.Sources do
                let sourceId = d.Source.Id

                match validSourceId sourceId with
                | Ok() -> ()
                | Error reason -> InvalidSourceId(sourceId, reason)

                match DataSourceDelivery.principal d.Delivery with
                | Some principal when not (principals.Contains principal) -> UndeclaredPrincipal(sourceId, principal)
                | _ -> ()

                match d.Delivery with
                | DataSourceDelivery.Upload -> ()
                | DataSourceDelivery.ScheduledPull(cadence, _) ->
                    if not (connectors.ContainsKey d.Source.Kind) then
                        UnknownConnector(sourceId, d.Source.Kind)

                    yield! cadenceRefusal sourceId cadence |> Option.toList

                    match d.Source.Tables with
                    | Some tables when
                        not (List.isEmpty tables)
                        && tables |> List.forall (String.IsNullOrWhiteSpace >> not)
                        ->
                        ()
                    | _ -> NoTablesToPull sourceId
                | DataSourceDelivery.Watched(location, _) ->
                    match connectors.TryFind d.Source.Kind with
                    | None -> UnknownConnector(sourceId, d.Source.Kind)
                    | Some capabilities when ConnectorCapabilities.declares ConnectorCapability.Watchable capabilities ->
                        ()
                    | Some _ -> NotWatchable(sourceId, d.Source.Kind)

                    for key in [ "container"; "prefix" ] do
                        if d.Source.ConnectionScope.ContainsKey key then
                            LocationInConfig(sourceId, key)

                    match watchedPrefix location with
                    | Ok _ -> ()
                    | Error reason -> InvalidLocation(sourceId, location, reason)

                    yield! cadenceRefusal sourceId watchPoll |> Option.toList
            yield!
                duplicates (declared.Sources |> List.map _.Source.Id)
                |> List.map DuplicateSourceId
            yield!
                duplicates (declared.Principals |> List.map _.Name)
                |> List.map DuplicatePrincipal
        ]

    // ── Check ────────────────────────────────────────────────────────

    /// The config a source is seeded with under `storage`: a watch's
    /// container is the principal's scope container and its prefix the
    /// declared location, so it reads only what lands in that scope.
    let private seededConfig (d: DeclaredDataSource) (storage: StorageScope) : DataSourceConfig =
        match d.Delivery with
        | DataSourceDelivery.Watched(location, _) ->
            let prefix = watchedPrefix location |> Result.defaultValue ""

            {
                d.Source with
                    ConnectionScope =
                        d.Source.ConnectionScope
                        |> Map.add "container" storage.Container
                        |> Map.add "prefix" prefix
            }
        | DataSourceDelivery.Upload
        | DataSourceDelivery.ScheduledPull _ -> d.Source

    /// Check one pull or watch declaration under its principal's resolution.
    let private checkOne
        (observed: DeliveryObservation)
        (principal: DeclaredPrincipal)
        (d: DeclaredDataSource)
        : Result<CheckedSource, DeclaredDeliveryRefusal> =
        let sourceId = d.Source.Id

        match observed.Resolutions.TryFind principal.Name with
        | None -> Error(PrincipalUnresolved(sourceId, principal.Name, "a resolution (none was observed)"))
        | Some(Error e) -> Error(PrincipalUnresolved(sourceId, principal.Name, DeclaredPrincipal.missingGrant e))
        | Some(Ok scope) ->
            match scope.Storage with
            | Some storage when storage.Persist ->
                let accepted =
                    Ok {
                        Declared = d
                        Principal = principal
                        Scope = scope
                        Config = seededConfig d storage
                    }

                match d.Delivery with
                | DataSourceDelivery.Watched(location, _) ->
                    match observed.Locations.TryFind sourceId with
                    | Some(Ok()) -> accepted
                    | Some(Error reason) -> Error(LocationUnreadable(sourceId, location, reason))
                    | None -> Error(LocationUnreadable(sourceId, location, "the location was not read"))
                | DataSourceDelivery.Upload
                | DataSourceDelivery.ScheduledPull _ -> accepted
            | _ -> Error(PrincipalScopeNotPersistent(sourceId, principal.Name, scope.ScopeId))

    let private oks (results: Result<'a, 'e> seq) : 'a list =
        results
        |> Seq.choose (function
            | Ok value -> Some value
            | Error _ -> None)
        |> List.ofSeq

    let private errors (results: Result<'a, 'e> seq) : 'e list =
        results
        |> Seq.choose (function
            | Error e -> Some e
            | Ok _ -> None)
        |> List.ofSeq

    /// The pull and watch declarations, each with the principal it runs as.
    /// `Upload` declarations deliver nothing: an uploaded source is created
    /// by the request that uploads to it, under that request's scope.
    let private delivered (declared: DeclaredDataSources) : (DeclaredDataSource * string) list =
        declared.Sources
        |> List.choose (fun d -> DataSourceDelivery.principal d.Delivery |> Option.map (fun name -> d, name))

    // ── Jobs ─────────────────────────────────────────────────────────

    /// The idempotency key of a declared delivery's job:
    /// `declared-data-delivery-{sourceId}-{jobKey}@{cadence}`. The cadence is
    /// part of the key because a scheduler keeps a job's trigger: a new
    /// cadence is a new job.
    let idempotencyKey (sourceId: DataSourceId) (jobKey: string) (cadence: string) : string =
        $"declared-data-delivery-{sourceId}-{jobKey}@{cadence}"

    /// Inverse of `idempotencyKey`, for reading a scheduler's store: the
    /// source id, the job key and the cadence. Unambiguous because a source
    /// id holds no ':' (so the first `-pull:` ends it) and a cron holds no
    /// '@' (so the last '@' starts it).
    let tryParseIdempotencyKey (key: string) : (DataSourceId * string * string) option =
        let prefix = "declared-data-delivery-"
        let at = key.LastIndexOf '@'

        if not (key.StartsWith(prefix, StringComparison.Ordinal)) || at < prefix.Length then
            None
        else
            let head = key.Substring(prefix.Length, at - prefix.Length)
            let cadence = key.Substring(at + 1)
            let pull = head.IndexOf("-pull:", StringComparison.Ordinal)

            if pull > 0 then
                Some(head.Substring(0, pull), head.Substring(pull + 1), cadence)
            elif
                head.EndsWith("-watch", StringComparison.Ordinal)
                && head.Length > "-watch".Length
            then
                Some(head.Substring(0, head.Length - "-watch".Length), "watch", cadence)
            else
                None

    /// The registrations a checked source's delivery wants, keyed by job key.
    let private registrationsFor (watchPoll: string) (c: CheckedSource) : (string * JobRegistration) list =
        let sourceId = c.Config.Id

        let registration (jobKey: string) (handler: string) (payload: string) (cadence: string) : JobRegistration = {
            ScopeId = c.Scope.ScopeId
            Handler = handler
            Payload = payload
            Trigger = CronTrigger cadence
            Idempotency =
                Some {
                    Key = idempotencyKey sourceId jobKey cadence
                    TtlSeconds = 60 * 60 * 24 * 365
                }
            RetryPolicy = JobRetryPolicy.defaults
            ShardKey = Some sourceId
            Precision = JobPrecision.Minute
            CreatedBy = c.Principal.UserId
            Tags =
                Map [
                    SourceTag, sourceId
                    JobKeyTag, jobKey
                    "delivery", DataSourceDelivery.token c.Declared.Delivery
                    "runAs", c.Principal.Name
                ]
        }

        match c.Declared.Delivery with
        | DataSourceDelivery.Upload -> []
        | DataSourceDelivery.ScheduledPull(cadence, _) ->
            c.Config.Tables
            |> Option.defaultValue []
            |> List.distinct
            |> List.map (fun table ->
                let jobKey = $"pull:{table}"

                jobKey,
                registration
                    jobKey
                    DataIngestionJobHandler.JobHandlerName
                    (DataIngestionJobHandler.payloadFor sourceId table)
                    cadence)
        | DataSourceDelivery.Watched _ -> [
            "watch", registration "watch" WatchJobHandlerName (watchPayload sourceId) watchPoll
          ]

    /// What one checked source's jobs need: a live job on the wanted key,
    /// handler and trigger is kept (re-issuing its scope where the scheduler
    /// can), each missing one is registered, every other live one cancelled.
    /// A kept job carrying no scope token on a scheduler that cannot re-issue
    /// one is refused here, before anything is written.
    let private jobsFor
        (observed: DeliveryObservation)
        (c: CheckedSource)
        : Result<DeliveryRegistration list * DeliveryRestamp list * DeliveryCancel list, DeclaredDeliveryRefusal list> =
        let sourceId = c.Config.Id
        let scopeId = c.Scope.ScopeId

        let live = observed.Live.TryFind(sourceId, scopeId) |> Option.defaultValue []

        let decided =
            registrationsFor observed.WatchPoll c
            |> List.map (fun (jobKey, registration) ->
                let current =
                    live
                    |> List.tryFind (fun job ->
                        job.Tags.TryFind JobKeyTag = Some jobKey
                        && job.Handler = registration.Handler
                        && job.Trigger = registration.Trigger)

                match current with
                | None ->
                    Ok(
                        Choice1Of2 {
                            SourceId = sourceId
                            JobKey = jobKey
                            Scope = c.Scope
                            Registration = registration
                        }
                    )
                | Some job ->
                    let carriesToken = job.Tags.ContainsKey CarriedJobScope.TokenTag

                    if observed.Reissues || carriesToken then
                        Ok(
                            Choice2Of2(
                                job.JobId,
                                {
                                    SourceId = sourceId
                                    Scope = c.Scope
                                    JobId = job.JobId
                                    CarriesToken = carriesToken
                                }
                            )
                        )
                    else
                        Error(UnscopedJob(sourceId, jobKey)))

        match errors decided with
        | _ :: _ as refusals -> Error refusals
        | [] ->
            let decided = oks decided

            let register =
                decided
                |> List.choose (function
                    | Choice1Of2 r -> Some r
                    | Choice2Of2 _ -> None)

            let kept =
                decided
                |> List.choose (function
                    | Choice2Of2(jobId, restamp) -> Some(jobId, restamp)
                    | Choice1Of2 _ -> None)

            let keptIds = kept |> List.map fst |> Set.ofList

            let cancel =
                live
                |> List.filter (fun job -> not (keptIds.Contains job.JobId))
                |> List.map (fun job -> {
                    SourceId = sourceId
                    ScopeId = scopeId
                    JobId = job.JobId
                })

            // A scheduler that cannot re-issue leaves a tokened job on the
            // token it has: there is nothing to perform.
            let restamp = if observed.Reissues then kept |> List.map snd else []

            Ok(register, restamp, cancel)

    /// Every live job of `row`, cancelled.
    let private cancelAll (observed: DeliveryObservation) ((sourceId, scopeId) as row) : DeliveryCancel list =
        observed.Live.TryFind row
        |> Option.defaultValue []
        |> List.map (fun job -> {
            SourceId = sourceId
            ScopeId = scopeId
            JobId = job.JobId
        })

    // ── Plan ─────────────────────────────────────────────────────────

    /// A declared data source set's delivery contract as a value (Phase
    /// 1095): every check, then what seeding writes, from the declaration and
    /// what `observe` read. Pure and total: a refused declaration plans
    /// nothing, and the refusal names every failing source and what it
    /// lacks. A source previously seeded with a delivery and now declared
    /// `Upload` keeps its config and ledger row and has its jobs cancelled;
    /// every other row no declaration accounts for is retired.
    let plan
        (declared: DeclaredDataSources)
        (observed: DeliveryObservation)
        : Result<DeliveryPlan, DeclaredDeliveryRefusal list> =
        let principals = declared.Principals |> List.map (fun p -> p.Name, p) |> Map.ofList

        let delivered = delivered declared

        let checkedSources () =
            delivered
            |> List.map (fun (d, name) ->
                match principals.TryFind name with
                | Some principal -> checkOne observed principal d
                | None -> Error(UndeclaredPrincipal(d.Source.Id, name)))

        match shapeRefusals observed.Connectors observed.WatchPoll declared with
        | _ :: _ as refusals -> Error refusals
        | [] when not delivered.IsEmpty && not observed.ResolverComposed -> Error [ NoPrincipalResolver ]
        | [] ->
            let checkedSources = checkedSources ()

            match errors checkedSources, observed.Ledger with
            | _ :: _ as refusals, _ -> Error refusals
            | [], Error reason -> Error [ LedgerUnavailable reason ]
            | [], Ok previous ->
                let sources = oks checkedSources
                let jobs = sources |> List.map (jobsFor observed)

                match errors jobs |> List.concat with
                | _ :: _ as refusals -> Error refusals
                | [] ->
                    let jobs = oks jobs

                    let deliveredRows =
                        sources |> List.map (fun c -> c.Config.Id, c.Scope.ScopeId) |> Set.ofList

                    let uploadIds =
                        declared.Sources
                        |> List.filter (fun d -> d.Delivery = DataSourceDelivery.Upload)
                        |> List.map _.Source.Id
                        |> Set.ofList

                    let uploadRows =
                        previous |> Set.filter (fun (sourceId, _) -> uploadIds.Contains sourceId)

                    let retired =
                        previous
                        |> Set.filter (fun ((sourceId, _) as row) ->
                            not (deliveredRows.Contains row || uploadIds.Contains sourceId))

                    Ok {
                        Delivered = deliveredRows
                        Seed =
                            sources
                            |> List.filter (fun c ->
                                observed.Configs.TryFind(c.Config.Id, c.Scope.ScopeId) <> Some c.Config)
                            |> List.map (fun c -> { Scope = c.Scope; Config = c.Config })
                        Register = jobs |> List.collect (fun (register, _, _) -> register)
                        Restamp = jobs |> List.collect (fun (_, restamp, _) -> restamp)
                        Cancel = [
                            yield! jobs |> List.collect (fun (_, _, cancel) -> cancel)
                            for row in Set.union uploadRows retired do
                                yield! cancelAll observed row
                        ]
                        Retire =
                            retired
                            |> Set.toList
                            |> List.map (fun (sourceId, scopeId) -> {
                                SourceId = sourceId
                                ScopeId = scopeId
                            })
                        LedgerBefore = previous
                        LedgerAfter = Set.union deliveredRows (Set.difference previous retired)
                    }

    // ── Observe ──────────────────────────────────────────────────────

    /// Everything `plan` reads, read before anything is written (Phase 1095):
    /// each delivery's principal resolved once, each watched location read
    /// through its connector's declared watch, the ledger, the live declared
    /// jobs and the current configs of every row the plan may touch. Reads
    /// only. A shape the declaration already fails reads nothing — its
    /// refusal is named before anything resolves.
    let observe
        (resolver: DeclaredPrincipalResolver option)
        (deps: DeclaredDeliveryDeps)
        (declared: DeclaredDataSources)
        : Async<Result<DeliveryObservation, DeclaredDeliveryRefusal list>> =
        async {
            let connectors = capabilitiesOf deps.Connectors
            let delivered = delivered declared

            let nothingRead = {
                Connectors = connectors
                WatchPoll = deps.WatchPoll
                ResolverComposed = resolver.IsSome
                Resolutions = Map.empty
                Locations = Map.empty
                Ledger = Ok Set.empty
                Live = Map.empty
                Configs = Map.empty
                Reissues = false
            }

            match shapeRefusals connectors deps.WatchPoll declared, resolver with
            | _ :: _, _ -> return Ok nothingRead
            | [], None when not delivered.IsEmpty -> return Ok nothingRead
            | [], resolver ->
                let used = delivered |> List.map snd |> Set.ofList

                let! resolutions =
                    match resolver with
                    | None -> async { return [||] }
                    | Some resolver ->
                        declared.Principals
                        |> List.filter (fun p -> used.Contains p.Name)
                        |> List.map (fun p -> async {
                            let! resolved = resolver.Resolve(DeclaredPrincipal.user p)
                            return p.Name, resolved
                        })
                        |> Async.Sequential

                let resolutions = Map.ofArray resolutions

                let persistentScopeOf name =
                    match resolutions.TryFind name with
                    | Some(Ok scope) ->
                        match scope.Storage with
                        | Some storage when storage.Persist -> Some(scope, storage)
                        | _ -> None
                    | _ -> None

                let! locations =
                    delivered
                    |> List.choose (fun (d, name) ->
                        match d.Delivery, persistentScopeOf name, connectorOf deps d.Source.Kind with
                        | DataSourceDelivery.Watched _, Some(scope, storage), Some connector ->
                            match ConnectorCapabilities.watchable connector with
                            | Ok watchable ->
                                Some(
                                    async {
                                        let ctx: DataSourceCallContext = {
                                            ScopeId = scope.ScopeId
                                            Config = seededConfig d storage
                                            Credential = None
                                        }

                                        let! listed = watchable.ListObjects ctx

                                        return
                                            d.Source.Id, listed |> Result.map ignore |> Result.mapError (sprintf "%A")
                                    }
                                )
                            | Error _ -> None
                        | _ -> None)
                    |> Async.Sequential

                let deliveredRows =
                    delivered
                    |> List.choose (fun (d, name) ->
                        persistentScopeOf name
                        |> Option.map (fun (scope, _) -> d.Source.Id, scope.ScopeId))

                let! ledger = deps.Ledger.ReadSeeded()

                let rows =
                    Set.union (Set.ofList deliveredRows) (ledger |> Result.defaultValue Set.empty)

                try
                    let! listed =
                        rows
                        |> Set.map snd
                        |> Set.toList
                        |> List.map (fun scopeId -> async {
                            let! jobs = deps.Scheduler.ListJobs scopeId
                            return scopeId, jobs
                        })
                        |> Async.Sequential

                    let listed = Map.ofArray listed

                    let live =
                        rows
                        |> Set.toList
                        |> List.map (fun ((sourceId, scopeId) as row) ->
                            row,
                            listed[scopeId]
                            |> List.filter (fun job ->
                                job.Status <> JobStatus.Cancelled
                                && not (isNull (box job.Tags))
                                && job.Tags.TryFind SourceTag = Some sourceId))
                        |> Map.ofList

                    let! configs =
                        deliveredRows
                        |> List.map (fun ((sourceId, scopeId) as row) -> async {
                            let! config = deps.Configs.Get(scopeId, sourceId)
                            return config |> Option.map (fun config -> row, config)
                        })
                        |> Async.Sequential

                    return
                        Ok {
                            nothingRead with
                                Resolutions = resolutions
                                Locations = Map.ofArray locations
                                Ledger = ledger
                                Live = live
                                Configs = configs |> Array.choose id |> Map.ofArray
                                Reissues = reissues deps.Scheduler
                        }
                with ex ->
                    return Error [ ObservationFailed ex.Message ]
        }

    // ── Execute ──────────────────────────────────────────────────────

    /// Re-issue a kept job's carried scope. A scheduler that cannot re-issue
    /// is a refusal only when the job carries no token.
    let private restamp (deps: DeclaredDeliveryDeps) (r: DeliveryRestamp) = async {
        match! JobScopeReissue.reissue deps.Scheduler r.Scope r.Scope.ScopeId r.JobId with
        | Ok() -> return Ok()
        | Error ScopeReissueError.Unsupported when r.CarriesToken -> return Ok()
        | Error ScopeReissueError.Unsupported ->
            return
                Error "its job carries no scope and this deployment's scheduler cannot re-issue one (IJobScopeReissue)"
        | Error refusal -> return Error(sprintf "%A" refusal)
    }

    /// Perform `plan` — the one effectful step of a declared delivery (Phase
    /// 1095). In order: write `Seed`, schedule `Register`, re-issue `Restamp`,
    /// cancel `Cancel`, delete `Retire`, and write the ledger. A step that
    /// fails is a `SeedFailed` naming its source, and its row's later steps
    /// are skipped. A delivered source that failed keeps the ledger rows it
    /// had and retires none of them; a retirement that did not happen keeps
    /// its row, so the next start retires it. The ledger is written only when
    /// it changes.
    let execute
        (deps: DeclaredDeliveryDeps)
        (plan: DeliveryPlan)
        : Async<Result<DeclaredSeedReport, DeclaredDeliveryRefusal list>> =
        async {
            let failed = Collections.Generic.Dictionary<DataSourceId * string, string>()

            let perform (row: DataSourceId * string) (work: Async<Result<unit, string>>) = async {
                if not (failed.ContainsKey row) then
                    try
                        match! work with
                        | Ok() -> ()
                        | Error reason -> failed[row] <- reason
                    with ex ->
                        failed[row] <- ex.Message
            }

            let step (work: unit -> Async<unit>) = async {
                do! work ()
                return Ok()
            }

            let schedule (r: DeliveryRegistration) = async {
                match! deps.Scheduler.Schedule(r.Scope, r.Registration) with
                | Ok _ -> return Ok()
                | Error e -> return Error(sprintf "scheduling its %s job failed: %A" r.JobKey e)
            }

            let steps = [
                for s in plan.Seed ->
                    (s.Config.Id, s.Scope.ScopeId), step (fun () -> deps.Configs.Save(s.Scope.ScopeId, s.Config))
                for r in plan.Register -> (r.SourceId, r.Scope.ScopeId), schedule r
                for r in plan.Restamp -> (r.SourceId, r.Scope.ScopeId), restamp deps r
                for c in plan.Cancel ->
                    (c.SourceId, c.ScopeId), step (fun () -> deps.Scheduler.Cancel(c.ScopeId, c.JobId))
            ]

            for row, work in steps do
                do! perform row work

            // A delivered source that failed retires none of its old rows.
            let failedDelivered =
                failed.Keys |> Seq.filter plan.Delivered.Contains |> Seq.map fst |> Set.ofSeq

            let retiring =
                plan.Retire |> List.filter (fun r -> not (failedDelivered.Contains r.SourceId))

            for r in retiring do
                do! perform (r.SourceId, r.ScopeId) (step (fun () -> deps.Configs.Delete(r.ScopeId, r.SourceId)))

            let failedRows = failed.Keys |> Set.ofSeq

            let retired =
                retiring
                |> List.map (fun r -> r.SourceId, r.ScopeId)
                |> Set.ofList
                |> fun rows -> Set.difference rows failedRows

            let unretired =
                plan.Retire
                |> List.map (fun r -> r.SourceId, r.ScopeId)
                |> Set.ofList
                |> fun rows -> Set.difference rows retired

            let ledger =
                Set.union
                    (plan.LedgerAfter
                     |> Set.filter (fun row -> plan.LedgerBefore.Contains row || not (failedRows.Contains row)))
                    unretired

            let! written =
                if ledger = plan.LedgerBefore then
                    async { return Ok() }
                else
                    deps.Ledger.WriteSeeded ledger

            let refusals = [
                for KeyValue((sourceId, _), reason) in failed do
                    SeedFailed(sourceId, reason)
                match written with
                | Ok() -> ()
                | Error reason -> LedgerUnavailable reason
            ]

            let rowId (sourceId, scopeId) = scopeId, sourceId

            return
                match refusals with
                | [] ->
                    Ok {
                        Seeded = Set.difference plan.Delivered failedRows |> Set.toList |> List.map rowId
                        Retired = retired |> Set.toList |> List.map rowId
                    }
                | refusals -> Error refusals
        }

    // ── Seed ─────────────────────────────────────────────────────────

    /// Seed a deployment's declared data sources (Phase 1015): `observe`,
    /// then `plan`, then `execute` — so every refusal the plan can see is
    /// named before anything is written, and seeding writes nothing then.
    /// `resolver` is the platform's `DeclaredPrincipalResolver`; `None`
    /// refuses any declared delivery.
    ///
    /// An `Upload` declaration seeds nothing (an uploaded source is created
    /// by the request that uploads to it, under that request's scope); a
    /// source previously seeded with a delivery and now declared `Upload`
    /// keeps its config where it was and has its delivery's jobs cancelled.
    let seed
        (resolver: DeclaredPrincipalResolver option)
        (deps: DeclaredDeliveryDeps)
        (declared: DeclaredDataSources)
        : Async<Result<DeclaredSeedReport, DeclaredDeliveryRefusal list>> =
        async {
            match! observe resolver deps declared with
            | Error refusals -> return Error refusals
            | Ok observed ->
                match plan declared observed with
                | Error refusals -> return Error refusals
                | Ok planned -> return! execute deps planned
        }

    // ── Watch ────────────────────────────────────────────────────────

    /// Whether a run means the object is done with: ingested, or refused
    /// for its own content — a rewritten object has a new identity and is
    /// tried afresh. Any other failure is retried at the next poll.
    let private settles (run: IngestionRun) : bool =
        match run.Status, run.Error with
        | IngestionStatus.Succeeded, _ -> true
        | _, Some(SchemaMismatch _) -> true
        | _ -> false

    /// The watch poll's handler: list the source's location through its
    /// connector and ingest, through `IDataIngestor.RunIngestion`, each
    /// object not yet recorded as ingested. Runs only under the carried
    /// scope its job was scheduled with — a job that re-minted none (its
    /// token unredeemable) ingests nothing until the next start re-issues
    /// it.
    let watchHandler (deps: DeclaredDeliveryDeps) (logger: ILogger) : IJobHandler =
        { new IJobHandler with
            member _.Execute(ctx) = async {
                match tryWatchSource ctx.Payload with
                | None -> return JobResult.PermanentFailure $"declared watch: malformed payload for job {ctx.JobId}"
                | Some sourceId ->
                    match ctx.Scope.Storage with
                    | Some _ when String.Equals(ctx.Scope.ScopeId, ctx.ScopeId, StringComparison.Ordinal) ->
                        match! deps.Configs.Get(ctx.ScopeId, sourceId) with
                        | None ->
                            return
                                JobResult.PermanentFailure
                                    $"declared watch: data source '{sourceId}' is not configured in its scope"
                        | Some config ->
                            match connectorOf deps config.Kind |> Option.map ConnectorCapabilities.watchable with
                            | Some(Ok watchable) ->
                                let call: DataSourceCallContext = {
                                    ScopeId = ctx.ScopeId
                                    Config = config
                                    Credential = None
                                }

                                match! watchable.ListObjects call with
                                | Error e ->
                                    return
                                        JobResult.TransientFailure
                                            $"declared watch '{sourceId}': the location could not be read: %A{e}"
                                | Ok objects ->
                                    let failures = ResizeArray<string>()

                                    for o in objects do
                                        match! deps.Ledger.WasIngested(ctx.ScopeId, sourceId, o.Identity) with
                                        | Error reason -> failures.Add reason
                                        | Ok true -> ()
                                        | Ok false ->
                                            match! deps.Ingestor.RunIngestion(ctx.ScopeId, sourceId, o.Table) with
                                            | Error e -> failures.Add $"'{o.BlobName}': %A{e}"
                                            | Ok run when settles run ->
                                                if run.Status <> IngestionStatus.Succeeded then
                                                    logger.Warn
                                                        $"[DeclaredDelivery] watched object '{o.BlobName}' of '{sourceId}' was refused for its content and will not be retried: %A{run.Error}"

                                                match!
                                                    deps.Ledger.RecordIngested(ctx.ScopeId, sourceId, o.Identity)
                                                with
                                                | Ok() -> ()
                                                | Error reason -> failures.Add reason
                                            | Ok run -> failures.Add $"'{o.BlobName}': %A{run.Error}"

                                    if failures.Count = 0 then
                                        return JobResult.Success
                                    else
                                        return
                                            JobResult.TransientFailure(
                                                $"declared watch '{sourceId}': " + String.concat "; " failures
                                            )
                            | _ ->
                                return
                                    JobResult.PermanentFailure
                                        $"declared watch: data source '{sourceId}' has no watchable connector for Kind '{config.Kind}'"
                    | _ ->
                        return
                            JobResult.PermanentFailure
                                $"declared watch '{sourceId}': the job carries no resolved scope, so nothing is ingested; the next start re-issues it"
            }
        }

    // ── Composition ──────────────────────────────────────────────────

    /// The declared-delivery deps over what the deployment composes, or the
    /// part it does not compose. Registers the watch poll's handler with the
    /// scheduler; the platform's own ingestion handler is registered when
    /// the platform builds the ingestor.
    let depsOf
        (composed: DeclaredDeliveryServices)
        (watchPoll: string)
        : Result<DeclaredDeliveryDeps, DeclaredDeliveryRefusal> =
        match composed.Configs, composed.Ingestor, composed.Scheduler, composed.Storage with
        | None, _, _, _
        | _, None, _, _ ->
            Error(DeliveryUnavailable "data ingestion (ServerConfig.DataIngestion = EnabledDataIngestion)")
        | _, _, None, _ -> Error(DeliveryUnavailable "job scheduler")
        | _, _, _, None -> Error(DeliveryUnavailable "blob storage")
        | Some configs, Some ingestor, Some scheduler, Some storage ->
            let deps = {
                Configs = configs
                Scheduler = scheduler
                Ingestor = ingestor
                Connectors = composed.Connectors
                Ledger = DataSourceConfigStore.declaredDeliveryLedger storage
                WatchPoll = watchPoll
            }

            // Each poll is one delivery (Phase 1042): the objects it ingests
            // reach a delivery-completed hook together, under the job's scope.
            scheduler.RegisterHandler(
                WatchJobHandlerName,
                watchHandler deps composed.Logger
                |> DeliveryCompleted.aroundJob WatchJobHandlerName
            )

            Ok deps

    /// The startup service over what the deployment composes: at start it
    /// seeds `declared` through the platform's `DeclaredPrincipalResolver`,
    /// and a refusal FAILS the start (`DeclaredDataSourcesRefused`) naming
    /// every source and what it lacks. A deployment declaring nothing starts
    /// whatever it composes.
    let startupService
        (declared: DeclaredDataSources)
        (watchPoll: string)
        (composed: DeclaredDeliveryServices)
        : Microsoft.Extensions.Hosting.IHostedService =
        let deps = depsOf composed watchPoll

        { new Microsoft.Extensions.Hosting.IHostedService with
            member _.StartAsync _ =
                async {
                    match deps with
                    | Error _ when List.isEmpty declared.Sources -> ()
                    | Error refusal -> return raise (DeclaredDataSourcesRefused [ refusal ])
                    | Ok deps ->
                        match! seed composed.Resolver deps declared with
                        | Ok _ -> ()
                        | Error refusals -> return raise (DeclaredDataSourcesRefused refusals)
                }
                |> Async.StartAsTask
                :> System.Threading.Tasks.Task

            member _.StopAsync _ =
                System.Threading.Tasks.Task.CompletedTask
        }

    /// Compose a deployment's declared data sources: registers the startup
    /// service, resolving what it needs from the deployment's container in
    /// its registration factory.
    ///
    /// ```fsharp skip=fragment
    /// services
    /// |> DeclaredDelivery.withDeclaredDataSources declared DeclaredDelivery.DefaultWatchPoll
    /// |> ignore
    /// ```
    ///
    /// Removing a declared source is declaring the set without it: the
    /// next start retires it. Stop registering the service only once a
    /// start with an empty `Sources` has retired everything.
    let withDeclaredDataSources
        (declared: DeclaredDataSources)
        (watchPoll: string)
        (services: IServiceCollection)
        : IServiceCollection =
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun (sp: IServiceProvider) ->
            let composed: DeclaredDeliveryServices = {
                Configs =
                    match sp.GetService(typeof<IDataSourceConfigStore>) with
                    | :? IDataSourceConfigStore as store -> Some store
                    | _ -> None
                Ingestor =
                    match sp.GetService(typeof<IDataIngestor>) with
                    | :? IDataIngestor as ingestor -> Some ingestor
                    | _ -> None
                Scheduler =
                    match sp.GetService(typeof<IJobScheduler>) with
                    | :? IJobScheduler as scheduler -> Some scheduler
                    | _ -> None
                Storage =
                    match sp.GetService(typeof<BlobStorage.IBlobStorage>) with
                    | :? BlobStorage.IBlobStorage as storage -> Some storage
                    | _ -> None
                Connectors = sp.GetServices(typeof<IDataSource>) |> Seq.cast<IDataSource> |> List.ofSeq
                Resolver =
                    match sp.GetService(typeof<DeclaredPrincipalResolver>) with
                    | :? DeclaredPrincipalResolver as resolver -> Some resolver
                    | _ -> None
                Logger =
                    match sp.GetService(typeof<ILogger>) with
                    | :? ILogger as logger -> logger
                    | _ -> ConsoleLogger.ConsoleLogger() :> ILogger
            }

            startupService declared watchPoll composed)