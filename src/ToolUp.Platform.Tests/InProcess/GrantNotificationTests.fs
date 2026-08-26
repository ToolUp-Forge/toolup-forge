// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GrantNotificationTests

// ─── Phase 556 — grant-event notification to affected principals ─────
//
// Phase 551 gave a module a voice on what must be true before it is
// granted, and refuses a grant that does not satisfy it. It tells nobody
// about the grants it ADMITS — so the grantee discovers their access by
// finding the module in a sidebar and the party the module names
// discovers it never. This pack pins the notice loop 556 closes, matching
// 556.D:
//
//   1. **A policied-module grant notifies; an unpolicied one does not.**
//      The same write, the same store, the same channel — only the
//      registry differs, which is the whole GP 13 claim.
//   2. **Both audiences resolve.** The grantee from the permission
//      document; the declared party through the deployment-supplied
//      `PartyRef` resolver, because a party reference is opaque to the
//      SDK (GP 9) and it must never guess one.
//   3. **A channel outage cannot fail a grant.** The write lands, the
//      failure is logged, and the caller sees `Ok`.
//   4. **No channel composed ⇒ silent and weightless.** Not "an observer
//      that publishes nowhere" — no observer at all, which is what the
//      composition-shaped case below asserts by observing that the
//      undecorated store is what a caller gets.
//
// **Non-vacuity.** Every "no notification" assertion is paired, in the
// same test, with a POSITIVE control that differs in exactly the one
// variable under test — so a `diff` that returned `[]` unconditionally,
// or a channel double that never recorded anything, fails the positive
// half rather than passing the negative one. The scheduler passed to the
// observer is SYNCHRONOUS, so one call yields both the write result and
// its notices: "wrote but did not notify" and "notified but did not
// write" each fail rather than passing half an assertion (the shape
// `GrantPolicyGuard.guardDispatch`'s pack established).

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.GrantNotification

// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read
let private Write = ModulePermission.Write

let private freshStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-grantnotify-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    PermissionStore(LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage) :> IPermissionStore

/// Records every publish, and can be told to throw so the isolation case
/// exercises a real failure rather than a mocked one.
type private RecordingChannel(?failWith: string) =
    let published = ResizeArray<string * Notification>()
    member _.Published = List.ofSeq published

    member this.Customs =
        this.Published
        |> List.choose (fun (scope, n) ->
            match n with
            | CustomNotification(key, payload) -> Some(scope, key, payload)
            | _ -> None)

    member this.Emails =
        this.Published
        |> List.choose (fun (_, n) ->
            match n with
            | TransactionalEmail e -> Some e
            | _ -> None)

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async {
            match failWith with
            | Some message -> return raise (InvalidOperationException message)
            | None -> published.Add(scopeId, notification)
        }

        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }

type private RecordingLogger() =
    let warnings = ResizeArray<string>()
    member _.Warnings = List.ofSeq warnings

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Add message
        member _.Error(_, _) = ()

/// The synchronous scheduler, so decision and delivery are observed
/// together from a single call.
let private runNow (work: Async<unit>) = Async.RunSynchronously work

let private registryOf declarations =
    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations declarations

let private observerOver
    (inner: IPermissionStore)
    (registry: GrantPolicyGuard.ModuleGrantPolicyRegistry)
    (channel: INotificationChannel)
    (settings: GrantNotificationSettings)
    (logger: ILogger)
    =
    GrantNotificationObserver(
        inner,
        registry,
        channel,
        settings,
        logger,
        (fun () -> Some "admin-1"),
        (fun () -> DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)),
        runNow
    )
    :> IPermissionStore

let private activeRecord policy justification = {
    State = GrantState.Active
    SatisfiedPolicy = policy
    Justification = justification
    ConsentedBy = None
}

