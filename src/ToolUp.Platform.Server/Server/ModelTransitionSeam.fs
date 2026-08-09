// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 644 — the author-agnostic model-transition seam ───────────
//
// Phase 453 gave a model artifact a governed lifecycle (`Draft` →
// `Fitted` → `Approved` → `Retired`) and one way to move it:
// `IModelRegistry.TransitionStatus`, which takes an actor id and a team
// role. That signature answers "may THIS TEAM MEMBER do this", which is
// the only question a single deployment with an admin screen ever asks.
//
// It is not the only author a lifecycle judgment can have. A peer
// deployment can hold the approval authority while the durable record
// lives here (Phase 644), and a promotion policy can author the same
// judgment with no human in it at all (Phase 645). Three authors, one
// state machine — so this file is the ONE entry point all three take,
// and the lifecycle graph, the authority check and the recorded
// attribution are written once.
//
// **The seam is author-agnostic by construction, not by convention.**
// An author is a value (`ModelTransitionAuthor`) and everything that
// varies with it — the channel the invocation arrived on, the identity
// recorded on the trail, the role the registry is handed — is a total
// function of that value. Phase 645 adds a case, not a second
// `invoke`; if it needed one, this seam would have failed at the only
// thing it exists to do.
//
// **Three refusal classes, and the order they are judged in is
// load-bearing.**
//
//   1. *Unknown artifact* — there is nothing to judge. Checked first
//      because every later check needs the artifact's current status.
//   2. *Invalid transition* — an edge the lifecycle graph forbids.
//      Checked BEFORE authority, so an author asking for an impossible
//      edge is told it is impossible rather than told to go and
//      negotiate a grant that would buy it nothing. This is also the
//      order `BlobModelRegistry` already judges in, and two judges that
//      disagree on order would report different reasons for the same
//      call.
//   3. *Insufficient authority* — the edge is legal and this author may
//      not take it. Last, so the message a caller acts on is the one
//      that is actually actionable.
//
// **What this seam does NOT do.** It does not re-implement the
// transition: `IModelRegistry.TransitionStatus` still performs the
// versioned write and still emits its own `ModelArtifactTransitioned`
// row. This is a judgment + attribution layer over it, which is why a
// deployment that never composes one keeps exactly the behaviour it had
// (GP 11) and pays nothing (GP 13).

/// Where a lifecycle invocation entered this deployment.
///
/// Two values, closed. A policy verdict is authored data-side, so it
/// arrives on `Local` and is told apart from a human by its AUTHOR kind
/// — one axis for "how did this reach us", a different axis for "who
/// decided", rather than one axis doing both jobs badly.
[<RequireQualifiedAccess>]
type ModelTransitionChannel =
    /// Authored inside this deployment — an admin screen, a scheduled
    /// task, or (Phase 645) a promotion policy.
    | Local
    /// Authored by a peer deployment and carried across the federation
    /// seam.
    | Peer

[<RequireQualifiedAccess>]
module ModelTransitionChannel =

    /// The stable lowercase label recorded on the audit trail. Do not
    /// rename without a wire-format bump — a trail is read long after
    /// the code that wrote it.
    let label (channel: ModelTransitionChannel) : string =
        match channel with
        | ModelTransitionChannel.Local -> "local"
        | ModelTransitionChannel.Peer -> "peer"

/// Who authored a lifecycle judgment.
///
/// **The extension point Phase 645 plugs into.** A promotion policy is
/// not a second kind of transition with its own state machine; it is
/// this DU's third case, and everything downstream of it — the channel,
/// the recorded identity, the role handed to the registry — is already
/// a total function over the cases.
type ModelTransitionAuthor =
    /// A team member acting through this deployment's own surfaces. The
    /// role travels with the author because it is the local authority
    /// question, and it is the ONLY case where a team role means
    /// anything.
    | LocalUser of userId: string * role: TeamRole
    /// A peer deployment's actor, invoking across the federation seam.
    /// Both identities are recorded: the peer is who this deployment has
    /// an agreement with, the actor is who the peer says asked.
    | PeerActor of peerId: string * actorId: string
    /// Phase 645 — a promotion policy's verdict. Named by the policy's
    /// stable id, because "the policy decided" is only auditable if the
    /// trail names WHICH policy.
    | PolicyVerdict of policyId: string

