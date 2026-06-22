// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Consent.ConsentStateStore

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

// ─── Phase 159 — durable per-subject consent persistence ───────────
//
// Phase 59 shipped the client-side `IConsentProvider` seam (consent
// decisions are browser-local). Phase 159 adds the *server-side*
// durable counterpart: a per-subject consent record that survives a
// process restart and is scoped per subject (GP 4), so a deployment
// that needs demonstrable, durable evidence of a subject's consent
// (a regulated public-utility app) can read it back authoritatively
// rather than trusting the browser.
//
// Two reference implementations ship:
//   * `InMemoryConsentStateStore` — concurrent-dictionary, single-
//     instance, dev / test. Clearly marked dev-only: state does NOT
//     survive a restart.
//   * `EntityBackedConsentStateStore` — over `IEntityStore` (Phase
//     19). Durable across restart, scope-isolated by construction
//     (every method derives its blob path from `scopeId`). This is
//     the production path the acceptance criterion validates.
//
// Six-rule portability audit (GP 12 / Phase 9c). Any distributed
// task-framework implementation of `IConsentStateStore` must satisfy
// all six:
//   1. Identity by value. The subject key is a `string` (the
//      browser-local `AnonymousUserId` until Phase 62 establishes an
//      authenticated-user seam) and the scope key is a `string` —
//      never a live handle.
//   2. Async at every boundary. Every method returns `Async<_>`.
//   3. Retry + supervision as data. No callback-typed `OnFailure` —
//      errors flow back through `Result<_, string>`; the caller
//      decides retry. Audit emission is fire-and-forget (it must
//      never fail the consent write — the decision is the user's and
//      is already captured).
//   4. Stateless handlers between invocations. The entity-backed impl
//      holds no in-memory state between calls — every `Upsert` /
//      `Get` derives its result from `IEntityStore` + parameters. A
//      grain that deactivates between two calls behaves identically.
//   5. No cross-shard ordering promises. Versions are linear within a
//      single `(subject, scope)` tuple (the `IEntityStore` version
//      counter); ordering across subjects/scopes is not promised.
//   6. Precision at the lower bound. `GrantedAt` is a
//      `DateTimeOffset` — second precision is the contract floor; no
//      sub-second ordering is promised.
//
// Contract-pack binding:
// `ToolUp.Platform.Tests.Contracts.IConsentStateStoreContract` drives
// the portable surface (round-trip + per-subject isolation) against
// both implementations.

/// How a stored consent decision was captured. Recorded alongside the
/// decision so an audit reviewer can tell a banner interaction from a
/// CMP-bridge read from a programmatic grant.
type ConsentSource =
    /// The user toggled categories in the SDK `ConsentBanner` and
    /// saved.
    | BannerInteraction
    /// A third-party Consent-Management-Platform reported the decision
    /// (read via `IConsentManagementBridge`); `provider` is the CMP
    /// name.
    | CmpDecision of provider: string
    /// A server-side / operator path set the decision directly (e.g.
    /// a migration, an admin tool). No user interaction.
    | ProgrammaticGrant

/// Durable per-subject consent record. Carries the three required
/// `IEntityStore` fields (`Id` = the subject key, `Type` =
/// `"ConsentRecord"`, `Version` = store-assigned) plus the consent
/// payload. Immutable (GP 5) — every state transition produces a new
/// record the store re-saves.
type ConsentRecord = {
    /// Entity id — the subject key. Equals `Subject` (the store keys
    /// records by subject, one head record per subject per scope).
    Id: EntityId
    /// Entity-type discriminator. Always `"ConsentRecord"`.
    Type: string
    /// Store-assigned monotonic version. Overwritten on every save.
    Version: int
    /// The subject the consent belongs to — the browser-local
    /// `AnonymousUserId` (or, post-Phase-62, an authenticated-user
    /// id). Identity by value (rule 1).
    Subject: string
    /// Categories the subject has granted.
    Granted: Set<ConsentCategory>
    /// Categories the subject has explicitly denied.
    Denied: Set<ConsentCategory>
    /// When the decision was last updated.
    GrantedAt: DateTimeOffset
    /// How the decision was captured.
    Source: ConsentSource
}

