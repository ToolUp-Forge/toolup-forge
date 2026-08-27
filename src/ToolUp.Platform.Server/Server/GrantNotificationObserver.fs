// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.GrantNotification

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.PermissionStore

// ─── Grant-event notification to affected principals (Phase 556) ─────
//
// Silent grants are the accident vector — "viewed by accident, nobody
// noticed". Phase 551 gave a module a voice on what must be true before
// it is granted at all, and refuses a grant that does not satisfy it.
// What 551 did NOT do is tell anybody when a grant it DID admit landed:
// the grantee learns they have access by finding the module in their
// sidebar, and the party the module's policy names learns nothing at
// all. This phase closes that notice loop — a cheap detective control
// over an already-enforced preventive one.
//
// **No new delivery machinery.** Every notice rides the substrate that
// already exists: `INotificationChannel.Publish` for the real-time /
// console surface (Phase 6a) and a `TransactionalEmail` envelope for
// per-principal out-of-band delivery (Phase 6f). The address resolution,
// the per-team preference kill-switch, the retry loop and the terminal
// `NotificationSent` / `NotificationDeliveryFailed` audit rows are all
// the transactional dispatcher's, unchanged.
//
// ─── Why this observes the WRITE and not an audit event ──────────────
//
// The phase text describes the bridge as "subscribing to permission-class
// audit events". Against the code as it actually stands that seam cannot
// carry the phase's own acceptance, for two independent reasons, and both
// were checked before this file was written:
//
//   1. **The event that would carry it is not emitted on the path that
//      matters.** `PermissionChanged` is emitted by `permissionApiHandler`
//      on `SetMemberPermissions` / `SetTeamDefaults` / `SetModuleExposure`
//      — and the Phase 551 decorator REFUSES a policy-bearing grant on the
//      legacy `SetMemberPermissions` path by construction, because that
//      signature has nowhere to carry acknowledgement or justification. The
//      initial grant on a policy-bearing module therefore lands through
//      `PermissionGrants.grantModuleAccess` → `SetTeamPermissions`, which
//      emits no audit event at all. An observer over the audit stream would
//      be silent for exactly the case this phase exists to cover: present,
//      plausible, and inert. That is the Phase 311 / Phase 36.A failure
//      shape this estate keeps writing down.
//   2. **The payloads do not carry the recipients.** `PermissionChanged`
//      names actor + affected user + module but never the grant record, so
//      "recorded pending consent" and "live" are indistinguishable; the
//      Phase 555 family (`AdminMutationProposed` / `Approved` / …) carries
//      `RequestId` / `MutationKind` / `Fingerprint` and neither a module
//      name nor a subject. Neither can resolve "who is affected" without
//      re-reading the store — at which point the audit event is decoration
//      on a store read rather than the source of truth.
//
// So the observer is an `IPermissionStore` decorator over the DOCUMENT
// DELTA of a write that SUCCEEDED. That is the same choice, for the same
// reason, that put the 551 guard and the 555 dual-control gate on this
// interface rather than on their callers: there is nothing for a caller to
// remember, and every write path — `grantModuleAccess`, a re-grant through
// `SetMemberPermissions`, a dual-control approval applying a parked write,
// a consumer driving the store directly — is observed by the same code.
// GP 6 still holds: the notice is derived from the same persisted evidence
// the audit trail is, not from a second bookkeeping store.
//
// ─── Where it sits, and what it costs when unused ────────────────────
//
//     GrantNotificationObserver        (556 — outermost, observes only)
//       └─ GrantPolicyPermissionStore  (551 — refuses an unbacked grant)
//            └─ DualControlPermissionStore (555 — parks a gated write)
//                 └─ SanitisingPermissionStore
//                      └─ PermissionStore
//
// Outermost, and deliberately: it must observe what was actually WRITTEN,
// so it has to sit outside every decorator that can refuse or park the
// write. It never refuses anything and never rewrites a result — a
// notification is not a control, and an observer that could fail a grant
// would be a worse bug than the silence it exists to fix.
//
// Composed ONLY when a non-empty `ModuleGrantPolicyRegistry` exists AND an
// `INotificationChannel` is resolvable (GP 13). A deployment where every
// module carries `AdminDiscretion` composes no observer at all — not an
// observer that always answers no — so the extra document read below does
// not exist in a pre-551 deployment's request path.

