module ToolUp.Platform.Tests.InProcess.EventStoreChainValidatorTests

// ─── Phase 9u — the event-store decorator chain guard ────────────────
//
// Two halves, tested separately because they fail differently:
//
//   * `EventStoreChain.describe` — does the walk see the real object
//     graph? Exercised against REAL decorator instances stacked by hand,
//     not against hand-built descriptions, because the whole value of an
//     observed-chain check is that it reads what was actually composed.
//     A test that hand-builds the description would pass just as happily
//     if `InnerStore` returned the wrong store.
//
//   * `EventStoreChainValidator.evaluate` — does the verdict fire on each
//     miswire and stay silent on a correct chain? Exercised against
//     hand-built descriptions, which is what lets a case like "two
//     decorators claim position 200" be expressed at all: `compose`
//     cannot produce it today, and a guard whose failure arm has never
//     been executed is a guard nobody has tested.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Tests.Contracts

// ── A minimal terminal store and a configurable decorator ────────────

type private StubEventStore() =
    let mutable written: ModuleEvent list = []
    member _.Written = written

    interface IEventStore with
        member _.Write(evt) = async { written <- evt :: written }
        member _.ReadAll(_) = async { return [] }
        member _.ReadByType(_, _) = async { return [] }
        member _.ReadBySource(_, _) = async { return [] }
        member _.ListScopes() = async { return [] }
        member _.Erase(_, _, _, _) = async { return Result.Ok(Unchecked.defaultof<ErasureSummary>) }

/// A decorator that declares whatever the test asks it to. Pass-through
/// on every `IEventStore` member — the walk is what is under test.
type private StubDecorator(inner: IEventStore, name: string, position: int) =
    interface IEventStoreDecorator with
        member _.DecoratorName = name
        member _.DecoratorPosition = position
        member _.DecoratorPurpose = sprintf "test decorator %s" name
        member _.InnerStore = inner

    interface IEventStore with
        member _.Write(evt) = inner.Write evt
        member _.ReadAll(scopeId) = inner.ReadAll scopeId
        member _.ReadByType(scopeId, eventType) = inner.ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId, sourceModule) =
            inner.ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = inner.ListScopes()

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

/// A decorator that does NOT implement `IEventStoreDecorator` — the
/// pre-Phase-9u third-party shape the walk must tolerate (GP 11).
type private OpaqueDecorator(inner: IEventStore) =
    interface IEventStore with
        member _.Write(evt) = inner.Write evt
        member _.ReadAll(scopeId) = inner.ReadAll scopeId
        member _.ReadByType(scopeId, eventType) = inner.ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId, sourceModule) =
            inner.ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = inner.ListScopes()

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

/// A decorator whose `InnerStore` is itself — the cycle the depth cap
/// exists for. Cannot be built by `compose`; can be built by a
/// third-party decorator with a wiring bug, and would otherwise recurse
/// the walk (and every `Write`) until the stack exhausts.
type private CyclicDecorator() =
    interface IEventStoreDecorator with
        member _.DecoratorName = "CyclicDecorator"
        member _.DecoratorPosition = 500
        member _.DecoratorPurpose = "cycles"
        member this.InnerStore = this :> IEventStore

    interface IEventStore with
        member _.Write(_) = async { return () }
        member _.ReadAll(_) = async { return [] }
        member _.ReadByType(_, _) = async { return [] }
        member _.ReadBySource(_, _) = async { return [] }
        member _.ListScopes() = async { return [] }
        member _.Erase(_, _, _, _) = async { return Result.Ok(Unchecked.defaultof<ErasureSummary>) }

/// The webhook dispatcher `HookedEventStore` needs, reduced to what the
/// contract pack exercises: `Dispatch` must not throw, and `TestFire` is
/// never reached from a write.
type private StubWebhookDispatcher() =
    interface WebhookDispatcher.IWebhookDispatcher with
        member _.Dispatch(_) = ()
        member _.TestFire(_, _) = async { return Result.Error "not exercised by the contract pack" }

let private link name position : EventStoreChainLink = {
    Name = name
    Position = Some position
    Purpose = Some(sprintf "test decorator %s" name)
}

/// Outermost-first, as `describe` produces.
let private chainOf links : EventStoreChainDescription = {
    Links = links
    InnerStoreName = "StubEventStore"
    Truncated = false
}

/// The canonical composed chain: job notify (300) outside webhook
/// dispatch (200) outside audit replication (100).
let private canonicalChain =
    chainOf [
        link "JobNotifyEventStore" EventStoreChain.JobNotifyPosition
        link "HookedEventStore" EventStoreChain.WebhookDispatchPosition
        link "AuditReplicationHookedEventStore" EventStoreChain.AuditReplicationPosition
    ]

