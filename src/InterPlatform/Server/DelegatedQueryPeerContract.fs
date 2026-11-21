// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.InterPlatform.PeerAuditEvents
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── The delegated-query profile ─────────────────────────────────────
//
// One deployment holds the connection to its data; another needs answers
// from it and holds no credential for it. The holder (the custodian)
// serves this contract and the other (the requester) calls it, in the
// shape the federation wire specification's delegated-query profile
// states. Three invocations make the profile: `Describe` answers the
// schema the calling peer may build a query against, `Submit` takes a
// query and its bound parameters and answers the result or a handle, and
// `Resolve` answers a handle's state.
//
// ── Forge carries the query; it never reads it ──
//
// The query, the described schema, the bound parameters and the answer
// cross as canonical JSON this file carries, hashes, audits and bounds,
// and never parses. What gives them meaning is `DelegatedQueryHandler`, a
// record of functions a deployment registers. So the contract serves any
// query representation a deployment composes a handler for, and names
// none.
//
// ── What the substrate applies, outside the handler ──
//
// The handler answers an opaque result with a DECLARED class, aggregate or
// row-level, and the smallest cohort any figure in it rests on. Everything
// else is applied here, where the handler cannot opt out of it:
//
//   * **Scope and principal from the binding.** The scope a call addresses
//     and the caller the handler is told about come from this deployment's
//     binding for the VALIDATED calling peer. Nothing in the request names
//     a principal, and an asserted scope that disagrees is refused.
//   * **The authority level against the declared class.** An aggregate
//     needs `AggregatesOnly`, a row-level answer `Full`. An answer whose
//     class exceeds the peer's grant is refused before any byte of it is
//     written, as a ceiling or as a narrowing.
//   * **The clean-room gate over every answer.** `governed` is the only
//     construction route, and its result is a `GatedContractRegistration`.
//     The gate's minimum cohort binds on the declared cohort, its budget is
//     charged per answer and returned for a frame that carried no data,
//     and a row-level answer passes only when the template's own release
//     terms, which both parties approve, release rows.
//   * **The audit record.** Each call records the query's hash and the
//     answer's declared class, never the query or the answer.
//
// A handler that declares `Aggregate` and answers rows is the handler's
// defect. The profile's remedy is `DelegatedQueryDeps.ClassCheck`: a
// deployment composes a check over its own answer representation, and an
// answer it disagrees with is refused rather than released.
//
// ── The queued leg never reaches the poll route ──
//
// A queued `Submit` schedules a job whose result is parked under a scope
// of this profile's own (`DelegatedQueryProfile.ResultScope`), not the
// scope the transport's `GET …/jobs/{jobId}` route reads. That route
// therefore answers `Pending` for every delegated-query handle, and the
// only way to collect a result is `Resolve`, a contract call, which runs
// the gate and the authority check again against the grant as it stands.
//
// ── Cost when unused (GP 13) ──
//
// Nothing here is composed by `PeerCompose.withPeer`. A deployment that
// never calls `governed` constructs none of these types and mounts no
// route.

// ─── Wire shapes ─────────────────────────────────────────────────────

/// The class an answer declares. The gate and the authority check read it;
/// nothing in forge checks it against the answer, which is opaque here.
[<RequireQualifiedAccess>]
type DelegatedQueryClass =
    /// Figures computed over cohorts of records. Needs `AggregatesOnly`.
    | Aggregate
    /// Individual records. Needs `Full`.
    | RowLevel

/// A query and the parameters bound to it, as the requester sent them.
/// Both are embedded canonical JSON in the requester's own representation.
type DelegatedQuerySubmission = {
    /// The query. Opaque: forge hashes and carries it.
    Query: string
    /// The bound parameters, by name, each value an embedded document.
    /// Keys are sorted ordinally on the wire.
    Parameters: Map<string, string>
}

/// What a handler answers, and what an `Answered` frame carries.
type DelegatedQueryResult = {
    /// The declared class.
    Class: DelegatedQueryClass
    /// The smallest number of records any figure in the answer rests on —
    /// what the clean-room gate's minimum cohort binds on. A row-level
    /// answer's figures each rest on one record, so it declares `1`.
    Cohort: int
    /// The answer. Opaque embedded canonical JSON.
    Answer: string
}

/// How `Submit` answers on this deployment.
[<RequireQualifiedAccess>]
type DelegatedQueryLeg =
    /// `Submit` answers the result directly.
    | Immediate
    /// `Submit` answers a handle, and `Resolve` collects the result.
    | Queued

