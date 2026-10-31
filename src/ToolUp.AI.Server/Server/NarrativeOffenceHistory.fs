// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// A grounded narrative learns from the refusals that recur, and proposes a
/// wording change an Owner or Admin approves.
///
/// **The history.** After every run that reaches the grounding gate, each
/// claim the gate refused — in the first draft or in any repair round — is
/// kept with its class, its place, whether a repair round fixed it, the run's
/// date and the wording version in force. The run's audit trail is the
/// source: the outcome row names the wording version, the repair-round rows
/// name every claim refused along the way. The history is one blob per
/// narrative in the scope's own container (`narrative-learning/<run key>`),
/// beside the wording history, and holds the proposals too.
///
/// **Recurrence.** A class recurs when it was refused on `Threshold` of the
/// last `Window` runs of the narrative under ONE wording version (3 of 5 by
/// default). A new version starts the count afresh: the evidence was
/// gathered under the old one.
///
/// **A proposal is never in force.** On recurrence the run's own provider,
/// under the funding the run itself used, is asked for ONE sentence to add to
/// the instruction. A sentence the grounding gate would refuse — one stating
/// a figure — is discarded. A kept one is saved as an OPEN proposal in the
/// learning blob, never in the wording history the run reads, so no run can
/// use it; only an Owner or Admin approving it (`NarrativePromptApi`) saves it
/// as a wording version. At most one open proposal per class per narrative;
/// a dismissed or approved class is not proposed again under the same
/// wording version; an open proposal whose wording changed before it was
/// decided lapses.
///
/// Off unless composed: `NarrativeOffenceLearning.compose` (GP 13).
module ToolUp.AI.NarrativeOffenceHistory

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.Extensions.DependencyInjection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Narrative
open ToolUp.Platform.Transport
open ToolUp.AI
open ToolUp.AI.GroundedNarrativeRun

/// The learning loop's knobs.
type NarrativeLearningPolicy = {
    /// The runs, under one wording version, a recurrence is counted over.
    Window: int
    /// The runs of the window that must refuse a class for it to recur.
    Threshold: int
    /// The refused claims a proposal is drafted from, at most.
    MaxEvidence: int
    /// The runs the history keeps per narrative; the oldest goes first.
    MaxRunsKept: int
    /// The longest proposed sentence kept, in characters.
    MaxSentenceLength: int
}

module NarrativeLearningPolicy =
    /// 3 of the last 5 runs, five claims of evidence, fifty runs kept, a
    /// sentence of at most 400 characters.
    let defaults: NarrativeLearningPolicy = {
        Window = 5
        Threshold = 3
        MaxEvidence = 5
        MaxRunsKept = 50
        MaxSentenceLength = 400
    }

// ─── The classifier ──────────────────────────────────────────────

