// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Preflight guard + `/dev/inspect` panel over the composed
/// `IEventStore` decorator chain.
module ToolUp.Platform.EventStoreChainValidator

open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 9u — the event-store decorator chain guard ────────────────
//
// A miswired decorator chain is invisible at every layer that could
// otherwise catch it. Each decorator is a faithful `IEventStore`, so a
// chain stacked in the wrong order persists, reads, type-checks and
// health-checks exactly as a correct one does. What it does differently
// is drop hooks — the audit replicator stops seeing events that a webhook
// dispatch hook handled first, or an `OnEvent` job fires before the
// dispatch it was meant to follow — silently, for the life of the
// deployment. Nobody reports a dropped hook; the subsystem simply looks
// idle, which is the same failure shape the trace-category guard next
// door was written for.
//
// This validator walks the composed chain (`EventStoreChain.describe`,
// which follows `IEventStoreDecorator.InnerStore` from the outside in)
// and checks three things about what it actually finds:
//
//   1. **No position conflict.** Two distinct decorators claiming one
//      position have no defined relative order, so the chain they form
//      is whatever the construction sites happened to do. Refusal names
//      both decorators and the canonical table.
//   2. **Positions decrease outward → inward.** `lower = closer to the
//      inner store`, so walking from the outside the declared positions
//      must strictly decrease. A pair that does not is named with both
//      positions.
//   3. **Audit replication sits inside webhook dispatch.** Implied by (2)
//      given the canonical numbers, and checked BY NAME as well, because
//      a position edited to satisfy (2) would otherwise conceal exactly
//      the inversion this phase was filed against. The message carries
//      the domain reason rather than the arithmetic one.
//
// ── Structural class, deliberately ──
// The check reads already-resident in-process objects, opens no socket
// and completes in microseconds, so it is not what `SkipPreflight` was
// built to bypass (see `IStructuralClassValidator`'s own note: the lever
// is for external probes whose dependency may be down). Booting a
// composition whose hook order is wrong is not a legitimate operator
// choice made under an outage — the deployment would come up looking
// healthy and quietly failing its audit contract.
//
// ── Why the guard is over the OBSERVED chain, not a declared one ──
// The decorator set is closed — all three are constructed by `compose`
// from `ServerConfig` flags, with no seam through which a deployment
// registers its own — and their construction order is fixed by data
// dependency, not by let-ordering style. So there is no registration
// list to sort, and a sort-at-assembly step could not have reordered the
// three if there were. Checking what was actually composed is strictly
// stronger: it catches a miswire however it arose, including one
// introduced by editing the construction sites, which is the regression
// that a sort could not see and that the goal statement actually names.

/// Stable `IConfigValidator.Name` for the chain guard.
[<Literal>]
let ValidatorName = "event-store-chain"

/// The chain rendered outermost → innermost for a message or a log line,
/// e.g. `JobNotifyEventStore(300) -> HookedEventStore(200) -> PersistentEventStore`.
let renderChain (chain: EventStoreChainDescription) =
    let links =
        chain.Links
        |> List.map (fun link ->
            match link.Position with
            | Some position -> sprintf "%s(%d)" link.Name position
            | None -> sprintf "%s(position undeclared)" link.Name)

    (links @ [ chain.InnerStoreName ]) |> String.concat " -> "

