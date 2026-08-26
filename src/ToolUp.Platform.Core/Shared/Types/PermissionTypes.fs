// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// RBAC permission on a single module. Hierarchy is encoded in
/// `hasPermission` — `Admin` implies `Write` and `Read`; `Write`
/// implies `Read`; `Read` stands alone. Users may be granted any
/// combination; the helpers normalise the hierarchy when checking.
///
/// `SchemaOnly` is the Phase 30d substrate role: the holder may call
/// `IDataCatalog.GetSchema` / `GetSyntheticSample` to see what data
/// exists and iterate against synthetic samples, but every real-row
/// read path is structurally refused. Outside the read-side hierarchy
/// (`SchemaOnly` does NOT imply `Read`, and `Read` does NOT imply
/// `SchemaOnly`) — the two grants describe different access intents
/// and a partner who is given `SchemaOnly` must not silently inherit
/// real-data access by being later granted `Read`.
///
/// `RequireQualifiedAccess` is mandatory because `Admin` collides with
/// `TeamRole.Admin` (different concept: module-level perm vs team
/// membership role). Forcing `ModulePermission.Admin` at call sites
/// keeps the two kinds of admin distinct.
[<RequireQualifiedAccess>]
type ModulePermission =
    /// View module data and call read-only methods. The minimum grant.
    | Read
    /// Read + mutate module data (invoke analysis, upload files,
    /// delete records).
    | Write
    /// Read + Write + module-scoped administrative actions (configure
    /// module defaults, manage per-module resources). Does not imply
    /// team-scope admin — that's `TeamRole.Admin`.
    | Admin
    /// Phase 30d — partner-sandbox grant. The holder can call
    /// `IDataCatalog.GetSchema` + `GetSyntheticSample` to discover data
    /// shapes and iterate against deterministically-generated synthetic
    /// rows, but every path that would return a real-row blob is
    /// refused with a `SchemaOnlyAccessAttempted` audit event. Does
    /// NOT imply `Read` — a partner who acquires real-data access must
    /// be granted `Read` explicitly. Intended for federated cross-
    /// instance partner tenants and any deployment that wants to expose
    /// "what data exists" without exposing "what's in it".
    | SchemaOnly

module ModulePermission =
    /// Does holding `granted` satisfy a requirement of `required`?
    /// Encodes the Read / Write / Admin hierarchy plus the
    /// Phase 30d `SchemaOnly` carve-out. `Admin` / `Write` / `Read` all
    /// satisfy a `SchemaOnly` requirement (more authority covers less —
    /// any real-data reader can trivially see schemas + synthetic
    /// samples). The reverse is structurally blocked: `SchemaOnly` does
    /// NOT satisfy `Read` / `Write` / `Admin`, so a partner whose only
    /// grant is `SchemaOnly` cannot inherit real-data access.
    let implies (granted: ModulePermission) (required: ModulePermission) =
        match granted, required with
        | ModulePermission.Admin, _ -> true
        | ModulePermission.Write, ModulePermission.Write
        | ModulePermission.Write, ModulePermission.Read
        | ModulePermission.Write, ModulePermission.SchemaOnly -> true
        | ModulePermission.Read, ModulePermission.Read
        | ModulePermission.Read, ModulePermission.SchemaOnly -> true
        | ModulePermission.SchemaOnly, ModulePermission.SchemaOnly -> true
        | _ -> false

/// Phase 551 — an opaque reference to a party whose approval a module's
/// declared `GrantPolicy` requires. A stable string the deployment
/// resolves (a tenant id, a DPO mailbox, a regulator handle); the SDK
/// never interprets it, mirroring the `PolicyRef` shape. GP 9 — the SDK
/// never names a module or a party; both are supplied at registration.
type PartyRef = PartyRef of string

module PartyRef =
    /// The underlying string. Never `null` for a ref built through
    /// `create`; a hand-constructed `PartyRef null` reads as `""`.
    let value (PartyRef raw) = if isNull (box raw) then "" else raw

    /// Build a ref from a raw string, trimming surrounding whitespace so
    /// `" acme "` and `"acme"` are the same party. Deliberately does NOT
    /// case-fold — a party reference is an opaque deployment identifier
    /// and the SDK is not entitled to decide its equality relation
    /// beyond stripping the whitespace a form field adds.
    let create (raw: string) =
        PartyRef(if isNull (box raw) then "" else raw.Trim())

    /// True when the ref names nobody. An unnameable counterparty is a
    /// fail-closed state, never a fall-through to admin discretion.
    let isEmpty (p: PartyRef) = value p = ""