module NarrativeOffenceClassifier =

    /// A digit that starts a figure: not preceded by a letter or a digit.
    let private figure = Regex(@"(?<![\p{L}\d])\d", RegexOptions.CultureInvariant)

    /// A letter followed by a digit inside one token ("Q3", "B2B").
    let private name = Regex(@"\p{L}\d", RegexOptions.CultureInvariant)

    /// The class of a number the gate found in prose with no reference: a
    /// figure written in digits, else a name carrying digits, else a number
    /// written in words.
    let ofUnreferencedClaim (claim: string) : NarrativeOffenceClass =
        let claim = if isNull claim then "" else claim

        if figure.IsMatch claim then
            NarrativeOffenceClass.UnreferencedNumber
        elif name.IsMatch claim then
            NarrativeOffenceClass.NameReadAsFigure
        else
            NarrativeOffenceClass.NumberWord

    /// The class of one gate offence.
    let classify (offence: GroundingOffence) : NarrativeOffenceClass =
        match offence.Reason with
        | UnreferencedNumber -> ofUnreferencedClaim offence.Claim
        | MetricWithoutReference -> NarrativeOffenceClass.UnreferencedNumber
        | UnresolvedReference _
        | UnregisteredReferenceKind _ -> NarrativeOffenceClass.MisattributedReference
        | SupersededReference _ -> NarrativeOffenceClass.Superseded
        | UndisclosableReference _ -> NarrativeOffenceClass.Withheld
        | MisstatedValue _ -> NarrativeOffenceClass.MisstatedValue

    /// The reason wordings of `GroundingOffenceReason.describe`, each with
    /// the class it names (`None`: decided by the claim).
    let private reasons: (string * NarrativeOffenceClass option) list = [
        @"a number in prose that is not a reference", None
        @"a metric with no reference", Some NarrativeOffenceClass.UnreferencedNumber
        @"reference '.*?' does not resolve", Some NarrativeOffenceClass.MisattributedReference
        @"reference '.*?' names kind '.*?', which is not a registered citable reference kind",
        Some NarrativeOffenceClass.MisattributedReference
        @"reference '.*?' is superseded(?: by '.*?')?", Some NarrativeOffenceClass.Superseded
        @"reference '.*?' is not disclosable here under policy .*?", Some NarrativeOffenceClass.Withheld
        @"reference '.*?' is stated as '.*?' but its value is '.*?'", Some NarrativeOffenceClass.MisstatedValue
    ]

    let private described =
        reasons
        |> List.map (fun (reason, cls) ->
            Regex(
                $"^(?<location>.*): {reason} — \"(?<claim>.*)\"$",
                RegexOptions.CultureInvariant ||| RegexOptions.Singleline
            ),
            cls)

    /// Read an offence back from its audit-trail line
    /// (`GroundingOffence.describe`): its class, location and claim. `None`
    /// for a line that is not one.
    let tryClassifyDescribed (line: string) : (NarrativeOffenceClass * string * string) option =
        if String.IsNullOrEmpty line then
            None
        else
            described
            |> List.tryPick (fun (pattern, cls) ->
                let m = pattern.Match line

                if not m.Success then
                    None
                else
                    let claim = m.Groups["claim"].Value

                    Some(
                        cls |> Option.defaultWith (fun () -> ofUnreferencedClaim claim),
                        m.Groups["location"].Value,
                        claim
                    ))

    /// The section and the kind of element a location names.
    let placeOf (location: string) : string * string =
        match GroundedRepair.tryPlace location with
        | None -> "", "element"
        | Some place ->
            let kind =
                match place.Part with
                | GroundedRepair.WholeElement -> "element"
                | GroundedRepair.Span _ -> "prose"
                | GroundedRepair.Item _ -> "list"
                | GroundedRepair.Row _ -> "table"
                | GroundedRepair.Entry _ -> "entry"

            place.Section, kind

    let private offence cls (location: string) claim repaired : NarrativeOffence =
        let section, kind = placeOf location

        {
            Class = cls
            Section = section
            ElementKind = kind
            Location = location
            Claim = claim
            Repaired = repaired
        }

    /// One gate offence as the history keeps it.
    let ofOffence (repaired: bool) (o: GroundingOffence) : NarrativeOffence =
        offence (classify o) o.Location o.Claim repaired

    /// One audit-trail line as the history keeps it.
    let ofDescribed (repaired: bool) (line: string) : NarrativeOffence option =
        tryClassifyDescribed line
        |> Option.map (fun (cls, location, claim) -> offence cls location claim repaired)

    /// Every claim one run refused: those still refused at its end (`final`,
    /// withheld or refusing the run), and those a repair round fixed — every
    /// other claim a round was asked to repair or left (`roundLines`).
    let runOffences (final: GroundingOffence list) (roundLines: string list) : NarrativeOffence list =
        let finalLines = final |> List.map GroundingOffence.describe |> Set.ofList

        let repaired =
            roundLines
            |> List.distinct
            |> List.filter (fun line -> not (finalLines.Contains line))
            |> List.choose (ofDescribed true)

        (final |> List.distinctBy GroundingOffence.describe |> List.map (ofOffence false))
        @ repaired

// ─── Recurrence, proposals and decisions (pure) ──────────────────

/// One narrative's learning in one scope: the runs and the proposals.
type NarrativeLearning = {
    /// Oldest first.
    Runs: NarrativeOffenceRun list
    /// Oldest first.
    Proposals: NarrativeWordingProposal list
}

