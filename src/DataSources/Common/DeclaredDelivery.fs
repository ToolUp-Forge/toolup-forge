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
// watch job lists the location through the connector's `IWatchableFileSource`
// and calls `IDataIngestor.RunIngestion` for each object it has not
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
    /// A watched source's connector is not a file connector a location can
    /// be watched through (`IWatchableFileSource`).
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

/// A declaration that passed every check, ready to write.
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

    let private cadenceRefusal (sourceId: DataSourceId) (cadence: string) =
        match CronExpression.tryParse cadence with
        | Ok _ -> None
        | Error reason -> Some(InvalidCadence(sourceId, cadence, reason))

    /// The refusals a declaration set earns before anything is resolved.
    let private shapeRefusals (deps: DeclaredDeliveryDeps) (declared: DeclaredDataSources) =
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
                    match connectorOf deps d.Source.Kind with
                    | None -> UnknownConnector(sourceId, d.Source.Kind)
                    | Some _ -> ()

                    yield! cadenceRefusal sourceId cadence |> Option.toList

                    match d.Source.Tables with
                    | Some tables when
                        not (List.isEmpty tables)
                        && tables |> List.forall (String.IsNullOrWhiteSpace >> not)
                        ->
                        ()
                    | _ -> NoTablesToPull sourceId
                | DataSourceDelivery.Watched(location, _) ->
                    match connectorOf deps d.Source.Kind with
                    | None -> UnknownConnector(sourceId, d.Source.Kind)
                    | Some(:? IWatchableFileSource) -> ()
                    | Some _ -> NotWatchable(sourceId, d.Source.Kind)

                    for key in [ "container"; "prefix" ] do
                        if d.Source.ConnectionScope.ContainsKey key then
                            LocationInConfig(sourceId, key)

                    match watchedPrefix location with
                    | Ok _ -> ()
                    | Error reason -> InvalidLocation(sourceId, location, reason)

                    yield! cadenceRefusal sourceId deps.WatchPoll |> Option.toList
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

    /// Check one pull or watch declaration under its principal's
    /// resolution. Writes nothing.
    let private checkOne
        (deps: DeclaredDeliveryDeps)
        (principal: DeclaredPrincipal)
        (resolved: Result<ResolvedScope, ScopeResolutionError>)
        (d: DeclaredDataSource)
        : Async<Result<CheckedSource, DeclaredDeliveryRefusal>> =
        async {
            let sourceId = d.Source.Id

            match resolved with
            | Error e -> return Error(PrincipalUnresolved(sourceId, principal.Name, DeclaredPrincipal.missingGrant e))
            | Ok scope ->
                match scope.Storage with
                | Some storage when storage.Persist ->
                    let config = seededConfig d storage

                    let accepted =
                        Ok {
                            Declared = d
                            Principal = principal
                            Scope = scope
                            Config = config
                        }

                    match d.Delivery, connectorOf deps d.Source.Kind with
                    | DataSourceDelivery.Watched(location, _), Some(:? IWatchableFileSource as watchable) ->
                        let ctx: DataSourceCallContext = {
                            ScopeId = scope.ScopeId
                            Config = config
                            Credential = None
                        }

                        match! watchable.ListObjects ctx with
                        | Ok _ -> return accepted
                        | Error e -> return Error(LocationUnreadable(sourceId, location, sprintf "%A" e))
                    | _ -> return accepted
                | _ -> return Error(PrincipalScopeNotPersistent(sourceId, principal.Name, scope.ScopeId))
        }

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

    /// Check every declaration, resolving each principal once. Writes
    /// nothing. `Upload` declarations pass unchecked: they seed nothing.
    let private checkAll
        (resolver: DeclaredPrincipalResolver option)
        (deps: DeclaredDeliveryDeps)
        (declared: DeclaredDataSources)
        : Async<Result<CheckedSource list, DeclaredDeliveryRefusal list>> =
        async {
            let delivered =
                declared.Sources
                |> List.filter (fun d -> (DataSourceDelivery.principal d.Delivery).IsSome)

            match shapeRefusals deps declared, resolver with
            | (_ :: _ as refusals), _ -> return Error refusals
            | [], _ when List.isEmpty delivered -> return Ok []
            | [], None -> return Error [ NoPrincipalResolver ]
            | [], Some resolver ->
                let used =
                    delivered
                    |> List.choose (_.Delivery >> DataSourceDelivery.principal)
                    |> Set.ofList

                let! resolutions =
                    declared.Principals
                    |> List.filter (fun p -> used.Contains p.Name)
                    |> List.map (fun p -> async {
                        let! resolved = resolver.Resolve(DeclaredPrincipal.user p)
                        return p.Name, (p, resolved)
                    })
                    |> Async.Sequential

                let resolutions = Map.ofArray resolutions

                let! checkedSources =
                    delivered
                    |> List.map (fun d ->
                        let principal, resolved =
                            resolutions[DataSourceDelivery.principal d.Delivery |> Option.get]

                        checkOne deps principal resolved d)
                    |> Async.Sequential

                return
                    match errors checkedSources with
                    | [] -> Ok(oks checkedSources)
                    | refusals -> Error refusals
        }

    // ── Jobs ─────────────────────────────────────────────────────────

    /// The live jobs a declared delivery scheduled for `sourceId` in scope.
    let private declaredJobs (deps: DeclaredDeliveryDeps) (scopeId: string) (sourceId: DataSourceId) = async {
        let! jobs = deps.Scheduler.ListJobs scopeId

        return
            jobs
            |> List.filter (fun job ->
                job.Status <> JobStatus.Cancelled
                && not (isNull (box job.Tags))
                && job.Tags.TryFind SourceTag = Some sourceId)
    }

    /// The registrations a checked source's delivery wants, keyed by job
    /// key. The cadence is part of the idempotency key because a scheduler
    /// keeps a job's trigger: a new cadence is a new job.
    let private registrationsFor (deps: DeclaredDeliveryDeps) (c: CheckedSource) : (string * JobRegistration) list =
        let sourceId = c.Config.Id

        let registration (jobKey: string) (handler: string) (payload: string) (cadence: string) : JobRegistration = {
            ScopeId = c.Scope.ScopeId
            Handler = handler
            Payload = payload
            Trigger = CronTrigger cadence
            Idempotency =
                Some {
                    Key = $"declared-data-delivery-{sourceId}-{jobKey}@{cadence}"
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
            "watch", registration "watch" WatchJobHandlerName (watchPayload sourceId) deps.WatchPoll
          ]

    /// Re-issue a recovered job's carried scope under the principal's
    /// resolution, so a job whose token an earlier key ring no longer
    /// redeems runs under the scope it was declared for. A scheduler that
    /// cannot re-issue is a refusal only when the job carries no token.
    let private restamp (deps: DeclaredDeliveryDeps) (scope: ResolvedScope) (job: JobDefinition) = async {
        match! JobScopeReissue.reissue deps.Scheduler scope job.ScopeId job.JobId with
        | Ok() -> return Ok()
        | Error ScopeReissueError.Unsupported when job.Tags.ContainsKey CarriedJobScope.TokenTag -> return Ok()
        | Error ScopeReissueError.Unsupported ->
            return
                Error "its job carries no scope and this deployment's scheduler cannot re-issue one (IJobScopeReissue)"
        | Error refusal -> return Error(sprintf "%A" refusal)
    }

    /// Align a checked source's jobs with its delivery: keep a live job on
    /// the wanted cadence (re-issuing its scope), schedule each missing one
    /// under the principal's resolution, cancel every other declared job.
    let private syncJobs (deps: DeclaredDeliveryDeps) (c: CheckedSource) : Async<Result<unit, string>> = async {
        let scopeId = c.Scope.ScopeId
        let! live = declaredJobs deps scopeId c.Config.Id
        let wanted = registrationsFor deps c

        let! outcomes =
            wanted
            |> List.map (fun (jobKey, registration) -> async {
                let current =
                    live
                    |> List.tryFind (fun job ->
                        job.Tags.TryFind JobKeyTag = Some jobKey
                        && job.Handler = registration.Handler
                        && job.Trigger = registration.Trigger)

                match current with
                | Some job ->
                    let! restamped = restamp deps c.Scope job
                    return restamped |> Result.map (fun () -> job.JobId)
                | None ->
                    match! deps.Scheduler.Schedule(c.Scope, registration) with
                    | Ok jobId -> return Ok jobId
                    | Error e -> return Error(sprintf "scheduling its %s job failed: %A" jobKey e)
            })
            |> Async.Sequential

        match errors outcomes with
        | _ :: _ as failures -> return Error(String.concat "; " failures)
        | [] ->
            let kept = oks outcomes |> Set.ofList

            for job in live do
                if not (kept.Contains job.JobId) then
                    do! deps.Scheduler.Cancel(scopeId, job.JobId)

            return Ok()
    }

    /// Cancel every declared job for `sourceId` in scope.
    let private cancelJobs (deps: DeclaredDeliveryDeps) (scopeId: string) (sourceId: DataSourceId) = async {
        let! live = declaredJobs deps scopeId sourceId

        for job in live do
            do! deps.Scheduler.Cancel(scopeId, job.JobId)
    }

    let private attempt (sourceId: DataSourceId) (work: Async<Result<unit, string>>) = async {
        try
            match! work with
            | Ok() -> return Ok()
            | Error reason -> return Error(SeedFailed(sourceId, reason))
        with ex ->
            return Error(SeedFailed(sourceId, ex.Message))
    }

    // ── Seed ─────────────────────────────────────────────────────────

    /// Seed a deployment's declared data sources: check every declaration
    /// (writing nothing if any is refused), seed each delivered source's
    /// config at its principal's scope and align its jobs, retire every
    /// previously seeded source no declaration now accounts for, and record
    /// where the sources now stand. `resolver` is the platform's
    /// `DeclaredPrincipalResolver`; `None` refuses any declared delivery.
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
            match! checkAll resolver deps declared with
            | Error refusals -> return Error refusals
            | Ok checkedSources ->
                match! deps.Ledger.ReadSeeded() with
                | Error reason -> return Error [ LedgerUnavailable reason ]
                | Ok previous ->
                    let! seeded =
                        checkedSources
                        |> List.map (fun c -> async {
                            let sourceId = c.Config.Id

                            let! written =
                                attempt
                                    sourceId
                                    (async {
                                        do! deps.Configs.Save(c.Scope.ScopeId, c.Config)
                                        return! syncJobs deps c
                                    })

                            return written |> Result.map (fun () -> sourceId, c.Scope.ScopeId)
                        })
                        |> Async.Sequential

                    let seededRows = Set.ofList (oks seeded)

                    let uploadIds =
                        declared.Sources
                        |> List.filter (fun d -> d.Delivery = DataSourceDelivery.Upload)
                        |> List.map _.Source.Id
                        |> Set.ofList

                    // A delivered source whose seeding failed keeps the rows
                    // it had; an `Upload` source keeps its rows (and config)
                    // with its delivery's jobs cancelled; every other row no
                    // declaration accounts for is retired.
                    let failedIds =
                        checkedSources
                        |> List.map _.Config.Id
                        |> List.filter (fun id -> not (seededRows |> Set.exists (fun (s, _) -> s = id)))
                        |> Set.ofList

                    let uploadRows =
                        previous |> Set.filter (fun (sourceId, _) -> uploadIds.Contains sourceId)

                    let! stopped =
                        uploadRows
                        |> Set.toList
                        |> List.map (fun (sourceId, scopeId) ->
                            attempt
                                sourceId
                                (async {
                                    do! cancelJobs deps scopeId sourceId
                                    return Ok()
                                }))
                        |> Async.Sequential

                    let toRetire =
                        previous
                        |> Set.filter (fun ((sourceId, _) as row) ->
                            not (
                                seededRows.Contains row
                                || failedIds.Contains sourceId
                                || uploadIds.Contains sourceId
                            ))

                    let! retired =
                        toRetire
                        |> Set.toList
                        |> List.map (fun (sourceId, scopeId) -> async {
                            let! result =
                                attempt
                                    sourceId
                                    (async {
                                        do! cancelJobs deps scopeId sourceId
                                        do! deps.Configs.Delete(scopeId, sourceId)
                                        return Ok()
                                    })

                            return result |> Result.map (fun () -> sourceId, scopeId)
                        })
                        |> Async.Sequential

                    let retiredRows = Set.ofList (oks retired)

                    // A row whose retirement failed stays in the ledger, so
                    // the next start retires it.
                    let! written = deps.Ledger.WriteSeeded(Set.union seededRows (Set.difference previous retiredRows))

                    let refusals = [
                        yield! errors seeded
                        yield! errors stopped
                        yield! errors retired
                        match written with
                        | Ok() -> ()
                        | Error reason -> LedgerUnavailable reason
                    ]

                    let rowId (sourceId, scopeId) = scopeId, sourceId

                    return
                        match refusals with
                        | [] ->
                            Ok {
                                Seeded = seededRows |> Set.toList |> List.map rowId
                                Retired = retiredRows |> Set.toList |> List.map rowId
                            }
                        | refusals -> Error refusals
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
                            match connectorOf deps config.Kind with
                            | Some(:? IWatchableFileSource as watchable) ->
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

            scheduler.RegisterHandler(WatchJobHandlerName, watchHandler deps composed.Logger)
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