// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── The fact tier's operator tasks ──────────────────────────────────
//
// Two one-off repairs a deployment runs through `toolup facts migrate`,
// which starts the composition root in its `--operator-task` mode (no
// listener bound) and prints the report the task writes:
//
//   - **facts-attribute-delegate-rows** records, on every stored image of a
//     delegated table, the run that wrote each row. An image stored before
//     rows were attributed was always written whole by the run its head
//     entry names, so the attribution is read from the head, never guessed.
//     Until it runs, a period slice over such an image is refused by name.
//   - **facts-reissue-jobs** re-issues the carried scope of a fact-tier job
//     (a recompute or a coherence check) persisted under a bare shard id,
//     which the runner refuses because nothing re-mints its scope. Which
//     kind of scope a shard id names is the OPERATOR's to say
//     (`--scope <kind>:<id>`), and the scope itself is resolved by the
//     deployment's own resolver for a principal the operator names
//     (`--principal <user-id>`); the resolution must be the key the operator
//     named, or nothing is re-issued. The task mints nothing of its own.
//
// Both take `--dry-run`, which reports and writes nothing.

/// One stored image of a delegated table the attribution examined.
type DelegateImageAttribution = {
    /// The scope the image is stored in.
    Scope: string
    /// The delegated table.
    TableId: string
    /// The image's commit sequence.
    Sequence: int64
    /// The run whose image it is — the writer of every unattributed row.
    RunId: string
    /// Rows the image holds.
    Rows: int
    /// Rows that named no writer before the task ran.
    Unattributed: int
}

/// What the attribution did, or with `DryRun` would do.
type DelegateAttributionReport = {
    /// Nothing was written.
    DryRun: bool
    /// Every image examined, in scope, table and sequence order.
    Images: DelegateImageAttribution list
}