[<RequireQualifiedAccess>]
module ModelTransitionAuthor =

    /// The author's kind, as the stable label the trail records.
    let kind (author: ModelTransitionAuthor) : string =
        match author with
        | LocalUser _ -> "user"
        | PeerActor _ -> "peer"
        | PolicyVerdict _ -> "policy"

    /// The author's identity in the form its kind implies. A peer actor
    /// carries BOTH identities in one string, `{peerId}/{actorId}`,
    /// because either alone is ambiguous: two peers can name the same
    /// actor id, and one peer's actor set is not this deployment's user
    /// set.
    let id (author: ModelTransitionAuthor) : string =
        match author with
        | LocalUser(userId, _) -> userId
        | PeerActor(peerId, actorId) -> $"{peerId}/{actorId}"
        | PolicyVerdict policyId -> policyId

    /// The channel an author's invocation arrives on.
    let channel (author: ModelTransitionAuthor) : ModelTransitionChannel =
        match author with
        | LocalUser _ -> ModelTransitionChannel.Local
        | PeerActor _ -> ModelTransitionChannel.Peer
        // A policy runs data-side, so it is local by the only definition
        // of the word this type uses: nothing crossed a seam to get here.
        | PolicyVerdict _ -> ModelTransitionChannel.Local

    /// The `TeamRole` the registry is handed for this author.
    ///
    /// **A local user carries its own role; a peer actor and a policy
    /// carry `Owner`, and that is a judgment already made rather than a
    /// privilege invented.** The registry's role gate asks "is this
    /// caller an Owner/Admin of the team", which is unanswerable for an
    /// actor that has no membership in it — and answering it `Member`
    /// would make a granted transition authority permanently unusable,
    /// which is not a safe default but a broken one. The authority
    /// question for those two authors is asked HERE instead, against the
    /// declared `ModelTransitionAuthority`, and it is asked before the
    /// registry is called at all. Presenting the registry with a role
    /// that contradicts a decision this seam has already taken would be
    /// two judges disagreeing about one call.
    let registryRole (author: ModelTransitionAuthor) : TeamRole =
        match author with
        | LocalUser(_, role) -> role
        | PeerActor _
        | PolicyVerdict _ -> Owner

/// What an author may drive an artifact INTO — the declared grant, as
/// data (GP 12 rule 3).
///
/// Target statuses rather than edges, deliberately. A grant is a
/// statement about authority ("this peer may approve"), and authority
/// attaches to the decision, not to the state the artifact happened to
/// be in when it was taken. Which edges exist is the lifecycle graph's
/// business and is judged separately, one step earlier.
type ModelTransitionAuthority = {
    /// Stable `ModelArtifactStatus` labels this author may transition
    /// into. **Empty means none, and empty is the default** — a grant
    /// nobody declared is one nobody agreed to (fail closed).
    Targets: Set<string>
}

