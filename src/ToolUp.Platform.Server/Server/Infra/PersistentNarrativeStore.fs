module ToolUp.Platform.PersistentNarrativeStore

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Narrative

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: `_platform`
// Path: `narratives/{scopeId}/{ts}-{narrativeId:N}.json`
//
// One blob per narrative entry. The timestamp prefix sorts
// lexicographically (ISO-ordered with hyphenated time component) so a
// `List` followed by a descending sort yields newest-first without
// reading payloads. The narrative-id suffix disambiguates writes
// that happen in the same 100-ns tick (possible on Windows's
// `DateTime.UtcNow` resolution under concurrent publishes).
//
// Scope ID lives in the path prefix so a `List(container, prefix)`
// is a cheap per-scope read and cross-scope leakage is structurally
// impossible (GP 4 / GP 12 rule 5).
//
// Each blob is a `NarrativeEntry` JSON-serialised via
// `ToolUp.Remoting.Json.SystemTextJson.FableConverters`.
// `NarrativeDocument` carries DUs whose round-trip identity matters;
// FableConverters is the canonical SDK choice for DU-bearing persisted
// blobs and matches what other stores in `_platform` use (see
// CLAUDE.md "Consumer dependency contract").

[<Literal>]
let private platformContainer = "_platform"

let private scopePrefix (scopeId: string) = $"narratives/{scopeId}/"

let private timestampPart (publishedAt: DateTime) =
    publishedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ss-fffffffZ")

let private blobName (scopeId: string) (entry: NarrativeEntry) =
    $"{scopePrefix scopeId}{timestampPart entry.PublishedAt}-{entry.Id:N}.json"

// ─── JSON serialisation ──────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private serialize (entry: NarrativeEntry) : byte[] =
    let json = JsonSerializer.Serialize(entry, jsonOptions)
    Encoding.UTF8.GetBytes(json)

let private deserialize (bytes: byte[]) : NarrativeEntry option =
    try
        let json = Encoding.UTF8.GetString(bytes)
        Some(JsonSerializer.Deserialize<NarrativeEntry>(json, jsonOptions))
    with _ ->
        None

// ─── Result helpers ──────────────────────────────────────────────