/// What `Describe` answers.
type DelegatedQueryDescription = {
    /// The schema the calling peer may build a query against. Opaque
    /// embedded canonical JSON, the handler's per-peer projection.
    Schema: string
    /// The answer classes this peer may receive here: those its effective
    /// level admits and the gate releases. Ordered narrowest first.
    Classes: DelegatedQueryClass list
    /// How `Submit` answers.
    Leg: DelegatedQueryLeg
}

/// The body of a `Resolve`.
type DelegatedQueryHandle = {
    /// The handle a queued `Submit` answered.
    Handle: string
}

/// Why a delegated-query request was refused.
[<RequireQualifiedAccess>]
type DelegatedQueryRefusal =
    /// The envelope declares a profile version beyond the reader's.
    | ProfileVersionUnsupported of requested: int * supported: int
    /// The document is not a request envelope, or its body is not the
    /// operation's document.
    | RequestUnreadable of reason: string
    /// The envelope names no operation of this profile, or one other than
    /// the method it was sent to.
    | UnknownOperation of operation: string
    /// The envelope asserted a scope other than the peer binding's.
    | ScopeWideningRefused of asserted: string
    /// The validated caller has no binding here.
    | PeerUnbound of peerId: string
    /// The answer's declared class requires a level above the peer's ceiling.
    | AuthorityLevelExceeded of answerClass: string * required: string * declared: string
    /// The ceiling admits the class; a narrowing beneath it does not.
    | AuthorityNarrowingRefused of answerClass: string * required: string * effective: string * narrowedBy: string
    /// The deployment's composed class check disagreed with the class the
    /// answer declared. Names the declared class and nothing else.
    | ClassMisdeclared of answerClass: string
    /// The handler declined the query, with the reason it gave.
    | HandlerRefused of reason: string
    /// The queued leg needs a substrate this deployment does not compose.
    | SubstrateDisabled of substrate: string

/// Every answer on the contract.
[<RequireQualifiedAccess>]
type DelegatedQueryAnswer =
    /// `Describe`'s answer.
    | Described of DelegatedQueryDescription
    /// A result, from `Submit` on the immediate leg or `Resolve`.
    | Answered of DelegatedQueryResult
    /// A queued `Submit`'s handle.
    | Scheduled of handle: string
    /// `Resolve` of a handle whose result is not ready, or that this peer
    /// does not hold: the two read alike, so a handle discloses nothing.
    | Pending
    /// A refusal.
    | Refused of DelegatedQueryRefusal

/// The request envelope every call carries as its one positional argument.
type DelegatedQueryRequest = {
    /// The profile version that defined `Operation`.
    ProfileVersion: int
    /// The invocation: `Describe`, `Submit` or `Resolve`.
    Operation: string
    /// The scope the caller believes it addresses. Checked, never routed on.
    AssertedScope: string option
    /// The operation's document, embedded.
    Body: string
}

/// The profile's constants.
[<RequireQualifiedAccess>]
module DelegatedQueryProfile =

    /// The profile version this reader reads.
    [<Literal>]
    let Version = 1

    /// The contract id, at `contractVersion`.
    [<Literal>]
    let ContractId = "fuaran.delegated-query"

    /// The contract's wire version.
    let contractVersion: ContractVersion = { Major = 1; Minor = 0 }

    /// The invocation answering the calling peer's schema.
    [<Literal>]
    let DescribeOperation = "Describe"

    /// The invocation taking a query and its parameters.
    [<Literal>]
    let SubmitOperation = "Submit"

    /// The invocation answering a handle's state.
    [<Literal>]
    let ResolveOperation = "Resolve"

    /// The three invocations.
    let operations: Set<string> =
        Set.ofList [ DescribeOperation; SubmitOperation; ResolveOperation ]

    /// The release declaration, in a template's `Release.Declarations`
    /// against `Submit` and `Resolve`, that releases row-level answers.
    [<Literal>]
    let RowRelease = "row-level"

    /// The result-store scope queued results park under: not the scope the
    /// transport's poll route reads, so only `Resolve` collects them.
    [<Literal>]
    let ResultScope = "_platform.peer.delegated-query"

    /// The class's wire label.
    let classLabel: DelegatedQueryClass -> string =
        function
        | DelegatedQueryClass.Aggregate -> "Aggregate"
        | DelegatedQueryClass.RowLevel -> "RowLevel"

    /// The authority level an answer of `answerClass` needs.
    let requiredAuthority: DelegatedQueryClass -> PeerDataVisibilityLevel =
        function
        | DelegatedQueryClass.Aggregate -> PeerDataVisibilityLevel.AggregatesOnly
        | DelegatedQueryClass.RowLevel -> PeerDataVisibilityLevel.Full

    /// A content address over a document's UTF-8 bytes: `sha256:` + hex.
    let hash (document: string) : string =
        let bytes = Encoding.UTF8.GetBytes(if isNull document then "" else document)

        "sha256:"
        + (SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant())

    /// A template over the three invocations with `floor` and no release
    /// declarations: aggregates only.
    let template (templateId: string) (floor: PrivacyGate) : CleanRoomTemplate = {
        TemplateId = templateId
        AllowedMethods = operations
        Floor = floor
        Release = ReleaseTerms.none
    }

    /// The same template, releasing row-level answers from `Submit` and
    /// `Resolve`. A different template content, so a different approval.
    let withRowRelease (template: CleanRoomTemplate) : CleanRoomTemplate = {
        template with
            Release = {
                template.Release with
                    Declarations =
                        template.Release.Declarations
                        |> Map.add SubmitOperation RowRelease
                        |> Map.add ResolveOperation RowRelease
            }
    }

    /// Whether `template` releases row-level answers from `operation`.
    let releasesRows (template: CleanRoomTemplate) (operation: string) : bool =
        Map.tryFind operation template.Release.Declarations = Some RowRelease