// ─── Wire contract ───────────────────────────────────────────────────

/// The `CustomNotification` key every grant notice travels under.
/// **Part of the public wire contract** — an admin console subscribes by
/// literal string match, exactly as `"Narrative.Published"` is subscribed
/// to. Scope-private (published on the granting team's own topic), so it
/// is not one of the reserved cross-scope kinds enumerated in the
/// technical guide's notification chapter.
[<Literal>]
let GrantNoticeKey = "Platform.GrantNotice"

/// What happened to the grant. One key carrying a discriminator rather
/// than three keys, because a subscriber wanting "every grant event on a
/// policied module" is the common case and would otherwise have to
/// register three listeners to get it.
module GrantNoticeTransition =
    /// A grant record appeared where none existed — authority was
    /// created (`Active`) or proposed to the subject (`PendingConsent`).
    [<Literal>]
    let Recorded = "recorded"

    /// A `PendingConsent` record became `Active` — the grantee accepted
    /// and the grant now carries authority at dispatch.
    [<Literal>]
    let Activated = "activated"

    /// The permission set on an already-recorded grant widened. The
    /// record did not move; what the holder can do did.
    [<Literal>]
    let Widened = "widened"

/// The neutral, value-free notice payload: who, what module, which
/// permissions, granted by whom, under which declared policy, when.
///
/// **Never module data.** Nothing here is read from the module's own
/// storage — every field is drawn from the permission document and the
/// compose-time policy registry. A notice can be logged, forwarded to an
/// email vendor, or carried across a shared bus without leaking anything
/// the grant itself did not already say.
type GrantNoticePayload = {
    TeamId: string
    ModuleName: string
    /// The principal who now holds (or is offered) the grant.
    SubjectId: string
    /// The administrator who performed the write, resolved from the
    /// server-side request identity. Empty when no identity could be
    /// resolved — never a client-asserted id.
    GrantedBy: string
    /// Comma-separated permission tokens (`Read,Write`). Never empty on
    /// a notice: a revocation produces none.
    Permissions: string
    /// `GrantPolicy.toToken` of the module's DECLARED policy.
    DeclaredPolicy: string
    /// `GrantState.toToken` of the record as written.
    GrantState: string
    /// One of the `GrantNoticeTransition` tokens.
    Transition: string
    /// `PartyRef.value` of the counterparty the declared policy names, or
    /// `""` when the policy names nobody. Opaque to the SDK (GP 9).
    Party: string
    OccurredAtUtc: DateTimeOffset
}

/// Who a notice is addressed to. The two audiences take different prose
/// ("you now have access to X" vs "Z was granted access to your module")
/// and resolve through different machinery, so they are distinguished at
/// the type level rather than by a boolean on the payload.
[<RequireQualifiedAccess>]
type GrantNoticeAudience =
    /// The principal named by `SubjectId` — a resolvable user id.
    | Grantee
    /// The party the module's declared policy names. A `PartyRef` is an
    /// opaque deployment string (a tenant id, a DPO mailbox, a regulator
    /// handle), NOT a user id, so it is resolved to recipients by a
    /// deployment-supplied function; absent one, this audience resolves
    /// to nobody and the leg is silently skipped.
    | DeclaredParty

/// One notice, before delivery. `diff` produces these; the observer
/// renders and publishes them.
type GrantNotice = {
    Audience: GrantNoticeAudience
    Payload: GrantNoticePayload
}

// ─── Message shapes (556.B) ──────────────────────────────────────────

/// Per-audience email rendering. Overridable per the notification
/// substrate's conventions — the same `EmailContent` a deployment could
/// build by hand, so a `TemplatedEmail` referencing a vendor-side
/// template is as available as the inline default.
type GrantNoticeTemplates = {
    Grantee: GrantNoticePayload -> EmailContent
    Party: GrantNoticePayload -> EmailContent
}