[<RequireQualifiedAccess>]
module ModelTransitionAuthority =

    /// The grant of an author nobody declared anything for: nothing.
    let none: ModelTransitionAuthority = { Targets = Set.empty }

    /// A grant over a set of target statuses. Labels naming no status
    /// this build knows are DROPPED rather than carried, for the reason
    /// `PeerDataVisibilityLevel.ofLabelOrDefault` reads an unrecognised
    /// level as the narrowest: a word this deployment cannot enforce is
    /// not a grant it can honour, and a grant that appears to carry one
    /// is a lie an operator would read.
    let ofTargets (targets: string seq) : ModelTransitionAuthority = {
        Targets = targets |> Seq.filter (ModelArtifactStatus.parse >> Option.isSome) |> Set.ofSeq
    }

    /// The full lifecycle grant — every status the graph can enter. The
    /// shape a co-located or otherwise fully-trusted author holds.
    let full: ModelTransitionAuthority =
        ofTargets (
            [
                ModelArtifactStatus.Draft
                ModelArtifactStatus.Fitted
                ModelArtifactStatus.Approved
                ModelArtifactStatus.Retired
            ]
            |> List.map ModelArtifactStatus.name
        )

    /// Does this grant admit a transition into `target`?
    let admits (target: ModelArtifactStatus) (authority: ModelTransitionAuthority) : bool =
        Set.contains (ModelArtifactStatus.name target) authority.Targets

    /// The granted labels in ordinal order — what a descriptor publishes
    /// and an operator surface displays, so neither hand-sorts a set.
    let labels (authority: ModelTransitionAuthority) : string list =
        authority.Targets
        |> Set.toList
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

/// Why a lifecycle invocation was refused at the seam.
///
/// A closed set of three, and each names a different thing the author
/// would have to change: the artifact it named, the edge it asked for,
/// or the grant it holds. Every payload is something the author already
/// knows — the key it sent, the statuses involved, its own identity —
/// so refusing discloses nothing.
[<RequireQualifiedAccess>]
type ModelTransitionRefusal =
    /// No artifact with that composite-key hash exists in the scope.
    | UnknownArtifact of artifactKey: string
    /// An edge the lifecycle graph forbids, including a self-transition.
    /// Refused regardless of authority: no grant makes an impossible
    /// edge possible, and telling an author otherwise would send it to
    /// negotiate for nothing.
    | InvalidTransition of artifactKey: string * from: string * target: string
    /// The edge is legal and this author's declared grant does not admit
    /// it. The one refusal in the set whose remedy is a conversation.
    | InsufficientAuthority of artifactKey: string * target: string * author: string

[<RequireQualifiedAccess>]
module ModelTransitionRefusal =

    /// Human-readable one-line description (logs, operator display, and
    /// the attributed audit row). The CASE is the contract; this wording
    /// is not.
    let describe (refusal: ModelTransitionRefusal) : string =
        match refusal with
        | ModelTransitionRefusal.UnknownArtifact artifactKey ->
            $"no model artifact '{artifactKey}' exists in this scope"
        | ModelTransitionRefusal.InvalidTransition(artifactKey, from, target) ->
            $"model artifact '{artifactKey}' cannot move from '{from}' to '{target}'"
        | ModelTransitionRefusal.InsufficientAuthority(artifactKey, target, author) ->
            $"'{author}' is not granted the authority to move model artifact '{artifactKey}' into '{target}'"

/// One invocation of the seam: which artifact, which target, who is
/// asking, and (optionally) why.
type ModelTransitionRequest = {
    /// The artifact's composite-key hash — its addressable id within the
    /// scope (Phase 453 plan D5).
    ArtifactKey: string
    Target: ModelArtifactStatus
    Author: ModelTransitionAuthor
    /// The author's stated reason. `None` is ordinary: a rationale is
    /// evidence, not a gate, and requiring one would only produce a
    /// deployment-wide habit of typing a full stop.
    Rationale: string option
}

/// What an admitted transition RECORDED — the attributed record, echoed
/// to the author and written to the append-only trail.
///
/// Carries the channel and author beside the state change, which is the
/// whole point of the phase: a lifecycle history that says only "moved
/// to Approved by alice" cannot answer whether alice is a person at this
/// deployment, an actor at a counterparty, or a policy.
type ModelTransitionRecord = {
    ArtifactKey: string
    FromStatus: string
    ToStatus: string
    /// `"local"` or `"peer"` — `ModelTransitionChannel.label`.
    Channel: string
    /// `"user"` / `"peer"` / `"policy"` — `ModelTransitionAuthor.kind`.
    AuthorKind: string
    AuthorId: string
    Rationale: string option
    RecordedAt: DateTimeOffset
    /// The artifact version the transition minted. Every transition is a
    /// new immutable version (GP 5), so this is what a caller cites when
    /// it says which record it is looking at.
    Version: int
}