/// One member entry plus its grant record, as
/// `PermissionGrants.grantModuleAccess` writes the pair.
let private docWith (subjectId: string) (moduleName: string) perms (record: ModuleGrantRecord option) = {
    TeamPermissions.empty with
        Members = Map.ofList [ subjectId, Map.ofList [ moduleName, perms ] ]
        Grants =
            match record with
            | Some r -> Map.ofList [ subjectId, Map.ofList [ moduleName, r ] ]
            | None -> Map.empty
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 556 — grant-event notification to affected principals" [

        testList "the delta (pure)" [
            testCase
                "a new grant record on a policied module produces a grantee notice — and the same write on an unpolicied module produces none"
            <| fun () ->
                let prior = TeamPermissions.empty

                let written =
                    docWith
                        "alice"
                        "Partner"
                        [ Read ]
                        (Some(activeRecord GrantPolicy.RequiresAcknowledgement "onboarding"))

                let policied =
                    diff
                        (registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ])
                        "t1"
                        "admin-1"
                        DateTimeOffset.UnixEpoch
                        prior
                        written

                Expect.hasLength policied 1 "exactly one notice — the grantee"
                Expect.equal policied.Head.Audience GrantNoticeAudience.Grantee "addressed to the grantee"
                Expect.equal policied.Head.Payload.SubjectId "alice" "naming the subject"
                Expect.equal policied.Head.Payload.ModuleName "Partner" "and the module"
                Expect.equal policied.Head.Payload.Permissions "Read" "and what was granted"
                Expect.equal policied.Head.Payload.GrantedBy "admin-1" "and who granted it"

                Expect.equal
                    policied.Head.Payload.Transition
                    GrantNoticeTransition.Recorded
                    "classified as a record appearing"

                // The positive control above is what makes this negative
                // meaningful: identical documents, identical write, only
                // the registry differs.
                let unpolicied =
                    diff
                        (registryOf [ "Partner", GrantPolicy.AdminDiscretion ])
                        "t1"
                        "admin-1"
                        DateTimeOffset.UnixEpoch
                        prior
                        written

                Expect.isEmpty unpolicied "an AdminDiscretion module notifies nobody"

            testCase "a revocation is never a notice, while the grant that preceded it is"
            <| fun () ->
                let registry = registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ]
                let record = activeRecord GrantPolicy.RequiresAcknowledgement "onboarding"
                let granted = docWith "alice" "Partner" [ Read ] (Some record)

                Expect.hasLength
                    (diff registry "t1" "admin-1" DateTimeOffset.UnixEpoch TeamPermissions.empty granted)
                    1
                    "the grant notifies"

                Expect.isEmpty
                    (diff registry "t1" "admin-1" DateTimeOffset.UnixEpoch granted TeamPermissions.empty)
                    "and its removal does not — a policy constrains the creation of authority, never its removal"

            testCase "consent acceptance is 'activated'; a widening is 'widened'; a narrowing is neither"
            <| fun () ->
                let registry = registryOf [ "Partner", GrantPolicy.RequiresSubjectConsent ]

                let pending = {
                    State = GrantState.PendingConsent
                    SatisfiedPolicy = GrantPolicy.RequiresSubjectConsent
                    Justification = "onboarding"
                    ConsentedBy = None
                }

                let accepted = {
                    pending with
                        State = GrantState.Active
                        ConsentedBy = Some "alice"
                }

                let offered = docWith "alice" "Partner" [ Read ] (Some pending)
                let live = docWith "alice" "Partner" [ Read ] (Some accepted)
                let widened = docWith "alice" "Partner" [ Read; Write ] (Some accepted)
                let narrowed = docWith "alice" "Partner" [ Read ] (Some accepted)

                let transitionOf prior written =
                    diff registry "t1" "admin-1" DateTimeOffset.UnixEpoch prior written
                    |> List.map _.Payload.Transition

                Expect.equal
                    (transitionOf TeamPermissions.empty offered)
                    [ GrantNoticeTransition.Recorded ]
                    "a PendingConsent record appearing is 'recorded'"

                Expect.equal
                    (diff registry "t1" "admin-1" DateTimeOffset.UnixEpoch TeamPermissions.empty offered
                     |> List.map _.Payload.GrantState)
                    [ GrantState.toToken GrantState.PendingConsent ]
                    "and carries the pending state, so the message can say 'offered' rather than 'granted'"

                Expect.equal (transitionOf offered live) [ GrantNoticeTransition.Activated ] "acceptance is 'activated'"

                Expect.equal
                    (transitionOf live widened)
                    [ GrantNoticeTransition.Widened ]
                    "adding Write to a live grant is 'widened'"

                Expect.isEmpty (transitionOf widened narrowed) "and dropping Write again is not a notice at all"

            testCase "a counterparty policy adds a declared-party notice alongside the grantee's"
            <| fun () ->
                let party = PartyRef.create "acme-dpo"

                let notices =
                    diff
                        (registryOf [ "Partner", GrantPolicy.RequiresCounterpartyApproval party ])
                        "t1"
                        "admin-1"
                        DateTimeOffset.UnixEpoch
                        TeamPermissions.empty
                        (docWith
                            "alice"
                            "Partner"
                            [ Read ]
                            (Some(activeRecord (GrantPolicy.RequiresCounterpartyApproval party) "approved")))

                Expect.equal
                    (notices |> List.map _.Audience)
                    [ GrantNoticeAudience.Grantee; GrantNoticeAudience.DeclaredParty ]
                    "both audiences, in that order"

                Expect.all notices (fun n -> n.Payload.Party = "acme-dpo") "both carry the declared party verbatim"

                // The contrast that proves the party leg is policy-driven
                // and not unconditional.
                let noParty =
                    diff
                        (registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ])
                        "t1"
                        "admin-1"
                        DateTimeOffset.UnixEpoch
                        TeamPermissions.empty
                        (docWith
                            "alice"
                            "Partner"
                            [ Read ]
                            (Some(activeRecord GrantPolicy.RequiresAcknowledgement "onboarding")))

                Expect.equal
                    (noParty |> List.map _.Audience)
                    [ GrantNoticeAudience.Grantee ]
                    "a policy that names nobody addresses nobody but the grantee"
        ]

        testList "recipient resolution" [
            testCase "the grantee resolves from the document; the party only through the deployment's resolver"
            <| fun () ->
                let party = PartyRef.create "acme-dpo"

                let notices =
                    diff
                        (registryOf [ "Partner", GrantPolicy.RequiresCounterpartyApproval party ])
                        "t1"
                        "admin-1"
                        DateTimeOffset.UnixEpoch
                        TeamPermissions.empty
                        (docWith
                            "alice"
                            "Partner"
                            [ Read ]
                            (Some(activeRecord (GrantPolicy.RequiresCounterpartyApproval party) "approved")))

                let granteeNotice =
                    notices |> List.find (fun n -> n.Audience = GrantNoticeAudience.Grantee)

                let partyNotice =
                    notices |> List.find (fun n -> n.Audience = GrantNoticeAudience.DeclaredParty)

                let bare = GrantNotificationSettings.defaults

                Expect.equal (recipientsOf bare granteeNotice) [ "alice" ] "the grantee is always addressable"

                Expect.isEmpty
                    (recipientsOf bare partyNotice)
                    "and without a resolver the SDK addresses nobody rather than guessing at an opaque PartyRef"

                let resolving =
                    bare
                    |> GrantNotificationSettings.withPartyResolver (fun p ->
                        if PartyRef.value p = "acme-dpo" then
                            [ "dpo-user"; "dpo-user"; "  " ]
                        else
                            [])

                Expect.equal
                    (recipientsOf resolving partyNotice)
                    [ "dpo-user" ]
                    "with one, its answer is taken — deduplicated and stripped of blanks"
        ]

        testList "delivery through the observer" [
            testCaseAsync "a policied grant publishes a real-time notice AND a transactional envelope per audience"
            <| async {
                let store = freshStore ()
                let channel = RecordingChannel()
                let logger = RecordingLogger()
                let party = PartyRef.create "acme-dpo"
                let policy = GrantPolicy.RequiresCounterpartyApproval party

                let settings =
                    GrantNotificationSettings.defaults
                    |> GrantNotificationSettings.withPartyResolver (fun _ -> [ "dpo-user" ])

                // The observer sits OUTSIDE the Phase 551 write guard, so
                // it is composed here over the bare store: what it observes
                // is what was written, whoever admitted it. That is also
                // what makes the counterparty arm testable today — 551
                // refuses that arm at the write until Phase 552 ships the
                // consent store, and the observer's behaviour when such a
                // record does land must be pinned before then, not after.
                let observed =
                    observerOver store (registryOf [ "Partner", policy ]) channel settings logger

                let! result =
                    observed.SetTeamPermissions(
                        "t1",
                        docWith "alice" "Partner" [ Read; Write ] (Some(activeRecord policy "approved by acme"))
                    )

                Expect.isOk result "the write lands"

                let customs = channel.Customs
                Expect.hasLength customs 1 "one real-time notice — the grant, not one per audience"
                let scope, key, payloadJson = customs.Head
                Expect.equal scope "team-t1" "published on the granting team's own topic"
                Expect.equal key GrantNoticeKey "under the documented wire key"
                Expect.stringContains payloadJson "alice" "carrying the subject"
                Expect.stringContains payloadJson "Partner" "and the module"
                Expect.stringContains payloadJson "acme-dpo" "and the declared party"

                Expect.stringContains payloadJson "requires-counterparty-approval" "and the declared policy token"

                let emails = channel.Emails
                Expect.hasLength emails 2 "one transactional envelope per audience"

                Expect.equal
                    (emails |> List.collect _.RecipientUserIds |> List.sort)
                    [ "alice"; "dpo-user" ]
                    "addressed to the grantee and the resolved party — the affected principals"

                Expect.isEmpty logger.Warnings "and nothing degraded"
            }

            testCaseAsync "an unpolicied grant through the same observer publishes nothing at all"
            <| async {
                let store = freshStore ()
                let channel = RecordingChannel()
                let logger = RecordingLogger()

                let observed =
                    observerOver
                        store
                        (registryOf [ "Partner", GrantPolicy.AdminDiscretion ])
                        channel
                        GrantNotificationSettings.defaults
                        logger

                let! result = observed.SetTeamPermissions("t1", docWith "alice" "Partner" [ Read; Write ] None)

                Expect.isOk result "the write still lands"
                Expect.isEmpty channel.Published "and notifies nobody (GP 13 — an undeclared module costs nothing)"

                // Same store, same channel, same observer shape: only the
                // registry moves. Without this the assertion above would
                // also pass against a channel double that never records.
                let policied =
                    observerOver
                        store
                        (registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ])
                        channel
                        GrantNotificationSettings.defaults
                        logger

                let! second =
                    policied.SetTeamPermissions(
                        "t1",
                        docWith
                            "alice"
                            "Partner"
                            [ Read; Write ]
                            (Some(activeRecord GrantPolicy.RequiresAcknowledgement "onboarding"))
                    )

                Expect.isOk second "the policied write lands too"
                Expect.isNonEmpty channel.Published "and this one DOES notify — so the silence above was the registry"
            }

            testCaseAsync "a channel outage never fails the grant — the write lands and the failure is logged"
            <| async {
                let store = freshStore ()
                let channel = RecordingChannel(failWith = "notification transport down")
                let logger = RecordingLogger()

                let observed =
                    observerOver
                        store
                        (registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ])
                        channel
                        GrantNotificationSettings.defaults
                        logger

                let! result =
                    observed.SetTeamPermissions(
                        "t1",
                        docWith
                            "alice"
                            "Partner"
                            [ Read ]
                            (Some(activeRecord GrantPolicy.RequiresAcknowledgement "onboarding"))
                    )

                Expect.isOk result "the caller sees success — a notification outage is not a grant failure"

                let! persisted = store.GetTeamPermissions "t1"

                Expect.equal
                    (TeamPermissions.grantsFor "alice" persisted
                     |> Map.tryFind "Partner"
                     |> Option.map _.State)
                    (Some GrantState.Active)
                    "and the grant is genuinely durable, not merely reported so"

                Expect.isNonEmpty logger.Warnings "the failure is logged rather than swallowed silently"

                Expect.stringContains
                    (String.Join(" ", logger.Warnings))
                    "notification transport down"
                    "naming the underlying cause"
            }

            testCaseAsync "the observer never rewrites the inner store's answer — a refusal stays a refusal"
            <| async {
                let channel = RecordingChannel()
                let logger = RecordingLogger()

                // The Phase 551 guard beneath refuses an unbacked grant on
                // a policied module. The observer must pass that through
                // untouched AND publish nothing: telling someone they hold
                // authority the store refused to create is worse than
                // telling them nothing.
                let guarded =
                    GrantPolicyGuard.GrantPolicyPermissionStore(
                        freshStore (),
                        registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ]
                    )
                    :> IPermissionStore

                let observed =
                    observerOver
                        guarded
                        (registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ])
                        channel
                        GrantNotificationSettings.defaults
                        logger

                let! result = observed.SetTeamPermissions("t1", docWith "alice" "Partner" [ Read ] None)

                Expect.isError result "the guard's refusal survives the observer"
                Expect.isEmpty channel.Published "and a refused grant notifies nobody"
            }

            testCaseAsync "team defaults and module exposure are not grants and notify nobody"
            <| async {
                let store = freshStore ()
                let channel = RecordingChannel()
                let logger = RecordingLogger()

                let observed =
                    observerOver
                        store
                        (registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ])
                        channel
                        GrantNotificationSettings.defaults
                        logger

                let! defaults = observed.SetTeamDefaults("t1", Map.ofList [ "Partner", [ Read ] ])
                Expect.isOk defaults "the defaults write lands"

                let! exposure = observed.SetModuleExposure("t1", "Partner", ModuleExposure.Hidden)
                Expect.isOk exposure "the exposure write lands"

                Expect.isEmpty
                    channel.Published
                    "neither has an affected principal — a default has no subject, an exposure change grants nothing"
            }
        ]

        testList "message shapes (556.B)" [
            testCase "the default templates carry the grant fact and never module data"
            <| fun () ->
                let payload = {
                    TeamId = "t1"
                    ModuleName = "Partner"
                    SubjectId = "alice"
                    GrantedBy = "admin-1"
                    Permissions = "Read,Write"
                    DeclaredPolicy = GrantPolicy.toToken GrantPolicy.RequiresAcknowledgement
                    GrantState = GrantState.toToken GrantState.Active
                    Transition = GrantNoticeTransition.Recorded
                    Party = ""
                    OccurredAtUtc = DateTimeOffset.UnixEpoch
                }

                let textOf =
                    function
                    | InlineEmail(subject, body, _) -> subject + " | " + body
                    | TemplatedEmail(id, _) -> failtestf "expected an inline default, got template '%s'" id

                let grantee = textOf (GrantNoticeTemplates.defaults.Grantee payload)
                let party = textOf (GrantNoticeTemplates.defaults.Party payload)

                Expect.stringContains grantee "Partner" "the grantee is told which module"
                Expect.stringContains grantee "Read,Write" "and which permissions"
                Expect.stringContains grantee "admin-1" "and who granted them"
                Expect.stringContains party "alice" "the party is told who was granted"
                Expect.stringContains party "Partner" "on which module"

                Expect.isFalse
                    (grantee.Contains "granted you" && party.Contains "granted you")
                    "the two audiences are addressed differently — the party is not told they hold the grant"

            testCase "a pending-consent notice says 'offered', not 'granted'"
            <| fun () ->
                let pending = {
                    TeamId = "t1"
                    ModuleName = "Partner"
                    SubjectId = "alice"
                    GrantedBy = ""
                    Permissions = "Read"
                    DeclaredPolicy = GrantPolicy.toToken GrantPolicy.RequiresSubjectConsent
                    GrantState = GrantState.toToken GrantState.PendingConsent
                    Transition = GrantNoticeTransition.Recorded
                    Party = ""
                    OccurredAtUtc = DateTimeOffset.UnixEpoch
                }

                match GrantNoticeTemplates.defaults.Grantee pending with
                | InlineEmail(subject, body, _) ->
                    Expect.stringContains subject "offered" "an inert grant is not announced as access held"

                    Expect.stringContains
                        body
                        "an administrator"
                        "and an unresolved actor reads as 'an administrator', never as an empty name"
                | TemplatedEmail(id, _) -> failtestf "expected an inline default, got template '%s'" id

            testCase "templates are overridable, including as a vendor-side template reference"
            <| fun () ->
                let settings =
                    GrantNotificationSettings.defaults
                    |> GrantNotificationSettings.withTemplates {
                        Grantee = fun p -> TemplatedEmail("grant-grantee", Map.ofList [ "module", p.ModuleName ])
                        Party = fun p -> TemplatedEmail("grant-party", Map.ofList [ "subject", p.SubjectId ])
                    }

                let payload = {
                    TeamId = "t1"
                    ModuleName = "Partner"
                    SubjectId = "alice"
                    GrantedBy = "admin-1"
                    Permissions = "Read"
                    DeclaredPolicy = GrantPolicy.toToken GrantPolicy.RequiresAcknowledgement
                    GrantState = GrantState.toToken GrantState.Active
                    Transition = GrantNoticeTransition.Recorded
                    Party = ""
                    OccurredAtUtc = DateTimeOffset.UnixEpoch
                }

                Expect.equal
                    (settings.Templates.Grantee payload)
                    (TemplatedEmail("grant-grantee", Map.ofList [ "module", "Partner" ]))
                    "the override is what the observer will render"
        ]

        testList "delivery discipline (556.C)" [
            testCaseAsync "either leg can be switched off independently"
            <| async {
                let run publishRealTime publishTransactional = async {
                    let channel = RecordingChannel()

                    let observed =
                        observerOver
                            (freshStore ())
                            (registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ])
                            channel
                            {
                                GrantNotificationSettings.defaults with
                                    PublishRealTime = publishRealTime
                                    PublishTransactional = publishTransactional
                            }
                            (RecordingLogger())

                    let! result =
                        observed.SetTeamPermissions(
                            "t1",
                            docWith
                                "alice"
                                "Partner"
                                [ Read ]
                                (Some(activeRecord GrantPolicy.RequiresAcknowledgement "onboarding"))
                        )

                    Expect.isOk result "the write lands regardless of the delivery settings"
                    return List.length channel.Customs, List.length channel.Emails
                }

                let! both = run true true
                Expect.equal both (1, 1) "both legs on: one real-time notice, one envelope"

                let! realTimeOnly = run true false
                Expect.equal realTimeOnly (1, 0) "transactional off: the real-time notice survives"

                let! transactionalOnly = run false true
                Expect.equal transactionalOnly (0, 1) "real-time off: the envelope survives"

                let! neither = run false false
                Expect.equal neither (0, 0) "both off: silent"
            }

            testCaseAsync
                "the correlation id is stable per (team, module, subject, transition) so a retrying vendor cannot double-send"
            <| async {
                let channel = RecordingChannel()

                let observed =
                    observerOver
                        (freshStore ())
                        (registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ])
                        channel
                        GrantNotificationSettings.defaults
                        (RecordingLogger())

                let! _ =
                    observed.SetTeamPermissions(
                        "t1",
                        docWith
                            "alice"
                            "Partner"
                            [ Read ]
                            (Some(activeRecord GrantPolicy.RequiresAcknowledgement "onboarding"))
                    )

                Expect.equal
                    (channel.Emails |> List.map _.CorrelationId)
                    [ Some "grant-notice:t1:Partner:alice:recorded" ]
                    "a deterministic id, derived from the grant rather than minted per publish"
            }
        ]
    ]