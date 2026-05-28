module ToolUp.Platform.InMemoryEventStore

open ToolUp.Platform

/// In-memory event store backed by a thread-safe list.
/// Filters every read path by `scopeId` so events are team-isolated even
/// though all scopes share the same underlying list. Suitable for
/// development; replace with `PersistentEventStore` (Phase 6) for production.
type InMemoryEventStore() =
    let mutable events: ModuleEvent list = []
    let lockObj = obj ()

    let readScoped (scopeId: string) =
        lock lockObj (fun () -> events |> List.filter (fun e -> e.ScopeId = scopeId))

    interface IEventStore with
        member _.Write(event) = async { lock lockObj (fun () -> events <- event :: events) }

        // Sort by `OccurredAt` on read — the contract promises
        // reverse-chronological ordering by that field, not by insertion
        // time. Callers that back-date events (replays, migrations)
        // expect the restored order, not insertion order.
        member _.ReadAll(scopeId) = async { return readScoped scopeId |> List.sortByDescending _.OccurredAt }

        member _.ReadByType(scopeId, eventType) = async {
            return readScoped scopeId |> List.filter (fun e -> e.EventType = eventType)
        }

        member _.ReadBySource(scopeId, sourceModule) = async {
            return readScoped scopeId |> List.filter (fun e -> e.SourceModule = sourceModule)
        }

        member _.ListScopes() = async { return lock lockObj (fun () -> events |> List.map _.ScopeId |> List.distinct) }

        member _.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "events"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op (would otherwise match every event)"
                    }
            else
                return
                    lock lockObj (fun () ->
                        let isMatch (e: ModuleEvent) =
                            e.ScopeId = scopeId && e.Payload.Contains subjectUserId

                        match policy with
                        | ErasurePolicy.RetainPerCompliance ->
                            Result.Error(
                                HandlerRefused(
                                    "events",
                                    "event-log retention legally overrides erasure under RetainPerCompliance"
                                )
                            )
                        | ErasurePolicy.HardDelete ->
                            let matched, kept = events |> List.partition isMatch

                            if not dryRun then
                                events <- kept

                            Result.Ok {
                                HandlerName = "events"
                                RecordsAffected = matched.Length
                                Note =
                                    Some(
                                        sprintf
                                            "%d event(s) %s in scope %s"
                                            matched.Length
                                            (if dryRun then "would be removed" else "removed")
                                            scopeId
                                    )
                            }
                        | ErasurePolicy.Tombstone ->
                            let matchedCount = events |> List.filter isMatch |> List.length

                            if not dryRun then
                                events <-
                                    events
                                    |> List.map (fun e ->
                                        if isMatch e then
                                            {
                                                e with
                                                    Payload = Erasure.TombstoneMarker
                                            }
                                        else
                                            e)

                            Result.Ok {
                                HandlerName = "events"
                                RecordsAffected = matchedCount
                                Note =
                                    Some(
                                        sprintf
                                            "%d event payload(s) %s in scope %s"
                                            matchedCount
                                            (if dryRun then "would be tombstoned" else "tombstoned")
                                            scopeId
                                    )
                            })
        }