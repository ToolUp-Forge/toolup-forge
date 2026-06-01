namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open ToolUp.Platform.Narrative

/// In-memory `INarrativeStore` — the default for single-process
/// deployments. Entries are kept per-scope with a hard cap
/// (`policy.MaxPerScope`) so long-running servers do not accumulate
/// unbounded narrative history; oldest entries are evicted first.
/// `policy.MaxAge`, when set, additionally evicts entries whose
/// `PublishedAt` is older than `now - MaxAge` (lazy sweep on every
/// `Publish`).
///
/// Distributed deployments swap this for a Redis/Postgres/blob
/// implementation via `ServerApp.withNarrativeStore`; the interface
/// contract is identical (GP 12).
type InMemoryNarrativeStore(policy: NarrativeRetentionPolicy) =
    // scopeId -> ring-buffer-ish list, newest first. Written through a
    // lock-free dictionary; per-scope list access is serialised via
    // `lock` on the list itself.
    let entriesByScope = ConcurrentDictionary<string, ResizeArray<NarrativeEntry>>()

    let getOrCreate scopeId =
        entriesByScope.GetOrAdd(scopeId, System.Func<string, ResizeArray<NarrativeEntry>>(fun _ -> ResizeArray()))

    let evict (bucket: ResizeArray<NarrativeEntry>) =
        // Age sweep first — drops stale entries regardless of count.
        match policy.MaxAge with
        | Some maxAge when maxAge > TimeSpan.Zero ->
            let cutoff = DateTime.UtcNow - maxAge
            // bucket is newest-first; walk from the tail and remove
            // entries older than the cutoff.
            let mutable i = bucket.Count - 1

            while i >= 0 && bucket.[i].PublishedAt < cutoff do
                bucket.RemoveAt(i)
                i <- i - 1
        | _ -> ()

        // Then count cap.
        match policy.MaxPerScope with
        | Some maxPerScope when maxPerScope >= 0 ->
            while bucket.Count > maxPerScope do
                bucket.RemoveAt(bucket.Count - 1)
        | _ -> ()

    let toInfo (e: NarrativeEntry) : NarrativeEntryInfo = {
        Id = e.Id
        ModuleId = e.ModuleId
        PageRoute = e.PageRoute
        Title = e.Title
        Subtitle = e.Subtitle
        PublishedAt = e.PublishedAt
        Tags = e.Tags
    }

    let publishInternal (scopeId, moduleId, pageRoute, document, tags) = async {
        let bucket = getOrCreate scopeId
        let id = Guid.NewGuid()

        let entry: NarrativeEntry = {
            Id = id
            ModuleId = moduleId
            PageRoute = pageRoute
            Title = document.Title
            Subtitle = document.Subtitle
            PublishedAt = DateTime.UtcNow
            Tags = tags
            Document = document
        }

        lock bucket (fun () ->
            bucket.Insert(0, entry)
            evict bucket)

        return id
    }

    let replaceLatestInternal (scopeId, moduleId, pageRoute, subtitleKey, document, tags) = async {
        let bucket = getOrCreate scopeId

        lock bucket (fun () ->
            let matches (e: NarrativeEntry) =
                e.ModuleId = moduleId
                && e.PageRoute = pageRoute
                && (match subtitleKey with
                    | None -> true
                    | Some _ -> e.Subtitle = subtitleKey)

            let survivors = bucket |> Seq.filter (matches >> not) |> Seq.toList
            bucket.Clear()
            bucket.AddRange(survivors))

        return! publishInternal (scopeId, moduleId, pageRoute, document, tags)
    }

    new() = InMemoryNarrativeStore(NarrativeRetentionPolicy.defaults)

    new(maxPerScope: int) =
        InMemoryNarrativeStore(
            {
                NarrativeRetentionPolicy.defaults with
                    MaxPerScope = Some maxPerScope
            }
        )

    interface INarrativeStore with
        member _.Publish(scopeId, moduleId, pageRoute, document) =
            publishInternal (scopeId, moduleId, pageRoute, document, [])

        member _.PublishTagged(scopeId, moduleId, pageRoute, document, tags) =
            publishInternal (scopeId, moduleId, pageRoute, document, tags)

        member _.ReplaceLatest(scopeId, moduleId, pageRoute, subtitleKey, document) =
            replaceLatestInternal (scopeId, moduleId, pageRoute, subtitleKey, document, [])

        member _.ReplaceLatestTagged(scopeId, moduleId, pageRoute, subtitleKey, document, tags) =
            replaceLatestInternal (scopeId, moduleId, pageRoute, subtitleKey, document, tags)

        member _.List(scopeId, limit) = async {
            match entriesByScope.TryGetValue scopeId with
            | true, bucket ->
                let snapshot = lock bucket (fun () -> bucket.ToArray())
                return snapshot |> Array.truncate (max 0 limit) |> Array.map toInfo |> Array.toList
            | false, _ -> return []
        }

        member _.ListByTag(scopeId, limit, tagFilter) = async {
            match entriesByScope.TryGetValue scopeId with
            | true, bucket ->
                let snapshot = lock bucket (fun () -> bucket.ToArray())

                let filtered =
                    match tagFilter with
                    | None -> snapshot
                    | Some tag -> snapshot |> Array.filter (fun e -> e.Tags |> List.contains tag)

                return filtered |> Array.truncate (max 0 limit) |> Array.map toInfo |> Array.toList
            | false, _ -> return []
        }

        member _.Get(scopeId, id) = async {
            match entriesByScope.TryGetValue scopeId with
            | true, bucket ->
                let snapshot = lock bucket (fun () -> bucket.ToArray())
                return snapshot |> Array.tryFind (fun e -> e.Id = id)
            | false, _ -> return None
        }

        member _.GetSection(scopeId, id, sectionId) = async {
            match entriesByScope.TryGetValue scopeId with
            | true, bucket ->
                let snapshot = lock bucket (fun () -> bucket.ToArray())

                return
                    snapshot
                    |> Array.tryFind (fun e -> e.Id = id)
                    |> Option.bind (fun e -> e.Document.Sections |> List.tryFind (fun s -> s.Id = sectionId))
            | false, _ -> return None
        }

        member _.DeleteScope(scopeId) = async {
            match entriesByScope.TryRemove scopeId with
            | true, bucket -> return lock bucket (fun () -> bucket.Count)
            | false, _ -> return 0
        }