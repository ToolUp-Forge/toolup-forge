module ToolUp.Scheduling.Tests.InProcess.InMemoryEventStore

open System.Collections.Concurrent
open ToolUp.Platform

// ─── Test-only in-memory IEventStore stub ─────────────────────────
//
// The Phase 6 `InMemoryEventStore` lives in `ToolUp.Platform`'s
// Server props injection. The contract pack only needs to verify
// that audit events are emitted with the right `SourceModule` /
// `EventType` / `ScopeId` — a small concurrent-list stub is
// sufficient. Captures the writes so tests can assert on them.

type InMemoryEventStore() =
    let written = ConcurrentBag<ModuleEvent>()

    member _.Events = written |> Seq.toList

    interface IEventStore with

        member _.Write(evt) = async {
            written.Add evt
            return ()
        }

        member _.ReadAll(scopeId) = async { return written |> Seq.filter (fun e -> e.ScopeId = scopeId) |> List.ofSeq }

        member _.ReadByType(scopeId, eventType) = async {
            return
                written
                |> Seq.filter (fun e -> e.ScopeId = scopeId && e.EventType = eventType)
                |> List.ofSeq
        }

        member _.ReadBySource(scopeId, sourceModule) = async {
            return
                written
                |> Seq.filter (fun e -> e.ScopeId = scopeId && e.SourceModule = sourceModule)
                |> List.ofSeq
        }

        member _.ListScopes() = async { return written |> Seq.map _.ScopeId |> Seq.distinct |> List.ofSeq }

        // Phase 9h erasure surface. Honest over the bag: drain, apply
        // the policy, re-add survivors. Sufficient for the Scheduling
        // audit-emission contract (the DSR contract pack exercises the
        // real Server-tier InMemoryEventStore).
        member _.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "events"
                        RecordsAffected = 0
                        Note = None
                    }
            else
                match policy with
                | ErasurePolicy.RetainPerCompliance ->
                    return Result.Error(HandlerRefused("events", "event-log retention overrides erasure"))
                | _ ->
                    let drained = ResizeArray<ModuleEvent>()
                    let mutable item = Unchecked.defaultof<ModuleEvent>

                    while written.TryTake(&item) do
                        drained.Add item

                    let all = List.ofSeq drained

                    let isMatch (e: ModuleEvent) =
                        e.ScopeId = scopeId && e.Payload.Contains subjectUserId

                    let matched = all |> List.filter isMatch

                    let survivors =
                        if dryRun then
                            all
                        else
                            match policy with
                            | ErasurePolicy.HardDelete -> all |> List.filter (isMatch >> not)
                            | _ ->
                                all
                                |> List.map (fun e ->
                                    if isMatch e then
                                        {
                                            e with
                                                Payload = Erasure.TombstoneMarker
                                        }
                                    else
                                        e)

                    survivors |> List.iter written.Add

                    return
                        Result.Ok {
                            HandlerName = "events"
                            RecordsAffected = matched.Length
                            Note = None
                        }
        }