module NarrativeLearning =
    let empty: NarrativeLearning = { Runs = []; Proposals = [] }

    /// The class order the panel and the drafting follow.
    let private rank (cls: NarrativeOffenceClass) =
        NarrativeOffenceClass.all |> List.findIndex ((=) cls)

    /// The recurrence window under `version`: its latest runs, newest first.
    let window (policy: NarrativeLearningPolicy) (version: int) (runs: NarrativeOffenceRun list) =
        runs
        |> List.filter (fun r -> r.WordingVersion = version)
        |> List.rev
        |> List.truncate policy.Window

    let private refuses (cls: NarrativeOffenceClass) (run: NarrativeOffenceRun) =
        run.Offences |> List.exists (fun o -> o.Class = cls)

    /// How `cls` stands under `version`.
    let standing
        (policy: NarrativeLearningPolicy)
        (runs: NarrativeOffenceRun list)
        (version: int)
        (cls: NarrativeOffenceClass)
        : NarrativeOffenceStanding =
        let runs = window policy version runs
        let withClass = runs |> List.filter (refuses cls) |> List.length

        {
            Class = cls
            WordingVersion = version
            Runs = List.length runs
            RunsWithClass = withClass
            Recurs = withClass >= policy.Threshold
        }

    /// Whether `cls` recurs under `version`.
    let recurs policy runs version cls =
        (standing policy runs version cls).Recurs

    /// Each class refused in the window under `version`, as it stands.
    let standings (policy: NarrativeLearningPolicy) (runs: NarrativeOffenceRun list) (version: int) =
        window policy version runs
        |> List.collect (fun r -> r.Offences |> List.map _.Class)
        |> List.distinct
        |> List.sortBy rank
        |> List.map (standing policy runs version)

    /// The classes due a proposal under `version`: they recur, no proposal
    /// of the class is open, and none was decided under this version.
    let due (policy: NarrativeLearningPolicy) (version: int) (learning: NarrativeLearning) =
        standings policy learning.Runs version
        |> List.filter _.Recurs
        |> List.map _.Class
        |> List.filter (fun cls ->
            not (
                learning.Proposals
                |> List.exists (fun p ->
                    p.Class = cls
                    && (p.State = NarrativeProposalState.Open || p.WordingVersion = version))
            ))

    /// Add a run: keep the newest `MaxRunsKept`, and lapse every open
    /// proposal drafted under a wording no longer in force.
    let record (policy: NarrativeLearningPolicy) (run: NarrativeOffenceRun) (learning: NarrativeLearning) = {
        Runs =
            learning.Runs @ [ run ]
            |> List.rev
            |> List.truncate policy.MaxRunsKept
            |> List.rev
        Proposals =
            learning.Proposals
            |> List.map (fun p ->
                if p.State = NarrativeProposalState.Open && p.WordingVersion <> run.WordingVersion then
                    {
                        p with
                            State = NarrativeProposalState.Lapsed
                    }
                else
                    p)
    }

    /// The refused claims of `cls` under `version` a proposal is drafted
    /// from, newest first.
    let evidence (policy: NarrativeLearningPolicy) (version: int) (cls: NarrativeOffenceClass) learning =
        window policy version learning.Runs
        |> List.collect (fun r -> r.Offences |> List.filter (fun o -> o.Class = cls) |> List.map _.Claim)
        |> List.distinct
        |> List.truncate policy.MaxEvidence

    /// The sentences approved for `cls` before, oldest first.
    let earlier (cls: NarrativeOffenceClass) (learning: NarrativeLearning) =
        learning.Proposals
        |> List.filter (fun p ->
            p.Class = cls
            && (match p.State with
                | NarrativeProposalState.Approved _ -> true
                | _ -> false))
        |> List.map _.Sentence

    /// Add `proposal` when its class is still due under its version;
    /// otherwise leave the learning as it is (`None`).
    let propose policy (proposal: NarrativeWordingProposal) (learning: NarrativeLearning) =
        if due policy proposal.WordingVersion learning |> List.contains proposal.Class then
            Some {
                learning with
                    Proposals = learning.Proposals @ [ proposal ]
            }
        else
            None

    /// Decide the OPEN proposal `id`.
    let decide
        (id: string)
        (state: NarrativeProposalState)
        (by: string)
        (at: DateTime)
        (learning: NarrativeLearning)
        : Result<NarrativeLearning * NarrativeWordingProposal, string> =
        match learning.Proposals |> List.tryFind (fun p -> p.Id = id) with
        | None -> Error(sprintf "No proposal '%s' exists for this report." id)
        | Some p when p.State <> NarrativeProposalState.Open ->
            Error(sprintf "Proposal '%s' was already decided (%A)." id p.State)
        | Some p ->
            let decided = {
                p with
                    State = state
                    DecidedBy = Some by
                    DecidedAt = Some at
            }

            Ok(
                {
                    learning with
                        Proposals = learning.Proposals |> List.map (fun q -> if q.Id = id then decided else q)
                },
                decided
            )

    /// The instruction with `sentence` appended.
    let appendSentence (prompt: string) (sentence: string) : string =
        let prompt = if isNull prompt then "" else prompt.TrimEnd()

        if prompt = "" then sentence else prompt + "\n" + sentence

    /// The admin's page: open proposals first, then the rest newest first,
    /// each with the instruction it would produce and, once approved, how
    /// its class has fared since; and each class's standing under the
    /// wording in force.
    let view
        (policy: NarrativeLearningPolicy)
        (runKey: string)
        (promptInForce: string)
        (versionInForce: int)
        (learning: NarrativeLearning)
        : NarrativeLearningView =
        let open', decided =
            learning.Proposals
            |> List.rev
            |> List.partition (fun p -> p.State = NarrativeProposalState.Open)

        {
            RunKey = runKey
            Proposals =
                open' @ decided
                |> List.map (fun p -> {
                    Proposal = p
                    ResultingPrompt = appendSentence promptInForce p.Sentence
                    FollowThrough =
                        match p.State with
                        | NarrativeProposalState.Approved version ->
                            Some(standing policy learning.Runs version p.Class)
                        | _ -> None
                })
            Standing = standings policy learning.Runs versionInForce
            Threshold = policy.Threshold
            Window = policy.Window
        }