/// The substrate the seam runs over.
///
/// `Audit` is NOT optional, unlike the audit log a peer job fusion
/// carries. A transition trail that a deployment can compose its way out
/// of is not an append-only trail, and the phase's claim is that this
/// one is: a deployment with no interest in audit composes the SDK's own
/// no-op-shaped log, which is a decision it makes explicitly rather than
/// by omitting a field.
type ModelTransitionDeps = {
    Registry: IModelRegistry
    Audit: IAuditLog
    /// Supplied rather than read from the clock so a test can assert a
    /// recorded instant without freezing one globally (GP 12 rule 6 —
    /// precision is declared, and here it is the caller's).
    Now: unit -> DateTimeOffset
}

/// The one entry point every author takes.
[<RequireQualifiedAccess>]
module ModelTransition =

    /// Record the attributed judgment. Written for an admitted AND a
    /// refused transition, so the attributed trail answers "who tried
    /// what" without joining to a row the registry never wrote — a
    /// transition refused here never reaches the registry at all.
    ///
    /// Best-effort in the sense `IAuditLog.Record` already is: the
    /// registry's own write is the primary effect and an audit store
    /// having a bad day must not change the outcome a caller is told.
    let private record
        (deps: ModelTransitionDeps)
        (scopeId: string)
        (request: ModelTransitionRequest)
        (from: string)
        (outcome: Result<ModelTransitionRecord, ModelTransitionRefusal>)
        : Async<unit> =
        async {
            try
                do!
                    deps.Audit.Record(
                        scopeId,
                        ModelArtifactTransitionAttributed {
                            CompositeKeyHash = request.ArtifactKey
                            FromStatus = from
                            ToStatus = ModelArtifactStatus.name request.Target
                            Channel = ModelTransitionChannel.label (ModelTransitionAuthor.channel request.Author)
                            AuthorKind = ModelTransitionAuthor.kind request.Author
                            AuthorId = ModelTransitionAuthor.id request.Author
                            Rationale = request.Rationale |> Option.defaultValue ""
                            Admitted = Result.isOk outcome
                            Refusal =
                                match outcome with
                                | Ok _ -> ""
                                | Error refusal -> ModelTransitionRefusal.describe refusal
                            ScopeId = scopeId
                        }
                    )
            with _ ->
                ()
        }

    /// Judge one invocation against the artifact's current status and the
    /// author's declared grant.
    ///
    /// **Pure and total over its inputs** — no store read, no clock,
    /// nothing ambient — which is what lets a conformance corpus certify
    /// a refusal vector against the SHIPPED function rather than against
    /// a harness's reconstruction of it, and what guarantees that a local
    /// action, a peer invocation and a policy verdict are judged
    /// identically. `current` is `None` for an artifact that does not
    /// exist.
    ///
    /// The order is stated in this file's header. In one line: nothing
    /// can be judged without an artifact, an impossible edge is refused
    /// before an insufficient grant, and the last refusal is the only one
    /// whose remedy is a conversation.
    let judge
        (current: ModelArtifactStatus option)
        (authority: ModelTransitionAuthority)
        (request: ModelTransitionRequest)
        : Result<ModelArtifactStatus, ModelTransitionRefusal> =
        match current with
        | None -> Error(ModelTransitionRefusal.UnknownArtifact request.ArtifactKey)
        | Some from ->
            if not (ModelArtifactStatus.canTransition from request.Target) then
                Error(
                    ModelTransitionRefusal.InvalidTransition(
                        request.ArtifactKey,
                        ModelArtifactStatus.name from,
                        ModelArtifactStatus.name request.Target
                    )
                )
            elif not (ModelTransitionAuthority.admits request.Target authority) then
                Error(
                    ModelTransitionRefusal.InsufficientAuthority(
                        request.ArtifactKey,
                        ModelArtifactStatus.name request.Target,
                        ModelTransitionAuthor.id request.Author
                    )
                )
            else
                Ok from

    /// Judge and (when admitted) perform one lifecycle transition.
    ///
    /// **`authority` is supplied by the caller rather than read from the
    /// author**, and that is the fail-closed half of the design: an
    /// author does not carry its own grant, because a value that told
    /// the seam what it was allowed to do would be a grant the author
    /// controls. The receiving surface resolves the grant from ITS OWN
    /// declarations — a peer's from its binding, a local user's from the
    /// team model, a policy's from its registration — and hands it in.
    /// `ModelTransitionAuthority.none` is the honest value for "nothing
    /// declared", and it admits nothing.
    ///
    /// The order of judgment is stated in this file's header; the short
    /// version is that an impossible edge is refused before an
    /// insufficient grant, so a caller is never sent to negotiate for a
    /// transition that could not happen anyway.
    let invoke
        (deps: ModelTransitionDeps)
        (scopeId: string)
        (authority: ModelTransitionAuthority)
        (request: ModelTransitionRequest)
        : Async<Result<ModelTransitionRecord, ModelTransitionRefusal>> =
        async {
            // Every registry error at THIS point reads as "there is
            // nothing here to judge" — `NotFound` says so directly, and a
            // storage failure leaves the seam with no current status,
            // which is the one input every later check needs. Reporting a
            // storage failure as a distinct refusal class would put a
            // fourth case on a closed wire vocabulary in order to tell a
            // counterparty about this deployment's disk.
            let! current = async {
                match! deps.Registry.Get(scopeId, request.ArtifactKey) with
                | Error _ -> return None
                | Ok artifact -> return Some artifact.Status
            }

            match judge current authority request with
            | Error refusal ->
                let fromLabel =
                    current |> Option.map ModelArtifactStatus.name |> Option.defaultValue ""

                do! record deps scopeId request fromLabel (Error refusal)
                return Error refusal
            | Ok from ->
                let fromLabel = ModelArtifactStatus.name from

                match!
                    deps.Registry.TransitionStatus(
                        scopeId,
                        request.ArtifactKey,
                        request.Target,
                        ModelTransitionAuthor.registryRole request.Author,
                        ModelTransitionAuthor.id request.Author
                    )
                with
                | Error error ->
                    // The registry judged too, and refused. Its
                    // vocabulary is wider than this one, so the
                    // outcome is folded into the class that matches
                    // what it actually said rather than a fourth
                    // case invented to carry it — and the described
                    // reason rides the attributed row, so nothing is
                    // lost from the trail.
                    let refusal =
                        match error with
                        | ModelRegistryError.NotFound -> ModelTransitionRefusal.UnknownArtifact request.ArtifactKey
                        | ModelRegistryError.IllegalTransition(from, target) ->
                            ModelTransitionRefusal.InvalidTransition(
                                request.ArtifactKey,
                                ModelArtifactStatus.name from,
                                ModelArtifactStatus.name target
                            )
                        | _ ->
                            ModelTransitionRefusal.InsufficientAuthority(
                                request.ArtifactKey,
                                ModelArtifactStatus.name request.Target,
                                ModelTransitionAuthor.id request.Author
                            )

                    do! record deps scopeId request fromLabel (Error refusal)
                    return Error refusal
                | Ok updated ->
                    let recorded = {
                        ArtifactKey = request.ArtifactKey
                        FromStatus = fromLabel
                        ToStatus = ModelArtifactStatus.name updated.Status
                        Channel = ModelTransitionChannel.label (ModelTransitionAuthor.channel request.Author)
                        AuthorKind = ModelTransitionAuthor.kind request.Author
                        AuthorId = ModelTransitionAuthor.id request.Author
                        Rationale = request.Rationale
                        RecordedAt = deps.Now()
                        Version = updated.Version
                    }

                    do! record deps scopeId request fromLabel (Ok recorded)
                    return Ok recorded
        }