namespace ToolUp.Platform

open System
open Newtonsoft.Json
open ToolUp.Platform
open ToolUp.Platform.Narrative

/// Notification keys for the narrative SSE stream. Treat these as part of
/// the public wire contract — clients subscribe by string match against
/// the `CustomNotification` envelope's `key`.
module NarrativeNotifications =
    [<Literal>]
    let PublishedKey = "Narrative.Published"

    [<Literal>]
    let ScopeResetKey = "Narrative.ScopeReset"

    /// Per-event payload published under `PublishedKey`. The shape
    /// mirrors `NarrativeEntryInfo` so subscribers can populate a
    /// list-view row directly without an extra round-trip to `List`.
    type PublishedPayload = {
        Id: NarrativeId
        ModuleId: string
        PageRoute: string option
        Title: string
        Subtitle: string option
        PublishedAt: DateTime
        Tags: string list
    }

    /// Payload published under `ScopeResetKey` after `DeleteScope`. The
    /// scope id matches the recipient scope; subscribers use it to drop
    /// any cached list/get state.
    type ScopeResetPayload = {
        ScopeId: string
        DeletedCount: int
        ResetAt: DateTime
    }

/// `INarrativeStore` decorator that publishes a notification on every
/// successful write — Publish, PublishTagged, ReplaceLatest,
/// ReplaceLatestTagged, DeleteScope. Subscribers receive
/// `CustomNotification(key, payloadJson)` via the existing SSE pipeline;
/// scope isolation is preserved because the channel routes by `scopeId`.
///
/// Falls back silently when `channel` is null (a configuration that
/// should not happen at runtime — `INotificationChannel` is always
/// resolved in `ComposeNotifications` — but the guard keeps test
/// harnesses without a channel working).
type NotifyingNarrativeStore(inner: INarrativeStore, channel: INotificationChannel) =

    let jsonSettings =
        let s = JsonSerializerSettings()
        s.Converters.Add(ToolUp.Remoting.Json.FableJsonConverter())
        s

    let publishPayload (scopeId: string) (key: string) (payload: obj) : Async<unit> = async {
        if isNull (box channel) then
            return ()
        else
            try
                let payloadJson = JsonConvert.SerializeObject(payload, jsonSettings)
                let notification = CustomNotification(key, payloadJson)
                do! channel.Publish(scopeId, notification)
            with _ ->
                // Notification publish must never break a successful
                // write; subscribers that miss an event recover via the
                // next List/Get round-trip.
                return ()
    }

    let entryToPayload (e: NarrativeEntry) : NarrativeNotifications.PublishedPayload = {
        Id = e.Id
        ModuleId = e.ModuleId
        PageRoute = e.PageRoute
        Title = e.Title
        Subtitle = e.Subtitle
        PublishedAt = e.PublishedAt
        Tags = e.Tags
    }

    let publishEvent (scopeId: string) (id: NarrativeId) : Async<unit> = async {
        let! entry = inner.Get(scopeId, id)

        match entry with
        | Some e -> do! publishPayload scopeId NarrativeNotifications.PublishedKey (entryToPayload e)
        | None -> return ()
    }

    interface INarrativeStore with
        member _.Publish(scopeId, moduleId, pageRoute, document) = async {
            let! id = inner.Publish(scopeId, moduleId, pageRoute, document)
            do! publishEvent scopeId id
            return id
        }

        member _.PublishTagged(scopeId, moduleId, pageRoute, document, tags) = async {
            let! id = inner.PublishTagged(scopeId, moduleId, pageRoute, document, tags)
            do! publishEvent scopeId id
            return id
        }

        member _.ReplaceLatest(scopeId, moduleId, pageRoute, subtitleKey, document) = async {
            let! id = inner.ReplaceLatest(scopeId, moduleId, pageRoute, subtitleKey, document)
            do! publishEvent scopeId id
            return id
        }

        member _.ReplaceLatestTagged(scopeId, moduleId, pageRoute, subtitleKey, document, tags) = async {
            let! id = inner.ReplaceLatestTagged(scopeId, moduleId, pageRoute, subtitleKey, document, tags)
            do! publishEvent scopeId id
            return id
        }

        member _.List(scopeId, limit) = inner.List(scopeId, limit)

        member _.ListByTag(scopeId, limit, tagFilter) =
            inner.ListByTag(scopeId, limit, tagFilter)

        member _.Get(scopeId, id) = inner.Get(scopeId, id)

        member _.GetSection(scopeId, id, sectionId) =
            inner.GetSection(scopeId, id, sectionId)

        member _.DeleteScope(scopeId) = async {
            let! deleted = inner.DeleteScope(scopeId)

            if deleted > 0 then
                let payload: NarrativeNotifications.ScopeResetPayload = {
                    ScopeId = scopeId
                    DeletedCount = deleted
                    ResetAt = DateTime.UtcNow
                }

                do! publishPayload scopeId NarrativeNotifications.ScopeResetKey payload

            return deleted
        }