/// Phase 551 — a module's **declared** precondition on being granted to
/// anyone. The admin-authored `ModulePermissions` map remains the
/// authority on *who* is granted; this is the module's own voice on
/// *what must be true first*, enforced fail-closed at dispatch as well
/// as at the grant write (the Phase 311 lesson: a gate the write path
/// must remember to call is a defect class, not a control).
///
/// **Narrowing-only.** Composition may tighten a module's declared
/// policy and never loosen it (D15) — `GrantPolicy.tighten` is the only
/// combinator, and `ServerModule.withGrantPolicy` refuses a loosening at
/// compose time rather than at first request.
///
/// **`AdminDiscretion` is byte-identical to today** (GP 11 / GP 13): a
/// module that declares nothing carries it, no registry is composed, no
/// grant records are loaded, and every path behaves exactly as it did
/// before this type existed.
///
/// Ordered by strictness — `AdminDiscretion` < `RequiresAcknowledgement`
/// < `RequiresSubjectConsent` < `RequiresCounterpartyApproval` — see
/// `GrantPolicy.strictness` for why that order is the one the estate
/// means, and note that two `RequiresCounterpartyApproval` arms naming
/// DIFFERENT parties are incomparable rather than equal.
[<RequireQualifiedAccess>]
type GrantPolicy =
    /// The pre-551 default. An administrator's grant stands on its own;
    /// no additional artifact is demanded at write or at dispatch.
    | AdminDiscretion
    /// The granting administrator must confirm the grant explicitly and
    /// record a justification. Ceremony on the admin side only — no
    /// third party is consulted, so the grant is live immediately.
    | RequiresAcknowledgement
    /// The grantee must accept before the grant carries authority. The
    /// write records the grant as `PendingConsent`; it is INERT at
    /// dispatch until the subject accepts.
    | RequiresSubjectConsent
    /// A named counterparty must approve. Until the consent store ships
    /// (Phase 552) this arm refuses every grant at write AND treats every
    /// grant row as inert at dispatch — conservatively correct rather
    /// than optimistically permissive.
    | RequiresCounterpartyApproval of PartyRef

/// Phase 551 — the lifecycle state of a recorded grant. A grant under
/// `AdminDiscretion` needs no record at all; the states below exist for
/// grants written under a stricter declared policy.
[<RequireQualifiedAccess>]
type GrantState =
    /// The declared precondition was satisfied; the grant carries
    /// authority.
    | Active
    /// Recorded, awaiting the grantee's acceptance
    /// (`RequiresSubjectConsent`). Present in the document and visible to
    /// an admin, but inert at dispatch.
    | PendingConsent

module GrantState =
    /// Stable wire token for persistence + audit.
    let toToken =
        function
        | GrantState.Active -> "active"
        | GrantState.PendingConsent -> "pending-consent"

    /// Parse a persisted token. An unrecognised token reads as
    /// `PendingConsent` — the inert state — never `Active`: a state this
    /// node cannot interpret must not be the one that confers authority.
    let ofToken (token: string) =
        match
            (if isNull (box token) then
                 ""
             else
                 token.Trim().ToLowerInvariant())
        with
        | "active" -> GrantState.Active
        | _ -> GrantState.PendingConsent

/// Phase 551 — the evidence that a module's declared `GrantPolicy` was
/// satisfied for one (subject, module) grant. Persisted alongside the
/// permission entry it qualifies, and re-read at dispatch: a permission
/// entry without an adequate record is inert.
///
/// `SatisfiedPolicy` records the policy that was actually met, not the
/// one in force now. That distinction is load-bearing: a module may
/// TIGHTEN its declared policy after grants exist, and a record whose
/// evidence was gathered under the looser policy must stop satisfying
/// the stricter one rather than being grandfathered.
type ModuleGrantRecord = {
    State: GrantState
    /// The policy this record's evidence satisfied at write time.
    SatisfiedPolicy: GrantPolicy
    /// The granting administrator's recorded reason. Non-empty whenever
    /// `SatisfiedPolicy` is at least `RequiresAcknowledgement`.
    Justification: string
    /// The subject who accepted, once they have
    /// (`RequiresSubjectConsent`). `None` while `PendingConsent`.
    ConsentedBy: string option
}

