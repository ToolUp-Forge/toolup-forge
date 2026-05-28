module ToolUp.Platform.EventStoreErasureHandler

open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.IDataExporter

// ─── Phase 9h — event-store DSR adapter ──────────────────────────────
//
// Bridges the Phase 9h `IEventStore.Erase` surface (Core interface
// extension) into the orchestrator's composition-time extension points
// (`IDataExporter` / `IErasureHandler`, see IDataExporter.fs). This is
// the per-store adapter the phase body calls for: new behaviour lands
// additively — the orchestrator and every other store are untouched;
// a deployment opts the event store into DSR by registering these two
// adapters at compose time.
//
// `Name = "events"`. The MVP orchestrator orders handlers
// alphabetically; deployments that need the event log erased in a
// specific position relative to other stores wrap/rename per the
// ErasurePipeline ordering note (`01-events` / `99-audit`).

[<Literal>]
let private HandlerName = "events"

let private jsonOptions = JsonSerializerOptions(WriteIndented = false)

/// `IDataExporter` for the event store. Streams every event in the
/// caller's scope whose payload names the subject as one JSON segment.
/// Scope isolation is inherited from `IEventStore.ReadAll scopeId`.
type EventStoreExporter(eventStore: IEventStore) =
    interface IDataExporter with
        member _.Name = HandlerName

        member _.Export(scopeId, subjectUserId) = async {
            if Erasure.isBlankSubject subjectUserId then
                return []
            else
                let! all = eventStore.ReadAll scopeId

                let matched = all |> List.filter (fun e -> e.Payload.Contains subjectUserId)

                if List.isEmpty matched then
                    return []
                else
                    let json = JsonSerializer.Serialize(matched, jsonOptions)

                    return [
                        {
                            Name = sprintf "events/%s.json" scopeId
                            MimeType = "application/json"
                            Body = Encoding.UTF8.GetBytes json
                        }
                    ]
        }

/// `IErasureHandler` for the event store. Delegates to
/// `IEventStore.Erase`; `dryRun = true` serves the two-phase-commit
/// preview, `dryRun = false` the confirm.
type EventStoreErasureHandler(eventStore: IEventStore) =
    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) =
            eventStore.Erase(scopeId, subjectUserId, policy, false)

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = eventStore.Erase(scopeId, subjectUserId, policy, true)

            return
                match result with
                | Result.Ok summary -> summary
                | Result.Error err -> {
                    HandlerName = HandlerName
                    RecordsAffected = 0
                    Note = Some(ErasureError.toMessage err)
                  }
        }

/// Compose-time registration helpers. A deployment opts the event
/// store into DSR by adding these to the registered exporter / erasure
/// handler lists (the `IDataExporter` / `IErasureHandler` extension
/// points) — no composition-root edit required.
let exporter (eventStore: IEventStore) : IDataExporter =
    EventStoreExporter eventStore :> IDataExporter

let erasureHandler (eventStore: IEventStore) : IErasureHandler =
    EventStoreErasureHandler eventStore :> IErasureHandler