// ─── The store ───────────────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private listOrEmpty (xs: 'a list) = if isNull (box xs) then [] else xs

/// One narrative's learning per scope, one blob in the scope's container,
/// written through `BlobMapStore`'s guarded read-modify-write.
type NarrativeLearningStore(storage: IBlobStorage, logger: ILogger) =
    let codec: BlobCodec<NarrativeLearning> = {
        Encode = fun learning -> Encoding.UTF8.GetBytes(JsonSerializer.Serialize(learning, jsonOptions))
        Decode =
            fun bytes ->
                try
                    let learning =
                        JsonSerializer.Deserialize<NarrativeLearning>(Encoding.UTF8.GetString bytes, jsonOptions)

                    if isNull (box learning) then
                        Error "the narrative's learning is empty"
                    else
                        Ok {
                            Runs =
                                listOrEmpty learning.Runs
                                |> List.map (fun r -> {
                                    r with
                                        Offences = listOrEmpty r.Offences
                                })
                            Proposals =
                                listOrEmpty learning.Proposals
                                |> List.map (fun p -> {
                                    p with
                                        Evidence = listOrEmpty p.Evidence
                                        Earlier = listOrEmpty p.Earlier
                                })
                        }
                with ex ->
                    Error ex.Message
    }

    let docs = BlobMapStore<NarrativeLearning>(storage, codec, logger)

    static member BlobName(runKey: string) =
        "narrative-learning/" + Uri.EscapeDataString runKey

    /// The learning so far; `empty` when none was ever recorded.
    member _.Read(container: string, runKey: string) : Async<Result<NarrativeLearning, string>> = async {
        match! docs.Read(container, NarrativeLearningStore.BlobName runKey) with
        | Ok None -> return Ok NarrativeLearning.empty
        | Ok(Some learning) -> return Ok learning
        | Error error -> return Error(BlobMapStoreError.describe error)
    }

    /// Change the learning by `change`, which may refuse (`Error`) or find
    /// nothing to write (`Ok(None, _)`). `change` may run more than once: a
    /// lost race replays it over the fresh read.
    member _.Update
        (container: string, runKey: string, change: NarrativeLearning -> Result<NarrativeLearning option * 'R, string>)
        : Async<Result<'R, string>> =
        async {
            let! result =
                docs.Update(
                    container,
                    NarrativeLearningStore.BlobName runKey,
                    fun stored ->
                        match change (stored |> Option.defaultValue NarrativeLearning.empty) with
                        | Error refusal -> BlobUpdate.Keep(Error refusal)
                        | Ok(None, result) -> BlobUpdate.Keep(Ok result)
                        | Ok(Some learning, result) -> BlobUpdate.Write(learning, Ok result)
                )

            return
                match result with
                | Ok outcome -> outcome
                | Error error -> Error(BlobMapStoreError.describe error)
        }

    /// Decide the open proposal `id`.
    member this.Decide(container, runKey, id, state, by, at) : Async<Result<NarrativeWordingProposal, string>> =
        this.Update(
            container,
            runKey,
            fun learning ->
                NarrativeLearning.decide id state by at learning
                |> Result.map (fun (learning, decided) -> Some learning, decided)
        )

    /// The store over `storage`; `None` when no blob storage is composed.
    static member Over(storage: IBlobStorage option, logger: ILogger option) : NarrativeLearningStore option =
        storage
        |> Option.map (fun storage ->
            let logger =
                logger
                |> Option.defaultValue
                    { new ILogger with
                        member _.Debug _ = ()
                        member _.Info _ = ()
                        member _.Warn _ = ()
                        member _.Error(_, _) = ()
                    }

            NarrativeLearningStore(storage, logger))

// ─── Drafting a proposal ─────────────────────────────────────────

module NarrativeProposalDrafting =
    /// The drafting call's instruction.
    let systemPrompt =
        String.concat "\n" [
            "You improve the standing instruction of a report an AI model writes on a schedule."
            "Every figure in the report must be cited from a recorded fact, and a checker refuses any claim that breaks that rule."
            "Answer with ONE sentence to ADD to the instruction so the writer stops making the mistake described."
            "Write no digits and no numbers in words. Answer with the sentence only."
        ]

    /// The drafting call's request.
    let request
        (cls: NarrativeOffenceClass)
        (evidence: string list)
        (instruction: string)
        (earlier: string list)
        : string =
        let lines = [
            "The mistake that keeps recurring: " + NarrativeOffenceClass.describe cls + "."
            ""
            "Claims the checker refused for it:"
            yield! evidence |> List.map (fun claim -> "- " + claim)
            ""
            "The instruction now in force:"
            instruction
            if not (List.isEmpty earlier) then
                ""
                "Sentences added before for this mistake, which did not stop it (do not repeat them):"
                yield! earlier |> List.map (fun sentence -> "- " + sentence)
        ]

        String.concat "\n" lines

    /// The sentence in a model's answer: its first non-blank line, without
    /// list markers or surrounding quotes; `None` when there is none or it
    /// is longer than `maxLength`.
    let sentenceOf (maxLength: int) (content: string) : string option =
        if String.IsNullOrWhiteSpace content then
            None
        else
            content.Split('\n')
            |> Array.map _.Trim()
            |> Array.tryFind (fun line -> line <> "")
            |> Option.map (fun line -> line.TrimStart('-', '*', ' ').Trim('"', '“', '”', ' '))
            |> Option.filter (fun sentence -> sentence <> "" && sentence.Length <= maxLength)

    /// Whether the grounding gate refuses `sentence` as prose — whether it
    /// states a figure.
    let statesFigure
        (gate: INarrativeGroundingGate)
        (scope: ResolvedScope)
        (definition: GroundedNarrativeDefinition)
        (sentence: string)
        : Async<bool> =
        async {
            let document: NarrativeDocument = {
                Title = "Proposed wording"
                Subtitle = None
                Sections = [
                    {
                        Id = "proposal"
                        Heading = "Proposal"
                        Subheading = None
                        Elements = [ Paragraph [ Text sentence ] ]
                    }
                ]
                Provenance = None
                Lang = None
                CanonicalUrl = None
            }

            match! gate.Check(scope, definition.Principal, definition.Surface, document) with
            | Grounded _ -> return false
            | Ungrounded _ -> return true
        }

// ─── The run, learning ───────────────────────────────────────────

/// What the learning run reads, resolved once by its registration factory
/// (`NarrativeOffenceLearning.compose`) and handed in as values.
type internal NarrativeLearningServices = {
    Logger: ILogger option
    Events: IEventStore option
    Storage: IBlobStorage option
    Registry: GroundedNarrativeRegistry option
    Gate: INarrativeGroundingGate option
    Provider: IAIProvider option
    ProviderFactory: IAIProviderFactory option
}

/// A service the container answered with, typed; `None` when it answered
/// nothing (or something else).
let private typed<'T> (service: obj) : 'T option =
    match service with
    | :? 'T as typedService -> Some typedService
    | _ -> None

/// A grounded run that records what the gate refused after every run and
/// drafts a proposal for a class that recurs. The run's outcome is the inner
/// run's, untouched: a failure to learn is logged, never raised.
type internal NarrativeOffenceLearningRun
    (inner: IGroundedNarrativeRun, services: NarrativeLearningServices, policy: NarrativeLearningPolicy) =
    let logger = services.Logger

    let warn (message: string) =
        logger |> Option.iter (fun l -> l.Warn message)

    let roundRowOf (payload: string) : GroundedNarrativeRepairRow option =
        try
            let row =
                JsonSerializer.Deserialize<GroundedNarrativeRepairRow>(payload, jsonOptions)

            if isNull (box row) then None else Some row
        with _ ->
            None

    /// The run that just ended, from the trail: its wording version, its
    /// outcome and every claim its repair rounds were asked to repair or
    /// left. `None` when the trail holds no outcome row for it.
    let fromTrail
        (events: IEventStore)
        (request: GroundedNarrativeRequest)
        (scopeId: string)
        (started: DateTime)
        (narrativeId: string option)
        =
        async {
            let! rows = events.ReadBySource(scopeId, GroundedNarrativeEvents.SourceModule)

            let isOutcome (row: ModuleEvent) =
                row.EventType = GroundedNarrativeEvents.PublishedType
                || row.EventType = GroundedNarrativeEvents.RefusedType
                || row.EventType = GroundedNarrativeEvents.FailedType

            let mine =
                rows
                |> List.sortBy _.OccurredAt
                |> List.choose (fun row ->
                    if isOutcome row then
                        GroundedNarrativeAuditRow.tryParse row.Payload
                        |> Option.filter (fun a -> a.RunKey = request.RunKey && a.Trigger = request.Trigger)
                        |> Option.map (fun a -> row, Choice1Of2 a)
                    elif row.EventType = GroundedNarrativeEvents.RepairRoundType then
                        roundRowOf row.Payload
                        |> Option.filter (fun r -> r.RunKey = request.RunKey && r.Trigger = request.Trigger)
                        |> Option.map (fun r -> row, Choice2Of2 r)
                    else
                        None)
                |> Array.ofList

            let target =
                mine
                |> Array.tryFindIndexBack (fun (row, entry) ->
                    match entry with
                    | Choice1Of2 a ->
                        row.OccurredAt >= started.AddMinutes -1.0
                        && (narrativeId.IsNone || a.NarrativeId = narrativeId)
                    | Choice2Of2 _ -> false)

            return
                target
                |> Option.map (fun index ->
                    let previous =
                        mine.[0 .. index - 1]
                        |> Array.tryFindIndexBack (fun (_, entry) ->
                            match entry with
                            | Choice1Of2 _ -> true
                            | Choice2Of2 _ -> false)
                        |> Option.defaultValue -1

                    let audit =
                        match snd mine.[index] with
                        | Choice1Of2 a -> a
                        | Choice2Of2 _ -> invalidOp "an outcome index names a round row"

                    let roundLines =
                        mine.[previous + 1 .. index - 1]
                        |> Array.toList
                        |> List.collect (fun (_, entry) ->
                            match entry with
                            | Choice2Of2 r -> listOrEmpty r.OffencesIn @ listOrEmpty r.OffencesOut
                            | Choice1Of2 _ -> [])

                    audit, roundLines, (fst mine.[index]).OccurredAt)
        }

    let resolveProvider (request: GroundedNarrativeRequest) (definition: GroundedNarrativeDefinition) scope = async {
        match services.Provider with
        | Some provider -> return Some provider
        | None ->
            match services.ProviderFactory with
            | None -> return None
            | Some factory ->
                let access =
                    request.Access
                    |> Option.orElse (AccessContext.forConfigScope definition.Principal scope)
                    |> Option.defaultWith (fun () ->
                        AccessContext.unrestricted (AuthenticatedUser definition.Principal))

                match! factory.Resolve access with
                | Ok provider -> return Some provider
                | Error err ->
                    warn (
                        sprintf
                            "narrative learning: no provider to draft a proposal for '%s': %s"
                            request.RunKey
                            (ProviderResolutionError.toMessage err)
                    )

                    return None
    }

    /// Draft, check and save one proposal for `cls`.
    let draft
        (request: GroundedNarrativeRequest)
        (scope: StorageScope)
        (store: NarrativeLearningStore)
        (learning: NarrativeLearning)
        (version: int)
        (cls: NarrativeOffenceClass)
        =
        async {
            match services.Registry, services.Gate with
            | Some registry, Some gate ->
                match registry.TryResolve request.RunKey with
                | None -> ()
                | Some declared ->
                    let! wording =
                        NarrativeWording.resolve (NarrativeWording.storeOver services.Storage logger) scope declared

                    // The wording moved since the run: the evidence no longer
                    // describes it, and the next run counts afresh.
                    if wording.Version = version then
                        match! resolveProvider request wording.Definition scope with
                        | None -> ()
                        | Some provider ->
                            let evidence = NarrativeLearning.evidence policy version cls learning
                            let earlier = NarrativeLearning.earlier cls learning

                            let input =
                                ModelInput.ofSystemPrompt
                                    "NarrativeOffenceLearning"
                                    (Some NarrativeProposalDrafting.systemPrompt)
                                    [
                                        AIProviderMessage.text
                                            "user"
                                            (NarrativeProposalDrafting.request
                                                cls
                                                evidence
                                                wording.Definition.Prompt
                                                earlier)
                                    ]

                            match! provider.SendMessage(input, [], None, RetryPolicy.defaults) with
                            | Error err -> warn (sprintf "narrative learning: drafting a proposal failed: %A" err)
                            | Ok response ->
                                match
                                    NarrativeProposalDrafting.sentenceOf policy.MaxSentenceLength response.Content
                                with
                                | None -> warn "narrative learning: the drafted proposal held no usable sentence"
                                | Some sentence ->
                                    match!
                                        NarrativeProposalDrafting.statesFigure
                                            gate
                                            request.Scope
                                            wording.Definition
                                            sentence
                                    with
                                    | true ->
                                        warn "narrative learning: a drafted proposal stated a figure and was discarded"
                                    | false ->
                                        let proposal = {
                                            Id = Guid.NewGuid().ToString "N"
                                            Class = cls
                                            Sentence = sentence
                                            WordingVersion = version
                                            Evidence = evidence
                                            Earlier = earlier
                                            ProposedAt = DateTime.UtcNow
                                            State = NarrativeProposalState.Open
                                            DecidedBy = None
                                            DecidedAt = None
                                        }

                                        let! saved =
                                            store.Update(
                                                scope.Container,
                                                request.RunKey,
                                                fun current -> Ok(NarrativeLearning.propose policy proposal current, ())
                                            )

                                        match saved with
                                        | Error reason ->
                                            warn ("narrative learning: the proposal was not saved: " + reason)
                                        | Ok() -> ()
            | _ -> ()
        }

    let learn (request: GroundedNarrativeRequest) (started: DateTime) (outcome: GroundedNarrativeOutcome) = async {
        let final, narrativeId =
            match outcome with
            | GroundedNarrativePublished(narrativeId, _, _, _, _, repair) ->
                Some repair.Withheld, Some(string narrativeId)
            | GroundedNarrativeRefused offences -> Some offences, None
            | _ -> None, None

        match final, request.Scope.Storage, services.Events, NarrativeLearningStore.Over(services.Storage, logger) with
        | Some final, Some scope, Some events, Some store ->
            match! fromTrail events request scope.ScopeId started narrativeId with
            | None -> warn (sprintf "narrative learning: no outcome row on the trail for '%s'" request.RunKey)
            | Some(audit, roundLines, ranAt) ->
                let run = {
                    RanAt = ranAt
                    WordingVersion = audit.PromptVersion
                    Outcome = audit.Outcome
                    Offences = NarrativeOffenceClassifier.runOffences final roundLines
                }

                let! recorded =
                    store.Update(
                        scope.Container,
                        request.RunKey,
                        fun learning ->
                            let learning = NarrativeLearning.record policy run learning
                            Ok(Some learning, learning)
                    )

                match recorded with
                | Error reason -> warn ("narrative learning: the run was not recorded: " + reason)
                | Ok learning ->
                    for cls in NarrativeLearning.due policy run.WordingVersion learning do
                        do! draft request scope store learning run.WordingVersion cls
        | _ -> ()
    }

    interface IGroundedNarrativeRun with
        member _.Run request = async {
            let started = DateTime.UtcNow
            let! outcome = inner.Run request

            try
                do! learn request started outcome
            with ex ->
                warn ("narrative learning failed: " + ex.Message)

            return outcome
        }

/// Composition.
module NarrativeOffenceLearning =
    /// Compose the learning loop onto an app that composed grounded
    /// narratives (`GroundedNarratives.compose`), after that call: the
    /// composed run is wrapped so every run is recorded, and `policy` is
    /// registered for the report admin's proposal panel. Pair it with
    /// `NarrativePromptHandler.withPromptAdmin`, which is where proposals
    /// are approved. A deployment that does not call this records nothing
    /// and proposes nothing.
    /// **Adds** its registrations through `ServerApp.addServiceConfig`.
    let compose (policy: NarrativeLearningPolicy) (app: ServerApp) : ServerApp =
        let register (s: IServiceCollection) =
            match s |> Seq.tryFindBack (fun d -> d.ServiceType = typeof<IGroundedNarrativeRun>) with
            | None ->
                invalidOp
                    "NarrativeOffenceLearning.compose found no grounded narrative run: compose GroundedNarratives.compose first"
            | Some descriptor ->
                let innerOf (sp: IServiceProvider) : IGroundedNarrativeRun =
                    if not (isNull descriptor.ImplementationInstance) then
                        descriptor.ImplementationInstance :?> IGroundedNarrativeRun
                    elif not (isNull descriptor.ImplementationFactory) then
                        descriptor.ImplementationFactory.Invoke sp :?> IGroundedNarrativeRun
                    else
                        ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType) :?> IGroundedNarrativeRun

                s.Remove descriptor |> ignore
                s.AddSingleton<NarrativeLearningPolicy>(policy) |> ignore

                s.AddSingleton<IGroundedNarrativeRun>(
                    Func<IServiceProvider, IGroundedNarrativeRun>(fun sp ->
                        let services = {
                            Logger = typed<ILogger> (sp.GetService typeof<ILogger>)
                            Events = typed<IEventStore> (sp.GetService typeof<IEventStore>)
                            Storage = typed<IBlobStorage> (sp.GetService typeof<IBlobStorage>)
                            Registry =
                                typed<GroundedNarrativeRegistry> (sp.GetService typeof<GroundedNarrativeRegistry>)
                            Gate = typed<INarrativeGroundingGate> (sp.GetService typeof<INarrativeGroundingGate>)
                            Provider = typed<IAIProvider> (sp.GetService typeof<IAIProvider>)
                            ProviderFactory = typed<IAIProviderFactory> (sp.GetService typeof<IAIProviderFactory>)
                        }

                        NarrativeOffenceLearningRun(innerOf sp, services, policy) :> IGroundedNarrativeRun)
                )
                |> ignore

                s

        ServerApp.addServiceConfig register app