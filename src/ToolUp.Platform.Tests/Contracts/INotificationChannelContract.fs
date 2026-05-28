module ToolUp.Platform.Tests.Contracts.INotificationChannelContract

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform

/// Contract test list for any `INotificationChannel` implementation.
/// Callers pass a display name (shown as the test-list title) and a
/// factory producing a fresh channel instance — the factory is invoked
/// once per test so stateful implementations get a clean slate. Tests
/// GUID-suffix their scope ids so implementations that share state
/// between factory invocations (e.g. a single Redis instance reused
/// across tests) don't cross-pollinate.
///
/// Delivery is asynchronous in cross-process implementations, so
/// assertions poll a `TaskCompletionSource` with a short timeout
/// rather than reading sequentially from `Publish`. The in-memory
/// implementation delivers synchronously and still passes without
/// change — the timeout is just slack.
let tests (name: string) (factory: unit -> INotificationChannel) =
    let uniqueScope () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        "scope-test-" + suffix

    // Reasonable upper bound for a local Redis instance to fan out
    // a publish back to a subscriber in the same process. Raised
    // from a tighter figure to tolerate Windows timer jitter in CI.
    let deliveryTimeout = TimeSpan.FromMilliseconds 1500.0

    /// Wait for `tcs` to complete within `timeout`, returning `None`
    /// on timeout. Avoids `Async.AwaitTask` hanging test runs when
    /// a notification never arrives (assertion failure is clearer).
    let awaitOrTimeout (tcs: TaskCompletionSource<'T>) = async {
        let! winner = Task.WhenAny(tcs.Task, Task.Delay(deliveryTimeout)) |> Async.AwaitTask

        if obj.ReferenceEquals(winner, tcs.Task) then
            let! value = tcs.Task |> Async.AwaitTask
            return Some value
        else
            return None
    }

    testList $"{name} — INotificationChannel contract" [
        testCaseAsync "Publish → Subscribe → handler receives envelope with matching scope and notification"
        <| async {
            let channel = factory ()
            let scope = uniqueScope ()
            let tcs = TaskCompletionSource<NotificationEnvelope>()

            let! _subId =
                channel.Subscribe(
                    scope,
                    fun env ->
                        // TrySetResult: second delivery (e.g. from a
                        // leftover Redis subscriber) is a no-op.
                        tcs.TrySetResult env |> ignore
                )

            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "hello"))

            match! awaitOrTimeout tcs with
            | None -> failtest "handler did not receive notification within timeout"
            | Some env ->
                Expect.equal env.ScopeId scope "envelope scope matches publish scope"

                match env.Notification with
                | SystemMessage(level, text) ->
                    Expect.equal level SystemMessageLevel.Info "message level round-trips"
                    Expect.equal text "hello" "message text round-trips"
                | other -> failtestf "unexpected notification kind: %A" other
        }

        testCaseAsync "Scope isolation: publish to scope A does not invoke scope B handler"
        <| async {
            let channel = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()
            let deliveredToB = TaskCompletionSource<NotificationEnvelope>()
            let deliveredToA = TaskCompletionSource<NotificationEnvelope>()

            let! _ = channel.Subscribe(scopeA, fun env -> deliveredToA.TrySetResult env |> ignore)
            let! _ = channel.Subscribe(scopeB, fun env -> deliveredToB.TrySetResult env |> ignore)

            do! channel.Publish(scopeA, SystemMessage(SystemMessageLevel.Info, "A-only"))

            // Wait for A-side delivery to confirm the publish landed;
            // then assert B never saw it. This order matters: if we
            // only waited on B's timeout we'd get a false pass from
            // a channel that silently dropped the publish entirely.
            match! awaitOrTimeout deliveredToA with
            | None -> failtest "scope A did not receive its own publish"
            | Some _ -> ()

            // Short wait to let any cross-scope delivery race settle.
            do! Async.Sleep 200

            Expect.isFalse
                deliveredToB.Task.IsCompleted
                "scope B received a notification published to scope A (isolation breach)"
        }

        testCaseAsync "Unsubscribe stops delivery"
        <| async {
            let channel = factory ()
            let scope = uniqueScope ()
            let received = ConcurrentQueue<NotificationEnvelope>()
            let! subId = channel.Subscribe(scope, received.Enqueue)

            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "first"))

            // Give cross-process transports a moment to deliver.
            do! Async.Sleep 200
            let countBeforeUnsub = received.Count

            do! channel.Unsubscribe subId
            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "second"))
            do! Async.Sleep 200

            Expect.equal received.Count countBeforeUnsub "no further deliveries after Unsubscribe"
        }

        testCaseAsync "Unsubscribe is idempotent on unknown ids and second calls"
        <| async {
            let channel = factory ()
            let scope = uniqueScope ()
            let! subId = channel.Subscribe(scope, ignore)

            do! channel.Unsubscribe subId
            // Second call on a known id — must not throw.
            do! channel.Unsubscribe subId
            // Unknown id — must not throw.
            do! channel.Unsubscribe(Guid.NewGuid())
        }

        testCaseAsync "Multiple subscribers for the same scope all receive"
        <| async {
            let channel = factory ()
            let scope = uniqueScope ()
            let first = TaskCompletionSource<NotificationEnvelope>()
            let second = TaskCompletionSource<NotificationEnvelope>()

            let! _ = channel.Subscribe(scope, fun env -> first.TrySetResult env |> ignore)
            let! _ = channel.Subscribe(scope, fun env -> second.TrySetResult env |> ignore)

            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "broadcast"))

            match! awaitOrTimeout first with
            | None -> failtest "first subscriber did not receive notification"
            | Some _ -> ()

            match! awaitOrTimeout second with
            | None -> failtest "second subscriber did not receive notification"
            | Some _ -> ()
        }

        testCaseAsync "Handler exception does not prevent sibling handler running"
        <| async {
            let channel = factory ()
            let scope = uniqueScope ()
            let survivor = TaskCompletionSource<NotificationEnvelope>()

            // First handler throws; second handler records receipt.
            // Both orderings must converge on "survivor completes".
            let! _ = channel.Subscribe(scope, fun _ -> failwith "subscriber explodes")
            let! _ = channel.Subscribe(scope, fun env -> survivor.TrySetResult env |> ignore)

            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "still delivered"))

            match! awaitOrTimeout survivor with
            | None -> failtest "sibling handler did not run after first handler threw"
            | Some _ -> ()
        }

        testCaseAsync "Publish with no subscribers does not throw"
        <| async {
            let channel = factory ()
            let scope = uniqueScope ()
            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "into the void"))
        }

        testCaseAsync "Envelope Ids are unique across publishes"
        <| async {
            let channel = factory ()
            let scope = uniqueScope ()
            let ids = ConcurrentBag<Guid>()
            let received = new CountdownEvent(3)

            let! _ =
                channel.Subscribe(
                    scope,
                    fun env ->
                        ids.Add env.Id
                        received.Signal() |> ignore
                )

            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "one"))
            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "two"))
            do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "three"))

            // Wait synchronously for three deliveries — the test only
            // asserts uniqueness, so we tolerate any delivery order.
            let arrived = received.Wait(deliveryTimeout)
            Expect.isTrue arrived "three notifications delivered within timeout"

            let distinct = ids |> Seq.distinct |> Seq.length
            Expect.equal distinct 3 "each envelope carries a unique Id"
        }
    ]