/// Phase 551 — a typed refusal from the grant-policy guard. Every arm
/// names the module and the policy that refused, so a caller can render
/// an actionable message without parsing prose, and an audit row carries
/// the same discriminator an operator dashboards on.
[<RequireQualifiedAccess>]
type GrantRefusal =
    /// The module declares `RequiresAcknowledgement` (or stricter) and
    /// the write carried no explicit confirmation.
    | AcknowledgementRequired of moduleName: string * policy: GrantPolicy
    /// Confirmation was given but no justification text accompanied it.
    | JustificationRequired of moduleName: string * policy: GrantPolicy
    /// The module declares `RequiresCounterpartyApproval` and no consent
    /// store is composed to satisfy it (Phase 552).
    | CounterpartyApprovalUnavailable of moduleName: string * party: PartyRef
    /// A permission entry exists for a policy-bearing module with no
    /// adequate grant record — the shape a row injected directly into the
    /// store takes.
    | UnbackedGrant of moduleName: string * policy: GrantPolicy
    /// Composition attempted to replace a declared policy with a weaker
    /// one.
    | PolicyLoosening of moduleName: string * declared: GrantPolicy * attempted: GrantPolicy
    /// Two `RequiresCounterpartyApproval` declarations naming different
    /// parties. Incomparable — neither narrows the other, so the estate
    /// refuses rather than silently picking one.
    | ConflictingCounterparty of moduleName: string * declared: PartyRef * attempted: PartyRef

/// Phase 551 — what a policy-satisfying grant write actually did.
[<RequireQualifiedAccess>]
type GrantWriteOutcome =
    /// The grant is live.
    | Granted
    /// Recorded and awaiting the named subject's acceptance. The
    /// permission entry exists but confers nothing until then.
    | RecordedPendingConsent of subjectId: string