module GrantNoticeTemplates =
    let private verb (payload: GrantNoticePayload) =
        if
            payload.Transition = GrantNoticeTransition.Recorded
            && payload.GrantState = GrantState.toToken GrantState.PendingConsent
        then
            "offered"
        elif payload.Transition = GrantNoticeTransition.Widened then
            "widened"
        else
            "granted"

    let private grantor (payload: GrantNoticePayload) =
        if String.IsNullOrWhiteSpace payload.GrantedBy then
            "an administrator"
        else
            payload.GrantedBy

    /// Inline plain-text defaults. Deliberately dull and factual: the
    /// notice is a detective control, and prose that editorialises about
    /// what the access "means" would be the SDK naming a domain concept
    /// it has no business naming.
    let defaults: GrantNoticeTemplates = {
        Grantee =
            fun p ->
                InlineEmail(
                    $"Access %s{verb p}: %s{p.ModuleName}",
                    $"%s{grantor p} %s{verb p} you '%s{p.Permissions}' access to the module '%s{p.ModuleName}' in team '%s{p.TeamId}' at %O{p.OccurredAtUtc}. "
                    + $"The module declares the grant policy '%s{p.DeclaredPolicy}'; this grant is recorded as '%s{p.GrantState}'. "
                    + "If you did not expect this, contact an administrator of the team.",
                    None
                )
        Party =
            fun p ->
                InlineEmail(
                    $"Grant on '%s{p.ModuleName}': %s{p.SubjectId}",
                    $"'%s{p.SubjectId}' was %s{verb p} '%s{p.Permissions}' access to the module '%s{p.ModuleName}' in team '%s{p.TeamId}' at %O{p.OccurredAtUtc}, by %s{grantor p}. "
                    + $"The module declares the grant policy '%s{p.DeclaredPolicy}'; the grant is recorded as '%s{p.GrantState}'.",
                    None
                )
    }

/// How a deployment tunes the fan-out. Both delivery legs default ON:
/// the phase's acceptance is that a grant on a policy-carrying module
/// produces a notification to the grantee and to the declared party
/// "through whatever channels are composed", and a leg that had to be
/// switched on would leave the control silent in the deployment that
/// declared a policy and assumed the notice came with it.
///
/// "Through whatever channels are composed" is what makes that safe: the
/// real-time publish reaches whoever is subscribed to the team's topic
/// (nobody, in a deployment with no console), and the transactional
/// envelope is dropped by `NotificationHandler`'s SSE filter and picked
/// up by an `INotificationSink` of `Kind = Email` only if one is wired.
/// Neither leg can reach a principal a deployment has not already given
/// the substrate a way to reach.
type GrantNotificationSettings = {
    /// Publish a `CustomNotification` on the granting team's topic.
    PublishRealTime: bool
    /// Publish a `TransactionalEmail` addressed to the resolved
    /// recipients of each audience.
    PublishTransactional: bool
    /// Resolve the opaque `PartyRef` a module's declared policy names
    /// into user ids the address book can look up. The SDK never
    /// interprets a `PartyRef` (GP 9), so absent a deployment-supplied
    /// resolver the party leg resolves to nobody — reported honestly on
    /// the payload as the `Party` field rather than silently dropped.
    ResolvePartyRecipients: PartyRef -> string list
    Templates: GrantNoticeTemplates
}

module GrantNotificationSettings =
    let defaults: GrantNotificationSettings = {
        PublishRealTime = true
        PublishTransactional = true
        ResolvePartyRecipients = fun _ -> []
        Templates = GrantNoticeTemplates.defaults
    }

    /// Supply the deployment's `PartyRef` → user-id resolution.
    let withPartyResolver (resolve: PartyRef -> string list) (settings: GrantNotificationSettings) = {
        settings with
            ResolvePartyRecipients = resolve
    }

    let withTemplates (templates: GrantNoticeTemplates) (settings: GrantNotificationSettings) = {
        settings with
            Templates = templates
    }

// ─── The delta (pure) ────────────────────────────────────────────────

let private permissionToken =
    function
    | ModulePermission.Read -> "Read"
    | ModulePermission.Write -> "Write"
    | ModulePermission.Admin -> "Admin"
    | ModulePermission.SchemaOnly -> "SchemaOnly"