/// The guard's verdict over an already-walked chain. Pure, so the whole
/// of its behaviour is testable without composing a server — hand it a
/// description built by hand or one produced by `EventStoreChain.describe`
/// over real decorators.
let evaluate (chain: EventStoreChainDescription) : ValidationResult =
    let rendered = renderChain chain
    let canonical = EventStoreChain.renderCanonicalTable ()

    // Ordered worst-first: a cyclic chain makes every other finding moot,
    // and a position conflict makes the order findings unstable. Each
    // check yields `Some message` when it fires; the first one wins.
    let cyclic () =
        if chain.Truncated then
            Some(
                sprintf
                    "The composed IEventStore decorator chain could not be walked to its inner store: the walk hit its depth cap after %d links, which can only mean a decorator's InnerStore cycles back into the chain. Every Write would then recurse until the stack exhausts. Observed prefix: %s. Canonical positions: %s."
                    chain.Links.Length
                    rendered
                    canonical
            )
        else
            None

    let conflicting () =
        EventStoreChain.positionConflicts chain
        |> List.tryHead
        |> Option.map (fun (position, names) ->
            sprintf
                "Two distinct IEventStore decorators declare DecoratorPosition = %d: %s. Position is what orders the chain, so two decorators claiming one slot have no defined order relative to each other and the hooks that fire for a given write become an accident of construction order. Give one of them a distinct position — the ranges between the canonical entries are free (101-199 inside webhook dispatch, 201-299 between dispatch and job triggers, 301-999 outside job triggers), with 0 and 1000 reserved for innermost and outermost. Observed chain: %s. Canonical positions: %s."
                position
                (String.concat " and " names)
                rendered
                canonical)

    let auditInverted () =
        if EventStoreChain.auditAboveWebhook chain then
            Some(
                sprintf
                    "AuditReplicationHookedEventStore is composed OUTSIDE HookedEventStore, so the webhook dispatch hook sees each write before the audit replicator does. The replicator's contract is that every persisted event is replicable to every registered IAuditSink; dispatch sits on the fan-out path, not the persistence path, so it must run outside the replicator and not before it. Restore the canonical order — audit replication innermost, then webhook dispatch, then job notification. Observed chain: %s. Canonical positions: %s."
                    rendered
                    canonical
            )
        else
            None

    let misordered () =
        EventStoreChain.outOfOrderPairs chain
        |> List.tryHead
        |> Option.map (fun (outerName, outerPos, innerName, innerPos) ->
            sprintf
                "The composed IEventStore decorator chain is not in declared-position order: %s (position %d) is composed OUTSIDE %s (position %d), but a lower position means closer to the inner store. A decorator running on the wrong side of another drops the hooks the order was chosen to guarantee, and it does so silently — every decorator is a faithful IEventStore, so the deployment persists, reads and health-checks exactly as a correct one does. Observed chain: %s. Canonical positions: %s."
                outerName
                outerPos
                innerName
                innerPos
                rendered
                canonical)

    match
        [ cyclic; conflicting; auditInverted; misordered ]
        |> List.tryPick (fun check -> check ())
    with
    | Some message -> Error message
    | None -> Ok

/// The `IConfigValidator` over the composed store. Structural class — it
/// runs even under `ServerConfig.SkipPreflight` and still aborts on
/// `Error`; see the note above on why a silently-miswired hook chain is
/// not something an emergency-boot lever should be able to wave through.
type EventStoreChainValidator(store: IEventStore) =
    interface IStructuralClassValidator

    interface IConfigValidator with
        member _.Name = ValidatorName
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async { return evaluate (EventStoreChain.describe store) }

/// `IDevDiagnosticsContributor` surfacing the composed chain on
/// `/dev/inspect` under the panel name `"Event-store decorator chain"`.
///
/// The panel answers what the preflight guard can only answer once, at
/// boot, and only when something is wrong: *what is actually wrapping my
/// event store, in what order, and what does each link do to a write?*
/// Each link carries its self-described purpose, so the panel explains
/// the chain rather than merely listing it — which is the difference
/// between a diagnostic an operator can act on and one they have to go
/// read the SDK source to interpret.
type EventStoreChainContributor(store: IEventStore) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let chain = EventStoreChain.describe store
            let verdict = evaluate chain

            let payload: obj =
                box {|
                    Decorators =
                        chain.Links
                        |> List.mapi (fun index link -> {|
                            // 0 = outermost, so the reader does not have to
                            // infer direction from the array order alone.
                            Depth = index
                            Name = link.Name
                            Position = link.Position |> Option.toNullable
                            Purpose = link.Purpose |> Option.defaultValue "(does not implement IEventStoreDecorator)"
                        |})
                    InnerStore = chain.InnerStoreName
                    Rendered = renderChain chain
                    Canonical =
                        EventStoreChain.canonicalTable
                        |> List.map (fun (p, d) -> {| Position = p; Entry = d |})
                    Status = ValidationResult.status verdict
                    Message = ValidationResult.message verdict
                |}

            return ("Event-store decorator chain", payload)
        }

/// The guard over the composed store — the shape `compose` registers.
let validator (store: IEventStore) : IConfigValidator =
    EventStoreChainValidator(store) :> IConfigValidator

/// The `/dev/inspect` panel over the same store — the shape `compose`
/// registers when dev endpoints are enabled.
let contributor (store: IEventStore) : IDevDiagnosticsContributor =
    EventStoreChainContributor(store) :> IDevDiagnosticsContributor