module GrantPolicy =
    /// Rank by strictness. Used for the narrowing-only rule and for the
    /// "was the evidence at least as strong as what is demanded now"
    /// comparison at dispatch.
    ///
    /// The order is `AdminDiscretion` < `RequiresAcknowledgement` <
    /// `RequiresSubjectConsent` < `RequiresCounterpartyApproval` because
    /// each step adds a party who must act: nobody, then the admin
    /// themselves, then the grantee, then an outsider. It is NOT a claim
    /// that a counterparty's approval implies the subject's — the ranks
    /// order *how hard the precondition is to satisfy*, which is what the
    /// narrowing rule is about.
    let strictness =
        function
        | GrantPolicy.AdminDiscretion -> 0
        | GrantPolicy.RequiresAcknowledgement -> 1
        | GrantPolicy.RequiresSubjectConsent -> 2
        | GrantPolicy.RequiresCounterpartyApproval _ -> 3

    /// Stable wire token for persistence + audit. The counterparty arm
    /// carries its party after a `:` so one token round-trips the whole
    /// value.
    let toToken =
        function
        | GrantPolicy.AdminDiscretion -> "admin-discretion"
        | GrantPolicy.RequiresAcknowledgement -> "requires-acknowledgement"
        | GrantPolicy.RequiresSubjectConsent -> "requires-subject-consent"
        | GrantPolicy.RequiresCounterpartyApproval party -> "requires-counterparty-approval:" + PartyRef.value party

    /// The strictest arm this node can CONSTRUCT from a token that
    /// carries no party reference — the fail-closed landing point for an
    /// unrecognised policy token (see `ofToken`).
    let strictestConstructible = GrantPolicy.RequiresSubjectConsent

    /// Parse a persisted policy token, **fail-closed**: an unrecognised
    /// token never reads as `AdminDiscretion`.
    ///
    /// Two unrecognised shapes are distinguished deliberately, because
    /// they mean different things:
    ///
    /// * A **wholly unknown** token — plausibly an arm a newer deployment
    ///   writes — lands on `strictestConstructible`
    ///   (`RequiresSubjectConsent`). Landing on the counterparty arm
    ///   instead would refuse every grant on the module permanently,
    ///   because the token carried no party to name and a fabricated one
    ///   is a worse answer than a strict one: it would put a counterparty
    ///   who does not exist into an audit record. Subject consent is the
    ///   strictest precondition an operator on THIS node can actually
    ///   satisfy.
    /// * A **known counterparty arm with a missing party** keeps the
    ///   counterparty arm with an empty ref, which nothing can satisfy.
    ///   Here the arm IS recognised, so downgrading it would be a
    ///   loosening on the strength of corruption — the same posture
    ///   `PermissionStore` takes on an unparseable document.
    let ofToken (token: string) =
        let normalised =
            if isNull (box token) then
                ""
            else
                token.Trim().ToLowerInvariant()

        if normalised.StartsWith "requires-counterparty-approval:" then
            GrantPolicy.RequiresCounterpartyApproval(
                PartyRef.create (normalised.Substring "requires-counterparty-approval:".Length)
            )
        else
            match normalised with
            | "admin-discretion" -> GrantPolicy.AdminDiscretion
            | "requires-acknowledgement" -> GrantPolicy.RequiresAcknowledgement
            | "requires-subject-consent" -> GrantPolicy.RequiresSubjectConsent
            | "requires-counterparty-approval" ->
                // The arm is recognised; the party is not there. Keep the
                // arm — an unnameable counterparty refuses everything.
                GrantPolicy.RequiresCounterpartyApproval(PartyRef.create "")
            | _ -> strictestConstructible

    /// Does `candidate` narrow (or equal) `declared`? Narrowing-only
    /// composition admits exactly the candidates for which this is true.
    /// Two counterparty arms naming different parties are incomparable
    /// and therefore NOT a narrowing — `tighten` reports them.
    let isNarrowing (declared: GrantPolicy) (candidate: GrantPolicy) =
        match declared, candidate with
        | GrantPolicy.RequiresCounterpartyApproval a, GrantPolicy.RequiresCounterpartyApproval b -> a = b
        | _ -> strictness candidate >= strictness declared

    /// Combine a declared policy with a composition-supplied one,
    /// admitting only a narrowing. Returns the stricter of the two, or a
    /// typed refusal naming the module.
    let tighten (moduleName: string) (declared: GrantPolicy) (candidate: GrantPolicy) =
        match declared, candidate with
        | GrantPolicy.RequiresCounterpartyApproval a, GrantPolicy.RequiresCounterpartyApproval b when a <> b ->
            Error(GrantRefusal.ConflictingCounterparty(moduleName, a, b))
        | _ when isNarrowing declared candidate -> Ok candidate
        | _ -> Error(GrantRefusal.PolicyLoosening(moduleName, declared, candidate))

    /// **The dispatch predicate.** Does a permission entry on a module
    /// declaring `policy` actually carry authority, given the grant
    /// record persisted for it (if any)?
    ///
    /// * `AdminDiscretion` — always true, record or no record. This is
    ///   the byte-for-byte-as-today arm and it is checked FIRST so a
    ///   deployment that declares nothing never reaches the rest.
    /// * `RequiresCounterpartyApproval` — always false until Phase 552
    ///   composes `IGrantConsentStore`. An `Active` record claiming to
    ///   satisfy it is not trusted: no path can legitimately have written
    ///   one, so its presence is evidence of injection, not of consent.
    /// * otherwise — the record must exist, be `Active`, and its
    ///   `SatisfiedPolicy` must be at least as strict as what is declared
    ///   now, so a module that tightened its policy invalidates the
    ///   grants written under the looser one instead of grandfathering
    ///   them.
    let isGrantLive (policy: GrantPolicy) (record: ModuleGrantRecord option) =
        match policy with
        | GrantPolicy.AdminDiscretion -> true
        | GrantPolicy.RequiresCounterpartyApproval _ -> false
        | _ ->
            match record with
            | None -> false
            | Some r -> r.State = GrantState.Active && strictness r.SatisfiedPolicy >= strictness policy

    /// Short stable label for the reason a grant is not live. Emitted on
    /// the dispatch-refusal audit row so an operator can separate "no
    /// record at all" (an injected row) from "waiting on the subject"
    /// (an ordinary pending grant) without joining to the document.
    let inertReason (policy: GrantPolicy) (record: ModuleGrantRecord option) =
        match policy, record with
        | GrantPolicy.AdminDiscretion, _ -> "live"
        | GrantPolicy.RequiresCounterpartyApproval _, _ -> "counterparty-approval-unavailable"
        | _, None -> "no-grant-record"
        | _, Some r when r.State <> GrantState.Active -> "awaiting-subject-consent"
        | _, Some _ -> "evidence-below-declared-policy"