let private permissionsCsv (perms: ModulePermission list) =
    perms |> List.map permissionToken |> String.concat ","

/// Does `written` confer anything `prior` did not? Uses the SDK's own
/// permission hierarchy (`Admin` implies `Write` implies `Read`;
/// `SchemaOnly` stands alone), so `[Admin]` → `[Read]` is a NARROWING and
/// produces no notice, while `[Read]` → `[Read; SchemaOnly]` widens.
///
/// Phase 555's `widenedModules` reads any change as a widening because it
/// is a GATE and must fail safe; this is a notice and must not cry wolf
/// on a narrowing, so the two differ deliberately rather than by accident.
let private widens (prior: ModulePermission list) (written: ModulePermission list) =
    written
    |> List.exists (fun w -> not (prior |> List.exists (fun p -> ModulePermission.implies p w)))

let private partyOf (policy: GrantPolicy) =
    match policy with
    | GrantPolicy.RequiresCounterpartyApproval party -> PartyRef.value party
    | _ -> ""

let private permissionsFor (subjectId: string) (moduleName: string) (doc: TeamPermissions) =
    doc.Members
    |> Map.tryFind subjectId
    |> Option.bind (Map.tryFind moduleName)
    |> Option.defaultValue []

let private recordFor (subjectId: string) (moduleName: string) (doc: TeamPermissions) =
    doc.Grants |> Map.tryFind subjectId |> Option.bind (Map.tryFind moduleName)

/// Classify one (subject, module) pair across a write. `None` when
/// nothing happened worth telling anybody about.
///
/// **A revocation is never a notice.** Phase 551's whole framing is that
/// a policy constrains the CREATION of authority and never its removal;
/// the notice loop this phase closes is about authority appearing.
let private classify
    (priorPerms: ModulePermission list)
    (writtenPerms: ModulePermission list)
    (priorRecord: ModuleGrantRecord option)
    (writtenRecord: ModuleGrantRecord option)
    : string option =
    if List.isEmpty writtenPerms then
        None
    else
        match priorRecord, writtenRecord with
        | None, Some _ -> Some GrantNoticeTransition.Recorded
        | Some prior, Some written when prior.State <> written.State ->
            if written.State = GrantState.Active then
                Some GrantNoticeTransition.Activated
            else
                // Active → PendingConsent is not a transition any shipped
                // path produces (consent is one-way). Reporting it as a
                // record appearing would be a lie, and swallowing it would
                // hide a document somebody edited by hand; `Recorded` is
                // the honest "this record is now what it is" reading.
                Some GrantNoticeTransition.Recorded
        | _, Some _ when widens priorPerms writtenPerms -> Some GrantNoticeTransition.Widened
        | _ -> None

/// Every notice a write produced. Pure over the two documents, so the
/// classification is testable without a channel, a store, or a clock.
///
/// Quantified over the REGISTRY, not over the document: only modules that
/// declare a non-default policy are considered at all, which is both the
/// phase's scope ("a grant touches a module carrying a non-default
/// `GrantPolicy`") and the reason an `AdminDiscretion`-only deployment
/// produces an empty list without walking anything.
let diff
    (registry: GrantPolicyGuard.ModuleGrantPolicyRegistry)
    (teamId: string)
    (actorId: string)
    (now: DateTimeOffset)
    (prior: TeamPermissions)
    (written: TeamPermissions)
    : GrantNotice list =
    [
        for KeyValue(moduleName, policy) in registry.Policies do
            // Subjects the write could have touched on this module:
            // everyone carrying a permission entry or a grant record on
            // either side of the write.
            let subjects =
                [
                    for KeyValue(subjectId, byModule) in written.Members do
                        if Map.containsKey moduleName byModule then
                            subjectId
                    for KeyValue(subjectId, byModule) in written.Grants do
                        if Map.containsKey moduleName byModule then
                            subjectId
                    for KeyValue(subjectId, byModule) in prior.Grants do
                        if Map.containsKey moduleName byModule then
                            subjectId
                ]
                |> List.distinct
                |> List.sort

            for subjectId in subjects do
                let priorPerms = permissionsFor subjectId moduleName prior
                let writtenPerms = permissionsFor subjectId moduleName written
                let priorRecord = recordFor subjectId moduleName prior
                let writtenRecord = recordFor subjectId moduleName written

                match classify priorPerms writtenPerms priorRecord writtenRecord with
                | None -> ()
                | Some transition ->
                    let payload = {
                        TeamId = teamId
                        ModuleName = moduleName
                        SubjectId = subjectId
                        GrantedBy = actorId
                        Permissions = permissionsCsv writtenPerms
                        DeclaredPolicy = GrantPolicy.toToken policy
                        GrantState =
                            writtenRecord
                            |> Option.map (fun r -> GrantState.toToken r.State)
                            |> Option.defaultValue (GrantState.toToken GrantState.Active)
                        Transition = transition
                        Party = partyOf policy
                        OccurredAtUtc = now
                    }

                    {
                        Audience = GrantNoticeAudience.Grantee
                        Payload = payload
                    }

                    if payload.Party <> "" then
                        {
                            Audience = GrantNoticeAudience.DeclaredParty
                            Payload = payload
                        }
    ]