let private expectError (result: ValidationResult) (what: string) =
    match result with
    | Error message -> message
    | other -> failtestf "expected Error (%s), got %A" what other

[<Tests>]
let tests =
    testList "Phase 9u — event-store decorator chain" [

        // ── the walk, over real object graphs ──

        test "describe walks a stacked chain outermost-first and names the terminal store" {
            let inner = StubEventStore()

            let composed =
                StubDecorator(
                    StubDecorator(
                        StubDecorator(inner, "AuditReplicationHookedEventStore", 100),
                        "HookedEventStore",
                        200
                    ),
                    "JobNotifyEventStore",
                    300
                )

            let chain = EventStoreChain.describe composed

            Expect.equal
                (chain.Links |> List.map _.Name)
                [
                    "JobNotifyEventStore"
                    "HookedEventStore"
                    "AuditReplicationHookedEventStore"
                ]
                "outermost first"

            Expect.equal (chain.Links |> List.map _.Position) [ Some 300; Some 200; Some 100 ] "declared positions"
            Expect.equal chain.InnerStoreName "StubEventStore" "terminal store named"
            Expect.isFalse chain.Truncated "walk completed"
        }

        test "describe over an undecorated store reports no links" {
            let chain = EventStoreChain.describe (StubEventStore())
            Expect.isEmpty chain.Links "the lightweight default composes no decorators"
            Expect.equal chain.InnerStoreName "StubEventStore" "the store itself is the terminal link"
        }

        test "describe stops at a decorator that does not declare itself (GP 11 — additive)" {
            let composed =
                StubDecorator(OpaqueDecorator(StubEventStore()), "JobNotifyEventStore", 300)

            let chain = EventStoreChain.describe composed

            Expect.equal (chain.Links |> List.map _.Name) [ "JobNotifyEventStore" ] "declared link recorded"

            Expect.equal
                chain.InnerStoreName
                "OpaqueDecorator"
                "the walk cannot see past an opaque link and says so rather than guessing"

            Expect.isFalse chain.Truncated "an opaque link is a clean stop, not a truncation"
        }

        test "describe caps a cyclic chain instead of recursing forever" {
            let chain = EventStoreChain.describe (CyclicDecorator())
            Expect.isTrue chain.Truncated "the depth cap fired"
        }

        // ── the verdict ──

        test "the canonical chain passes" {
            Expect.equal (EventStoreChainValidator.evaluate canonicalChain) Ok "correct order is silent"
        }

        test "an undecorated store passes" {
            Expect.equal (EventStoreChainValidator.evaluate (chainOf [])) Ok "the lightweight default is not a finding"
        }

        test "a single decorator passes" {
            let chain = chainOf [ link "JobNotifyEventStore" EventStoreChain.JobNotifyPosition ]
            Expect.equal (EventStoreChainValidator.evaluate chain) Ok "job scheduler only"
        }

        test "audit replication composed OUTSIDE webhook dispatch refuses boot, with the domain reason" {
            let inverted =
                chainOf [
                    link "AuditReplicationHookedEventStore" EventStoreChain.AuditReplicationPosition
                    link "HookedEventStore" EventStoreChain.WebhookDispatchPosition
                ]

            let message =
                expectError (EventStoreChainValidator.evaluate inverted) "audit above webhook"

            Expect.stringContains message "AuditReplicationHookedEventStore" "names the audit decorator"
            Expect.stringContains message "HookedEventStore" "names the webhook decorator"
            Expect.stringContains message "replicable" "gives the reason, not just the arithmetic"
            Expect.stringContains message "Observed chain" "shows what was actually composed"
        }

        test "the audit inversion is caught by NAME even when the positions were edited to look ordered" {
            // Someone 'fixes' the order check by renumbering audit above
            // webhook. The generic check is satisfied; the miswire is not.
            let renumbered =
                chainOf [
                    link "AuditReplicationHookedEventStore" 250
                    link "HookedEventStore" EventStoreChain.WebhookDispatchPosition
                ]

            Expect.isEmpty
                (EventStoreChain.outOfOrderPairs renumbered)
                "the generic order check is satisfied by the renumbering — this is why the by-name check exists"

            let message =
                expectError (EventStoreChainValidator.evaluate renumbered) "renumbered inversion"

            Expect.stringContains message "OUTSIDE HookedEventStore" "still refused"
        }

        test "two decorators at one position refuse boot, naming both and the free ranges" {
            let conflicting =
                chainOf [
                    link "JobNotifyEventStore" EventStoreChain.JobNotifyPosition
                    link "CustomAuditMirror" EventStoreChain.WebhookDispatchPosition
                    link "HookedEventStore" EventStoreChain.WebhookDispatchPosition
                ]

            let message =
                expectError (EventStoreChainValidator.evaluate conflicting) "position conflict"

            Expect.stringContains message "DecoratorPosition = 200" "names the contested position"
            Expect.stringContains message "CustomAuditMirror" "names the first claimant"
            Expect.stringContains message "HookedEventStore" "names the second claimant"
            Expect.stringContains message "101-199" "points at a free range"
        }

        test "one decorator repeated at its own position is not a conflict" {
            // `positionConflicts` groups by position and requires DISTINCT
            // names — the same decorator type appearing twice is a
            // different (and legal) thing from two types claiming one slot.
            let repeated =
                chainOf [
                    link "HookedEventStore" EventStoreChain.WebhookDispatchPosition
                    link "HookedEventStore" EventStoreChain.WebhookDispatchPosition
                ]

            Expect.isEmpty (EventStoreChain.positionConflicts repeated) "same name, not a conflict"
        }

        test "a non-audit pair out of declared order refuses boot with both positions" {
            let misordered =
                chainOf [
                    link "HookedEventStore" EventStoreChain.WebhookDispatchPosition
                    link "JobNotifyEventStore" EventStoreChain.JobNotifyPosition
                ]

            let message =
                expectError (EventStoreChainValidator.evaluate misordered) "out of order"

            Expect.stringContains message "position 200" "names the outer position"
            Expect.stringContains message "position 300" "names the inner position"
            Expect.stringContains message "closer to the inner store" "states the convention"
        }

        test "a truncated (cyclic) chain refuses boot ahead of every other finding" {
            let truncated = {
                chainOf [ link "CyclicDecorator" 500 ] with
                    Truncated = true
            }

            let message = expectError (EventStoreChainValidator.evaluate truncated) "cyclic"
            Expect.stringContains message "cycles back" "names the cause"
        }

        test "an undeclared link is skipped by the order check rather than guessed at" {
            let withOpaque =
                chainOf [
                    link "JobNotifyEventStore" EventStoreChain.JobNotifyPosition
                    {
                        Name = "OpaqueDecorator"
                        Position = None
                        Purpose = None
                    }
                    link "AuditReplicationHookedEventStore" EventStoreChain.AuditReplicationPosition
                ]

            Expect.equal (EventStoreChainValidator.evaluate withOpaque) Ok "300 then 100 still decreases"
        }

        // ── registration shape ──

        test "the validator is structural-class, so SkipPreflight cannot wave a miswire through" {
            let validator = EventStoreChainValidator.validator (StubEventStore())
            Expect.isTrue (validator :? IStructuralClassValidator) "opts into the always-run set"
            Expect.equal validator.Name EventStoreChainValidator.ValidatorName "stable registration name"
        }

        test "the validator runs the walk against the live composed store" {
            let inner = StubEventStore()

            let composed =
                StubDecorator(StubDecorator(inner, "HookedEventStore", 200), "AuditReplicationHookedEventStore", 100)

            let result =
                (EventStoreChainValidator.validator composed).Validate()
                |> Async.RunSynchronously

            let message = expectError result "live inverted chain"
            Expect.stringContains message "Observed chain" "reports what it walked"
        }

        // ── the IEventStoreDecorator contract pack, bound by each of the
        //    three first-party decorators ──
        //
        // The pack asserts that what a decorator SAYS about itself is true
        // of the object saying it — chiefly that `InnerStore` is the store
        // it actually writes through, which is what makes the chain walk,
        // the boot guard and the /dev/inspect panel truthful rather than
        // merely confident. Each binding closes over its decorator's other
        // constructor dependencies; the pack supplies the inner store.

        IEventStoreDecoratorContract.tests "AuditReplicationHookedEventStore" (fun inner ->
            AuditReplicator.AuditReplicationHookedEventStore(inner, ignore) :> IEventStore)

        IEventStoreDecoratorContract.tests "HookedEventStore" (fun inner ->
            HookedEventStore.HookedEventStore(inner, StubWebhookDispatcher()) :> IEventStore)

        IEventStoreDecoratorContract.tests "JobNotifyEventStore" (fun inner ->
            JobNotifyEventStore.JobNotifyEventStore(inner, (fun () -> None)) :> IEventStore)

        test "the contributor names the panel and reports the walked chain" {
            let inner = StubEventStore()
            let composed = StubDecorator(inner, "JobNotifyEventStore", 300)

            let panelName, payload =
                (EventStoreChainValidator.contributor composed).Contribute()
                |> Async.RunSynchronously

            Expect.equal panelName "Event-store decorator chain" "panel name is the /dev/inspect key"

            let rendered = sprintf "%A" payload
            Expect.stringContains rendered "JobNotifyEventStore" "the decorator appears in the payload"
            Expect.stringContains rendered "StubEventStore" "so does the terminal store"
        }
    ]