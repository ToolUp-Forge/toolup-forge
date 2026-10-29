// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1111 — the server half of `NarrativePromptApi`: an Owner or Admin
/// of the publishing team rewords a grounded narrative's instruction and
/// section guidance, keeps every earlier wording as a version, resets to the
/// declaration, and runs the narrative now with the wording in force.
///
/// **What it can change, and what it cannot.** A save stores a `Prompt` and
/// guidance for sections the narrative DECLARES (an undeclared or repeated
/// section is refused by name, through `GroundedNarrativeDefinition.reword`).
/// It has no field for anything else: the title, the section structure, the
/// metrics, the surface and the grounding rules (`GroundedNarrativeRun.rules`)
/// stay the declaration's, and the rules are returned read-only. A wording
/// that tells the model to drop citations still meets the grounding gate,
/// which refuses the report: the gate, not the wording, is the guarantee.
///
/// **Gates, every method, in one place (`authorised`)** — the 1107 shape: the
/// caller is a `TeamMember` (the active team IS the publishing scope; a
/// share-token bearer, an anonymous session and a publication reader are
/// refused), whose role passes `TeamRoles.canWriteTeamConfig` (Owner or
/// Admin), on a request the platform resolved to that team's scope. The
/// wording is stored under that RESOLVED scope's container, the same key the
/// run reads under the scope it runs in, so one team's wording cannot reach
/// another team's run.
///
/// **Regenerate** schedules a manual job (`NarrativeRegenerateJob`) under the
/// request's resolved scope and fires it through `IJobScheduler.TriggerOnce`
/// — the manual trigger the jobs admin's re-run uses — so the run is
/// published or refused exactly as a scheduled one, and a run that fails for
/// good is listed in the jobs admin with its reason.
///
/// Off unless composed: `withPromptAdmin` mounts the API and registers the
/// regenerate job handler (GP 13).
module ToolUp.AI.NarrativePromptHandler

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.TeamManagement
open ToolUp.AI
open ToolUp.AI.GroundedNarrativeRun

// ─── Audit rows ──────────────────────────────────────────────────
//
// Both ride the uniform `RemotingMethodAudited` case with an open-vocabulary
// `Custom:` kind (the Phase 859 shape), so no exhaustive match over the audit
// union has to change.

module NarrativePromptAudit =
    /// A saved wording version.
    [<Literal>]
    let SavedKind = "Custom:NarrativePromptSaved"

    /// The declared wording put back.
    [<Literal>]
    let ResetKind = "Custom:NarrativePromptReset"

// ─── The regenerate job ──────────────────────────────────────────