module ConsentRecord =
    /// Stable entity-type discriminator — joined on by audit / DSAR
    /// tooling. Do not rename without a migration.
    [<Literal>]
    let entityType = "ConsentRecord"

    /// Build a record for `subject` granting `granted` (and denying
    /// the rest of the supplied `decided` set). `Necessary` is always
    /// granted — it is the strictly-required category and cannot be
    /// denied (mirrors `ConsentState.initial`).
    let create
        (subject: string)
        (granted: Set<ConsentCategory>)
        (denied: Set<ConsentCategory>)
        (at: DateTimeOffset)
        (source: ConsentSource)
        : ConsentRecord =
        {
            Id = subject
            Type = entityType
            Version = 0
            Subject = subject
            Granted = Set.add Necessary granted
            Denied = Set.remove Necessary denied
            GrantedAt = at
            Source = source
        }

    /// `IEntityStore` registration for the consent record. No secondary
    /// indexes — lookup is by subject (= entity id) via `Get`, the only
    /// access pattern the store needs.
    let registration: EntityRegistration<ConsentRecord> =
        EntityRegistration.create<ConsentRecord> entityType

// ─── Store interface ──────────────────────────────────────────────

/// Durable per-subject consent store. Scope-isolated by construction
/// (every method takes `scopeId`); six-rule-portable (see file
/// header).
type IConsentStateStore =
    /// Idempotent upsert of a subject's consent record. Overwrites the
    /// subject's prior decision wholesale (consent is a current-state
    /// concept, not an append log — the history is recoverable via the
    /// underlying `IEntityStore` version chain). Emits a
    /// `Custom:ConsentGranted` audit event. Returns the stored record
    /// (with its assigned version) or a diagnostic string on failure.
    abstract Upsert: scopeId: string * record: ConsentRecord -> Async<Result<ConsentRecord, string>>

    /// Read a subject's current consent record, or `None` when the
    /// subject has no stored decision in this scope.
    abstract Get: scopeId: string * subject: string -> Async<ConsentRecord option>

    /// Withdraw consent for the given categories (moves them from
    /// `Granted` to `Denied`; `Necessary` is never withdrawable).
    /// Idempotent — withdrawing a never-granted category is a no-op.
    /// Returns `Error` when the subject has no stored record. Emits a
    /// `Custom:ConsentWithdrawn` audit event.
    abstract Withdraw:
        scopeId: string * subject: string * categories: ConsentCategory list -> Async<Result<ConsentRecord, string>>

// ─── Audit emission (Custom: vocabulary — no new AuditEvent case) ──
//
// Consent changes are audit-worthy (GP 6). Rather than add a new
// `AuditEvent` DU case (which would force Phase 114 registry
// coordination + a wire-format bump), the store reuses the
// open-vocabulary `RemotingMethodAudited` row with a
// `Kind = "Custom:ConsentGranted"` / `"Custom:ConsentWithdrawn"`
// discriminator — the same `Custom:<name>` convention the
// `[<Audit "Custom:...">]` dispatcher path emits. Fire-and-forget per
// the `IAuditLog` contract: a failed audit write never fails the
// consent write (rule 3).

module private ConsentAudit =
    [<Literal>]
    let sourceModule = "_platform.consent"

    let private categoryList (cs: Set<ConsentCategory>) : string =
        cs |> Set.toList |> List.map string |> String.concat ","

    let emit (auditLog: IAuditLog option) (scopeId: string) (kind: string) (record: ConsentRecord) : unit =
        match auditLog with
        | None -> ()
        | Some log ->
            let payload =
                Map [
                    "subject", record.Subject
                    "granted", categoryList record.Granted
                    "denied", categoryList record.Denied
                    "source", string record.Source
                ]

            let event =
                AuditEvent.RemotingMethodAudited {
                    Kind = kind
                    MethodName = "ConsentStateStore.Upsert"
                    SubjectId = record.Subject
                    CorrelationId = None
                    Payload = payload
                }

            // Fire-and-forget — never block / fail the consent write.
            log.Record(scopeId, event) |> Async.Start

// ─── Withdrawal helper (shared by both impls) ─────────────────────

module private Withdrawal =
    /// Apply a withdrawal to a record: move every withdrawable category
    /// from `Granted` to `Denied`. `Necessary` is never withdrawable.
    let apply (categories: ConsentCategory list) (at: DateTimeOffset) (record: ConsentRecord) : ConsentRecord =
        let toWithdraw = categories |> List.filter (fun c -> c <> Necessary) |> Set.ofList

        {
            record with
                Granted = Set.difference record.Granted toWithdraw
                Denied = Set.union record.Denied toWithdraw
                GrantedAt = at
        }

// ─── In-memory implementation (dev / test only) ───────────────────