module GrantRefusal =
    /// Human-readable rendering, prefixed with a stable machine-greppable
    /// code. Interface members that return `Result<unit, string>` carry
    /// this; callers that want the typed value use the guard's own entry
    /// points, which never stringify.
    let describe =
        function
        | GrantRefusal.AcknowledgementRequired(m, p) ->
            $"GRANT-POLICY-ACK-REQUIRED: module '{m}' declares '{GrantPolicy.toToken p}'; the grant must carry an explicit acknowledgement."
        | GrantRefusal.JustificationRequired(m, p) ->
            $"GRANT-POLICY-JUSTIFICATION-REQUIRED: module '{m}' declares '{GrantPolicy.toToken p}'; the grant must carry a justification."
        | GrantRefusal.CounterpartyApprovalUnavailable(m, party) ->
            $"GRANT-POLICY-COUNTERPARTY-UNAVAILABLE: module '{m}' requires approval from party '{PartyRef.value party}' and no consent store is composed."
        | GrantRefusal.UnbackedGrant(m, p) ->
            $"GRANT-POLICY-UNBACKED-GRANT: module '{m}' declares '{GrantPolicy.toToken p}' but the written permission entry carries no adequate grant record."
        | GrantRefusal.PolicyLoosening(m, declared, attempted) ->
            $"GRANT-POLICY-LOOSENING: module '{m}' declares '{GrantPolicy.toToken declared}'; '{GrantPolicy.toToken attempted}' would loosen it (narrowing-only)."
        | GrantRefusal.ConflictingCounterparty(m, declared, attempted) ->
            $"GRANT-POLICY-CONFLICTING-COUNTERPARTY: module '{m}' already requires approval from '{PartyRef.value declared}'; '{PartyRef.value attempted}' neither narrows nor equals it."

    /// The stable discriminator an audit row and an operator dashboard
    /// group by.
    let code =
        function
        | GrantRefusal.AcknowledgementRequired _ -> "acknowledgement-required"
        | GrantRefusal.JustificationRequired _ -> "justification-required"
        | GrantRefusal.CounterpartyApprovalUnavailable _ -> "counterparty-approval-unavailable"
        | GrantRefusal.UnbackedGrant _ -> "unbacked-grant"
        | GrantRefusal.PolicyLoosening _ -> "policy-loosening"
        | GrantRefusal.ConflictingCounterparty _ -> "conflicting-counterparty"

/// Per-team, per-module **exposure** state — the tri-state behind the
/// team-management "module exposure" control. Orthogonal to the RBAC
/// permission maps: exposure governs *whether the module is offered to
/// the team at all*; permission governs *what a member may do once it
/// is*. Absence from `TeamPermissions.Exposure` ⇒ `Available` (the
/// default, so a brand-new team and every pre-exposure persisted
/// document show every module).
///
/// `Available` and `Hidden` remain pure visibility/navigation states
/// (NOT an authorization boundary — the per-route permission guard
/// `canAccessModule` / `hasPermission` is the enforcement). `Hidden`
/// preserves the legacy "Expose in team" off behaviour: the module
/// leaves the sidebar + Home but its data types stay mappable.
/// `Unavailable` is the stronger *clearance* state: the module leaves
/// the sidebar + Home AND its data types are no longer offered for
/// mapping ("this team isn't cleared to use it").
///
/// Extensible — a future "visible-but-locked upsell" state is simply a
/// 4th case rather than a second orthogonal flag.
[<RequireQualifiedAccess>]
type ModuleExposure =
    /// Default. In the sidebar + Home; data types mappable.
    | Available
    /// Cosmetically hidden — off the sidebar + Home, but data types
    /// stay mappable. The legacy "Expose in team" off state.
    | Hidden
    /// Not cleared for this team — off the sidebar + Home, AND its data
    /// types are refused for mapping/detection. Upload still succeeds.
    | Unavailable