module NarrativeRegenerateJob =
    /// The job handler name a regeneration is scheduled under.
    [<Literal>]
    let HandlerName = "_narratives.regenerate"

    /// The `GroundedNarrativeRequest.Trigger` a regeneration records.
    [<Literal>]
    let TriggerName = "admin-regenerate"

    /// What a regeneration job carries.
    type RegeneratePayload = { RunKey: string }

    let private jsonOptions = FableConverters.create ()

    let tryParsePayload (json: string) : RegeneratePayload option =
        try
            let parsed = JsonSerializer.Deserialize<RegeneratePayload>(json, jsonOptions)

            if isNull (box parsed) || String.IsNullOrWhiteSpace parsed.RunKey then
                None
            else
                Some parsed
        with _ ->
            None

    /// One job per narrative per scope: the idempotency key makes a second
    /// regeneration within a day fire the SAME job again rather than add a
    /// row to the jobs admin each time.
    let registrationFor (scopeId: string) (runKey: string) (requestedBy: string) : JobRegistration = {
        ScopeId = scopeId
        Handler = HandlerName
        Payload = JsonSerializer.Serialize({ RunKey = runKey }, jsonOptions)
        Trigger = Trigger.Manual
        Idempotency =
            Some {
                Key = sprintf "narrative-regenerate-%s" runKey
                TtlSeconds = 86400
            }
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = Some runKey
        Precision = JobPrecision.Minute
        CreatedBy = requestedBy
        Tags = Map.ofList [ "origin", TriggerName; "run", runKey ]
    }

    /// Run the narrative under the scope the job carries. The outcome maps to
    /// a job result through `GroundedNarrativeOutcome.toJobResult`, as the
    /// data-arrival job's does.
    type RegenerateJobHandler(run: IGroundedNarrativeRun) =
        interface IJobHandler with
            member _.Execute(ctx: JobContext) = async {
                match tryParsePayload ctx.Payload with
                | None -> return PermanentFailure "narrative regeneration job: unreadable payload"
                | Some _ when ctx.Scope.IsAnonymous ->
                    return
                        PermanentFailure
                            "a narrative regeneration must run under a resolved scope; this one runs anonymous"
                | Some payload ->
                    let! outcome =
                        run.Run {
                            RunKey = payload.RunKey
                            Scope = ctx.Scope
                            Access = None
                            Trigger = TriggerName
                        }

                    return GroundedNarrativeOutcome.toJobResult outcome
            }

    /// Register the handler with the composed scheduler at startup — only
    /// when a scheduler and a run are composed (GP 13).
    let hostedService (sp: IServiceProvider) : IHostedService =
        { new IHostedService with
            member _.StartAsync(_ct) =
                match sp.GetService(typeof<IJobScheduler>), sp.GetService(typeof<IGroundedNarrativeRun>) with
                | (:? IJobScheduler as scheduler), (:? IGroundedNarrativeRun as run) ->
                    scheduler.RegisterHandler(HandlerName, RegenerateJobHandler(run))
                | _ -> ()

                System.Threading.Tasks.Task.CompletedTask

            member _.StopAsync(_ct) =
                System.Threading.Tasks.Task.CompletedTask
        }

// ─── The API ─────────────────────────────────────────────────────

/// Who an authorised call is for.
type private Caller = {
    UserId: string
    TeamId: string
    /// The scope the request resolved to — the team's.
    Resolved: ResolvedScope
    Scope: StorageScope
    Registry: GroundedNarrativeRegistry
}

let private describeScheduleError (error: ScheduleError) : string =
    match error with
    | ScheduleError.InvalidCron(expression, reason) -> sprintf "invalid schedule '%s': %s" expression reason
    | ScheduleError.HandlerNotRegistered name ->
        sprintf "the job handler '%s' is not registered (is a job scheduler composed and started?)" name
    | ScheduleError.PrecisionUnsupported(supplied, _) -> sprintf "the scheduler does not support %A precision" supplied
    | ScheduleError.StorageFailure message -> message

/// One trimmed guidance entry, with a null-safe read of the wire record.
let private trimmed (g: NarrativeSectionGuidance) : NarrativeSectionGuidance = {
    SectionId = (if isNull g.SectionId then "" else g.SectionId.Trim())
    Guidance = (if isNull g.Guidance then "" else g.Guidance.Trim())
}

/// Validate a save against the declaration and the limits. `Ok` carries the
/// trimmed instruction and guidance.
let private validate
    (definition: GroundedNarrativeDefinition)
    (request: SaveNarrativePromptRequest)
    : Result<string * NarrativeSectionGuidance list, string> =
    let prompt = if isNull request.Prompt then "" else request.Prompt.Trim()

    let guidance =
        (if isNull (box request.Guidance) then
             []
         else
             request.Guidance)
        |> List.map trimmed

    if String.IsNullOrWhiteSpace prompt then
        Error "The instruction cannot be blank. To use the declared wording, reset instead."
    elif prompt.Length > NarrativePromptLimits.MaxPromptLength then
        Error(
            sprintf
                "The instruction is %d characters; the limit is %d."
                prompt.Length
                NarrativePromptLimits.MaxPromptLength
        )
    else
        match guidance |> List.tryFind (fun g -> g.Guidance = "") with
        | Some blank ->
            Error(
                sprintf
                    "Section '%s' has blank guidance. Leave a section out to keep its declared guidance."
                    blank.SectionId
            )
        | None ->
            match
                guidance
                |> List.tryFind (fun g -> g.Guidance.Length > NarrativePromptLimits.MaxGuidanceLength)
            with
            | Some long ->
                Error(
                    sprintf
                        "Section '%s' guidance is %d characters; the limit is %d."
                        long.SectionId
                        long.Guidance.Length
                        NarrativePromptLimits.MaxGuidanceLength
                )
            | None ->
                GroundedNarrativeDefinition.reword
                    prompt
                    (guidance |> List.map (fun g -> g.SectionId, g.Guidance))
                    definition
                |> Result.map (fun _ -> prompt, guidance)