/// Dev-only single-instance store. State lives in a concurrent
/// dictionary keyed by `(scopeId, subject)` — it does NOT survive a
/// process restart. Production deployments use
/// `EntityBackedConsentStateStore`. Scope isolation is enforced by the
/// composite key (rule 4 / GP 4): a read for `(scopeA, subject)` can
/// never observe `(scopeB, subject)`.
type InMemoryConsentStateStore(?auditLog: IAuditLog) =
    let store = ConcurrentDictionary<string * string, ConsentRecord>()
    let auditLog = auditLog

    interface IConsentStateStore with
        member _.Upsert(scopeId, record) = async {
            let key = (scopeId, record.Subject)

            let nextVersion =
                match store.TryGetValue key with
                | true, prev -> prev.Version + 1
                | _ -> 1

            let stored = {
                record with
                    Id = record.Subject
                    Type = ConsentRecord.entityType
                    Version = nextVersion
            }

            store[key] <- stored
            ConsentAudit.emit auditLog scopeId "Custom:ConsentGranted" stored
            return Ok stored
        }

        member _.Get(scopeId, subject) = async {
            match store.TryGetValue((scopeId, subject)) with
            | true, record -> return Some record
            | _ -> return None
        }

        member _.Withdraw(scopeId, subject, categories) = async {
            match store.TryGetValue((scopeId, subject)) with
            | true, prev ->
                let withdrawn =
                    prev
                    |> Withdrawal.apply categories DateTimeOffset.UtcNow
                    |> fun r -> { r with Version = prev.Version + 1 }

                store[(scopeId, subject)] <- withdrawn
                ConsentAudit.emit auditLog scopeId "Custom:ConsentWithdrawn" withdrawn
                return Ok withdrawn
            | _ -> return Error(sprintf "No consent record for subject %s in scope %s" subject scopeId)
        }

// ─── Entity-backed implementation (production, durable) ───────────

/// Durable store over `IEntityStore`. Survives restart and is
/// scope-isolated by construction (the entity store derives its blob
/// container from `scopeId`). Stateless between calls (rule 4) — every
/// operation reads/writes through `IEntityStore` with no in-memory
/// cache. Requires the `ConsentRecord.registration` entity type to be
/// registered with the entity store (the compose path prepends it when
/// `ServerConfig.ConsentStateStore = EntityBackedConsentStateStore`).
type EntityBackedConsentStateStore(entityStore: IEntityStore, ?auditLog: IAuditLog) =
    let auditLog = auditLog

    interface IConsentStateStore with
        member _.Upsert(scopeId, record) = async {
            let toSave = {
                record with
                    Id = record.Subject
                    Type = ConsentRecord.entityType
            }

            let! result = entityStore.Save<ConsentRecord>(scopeId, toSave)

            match result with
            | Ok ref ->
                let stored = { toSave with Version = ref.Version }
                ConsentAudit.emit auditLog scopeId "Custom:ConsentGranted" stored
                return Ok stored
            | Error e -> return Error(EntityError.message e)
        }

        member _.Get(scopeId, subject) = async {
            let! result = entityStore.Get<ConsentRecord>(scopeId, ConsentRecord.entityType, subject)

            match result with
            | Ok record -> return Some record
            | Error _ -> return None
        }

        member _.Withdraw(scopeId, subject, categories) = async {
            let! existing = entityStore.Get<ConsentRecord>(scopeId, ConsentRecord.entityType, subject)

            match existing with
            | Error e -> return Error(EntityError.message e)
            | Ok prev ->
                let withdrawn = Withdrawal.apply categories DateTimeOffset.UtcNow prev
                let! saved = entityStore.Save<ConsentRecord>(scopeId, withdrawn)

                match saved with
                | Ok ref ->
                    let stored = { withdrawn with Version = ref.Version }
                    ConsentAudit.emit auditLog scopeId "Custom:ConsentWithdrawn" stored
                    return Ok stored
                | Error e -> return Error(EntityError.message e)
        }

module ConsentStateStore =
    /// Construct the in-memory dev-only store.
    let inMemory (auditLog: IAuditLog option) : IConsentStateStore =
        match auditLog with
        | Some log -> InMemoryConsentStateStore(log) :> _
        | None -> InMemoryConsentStateStore() :> _

    /// Construct the durable entity-backed store over an `IEntityStore`.
    let entityBacked (entityStore: IEntityStore) (auditLog: IAuditLog option) : IConsentStateStore =
        match auditLog with
        | Some log -> EntityBackedConsentStateStore(entityStore, log) :> _
        | None -> EntityBackedConsentStateStore(entityStore) :> _