// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── IEventStoreDecorator — the event-store chain's position model ───
//
// Phase 9u. `compose` stacks up to three `IEventStore` decorators around
// the resolved inner store:
//
//     JobNotifyEventStore                     (300, outermost)
//       └── HookedEventStore                  (200)
//             └── AuditReplicationHookedEventStore (100, innermost)
//                   └── the resolved IEventStore
//
// The order is load-bearing and each adjacency has a reason. The audit
// replicator must see an event BEFORE the webhook dispatch hook, because
// the replicator's contract is that every persisted event is replicable
// and a dispatch hook sits on the fan-out path, not the persistence path.
// The job-notify wrapper is outermost so an `OnEvent` trigger fires only
// after both the replication enqueue and the webhook dispatch have been
// handed the event.
//
// Getting that order wrong produces NO error. Every decorator is a
// faithful `IEventStore`, so a miswired chain persists, reads and
// type-checks exactly as a correct one does — it simply drops hooks for
// some events, silently and forever. That is the failure this interface
// exists to make visible.
//
// ── What this interface is, and what it deliberately is not ──
//
// It is a *self-description*: a decorator states its name, its intended
// position, its purpose, and the store it wraps. From those four facts
// the composed chain can be WALKED at boot (`EventStoreChain.describe`),
// CHECKED against the canonical order (`positionConflicts` /
// `outOfOrderPairs` / `auditAboveWebhook`, which the `event-store-chain`
// config validator runs at boot), and SHOWN on `/dev/inspect`.
//
// It is NOT a registration/sort mechanism, and the distinction is the
// finding Phase 9u landed rather than an omission. The decorator set is
// closed: all three are constructed by `compose` itself from
// `ServerConfig` flags, there is no seam through which a deployment
// registers a custom one, and the construction order is fixed by DATA
// DEPENDENCY rather than by let-ordering style — the webhook subsystem
// takes the audit-decorated store as a constructor argument, and the
// job-notify wrapper takes the webhook-hooked one. A "sort the registered
// decorators by position, then stack" step therefore has no registration
// list to sort and could not reorder the three if it had one. Validating
// the OBSERVED chain is strictly stronger than sorting a declared one: it
// catches a miswire however it arose, including one introduced by editing
// the construction sites themselves, which is precisely the regression a
// sort-at-assembly cannot see.
//
// **Additive (GP 11).** A decorator that does not implement this
// interface is walked as an opaque link and reported as such — it is not
// an error. Nothing downstream of `IEventStore` changes shape, so an
// existing third-party decorator keeps a deployment's prior behaviour
// byte-for-byte.

/// An `IEventStore` decorator that self-describes its place in the
/// composed chain. Implemented alongside `IEventStore` — the walker
/// type-tests for it, so implementing it is opt-in and additive.
type IEventStoreDecorator =
    /// Stable display name, used in the preflight refusal message and on
    /// the `/dev/inspect` chain panel. Conventionally the type's own name.
    abstract member DecoratorName: string

    /// Intended distance from the inner store — **lower is closer to the
    /// inner store**. See `EventStoreChain` for the canonical table and
    /// the two reserved bounds.
    abstract member DecoratorPosition: int

    /// One sentence on what this decorator does to a write, in the voice
    /// of the panel that displays it ("Fires …", "Enqueues …").
    abstract member DecoratorPurpose: string

    /// The store this decorator wraps. The walker follows this to the
    /// next link; the terminal store is the one that is not a decorator.
    abstract member InnerStore: IEventStore

/// One walked link of the composed chain, outermost first.
type EventStoreChainLink = {
    /// The decorator's `DecoratorName`, or the CLR type name when the
    /// link does not implement `IEventStoreDecorator`.
    Name: string
    /// The declared position, or `None` for an undeclared link.
    Position: int option
    /// The declared purpose, or `None` for an undeclared link.
    Purpose: string option
}

/// The composed chain as observed at boot: the decorators wrapping the
/// resolved store, outermost first, plus the terminal store's type name.
type EventStoreChainDescription = {
    /// Decorator links, **outermost first** — so declared positions run
    /// strictly DECREASING through this list when the chain is correct.
    Links: EventStoreChainLink list
    /// CLR type name of the terminal (undecorated) store.
    InnerStoreName: string
    /// True when the walk hit its depth cap, which can only mean a cyclic
    /// `InnerStore`. `Links` is then a prefix, and the validator refuses.
    Truncated: bool
}