/// The narrative's most recent run in the scope, from the audit trail.
let private lastRunOf (rows: ModuleEvent list) (runKey: string) : NarrativeLastRun option =
    rows
    |> List.choose (fun row ->
        GroundedNarrativeAuditRow.tryParse row.Payload
        |> Option.filter (fun parsed -> parsed.RunKey = runKey)
        |> Option.map (fun parsed -> row.OccurredAt, parsed))
    |> List.sortByDescending fst
    |> List.tryHead
    |> Option.map (fun (at, row) ->
        let offences =
            if isNull (box row.Offences) || List.isEmpty row.Offences then
                None
            else
                Some(String.concat "; " row.Offences)

        let detail =
            [ row.Detail; offences; row.PromptFallback ]
            |> List.choose id
            |> function
                | [] -> None
                | parts -> Some(String.concat "; " parts)

        {
            Outcome = row.Outcome
            Trigger = row.Trigger
            PromptVersion = row.PromptVersion
            Detail = detail
            OccurredAt = at
        })

/// The handler's one read of the request's container: the per-request
/// `AccessContext` and the substrate are request-scoped, and `makeApi`'s
/// builder is handed only the `HttpContext`.
let private serviceOf<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | :? 'T as s -> Some s
    | _ -> None

/// `NarrativePromptApi` over the request. Every method acts on the caller's
/// active team.
let narrativePromptApi (ctx: HttpContext) : NarrativePromptApi =
    let access =
        serviceOf<AccessContext> ctx
        |> Option.defaultWith (fun () -> AccessContext.unrestricted (AnonymousSession "anonymous"))

    let store =
        NarrativeWording.storeOver (serviceOf<IBlobStorage> ctx) (serviceOf<ILogger> ctx)

    let authorised (f: Caller -> Async<Result<'T, string>>) : Async<Result<'T, string>> = async {
        match serviceOf<GroundedNarrativeRegistry> ctx with
        | None -> return Error "Grounded narratives are not composed in this deployment."
        | Some registry ->
            match access.Subject with
            | TeamMember(userId, teamId) when not (AccessContext.isPublicationReader access) ->
                match serviceOf<ITeamStore> ctx with
                | None -> return Error "Team management is not available in this deployment."
                | Some teams ->
                    match! teams.GetMemberRole(teamId, userId) with
                    | Some role when TeamRoles.canWriteTeamConfig role ->
                        let resolved = StorageScopeResolver.ScopeResolution.forRequest ctx

                        match resolved.Storage with
                        | Some scope when scope.ScopeId = teamId ->
                            return!
                                f {
                                    UserId = userId
                                    TeamId = teamId
                                    Resolved = resolved
                                    Scope = scope
                                    Registry = registry
                                }
                        | _ ->
                            return
                                Error
                                    "This request did not resolve to your team's scope; reopen the team and try again."
                    | Some role ->
                        return
                            Error
                                $"Only team owners and admins can edit a report's wording. Your role: {TeamRoles.displayName role}."
                    | None -> return Error "You are not a member of this team."
            | _ -> return Error "A report's wording is edited by an owner or admin of the publishing team."
    }

    let definitionOf (caller: Caller) (runKey: string) : Result<GroundedNarrativeDefinition, string> =
        match caller.Registry.TryResolve(if isNull runKey then "" else runKey) with
        | Some definition -> Ok definition
        | None -> Error(sprintf "No report '%s' is registered in this deployment." runKey)

    let versionsOf (caller: Caller) (runKey: string) : Async<Result<NarrativePromptVersion list, string>> = async {
        match store with
        | None -> return Ok []
        | Some store -> return! store.Versions(caller.Scope.Container, runKey)
    }

    let viewOf (caller: Caller) (definition: GroundedNarrativeDefinition) = async {
        match! versionsOf caller definition.Key with
        | Error reason -> return Error(sprintf "The saved wording of '%s' could not be read: %s" definition.Key reason)
        | Ok versions ->
            let! rows = async {
                match serviceOf<IEventStore> ctx with
                | Some events -> return! events.ReadBySource(caller.Scope.ScopeId, GroundedNarrativeEvents.SourceModule)
                | None -> return []
            }

            return
                Ok {
                    RunKey = definition.Key
                    DisplayName = definition.DisplayName
                    Title = definition.Title
                    DeclaredPrompt = definition.Prompt
                    DeclaredSections =
                        definition.Sections
                        |> List.map (fun s -> {
                            SectionId = s.Id
                            Heading = s.Heading
                            Guidance = s.Guidance
                        })
                    Current = NarrativeWording.current versions
                    LatestVersion = NarrativeWording.latestNumber versions
                    Rules = String.concat "\n" rules
                    LastRun = lastRunOf rows definition.Key
                }
    }

    /// Each save and reset is audited under the team, awaited before the
    /// response. Not wrapped in a catch: `IAuditLog.Record` absorbs a failed
    /// write under the default policy and raises under a compliance-grade one
    /// on purpose, so the call then fails rather than completing un-audited.
    let auditFor
        (caller: Caller)
        (kind: string)
        (methodName: string)
        (runKey: string)
        (version: NarrativePromptVersion)
        =
        async {
            match serviceOf<IAuditLog> ctx with
            | Some log ->
                do!
                    log.Record(
                        AuditStreamKey.ofTeam caller.TeamId,
                        RemotingMethodAudited {
                            Kind = kind
                            MethodName = methodName
                            SubjectId = caller.UserId
                            CorrelationId = None
                            Payload =
                                Map.ofList [
                                    "teamId", caller.TeamId
                                    "runKey", runKey
                                    "version", string version.Version
                                    "author", version.Author
                                ]
                        }
                    )
            | None -> ()
        }

    let withDefinition (runKey: string) (f: Caller -> GroundedNarrativeDefinition -> Async<Result<'T, string>>) =
        authorised (fun caller -> async {
            match definitionOf caller runKey with
            | Error reason -> return Error reason
            | Ok definition -> return! f caller definition
        })

    let noStore =
        "A report's wording cannot be saved: no blob storage is composed in this deployment."

    {
        ListNarratives =
            fun () ->
                authorised (fun caller -> async {
                    let rec gather (acc: NarrativePromptSummary list) (definitions: GroundedNarrativeDefinition list) = async {
                        match definitions with
                        | [] -> return Ok(List.rev acc)
                        | definition :: rest ->
                            match! versionsOf caller definition.Key with
                            | Error reason ->
                                return
                                    Error(
                                        sprintf "The saved wording of '%s' could not be read: %s" definition.Key reason
                                    )
                            | Ok versions ->
                                let summary = {
                                    RunKey = definition.Key
                                    DisplayName = definition.DisplayName
                                    CurrentVersion =
                                        NarrativeWording.current versions
                                        |> Option.map _.Version
                                        |> Option.defaultValue 0
                                }

                                return! gather (summary :: acc) rest
                    }

                    return! gather [] caller.Registry.Definitions
                })

        GetNarrative = fun runKey -> withDefinition runKey viewOf

        ListVersions =
            fun runKey ->
                withDefinition runKey (fun caller definition -> async {
                    let! versions = versionsOf caller definition.Key
                    return versions |> Result.map List.rev
                })

        SavePrompt =
            fun request ->
                withDefinition (if isNull (box request) then "" else request.RunKey) (fun caller definition -> async {
                    match validate definition request, store with
                    | Error refusal, _ -> return Error refusal
                    | Ok _, None -> return Error noStore
                    | Ok(prompt, guidance), Some store ->
                        let! appended =
                            store.Append(
                                caller.Scope.Container,
                                definition.Key,
                                fun versions ->
                                    let latest = NarrativeWording.latestNumber versions

                                    if latest <> request.BasedOnVersion then
                                        let saved = List.last versions

                                        Error(
                                            sprintf
                                                "Version %d was saved by %s at %s, after the version %d you opened. Reload and edit the current wording."
                                                saved.Version
                                                saved.Author
                                                (saved.SavedAt.ToString "u")
                                                request.BasedOnVersion
                                        )
                                    else
                                        Ok(
                                            Some {
                                                Version = latest + 1
                                                IsReset = false
                                                Prompt = prompt
                                                Guidance = guidance
                                                Author = caller.UserId
                                                SavedAt = DateTime.UtcNow
                                            }
                                        )
                            )

                        match appended with
                        | Error reason -> return Error reason
                        | Ok None -> return! viewOf caller definition
                        | Ok(Some version) ->
                            do! auditFor caller NarrativePromptAudit.SavedKind "SavePrompt" definition.Key version

                            return! viewOf caller definition
                })

        ResetPrompt =
            fun runKey ->
                withDefinition runKey (fun caller definition -> async {
                    match store with
                    | None -> return Error noStore
                    | Some store ->
                        let! appended =
                            store.Append(
                                caller.Scope.Container,
                                definition.Key,
                                fun versions ->
                                    match NarrativeWording.current versions with
                                    // The declaration is already in force.
                                    | None -> Ok None
                                    | Some _ ->
                                        Ok(
                                            Some {
                                                Version = NarrativeWording.latestNumber versions + 1
                                                IsReset = true
                                                Prompt = ""
                                                Guidance = []
                                                Author = caller.UserId
                                                SavedAt = DateTime.UtcNow
                                            }
                                        )
                            )

                        match appended with
                        | Error reason -> return Error reason
                        | Ok None -> return! viewOf caller definition
                        | Ok(Some version) ->
                            do! auditFor caller NarrativePromptAudit.ResetKind "ResetPrompt" definition.Key version

                            return! viewOf caller definition
                })

        Regenerate =
            fun runKey ->
                withDefinition runKey (fun caller definition -> async {
                    match serviceOf<IJobScheduler> ctx with
                    | None ->
                        return
                            Error
                                "No job scheduler is composed in this deployment, so a report cannot be regenerated on demand."
                    | Some scheduler ->
                        let registration =
                            NarrativeRegenerateJob.registrationFor caller.Scope.ScopeId definition.Key caller.UserId

                        match! scheduler.Schedule(caller.Resolved, registration) with
                        | Error error -> return Error(describeScheduleError error)
                        | Ok jobId ->
                            match! scheduler.TriggerOnce(caller.Scope.ScopeId, jobId, caller.UserId) with
                            | Error reason -> return Error reason
                            | Ok() -> return Ok jobId
                })
    }

/// Compose the wording admin onto an app that composed grounded narratives
/// (`GroundedNarratives.compose`): mount `NarrativePromptApi` and register the
/// regenerate job handler with the scheduler at startup. A deployment that
/// does not call this mounts nothing (GP 13); its runs still read a wording
/// saved for their scope, of which there is none.
let withPromptAdmin (app: ServerApp) : ServerApp =
    let register (s: IServiceCollection) =
        // Phase 1051 — exempt from the profile matrix: a handler
        // registration, placed by the scheduler's own gate.
        s
        |> ProcessProfileGate.addExempt
            "Narrative regenerate handler registration"
            ProcessProfileGate.SchedulerRegistrationReason
            NarrativeRegenerateJob.hostedService
        |> ignore

        s

    let serviceConfig =
        match app.Extensions.ServiceConfig with
        | None -> Some register
        | Some existing -> Some(fun s -> register (existing s))

    {
        app with
            Extensions = {
                app.Extensions with
                    ServiceConfig = serviceConfig
                    Handlers = app.Extensions.Handlers @ [ makeApi narrativePromptApi ]
            }
    }