/// The refusal classes the conformance corpus names.
[<RequireQualifiedAccess>]
module DelegatedQueryRefusal =

    /// The stable refusal class a conformance vector names.
    let className: DelegatedQueryRefusal -> string =
        function
        | DelegatedQueryRefusal.ProfileVersionUnsupported _ -> "delegated-query-profile-version-unsupported"
        | DelegatedQueryRefusal.RequestUnreadable _ -> "delegated-query-request-unreadable"
        | DelegatedQueryRefusal.UnknownOperation _ -> "delegated-query-unknown-operation"
        | DelegatedQueryRefusal.ScopeWideningRefused _ -> "delegated-query-scope-widening"
        | DelegatedQueryRefusal.PeerUnbound _ -> "delegated-query-peer-unbound"
        | DelegatedQueryRefusal.AuthorityLevelExceeded _ -> "delegated-query-authority-level-exceeded"
        | DelegatedQueryRefusal.AuthorityNarrowingRefused _ -> "delegated-query-authority-narrowed"
        | DelegatedQueryRefusal.ClassMisdeclared _ -> "delegated-query-class-misdeclared"
        | DelegatedQueryRefusal.HandlerRefused _ -> "delegated-query-handler-refused"
        | DelegatedQueryRefusal.SubstrateDisabled _ -> "delegated-query-substrate-disabled"

// ─── The handler seam and the deployment's declarations ──────────────

/// The caller a handler is told about. Built from the binding for the
/// validated calling peer, never from the request.
type DelegatedQueryCaller = {
    /// The validated calling peer.
    PeerId: string
    /// The scope this deployment's binding resolves the peer's calls under.
    ScopeId: string
    /// The peer's effective authority level after any narrowing.
    Authority: PeerDataVisibilityLevel
}

/// What a deployment registers to give the profile meaning. Both functions
/// receive the validated caller; neither is told anything a request body
/// asserted. An `Error` is a refusal the requester reads as
/// `HandlerRefused` with that reason, so it says nothing the handler would
/// not tell the caller.
type DelegatedQueryHandler = {
    /// The schema this caller may build a query against, as canonical JSON.
    Describe: DelegatedQueryCaller -> Async<Result<string, string>>
    /// Evaluate a query with its bound parameters, answering an opaque
    /// result with its declared class and cohort.
    Evaluate: DelegatedQueryCaller -> DelegatedQuerySubmission -> Async<Result<DelegatedQueryResult, string>>
}

/// What one requester peer is bound to on this deployment.
type DelegatedQueryBinding = {
    /// The peer the binding is for.
    PeerId: string
    /// The scope its calls resolve under. Decided here, never on the wire.
    ScopeId: string
    /// What it may see: the ceiling and any narrowing beneath it.
    Visibility: PeerVisibilityBinding
}

/// The profile's record of one call: the query's hash and the answer's
/// declared class, never the query, its parameters or the answer.
type DelegatedQueryAuditPayload = {
    /// The contract id, `fuaran.delegated-query`.
    ContractId: string
    /// The invocation.
    Operation: string
    /// The validated calling peer.
    CallerPeerId: string
    /// The cascade correlation id the receiver derived.
    RootRequestId: string
    /// The query's content address, when the call carried one.
    QueryHash: string option
    /// The content address of the bound parameters' canonical encoding.
    ParametersHash: string option
    /// The class the answer declared, when the call produced an answer.
    AnswerClass: string option
    /// `described`, `answered`, `scheduled`, `pending`, or the refusal class.
    Outcome: string
    /// When the call resolved.
    OccurredAt: DateTimeOffset
}