/// The delegate row-attribution migration.
[<RequireQualifiedAccess>]
module DelegateRowAttribution =

    /// Record the writer of every unattributed row of every delegated image in
    /// `scopes`, held in `storage` — the run the table's head names for the
    /// image's sequence. With `dryRun`, report and write nothing. `Error`
    /// names the first image that could not be read or written.
    let attribute
        (storage: IBlobStorage)
        (dryRun: bool)
        (scopes: string list)
        : Async<Result<DelegateAttributionReport, string>> =
        async {
            let tables = DelegateTableStore storage

            let image (scopeId: string) (commit: DelegateTableCommit) = async {
                let run = commit.Run

                match! tables.StoredImage(scopeId, run.TableId, run.Watermark.Sequence) with
                | Error e -> return Error(FactTableWriteError.describe e)
                | Ok None ->
                    return
                        Error(
                            sprintf
                                "scope '%s': the image of table '%s' at sequence %d is missing"
                                scopeId
                                run.TableId
                                run.Watermark.Sequence
                        )
                | Ok(Some rows) ->
                    let unattributed =
                        rows |> List.filter (DelegateStoredRow.isAttributed >> not) |> List.length

                    let found = {
                        Scope = scopeId
                        TableId = run.TableId
                        Sequence = run.Watermark.Sequence
                        RunId = run.RunId
                        Rows = rows.Length
                        Unattributed = unattributed
                    }

                    if unattributed = 0 || dryRun then
                        return Ok found
                    else
                        let attributed =
                            rows
                            |> List.map (fun r ->
                                if DelegateStoredRow.isAttributed r then
                                    r
                                else
                                    { r with WrittenBy = Some run.RunId })

                        match!
                            FactTableBlobIo.put
                                storage
                                scopeId
                                (tables.RowsName run.TableId run.Watermark.Sequence)
                                attributed
                        with
                        | Error e -> return Error(FactTableWriteError.describe e)
                        | Ok() -> return Ok found
            }

            let rec each (work: (string * DelegateTableCommit) list) (acc: DelegateImageAttribution list) = async {
                match work with
                | [] -> return Ok(List.rev acc)
                | (scopeId, commit) :: rest ->
                    match! image scopeId commit with
                    | Error e -> return Error e
                    | Ok found -> return! each rest (found :: acc)
            }

            let rec heads (remaining: string list) (acc: (string * DelegateTableCommit) list) = async {
                match remaining with
                | [] -> return Ok(List.rev acc)
                | scopeId :: rest ->
                    let! names = storage.List(scopeId, tables.HeadsPrefix)

                    let rec read (names: string list) (acc: (string * DelegateTableCommit) list) = async {
                        match names with
                        | [] -> return Ok acc
                        | name :: more ->
                            match! FactTableBlobIo.tryGet<DelegateTableHead> storage scopeId name with
                            | Error e -> return Error(FactTableWriteError.describe e)
                            | Ok None -> return! read more acc
                            | Ok(Some head) ->
                                return! read more (List.rev (head.Commits |> List.map (fun c -> scopeId, c)) @ acc)
                    }

                    match! read (List.sort names) acc with
                    | Error e -> return Error e
                    | Ok acc -> return! heads rest acc
            }

            match! heads (List.distinct scopes) [] with
            | Error e -> return Error e
            | Ok work ->
                match! each work [] with
                | Error e -> return Error e
                | Ok images ->
                    return
                        Ok {
                            DryRun = dryRun
                            Images = images |> List.sortBy (fun i -> i.Scope, i.TableId, i.Sequence)
                        }
        }

    /// The report as the task prints it.
    let render (report: DelegateAttributionReport) : string =
        let pending = report.Images |> List.filter (fun i -> i.Unattributed > 0)
        let rows = pending |> List.sumBy _.Unattributed

        let verb = if report.DryRun then "would attribute" else "attributed"

        [
            yield
                sprintf
                    "facts-attribute-delegate-rows%s: %d image(s) examined; %s %d row(s) in %d image(s)"
                    (if report.DryRun then " (dry run, nothing written)" else "")
                    report.Images.Length
                    verb
                    rows
                    pending.Length
            for i in report.Images ->
                sprintf
                    "  %s  %s #%d  run %s  %d row(s), %s"
                    i.Scope
                    i.TableId
                    i.Sequence
                    i.RunId
                    i.Rows
                    (if i.Unattributed = 0 then
                         "already attributed"
                     else
                         sprintf "%d %s to the run" i.Unattributed verb)
        ]
        |> List.map (fun line -> line + "\n")
        |> String.concat ""

/// A fact-tier job the runner refuses because it carries no re-minted scope.
type RefusedFactJob = {
    /// The job.
    JobId: JobId
    /// Its handler: a recompute or a coherence check.
    Handler: string
    /// Who scheduled it.
    CreatedBy: string
    /// When.
    CreatedAt: DateTime
}

/// What happened to one refused job.
[<RequireQualifiedAccess>]
type FactJobReissueOutcome =
    /// Listed by a dry run; nothing written.
    | WouldReissue
    /// Its carried scope was re-issued under the resolved scope.
    | Reissued
    /// The scheduler refused the re-issue; the job is as it was.
    | Failed of reason: string

/// What the re-issue did, or with `DryRun` would do.
type FactJobReissueReport = {
    /// The key the operator named.
    Key: ScopeKey
    /// Nothing was written.
    DryRun: bool
    /// Every refused fact-tier job in the shard, with what happened to it.
    Jobs: (RefusedFactJob * FactJobReissueOutcome) list
    /// Why nothing was re-issued, when the scope could not be resolved as the
    /// key the operator named.
    Refusal: string option
}