/// Convert `IBlobStorage` Result-bearing returns into the shape the
/// in-memory implementation contract gives callers (no exceptions on
/// the happy path; deletions are best-effort idempotent).
let private throwOnError (op: string) (result: Result<'a, string>) : 'a =
    match result with
    | Ok v -> v
    | Error msg -> raise (Exception($"PersistentNarrativeStore: {op} failed: {msg}"))

/// Blob-backed `INarrativeStore`. Persists narratives across process
/// restarts (the in-memory implementation evicts on every restart,
/// which leaves AI tools like `list_narratives` blind to anything
/// the user generated in a previous session).
///
/// Writes are O(1) (one blob per `Publish`); `List` and `Get` cost a
/// `List` round-trip plus a `Download` per entry. For the dev-deployment
/// volumes this targets (capped at `policy.MaxPerScope` per scope), the
/// simplicity wins; high-volume deployments should swap in a
/// secondary-indexed `INarrativeStore` singleton.
///
/// `policy.MaxAge`, when set, evicts entries whose `PublishedAt` is
/// older than `now - MaxAge` (lazy sweep on every `Publish`). Eviction
/// deletes the underlying blob — quiet scopes retain old entries until
/// the next write triggers the sweep.
///
/// Distribution: persists to whatever `IBlobStorage` is registered, so
/// a Redis / Postgres / S3-backed deployment gets durable narratives
/// the moment it switches its storage layer (no further wiring).
type PersistentNarrativeStore(blobStorage: IBlobStorage, policy: NarrativeRetentionPolicy) =

    let toInfo (e: NarrativeEntry) : NarrativeEntryInfo = {
        Id = e.Id
        ModuleId = e.ModuleId
        PageRoute = e.PageRoute
        Title = e.Title
        Subtitle = e.Subtitle
        PublishedAt = e.PublishedAt
        Tags = e.Tags
    }

    /// List every narrative blob for `scopeId`, deserialise each, drop
    /// ones whose payload is unparseable, return newest-first.
    let listEntries (scopeId: string) : Async<NarrativeEntry list> = async {
        let prefix = scopePrefix scopeId
        let! names = blobStorage.List(platformContainer, prefix)

        // Lexicographic descending sort = newest first by timestamp prefix.
        let ordered = names |> List.sortDescending

        let! entries =
            ordered
            |> List.map (fun name -> async {
                let! result = blobStorage.Download(platformContainer, name)

                return
                    match result with
                    | Ok bytes -> deserialize bytes
                    | Error _ -> None
            })
            |> Async.Parallel

        return entries |> Array.choose id |> Array.toList
    }

    let publishInternal (scopeId, moduleId, pageRoute, document, tags) = async {
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

        let name = blobName scopeId entry
        let bytes = serialize entry
        let! uploadResult = blobStorage.Upload(platformContainer, name, bytes)
        uploadResult |> throwOnError "Upload" |> ignore

        // Eviction sweep. Best-effort: a failed delete leaves a stale
        // blob but does not break the publish.
        let! all = listEntries scopeId

        let ageCutoff =
            match policy.MaxAge with
            | Some maxAge when maxAge > TimeSpan.Zero -> Some(DateTime.UtcNow - maxAge)
            | _ -> None

        let agedOut =
            match ageCutoff with
            | Some cutoff -> all |> List.filter (fun e -> e.PublishedAt < cutoff)
            | None -> []

        let survivors =
            match ageCutoff with
            | Some cutoff -> all |> List.filter (fun e -> e.PublishedAt >= cutoff)
            | None -> all

        let countOverflow =
            match policy.MaxPerScope with
            | Some maxPerScope when maxPerScope >= 0 && survivors.Length > maxPerScope ->
                survivors |> List.skip maxPerScope
            | _ -> []

        for old in agedOut @ countOverflow do
            let oldName = blobName scopeId old
            let! _ = blobStorage.Delete(platformContainer, oldName)
            ()

        return id
    }

    let replaceLatestInternal (scopeId, moduleId, pageRoute, subtitleKey, document, tags) = async {
        let! all = listEntries scopeId

        let matches (e: NarrativeEntry) =
            e.ModuleId = moduleId
            && e.PageRoute = pageRoute
            && (match subtitleKey with
                | None -> true
                | Some _ -> e.Subtitle = subtitleKey)

        let toRemove = all |> List.filter matches

        for old in toRemove do
            let oldName = blobName scopeId old
            let! _ = blobStorage.Delete(platformContainer, oldName)
            ()

        return! publishInternal (scopeId, moduleId, pageRoute, document, tags)
    }

    new(blobStorage: IBlobStorage) = PersistentNarrativeStore(blobStorage, NarrativeRetentionPolicy.defaults)

    new(blobStorage: IBlobStorage, maxPerScope: int) =
        PersistentNarrativeStore(
            blobStorage,
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
            let! all = listEntries scopeId
            return all |> List.truncate (max 0 limit) |> List.map toInfo
        }

        member _.ListByTag(scopeId, limit, tagFilter) = async {
            let! all = listEntries scopeId

            let filtered =
                match tagFilter with
                | None -> all
                | Some tag -> all |> List.filter (fun e -> e.Tags |> List.contains tag)

            return filtered |> List.truncate (max 0 limit) |> List.map toInfo
        }

        member this.Get(scopeId, id) = async {
            // Optimisation: filter blob names by id suffix before
            // downloading, so Get is O(N) names + O(1) download instead
            // of O(N) downloads. The blob name format embeds `{id:N}`
            // immediately before the `.json` extension.
            let prefix = scopePrefix scopeId
            let! names = blobStorage.List(platformContainer, prefix)
            let suffix = sprintf "%s.json" (id.ToString "N")
            let candidate = names |> List.tryFind (fun n -> n.EndsWith suffix)

            match candidate with
            | None -> return None
            | Some name ->
                let! result = blobStorage.Download(platformContainer, name)

                return
                    match result with
                    | Ok bytes -> deserialize bytes
                    | Error _ -> None
        }

        member this.GetSection(scopeId, id, sectionId) = async {
            let! entry = (this :> INarrativeStore).Get(scopeId, id)

            return
                entry
                |> Option.bind (fun e -> e.Document.Sections |> List.tryFind (fun s -> s.Id = sectionId))
        }

        member _.DeleteScope(scopeId) = async {
            // List every narrative blob for the scope and delete each.
            // Best-effort on individual deletes — a failed delete is
            // logged at the IBlobStorage layer and counted as a miss.
            // Returns the count of successful deletes so callers can
            // surface the reset outcome.
            let prefix = scopePrefix scopeId
            let! names = blobStorage.List(platformContainer, prefix)
            let mutable deleted = 0

            for name in names do
                let! result = blobStorage.Delete(platformContainer, name)

                match result with
                | Ok _ -> deleted <- deleted + 1
                | Error _ -> ()

            return deleted
        }