/// What the contract is built from.
type DelegatedQueryDeps = {
    /// The binding for a validated peer id, or `None` for a peer this
    /// deployment does not serve.
    ResolveBinding: string -> Async<DelegatedQueryBinding option>
    /// The handler the deployment registers.
    Handler: DelegatedQueryHandler
    /// How `Submit` answers.
    Leg: DelegatedQueryLeg
    /// A check of an answer's declared class against the answer, in the
    /// deployment's own representation. `None` takes the handler's word.
    ClassCheck: (DelegatedQueryCaller -> DelegatedQueryResult -> Result<unit, string>) option
    /// Where each call's record goes (`DelegatedQueryPeerContract.noAudit`
    /// to record nothing).
    Audit: DelegatedQueryAuditPayload -> Async<unit>
}

/// The clean-room gate every answer crosses.
type DelegatedQueryGate = {
    /// The broker the floor is enforced through.
    Broker: ICleanRoomBroker
    /// The template: the invocations it answers, the minimum cohort, and
    /// whether rows are released.
    Template: CleanRoomTemplate
    /// The bilateral approval check, or `None`.
    Approval: CleanRoomApprovalCheck option
    /// The per-counterparty budget each answer is charged to, or `None`.
    Budget: PrivacyBudgetMeter option
    /// The gate's decision sink (`CleanRoomGate.noAudit` to record nothing).
    Audit: PeerCleanRoomDecisionPayload -> Async<unit>
}

/// Why a gate was refused at composition.
[<RequireQualifiedAccess>]
type DelegatedQueryTemplateRefusal =
    /// The template switches on calibrated noise, which an opaque answer
    /// cannot carry, so every answer would be withheld.
    | NoiseDeclared of templateId: string
    /// A release declaration this profile does not read.
    | UnreadableDeclaration of templateId: string * operation: string * declaration: string

/// Accounts of a composition refusal.
[<RequireQualifiedAccess>]
module DelegatedQueryTemplateRefusal =

    /// A one-line account of a refusal, for a composition error.
    let describe: DelegatedQueryTemplateRefusal -> string =
        function
        | DelegatedQueryTemplateRefusal.NoiseDeclared id ->
            $"clean-room template '{id}' declares calibrated noise, which a delegated-query answer cannot carry"
        | DelegatedQueryTemplateRefusal.UnreadableDeclaration(id, operation, declaration) ->
            $"clean-room template '{id}' declares '{declaration}' for '{operation}'; the delegated-query profile reads only '{DelegatedQueryProfile.RowRelease}' on '{DelegatedQueryProfile.SubmitOperation}' and '{DelegatedQueryProfile.ResolveOperation}'"

/// A queued result as it is parked: the answer the job released, and the
/// hashes the record at `Resolve` carries.
type DelegatedQueryParked = {
    /// The answer the queued evaluation released.
    Outcome: DelegatedQueryAnswer
    /// The query's content address.
    QueryHash: string
    /// The bound parameters' content address.
    ParametersHash: string
}