/// The re-issue of fact-tier jobs persisted under a bare shard id.
[<RequireQualifiedAccess>]
module FactJobReissue =

    /// The fact-tier handlers whose jobs the runner refuses without a
    /// re-minted scope.
    let handlers: string list = [ RecomputeJobHandler.HandlerName; CoherenceJobHandler.HandlerName ]

    /// `<kind>:<id>` as the key it names: `team`, `user` or `session`, and an
    /// id the key grammar renders. Anything else is refused — the kind is the
    /// operator's to say, never inferred from the id.
    let parseKey (text: string) : Result<ScopeKey, string> =
        let refused () =
            Error(sprintf "'%s' is not a scope key: write <kind>:<id>, where <kind> is team, user or session" text)

        match (if isNull text then "" else text).Split(':', 2) with
        | [| kind; id |] when not (String.IsNullOrWhiteSpace id) ->
            let key =
                match kind with
                | "team" -> Some(ScopeKey.Team id)
                | "user" -> Some(ScopeKey.User id)
                | "session" -> Some(ScopeKey.Session id)
                | _ -> None

            match key with
            | Some key when ScopeKey.tryParseContainer (ScopeKey.container key) = Some key -> Ok key
            | _ -> refused ()
        | _ -> refused ()

    /// The fact-tier jobs in `jobs` a dispatch would refuse: not cancelled,
    /// registered under a real shard, and carrying no scope `carrier` re-mints.
    let refused (carrier: ScopeCarrier) (jobs: JobDefinition list) : RefusedFactJob list =
        jobs
        |> List.filter (fun job ->
            List.contains job.Handler handlers
            && job.Status <> JobStatus.Cancelled
            && job.ScopeId <> ResolvedScope.AnonymousScopeId
            && (CarriedJobScope.ofDefinition carrier job).IsAnonymous)
        |> List.map (fun job -> {
            JobId = job.JobId
            Handler = job.Handler
            CreatedBy = job.CreatedBy
            CreatedAt = job.CreatedAt
        })
        |> List.sortBy (fun j -> j.CreatedAt, j.JobId)

    /// Re-issue every refused fact-tier job in the shard `key` names under the
    /// scope the deployment's resolver resolves `principal` to — refused
    /// unless that scope is `key`. With `dryRun`, list them and write nothing;
    /// a dry run given a principal also checks it resolves to `key`.
    let reissue
        (scheduler: IJobScheduler)
        (carrier: ScopeCarrier)
        (resolver: DeclaredPrincipalResolver)
        (key: ScopeKey)
        (principal: string option)
        (dryRun: bool)
        : Async<FactJobReissueReport> =
        async {
            let shard = ScopeKey.scopeId key
            let! jobs = scheduler.ListJobs shard
            let refusedJobs = refused carrier jobs

            let report (outcomes: (RefusedFactJob * FactJobReissueOutcome) list) (refusal: string option) = {
                Key = key
                DryRun = dryRun
                Jobs = outcomes
                Refusal = refusal
            }

            let! resolved = async {
                match principal with
                | None ->
                    return
                        if dryRun then
                            Ok None
                        else
                            Error
                                "name the principal the deployment's resolver resolves to this scope (--principal <user-id>)"
                | Some userId ->
                    match! resolver.Resolve(DeclaredPrincipal.userOf userId userId) with
                    | Error e ->
                        return
                            Error(
                                sprintf
                                    "principal '%s' does not resolve: it lacks %s"
                                    userId
                                    (DeclaredPrincipal.missingGrant e)
                            )
                    | Ok scope when scope.Key = Some key -> return Ok(Some scope)
                    | Ok scope ->
                        return
                            Error(
                                sprintf
                                    "principal '%s' resolves to %s, not to %s; nothing was re-issued"
                                    userId
                                    (scope.Key
                                     |> Option.map ScopeKey.container
                                     |> Option.defaultValue "a scope outside the key grammar")
                                    (ScopeKey.container key)
                            )
            }

            match resolved with
            | Error refusal ->
                return report (refusedJobs |> List.map (fun j -> j, FactJobReissueOutcome.WouldReissue)) (Some refusal)
            | Ok _ when dryRun ->
                return report (refusedJobs |> List.map (fun j -> j, FactJobReissueOutcome.WouldReissue)) None
            | Ok None -> return report [] (Some "no scope was resolved")
            | Ok(Some scope) ->
                let rec each (remaining: RefusedFactJob list) acc = async {
                    match remaining with
                    | [] -> return List.rev acc
                    | job :: rest ->
                        match! JobScopeReissue.reissue scheduler scope shard job.JobId with
                        | Ok() -> return! each rest ((job, FactJobReissueOutcome.Reissued) :: acc)
                        | Error e -> return! each rest ((job, FactJobReissueOutcome.Failed(sprintf "%A" e)) :: acc)
                }

                let! outcomes = each refusedJobs []
                return report outcomes None
        }

    /// The report as the task prints it.
    let render (report: FactJobReissueReport) : string =
        let head =
            sprintf
                "facts-reissue-jobs%s: %d refused fact-tier job(s) in %s"
                (if report.DryRun then " (dry run, nothing written)" else "")
                report.Jobs.Length
                (ScopeKey.container report.Key)

        [
            yield head
            match report.Refusal with
            | Some refusal -> yield sprintf "  refused: %s" refusal
            | None -> ()
            for job, outcome in report.Jobs ->
                sprintf
                    "  %O  %s  scheduled by %s at %s  %s"
                    job.JobId
                    job.Handler
                    job.CreatedBy
                    (job.CreatedAt.ToString "o")
                    (match outcome with
                     | FactJobReissueOutcome.WouldReissue -> "would be re-issued"
                     | FactJobReissueOutcome.Reissued -> "re-issued"
                     | FactJobReissueOutcome.Failed reason -> sprintf "NOT re-issued: %s" reason)
        ]
        |> List.map (fun line -> line + "\n")
        |> String.concat ""

    /// The exit code: 0 when nothing was refused and every job was re-issued
    /// (or, with a dry run, listed); 1 otherwise.
    let exitCode (report: FactJobReissueReport) : int =
        let failed =
            report.Jobs
            |> List.exists (fun (_, o) ->
                match o with
                | FactJobReissueOutcome.Failed _ -> true
                | _ -> false)

        if report.Refusal.IsSome || failed then 1 else 0