module ModuleExposure =
    /// Stable wire token for persistence + audit. `Available` is never
    /// serialised (absence ⇒ Available), so it has no token of its own
    /// on the write path; included here for completeness.
    let toToken =
        function
        | ModuleExposure.Available -> "available"
        | ModuleExposure.Hidden -> "hidden"
        | ModuleExposure.Unavailable -> "unavailable"

    /// Parse a persisted/legacy token. Unknown tokens fall back to the
    /// safe-but-visible `Available` default rather than throwing — a
    /// malformed document must not strand a team with no modules.
    let ofToken (token: string) =
        match token.Trim().ToLowerInvariant() with
        | "hidden" -> ModuleExposure.Hidden
        | "unavailable" -> ModuleExposure.Unavailable
        | _ -> ModuleExposure.Available

    /// Whether the module is shown in the sidebar + Home. Only
    /// `Available` is exposed; `Hidden` and `Unavailable` are not.
    let isExposed =
        function
        | ModuleExposure.Available -> true
        | ModuleExposure.Hidden
        | ModuleExposure.Unavailable -> false

    /// Whether the module's data types may be mapped/detected. Both
    /// `Available` and `Hidden` are mappable; only `Unavailable` blocks.
    let isMappable =
        function
        | ModuleExposure.Available
        | ModuleExposure.Hidden -> true
        | ModuleExposure.Unavailable -> false

/// Persisted per-team permission document. One per team, stored under
/// `_platform/permissions/{teamId}.json` by the blob-backed
/// `PermissionStore`.
///
/// `Defaults` are applied when a member has no explicit per-module
/// entry. `Members` maps userId → moduleName → permissions.
/// Effective permissions for a user on a module: `Members[userId][module]`
/// if present, else `Defaults[module]`, else no access.
///
/// `Exposure` is the **per-team module-exposure** axis (see
/// `ModuleExposure`), orthogonal to the permission maps above. A module
/// absent from the map is `Available`; entries record the non-default
/// `Hidden` / `Unavailable` states. A `Hidden` / `Unavailable` module
/// is removed from the team's sidebar + Home for every member (and for
/// a platform admin acting on the team), regardless of permission
/// level; `Unavailable` additionally blocks data mapping. Exposure is a
/// navigation/availability concern, NOT the per-route authorization
/// boundary.
///
/// Lives in the shared compilation layer because the client-facing
/// `PlatformApi` exposes it — team admins read and edit it from the
/// team-management UI.
type TeamPermissions = {
    Defaults: Map<string, ModulePermission list>
    Members: Map<string, Map<string, ModulePermission list>>
    /// Per-module exposure state. A module absent from the map is
    /// `Available` (default); entries hold the non-default `Hidden` /
    /// `Unavailable` states. See the `ModuleExposure` doc for the
    /// exposure-vs-permission distinction.
    Exposure: Map<string, ModuleExposure>
    /// Phase 551 — grant-policy evidence, keyed `userId → moduleName →
    /// record`, mirroring `Members`. Populated ONLY for grants written
    /// against a module whose declared `GrantPolicy` is stricter than
    /// `AdminDiscretion`; an empty map is the whole of every pre-551
    /// document and is byte-identical to today (GP 11).
    ///
    /// A `Members` entry with no corresponding record here is exactly the
    /// shape a row injected straight into the store takes, which is why
    /// dispatch re-derives liveness from this map rather than trusting
    /// `Members` alone (Phase 311 lesson — write-path-only enforcement is
    /// insufficient).
    Grants: Map<string, Map<string, ModuleGrantRecord>>
}

module TeamPermissions =
    let empty = {
        Defaults = Map.empty
        Members = Map.empty
        Exposure = Map.empty
        Grants = Map.empty
    }

    /// The grant records recorded for one subject, module-keyed. Empty
    /// when the subject has none — the pre-551 shape.
    let grantsFor (userId: string) (perms: TeamPermissions) : Map<string, ModuleGrantRecord> =
        perms.Grants |> Map.tryFind userId |> Option.defaultValue Map.empty