// ─── Delivery (556.C) ────────────────────────────────────────────────

/// The user ids one notice is addressed to. Empty is a normal, silent
/// outcome for `DeclaredParty` — a deployment that supplied no resolver
/// has not told the SDK how to reach the party, and inventing a recipient
/// would be worse than saying nothing.
let recipientsOf (settings: GrantNotificationSettings) (notice: GrantNotice) : string list =
    match notice.Audience with
    | GrantNoticeAudience.Grantee ->
        if String.IsNullOrWhiteSpace notice.Payload.SubjectId then
            []
        else
            [ notice.Payload.SubjectId ]
    | GrantNoticeAudience.DeclaredParty ->
        if notice.Payload.Party = "" then
            []
        else
            settings.ResolvePartyRecipients(PartyRef.create notice.Payload.Party)
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> List.distinct

/// `IPermissionStore` decorator that observes successful grant writes and
/// publishes a notice per affected principal. Never refuses, never
/// rewrites a result, never fails a write.
///
/// `schedule` runs the fan-out. Production passes `Async.Start` — delivery
/// is fire-and-forget and a channel that stalls must not hold a grant
/// write open; a test passes a synchronous scheduler and observes the
/// write and its notices from one call, so "wrote but did not notify" and
/// "notified but did not write" both fail rather than passing half the
/// assertion (the shape `GrantPolicyGuard.guardDispatch` established).
///
/// `resolveActor` is the server-side request identity, threaded in from
/// the composition root exactly as Phase 555's proposer is. `None` is a
/// legitimate answer (a background write, a consumer driving the store
/// outside a request) and yields an empty `GrantedBy` — never a
/// client-asserted id, and never a fabricated one.
///
/// **No retry.** Per 556.C the channel companion's own semantics are the
/// delivery contract; a retry loop here would double-send against a
/// transport that already retries, and the transactional dispatcher
/// already owns the retry policy for the out-of-band leg.
type GrantNotificationObserver
    (
        inner: IPermissionStore,
        registry: GrantPolicyGuard.ModuleGrantPolicyRegistry,
        channel: INotificationChannel,
        settings: GrantNotificationSettings,
        logger: ILogger,
        resolveActor: unit -> string option,
        now: unit -> DateTimeOffset,
        schedule: Async<unit> -> unit
    ) =

    let jsonOptions = FableConverters.create ()

    let scopeOf (teamId: string) = $"team-{teamId}"

    /// Publish one notice. Swallow-and-log: a notification outage must
    /// never fail the grant write, and it has already succeeded by the
    /// time this runs.
    let publish (notice: GrantNotice) : Async<unit> = async {
        try
            let scopeId = scopeOf notice.Payload.TeamId
            let recipients = recipientsOf settings notice

            // The real-time leg is published once per notice regardless of
            // recipient resolution: a console subscribed to the team topic
            // is watching the GRANT, and a party the deployment cannot
            // address is precisely the case an operator wants to see.
            if settings.PublishRealTime && notice.Audience = GrantNoticeAudience.Grantee then
                let payloadJson = JsonSerializer.Serialize(notice.Payload, jsonOptions)
                do! channel.Publish(scopeId, CustomNotification(GrantNoticeKey, payloadJson))

            if settings.PublishTransactional && not (List.isEmpty recipients) then
                let content =
                    match notice.Audience with
                    | GrantNoticeAudience.Grantee -> settings.Templates.Grantee notice.Payload
                    | GrantNoticeAudience.DeclaredParty -> settings.Templates.Party notice.Payload

                do!
                    channel.Publish(
                        scopeId,
                        TransactionalEmail {
                            RecipientUserIds = recipients
                            Content = content
                            CorrelationId =
                                Some
                                    $"grant-notice:{notice.Payload.TeamId}:{notice.Payload.ModuleName}:{notice.Payload.SubjectId}:{notice.Payload.Transition}"
                        }
                    )
        with ex ->
            logger.Warn
                $"[GrantNotificationObserver] failed to publish a grant notice for module '{notice.Payload.ModuleName}' in team '{notice.Payload.TeamId}'; the grant itself is unaffected. {ex.Message}"
    }

    let fanOut (teamId: string) (prior: TeamPermissions) (written: TeamPermissions) =
        try
            let actorId = resolveActor () |> Option.defaultValue ""
            let notices = diff registry teamId actorId (now ()) prior written

            if not (List.isEmpty notices) then
                schedule (
                    async {
                        for notice in notices do
                            do! publish notice
                    }
                )
        with ex ->
            // The delta itself is pure, so this is unreachable short of a
            // corrupt document — logged rather than propagated for the
            // same reason the publish is: the write has already landed.
            logger.Warn
                $"[GrantNotificationObserver] failed to derive grant notices for team '{teamId}'; the grant itself is unaffected. {ex.Message}"

    /// Read the pre-write document, delegate, and fan out on success.
    /// The read is the observer's only cost and it is paid only on a
    /// policy-declaring deployment's permission WRITES — never on a read
    /// path, and never at all when no module declares a policy (the
    /// observer is not composed).
    let observing (teamId: string) (write: Async<Result<unit, string>>) = async {
        let! prior = async {
            try
                let! doc = inner.GetTeamPermissions teamId
                return Some doc
            with ex ->
                logger.Warn
                    $"[GrantNotificationObserver] could not read the pre-write permission document for team '{teamId}'; the write proceeds unobserved. {ex.Message}"

                return None
        }

        let! result = write

        match result, prior with
        | Ok(), Some priorDoc ->
            try
                let! written = inner.GetTeamPermissions teamId
                fanOut teamId priorDoc written
            with ex ->
                logger.Warn
                    $"[GrantNotificationObserver] could not read back the permission document for team '{teamId}' after a successful write; no notice was published. {ex.Message}"
        | _ -> ()

        return result
    }

    /// The composed inner store, for a caller that needs to reach past
    /// the observer (a test asserting the write landed, an admin surface
    /// resolving the concrete decorator beneath).
    member _.Inner = inner

    interface IPermissionStore with
        member _.GetTeamPermissions teamId = inner.GetTeamPermissions teamId

        member _.GetEffectivePermissions(userId, teamId) =
            inner.GetEffectivePermissions(userId, teamId)

        member _.GetModuleExposure teamId = inner.GetModuleExposure teamId

        // Exposure is a visibility axis, not a grant of authority to a
        // principal — there is no affected principal to notify.
        member _.SetModuleExposure(teamId, moduleName, state) =
            inner.SetModuleExposure(teamId, moduleName, state)

        member _.SetTeamPermissions(teamId, permissions) =
            observing teamId (inner.SetTeamPermissions(teamId, permissions))

        member _.SetMemberPermissions(teamId, userId, moduleName, permissions) =
            observing teamId (inner.SetMemberPermissions(teamId, userId, moduleName, permissions))

        // A team default has no subject to notify: it applies to every
        // member who lacks an explicit entry, which is why Phase 551
        // refuses a policy-bearing module in the defaults map outright.
        member _.SetTeamDefaults(teamId, defaults) = inner.SetTeamDefaults(teamId, defaults)