/// The two operator tasks, as the `--operator-task` startup mode runs them.
[<RequireQualifiedAccess>]
module FactsOperatorTasks =

    /// The delegate row-attribution task's name.
    [<Literal>]
    let AttributeDelegateRows = "facts-attribute-delegate-rows"

    /// The fact-job re-issue task's name.
    [<Literal>]
    let ReissueJobs = "facts-reissue-jobs"

    let private tryService<'T> = ServiceComposition.tryService<'T>

    let private usageAttribute =
        "usage: facts-attribute-delegate-rows (--scope <id>... | --all-scopes) [--dry-run]\n"

    let private usageReissue =
        "usage: facts-reissue-jobs --scope <team|user|session>:<id> [--principal <user-id>] [--dry-run]\n"

    let private runAttribute (sp: IServiceProvider) (args: string list) : Async<string * int> = async {
        let rec parse scopes all dry rest =
            match rest with
            | [] -> Ok(List.rev scopes, all, dry)
            | "--scope" :: id :: tail -> parse (id :: scopes) all dry tail
            | "--all-scopes" :: tail -> parse scopes true dry tail
            | "--dry-run" :: tail -> parse scopes all true tail
            | token :: _ -> Error(sprintf "unknown argument '%s'" token)

        match parse [] false false args with
        | Error e -> return sprintf "%s: %s\n%s" AttributeDelegateRows e usageAttribute, 2
        | Ok([], false, _) ->
            return
                sprintf "%s: name a scope (--scope <id>) or pass --all-scopes\n%s" AttributeDelegateRows usageAttribute,
                2
        | Ok(_ :: _, true, _) ->
            return sprintf "%s: --scope and --all-scopes are exclusive\n%s" AttributeDelegateRows usageAttribute, 2
        | Ok(named, all, dryRun) ->
            let! scopes = async {
                if not all then
                    return named
                else
                    match tryService<IScopeEnumerator> sp, tryService<TeamManagement.ITeamStore> sp with
                    | Some enumerator, _ -> return! enumerator.ListScopes()
                    | None, Some teams -> return! (ScopeEnumeration.fromTeamStore teams).ListScopes()
                    | None, None -> return ScopeEnumeration.wellKnownContainers
            }

            match! DelegateRowAttribution.attribute (sp.GetRequiredService<IBlobStorage>()) dryRun scopes with
            | Ok report -> return DelegateRowAttribution.render report, 0
            | Error e -> return sprintf "%s: %s\n" AttributeDelegateRows e, 1
    }

    let private runReissue (sp: IServiceProvider) (args: string list) : Async<string * int> = async {
        let rec parse key principal dry rest =
            match rest with
            | [] -> Ok(key, principal, dry)
            | "--scope" :: text :: tail ->
                match key with
                | Some _ -> Error "name one --scope per run"
                | None ->
                    FactJobReissue.parseKey text
                    |> Result.bind (fun k -> parse (Some k) principal dry tail)
            | "--principal" :: userId :: tail -> parse key (Some userId) dry tail
            | "--dry-run" :: tail -> parse key principal true tail
            | token :: _ -> Error(sprintf "unknown argument '%s'" token)

        match parse None None false args with
        | Error e -> return sprintf "%s: %s\n%s" ReissueJobs e usageReissue, 2
        | Ok(None, _, _) -> return sprintf "%s: name the scope (--scope <kind>:<id>)\n%s" ReissueJobs usageReissue, 2
        | Ok(Some key, principal, dryRun) ->
            match
                tryService<IJobScheduler> sp, tryService<ScopeCarrier> sp, tryService<DeclaredPrincipalResolver> sp
            with
            | Some scheduler, Some carrier, Some resolver ->
                let! report = FactJobReissue.reissue scheduler carrier resolver key principal dryRun
                return FactJobReissue.render report, FactJobReissue.exitCode report
            | _ ->
                return
                    sprintf
                        "%s: this composition does not hold a job scheduler, the platform's ScopeCarrier and its DeclaredPrincipalResolver; compose the server through the platform\n"
                        ReissueJobs,
                    1
    }

    /// The row-attribution task.
    let attributeDelegateRowsTask: StartupModes.OperatorTask =
        StartupModes.OperatorTask(
            AttributeDelegateRows,
            "record the run that wrote each row of every delegated table's stored image",
            runAttribute
        )

    /// The fact-job re-issue task.
    let reissueJobsTask: StartupModes.OperatorTask =
        StartupModes.OperatorTask(
            ReissueJobs,
            "re-issue fact-tier jobs persisted under a bare shard id under the scope the operator names",
            runReissue
        )

/// Composition for the fact tier's operator tasks.
module FactsOperatorTaskCompose =

    /// Adds the fact tier's two operator tasks, so `toolup facts migrate
    /// --attribute-delegate-rows` and `--reissue-jobs` can run against this
    /// composition. Registers nothing a normal boot reads: a task runs only
    /// when the process is started in the `--operator-task` mode.
    let withFactsOperatorTasks (app: ServerApp) : ServerApp =
        let register (services: IServiceCollection) =
            services
                .AddSingleton<StartupModes.OperatorTask>(FactsOperatorTasks.attributeDelegateRowsTask)
                .AddSingleton<StartupModes.OperatorTask>(FactsOperatorTasks.reissueJobsTask)

        app
        |> ServerApp.addContribution (Contribution.service (ComponentId.forCompanionSlot "ToolUp.Facts") register)