/// The canonical position table, the chain walk, and the order check.
/// Pure and Fable-safe — no reflection beyond `GetType().Name`, no I/O —
/// so the whole of the invariant is testable without composing a server.
module EventStoreChain =

    /// Reserved lower bound. A decorator declaring `0` asks to sit
    /// directly against the resolved store, inside everything else.
    [<Literal>]
    let InnermostPosition = 0

    /// `AuditReplicationHookedEventStore` — innermost of the three
    /// first-party decorators. Every persisted event must be replicable,
    /// so the replicator sees writes before any fan-out hook.
    [<Literal>]
    let AuditReplicationPosition = 100

    /// `HookedEventStore` — webhook dispatch, outside audit replication.
    [<Literal>]
    let WebhookDispatchPosition = 200

    /// `JobNotifyEventStore` — `Trigger.OnEvent` job notification,
    /// outermost of the three so triggers fire after the dispatch hooks.
    [<Literal>]
    let JobNotifyPosition = 300

    /// Reserved upper bound. A decorator declaring `1000` asks to sit
    /// outside everything else.
    [<Literal>]
    let OutermostPosition = 1000

    /// Hard cap on the walk. Three first-party decorators plus generous
    /// headroom; reaching it means `InnerStore` cycles.
    [<Literal>]
    let private MaxChainDepth = 32

    /// The canonical table, rendered into every refusal message so the
    /// operator reading it does not have to find this file.
    let canonicalTable: (int * string) list = [
        InnermostPosition, "(reserved — innermost)"
        AuditReplicationPosition, "AuditReplicationHookedEventStore — audit replication enqueue"
        WebhookDispatchPosition, "HookedEventStore — webhook dispatch"
        JobNotifyPosition, "JobNotifyEventStore — OnEvent job triggers"
        OutermostPosition, "(reserved — outermost)"
    ]

    /// The canonical table as one display line per row.
    let renderCanonicalTable () =
        canonicalTable
        |> List.map (fun (position, description) -> sprintf "%d = %s" position description)
        |> String.concat "; "

    /// Walk the composed store from the outside in, recording every link.
    /// A link that does not implement `IEventStoreDecorator` terminates
    /// the walk — the walker cannot see past an opaque decorator, and
    /// reporting the chain it CAN see is more useful than guessing.
    let describe (store: IEventStore) : EventStoreChainDescription =
        let rec walk (current: IEventStore) (depth: int) (acc: EventStoreChainLink list) =
            if depth >= MaxChainDepth then
                {
                    Links = List.rev acc
                    InnerStoreName = current.GetType().Name
                    Truncated = true
                }
            else
                match box current with
                | :? IEventStoreDecorator as decorator ->
                    let link = {
                        Name = decorator.DecoratorName
                        Position = Some decorator.DecoratorPosition
                        Purpose = Some decorator.DecoratorPurpose
                    }

                    walk decorator.InnerStore (depth + 1) (link :: acc)
                | _ -> {
                    Links = List.rev acc
                    InnerStoreName = current.GetType().Name
                    Truncated = false
                  }

        walk store 0 []

    /// Two distinct decorators declaring the same position — the conflict
    /// case. Returns each colliding position with the names that claimed it.
    let positionConflicts (chain: EventStoreChainDescription) : (int * string list) list =
        chain.Links
        |> List.choose (fun link -> link.Position |> Option.map (fun p -> p, link.Name))
        |> List.groupBy fst
        |> List.choose (fun (position, entries) ->
            let names = entries |> List.map snd |> List.distinct

            if List.length names > 1 then
                Some(position, names)
            else
                None)

    /// Adjacent pairs whose declared positions do not decrease outermost
    /// → innermost. Each result is `(outer, outerPosition, inner,
    /// innerPosition)`; an undeclared link is skipped rather than guessed at.
    let outOfOrderPairs (chain: EventStoreChainDescription) : (string * int * string * int) list =
        let declared =
            chain.Links
            |> List.choose (fun link -> link.Position |> Option.map (fun p -> link.Name, p))

        declared
        |> List.pairwise
        |> List.choose (fun ((outerName, outerPos), (innerName, innerPos)) ->
            if outerPos <= innerPos then
                Some(outerName, outerPos, innerName, innerPos)
            else
                None)

    /// True when the audit-replication decorator sits OUTSIDE the webhook
    /// dispatch hook — the specific inversion Phase 9u was filed against.
    /// Checked by name as well as by position, because a position edited
    /// to make the generic order check pass would otherwise hide exactly
    /// the miswire this names.
    let auditAboveWebhook (chain: EventStoreChainDescription) : bool =
        let indexOf name =
            chain.Links |> List.tryFindIndex (fun link -> link.Name = name)

        // Outermost first, so a SMALLER index means further out.
        match indexOf "AuditReplicationHookedEventStore", indexOf "HookedEventStore" with
        | Some auditIndex, Some webhookIndex -> auditIndex < webhookIndex
        | _ -> false