/// The custodian's contract and the requester's vocabulary for it.
[<RequireQualifiedAccess>]
module DelegatedQueryPeerContract =

    /// The audit sink a deployment that records nothing composes.
    let noAudit: DelegatedQueryAuditPayload -> Async<unit> =
        fun _ -> async { return () }

    // ── Reading and admitting a request ──────────────────────────────

    let private nonNull (value: 'T) = not (isNull (box value))

    /// Admit a parsed envelope sent to `methodName` under `boundScope`.
    let admit (methodName: string) (boundScope: string) (request: DelegatedQueryRequest) =
        if request.ProfileVersion > DelegatedQueryProfile.Version then
            Error(
                DelegatedQueryRefusal.ProfileVersionUnsupported(request.ProfileVersion, DelegatedQueryProfile.Version)
            )
        elif
            not (Set.contains request.Operation DelegatedQueryProfile.operations)
            || request.Operation <> methodName
        then
            Error(DelegatedQueryRefusal.UnknownOperation request.Operation)
        else
            match request.AssertedScope with
            | Some asserted when asserted <> boundScope -> Error(DelegatedQueryRefusal.ScopeWideningRefused asserted)
            | _ -> Ok request

    /// Read a request envelope and admit it — the reader the profile's
    /// reject vectors are certified against. A document that does not parse
    /// is refused, never partially read.
    let read (methodName: string) (boundScope: string) (json: string) =
        let parsed =
            try
                let request = JsonRpc.deserialize<DelegatedQueryRequest> json

                if nonNull request && nonNull request.Operation && nonNull request.Body then
                    Ok request
                else
                    Error "the document is not a delegated-query request envelope"
            with ex ->
                Error ex.Message

        match parsed with
        | Error reason -> Error(DelegatedQueryRefusal.RequestUnreadable reason)
        | Ok request -> admit methodName boundScope request

    let private readBody<'T> (what: string) (body: string) (valid: 'T -> bool) =
        try
            let value = JsonRpc.deserialize<'T> body

            if nonNull value && valid value then
                Ok value
            else
                Error(DelegatedQueryRefusal.RequestUnreadable $"the body is not a {what}")
        with ex ->
            Error(DelegatedQueryRefusal.RequestUnreadable ex.Message)

    let private readSubmission (body: string) =
        readBody<DelegatedQuerySubmission> "delegated-query submission" body (fun s ->
            nonNull s.Query && nonNull s.Parameters)

    let private readHandle (body: string) =
        readBody<DelegatedQueryHandle> "delegated-query handle" body (fun h -> nonNull h.Handle)
        |> Result.bind (fun h ->
            match Guid.TryParse h.Handle with
            | true, id -> Ok id
            | _ -> Error(DelegatedQueryRefusal.RequestUnreadable "the handle is not one this profile issues"))

    /// Judge an answer's declared class against the peer's grant and the
    /// deployment's class check. What is answered is decided here, before
    /// any byte of the answer is written.
    let release
        (check: (DelegatedQueryCaller -> DelegatedQueryResult -> Result<unit, string>) option)
        (caller: DelegatedQueryCaller)
        (authority: PeerVisibilityResolution)
        (result: DelegatedQueryResult)
        : DelegatedQueryAnswer =
        if not (nonNull result && nonNull result.Answer) then
            DelegatedQueryAnswer.Refused(DelegatedQueryRefusal.HandlerRefused "the handler answered nothing")
        else
            let label = DelegatedQueryProfile.classLabel result.Class
            let required = DelegatedQueryProfile.requiredAuthority result.Class

            if not (PeerDataVisibilityLevel.admits authority.Ceiling required) then
                DelegatedQueryAnswer.Refused(
                    DelegatedQueryRefusal.AuthorityLevelExceeded(
                        label,
                        PeerDataVisibilityLevel.label required,
                        PeerDataVisibilityLevel.label authority.Ceiling
                    )
                )
            elif not (PeerDataVisibilityLevel.admits authority.Effective required) then
                DelegatedQueryAnswer.Refused(
                    DelegatedQueryRefusal.AuthorityNarrowingRefused(
                        label,
                        PeerDataVisibilityLevel.label required,
                        PeerDataVisibilityLevel.label authority.Effective,
                        authority.NarrowedBy
                        |> Option.map PeerVisibilityScope.toString
                        |> Option.defaultValue ""
                    )
                )
            else
                match check |> Option.map (fun c -> c caller result) with
                | Some(Error _) -> DelegatedQueryAnswer.Refused(DelegatedQueryRefusal.ClassMisdeclared label)
                | Some(Ok())
                | None -> DelegatedQueryAnswer.Answered result

    // ── The requester's vocabulary ───────────────────────────────────

    /// A request envelope for `operation` carrying `body` embedded.
    let request (operation: string) (body: string) : DelegatedQueryRequest = {
        ProfileVersion = DelegatedQueryProfile.Version
        Operation = operation
        AssertedScope = None
        Body = body
    }

    /// A `Describe` request.
    let describeRequest () =
        request DelegatedQueryProfile.DescribeOperation "{}"

    /// A `Submit` request.
    let submissionRequest (submission: DelegatedQuerySubmission) =
        request DelegatedQueryProfile.SubmitOperation (JsonRpc.serialize submission)

    /// A `Resolve` request for a handle a queued `Submit` answered.
    let resolveRequest (handle: string) =
        request DelegatedQueryProfile.ResolveOperation (JsonRpc.serialize { Handle = handle })

    /// The positional-argument array a requester sends.
    let arguments (request: DelegatedQueryRequest) : string =
        JsonRpc.serialize [ JsonRpc.serialize request ]

    /// Read an answer document.
    let readAnswer (json: string) : Result<DelegatedQueryAnswer, string> =
        try
            let answer = JsonRpc.deserialize<DelegatedQueryAnswer> json

            if nonNull answer then
                Ok answer
            else
                Error "the document is not a delegated-query answer"
        with ex ->
            Error ex.Message

    // ── The custodian's dispatch ─────────────────────────────────────

    let private soleArgument (argsJson: string) =
        try
            match JsonRpc.deserialize<string list> argsJson with
            | [ single ] -> Ok single
            | other ->
                Error(
                    DelegatedQueryRefusal.RequestUnreadable
                        $"a delegated-query call carries exactly one embedded request argument; this one carried {List.length other}"
                )
        with ex ->
            Error(DelegatedQueryRefusal.RequestUnreadable ex.Message)

    let private outcomeOf: DelegatedQueryAnswer -> string =
        function
        | DelegatedQueryAnswer.Described _ -> "described"
        | DelegatedQueryAnswer.Answered _ -> "answered"
        | DelegatedQueryAnswer.Scheduled _ -> "scheduled"
        | DelegatedQueryAnswer.Pending -> "pending"
        | DelegatedQueryAnswer.Refused refusal -> DelegatedQueryRefusal.className refusal

    /// The query and parameter hashes of a submission.
    let private hashes (submission: DelegatedQuerySubmission) =
        DelegatedQueryProfile.hash submission.Query,
        DelegatedQueryProfile.hash (JsonRpc.serialize submission.Parameters)

    /// Record a call, best-effort: a recording failure never changes an
    /// answer that has been decided.
    let private record
        (deps: DelegatedQueryDeps)
        (operation: string)
        (peerId: string)
        (rootRequestId: string)
        (submitted: (string * string) option)
        (answer: DelegatedQueryAnswer)
        =
        async {
            try
                do!
                    deps.Audit {
                        ContractId = DelegatedQueryProfile.ContractId
                        Operation = operation
                        CallerPeerId = peerId
                        RootRequestId = rootRequestId
                        QueryHash = submitted |> Option.map fst
                        ParametersHash = submitted |> Option.map snd
                        AnswerClass =
                            match answer with
                            | DelegatedQueryAnswer.Answered result ->
                                Some(DelegatedQueryProfile.classLabel result.Class)
                            | _ -> None
                        Outcome = outcomeOf answer
                        OccurredAt = DateTimeOffset.UtcNow
                    }
            with ex when not (ex :? OperationCanceledException) ->
                ()
        }

    /// Resolve the binding and read the envelope: the prologue every
    /// dispatch and every queued job runs, once.
    let private admitted (deps: DelegatedQueryDeps) (peerId: string) (methodName: string) (argsJson: string) = async {
        match! deps.ResolveBinding peerId with
        | None -> return Error(DelegatedQueryRefusal.PeerUnbound peerId)
        | Some binding ->
            let authority = PeerVisibility.resolve binding.Visibility binding.PeerId

            let caller = {
                PeerId = binding.PeerId
                ScopeId = binding.ScopeId
                Authority = authority.Effective
            }

            return
                soleArgument argsJson
                |> Result.bind (read methodName binding.ScopeId)
                |> Result.map (fun request -> caller, authority, request)
    }

    let private evaluate (deps: DelegatedQueryDeps) caller authority submission = async {
        match! deps.Handler.Evaluate caller submission with
        | Error reason -> return DelegatedQueryAnswer.Refused(DelegatedQueryRefusal.HandlerRefused reason)
        | Ok result -> return release deps.ClassCheck caller authority result
    }

    let private schedule (fusion: PeerJobFusion) (context: PeerCallContext) (argsJson: string) = async {
        let payload: PeerJobPayload = {
            OwnerPeerId = context.Peer.PeerId
            ArgsJson = argsJson
            RootRequestId = context.RootRequestId
        }

        let registration: JobRegistration = {
            ScopeId = PeerJob.Scope
            Handler = PeerJob.handlerName DelegatedQueryProfile.ContractId DelegatedQueryProfile.SubmitOperation
            Payload = JsonRpc.serialize payload
            Trigger = Manual
            Idempotency = None
            RetryPolicy = JobRetryPolicy.defaults
            ShardKey = None
            Precision = JobPrecision.Minute
            CreatedBy = PeerJob.SourceModule
            Tags = Map.empty
        }

        match! fusion.Scheduler.Schedule registration with
        | Error _ -> return Error(PeerHandler "failed to schedule the delegated query")
        | Ok(Scheduled jobId | Coalesced jobId) ->
            match! fusion.Scheduler.TriggerOnce(PeerJob.Scope, jobId, UserId PeerJob.SourceModule) with
            | Error refusal ->
                return Error(PeerHandler $"failed to trigger the delegated query: {TriggerError.message refusal}")
            | Ok() -> return Ok(DelegatedQueryAnswer.Scheduled(string jobId))
    }

    /// Collect a parked result, re-judged against the grant as it stands.
    /// A handle another peer holds reads as `Pending`, as does an absent one.
    let private collect
        (deps: DelegatedQueryDeps)
        (fusion: PeerJobFusion)
        (peerId: string)
        caller
        authority
        (jobId: Guid)
        =
        async {
            match! fusion.ResultStore.TryGetResult(DelegatedQueryProfile.ResultScope, jobId) with
            | Some {
                       OwnerPeerId = owner
                       Status = PeerJobStatus.Completed json
                   } when owner = peerId ->
                let parked = JsonRpc.deserialize<DelegatedQueryParked> json

                let answer =
                    match parked.Outcome with
                    | DelegatedQueryAnswer.Answered result -> release deps.ClassCheck caller authority result
                    | other -> other

                return answer, Some(parked.QueryHash, parked.ParametersHash)
            | _ -> return DelegatedQueryAnswer.Pending, None
        }

    let private answer
        (deps: DelegatedQueryDeps)
        (rowsReleased: bool)
        (fusion: PeerJobFusion option)
        (context: PeerCallContext)
        (methodName: string)
        (argsJson: string)
        =
        async {
            let peerId = context.Peer.PeerId

            match! admitted deps peerId methodName argsJson with
            | Error refusal -> return Ok(DelegatedQueryAnswer.Refused refusal), None
            | Ok(caller, authority, request) ->
                match request.Operation with
                | DelegatedQueryProfile.DescribeOperation ->
                    match! deps.Handler.Describe caller with
                    | Error reason ->
                        return Ok(DelegatedQueryAnswer.Refused(DelegatedQueryRefusal.HandlerRefused reason)), None
                    | Ok schema ->
                        let classes =
                            [ DelegatedQueryClass.Aggregate; DelegatedQueryClass.RowLevel ]
                            |> List.filter (fun c ->
                                PeerDataVisibilityLevel.admits
                                    authority.Effective
                                    (DelegatedQueryProfile.requiredAuthority c)
                                && (c = DelegatedQueryClass.Aggregate || rowsReleased))

                        return
                            Ok(
                                DelegatedQueryAnswer.Described {
                                    Schema = schema
                                    Classes = classes
                                    Leg = deps.Leg
                                }
                            ),
                            None
                | DelegatedQueryProfile.SubmitOperation ->
                    match readSubmission request.Body with
                    | Error refusal -> return Ok(DelegatedQueryAnswer.Refused refusal), None
                    | Ok submission ->
                        let submitted = Some(hashes submission)

                        match deps.Leg, fusion with
                        | DelegatedQueryLeg.Immediate, _ ->
                            let! answered = evaluate deps caller authority submission
                            return Ok answered, submitted
                        | DelegatedQueryLeg.Queued, Some f ->
                            let! scheduled = schedule f context argsJson
                            return scheduled, submitted
                        | DelegatedQueryLeg.Queued, None ->
                            return
                                Ok(
                                    DelegatedQueryAnswer.Refused(
                                        DelegatedQueryRefusal.SubstrateDisabled "job scheduler"
                                    )
                                ),
                                submitted
                | _ ->
                    match readHandle request.Body, fusion with
                    | Error refusal, _ -> return Ok(DelegatedQueryAnswer.Refused refusal), None
                    | Ok _, None ->
                        return
                            Ok(DelegatedQueryAnswer.Refused(DelegatedQueryRefusal.SubstrateDisabled "job scheduler")),
                            None
                    | Ok jobId, Some f ->
                        let! collected, submitted = collect deps f peerId caller authority jobId
                        return Ok collected, submitted
        }

    let private registration (deps: DelegatedQueryDeps) (rowsReleased: bool) (fusion: PeerJobFusion option) =
        let dispatch: PeerDispatch =
            fun context methodName argsJson -> async {
                let! outcome, submitted = answer deps rowsReleased fusion context methodName argsJson

                match outcome with
                | Ok answered ->
                    do! record deps methodName context.Peer.PeerId context.RootRequestId submitted answered
                    return Ok(JsonRpc.serialize answered)
                | Error e -> return Error e
            }

        {
            ContractId = DelegatedQueryProfile.ContractId
            Versions = [ DelegatedQueryProfile.contractVersion ]
            Dispatch = dispatch
        }

    /// Runs a queued `Submit` and parks what it released under the
    /// profile's own result scope.
    type private SubmitJobHandler(deps: DelegatedQueryDeps, fusion: PeerJobFusion) =
        interface IJobHandler with
            member _.Execute(ctx: JobContext) = async {
                let envelope =
                    try
                        JsonRpc.deserialize<PeerJobPayload> ctx.Payload
                    with _ -> {
                        OwnerPeerId = ""
                        ArgsJson = ctx.Payload
                        RootRequestId = ""
                    }

                let! admittedCall =
                    admitted deps envelope.OwnerPeerId DelegatedQueryProfile.SubmitOperation envelope.ArgsJson

                let! answered, submitted =
                    match admittedCall with
                    | Error refusal -> async { return DelegatedQueryAnswer.Refused refusal, ("", "") }
                    | Ok(caller, authority, request) ->
                        match readSubmission request.Body with
                        | Error refusal -> async { return DelegatedQueryAnswer.Refused refusal, ("", "") }
                        | Ok submission -> async {
                            let! answered = evaluate deps caller authority submission
                            return answered, hashes submission
                          }

                let parked = {
                    Outcome = answered
                    QueryHash = fst submitted
                    ParametersHash = snd submitted
                }

                do!
                    fusion.ResultStore.SaveResult(
                        DelegatedQueryProfile.ResultScope,
                        ctx.JobId,
                        envelope.OwnerPeerId,
                        PeerJobStatus.Completed(JsonRpc.serialize parked)
                    )

                let rootRequestId =
                    if isNull envelope.RootRequestId then
                        ""
                    else
                        envelope.RootRequestId

                do!
                    record
                        deps
                        DelegatedQueryProfile.SubmitOperation
                        envelope.OwnerPeerId
                        rootRequestId
                        (Some submitted)
                        answered

                return Success
            }

    /// How the gate reads one answer on this contract: an answered result
    /// by its declared class and cohort, every other frame as inert.
    let private gateReading (template: CleanRoomTemplate) (methodName: string) (json: string) : GateReading =
        match readAnswer json with
        | Error reason -> GateReading.Unreadable $"the answer is not a delegated-query answer: {reason}"
        | Ok(DelegatedQueryAnswer.Answered result) ->
            match result.Class with
            | DelegatedQueryClass.Aggregate -> GateReading.DeclaredAggregate result.Cohort
            | DelegatedQueryClass.RowLevel when DelegatedQueryProfile.releasesRows template methodName ->
                GateReading.DeclaredRows
            | DelegatedQueryClass.RowLevel ->
                GateReading.Unreadable
                    $"a row-level answer, which template '{template.TemplateId}' does not release from '{methodName}'"
        | Ok _ -> GateReading.Inert

    /// Refusals of a template at composition, by name. Empty when it composes.
    let check (template: CleanRoomTemplate) : DelegatedQueryTemplateRefusal list =
        let noise =
            match template.Release.Noise with
            | Some _ -> [ DelegatedQueryTemplateRefusal.NoiseDeclared template.TemplateId ]
            | None -> []

        let declarations =
            template.Release.Declarations
            |> Map.toList
            |> List.filter (fun (operation, declaration) ->
                declaration <> DelegatedQueryProfile.RowRelease
                || (operation <> DelegatedQueryProfile.SubmitOperation
                    && operation <> DelegatedQueryProfile.ResolveOperation))
            |> List.map (fun (operation, declaration) ->
                DelegatedQueryTemplateRefusal.UnreadableDeclaration(template.TemplateId, operation, declaration))

        noise @ declarations

    /// The delegated-query contract behind its clean-room gate, paired with
    /// the job handler its queued leg runs, by the name it is scheduled
    /// under. The only construction route: there is no ungated registration
    /// to reach for.
    ///
    /// `fusion` is `None` on a deployment with no job substrate; a queued
    /// leg then refuses with `SubstrateDisabled`.
    let governed
        (gate: DelegatedQueryGate)
        (deps: DelegatedQueryDeps)
        (fusion: PeerJobFusion option)
        : Result<GatedContractRegistration * (string * IJobHandler) list, DelegatedQueryTemplateRefusal list> =
        match check gate.Template with
        | [] ->
            let rowsReleased =
                DelegatedQueryProfile.releasesRows gate.Template DelegatedQueryProfile.SubmitOperation

            Ok(
                CleanRoomGate.wrapReading
                    (gateReading gate.Template)
                    gate.Broker
                    gate.Template
                    gate.Approval
                    gate.Budget
                    None
                    gate.Audit
                    (registration deps rowsReleased fusion),
                match deps.Leg, fusion with
                | DelegatedQueryLeg.Queued, Some f -> [
                    PeerJob.handlerName DelegatedQueryProfile.ContractId DelegatedQueryProfile.SubmitOperation,
                    SubmitJobHandler(deps, f) :> IJobHandler
                  ]
                | _ -> []
            )
        | refusals -> Error refusals

    /// The builder `PeerOptions.withContract` takes, with the template
    /// checked once here rather than at every materialisation.
    let builder
        (gate: DelegatedQueryGate)
        (deps: DelegatedQueryDeps)
        : Result<PeerJobFusion option -> PeerContractHost, DelegatedQueryTemplateRefusal list> =
        match check gate.Template with
        | [] ->
            Ok(fun fusion ->
                match governed gate deps fusion with
                | Ok(gated, jobHandlers) -> {
                    Registration = gated.Registration
                    JobHandlers = jobHandlers
                  }
                | Error refusals ->
                    failwith (
                        refusals
                        |> List.map DelegatedQueryTemplateRefusal.describe
                        |> String.concat "; "
                    ))
        | refusals -> Error refusals